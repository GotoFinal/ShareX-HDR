#region License Information (GPL v3)

/*
    ShareX - A program to capture and share images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

#endregion License Information (GPL v3)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Strict decoder for the single-part, uncompressed, scanline OpenEXR subset
    /// written by <see cref="OpenExrHdrImageEncoder"/>. This deliberately is not a
    /// general OpenEXR decoder: accepting only the ShareX layout keeps allocations,
    /// channel semantics, alpha association, and color interpretation bounded.
    /// </summary>
    public sealed class OpenExrHdrImageDecoder
    {
        private const int OpenExrMagic = 20000630;
        private const uint OpenExrVersionAndFlags = 2;
        private const int MaximumHeaderBytes = 1024 * 1024;
        private const long MaximumDecodedPixelBytes = 1024L * 1024 * 1024;
        private const int ChannelCount = 4;
        private const int BytesPerSample = 2;

        private static readonly string[] ExpectedChannelNames = { "A", "B", "G", "R" };
        private static readonly byte[] ExpectedPerceptuallyLinear = { 0, 1, 1, 1 };
        private static readonly int[] ChannelDestinationOffsets = { 6, 4, 2, 0 };

        public bool IsSupportedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using FileStream stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                return IsSupported(stream);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Performs a bounded header-only identity probe. Full scanline and EOF
        /// validation remains the responsibility of <see cref="Decode(Stream)"/>.
        /// </summary>
        public bool IsSupported(Stream source)
            => IsSupported(source, out _);

        public bool IsSdr(Stream source) => IsSupported(source, out bool isSdr) && isSdr;

        public bool IsSdrFile(string filePath)
        {
            try
            {
                using var source = File.OpenRead(filePath);
                return IsSdr(source);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private bool IsSupported(Stream source, out bool isSdr)
        {
            isSdr = false;
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                return false;
            }

            long originalPosition = source.Position;
            try
            {
                source.Position = 0;
                if (source.Length < 32)
                {
                    return false;
                }

                using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
                if (reader.ReadInt32() != OpenExrMagic || reader.ReadUInt32() != OpenExrVersionAndFlags)
                {
                    return false;
                }

                var attributes = new HashSet<string>(StringComparer.Ordinal);
                bool validChannels = false;
                bool validCompression = false;
                bool validLineOrder = false;
                bool validPixelAspectRatio = false;
                bool validScreenWindowCenter = false;
                bool validScreenWindowWidth = false;
                bool validChromaticities = false;
                float whiteLuminance = 0f;
                float? encodedRgbScale = null;
                bool foundDataWindow = false;
                bool foundDisplayWindow = false;
                bool headerTerminated = false;
                int width = 0;
                int height = 0;
                int displayWidth = 0;
                int displayHeight = 0;

                while (source.Position < source.Length && source.Position <= MaximumHeaderBytes)
                {
                    string name = ReadNullTerminatedAscii(reader, 255);
                    if (source.Position > MaximumHeaderBytes)
                    {
                        return false;
                    }

                    if (name.Length == 0)
                    {
                        headerTerminated = true;
                        break;
                    }

                    if (!attributes.Add(name))
                    {
                        return false;
                    }

                    string type = ReadNullTerminatedAscii(reader, 255);
                    int size = reader.ReadInt32();
                    if (size < 0 || size > MaximumHeaderBytes ||
                        source.Position > MaximumHeaderBytes - size ||
                        size > source.Length - source.Position)
                    {
                        return false;
                    }

                    byte[] value = reader.ReadBytes(size);
                    if (value.Length != size)
                    {
                        return false;
                    }

                    switch (name)
                    {
                        case "channels" when type == "chlist": validChannels = ValidateChannels(value); break;
                        case "compression" when type == "compression": validCompression = value.AsSpan().SequenceEqual(new byte[] { 0 }); break;
                        case "dataWindow" when type == "box2i": foundDataWindow = TryReadWindow(value, out width, out height); break;
                        case "displayWindow" when type == "box2i": foundDisplayWindow = TryReadWindow(value, out displayWidth, out displayHeight); break;
                        case "lineOrder" when type == "lineOrder": validLineOrder = value.AsSpan().SequenceEqual(new byte[] { 0 }); break;
                        case "pixelAspectRatio" when type == "float": validPixelAspectRatio = HasSingleFloatBits(value, 1f); break;
                        case "screenWindowCenter" when type == "v2f":
                            validScreenWindowCenter = value.Length == 8 &&
                                BinaryPrimitives.ReadInt32LittleEndian(value) == BitConverter.SingleToInt32Bits(0f) &&
                                BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(4)) == BitConverter.SingleToInt32Bits(0f);
                            break;
                        case "screenWindowWidth" when type == "float": validScreenWindowWidth = HasSingleFloatBits(value, 1f); break;
                        case "chromaticities" when type == "chromaticities": validChromaticities = ValidateRec709Chromaticities(value); break;
                        case "whiteLuminance" when type == "float": TryReadSingleFloat(value, out whiteLuminance); break;
                        case "shareXDynamicRange" when type == "string": isSdr = value.AsSpan().SequenceEqual("SDR"u8); break;
                        case "shareXScRgbScale" when type == "float" && TryReadSingleFloat(value, out float scale): encodedRgbScale = scale; break;
                    }
                }

                long pixelBytes = checked((long)width * height * HdrRgba16FloatBuffer.BytesPerPixel);
                long offsetTableBytes = checked((long)height * sizeof(long));
                return headerTerminated && validChannels && validCompression && validLineOrder &&
                    validPixelAspectRatio && validScreenWindowCenter && validScreenWindowWidth &&
                    validChromaticities &&
                    TryGetScRgbDecodeScale(whiteLuminance, encodedRgbScale, out _) && foundDataWindow && foundDisplayWindow &&
                    width == displayWidth && height == displayHeight &&
                    pixelBytes > 0 && pixelBytes <= MaximumDecodedPixelBytes && pixelBytes <= int.MaxValue &&
                    offsetTableBytes <= source.Length - source.Position;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException or IOException or InvalidDataException or OverflowException or ArgumentException)
            {
                return false;
            }
            finally
            {
                source.Position = originalPosition;
            }
        }

        public HdrRgba16FloatBuffer DecodeFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("An OpenEXR file path is required.", nameof(filePath));
            }

            using FileStream stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return Decode(stream);
        }

        /// <summary>
        /// Decodes a ShareX OpenEXR file into an owned HDR document suitable for
        /// tone-mapped preview and semantic editor replay. ShareX OpenEXR files do
        /// not yet persist per-monitor calibration, so imported files use the
        /// supplied SDR white and peak values.
        /// </summary>
        public HdrImageDocument DecodeDocumentFile(
            string filePath,
            float sdrWhiteNits = HdrCaptureSettings.DefaultBrightnessNits,
            float displayPeakNits = HdrFileOutputSettings.DefaultMasteringDisplayMaximumNits)
        {
            if (!float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
            }

            if (!float.IsFinite(displayPeakNits) || displayPeakNits <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(displayPeakNits));
            }

            HdrRgba16FloatBuffer pixels = DecodeFile(filePath);
            try
            {
                var bounds = new System.Drawing.Rectangle(0, 0, pixels.Width, pixels.Height);
                var sourceSegment = new HdrCaptureSourceSegment(
                    bounds,
                    Path.GetFileName(filePath),
                    wasHdrActive: true,
                    sdrWhiteNits,
                    displayPeakNits);
                return new HdrImageDocument(bounds, pixels, new[] { sourceSegment });
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }

        public HdrRgba16FloatBuffer Decode(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                throw new ArgumentException("The OpenEXR source stream must be readable and seekable.", nameof(source));
            }

            long originalPosition = source.Position;
            try
            {
                source.Position = 0;
                return DecodeCore(source);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException or IOException or OverflowException or ArgumentException)
            {
                throw new InvalidDataException("The OpenEXR file is truncated or structurally invalid.", exception);
            }
            finally
            {
                source.Position = originalPosition;
            }
        }

        private static HdrRgba16FloatBuffer DecodeCore(Stream source)
        {
            if (source.Length < 32)
            {
                throw new InvalidDataException("The OpenEXR file is too small.");
            }

            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != OpenExrMagic || reader.ReadUInt32() != OpenExrVersionAndFlags)
            {
                throw new InvalidDataException("Only unflagged OpenEXR version 2 files are supported.");
            }

            var attributes = new HashSet<string>(StringComparer.Ordinal);
            bool validChannels = false;
            bool validCompression = false;
            bool validLineOrder = false;
            bool validPixelAspectRatio = false;
            bool validScreenWindowCenter = false;
            bool validScreenWindowWidth = false;
            bool validChromaticities = false;
            float whiteLuminance = 0f;
            float? encodedRgbScale = null;
            bool foundDataWindow = false;
            bool foundDisplayWindow = false;
            int width = 0;
            int height = 0;
            int displayWidth = 0;
            int displayHeight = 0;

            while (source.Position < source.Length)
            {
                if (source.Position > MaximumHeaderBytes)
                {
                    throw new InvalidDataException("The OpenEXR header exceeds the supported limit.");
                }

                string name = ReadNullTerminatedAscii(reader, 255);
                if (source.Position > MaximumHeaderBytes)
                {
                    throw new InvalidDataException("The OpenEXR header exceeds the supported limit.");
                }

                if (name.Length == 0)
                {
                    break;
                }

                if (!attributes.Add(name))
                {
                    throw new InvalidDataException($"The OpenEXR header contains duplicate '{name}' attributes.");
                }

                string type = ReadNullTerminatedAscii(reader, 255);
                int size = reader.ReadInt32();
                if (size < 0 || size > MaximumHeaderBytes ||
                    source.Position > MaximumHeaderBytes - size ||
                    size > source.Length - source.Position)
                {
                    throw new InvalidDataException("The OpenEXR attribute size is invalid.");
                }

                byte[] value = reader.ReadBytes(size);
                if (value.Length != size)
                {
                    throw new EndOfStreamException();
                }

                switch (name)
                {
                    case "channels" when type == "chlist":
                        validChannels = ValidateChannels(value);
                        break;
                    case "compression" when type == "compression":
                        validCompression = value.AsSpan().SequenceEqual(new byte[] { 0 });
                        break;
                    case "dataWindow" when type == "box2i":
                        foundDataWindow = TryReadWindow(value, out width, out height);
                        break;
                    case "displayWindow" when type == "box2i":
                        foundDisplayWindow = TryReadWindow(value, out displayWidth, out displayHeight);
                        break;
                    case "lineOrder" when type == "lineOrder":
                        validLineOrder = value.AsSpan().SequenceEqual(new byte[] { 0 });
                        break;
                    case "pixelAspectRatio" when type == "float":
                        validPixelAspectRatio = HasSingleFloatBits(value, 1f);
                        break;
                    case "screenWindowCenter" when type == "v2f":
                        validScreenWindowCenter = value.Length == 8 &&
                            BinaryPrimitives.ReadInt32LittleEndian(value) == BitConverter.SingleToInt32Bits(0f) &&
                            BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(4)) == BitConverter.SingleToInt32Bits(0f);
                        break;
                    case "screenWindowWidth" when type == "float":
                        validScreenWindowWidth = HasSingleFloatBits(value, 1f);
                        break;
                    case "chromaticities" when type == "chromaticities":
                        validChromaticities = ValidateRec709Chromaticities(value);
                        break;
                    case "whiteLuminance" when type == "float":
                        TryReadSingleFloat(value, out whiteLuminance);
                        break;
                    case "shareXScRgbScale" when type == "float" && TryReadSingleFloat(value, out float scale):
                        encodedRgbScale = scale;
                        break;
                }
            }

            if (!validChannels || !validCompression || !validLineOrder ||
                !validPixelAspectRatio || !validScreenWindowCenter || !validScreenWindowWidth ||
                !validChromaticities ||
                !TryGetScRgbDecodeScale(whiteLuminance, encodedRgbScale, out float rgbDecodeScale) ||
                !foundDataWindow || !foundDisplayWindow ||
                width != displayWidth || height != displayHeight)
            {
                throw new InvalidDataException("The OpenEXR file is outside the ShareX linear-scRGB subset.");
            }

            long pixelBytes = checked((long)width * height * HdrRgba16FloatBuffer.BytesPerPixel);
            if (pixelBytes <= 0 || pixelBytes > MaximumDecodedPixelBytes || pixelBytes > int.MaxValue)
            {
                throw new InvalidDataException("The OpenEXR dimensions exceed the supported decode limit.");
            }

            long offsetTableStart = source.Position;
            long offsetTableBytes = checked((long)height * sizeof(long));
            long expectedChunkOffset = checked(offsetTableStart + offsetTableBytes);
            int expectedChunkDataBytes = checked(width * ChannelCount * BytesPerSample);
            if (expectedChunkOffset > source.Length)
            {
                throw new InvalidDataException("The OpenEXR offset table is truncated.");
            }

            var result = new HdrRgba16FloatBuffer(width, height);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    source.Position = checked(offsetTableStart + (long)y * sizeof(long));
                    long chunkOffset = reader.ReadInt64();
                    if (chunkOffset != expectedChunkOffset || chunkOffset > source.Length - 8)
                    {
                        throw new InvalidDataException("The OpenEXR scanline offset table is invalid.");
                    }

                    source.Position = chunkOffset;
                    if (reader.ReadInt32() != y || reader.ReadInt32() != expectedChunkDataBytes ||
                        source.Position + expectedChunkDataBytes > source.Length)
                    {
                        throw new InvalidDataException("The OpenEXR scanline chunk is invalid.");
                    }

                    Span<byte> destinationRow = result.GetWritableRowSpan(y);
                    for (int channel = 0; channel < ChannelCount; channel++)
                    {
                        int destinationChannelOffset = ChannelDestinationOffsets[channel];
                        for (int x = 0; x < width; x++)
                        {
                            ushort sample = SanitizeHalfBits(reader.ReadUInt16());
                            if (destinationChannelOffset != 6 && rgbDecodeScale != 1f)
                            {
                                sample = ScaleHalfBits(sample, rgbDecodeScale);
                            }

                            BinaryPrimitives.WriteUInt16LittleEndian(
                                destinationRow.Slice(
                                    x * HdrRgba16FloatBuffer.BytesPerPixel + destinationChannelOffset,
                                    BytesPerSample),
                                sample);
                        }
                    }

                    expectedChunkOffset = checked(source.Position);
                }

                if (expectedChunkOffset != source.Length)
                {
                    throw new InvalidDataException("The OpenEXR file contains trailing or unreferenced data.");
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static bool ValidateChannels(ReadOnlySpan<byte> channels)
        {
            int position = 0;
            for (int channel = 0; channel < ExpectedChannelNames.Length; channel++)
            {
                if (!TryReadNullTerminatedAscii(channels, ref position, 31, out string name) ||
                    name != ExpectedChannelNames[channel] ||
                    position + 16 > channels.Length ||
                    BinaryPrimitives.ReadInt32LittleEndian(channels.Slice(position, 4)) != 1 ||
                    channels[position + 4] != ExpectedPerceptuallyLinear[channel] ||
                    channels[position + 5] != 0 || channels[position + 6] != 0 || channels[position + 7] != 0 ||
                    BinaryPrimitives.ReadInt32LittleEndian(channels.Slice(position + 8, 4)) != 1 ||
                    BinaryPrimitives.ReadInt32LittleEndian(channels.Slice(position + 12, 4)) != 1)
                {
                    return false;
                }

                position += 16;
            }

            return position == channels.Length - 1 && channels[position] == 0;
        }

        private static bool TryReadWindow(ReadOnlySpan<byte> value, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (value.Length != 16 ||
                BinaryPrimitives.ReadInt32LittleEndian(value) != 0 ||
                BinaryPrimitives.ReadInt32LittleEndian(value.Slice(4)) != 0)
            {
                return false;
            }

            int maximumX = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(8));
            int maximumY = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(12));
            if (maximumX < 0 || maximumY < 0)
            {
                return false;
            }

            width = checked(maximumX + 1);
            height = checked(maximumY + 1);
            return true;
        }

        private static bool ValidateRec709Chromaticities(ReadOnlySpan<byte> value)
        {
            float[] expected = { 0.6400f, 0.3300f, 0.3000f, 0.6000f, 0.1500f, 0.0600f, 0.3127f, 0.3290f };
            if (value.Length != expected.Length * sizeof(float))
            {
                return false;
            }

            for (int index = 0; index < expected.Length; index++)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(value.Slice(index * sizeof(float), sizeof(float))) !=
                    BitConverter.SingleToInt32Bits(expected[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasSingleFloatBits(ReadOnlySpan<byte> value, float expected) =>
            value.Length == sizeof(float) &&
            BinaryPrimitives.ReadInt32LittleEndian(value) == BitConverter.SingleToInt32Bits(expected);

        private static bool TryReadSingleFloat(ReadOnlySpan<byte> value, out float result)
        {
            result = value.Length == sizeof(float)
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                : 0f;
            return value.Length == sizeof(float) && float.IsFinite(result);
        }

        private static bool TryGetScRgbDecodeScale(
            float whiteLuminance,
            float? encodedRgbScale,
            out float decodeScale)
        {
            decodeScale = 0f;
            if (whiteLuminance == HdrRgba16FloatBuffer.ReferenceWhiteNits && encodedRgbScale == null)
            {
                decodeScale = 1f;
                return true;
            }

            if (!float.IsFinite(whiteLuminance) ||
                whiteLuminance < HdrFileOutputSettings.MinimumMasteringDisplayNits ||
                whiteLuminance > HdrFileOutputSettings.MaximumMasteringDisplayNits ||
                encodedRgbScale is not float scale || !float.IsFinite(scale) || scale <= 0f ||
                Math.Abs(whiteLuminance * scale - HdrRgba16FloatBuffer.ReferenceWhiteNits) > 0.01f)
            {
                return false;
            }

            decodeScale = 1f / scale;
            return float.IsFinite(decodeScale);
        }

        private static ushort ScaleHalfBits(ushort bits, float scale)
        {
            float value = (float)BitConverter.UInt16BitsToHalf(bits) * scale;
            value = Math.Clamp(value, -65504f, 65504f);
            return BitConverter.HalfToUInt16Bits((Half)value);
        }

        private static ushort SanitizeHalfBits(ushort bits)
        {
            if ((bits & 0x7c00) != 0x7c00)
            {
                return bits;
            }

            bool isInfinity = (bits & 0x03ff) == 0;
            if (!isInfinity)
            {
                return 0;
            }

            return (bits & 0x8000) == 0
                ? BitConverter.HalfToUInt16Bits(Half.MaxValue)
                : BitConverter.HalfToUInt16Bits(Half.MinValue);
        }

        private static string ReadNullTerminatedAscii(BinaryReader reader, int maximumLength)
        {
            var bytes = new List<byte>(Math.Min(maximumLength, 64));
            while (bytes.Count <= maximumLength)
            {
                byte value = reader.ReadByte();
                if (value == 0)
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }

                if (value > 0x7f)
                {
                    throw new InvalidDataException("The OpenEXR header contains a non-ASCII name.");
                }

                bytes.Add(value);
            }

            throw new InvalidDataException("The OpenEXR header contains an overlong name.");
        }

        private static bool TryReadNullTerminatedAscii(
            ReadOnlySpan<byte> source,
            ref int position,
            int maximumLength,
            out string value)
        {
            int start = position;
            while (position < source.Length && position - start <= maximumLength)
            {
                byte current = source[position++];
                if (current == 0)
                {
                    value = Encoding.ASCII.GetString(source.Slice(start, position - start - 1));
                    return true;
                }

                if (current > 0x7f)
                {
                    break;
                }
            }

            value = string.Empty;
            return false;
        }
    }
}
