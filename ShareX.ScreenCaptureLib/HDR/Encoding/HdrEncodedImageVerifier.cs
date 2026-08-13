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
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    public static class HdrEncodedImageVerifier
    {
        private const int OpenExrMagic = 20000630;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static bool VerifyFile(
            string filePath,
            HdrFileFormat format,
            int expectedWidth,
            int expectedHeight)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            using FileStream stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return Verify(stream, format, expectedWidth, expectedHeight);
        }

        public static bool Verify(
            Stream stream,
            HdrFileFormat format,
            int expectedWidth,
            int expectedHeight)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanRead || !stream.CanSeek || expectedWidth <= 0 || expectedHeight <= 0)
            {
                return false;
            }

            long originalPosition = stream.Position;

            try
            {
                stream.Position = 0;
                return format switch
                {
                    HdrFileFormat.UltraHdrJpeg => VerifyUltraHdr(stream, expectedWidth, expectedHeight),
                    HdrFileFormat.OpenExr => VerifyOpenExr(stream, expectedWidth, expectedHeight),
                    HdrFileFormat.HdrPng => VerifyHdrPng(stream, expectedWidth, expectedHeight),
                    _ => false
                };
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or OverflowException or ArgumentException)
            {
                return false;
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }

        private static bool VerifyUltraHdr(Stream stream, int expectedWidth, int expectedHeight)
        {
            if (stream.Length <= 0 || stream.Length > int.MaxValue)
            {
                return false;
            }

            if (stream is MemoryStream memoryStream &&
                memoryStream.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                return VerifyUltraHdrBytes(
                    buffer.AsSpan(0, checked((int)memoryStream.Length)),
                    expectedWidth,
                    expectedHeight);
            }

            byte[] encoded = new byte[checked((int)stream.Length)];
            stream.ReadExactly(encoded);
            return VerifyUltraHdrBytes(encoded, expectedWidth, expectedHeight);
        }

        private static bool VerifyUltraHdrBytes(
            ReadOnlySpan<byte> encoded,
            int expectedWidth,
            int expectedHeight)
        {
            return TryReadJpegDimensions(encoded, out int width, out int height) &&
                width == expectedWidth && height == expectedHeight &&
                HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.UltraHdrJpeg, out _) &&
                UltraHdrJpegImageEncoder.IsUltraHdrImage(encoded);
        }

        private static bool TryReadJpegDimensions(
            ReadOnlySpan<byte> encoded,
            out int width,
            out int height)
        {
            width = 0;
            height = 0;

            if (encoded.Length < 4 || encoded[0] != 0xff || encoded[1] != 0xd8)
            {
                return false;
            }

            int position = 2;
            while (position < encoded.Length)
            {
                if (encoded[position++] != 0xff)
                {
                    return false;
                }

                while (position < encoded.Length && encoded[position] == 0xff)
                {
                    position++;
                }

                if (position >= encoded.Length)
                {
                    return false;
                }

                byte marker = encoded[position++];
                if (marker == 0xd9 || marker == 0xda)
                {
                    return false;
                }

                if (marker == 0x01 || marker is >= 0xd0 and <= 0xd8)
                {
                    continue;
                }

                if (position + 2 > encoded.Length)
                {
                    return false;
                }

                int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(position, 2));
                if (segmentLength < 2 || position + segmentLength > encoded.Length)
                {
                    return false;
                }

                bool isStartOfFrame = marker is >= 0xc0 and <= 0xcf &&
                    marker is not 0xc4 and not 0xc8 and not 0xcc;
                if (isStartOfFrame)
                {
                    if (segmentLength < 7)
                    {
                        return false;
                    }

                    height = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(position + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(position + 5, 2));
                    return width > 0 && height > 0;
                }

                position += segmentLength;
            }

            return false;
        }

        private static bool VerifyOpenExr(Stream stream, int expectedWidth, int expectedHeight)
        {
            if (stream.Length < 32)
            {
                return false;
            }

            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != OpenExrMagic)
            {
                return false;
            }

            uint versionAndFlags = reader.ReadUInt32();
            if ((versionAndFlags & 0xff) != 2 || (versionAndFlags & 0xffffff00) != 0)
            {
                return false;
            }

            bool foundChannels = false;
            bool foundDataWindow = false;
            bool foundNoCompression = false;

            while (stream.Position < stream.Length)
            {
                string name = ReadNullTerminatedAscii(reader, 255);
                if (name.Length == 0)
                {
                    break;
                }

                string type = ReadNullTerminatedAscii(reader, 255);
                int size = reader.ReadInt32();
                if (size < 0 || size > stream.Length - stream.Position)
                {
                    return false;
                }

                long valueStart = stream.Position;

                if (name == "channels" && type == "chlist")
                {
                    foundChannels = VerifyOpenExrChannels(reader, size);
                }
                else if (name == "dataWindow" && type == "box2i" && size == 16)
                {
                    int xMin = reader.ReadInt32();
                    int yMin = reader.ReadInt32();
                    int xMax = reader.ReadInt32();
                    int yMax = reader.ReadInt32();
                    foundDataWindow = xMin == 0 && yMin == 0 &&
                        xMax == expectedWidth - 1 && yMax == expectedHeight - 1;
                }
                else if (name == "compression" && type == "compression" && size == 1)
                {
                    foundNoCompression = reader.ReadByte() == 0;
                }

                stream.Position = checked(valueStart + size);
            }

            if (!foundChannels || !foundDataWindow || !foundNoCompression)
            {
                return false;
            }

            long offsetTableStart = stream.Position;
            long offsetTableBytes = checked((long)expectedHeight * sizeof(long));
            long expectedChunkOffset = checked(offsetTableStart + offsetTableBytes);
            int expectedChunkDataBytes = checked(expectedWidth * 4 * sizeof(ushort));

            if (expectedChunkOffset > stream.Length)
            {
                return false;
            }

            for (int y = 0; y < expectedHeight; y++)
            {
                stream.Position = checked(offsetTableStart + y * sizeof(long));
                long chunkOffset = reader.ReadInt64();
                if (chunkOffset != expectedChunkOffset || chunkOffset > stream.Length - 8)
                {
                    return false;
                }

                stream.Position = chunkOffset;
                if (reader.ReadInt32() != y || reader.ReadInt32() != expectedChunkDataBytes ||
                    stream.Position + expectedChunkDataBytes > stream.Length)
                {
                    return false;
                }

                expectedChunkOffset = checked(stream.Position + expectedChunkDataBytes);
            }

            return expectedChunkOffset == stream.Length;
        }

        private static bool VerifyOpenExrChannels(BinaryReader reader, int size)
        {
            long end = checked(reader.BaseStream.Position + size);
            string[] expectedNames = { "A", "B", "G", "R" };

            for (int i = 0; i < expectedNames.Length; i++)
            {
                if (reader.BaseStream.Position >= end ||
                    ReadNullTerminatedAscii(reader, 31) != expectedNames[i] ||
                    reader.ReadInt32() != 1)
                {
                    return false;
                }

                reader.ReadByte();
                reader.ReadBytes(3);
                if (reader.ReadInt32() != 1 || reader.ReadInt32() != 1)
                {
                    return false;
                }
            }

            return reader.BaseStream.Position < end && reader.ReadByte() == 0 &&
                reader.BaseStream.Position == end;
        }

        private static bool VerifyHdrPng(Stream stream, int expectedWidth, int expectedHeight)
        {
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            bool foundIhdr = false;
            bool foundCicp = false;
            bool foundMdcv = false;
            bool foundClli = false;
            bool foundIdat = false;
            bool foundIend = false;

            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            byte[] copyBuffer = new byte[81920];

            while (stream.Position < stream.Length)
            {
                uint length = ReadUInt32BigEndian(reader);
                if (length > int.MaxValue || length > stream.Length - stream.Position - 8)
                {
                    return false;
                }

                byte[] typeBytes = reader.ReadBytes(4);
                if (typeBytes.Length != 4)
                {
                    return false;
                }

                string type = Encoding.ASCII.GetString(typeBytes);
                uint crc = UpdateCrc(uint.MaxValue, typeBytes);
                long dataStart = stream.Position;

                if (type == "IHDR" && length == 13)
                {
                    byte[] ihdr = new byte[13];
                    stream.ReadExactly(ihdr);
                    crc = UpdateCrc(crc, ihdr);
                    foundIhdr = BinaryPrimitives.ReadUInt32BigEndian(ihdr) == expectedWidth &&
                        BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4)) == expectedHeight &&
                        ihdr[8] == 16 && ihdr[9] == 6 && ihdr[10] == 0 &&
                        ihdr[11] == 0 && ihdr[12] == 0;
                }
                else if (type == "cICP" && length == 4)
                {
                    byte[] cicp = new byte[4];
                    stream.ReadExactly(cicp);
                    crc = UpdateCrc(crc, cicp);
                    foundCicp = cicp.AsSpan().SequenceEqual(new byte[] { 9, 16, 0, 1 });
                }
                else
                {
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int toRead = checked((int)Math.Min(copyBuffer.Length, remaining));
                        int read = stream.Read(copyBuffer, 0, toRead);
                        if (read != toRead)
                        {
                            return false;
                        }

                        crc = UpdateCrc(crc, copyBuffer.AsSpan(0, read));
                        remaining -= read;
                    }

                    foundMdcv |= type == "mDCV" && length == 24;
                    foundClli |= type == "cLLI" && length == 8;
                    foundIdat |= type == "IDAT" && length > 0;
                    foundIend |= type == "IEND" && length == 0;
                }

                if (stream.Position != dataStart + length ||
                    ReadUInt32BigEndian(reader) != ~crc)
                {
                    return false;
                }

                if (type == "IEND")
                {
                    break;
                }
            }

            return foundIhdr && foundCicp && foundMdcv && foundClli && foundIdat &&
                foundIend && stream.Position == stream.Length;
        }

        private static string ReadNullTerminatedAscii(BinaryReader reader, int maximumLength)
        {
            var value = new StringBuilder();

            for (int i = 0; i <= maximumLength; i++)
            {
                byte next = reader.ReadByte();
                if (next == 0)
                {
                    return value.ToString();
                }

                if (next > 0x7f || i == maximumLength)
                {
                    throw new InvalidDataException("Invalid null-terminated ASCII field.");
                }

                value.Append((char)next);
            }

            throw new InvalidDataException("Missing null terminator.");
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            Span<byte> bytes = stackalloc byte[4];
            reader.BaseStream.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
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
            uint[] table = new uint[256];

            for (uint n = 0; n < table.Length; n++)
            {
                uint value = n;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
                }

                table[n] = value;
            }

            return table;
        }
    }
}
