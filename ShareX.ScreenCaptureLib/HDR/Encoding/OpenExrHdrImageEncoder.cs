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
    /// <summary>
    /// Writes a single-part, uncompressed OpenEXR scanline image. The HALF
    /// samples can remain raw scRGB or be display-referenced for conventional viewers.
    /// </summary>
    public sealed class OpenExrHdrImageEncoder : IHdrImageEncoder
    {
        private const int OpenExrMagic = 20000630;
        private const int OpenExrVersion = 2;
        private const int HalfPixelType = 1;
        private const int ChannelCount = 4;
        private const int BytesPerSample = 2;

        private static readonly int[] ChannelSourceOffsets = { 6, 4, 2, 0 }; // A, B, G, R

        public HdrFileFormat Format => HdrFileFormat.OpenExr;

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
            bool displayReferenced = options.OpenExrExposureMode == OpenExrExposureMode.DisplayReferenced;
            float whiteLuminance = displayReferenced
                ? options.GetValidatedOpenExrReferenceWhiteNits()
                : HdrRgba16FloatBuffer.ReferenceWhiteNits;
            float rgbScale = HdrRgba16FloatBuffer.ReferenceWhiteNits / whiteLuminance;
            using MemoryStream header = CreateHeader(source.Width, source.Height, whiteLuminance, rgbScale);
            int scanlineDataSize = checked(source.Width * ChannelCount * BytesPerSample);
            long firstChunkOffset = checked(header.Length + (long)source.Height * sizeof(long));
            long chunkSize = checked(sizeof(int) + sizeof(int) + scanlineDataSize);
            long bytesWritten = checked(firstChunkOffset + (long)source.Height * chunkSize);

            header.Position = 0;
            header.CopyTo(destination);

            for (int y = 0; y < source.Height; y++)
            {
                WriteInt64(destination, checked(firstChunkOffset + y * chunkSize));
            }

            for (int y = 0; y < source.Height; y++)
            {
                WriteInt32(destination, y);
                WriteInt32(destination, scanlineDataSize);
                WriteScanline(destination, source.GetRowSpan(y), source.Width, rgbScale);
            }

            return new HdrEncodedImageInfo(
                Format,
                ".exr",
                "image/x-exr",
                !displayReferenced,
                bytesWritten);
        }

        private static MemoryStream CreateHeader(int width, int height, float whiteLuminance, float rgbScale)
        {
            var stream = new MemoryStream(512);
            WriteInt32(stream, OpenExrMagic);
            WriteInt32(stream, OpenExrVersion);

            WriteAttribute(stream, "channels", "chlist", WriteChannelList);
            WriteAttribute(stream, "compression", "compression", value => value.WriteByte(0));
            WriteAttribute(stream, "dataWindow", "box2i", value => WriteBox2i(value, width, height));
            WriteAttribute(stream, "displayWindow", "box2i", value => WriteBox2i(value, width, height));
            WriteAttribute(stream, "lineOrder", "lineOrder", value => value.WriteByte(0));
            WriteAttribute(stream, "pixelAspectRatio", "float", value => WriteFloat(value, 1f));
            WriteAttribute(stream, "screenWindowCenter", "v2f", value =>
            {
                WriteFloat(value, 0f);
                WriteFloat(value, 0f);
            });
            WriteAttribute(stream, "screenWindowWidth", "float", value => WriteFloat(value, 1f));
            WriteAttribute(stream, "chromaticities", "chromaticities", WriteRec709Chromaticities);
            WriteAttribute(stream, "whiteLuminance", "float", value =>
                WriteFloat(value, whiteLuminance));
            if (rgbScale != 1f)
            {
                WriteAttribute(stream, "shareXScRgbScale", "float", value =>
                    WriteFloat(value, rgbScale));
            }
            WriteStringAttribute(stream, "software", "ShareX");
            WriteStringAttribute(
                stream,
                "comments",
                rgbScale == 1f
                    ? "Linear scRGB (BT.709/D65), associated alpha; 1.0 = 80 cd/m^2"
                    : $"Display-referenced linear BT.709/D65, associated alpha; 1.0 = {whiteLuminance:0.###} cd/m^2; ShareX scale={rgbScale:R}");

            stream.WriteByte(0);
            stream.Position = 0;
            return stream;
        }

        private static void WriteChannelList(Stream stream)
        {
            WriteChannel(stream, "A", false);
            WriteChannel(stream, "B", true);
            WriteChannel(stream, "G", true);
            WriteChannel(stream, "R", true);
            stream.WriteByte(0);
        }

        private static void WriteChannel(Stream stream, string name, bool perceptuallyLinear)
        {
            WriteNullTerminatedAscii(stream, name);
            WriteInt32(stream, HalfPixelType);
            stream.WriteByte(perceptuallyLinear ? (byte)1 : (byte)0);
            stream.WriteByte(0);
            stream.WriteByte(0);
            stream.WriteByte(0);
            WriteInt32(stream, 1);
            WriteInt32(stream, 1);
        }

        private static void WriteScanline(
            Stream destination,
            ReadOnlySpan<byte> sourceRow,
            int width,
            float rgbScale)
        {
            Span<byte> scaledSample = stackalloc byte[BytesPerSample];
            foreach (int channelOffset in ChannelSourceOffsets)
            {
                for (int x = 0; x < width; x++)
                {
                    ReadOnlySpan<byte> sample = sourceRow.Slice(
                        x * HdrRgba16FloatBuffer.BytesPerPixel + channelOffset,
                        BytesPerSample);
                    if (channelOffset == 6 || rgbScale == 1f)
                    {
                        destination.Write(sample);
                    }
                    else
                    {
                        float value = (float)BitConverter.UInt16BitsToHalf(
                            BinaryPrimitives.ReadUInt16LittleEndian(sample));
                        value = float.IsNaN(value)
                            ? 0f
                            : Math.Clamp(value * rgbScale, -65504f, 65504f);
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            scaledSample,
                            BitConverter.HalfToUInt16Bits((Half)value));
                        destination.Write(scaledSample);
                    }
                }
            }
        }

        private static void WriteBox2i(Stream stream, int width, int height)
        {
            WriteInt32(stream, 0);
            WriteInt32(stream, 0);
            WriteInt32(stream, width - 1);
            WriteInt32(stream, height - 1);
        }

        private static void WriteRec709Chromaticities(Stream stream)
        {
            WriteFloat(stream, 0.6400f);
            WriteFloat(stream, 0.3300f);
            WriteFloat(stream, 0.3000f);
            WriteFloat(stream, 0.6000f);
            WriteFloat(stream, 0.1500f);
            WriteFloat(stream, 0.0600f);
            WriteFloat(stream, 0.3127f);
            WriteFloat(stream, 0.3290f);
        }

        private static void WriteStringAttribute(Stream stream, string name, string value)
        {
            WriteAttribute(stream, name, "string", data =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                data.Write(bytes);
            });
        }

        private static void WriteAttribute(Stream stream, string name, string type, Action<Stream> writeValue)
        {
            using var value = new MemoryStream();
            writeValue(value);

            WriteNullTerminatedAscii(stream, name);
            WriteNullTerminatedAscii(stream, type);
            WriteInt32(stream, checked((int)value.Length));
            value.Position = 0;
            value.CopyTo(stream);
        }

        private static void WriteNullTerminatedAscii(Stream stream, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes);
            stream.WriteByte(0);
        }

        private static void WriteFloat(Stream stream, float value) =>
            WriteInt32(stream, BitConverter.SingleToInt32Bits(value));

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt64(Stream stream, long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
