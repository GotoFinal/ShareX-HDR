#region License Information (GPL v3)

/*
    ShareX - A program to capture and share images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Experimental PNG Third Edition HDR encoder. Pixels are converted from
    /// linear scRGB to full-range 16-bit BT.2020/PQ and tagged with cICP,
    /// mDCV and cLLI chunks. Decoder support for these chunks is still uneven.
    /// </summary>
    public sealed class HdrPngImageEncoder : IHdrImageEncoder
    {
        private const float ScRgbToNits = HdrRgba16FloatBuffer.ReferenceWhiteNits;
        private const float PqMaximumNits = 10000f;

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = CreateCrcTable();

        public HdrFileFormat Format => HdrFileFormat.HdrPng;

        public HdrEncodedImageInfo Encode(
            HdrRgba16FloatBuffer source,
            Stream destination,
            HdrImageEncodingOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);

            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream must be writable.", nameof(destination));
            }

            options ??= new HdrImageEncodingOptions();
            float masteringMaximumNits = options.GetValidatedMasteringDisplayMaximumNits();
            float masteringMinimumNits = options.GetValidatedMasteringDisplayMinimumNits(masteringMaximumNits);

            using var compressedPixels = new MemoryStream();
            (float maxCll, float maxFall) = CompressPixels(
                source,
                compressedPixels,
                masteringMaximumNits);

            long bytesWritten = 0;
            destination.Write(PngSignature);
            bytesWritten += PngSignature.Length;

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, checked((uint)source.Width));
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4), checked((uint)source.Height));
            ihdr[8] = 16; // sample depth
            ihdr[9] = 6;  // RGBA
            bytesWritten += WriteChunk(destination, "IHDR", ihdr);

            // BT.2020 primaries, ST 2084 PQ, identity RGB matrix, full range.
            bytesWritten += WriteChunk(destination, "cICP", new byte[] { 9, 16, 0, 1 });
            bytesWritten += WriteChunk(
                destination,
                "mDCV",
                CreateMasteringDisplayMetadata(masteringMaximumNits, masteringMinimumNits));
            bytesWritten += WriteChunk(destination, "cLLI", CreateContentLightMetadata(maxCll, maxFall));

            if (!compressedPixels.TryGetBuffer(out ArraySegment<byte> compressedBuffer))
            {
                throw new InvalidOperationException("Unable to access the encoded PNG pixel buffer.");
            }

            bytesWritten += WriteChunk(
                destination,
                "IDAT",
                compressedBuffer.AsSpan(0, checked((int)compressedPixels.Length)));
            bytesWritten += WriteChunk(destination, "IEND", ReadOnlySpan<byte>.Empty);

            return new HdrEncodedImageInfo(
                Format,
                ".png",
                "image/png",
                false,
                bytesWritten,
                maxCll,
                maxFall);
        }

        private static (float MaxCll, float MaxFall) CompressPixels(
            HdrRgba16FloatBuffer source,
            MemoryStream destination,
            float masteringMaximumNits)
        {
            float maxCll = 0f;
            double luminanceSum = 0d;
            byte[] encodedRow = new byte[checked(1 + source.Width * 8)];

            using (var zlib = new ZLibStream(destination, CompressionLevel.SmallestSize, true))
            {
                for (int y = 0; y < source.Height; y++)
                {
                    ReadOnlySpan<byte> sourceRow = source.GetRowSpan(y);
                    Span<byte> outputRow = encodedRow;
                    outputRow[0] = 0; // PNG filter: None

                    for (int x = 0; x < source.Width; x++)
                    {
                        ReadOnlySpan<byte> pixel = sourceRow.Slice(
                            x * HdrRgba16FloatBuffer.BytesPerPixel,
                            HdrRgba16FloatBuffer.BytesPerPixel);

                        float alpha = Math.Clamp(ReadFiniteHalf(pixel, 6), 0f, 1f);
                        float red709 = Unpremultiply(ReadFiniteHalf(pixel, 0), alpha);
                        float green709 = Unpremultiply(ReadFiniteHalf(pixel, 2), alpha);
                        float blue709 = Unpremultiply(ReadFiniteHalf(pixel, 4), alpha);

                        ConvertRec709ToRec2020(
                            red709,
                            green709,
                            blue709,
                            out float red2020,
                            out float green2020,
                            out float blue2020);

                        float redNits = Math.Clamp(red2020 * ScRgbToNits, 0f, masteringMaximumNits);
                        float greenNits = Math.Clamp(green2020 * ScRgbToNits, 0f, masteringMaximumNits);
                        float blueNits = Math.Clamp(blue2020 * ScRgbToNits, 0f, masteringMaximumNits);

                        maxCll = Math.Max(maxCll, Math.Max(redNits, Math.Max(greenNits, blueNits)));
                        luminanceSum += 0.2627d * redNits + 0.6780d * greenNits + 0.0593d * blueNits;

                        int outputOffset = 1 + x * 8;
                        WriteUInt16BigEndian(outputRow, outputOffset, QuantizeUnsigned16(EncodePq(redNits)));
                        WriteUInt16BigEndian(outputRow, outputOffset + 2, QuantizeUnsigned16(EncodePq(greenNits)));
                        WriteUInt16BigEndian(outputRow, outputOffset + 4, QuantizeUnsigned16(EncodePq(blueNits)));
                        WriteUInt16BigEndian(outputRow, outputOffset + 6, QuantizeUnsigned16(alpha));
                    }

                    zlib.Write(encodedRow);
                }
            }

            float maxFall = (float)(luminanceSum / checked((long)source.Width * source.Height));
            return (maxCll, maxFall);
        }

        private static float ReadFiniteHalf(ReadOnlySpan<byte> pixel, int offset)
        {
            Half value = BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(pixel.Slice(offset, 2)));
            return Half.IsFinite(value) ? (float)value : 0f;
        }

        private static float Unpremultiply(float value, float alpha)
        {
            if (alpha <= 0f)
            {
                return 0f;
            }

            return value / alpha;
        }

        private static void ConvertRec709ToRec2020(
            float red,
            float green,
            float blue,
            out float red2020,
            out float green2020,
            out float blue2020)
        {
            red2020 = 0.6274039f * red + 0.3292830f * green + 0.0433131f * blue;
            green2020 = 0.0690973f * red + 0.9195404f * green + 0.0113623f * blue;
            blue2020 = 0.0163914f * red + 0.0880133f * green + 0.8955953f * blue;
        }

        private static float EncodePq(float nits)
        {
            const double m1 = 2610d / 16384d;
            const double m2 = 2523d / 32d;
            const double c1 = 3424d / 4096d;
            const double c2 = 2413d / 128d;
            const double c3 = 2392d / 128d;

            double normalized = Math.Clamp(nits / PqMaximumNits, 0d, 1d);
            double luminancePower = Math.Pow(normalized, m1);
            return (float)Math.Pow(
                (c1 + c2 * luminancePower) / (1d + c3 * luminancePower),
                m2);
        }

        private static ushort QuantizeUnsigned16(float value) =>
            checked((ushort)Math.Clamp((int)MathF.Round(value * ushort.MaxValue), 0, ushort.MaxValue));

        private static void WriteUInt16BigEndian(Span<byte> destination, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, sizeof(ushort)), value);

        private static byte[] CreateMasteringDisplayMetadata(float maximumNits, float minimumNits)
        {
            byte[] result = new byte[24];
            Span<byte> data = result;

            // BT.2020 primaries and D65, divided by the PNG mDCV divisor 0.00002.
            WriteUInt16BigEndian(data, 0, 35400);
            WriteUInt16BigEndian(data, 2, 14600);
            WriteUInt16BigEndian(data, 4, 8500);
            WriteUInt16BigEndian(data, 6, 39850);
            WriteUInt16BigEndian(data, 8, 6550);
            WriteUInt16BigEndian(data, 10, 2300);
            WriteUInt16BigEndian(data, 12, 15635);
            WriteUInt16BigEndian(data, 14, 16450);
            BinaryPrimitives.WriteUInt32BigEndian(data.Slice(16), NitsToMetadataValue(maximumNits));
            BinaryPrimitives.WriteUInt32BigEndian(data.Slice(20), NitsToMetadataValue(minimumNits));
            return result;
        }

        private static byte[] CreateContentLightMetadata(float maxCll, float maxFall)
        {
            byte[] result = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(result, NitsToMetadataValue(maxCll));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), NitsToMetadataValue(maxFall));
            return result;
        }

        private static uint NitsToMetadataValue(float nits) =>
            checked((uint)Math.Clamp(MathF.Round(Math.Max(0f, nits) * 10000f), 0f, uint.MaxValue));

        private static long WriteChunk(Stream destination, string type, ReadOnlySpan<byte> data)
        {
            if (type.Length != 4)
            {
                throw new ArgumentException("PNG chunk types must contain four ASCII characters.", nameof(type));
            }

            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
            destination.Write(length);

            Span<byte> typeBytes = stackalloc byte[4];
            Encoding.ASCII.GetBytes(type, typeBytes);
            destination.Write(typeBytes);
            destination.Write(data);

            uint crc = UpdateCrc(uint.MaxValue, typeBytes);
            crc = UpdateCrc(crc, data) ^ uint.MaxValue;
            Span<byte> encodedCrc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encodedCrc, crc);
            destination.Write(encodedCrc);

            return checked(12L + data.Length);
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
    }
}
