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
using System.Diagnostics;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    internal enum HdrPqPixelLayout
    {
        Rgba10LittleEndian,
        PngRgba16BigEndian
    }

    internal sealed class HdrPqPixelConversionResult
    {
        public byte[] Pixels { get; init; }
        public float MaxCll { get; init; }
        public float MaxFall { get; init; }
        public string Backend { get; init; }
        public double TotalMilliseconds { get; init; }
    }

    /// <summary>
    /// Converts premultiplied linear scRGB FP16 pixels to straight-alpha
    /// BT.2020/PQ samples shared by the AVIF and PNG encoders.
    /// </summary>
    internal static unsafe class HdrPqPixelConverter
    {
        private const int PqLookupBits = 16;
        private const int PqLookupSize = 1 << PqLookupBits;
        private const float PqMaximumNits = 10000f;
        private static readonly float[] PqSqrtLookup = CreatePqSqrtLookup();

        public static HdrPqPixelConversionResult Convert(
            HdrRgba16FloatBuffer source,
            float masteringMaximumNits,
            HdrPqPixelLayout layout,
            HdrProcessingBackend backend)
        {
            ArgumentNullException.ThrowIfNull(source);
            masteringMaximumNits = Math.Clamp(
                masteringMaximumNits,
                HdrFileOutputSettings.MinimumMasteringDisplayNits,
                HdrFileOutputSettings.MaximumMasteringDisplayNits);

            string fallbackReason = null;
            if (backend == HdrProcessingBackend.Gpu &&
                GpuHdrPqPixelConverter.TryConvert(
                    source,
                    masteringMaximumNits,
                    layout,
                    out HdrPqPixelConversionResult gpuResult,
                    out fallbackReason))
            {
                return gpuResult;
            }

            if (backend == HdrProcessingBackend.Gpu)
            {
                HdrEncodingPerformance.Log(
                    $"HDR output conversion fallback | requested=GPU backend=CPU " +
                    $"reason={fallbackReason}");
            }

            return ConvertCpu(source, masteringMaximumNits, layout);
        }

        internal static HdrPqPixelConversionResult ConvertCpu(
            HdrRgba16FloatBuffer source,
            float masteringMaximumNits,
            HdrPqPixelLayout layout)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            int rowPrefix = layout == HdrPqPixelLayout.PngRgba16BigEndian ? 1 : 0;
            int maximumSample = layout == HdrPqPixelLayout.Rgba10LittleEndian ? 1023 : 65535;
            int rowBytes = checked(rowPrefix + source.Width * sizeof(ushort) * 4);
            byte[] result = new byte[checked(rowBytes * source.Height)];
            float[] rowMaxima = new float[source.Height];
            double[] rowLuminanceSums = new double[source.Height];
            bool useParallelConversion =
                source.Height >= 64 && source.Width * source.Height >= 262144;

            fixed (byte* sourcePointer = source.GetWritablePixelSpan())
            fixed (byte* destinationPointer = result)
            {
                nint sourceAddress = (nint)sourcePointer;
                nint destinationAddress = (nint)destinationPointer;

                Action<int> convertRow = y => ConvertRow(
                    (byte*)sourceAddress + y * source.RowBytes,
                    (byte*)destinationAddress + y * rowBytes,
                    source.Width,
                    masteringMaximumNits,
                    maximumSample,
                    layout,
                    out rowMaxima[y],
                    out rowLuminanceSums[y]);

                if (useParallelConversion)
                {
                    Parallel.For(0, source.Height, convertRow);
                }
                else
                {
                    for (int y = 0; y < source.Height; y++)
                    {
                        convertRow(y);
                    }
                }
            }

            float maxCll = 0f;
            double luminanceSum = 0d;
            for (int y = 0; y < source.Height; y++)
            {
                maxCll = Math.Max(maxCll, rowMaxima[y]);
                luminanceSum += rowLuminanceSums[y];
            }

            totalTimer.Stop();
            float maxFall = (float)(luminanceSum / checked((long)source.Width * source.Height));
            HdrEncodingPerformance.Log(
                $"HDR output conversion | backend=CPU size={source.Width}x{source.Height} " +
                $"layout={layout} parallel={useParallelConversion} " +
                $"workers={(useParallelConversion ? Environment.ProcessorCount : 1)} " +
                $"totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");

            return new HdrPqPixelConversionResult
            {
                Pixels = result,
                MaxCll = maxCll,
                MaxFall = maxFall,
                Backend = "CPU",
                TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds
            };
        }

        private static void ConvertRow(
            byte* sourceRow,
            byte* destinationRow,
            int width,
            float masteringMaximumNits,
            int maximumSample,
            HdrPqPixelLayout layout,
            out float rowMaxCll,
            out double rowLuminanceSum)
        {
            bool pngLayout = layout == HdrPqPixelLayout.PngRgba16BigEndian;
            if (pngLayout)
            {
                destinationRow[0] = 0;
                destinationRow++;
            }

            rowMaxCll = 0f;
            rowLuminanceSum = 0d;
            ushort* sourceSamples = (ushort*)sourceRow;

            for (int x = 0; x < width; x++)
            {
                float alpha = Math.Clamp(ReadFiniteHalf(sourceSamples[3]), 0f, 1f);
                float red709 = Unpremultiply(ReadFiniteHalf(sourceSamples[0]), alpha);
                float green709 = Unpremultiply(ReadFiniteHalf(sourceSamples[1]), alpha);
                float blue709 = Unpremultiply(ReadFiniteHalf(sourceSamples[2]), alpha);

                float red2020 = 0.6274039f * red709 + 0.3292830f * green709 + 0.0433131f * blue709;
                float green2020 = 0.0690973f * red709 + 0.9195404f * green709 + 0.0113623f * blue709;
                float blue2020 = 0.0163914f * red709 + 0.0880133f * green709 + 0.8955953f * blue709;

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

                rowMaxCll = Math.Max(rowMaxCll, Math.Max(redNits, Math.Max(greenNits, blueNits)));
                rowLuminanceSum += 0.2627d * redNits + 0.6780d * greenNits + 0.0593d * blueNits;

                WriteSample(destinationRow, QuantizePq(redNits, maximumSample), pngLayout);
                WriteSample(destinationRow + 2, QuantizePq(greenNits, maximumSample), pngLayout);
                WriteSample(destinationRow + 4, QuantizePq(blueNits, maximumSample), pngLayout);
                WriteSample(
                    destinationRow + 6,
                    checked((ushort)Math.Clamp(
                        (int)MathF.Round(alpha * maximumSample),
                        0,
                        maximumSample)),
                    pngLayout);

                sourceSamples += 4;
                destinationRow += 8;
            }
        }

        private static float ReadFiniteHalf(ushort bits)
        {
            Half value = BitConverter.UInt16BitsToHalf(bits);
            return Half.IsFinite(value) ? (float)value : 0f;
        }

        private static float Unpremultiply(float value, float alpha) => alpha > 0f ? value / alpha : 0f;

        internal static ushort QuantizePq(float nits, int maximumSample)
        {
            float root = MathF.Sqrt(Math.Clamp(nits / PqMaximumNits, 0f, 1f));
            float lookupPosition = root * PqLookupSize;
            int index = Math.Min((int)lookupPosition, PqLookupSize - 1);
            float fraction = lookupPosition - index;
            float lower = PqSqrtLookup[index];
            float pq = lower + (PqSqrtLookup[index + 1] - lower) * fraction;
            return checked((ushort)Math.Clamp(
                (int)MathF.Round(pq * maximumSample),
                0,
                maximumSample));
        }

        private static void WriteSample(byte* destination, ushort value, bool bigEndian)
        {
            if (bigEndian)
            {
                destination[0] = (byte)(value >> 8);
                destination[1] = (byte)value;
            }
            else
            {
                destination[0] = (byte)value;
                destination[1] = (byte)(value >> 8);
            }
        }

        private static float[] CreatePqSqrtLookup()
        {
            var lookup = new float[PqLookupSize + 1];
            for (int index = 0; index <= PqLookupSize; index++)
            {
                double root = index / (double)PqLookupSize;
                lookup[index] = EncodePqExact((float)(root * root * PqMaximumNits));
            }

            return lookup;
        }

        private static float EncodePqExact(float nits)
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
    }
}
