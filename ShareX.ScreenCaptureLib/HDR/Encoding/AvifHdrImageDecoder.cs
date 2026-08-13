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
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Decodes ShareX's 10-bit BT.2020/PQ AVIF files back to premultiplied scRGB.
    /// General SDR, HLG, subsampled, and non-BT.2020 AVIF files are deliberately rejected.
    /// </summary>
    public sealed class AvifHdrImageDecoder
    {
        private const int ErrorBufferBytes = 512;
        private const long MaximumEncodedBytes = 512L * 1024 * 1024;
        private const long MaximumDecodedBytes = 1024L * 1024 * 1024;
        private const int Maximum10BitSample = 1023;
        private const float PqMaximumNits = 10000f;

        public bool IsSupportedFile(string filePath)
        {
            if (!HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.Avif, out _) ||
                string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                byte[] encoded = ReadBoundedFile(filePath);
                return AvifHdrImageEncoder.TryProbeHdr(encoded, out _, out _, out _, out _, out _);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                return false;
            }
        }

        public HdrRgba16FloatBuffer DecodeFile(string filePath)
        {
            byte[] encoded = ReadBoundedFile(filePath);
            return Decode(encoded, out _);
        }

        public HdrImageDocument DecodeDocumentFile(
            string filePath,
            float sdrWhiteNits = HdrCaptureSettings.DefaultBrightnessNits)
        {
            if (!float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
            }

            byte[] encoded = ReadBoundedFile(filePath);
            HdrRgba16FloatBuffer pixels = Decode(encoded, out float displayPeakNits);
            try
            {
                var bounds = new System.Drawing.Rectangle(0, 0, pixels.Width, pixels.Height);
                var segment = new HdrCaptureSourceSegment(
                    bounds,
                    Path.GetFileName(filePath),
                    wasHdrActive: true,
                    sdrWhiteNits,
                    displayPeakNits);
                return new HdrImageDocument(bounds, pixels, new[] { segment });
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }

        public HdrRgba16FloatBuffer Decode(Stream source)
        {
            byte[] encoded = ReadBoundedStream(source);
            return Decode(encoded, out _);
        }

        private static unsafe HdrRgba16FloatBuffer Decode(
            ReadOnlySpan<byte> encoded,
            out float displayPeakNits)
        {
            if (encoded.IsEmpty || encoded.Length > MaximumEncodedBytes)
            {
                throw new InvalidDataException("The AVIF size exceeds the supported decode limit.");
            }

            HdrEncoderCapabilities.EnsureAvailable(HdrFileFormat.Avif);
            byte[] errorBuffer = new byte[ErrorBufferBytes];
            IntPtr decodedData = IntPtr.Zero;

            try
            {
                fixed (byte* encodedPointer = encoded)
                fixed (byte* errorPointer = errorBuffer)
                {
                    int status = NativeMethods.DecodeRgba10(
                        encodedPointer,
                        checked((nuint)encoded.Length),
                        out decodedData,
                        out uint width,
                        out uint height,
                        out uint stridePixels,
                        out displayPeakNits,
                        errorPointer,
                        ErrorBufferBytes);
                    if (status != 0)
                    {
                        throw new InvalidDataException(
                            $"HDR AVIF decoding failed ({status}): " +
                            AvifHdrImageEncoder.ReadNullTerminatedUtf8(errorBuffer));
                    }

                    long decodedBytes = checked((long)width * height * sizeof(ushort) * 4);
                    if (decodedData == IntPtr.Zero || width == 0 || height == 0 ||
                        width > int.MaxValue || height > int.MaxValue || stridePixels != width ||
                        decodedBytes <= 0 || decodedBytes > MaximumDecodedBytes || decodedBytes > int.MaxValue ||
                        !float.IsFinite(displayPeakNits) || displayPeakNits <= 0f)
                    {
                        throw new InvalidDataException("The native AVIF decoder returned invalid output metadata.");
                    }

                    var result = new HdrRgba16FloatBuffer((int)width, (int)height);
                    try
                    {
                        CopyAndConvert((byte*)decodedData.ToPointer(), result);
                        displayPeakNits = Math.Clamp(
                            displayPeakNits,
                            HdrFileOutputSettings.MinimumMasteringDisplayNits,
                            HdrFileOutputSettings.MaximumMasteringDisplayNits);
                        return result;
                    }
                    catch
                    {
                        result.Dispose();
                        throw;
                    }
                }
            }
            catch (DllNotFoundException exception)
            {
                throw new NotSupportedException(
                    $"HDR AVIF requires the {AvifHdrImageEncoder.NativeLibraryName}.dll native component for this architecture.",
                    exception);
            }
            catch (BadImageFormatException exception)
            {
                throw new NotSupportedException(
                    $"The installed {AvifHdrImageEncoder.NativeLibraryName}.dll native component does not match this process architecture.",
                    exception);
            }
            finally
            {
                if (decodedData != IntPtr.Zero)
                {
                    AvifHdrImageEncoder.NativeMethods.Free(decodedData);
                }
            }
        }

        private static unsafe void CopyAndConvert(byte* source, HdrRgba16FloatBuffer destination)
        {
            int sourceRowBytes = checked(destination.Width * sizeof(ushort) * 4);

            for (int y = 0; y < destination.Height; y++)
            {
                ReadOnlySpan<byte> sourceRow = new ReadOnlySpan<byte>(
                    source + y * sourceRowBytes,
                    sourceRowBytes);
                Span<byte> destinationRow = destination.GetWritableRowSpan(y);

                for (int x = 0; x < destination.Width; x++)
                {
                    int sourceOffset = x * sizeof(ushort) * 4;
                    float redNits = DecodePq(ReadSample(sourceRow, sourceOffset));
                    float greenNits = DecodePq(ReadSample(sourceRow, sourceOffset + 2));
                    float blueNits = DecodePq(ReadSample(sourceRow, sourceOffset + 4));
                    float alpha = ReadSample(sourceRow, sourceOffset + 6);

                    ConvertRec2020ToRec709(
                        redNits,
                        greenNits,
                        blueNits,
                        out float red709,
                        out float green709,
                        out float blue709);

                    int destinationOffset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float normalization = alpha / HdrRgba16FloatBuffer.ReferenceWhiteNits;
                    WriteHalf(destinationRow, destinationOffset, red709 * normalization);
                    WriteHalf(destinationRow, destinationOffset + 2, green709 * normalization);
                    WriteHalf(destinationRow, destinationOffset + 4, blue709 * normalization);
                    WriteHalf(destinationRow, destinationOffset + 6, alpha);
                }
            }
        }

        private static float ReadSample(ReadOnlySpan<byte> source, int offset)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort)));
            if (value > Maximum10BitSample)
            {
                throw new InvalidDataException("The native AVIF decoder returned a sample outside the 10-bit range.");
            }
            return value / (float)Maximum10BitSample;
        }

        private static float DecodePq(float encoded)
        {
            const double m1 = 2610d / 16384d;
            const double m2 = 2523d / 32d;
            const double c1 = 3424d / 4096d;
            const double c2 = 2413d / 128d;
            const double c3 = 2392d / 128d;

            double encodedPower = Math.Pow(Math.Clamp(encoded, 0f, 1f), 1d / m2);
            double numerator = Math.Max(encodedPower - c1, 0d);
            double denominator = c2 - c3 * encodedPower;
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

        private static void WriteHalf(Span<byte> destination, int offset, float value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)value));

        private static byte[] ReadBoundedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("An AVIF file path is required.", nameof(filePath));
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadBoundedStream(stream);
        }

        private static byte[] ReadBoundedStream(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                throw new ArgumentException("The AVIF source stream must be readable and seekable.", nameof(source));
            }

            long originalPosition = source.Position;
            try
            {
                if (source.Length <= 0 || source.Length > MaximumEncodedBytes || source.Length > int.MaxValue)
                {
                    throw new InvalidDataException("The AVIF size exceeds the supported decode limit.");
                }

                source.Position = 0;
                byte[] encoded = new byte[checked((int)source.Length)];
                source.ReadExactly(encoded);
                return encoded;
            }
            finally
            {
                source.Position = originalPosition;
            }
        }

        private static unsafe class NativeMethods
        {
            [DllImport(
                AvifHdrImageEncoder.NativeLibraryName,
                EntryPoint = "sharex_avif_decode_rgba10",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern int DecodeRgba10(
                void* encodedData,
                nuint encodedSize,
                out IntPtr rgba10Data,
                out uint width,
                out uint height,
                out uint stridePixels,
                out float masteringMaximumNits,
                void* error,
                nuint errorCapacity);
        }
    }
}
