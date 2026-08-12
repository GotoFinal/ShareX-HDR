#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib
{
    internal static unsafe class HdrToSdrToneMapper
    {
        // In scRGB, 1.0 represents 80 nits. Windows exposes the actual SDR
        // white level for each active HDR monitor through DisplayConfig.
        private const float ScRgbNitsPerUnit = 80f;
        private const float HdrDetectionMargin = 1.05f;
        private const float SdrShoulderStart = 0.75f;
        private const float SdrWhiteOutputLevel = 0.90f;
        private const float MaximumSignalNits = 10000f;

        public static Bitmap ToneMapRgba16Float(
            IntPtr source,
            int sourceRowPitch,
            int width,
            int height,
            HdrCaptureSettings settings,
            float sdrWhiteNits)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (source == IntPtr.Zero)
            {
                throw new ArgumentException("The source pointer cannot be null.", nameof(source));
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "The frame dimensions must be positive.");
            }

            if (sourceRowPitch < width * sizeof(ushort) * 4)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRowPitch), "The source row pitch is smaller than one RGBA16F row.");
            }

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            try
            {
                BitmapData destination = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                try
                {
                    ToneMapRgba16Float(
                        (byte*)source,
                        sourceRowPitch,
                        (byte*)destination.Scan0,
                        destination.Stride,
                        width,
                        height,
                        settings.HdrBrightnessNits,
                        sdrWhiteNits);
                }
                finally
                {
                    bitmap.UnlockBits(destination);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static void ToneMapRgba16Float(
            byte* source,
            int sourceRowPitch,
            byte* destination,
            int destinationStride,
            int width,
            int height,
            float configuredPeakNits,
            float sdrWhiteNits)
        {
            float paperWhiteNits = Math.Clamp(
                sdrWhiteNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumBrightnessNits);
            float paperWhiteScRgb = paperWhiteNits / ScRgbNitsPerUnit;
            float framePeakNits = FindFramePeakNits(
                source,
                sourceRowPitch,
                width,
                height);
            bool containsHdrHeadroom = framePeakNits > paperWhiteNits * HdrDetectionMargin;
            float sourcePeakNits = Math.Max(
                Math.Clamp(
                    configuredPeakNits,
                    HdrCaptureSettings.MinimumBrightnessNits,
                    HdrCaptureSettings.MaximumBrightnessNits),
                framePeakNits);
            float sourcePeakRelativeToWhite = Math.Max(sourcePeakNits / paperWhiteNits, HdrDetectionMargin);

            for (int y = 0; y < height; y++)
            {
                ushort* sourcePixel = (ushort*)(source + y * sourceRowPitch);
                byte* destinationPixel = destination + y * destinationStride;

                for (int x = 0; x < width; x++)
                {
                    float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                    float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                    float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));
                    ToneMapPixel(
                        ref red,
                        ref green,
                        ref blue,
                        paperWhiteScRgb,
                        sourcePeakRelativeToWhite,
                        containsHdrHeadroom);

                    destinationPixel[0] = ToSrgbByte(blue);
                    destinationPixel[1] = ToSrgbByte(green);
                    destinationPixel[2] = ToSrgbByte(red);
                    destinationPixel[3] = 255;

                    sourcePixel += 4;
                    destinationPixel += 4;
                }
            }
        }

        private static void ToneMapPixel(
            ref float red,
            ref float green,
            ref float blue,
            float paperWhiteScRgb,
            float sourcePeakRelativeToWhite,
            bool containsHdrHeadroom)
        {
            float luminance = red * 0.2126f + green * 0.7152f + blue * 0.0722f;

            if (luminance <= 0f)
            {
                red = green = blue = 0f;
                return;
            }

            float normalizedLuminance = luminance / paperWhiteScRgb;
            float mappedLuminance = containsHdrHeadroom
                ? ToneMapHdrLuminance(normalizedLuminance, sourcePeakRelativeToWhite)
                : Math.Clamp(normalizedLuminance, 0f, 1f);

            float scale = mappedLuminance / luminance;
            red = Math.Clamp(red * scale, 0f, 1f);
            green = Math.Clamp(green * scale, 0f, 1f);
            blue = Math.Clamp(blue * scale, 0f, 1f);
        }

        private static float ToneMapHdrLuminance(float luminance, float sourcePeak)
        {
            if (luminance <= SdrShoulderStart)
            {
                return Math.Max(luminance, 0f);
            }

            if (luminance <= 1f)
            {
                float range = 1f - SdrShoulderStart;
                float position = (luminance - SdrShoulderStart) / range;
                float highlightSlope = Math.Min(
                    1f,
                    (1f - SdrWhiteOutputLevel) / MathF.Log(sourcePeak));

                return CubicHermite(
                    SdrShoulderStart,
                    SdrWhiteOutputLevel,
                    range,
                    highlightSlope * range,
                    position);
            }

            float highlightPosition = Math.Clamp(MathF.Log(luminance) / MathF.Log(sourcePeak), 0f, 1f);
            return SdrWhiteOutputLevel + (1f - SdrWhiteOutputLevel) * highlightPosition;
        }

        private static float CubicHermite(
            float start,
            float end,
            float startTangent,
            float endTangent,
            float position)
        {
            float positionSquared = position * position;
            float positionCubed = positionSquared * position;

            return (2f * positionCubed - 3f * positionSquared + 1f) * start +
                (positionCubed - 2f * positionSquared + position) * startTangent +
                (-2f * positionCubed + 3f * positionSquared) * end +
                (positionCubed - positionSquared) * endTangent;
        }

        private static float FindFramePeakNits(
            byte* source,
            int sourceRowPitch,
            int width,
            int height)
        {
            float peakLuminance = 0f;

            for (int y = 0; y < height; y++)
            {
                ushort* sourcePixel = (ushort*)(source + y * sourceRowPitch);

                for (int x = 0; x < width; x++)
                {
                    float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                    float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                    float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));
                    float luminance = red * 0.2126f + green * 0.7152f + blue * 0.0722f;
                    peakLuminance = Math.Max(peakLuminance, luminance);

                    sourcePixel += 4;
                }
            }

            return Math.Min(peakLuminance * ScRgbNitsPerUnit, MaximumSignalNits);
        }

        private static byte ToSrgbByte(float linear)
        {
            float srgb = linear <= 0.0031308f
                ? linear * 12.92f
                : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;

            return ToByte(srgb);
        }

        private static float SanitizeLinear(float value)
        {
            return float.IsFinite(value) && value > 0f ? value : 0f;
        }

        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        }
    }
}
