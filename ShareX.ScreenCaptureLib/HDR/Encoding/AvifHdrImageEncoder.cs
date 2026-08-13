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
        private const int Maximum10BitSample = 1023;
        private const float PqMaximumNits = 10000f;

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
            float masteringMaximumNits = options.GetValidatedMasteringDisplayMaximumNits();
            float masteringMinimumNits = options.GetValidatedMasteringDisplayMinimumNits(masteringMaximumNits);
            byte[] pqPixels = CreatePackedRgba10(
                source,
                masteringMaximumNits,
                out float maxCll,
                out float maxFall);
            byte[] errorBuffer = new byte[ErrorBufferBytes];
            IntPtr encodedData = IntPtr.Zero;

            try
            {
                fixed (byte* inputPointer = pqPixels)
                fixed (byte* errorPointer = errorBuffer)
                {
                    int result = NativeMethods.EncodeRgba10(
                        inputPointer,
                        checked((uint)source.Width),
                        checked((uint)source.Height),
                        checked((uint)source.Width),
                        Math.Clamp(options.AvifQuality, 1, 100),
                        Math.Clamp(options.AvifSpeed, 0, 10),
                        masteringMaximumNits,
                        masteringMinimumNits,
                        maxCll,
                        maxFall,
                        out encodedData,
                        out nuint encodedSize,
                        errorPointer,
                        ErrorBufferBytes);

                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            $"HDR AVIF encoding failed ({result}): {ReadNullTerminatedUtf8(errorBuffer)}");
                    }

                    if (encodedData == IntPtr.Zero || encodedSize == 0 || encodedSize > int.MaxValue)
                    {
                        throw new InvalidOperationException("The native AVIF encoder returned an invalid buffer.");
                    }

                    destination.Write(new ReadOnlySpan<byte>(encodedData.ToPointer(), checked((int)encodedSize)));
                    return new HdrEncodedImageInfo(
                        Format,
                        ".avif",
                        "image/avif",
                        false,
                        checked((long)encodedSize),
                        maxCll,
                        maxFall);
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
            ArgumentNullException.ThrowIfNull(source);
            masteringMaximumNits = Math.Clamp(
                masteringMaximumNits,
                HdrFileOutputSettings.MinimumMasteringDisplayNits,
                HdrFileOutputSettings.MaximumMasteringDisplayNits);

            byte[] result = new byte[checked(source.Width * source.Height * sizeof(ushort) * 4)];
            maxCll = 0f;
            double luminanceSum = 0d;

            for (int y = 0; y < source.Height; y++)
            {
                ReadOnlySpan<byte> sourceRow = source.GetRowSpan(y);
                Span<byte> destinationRow = result.AsSpan(
                    y * source.Width * sizeof(ushort) * 4,
                    source.Width * sizeof(ushort) * 4);

                for (int x = 0; x < source.Width; x++)
                {
                    int sourceOffset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float alpha = Math.Clamp(ReadFiniteHalf(sourceRow, sourceOffset + 6), 0f, 1f);
                    float red709 = Unpremultiply(ReadFiniteHalf(sourceRow, sourceOffset), alpha);
                    float green709 = Unpremultiply(ReadFiniteHalf(sourceRow, sourceOffset + 2), alpha);
                    float blue709 = Unpremultiply(ReadFiniteHalf(sourceRow, sourceOffset + 4), alpha);

                    ConvertRec709ToRec2020(
                        red709,
                        green709,
                        blue709,
                        out float red2020,
                        out float green2020,
                        out float blue2020);

                    float redNits = Math.Clamp(
                        red2020 * HdrRgba16FloatBuffer.ReferenceWhiteNits,
                        0f,
                        masteringMaximumNits);
                    float greenNits = Math.Clamp(
                        green2020 * HdrRgba16FloatBuffer.ReferenceWhiteNits,
                        0f,
                        masteringMaximumNits);
                    float blueNits = Math.Clamp(
                        blue2020 * HdrRgba16FloatBuffer.ReferenceWhiteNits,
                        0f,
                        masteringMaximumNits);

                    maxCll = Math.Max(maxCll, Math.Max(redNits, Math.Max(greenNits, blueNits)));
                    luminanceSum += 0.2627d * redNits + 0.6780d * greenNits + 0.0593d * blueNits;

                    int destinationOffset = x * sizeof(ushort) * 4;
                    WriteSample(destinationRow, destinationOffset, EncodePq(redNits));
                    WriteSample(destinationRow, destinationOffset + 2, EncodePq(greenNits));
                    WriteSample(destinationRow, destinationOffset + 4, EncodePq(blueNits));
                    WriteSample(destinationRow, destinationOffset + 6, alpha);
                }
            }

            maxFall = (float)(luminanceSum / checked((long)source.Width * source.Height));
            return result;
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

        private static float ReadFiniteHalf(ReadOnlySpan<byte> source, int offset)
        {
            Half value = BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort))));
            return Half.IsFinite(value) ? (float)value : 0f;
        }

        private static float Unpremultiply(float value, float alpha) => alpha > 0f ? value / alpha : 0f;

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

        private static void WriteSample(Span<byte> destination, int offset, float value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, sizeof(ushort)),
                checked((ushort)Math.Clamp(
                    (int)MathF.Round(value * Maximum10BitSample),
                    0,
                    Maximum10BitSample)));

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
