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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Encodes a 10-bit 4:4:4 BT.2020/PQ AVIF through the pinned libavif bridge.
    /// The managed conversion keeps ShareX's scRGB convention out of the native ABI.
    /// </summary>
    public sealed class AvifHdrImageEncoder : IHdrImageEncoder
    {
        internal const string NativeLibraryName = "ShareX.Avif";
        private const int ErrorBufferBytes = 512;

        public HdrFileFormat Format => HdrFileFormat.Avif;

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

            options ??= new HdrImageEncodingOptions();
            Stopwatch totalTimer = Stopwatch.StartNew();
            float masteringMaximumNits = options.GetValidatedMasteringDisplayMaximumNits();
            float masteringMinimumNits = options.GetValidatedMasteringDisplayMinimumNits(masteringMaximumNits);
            HdrPqPixelConversionResult conversion = HdrPqPixelConverter.Convert(
                source,
                masteringMaximumNits,
                HdrPqPixelLayout.Rgba10LittleEndian,
                options.ProcessingBackend);
            byte[] pqPixels = conversion.Pixels;
            byte[] errorBuffer = new byte[ErrorBufferBytes];
            IntPtr encodedData = IntPtr.Zero;

            try
            {
                fixed (byte* inputPointer = pqPixels)
                fixed (byte* errorPointer = errorBuffer)
                {
                    Stopwatch codecTimer = Stopwatch.StartNew();
                    int result = NativeMethods.EncodeRgba10(
                        inputPointer,
                        checked((uint)source.Width),
                        checked((uint)source.Height),
                        checked((uint)source.Width),
                        Math.Clamp(options.AvifQuality, 1, 100),
                        Math.Clamp(options.AvifSpeed, 0, 10),
                        masteringMaximumNits,
                        masteringMinimumNits,
                        conversion.MaxCll,
                        conversion.MaxFall,
                        out encodedData,
                        out nuint encodedSize,
                        errorPointer,
                        ErrorBufferBytes);
                    codecTimer.Stop();

                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            $"HDR AVIF encoding failed ({result}): {ReadNullTerminatedUtf8(errorBuffer)}");
                    }

                    if (encodedData == IntPtr.Zero || encodedSize == 0 || encodedSize > int.MaxValue)
                    {
                        throw new InvalidOperationException("The native AVIF encoder returned an invalid buffer.");
                    }

                    Stopwatch writeTimer = Stopwatch.StartNew();
                    destination.Write(new ReadOnlySpan<byte>(encodedData.ToPointer(), checked((int)encodedSize)));
                    writeTimer.Stop();
                    totalTimer.Stop();
                    HdrEncodingPerformance.Log(
                        $"HDR AVIF encode stages | size={source.Width}x{source.Height} " +
                        $"backend={conversion.Backend} conversionMs={conversion.TotalMilliseconds:F1} " +
                        $"codecMs={codecTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"writeMs={writeTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"bytes={encodedSize} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                    return new HdrEncodedImageInfo(
                        Format,
                        ".avif",
                        "image/avif",
                        false,
                        checked((long)encodedSize),
                        conversion.MaxCll,
                        conversion.MaxFall);
                }
            }
            catch (DllNotFoundException exception)
            {
                throw new NotSupportedException(
                    $"HDR AVIF requires the {NativeLibraryName}.dll native component for this architecture.",
                    exception);
            }
            catch (BadImageFormatException exception)
            {
                throw new NotSupportedException(
                    $"The installed {NativeLibraryName}.dll native component does not match this process architecture.",
                    exception);
            }
            finally
            {
                if (encodedData != IntPtr.Zero)
                {
                    NativeMethods.Free(encodedData);
                }
            }
        }

        internal static byte[] CreatePackedRgba10(
            HdrRgba16FloatBuffer source,
            float masteringMaximumNits,
            out float maxCll,
            out float maxFall)
        {
            HdrPqPixelConversionResult conversion = HdrPqPixelConverter.ConvertCpu(
                source,
                masteringMaximumNits,
                HdrPqPixelLayout.Rgba10LittleEndian);
            maxCll = conversion.MaxCll;
            maxFall = conversion.MaxFall;
            return conversion.Pixels;
        }

        public static unsafe bool TryProbeHdr(
            ReadOnlySpan<byte> encodedImage,
            out int width,
            out int height,
            out float masteringMaximumNits,
            out float maxCll,
            out float maxFall)
        {
            width = 0;
            height = 0;
            masteringMaximumNits = 0f;
            maxCll = 0f;
            maxFall = 0f;

            if (encodedImage.IsEmpty ||
                !HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.Avif, out _))
            {
                return false;
            }

            fixed (byte* encodedPointer = encodedImage)
            {
                int result = NativeMethods.ProbeHdr(
                    encodedPointer,
                    checked((nuint)encodedImage.Length),
                    out uint nativeWidth,
                    out uint nativeHeight,
                    out uint depth,
                    out masteringMaximumNits,
                    out ushort nativeMaxCll,
                    out ushort nativeMaxFall);
                if (result == 0 || nativeWidth == 0 || nativeHeight == 0 ||
                    nativeWidth > int.MaxValue || nativeHeight > int.MaxValue || depth != 10)
                {
                    return false;
                }

                width = (int)nativeWidth;
                height = (int)nativeHeight;
                maxCll = nativeMaxCll;
                maxFall = nativeMaxFall;
                return float.IsFinite(masteringMaximumNits) && masteringMaximumNits > 0f;
            }
        }

        internal static string ReadNullTerminatedUtf8(byte[] buffer)
        {
            int length = Array.IndexOf(buffer, (byte)0);
            if (length < 0)
            {
                length = buffer.Length;
            }

            string message = Encoding.UTF8.GetString(buffer, 0, length).Trim();
            return string.IsNullOrEmpty(message) ? "No details supplied" : message;
        }

        internal static unsafe class NativeMethods
        {
            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_avif_encode_rgba10",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern int EncodeRgba10(
                void* rgba10,
                uint width,
                uint height,
                uint stridePixels,
                int quality,
                int speed,
                float masteringMaximumNits,
                float masteringMinimumNits,
                float maxCll,
                float maxFall,
                out IntPtr encodedData,
                out nuint encodedSize,
                void* error,
                nuint errorCapacity);

            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_avif_probe_hdr",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern int ProbeHdr(
                void* encodedData,
                nuint encodedSize,
                out uint width,
                out uint height,
                out uint depth,
                out float masteringMaximumNits,
                out ushort maxCll,
                out ushort maxFall);

            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_avif_free",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern void Free(IntPtr allocation);
        }
    }
}
