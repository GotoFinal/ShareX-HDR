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
using System.Runtime.InteropServices;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Encodes backward-compatible gain-map JPEGs through the pinned
    /// libultrahdr v2.0.1 ShareX bridge.
    /// </summary>
    public sealed class UltraHdrJpegImageEncoder : IHdrImageEncoder
    {
        internal const float LibUltraHdrReferenceWhiteNits = 203f;
        internal const float ScRgbToUltraHdrScale =
            HdrRgba16FloatBuffer.ReferenceWhiteNits / LibUltraHdrReferenceWhiteNits;

        private const string NativeLibraryName = "ShareX.UltraHdr";
        private const int ErrorBufferBytes = 512;

        public HdrFileFormat Format => HdrFileFormat.UltraHdrJpeg;

        public unsafe HdrEncodedImageInfo Encode(
            HdrRgba16FloatBuffer source,
            Stream destination,
            HdrImageEncodingOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            HdrEncoderCapabilities.EnsureAvailable(Format);

            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream must be writable.", nameof(destination));
            }

            if (source.Width < 8 || source.Height < 8)
            {
                throw new ArgumentException(
                    "Ultra HDR JPEG requires image dimensions of at least 8 by 8 pixels.", nameof(source));
            }

            options ??= new HdrImageEncodingOptions();
            Stopwatch totalTimer = Stopwatch.StartNew();
            Stopwatch conversionTimer = Stopwatch.StartNew();
            byte[] normalizedPixels = CreateLibUltraHdrInput(
                source,
                options.FlattenTransparencyForUltraHdr);
            conversionTimer.Stop();
            byte[] errorBuffer = new byte[ErrorBufferBytes];
            IntPtr encodedData = IntPtr.Zero;

            try
            {
                fixed (byte* inputPointer = normalizedPixels)
                fixed (byte* errorPointer = errorBuffer)
                {
                    Stopwatch codecTimer = Stopwatch.StartNew();
                    int result = NativeMethods.EncodeRgba16Float(
                        inputPointer,
                        checked((uint)source.Width),
                        checked((uint)source.Height),
                        checked((uint)source.Width),
                        Math.Clamp(options.Quality, 1, 100),
                        Math.Clamp(options.GainMapQuality, 1, 100),
                        Math.Clamp(options.GetValidatedMasteringDisplayMaximumNits(), 203f, 10000f),
                        out encodedData,
                        out nuint encodedSize,
                        errorPointer,
                        ErrorBufferBytes);
                    codecTimer.Stop();

                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            $"Ultra HDR JPEG encoding failed ({result}): {ReadNullTerminatedUtf8(errorBuffer)}");
                    }

                    if (encodedData == IntPtr.Zero || encodedSize == 0 || encodedSize > int.MaxValue)
                    {
                        throw new InvalidOperationException("The native Ultra HDR encoder returned an invalid buffer.");
                    }

                    Stopwatch writeTimer = Stopwatch.StartNew();
                    destination.Write(new ReadOnlySpan<byte>(encodedData.ToPointer(), checked((int)encodedSize)));
                    writeTimer.Stop();
                    totalTimer.Stop();
                    HdrEncodingPerformance.Log(
                        $"HDR Ultra JPEG encode stages | size={source.Width}x{source.Height} " +
                        $"conversionMs={conversionTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"codecMs={codecTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"writeMs={writeTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"bytes={encodedSize} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                    return new HdrEncodedImageInfo(
                        Format,
                        ".jpg",
                        "image/jpeg",
                        false,
                        checked((long)encodedSize));
                }
            }
            catch (DllNotFoundException e)
            {
                throw new NotSupportedException(
                    $"Ultra HDR JPEG requires the {NativeLibraryName} native component for this architecture.",
                    e);
            }
            catch (BadImageFormatException e)
            {
                throw new NotSupportedException(
                    $"The installed {NativeLibraryName} native component does not match this process architecture.",
                    e);
            }
            finally
            {
                if (encodedData != IntPtr.Zero)
                {
                    NativeMethods.Free(encodedData);
                }
            }
        }

        internal static byte[] CreateLibUltraHdrInput(
            HdrRgba16FloatBuffer source,
            bool flattenTransparencyToBlack = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            byte[] result = new byte[checked(source.Width * source.Height * HdrRgba16FloatBuffer.BytesPerPixel)];

            for (int y = 0; y < source.Height; y++)
            {
                ReadOnlySpan<byte> sourceRow = source.GetRowSpan(y);
                Span<byte> destinationRow = result.AsSpan(
                    y * source.Width * HdrRgba16FloatBuffer.BytesPerPixel,
                    source.Width * HdrRgba16FloatBuffer.BytesPerPixel);

                for (int x = 0; x < source.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float alpha = Math.Clamp(ReadFiniteHalf(sourceRow, offset + 6), 0f, 1f);
                    if (alpha < 1f && !flattenTransparencyToBlack)
                    {
                        throw new InvalidOperationException(
                            "Ultra HDR JPEG cannot preserve transparency. Select OpenEXR/HDR PNG, " +
                            "or explicitly enable flattening transparent pixels to black.");
                    }

                    float red = ReadFiniteHalf(sourceRow, offset);
                    float green = ReadFiniteHalf(sourceRow, offset + 2);
                    float blue = ReadFiniteHalf(sourceRow, offset + 4);

                    if (!flattenTransparencyToBlack)
                    {
                        red = Unpremultiply(red, alpha);
                        green = Unpremultiply(green, alpha);
                        blue = Unpremultiply(blue, alpha);
                    }

                    red *= ScRgbToUltraHdrScale;
                    green *= ScRgbToUltraHdrScale;
                    blue *= ScRgbToUltraHdrScale;

                    WriteHalf(destinationRow, offset, Math.Clamp(red, 0f, 10000f / LibUltraHdrReferenceWhiteNits));
                    WriteHalf(destinationRow, offset + 2, Math.Clamp(green, 0f, 10000f / LibUltraHdrReferenceWhiteNits));
                    WriteHalf(destinationRow, offset + 4, Math.Clamp(blue, 0f, 10000f / LibUltraHdrReferenceWhiteNits));
                    WriteHalf(destinationRow, offset + 6, flattenTransparencyToBlack ? 1f : alpha);
                }
            }

            return result;
        }
        public static unsafe bool IsUltraHdrImage(ReadOnlySpan<byte> encodedImage)
        {
            if (encodedImage.IsEmpty)
            {
                return false;
            }

            fixed (byte* encodedPointer = encodedImage)
            {
                return NativeMethods.IsUltraHdrImage(encodedPointer, encodedImage.Length) != 0;
            }
        }


        private static float ReadFiniteHalf(ReadOnlySpan<byte> source, int offset)
        {
            Half value = BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2)));
            return Half.IsFinite(value) ? (float)value : 0f;
        }

        private static float Unpremultiply(float value, float alpha) =>
            alpha > 0f ? value / alpha : 0f;

        private static void WriteHalf(Span<byte> destination, int offset, float value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, 2),
                BitConverter.HalfToUInt16Bits((Half)value));

        private static string ReadNullTerminatedUtf8(byte[] buffer)
        {
            int length = Array.IndexOf(buffer, (byte)0);
            if (length < 0)
            {
                length = buffer.Length;
            }

            string message = Encoding.UTF8.GetString(buffer, 0, length).Trim();
            return string.IsNullOrEmpty(message) ? "No details supplied" : message;
        }

        private static unsafe class NativeMethods
        {
            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_uhdr_encode_rgba16f",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern int EncodeRgba16Float(
                void* rgba16Float,
                uint width,
                uint height,
                uint stridePixels,
                int baseQuality,
                int gainMapQuality,
                float targetPeakNits,
                out IntPtr encodedData,
                out nuint encodedSize,
                void* error,
                nuint errorCapacity);

            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_uhdr_is_image",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern int IsUltraHdrImage(
                void* encodedImage,
                int encodedSize);

            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_uhdr_free",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern void Free(IntPtr allocation);
        }
    }
}
