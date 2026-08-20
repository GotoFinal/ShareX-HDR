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
using System.Diagnostics;
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
            Stopwatch totalTimer = Stopwatch.StartNew();
            float masteringMaximumNits = options.GetValidatedMasteringDisplayMaximumNits();
            float masteringMinimumNits = options.GetValidatedMasteringDisplayMinimumNits(masteringMaximumNits);
            HdrPqPixelConversionResult conversion = HdrPqPixelConverter.Convert(
                source,
                masteringMaximumNits,
                HdrPqPixelLayout.PngRgba16BigEndian,
                options.ProcessingBackend);

            using var compressedPixels = new MemoryStream();
            Stopwatch compressionTimer = Stopwatch.StartNew();
            using (var zlib = new ZLibStream(compressedPixels, CompressionLevel.SmallestSize, true))
            {
                zlib.Write(conversion.Pixels);
            }
            compressionTimer.Stop();

            Stopwatch chunkTimer = Stopwatch.StartNew();
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
            bytesWritten += WriteChunk(
                destination,
                "cLLI",
                CreateContentLightMetadata(conversion.MaxCll, conversion.MaxFall));

            if (!compressedPixels.TryGetBuffer(out ArraySegment<byte> compressedBuffer))
            {
                throw new InvalidOperationException("Unable to access the encoded PNG pixel buffer.");
            }

            bytesWritten += WriteChunk(
                destination,
                "IDAT",
                compressedBuffer.AsSpan(0, checked((int)compressedPixels.Length)));
            bytesWritten += WriteChunk(destination, "IEND", ReadOnlySpan<byte>.Empty);
            chunkTimer.Stop();
            totalTimer.Stop();
            HdrEncodingPerformance.Log(
                $"HDR PNG encode stages | size={source.Width}x{source.Height} " +
                $"backend={conversion.Backend} conversionMs={conversion.TotalMilliseconds:F1} " +
                $"compressionMs={compressionTimer.Elapsed.TotalMilliseconds:F1} " +
                $"chunksMs={chunkTimer.Elapsed.TotalMilliseconds:F1} " +
                $"bytes={bytesWritten} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");

            return new HdrEncodedImageInfo(
                Format,
                ".png",
                "image/png",
                false,
                bytesWritten,
                conversion.MaxCll,
                conversion.MaxFall);
        }

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
