#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Ordinary 8-bit sRGB AVIF. Decoding deliberately accepts only the
    /// single-frame, full-range identity-matrix subset produced by this codec.
    /// It must not interpret HDR/PQ or other unmanaged color spaces as SDR.
    /// </summary>
    public static class SdrAvifImageCodec
    {
        private const int ErrorBufferBytes = 512;
        private const long MaximumEncodedBytes = 512L * 1024 * 1024;
        private const long MaximumDecodedBytes = 1024L * 1024 * 1024;

        public static bool TryGetAvailability(out string unavailableReason) =>
            HdrEncoderCapabilities.TryGetNativeAvailability(
                "SDR AVIF", "ShareX.Avif.dll",
                ["sharex_avif_encode_bgra8", "sharex_avif_decode_bgra8", "sharex_avif_probe_sdr", "sharex_avif_free"],
                [Architecture.X64, Architecture.Arm64],
                "Use PNG or OpenEXR on this architecture.", out unavailableReason);

        public static unsafe void Encode(Image source, Stream destination, int quality = 90, int speed = 6)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite) throw new ArgumentException("The AVIF destination must be writable.", nameof(destination));
            EnsureAvailable();

            Bitmap converted = null;
            Bitmap bitmap = source as Bitmap;
            if (bitmap == null || bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            {
                bitmap = converted = CreateArgbBitmap(source);
            }
            BitmapData data = null;
            IntPtr encodedData = IntPtr.Zero;
            try
            {
                data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] packedRows = null;
                IntPtr input = data.Scan0;
                int rowBytes = data.Stride;
                if (rowBytes < 0)
                {
                    rowBytes = checked(bitmap.Width * 4);
                    packedRows = new byte[checked(rowBytes * bitmap.Height)];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        new ReadOnlySpan<byte>((byte*)data.Scan0 + (nint)y * data.Stride, rowBytes)
                            .CopyTo(packedRows.AsSpan(y * rowBytes, rowBytes));
                    }
                }
                byte[] error = new byte[ErrorBufferBytes];
                fixed (byte* packedPointer = packedRows)
                fixed (byte* errorPointer = error)
                {
                    int status = NativeMethods.Encode(
                        packedRows == null ? input.ToPointer() : packedPointer,
                        checked((uint)bitmap.Width), checked((uint)bitmap.Height), checked((uint)rowBytes),
                        Math.Clamp(quality, 0, 100), Math.Clamp(speed, 0, 10),
                        out encodedData, out nuint encodedSize, errorPointer, ErrorBufferBytes);
                    if (status != 0) throw new InvalidDataException($"SDR AVIF encoding failed ({status}): {AvifHdrImageEncoder.ReadNullTerminatedUtf8(error)}");
                    if (encodedData == IntPtr.Zero || encodedSize == 0 || encodedSize > MaximumEncodedBytes)
                        throw new InvalidDataException("The native SDR AVIF encoder returned invalid output.");
                    destination.Write(new ReadOnlySpan<byte>(encodedData.ToPointer(), checked((int)encodedSize)));
                }
            }
            finally
            {
                if (encodedData != IntPtr.Zero) AvifHdrImageEncoder.NativeMethods.Free(encodedData);
                if (data != null) bitmap.UnlockBits(data);
                converted?.Dispose();
            }
        }

        public static unsafe bool TryProbe(ReadOnlySpan<byte> encoded, out int width, out int height)
        {
            width = height = 0;
            if (encoded.IsEmpty || encoded.Length > MaximumEncodedBytes || !TryGetAvailability(out _)) return false;
            fixed (byte* input = encoded)
            {
                if (NativeMethods.Probe(input, (nuint)encoded.Length, out uint nativeWidth, out uint nativeHeight) != 1 ||
                    nativeWidth == 0 || nativeHeight == 0 || nativeWidth > int.MaxValue || nativeHeight > int.MaxValue ||
                    (long)nativeWidth * nativeHeight * 4 > MaximumDecodedBytes) return false;
                width = (int)nativeWidth;
                height = (int)nativeHeight;
                return true;
            }
        }

        public static bool IsSupportedFile(string filePath)
        {
            try
            {
                using var source = File.OpenRead(filePath);
                if (source.Length <= 0 || source.Length > MaximumEncodedBytes) return false;
                byte[] encoded = new byte[checked((int)source.Length)];
                source.ReadExactly(encoded);
                return TryProbe(encoded, out _, out _);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static unsafe Bitmap Decode(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek) throw new ArgumentException("The AVIF source must be readable and seekable.", nameof(source));
            if (source.Length <= 0 || source.Length > MaximumEncodedBytes) throw new InvalidDataException("The AVIF file exceeds the decode limit.");
            EnsureAvailable();
            long position = source.Position;
            byte[] encoded = new byte[checked((int)source.Length)];
            try
            {
                source.Position = 0;
                source.ReadExactly(encoded);
            }
            finally { source.Position = position; }
            IntPtr decodedData = IntPtr.Zero;
            try
            {
                byte[] error = new byte[ErrorBufferBytes];
                fixed (byte* input = encoded)
                fixed (byte* errorPointer = error)
                {
                    int status = NativeMethods.Decode(input, (nuint)encoded.Length, out decodedData,
                        out uint width, out uint height, errorPointer, ErrorBufferBytes);
                    if (status != 0) throw new InvalidDataException($"SDR AVIF decoding failed ({status}): {AvifHdrImageEncoder.ReadNullTerminatedUtf8(error)}");
                    if (decodedData == IntPtr.Zero || width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
                        (long)width * height * 4 > MaximumDecodedBytes)
                        throw new InvalidDataException("The native SDR AVIF decoder returned invalid output.");
                    var result = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
                    try
                    {
                        BitmapData data = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            int rowBytes = checked((int)width * 4);
                            for (int y = 0; y < (int)height; y++)
                            {
                                new ReadOnlySpan<byte>((byte*)decodedData + (nint)y * rowBytes, rowBytes)
                                    .CopyTo(new Span<byte>((byte*)data.Scan0 + (nint)y * data.Stride, rowBytes));
                            }
                        }
                        finally { result.UnlockBits(data); }
                        return result;
                    }
                    catch { result.Dispose(); throw; }
                }
            }
            finally { if (decodedData != IntPtr.Zero) AvifHdrImageEncoder.NativeMethods.Free(decodedData); }
        }

        internal static Bitmap CreateArgbBitmap(Image source)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            try
            {
                using Graphics graphics = Graphics.FromImage(result);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(source, new Rectangle(Point.Empty, result.Size));
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static void EnsureAvailable()
        {
            if (!TryGetAvailability(out string reason)) throw new NotSupportedException(reason);
        }

        private static unsafe class NativeMethods
        {
            [DllImport(AvifHdrImageEncoder.NativeLibraryName, EntryPoint = "sharex_avif_encode_bgra8", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern int Encode(void* bgra8, uint width, uint height, uint rowBytes, int quality, int speed,
                out IntPtr encodedData, out nuint encodedSize, void* error, nuint errorCapacity);

            [DllImport(AvifHdrImageEncoder.NativeLibraryName, EntryPoint = "sharex_avif_probe_sdr", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern int Probe(void* encodedData, nuint encodedSize, out uint width, out uint height);

            [DllImport(AvifHdrImageEncoder.NativeLibraryName, EntryPoint = "sharex_avif_decode_bgra8", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern int Decode(void* encodedData, nuint encodedSize, out IntPtr bgra8, out uint width, out uint height, void* error, nuint errorCapacity);
        }
    }
}
