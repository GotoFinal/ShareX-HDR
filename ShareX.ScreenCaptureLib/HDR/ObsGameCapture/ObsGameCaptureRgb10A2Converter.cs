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

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// The OBS hook protocol publishes RGB10A2's DXGI format but not its color space.
    /// This choice must therefore come from the caller, just as it does in OBS Studio.
    /// </summary>
    public enum ObsGameCaptureRgb10A2ColorSpace
    {
        Srgb,
        Rec2100Pq
    }

    public static class ObsGameCaptureRgb10A2Converter
    {
        private const int SourceBytesPerPixel = 4;
        private const float PqMaximumNitsInScRgb = 10_000f / HdrRgba16FloatBuffer.ReferenceWhiteNits;

        public static HdrRgba16FloatBuffer ConvertToRgba16Float(
            ReadOnlySpan<byte> source,
            int sourceRowBytes,
            int width,
            int height,
            ObsGameCaptureRgb10A2ColorSpace colorSpace)
        {
            return ConvertToRgba16Float(
                source, sourceRowBytes, width, height, colorSpace, ObsGameCaptureAlphaMode.Straight);
        }

        public static HdrRgba16FloatBuffer ConvertToRgba16Float(
            ReadOnlySpan<byte> source,
            int sourceRowBytes,
            int width,
            int height,
            ObsGameCaptureRgb10A2ColorSpace colorSpace,
            bool allowTransparency)
        {
            return ConvertToRgba16Float(
                source,
                sourceRowBytes,
                width,
                height,
                colorSpace,
                allowTransparency ? ObsGameCaptureAlphaMode.Straight : ObsGameCaptureAlphaMode.Opaque);
        }

        public static HdrRgba16FloatBuffer ConvertToRgba16Float(
            ReadOnlySpan<byte> source,
            int sourceRowBytes,
            int width,
            int height,
            ObsGameCaptureRgb10A2ColorSpace colorSpace,
            ObsGameCaptureAlphaMode alphaMode)
        {
            if (!Enum.IsDefined(colorSpace))
            {
                throw new ArgumentOutOfRangeException(nameof(colorSpace));
            }

            if (!Enum.IsDefined(alphaMode))
            {
                throw new ArgumentOutOfRangeException(nameof(alphaMode));
            }

            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            int activeSourceRowBytes = checked(width * SourceBytesPerPixel);

            if (sourceRowBytes < activeSourceRowBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRowBytes));
            }

            int requiredLength = checked((height - 1) * sourceRowBytes + activeSourceRowBytes);

            if (source.Length < requiredLength)
            {
                throw new ArgumentException("The source buffer is smaller than the requested image.", nameof(source));
            }

            var result = new HdrRgba16FloatBuffer(width, height);
            Span<byte> destination = result.GetWritablePixelSpan();

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> sourceRow = source.Slice(y * sourceRowBytes, activeSourceRowBytes);
                Span<byte> destinationRow = destination.Slice(y * result.RowBytes, width * HdrRgba16FloatBuffer.BytesPerPixel);

                for (int x = 0; x < width; x++)
                {
                    uint packed = BinaryPrimitives.ReadUInt32LittleEndian(
                        sourceRow.Slice(x * SourceBytesPerPixel, SourceBytesPerPixel));
                    ConvertPixel(packed, colorSpace, alphaMode, destinationRow.Slice(
                        x * HdrRgba16FloatBuffer.BytesPerPixel,
                        HdrRgba16FloatBuffer.BytesPerPixel));
                }
            }

            return result;
        }

        internal static unsafe HdrRgba16FloatBuffer ConvertToRgba16Float(
            IntPtr source,
            int sourceRowBytes,
            int width,
            int height,
            ObsGameCaptureRgb10A2ColorSpace colorSpace)
        {
            return ConvertToRgba16Float(
                source, sourceRowBytes, width, height, colorSpace, ObsGameCaptureAlphaMode.Straight);
        }

        internal static unsafe HdrRgba16FloatBuffer ConvertToRgba16Float(
            IntPtr source,
            int sourceRowBytes,
            int width,
            int height,
            ObsGameCaptureRgb10A2ColorSpace colorSpace,
            bool allowTransparency)
        {
            return ConvertToRgba16Float(
                source,
                sourceRowBytes,
                width,
                height,
                colorSpace,
                allowTransparency ? ObsGameCaptureAlphaMode.Straight : ObsGameCaptureAlphaMode.Opaque);
        }

        internal static unsafe HdrRgba16FloatBuffer ConvertToRgba16Float(
            IntPtr source,
            int sourceRowBytes,
            int width,
            int height,
            ObsGameCaptureRgb10A2ColorSpace colorSpace,
            ObsGameCaptureAlphaMode alphaMode)
        {
            if (source == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(source));
            }

            int sourceLength = checked(sourceRowBytes * height);
            return ConvertToRgba16Float(
                new ReadOnlySpan<byte>(source.ToPointer(), sourceLength),
                sourceRowBytes,
                width,
                height,
                colorSpace,
                alphaMode);
        }

        private static void ConvertPixel(
            uint packed,
            ObsGameCaptureRgb10A2ColorSpace colorSpace,
            ObsGameCaptureAlphaMode alphaMode,
            Span<byte> destination)
        {
            float red = (packed & 0x3ff) / 1023f;
            float green = ((packed >> 10) & 0x3ff) / 1023f;
            float blue = ((packed >> 20) & 0x3ff) / 1023f;
            float alpha = ((packed >> 30) & 0x3) / 3f;

            if (colorSpace == ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq)
            {
                float red2020 = DecodePq(red) * PqMaximumNitsInScRgb;
                float green2020 = DecodePq(green) * PqMaximumNitsInScRgb;
                float blue2020 = DecodePq(blue) * PqMaximumNitsInScRgb;

                red = 1.660491f * red2020 - 0.587641f * green2020 - 0.072850f * blue2020;
                green = -0.124550f * red2020 + 1.132900f * green2020 - 0.008349f * blue2020;
                blue = -0.018151f * red2020 - 0.100579f * green2020 + 1.118730f * blue2020;
            }
            else
            {
                red = DecodeSrgb(red);
                green = DecodeSrgb(green);
                blue = DecodeSrgb(blue);
            }

            float outputAlpha = alphaMode == ObsGameCaptureAlphaMode.Opaque ? 1f : alpha;
            float alphaMultiplier = alphaMode == ObsGameCaptureAlphaMode.Straight ? alpha : 1f;
            WriteHalf(destination, 0, red * alphaMultiplier);
            WriteHalf(destination, 2, green * alphaMultiplier);
            WriteHalf(destination, 4, blue * alphaMultiplier);
            WriteHalf(destination, 6, outputAlpha);
        }

        private static float DecodeSrgb(float encoded)
        {
            return encoded <= 0.04045f
                ? encoded / 12.92f
                : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        private static float DecodePq(float encoded)
        {
            const float m1 = 2610f / 16384f;
            const float m2 = 2523f / 32f;
            const float c1 = 3424f / 4096f;
            const float c2 = 2413f / 128f;
            const float c3 = 2392f / 128f;

            float power = MathF.Pow(encoded, 1f / m2);
            float numerator = MathF.Max(power - c1, 0f);
            float denominator = c2 - c3 * power;
            return MathF.Pow(numerator / denominator, 1f / m1);
        }

        private static void WriteHalf(Span<byte> destination, int offset, float value)
        {
            value = Math.Clamp(value, (float)Half.MinValue, (float)Half.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, 2),
                BitConverter.HalfToUInt16Bits((Half)value));
        }
    }
}
