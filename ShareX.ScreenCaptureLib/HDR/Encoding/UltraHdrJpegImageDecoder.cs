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
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Decodes Ultra HDR gain-map JPEGs through the pinned libultrahdr v2.0.1
    /// bridge and normalizes its linear 203-nit RGB convention to ShareX scRGB.
    /// </summary>
    public sealed class UltraHdrJpegImageDecoder
    {
        private const string NativeLibraryName = "ShareX.UltraHdr";
        private const int ErrorBufferBytes = 512;
        private const long MaximumEncodedBytes = 512L * 1024 * 1024;
        private const long MaximumDecodedBytes = 1024L * 1024 * 1024;
        private const float UltraHdrToScRgbScale =
            UltraHdrJpegImageEncoder.LibUltraHdrReferenceWhiteNits /
            HdrRgba16FloatBuffer.ReferenceWhiteNits;

        public bool IsSupportedFile(string filePath)
        {
            if (!HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.UltraHdrJpeg, out _) ||
                string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                byte[] encoded = ReadBoundedFile(filePath);
                return UltraHdrJpegImageEncoder.IsUltraHdrImage(encoded);
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
                throw new InvalidDataException("The Ultra HDR JPEG size exceeds the supported decode limit.");
            }

            HdrEncoderCapabilities.EnsureAvailable(HdrFileFormat.UltraHdrJpeg);
            byte[] errorBuffer = new byte[ErrorBufferBytes];
            IntPtr decodedData = IntPtr.Zero;

            try
            {
                fixed (byte* encodedPointer = encoded)
                fixed (byte* errorPointer = errorBuffer)
                {
                    int status = NativeMethods.DecodeRgba16Float(
                        encodedPointer,
                        checked((nuint)encoded.Length),
                        out decodedData,
                        out uint width,
                        out uint height,
                        out uint stridePixels,
                        out int colorGamut,
                        out float hdrCapacityMax,
                        errorPointer,
                        ErrorBufferBytes);
                    if (status != 0)
                    {
                        throw new InvalidDataException(
                            $"Ultra HDR JPEG decoding failed ({status}): {ReadNullTerminatedUtf8(errorBuffer)}");
                    }

                    long decodedBytes = checked((long)width * height * HdrRgba16FloatBuffer.BytesPerPixel);
                    if (decodedData == IntPtr.Zero || width == 0 || height == 0 ||
                        width > int.MaxValue || height > int.MaxValue || stridePixels != width ||
                        decodedBytes <= 0 || decodedBytes > MaximumDecodedBytes || decodedBytes > int.MaxValue ||
                        colorGamut is < 0 or > 2 || !float.IsFinite(hdrCapacityMax) || hdrCapacityMax < 1f)
                    {
                        throw new InvalidDataException("The native Ultra HDR decoder returned invalid output metadata.");
                    }

                    var result = new HdrRgba16FloatBuffer((int)width, (int)height);
                    try
                    {
                        CopyAndNormalize(
                            (byte*)decodedData.ToPointer(),
                            result,
                            colorGamut);
                        displayPeakNits = Math.Clamp(
                            hdrCapacityMax * UltraHdrJpegImageEncoder.LibUltraHdrReferenceWhiteNits,
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
                    $"Ultra HDR JPEG requires the {NativeLibraryName} native component for this architecture.",
                    exception);
            }
            catch (BadImageFormatException exception)
            {
                throw new NotSupportedException(
                    $"The installed {NativeLibraryName} native component does not match this process architecture.",
                    exception);
            }
            finally
            {
                if (decodedData != IntPtr.Zero)
                {
                    NativeMethods.Free(decodedData);
                }
            }
        }

        private static unsafe void CopyAndNormalize(
            byte* source,
            HdrRgba16FloatBuffer destination,
            int colorGamut)
        {
            for (int y = 0; y < destination.Height; y++)
            {
                ReadOnlySpan<byte> sourceRow = new ReadOnlySpan<byte>(
                    source + y * destination.Width * HdrRgba16FloatBuffer.BytesPerPixel,
                    destination.Width * HdrRgba16FloatBuffer.BytesPerPixel);
                Span<byte> destinationRow = destination.GetWritableRowSpan(y);

                for (int x = 0; x < destination.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float red = ReadFiniteHalf(sourceRow, offset);
                    float green = ReadFiniteHalf(sourceRow, offset + 2);
                    float blue = ReadFiniteHalf(sourceRow, offset + 4);
                    float alpha = Math.Clamp(ReadFiniteHalf(sourceRow, offset + 6), 0f, 1f);

                    ConvertToRec709(colorGamut, ref red, ref green, ref blue);
                    WriteHalf(destinationRow, offset, red * UltraHdrToScRgbScale * alpha);
                    WriteHalf(destinationRow, offset + 2, green * UltraHdrToScRgbScale * alpha);
                    WriteHalf(destinationRow, offset + 4, blue * UltraHdrToScRgbScale * alpha);
                    WriteHalf(destinationRow, offset + 6, alpha);
                }
            }
        }

        private static void ConvertToRec709(
            int colorGamut,
            ref float red,
            ref float green,
            ref float blue)
        {
            if (colorGamut == 0)
            {
                return;
            }

            float sourceRed = red;
            float sourceGreen = green;
            float sourceBlue = blue;
            if (colorGamut == 1)
            {
                // Display-P3/D65 to BT.709/D65.
                red = 1.2249401f * sourceRed - 0.2249404f * sourceGreen;
                green = -0.0420569f * sourceRed + 1.0420571f * sourceGreen;
                blue = -0.0196376f * sourceRed - 0.0786361f * sourceGreen + 1.0982735f * sourceBlue;
            }
            else
            {
                // BT.2020/BT.2100 primaries to BT.709/D65.
                red = 1.6604910f * sourceRed - 0.5876411f * sourceGreen - 0.0728499f * sourceBlue;
                green = -0.1245505f * sourceRed + 1.1328999f * sourceGreen - 0.0083494f * sourceBlue;
                blue = -0.0181508f * sourceRed - 0.1005789f * sourceGreen + 1.1187297f * sourceBlue;
            }
        }

        private static byte[] ReadBoundedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("An Ultra HDR JPEG file path is required.", nameof(filePath));
            }

            using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadBoundedStream(stream);
        }

        private static byte[] ReadBoundedStream(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || !source.CanSeek)
            {
                throw new ArgumentException("The Ultra HDR JPEG source stream must be readable and seekable.", nameof(source));
            }

            long originalPosition = source.Position;
            try
            {
                if (source.Length <= 0 || source.Length > MaximumEncodedBytes || source.Length > int.MaxValue)
                {
                    throw new InvalidDataException("The Ultra HDR JPEG size exceeds the supported decode limit.");
                }

                source.Position = 0;
                byte[] encoded = new byte[(int)source.Length];
                source.ReadExactly(encoded);
                return encoded;
            }
            finally
            {
                source.Position = originalPosition;
            }
        }

        private static float ReadFiniteHalf(ReadOnlySpan<byte> source, int offset)
        {
            Half value = BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort))));
            return Half.IsFinite(value) ? (float)value : 0f;
        }

        private static void WriteHalf(Span<byte> destination, int offset, float value)
        {
            float finite = float.IsFinite(value) ? Math.Clamp(value, -65504f, 65504f) : 0f;
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)finite));
        }

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
                EntryPoint = "sharex_uhdr_decode_rgba16f",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern int DecodeRgba16Float(
                void* encodedData,
                nuint encodedSize,
                out IntPtr rgba16FloatData,
                out uint width,
                out uint height,
                out uint stridePixels,
                out int colorGamut,
                out float hdrCapacityMax,
                void* error,
                nuint errorCapacity);

            [DllImport(
                NativeLibraryName,
                EntryPoint = "sharex_uhdr_free",
                CallingConvention = CallingConvention.Cdecl,
                ExactSpelling = true)]
            public static extern void Free(IntPtr allocation);
        }
    }
}
