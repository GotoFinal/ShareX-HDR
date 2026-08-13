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
using System.IO.Compression;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Strict decoder for the 16-bit BT.2020/PQ PNG layout written by
    /// <see cref="HdrPngImageEncoder"/>. It deliberately rejects general PNG
    /// files so SDR PNGs remain on ShareX's established image-loading path.
    /// </summary>
    public sealed class HdrPngImageDecoder
    {
        private const int MaximumChunkBytes = 512 * 1024 * 1024;
        private const long MaximumDecodedPixelBytes = 1024L * 1024 * 1024;
        private const float PqMaximumNits = 10000f;

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly byte[] ExpectedCicp = { 9, 16, 0, 1 };
        private static readonly ushort[] ExpectedChromaticities =
        {
            35400, 14600, 8500, 39850, 6550, 2300, 15635, 16450
        };
        private static readonly uint[] CrcTable = CreateCrcTable();

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
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public bool IsSupported(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                return false;
            }

            long originalPosition = source.Position;
            try
            {
                source.Position = 0;
                using var reader = new BinaryReader(source, Encoding.ASCII, leaveOpen: true);
                if (!ReadSignature(reader))
                {
                    return false;
                }

                PngChunk header = ReadChunk(reader, 13);
                if (header.Type != "IHDR" || !TryReadHeader(header.Data, out _, out _))
                {
                    return false;
                }

                PngChunk cicp = ReadChunk(reader, ExpectedCicp.Length);
                return cicp.Type == "cICP" && cicp.Data.AsSpan().SequenceEqual(ExpectedCicp);
            }
            catch (Exception exception) when (
                exception is EndOfStreamException or IOException or InvalidDataException or OverflowException)
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
            using FileStream stream = OpenFile(filePath);
            return Decode(stream);
        }

        public HdrImageDocument DecodeDocumentFile(
            string filePath,
            float sdrWhiteNits = HdrCaptureSettings.DefaultBrightnessNits)
        {
            if (!float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
            }

            using FileStream stream = OpenFile(filePath);
            HdrRgba16FloatBuffer pixels = Decode(stream, out float masteringMaximumNits);
            try
            {
                var bounds = new System.Drawing.Rectangle(0, 0, pixels.Width, pixels.Height);
                var segment = new HdrCaptureSourceSegment(
                    bounds,
                    Path.GetFileName(filePath),
                    wasHdrActive: true,
                    sdrWhiteNits,
                    masteringMaximumNits);
                return new HdrImageDocument(bounds, pixels, new[] { segment });
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }

        public HdrRgba16FloatBuffer Decode(Stream source) => Decode(source, out _);

        private static HdrRgba16FloatBuffer Decode(Stream source, out float masteringMaximumNits)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                throw new ArgumentException("The HDR PNG source stream must be readable and seekable.", nameof(source));
            }

            long originalPosition = source.Position;
            try
            {
                source.Position = 0;
                return DecodeCore(source, out masteringMaximumNits);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException or IOException or OverflowException or ArgumentException)
            {
                throw new InvalidDataException("The HDR PNG file is truncated or structurally invalid.", exception);
            }
            finally
            {
                source.Position = originalPosition;
            }
        }

        private static HdrRgba16FloatBuffer DecodeCore(Stream source, out float masteringMaximumNits)
        {
            masteringMaximumNits = 0f;
            using var reader = new BinaryReader(source, Encoding.ASCII, leaveOpen: true);
            if (!ReadSignature(reader))
            {
                throw new InvalidDataException("The PNG signature is invalid.");
            }

            PngChunk header = ReadExpectedChunk(reader, "IHDR", 13);
            if (!TryReadHeader(header.Data, out int width, out int height))
            {
                throw new InvalidDataException("Only 16-bit non-interlaced RGBA PNG files are supported.");
            }

            long pixelBytes = checked((long)width * height * HdrRgba16FloatBuffer.BytesPerPixel);
            if (pixelBytes <= 0 || pixelBytes > MaximumDecodedPixelBytes || pixelBytes > int.MaxValue)
            {
                throw new InvalidDataException("The HDR PNG dimensions exceed the supported decode limit.");
            }

            PngChunk cicp = ReadExpectedChunk(reader, "cICP", ExpectedCicp.Length);
            if (!cicp.Data.AsSpan().SequenceEqual(ExpectedCicp))
            {
                throw new InvalidDataException("The PNG is not full-range BT.2020/PQ RGB.");
            }

            PngChunk mastering = ReadExpectedChunk(reader, "mDCV", 24);
            masteringMaximumNits = ValidateMasteringMetadata(mastering.Data);

            PngChunk contentLight = ReadExpectedChunk(reader, "cLLI", 8);
            ValidateContentLightMetadata(contentLight.Data, masteringMaximumNits);

            PngChunk imageData = ReadExpectedChunk(reader, "IDAT");
            PngChunk end = ReadExpectedChunk(reader, "IEND", 0);
            if (end.Data.Length != 0 || source.Position != source.Length)
            {
                throw new InvalidDataException("The HDR PNG contains trailing data.");
            }

            return DecodePixels(imageData.Data, width, height);
        }

        private static HdrRgba16FloatBuffer DecodePixels(byte[] compressed, int width, int height)
        {
            int rowBytes = checked(width * HdrRgba16FloatBuffer.BytesPerPixel);
            byte[] encodedRow = new byte[checked(rowBytes + 1)];
            var result = new HdrRgba16FloatBuffer(width, height);

            try
            {
                using var compressedStream = new MemoryStream(compressed, writable: false);
                using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                for (int y = 0; y < height; y++)
                {
                    zlib.ReadExactly(encodedRow);
                    if (encodedRow[0] != 0)
                    {
                        throw new InvalidDataException("Only the PNG None scanline filter is supported.");
                    }

                    DecodeRow(encodedRow.AsSpan(1), result.GetWritableRowSpan(y), width);
                }

                if (zlib.ReadByte() != -1)
                {
                    throw new InvalidDataException("The HDR PNG contains excess decompressed pixel data.");
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void DecodeRow(ReadOnlySpan<byte> source, Span<byte> destination, int width)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                float red2020 = DecodePq(ReadUInt16BigEndian(source, offset)) /
                    HdrRgba16FloatBuffer.ReferenceWhiteNits;
                float green2020 = DecodePq(ReadUInt16BigEndian(source, offset + 2)) /
                    HdrRgba16FloatBuffer.ReferenceWhiteNits;
                float blue2020 = DecodePq(ReadUInt16BigEndian(source, offset + 4)) /
                    HdrRgba16FloatBuffer.ReferenceWhiteNits;
                float alpha = ReadUInt16BigEndian(source, offset + 6) / (float)ushort.MaxValue;

                ConvertRec2020ToRec709(
                    red2020,
                    green2020,
                    blue2020,
                    out float red709,
                    out float green709,
                    out float blue709);

                WriteHalf(destination, offset, red709 * alpha);
                WriteHalf(destination, offset + 2, green709 * alpha);
                WriteHalf(destination, offset + 4, blue709 * alpha);
                WriteHalf(destination, offset + 6, alpha);
            }
        }

        private static float DecodePq(ushort encoded)
        {
            const double m1 = 2610d / 16384d;
            const double m2 = 2523d / 32d;
            const double c1 = 3424d / 4096d;
            const double c2 = 2413d / 128d;
            const double c3 = 2392d / 128d;

            double signal = encoded / (double)ushort.MaxValue;
            double signalPower = Math.Pow(signal, 1d / m2);
            double numerator = Math.Max(signalPower - c1, 0d);
            double denominator = c2 - c3 * signalPower;
            if (denominator <= 0d)
            {
                return PqMaximumNits;
            }

            return (float)(Math.Pow(numerator / denominator, 1d / m1) * PqMaximumNits);
        }

        private static void ConvertRec2020ToRec709(
            float red,
            float green,
            float blue,
            out float red709,
            out float green709,
            out float blue709)
        {
            red709 = 1.6604910f * red - 0.5876411f * green - 0.0728499f * blue;
            green709 = -0.1245505f * red + 1.1328999f * green - 0.0083494f * blue;
            blue709 = -0.0181508f * red - 0.1005789f * green + 1.1187297f * blue;
        }

        private static float ValidateMasteringMetadata(ReadOnlySpan<byte> data)
        {
            for (int index = 0; index < ExpectedChromaticities.Length; index++)
            {
                if (ReadUInt16BigEndian(data, index * sizeof(ushort)) != ExpectedChromaticities[index])
                {
                    throw new InvalidDataException("The HDR PNG mastering primaries are not BT.2020/D65.");
                }
            }

            float maximumNits = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4)) / 10000f;
            float minimumNits = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4)) / 10000f;
            if (!float.IsFinite(maximumNits) ||
                maximumNits < HdrFileOutputSettings.MinimumMasteringDisplayNits ||
                maximumNits > HdrFileOutputSettings.MaximumMasteringDisplayNits ||
                minimumNits < 0f || minimumNits > Math.Min(1f, maximumNits))
            {
                throw new InvalidDataException("The HDR PNG mastering luminance metadata is invalid.");
            }

            return maximumNits;
        }

        private static void ValidateContentLightMetadata(ReadOnlySpan<byte> data, float masteringMaximumNits)
        {
            float maxCll = BinaryPrimitives.ReadUInt32BigEndian(data) / 10000f;
            float maxFall = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)) / 10000f;
            if (maxCll < 0f || maxFall < 0f ||
                maxCll > masteringMaximumNits + 0.0001f ||
                maxFall > masteringMaximumNits + 0.0001f)
            {
                throw new InvalidDataException("The HDR PNG content-light metadata is invalid.");
            }
        }

        private static bool TryReadHeader(ReadOnlySpan<byte> data, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (data.Length != 13 || data[8] != 16 || data[9] != 6 ||
                data[10] != 0 || data[11] != 0 || data[12] != 0)
            {
                return false;
            }

            uint encodedWidth = BinaryPrimitives.ReadUInt32BigEndian(data);
            uint encodedHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
            if (encodedWidth == 0 || encodedHeight == 0 ||
                encodedWidth > int.MaxValue || encodedHeight > int.MaxValue)
            {
                return false;
            }

            width = (int)encodedWidth;
            height = (int)encodedHeight;
            return true;
        }

        private static bool ReadSignature(BinaryReader reader) =>
            reader.ReadBytes(PngSignature.Length).AsSpan().SequenceEqual(PngSignature);

        private static PngChunk ReadExpectedChunk(BinaryReader reader, string expectedType, int? expectedLength = null)
        {
            PngChunk chunk = ReadChunk(reader, expectedLength);
            if (chunk.Type != expectedType)
            {
                throw new InvalidDataException($"Expected PNG chunk '{expectedType}', found '{chunk.Type}'.");
            }

            return chunk;
        }

        private static PngChunk ReadChunk(BinaryReader reader, int? expectedLength = null)
        {
            uint encodedLength = ReadUInt32BigEndian(reader);
            if (encodedLength > MaximumChunkBytes || encodedLength > int.MaxValue ||
                (expectedLength.HasValue && encodedLength != expectedLength.Value))
            {
                throw new InvalidDataException("The PNG chunk length is invalid.");
            }

            int length = (int)encodedLength;
            byte[] typeBytes = reader.ReadBytes(4);
            if (typeBytes.Length != 4 || Array.Exists(
                typeBytes,
                value => !((value >= (byte)'A' && value <= (byte)'Z') ||
                    (value >= (byte)'a' && value <= (byte)'z'))))
            {
                throw new InvalidDataException("The PNG chunk type is invalid.");
            }

            if (length > reader.BaseStream.Length - reader.BaseStream.Position - sizeof(uint))
            {
                throw new InvalidDataException("The PNG chunk is truncated.");
            }

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
            {
                throw new EndOfStreamException();
            }

            uint expectedCrc = ReadUInt32BigEndian(reader);
            uint actualCrc = UpdateCrc(uint.MaxValue, typeBytes);
            actualCrc = UpdateCrc(actualCrc, data) ^ uint.MaxValue;
            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException("The PNG chunk CRC is invalid.");
            }

            return new PngChunk(Encoding.ASCII.GetString(typeBytes), data);
        }

        private static FileStream OpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("An HDR PNG file path is required.", nameof(filePath));
            }

            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> source, int offset) =>
            BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset, sizeof(ushort)));

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            reader.BaseStream.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private static void WriteHalf(Span<byte> destination, int offset, float value)
        {
            float finite = float.IsFinite(value) ? Math.Clamp(value, -65504f, 65504f) : 0f;
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)finite));
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        private readonly record struct PngChunk(string Type, byte[] Data);
    }
}
