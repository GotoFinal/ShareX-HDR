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

using ShareX.HelpersLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    internal static unsafe class HdrToSdrToneMapper
    {
        // In scRGB, 1.0 represents 80 nits. Windows exposes the actual SDR
        // white level for each active HDR monitor through DisplayConfig.
        private const float ScRgbNitsPerUnit = 80f;
        private const float HdrDetectionMargin = 1.02f;
        private const float ReferenceHdrWhiteNits = 203f;
        private const int ContentPeakHistogramBins = 512;
        private const int MinimumReliablePeakSamples = 8;
        private const int MaximumPeakAnalysisSamples = 1_000_000;
        private const double ContentPeakPercentile = 0.999;
        private const float ContentPeakHeadroom = 1.05f;
        private const int TileSize = 16;
        private const int TileLinkRadius = 6;
        private const int MinimumRegionSeedTiles = 4;
        private const int BoundarySearchDistance = TileSize * 4;
        private const int UncertainBoundaryFeather = TileSize * 2;
        private const float MinimumBoundaryStrength = 0.015f;
        private const float BoundaryStrengthRatio = 4f;
        private const int SrgbLutMaximum = 65535;
        private const int AutomaticPromotionTileSize = 16;
        private const int AutomaticPromotionMinimumPixelsPerTile = 8;
        private const int MaximumPromotedWindowCacheEntries = 256;
        private static readonly byte[] SrgbEncodingLut = CreateSrgbEncodingLut();
        private static readonly ConcurrentDictionary<PromotedWindowIdentity, byte> promotedWindows =
            new ConcurrentDictionary<PromotedWindowIdentity, byte>();

        public static Bitmap ToneMapRgba16Float(
            IntPtr source,
            int sourceRowPitch,
            int width,
            int height,
            HdrCaptureSettings settings,
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            bool preserveAlpha = false,
            IReadOnlyList<HdrWindowRegion> windowRegions = null)
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
                        settings.PeakBrightnessMode,
                        settings.ToneMappingMode,
                        settings.PaperWhiteMode,
                        settings.PaperWhiteNits,
                        sdrWhiteNits,
                        displayMaxLuminanceNits,
                        preserveAlpha,
                        windowRegions);
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

        internal static ToneMapParameters CreateToneMapParameters(
            float configuredPeakNits,
            HdrPeakBrightnessMode peakBrightnessMode,
            HdrToneMappingMode toneMappingMode,
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            float measuredContentPeakNits = float.NaN,
            HdrPaperWhiteMode paperWhiteMode = HdrPaperWhiteMode.Automatic,
            float customPaperWhiteNits = HdrCaptureSettings.DefaultBrightnessNits)
        {
            float paperWhiteNits = Math.Clamp(
                paperWhiteMode == HdrPaperWhiteMode.Custom
                    ? customPaperWhiteNits
                    : sdrWhiteNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumPaperWhiteNits);
            float sourcePeakNits = ResolveSourcePeakNits(
                configuredPeakNits,
                peakBrightnessMode,
                paperWhiteNits,
                displayMaxLuminanceNits,
                measuredContentPeakNits);

            return new ToneMapParameters(
                paperWhiteNits,
                paperWhiteNits / ScRgbNitsPerUnit,
                sourcePeakNits);
        }

        internal static byte[] CreateToneMapMaskR8(
            IntPtr source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            HdrToneMappingMode toneMappingMode = HdrToneMappingMode.ContentAware,
            IReadOnlyList<Rectangle> windowRectangles = null)
        {
            IReadOnlyList<HdrWindowRegion> windowRegions = windowRectangles?
                .Select(x => new HdrWindowRegion(x))
                .ToArray();
            return CreateToneMapMaskR8WithWindowRegions(
                (IntPtr)source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb,
                toneMappingMode,
                windowRegions);
        }

        internal static byte[] CreateToneMapMaskR8WithWindowRegions(
            IntPtr source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            HdrToneMappingMode toneMappingMode,
            IReadOnlyList<HdrWindowRegion> windowRegions)
        {
            if (source == IntPtr.Zero)
            {
                throw new ArgumentException(nameof(source));
            }

            if (sourceRowPitch < width * sizeof(ushort) * 4)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRowPitch));
            }

            byte[] mask = new byte[checked(width * height)];
            if (toneMappingMode == HdrToneMappingMode.Uniform)
            {
                Array.Fill(mask, byte.MaxValue);
                return mask;
            }

            HdrFrameAnalysis analysis = AnalyzeFrame(
                (byte*)source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb);
            nint sourceAddress = source;
            float headroomThreshold = paperWhiteScRgb * HdrDetectionMargin;

            Parallel.For(0, height, y =>
            {
                int rowOffset = y * width;
                ushort* sourcePixel = (ushort*)((byte*)sourceAddress + y * sourceRowPitch);

                for (int x = 0; x < width; x++)
                {
                    float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                    float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                    float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));
                    mask[rowOffset + x] = GetRec2020Max(red, green, blue) > headroomThreshold
                        ? byte.MaxValue
                        : ToByte(analysis.GetToneMapAmount(x, y));
                    sourcePixel += 4;
                }
            });

            ApplyWindowBoundaries(
                (byte*)source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb,
                toneMappingMode,
                windowRegions,
                mask);
            return mask;
        }

        private static void ToneMapRgba16Float(
            byte* source,
            int sourceRowPitch,
            byte* destination,
            int destinationStride,
            int width,
            int height,
            float configuredPeakNits,
            HdrPeakBrightnessMode peakBrightnessMode,
            HdrToneMappingMode toneMappingMode,
            HdrPaperWhiteMode paperWhiteMode,
            float customPaperWhiteNits,
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            bool preserveAlpha,
            IReadOnlyList<HdrWindowRegion> windowRegions)
        {
            HdrCaptureSettings settings = new HdrCaptureSettings
            {
                HdrBrightnessNits = configuredPeakNits,
                PeakBrightnessMode = peakBrightnessMode,
                ToneMappingMode = toneMappingMode,
                PaperWhiteMode = paperWhiteMode,
                PaperWhiteNits = customPaperWhiteNits
            };
            ToneMapInputAnalysis inputAnalysis = AnalyzeToneMapInput(
                (IntPtr)source,
                sourceRowPitch,
                width,
                height,
                settings,
                sdrWhiteNits,
                displayMaxLuminanceNits,
                preserveAlpha,
                windowRegions);
            LogAnalysis("CPU", settings, inputAnalysis, displayMaxLuminanceNits);
            ToneMapParameters parameters = inputAnalysis.Parameters;
            float paperWhiteScRgb = parameters.PaperWhiteScRgb;
            bool useContentDetection = inputAnalysis.ToneMapMask != null;
            byte[] toneMapMask = inputAnalysis.ToneMapMask;
            ReferenceWhiteToneMapper toneMapper = new ReferenceWhiteToneMapper(parameters.InputMaximum);

            nint sourceAddress = (nint)source;
            nint destinationAddress = (nint)destination;

            Parallel.For(0, height, y =>
            {
                ushort* sourcePixel = (ushort*)((byte*)sourceAddress + y * sourceRowPitch);
                byte* destinationPixel = (byte*)destinationAddress + y * destinationStride;

                for (int x = 0; x < width; x++)
                {
                    float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                    float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                    float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));
                    float alpha = Math.Clamp(
                        SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[3])),
                        0f,
                        1f);

                    if (preserveAlpha)
                    {
                        if (alpha <= 0f)
                        {
                            red = green = blue = 0f;
                        }
                        else if (alpha < 1f)
                        {
                            float inverseAlpha = 1f / alpha;
                            red *= inverseAlpha;
                            green *= inverseAlpha;
                            blue *= inverseAlpha;
                        }
                    }
                    ToneMapPixel(
                        ref red,
                        ref green,
                        ref blue,
                        paperWhiteScRgb,
                        toneMapper,
                        useContentDetection
                            ? toneMapMask[y * width + x] / 255f
                            : 1f);

                    destinationPixel[0] = ToSrgbByte(blue);
                    destinationPixel[1] = ToSrgbByte(green);
                    destinationPixel[2] = ToSrgbByte(red);
                    destinationPixel[3] = preserveAlpha ? ToByte(alpha) : byte.MaxValue;

                    sourcePixel += 4;
                    destinationPixel += 4;
                }
            });
        }

        private static float ResolveSourcePeakNits(
            float configuredPeakNits,
            HdrPeakBrightnessMode peakBrightnessMode,
            float paperWhiteNits,
            float displayMaxLuminanceNits,
            float measuredContentPeakNits)
        {
            float sourcePeakNits = Math.Clamp(
                configuredPeakNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumBrightnessNits);
            if (peakBrightnessMode == HdrPeakBrightnessMode.Custom)
            {
                return sourcePeakNits;
            }

            if (float.IsFinite(measuredContentPeakNits) && measuredContentPeakNits > 0f)
            {
                return Math.Clamp(
                    measuredContentPeakNits,
                    paperWhiteNits * 1.0001f,
                    HdrCaptureSettings.MaximumBrightnessNits);
            }

            // DXGI MaxLuminance describes the display, not necessarily the
            // content values retained in the scRGB compositor surface. Using
            // it as the content maximum clipped every value above the panel
            // peak to identical SDR white. Normal automatic calls always pass
            // a measurement; this is only a compatibility fallback for callers
            // that construct parameters without an image to analyze.
            return HdrCaptureSettings.DefaultCustomPeakBrightnessNits;
        }

        internal static ToneMapInputAnalysis AnalyzeToneMapInput(
            IntPtr source,
            int sourceRowPitch,
            int width,
            int height,
            HdrCaptureSettings settings,
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            bool preserveAlpha = false,
            IReadOnlyList<HdrWindowRegion> windowRegions = null)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (source == IntPtr.Zero || width <= 0 || height <= 0 ||
                sourceRowPitch < width * sizeof(ushort) * 4)
            {
                throw new ArgumentException("The HDR source buffer is invalid.", nameof(source));
            }

            float paperWhiteNits = Math.Clamp(
                settings.PaperWhiteMode == HdrPaperWhiteMode.Custom
                    ? settings.PaperWhiteNits
                    : sdrWhiteNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumPaperWhiteNits);
            float paperWhiteScRgb = paperWhiteNits / ScRgbNitsPerUnit;
            byte[] toneMapMask = settings.ToneMappingMode != HdrToneMappingMode.Uniform
                ? CreateToneMapMaskR8WithWindowRegions(
                    source,
                    sourceRowPitch,
                    width,
                    height,
                    paperWhiteScRgb,
                    settings.ToneMappingMode,
                    windowRegions)
                : null;
            ContentPeakMeasurement measurement = settings.PeakBrightnessMode == HdrPeakBrightnessMode.Automatic
                ? MeasureContentPeak(
                    (byte*)source,
                    sourceRowPitch,
                    width,
                    height,
                    paperWhiteNits,
                    toneMapMask,
                    settings.ToneMappingMode,
                    preserveAlpha)
                : ContentPeakMeasurement.NotMeasured;
            ToneMapParameters parameters = CreateToneMapParameters(
                settings.HdrBrightnessNits,
                settings.PeakBrightnessMode,
                settings.ToneMappingMode,
                sdrWhiteNits,
                displayMaxLuminanceNits,
                measurement.EffectivePeakNits,
                settings.PaperWhiteMode,
                settings.PaperWhiteNits);
            return new ToneMapInputAnalysis(parameters, toneMapMask, measurement);
        }

        internal static ToneMapInputAnalysis AnalyzeToneMapInput(
            HdrRgba16FloatBuffer source,
            HdrCaptureSettings settings,
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            bool preserveAlpha = false,
            IReadOnlyList<HdrWindowRegion> windowRegions = null)
        {
            ArgumentNullException.ThrowIfNull(source);

            fixed (byte* sourcePointer = source.GetWritablePixelSpan())
            {
                return AnalyzeToneMapInput(
                    (IntPtr)sourcePointer,
                    source.RowBytes,
                    source.Width,
                    source.Height,
                    settings,
                    sdrWhiteNits,
                    displayMaxLuminanceNits,
                    preserveAlpha,
                    windowRegions);
            }
        }

        internal static void LogAnalysis(
            string backend,
            HdrCaptureSettings settings,
            ToneMapInputAnalysis analysis,
            float displayMaxLuminanceNits)
        {
            ContentPeakMeasurement peak = analysis.ContentPeak;
            string peakSource = settings.PeakBrightnessMode == HdrPeakBrightnessMode.Custom
                ? "custom"
                : peak.SampleCount > 0
                    ? "measured"
                    : "automatic-fallback";
            DebugHelper.WriteLine(
                $"HDR tone-map analysis | backend={backend} mode={settings.ToneMappingMode} " +
                $"paperWhiteMode={settings.PaperWhiteMode} paperWhite={analysis.Parameters.PaperWhiteNits:F1}nits " +
                $"peakMode={settings.PeakBrightnessMode} peakSource={peakSource} " +
                $"sourcePeak={analysis.Parameters.SourcePeakNits:F1}nits " +
                $"observedPeak={peak.ObservedMaximumNits:F1}nits samples={peak.SampleCount} " +
                $"sampleStep={peak.SampleStep} displayPeak={displayMaxLuminanceNits:F1}nits " +
                $"reason={peak.Reason}");
        }

        private static ContentPeakMeasurement MeasureContentPeak(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteNits,
            byte[] toneMapMask,
            HdrToneMappingMode toneMappingMode,
            bool preserveAlpha)
        {
            long totalPixels = (long)width * height;
            int sampleStep = Math.Max(
                1,
                (int)Math.Ceiling(Math.Sqrt(totalPixels / (double)MaximumPeakAnalysisSamples)));
            float thresholdNits = paperWhiteNits * HdrDetectionMargin;
            float maximumNits = HdrCaptureSettings.MaximumBrightnessNits;
            double logarithmicRange = Math.Log(maximumNits / thresholdNits);
            int[] histogram = new int[ContentPeakHistogramBins];
            int sampleCount = 0;
            float observedMaximumNits = 0f;

            for (int y = 0; y < height; y += sampleStep)
            {
                ushort* sourcePixel = (ushort*)(source + y * sourceRowPitch);

                for (int x = 0; x < width; x += sampleStep)
                {
                    int maskIndex = y * width + x;
                    if (toneMapMask != null && toneMapMask[maskIndex] < 128)
                    {
                        continue;
                    }

                    ushort* pixel = sourcePixel + x * 4;
                    float alpha = Math.Clamp(
                        SanitizeLinear((float)BitConverter.UInt16BitsToHalf(pixel[3])),
                        0f,
                        1f);
                    if (preserveAlpha && alpha <= 0.001f)
                    {
                        continue;
                    }

                    float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(pixel[0]));
                    float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(pixel[1]));
                    float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(pixel[2]));
                    if (preserveAlpha && alpha < 1f)
                    {
                        float inverseAlpha = 1f / alpha;
                        red *= inverseAlpha;
                        green *= inverseAlpha;
                        blue *= inverseAlpha;
                    }

                    float peakNits = GetRec2020Max(red, green, blue) * ScRgbNitsPerUnit;
                    if (!float.IsFinite(peakNits) || peakNits <= thresholdNits)
                    {
                        continue;
                    }

                    peakNits = Math.Min(peakNits, maximumNits);
                    observedMaximumNits = Math.Max(observedMaximumNits, peakNits);
                    double normalized = logarithmicRange > 0d
                        ? Math.Log(peakNits / thresholdNits) / logarithmicRange
                        : 0d;
                    int bin = Math.Clamp(
                        (int)(normalized * ContentPeakHistogramBins),
                        0,
                        ContentPeakHistogramBins - 1);
                    histogram[bin]++;
                    sampleCount++;
                }
            }

            if (sampleCount == 0)
            {
                return new ContentPeakMeasurement(
                    paperWhiteNits * 1.0001f,
                    0f,
                    0,
                    sampleStep,
                    "No tone-mapped pixels exceeded SDR paper white.");
            }

            if (sampleCount < MinimumReliablePeakSamples)
            {
                float sparsePeak = toneMappingMode == HdrToneMappingMode.Uniform
                    ? paperWhiteNits * 1.0001f
                    : Math.Min(HdrCaptureSettings.DefaultCustomPeakBrightnessNits, maximumNits);
                return new ContentPeakMeasurement(
                    sparsePeak,
                    observedMaximumNits,
                    sampleCount,
                    sampleStep,
                    "Too few HDR samples for a stable percentile; used the safe sparse-content fallback.");
            }

            int targetRank = Math.Clamp(
                (int)Math.Ceiling(sampleCount * ContentPeakPercentile),
                1,
                sampleCount);
            int cumulative = 0;
            int selectedBin = histogram.Length - 1;

            for (int bin = 0; bin < histogram.Length; bin++)
            {
                cumulative += histogram[bin];
                if (cumulative >= targetRank)
                {
                    selectedBin = bin;
                    break;
                }
            }

            double upperNormalized = (selectedBin + 1d) / ContentPeakHistogramBins;
            float percentilePeak = (float)(thresholdNits * Math.Exp(logarithmicRange * upperNormalized));
            float effectivePeak = Math.Clamp(
                percentilePeak * ContentPeakHeadroom,
                paperWhiteNits * 1.0001f,
                maximumNits);
            return new ContentPeakMeasurement(
                effectivePeak,
                observedMaximumNits,
                sampleCount,
                sampleStep,
                $"Measured the {ContentPeakPercentile:P1} HDR-sample percentile with {ContentPeakHeadroom:P0} headroom.");
        }

        private static void ToneMapPixel(
            ref float red,
            ref float green,
            ref float blue,
            float paperWhiteScRgb,
            ReferenceWhiteToneMapper toneMapper,
            float toneMapAmount)
        {
            red /= paperWhiteScRgb;
            green /= paperWhiteScRgb;
            blue /= paperWhiteScRgb;
            float maxRgb = GetRec2020Max(red, green, blue);

            if (toneMapAmount > 0f)
            {
                float gain = toneMapper.GetGain(maxRgb);
                float blendedGain = 1f + (gain - 1f) * toneMapAmount;
                red *= blendedGain;
                green *= blendedGain;
                blue *= blendedGain;
            }

            red = Math.Clamp(red, 0f, 1f);
            green = Math.Clamp(green, 0f, 1f);
            blue = Math.Clamp(blue, 0f, 1f);
        }

        private static HdrFrameAnalysis AnalyzeFrame(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb)
        {
            int tilesX = (width + TileSize - 1) / TileSize;
            int tilesY = (height + TileSize - 1) / TileSize;
            bool[] seedTiles = new bool[tilesX * tilesY];
            float headroomThreshold = paperWhiteScRgb * HdrDetectionMargin;
            nint sourceAddress = (nint)source;

            Parallel.For(0, tilesY, tileY =>
            {
                int top = tileY * TileSize;
                int bottom = Math.Min(height, top + TileSize);

                for (int tileX = 0; tileX < tilesX; tileX++)
                {
                    int left = tileX * TileSize;
                    int right = Math.Min(width, left + TileSize);
                    int headroomPixelCount = 0;

                    for (int y = top; y < bottom; y++)
                    {
                        ushort* sourcePixel = (ushort*)((byte*)sourceAddress + y * sourceRowPitch) + left * 4;

                        for (int x = left; x < right; x++)
                        {
                            float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                            float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                            float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));

                            if (GetRec2020Max(red, green, blue) > headroomThreshold)
                            {
                                headroomPixelCount++;
                            }

                            sourcePixel += 4;
                        }
                    }

                    int tilePixels = (right - left) * (bottom - top);
                    int minimumHeadroomPixels = Math.Max(2, (tilePixels + 99) / 100);
                    int tileIndex = tileY * tilesX + tileX;
                    seedTiles[tileIndex] = headroomPixelCount >= minimumHeadroomPixels;
                }
            });

            return new HdrFrameAnalysis(BuildToneMapRegions(
                source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb,
                seedTiles,
                tilesX,
                tilesY));
        }

        private static void ApplyWindowBoundaries(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            HdrToneMappingMode toneMappingMode,
            IReadOnlyList<HdrWindowRegion> windowRegions,
            byte[] mask)
        {
            if (windowRegions == null || windowRegions.Count == 0)
            {
                return;
            }

            List<VisibleWindowRegion> visibleWindows = BuildVisibleWindowRegions(
                width,
                height,
                windowRegions);
            float headroomThreshold = paperWhiteScRgb * HdrDetectionMargin;

            foreach (VisibleWindowRegion window in visibleWindows)
            {
                bool forceHdr = window.Region.PromotionKind == HdrWindowPromotionKind.ForceHdr;

                if (window.Region.PromotionKind == HdrWindowPromotionKind.DetectFullscreenHdr)
                {
                    forceHdr = IsPromoted(window.Region) || HasCoherentHdrContent(
                        source,
                        sourceRowPitch,
                        window.ContentRectangles,
                        headroomThreshold);

                    if (forceHdr)
                    {
                        RememberPromotion(window.Region);
                    }
                }

                if (forceHdr)
                {
                    // Keep title bars/non-client pixels SDR while promoting the
                    // complete visible game client. Higher z-order windows were
                    // already subtracted from both rectangle collections.
                    FillMaskRectangles(mask, width, window.Rectangles, 0);
                    FillMaskRectangles(mask, width, window.ContentRectangles, byte.MaxValue);
                    continue;
                }

                bool isHdr = HasHdrContent(
                    source,
                    sourceRowPitch,
                    window.Rectangles,
                    headroomThreshold,
                    toneMappingMode == HdrToneMappingMode.PerWindow);

                if (toneMappingMode == HdrToneMappingMode.ContentAware && isHdr)
                {
                    continue;
                }

                byte value = isHdr && toneMappingMode == HdrToneMappingMode.PerWindow
                    ? byte.MaxValue
                    : (byte)0;
                FillMaskRectangles(mask, width, window.Rectangles, value);
            }
        }

        private static List<VisibleWindowRegion> BuildVisibleWindowRegions(
            int width,
            int height,
            IReadOnlyList<HdrWindowRegion> windowRegions)
        {
            var result = new List<VisibleWindowRegion>();
            var uncovered = new List<Rectangle>
            {
                new Rectangle(0, 0, width, height)
            };
            var frame = new Rectangle(0, 0, width, height);

            foreach (HdrWindowRegion windowRegion in windowRegions)
            {
                Rectangle clippedWindow = Rectangle.Intersect(frame, windowRegion.Bounds);
                if (clippedWindow.Width <= 0 || clippedWindow.Height <= 0)
                {
                    continue;
                }

                var visible = new List<Rectangle>();
                var nextUncovered = new List<Rectangle>();

                foreach (Rectangle available in uncovered)
                {
                    Rectangle intersection = Rectangle.Intersect(available, clippedWindow);
                    if (intersection.Width <= 0 || intersection.Height <= 0)
                    {
                        nextUncovered.Add(available);
                        continue;
                    }

                    visible.Add(intersection);
                    SubtractRectangle(available, intersection, nextUncovered);
                }

                if (visible.Count > 0)
                {
                    List<Rectangle> visibleContent = visible
                        .Select(x => Rectangle.Intersect(x, windowRegion.ContentBounds))
                        .Where(x => x.Width > 0 && x.Height > 0)
                        .ToList();
                    result.Add(new VisibleWindowRegion(visible, visibleContent, windowRegion));
                }

                uncovered = nextUncovered;
                if (uncovered.Count == 0)
                {
                    break;
                }
            }

            return result;
        }

        private static bool HasCoherentHdrContent(
            byte* source,
            int sourceRowPitch,
            IReadOnlyList<Rectangle> rectangles,
            float headroomThreshold)
        {
            foreach (Rectangle rectangle in rectangles)
            {
                for (int tileTop = rectangle.Top;
                    tileTop < rectangle.Bottom;
                    tileTop += AutomaticPromotionTileSize)
                {
                    int tileBottom = Math.Min(rectangle.Bottom, tileTop + AutomaticPromotionTileSize);

                    for (int tileLeft = rectangle.Left;
                        tileLeft < rectangle.Right;
                        tileLeft += AutomaticPromotionTileSize)
                    {
                        int tileRight = Math.Min(rectangle.Right, tileLeft + AutomaticPromotionTileSize);
                        int headroomPixels = 0;

                        for (int y = tileTop; y < tileBottom; y++)
                        {
                            ushort* sourcePixel = (ushort*)(source + y * sourceRowPitch) + tileLeft * 4;

                            for (int x = tileLeft; x < tileRight; x++)
                            {
                                float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                                float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                                float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));
                                if (GetRec2020Max(red, green, blue) > headroomThreshold &&
                                    ++headroomPixels >= AutomaticPromotionMinimumPixelsPerTile)
                                {
                                    return true;
                                }

                                sourcePixel += 4;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsPromoted(HdrWindowRegion region)
        {
            return region.HasStableIdentity && promotedWindows.ContainsKey(
                new PromotedWindowIdentity(
                    region.WindowHandle,
                    region.ProcessId,
                    region.ProcessStartTimeUtcTicks));
        }

        private static void RememberPromotion(HdrWindowRegion region)
        {
            if (!region.HasStableIdentity)
            {
                return;
            }

            if (promotedWindows.Count >= MaximumPromotedWindowCacheEntries)
            {
                promotedWindows.Clear();
            }

            if (promotedWindows.TryAdd(
                new PromotedWindowIdentity(
                    region.WindowHandle,
                    region.ProcessId,
                    region.ProcessStartTimeUtcTicks),
                0))
            {
                DebugHelper.WriteLine(
                    $"HDR tone mapping | automatically promoted fullscreen window " +
                    $"pid={region.ProcessId} hwnd=0x{region.WindowHandle:X}");
            }
        }

        internal static void ClearWindowPromotionCacheForTests()
        {
            promotedWindows.Clear();
        }

        private static void SubtractRectangle(
            Rectangle source,
            Rectangle intersection,
            ICollection<Rectangle> result)
        {
            AddIfVisible(new Rectangle(
                source.Left,
                source.Top,
                source.Width,
                intersection.Top - source.Top));
            AddIfVisible(new Rectangle(
                source.Left,
                intersection.Bottom,
                source.Width,
                source.Bottom - intersection.Bottom));
            AddIfVisible(new Rectangle(
                source.Left,
                intersection.Top,
                intersection.Left - source.Left,
                intersection.Height));
            AddIfVisible(new Rectangle(
                intersection.Right,
                intersection.Top,
                source.Right - intersection.Right,
                intersection.Height));

            void AddIfVisible(Rectangle rectangle)
            {
                if (rectangle.Width > 0 && rectangle.Height > 0)
                {
                    result.Add(rectangle);
                }
            }
        }

        private static bool HasHdrContent(
            byte* source,
            int sourceRowPitch,
            IReadOnlyList<Rectangle> rectangles,
            float headroomThreshold,
            bool requireWindowCoverage)
        {
            long pixelCount = 0;
            foreach (Rectangle rectangle in rectangles)
            {
                pixelCount += (long)rectangle.Width * rectangle.Height;
            }

            int requiredPixels = requireWindowCoverage
                ? (int)Math.Min(int.MaxValue, Math.Max(2L, (pixelCount + 99L) / 100L))
                : 2;
            int headroomPixels = 0;

            foreach (Rectangle rectangle in rectangles)
            {
                for (int y = rectangle.Top; y < rectangle.Bottom; y++)
                {
                    ushort* sourcePixel = (ushort*)(source + y * sourceRowPitch) + rectangle.Left * 4;

                    for (int x = rectangle.Left; x < rectangle.Right; x++)
                    {
                        float red = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[0]));
                        float green = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[1]));
                        float blue = SanitizeLinear((float)BitConverter.UInt16BitsToHalf(sourcePixel[2]));
                        if (GetRec2020Max(red, green, blue) > headroomThreshold &&
                            ++headroomPixels >= requiredPixels)
                        {
                            return true;
                        }

                        sourcePixel += 4;
                    }
                }
            }

            return false;
        }

        private static void FillMaskRectangles(
            byte[] mask,
            int width,
            IReadOnlyList<Rectangle> rectangles,
            byte value)
        {
            foreach (Rectangle rectangle in rectangles)
            {
                for (int y = rectangle.Top; y < rectangle.Bottom; y++)
                {
                    mask.AsSpan(y * width + rectangle.Left, rectangle.Width).Fill(value);
                }
            }
        }

        private static List<ToneMapRegion> BuildToneMapRegions(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            bool[] seedTiles,
            int tilesX,
            int tilesY)
        {
            bool[] expandedTiles = new bool[seedTiles.Length];
            int seedTileCount = 0;

            for (int tileY = 0; tileY < tilesY; tileY++)
            {
                for (int tileX = 0; tileX < tilesX; tileX++)
                {
                    int tileIndex = tileY * tilesX + tileX;

                    if (!seedTiles[tileIndex])
                    {
                        continue;
                    }

                    seedTileCount++;

                    for (int linkedY = Math.Max(0, tileY - TileLinkRadius);
                        linkedY <= Math.Min(tilesY - 1, tileY + TileLinkRadius);
                        linkedY++)
                    {
                        for (int linkedX = Math.Max(0, tileX - TileLinkRadius);
                            linkedX <= Math.Min(tilesX - 1, tileX + TileLinkRadius);
                            linkedX++)
                        {
                            expandedTiles[linkedY * tilesX + linkedX] = true;
                        }
                    }
                }
            }

            if (seedTileCount == 0)
            {
                return new List<ToneMapRegion>();
            }

            int[] componentLabels = LabelTileComponents(expandedTiles, tilesX, tilesY, out int componentCount);
            TileComponent[] components = new TileComponent[componentCount + 1];

            for (int tileY = 0; tileY < tilesY; tileY++)
            {
                for (int tileX = 0; tileX < tilesX; tileX++)
                {
                    int tileIndex = tileY * tilesX + tileX;

                    if (!seedTiles[tileIndex])
                    {
                        continue;
                    }

                    int componentLabel = componentLabels[tileIndex];
                    components[componentLabel] ??= new TileComponent();
                    components[componentLabel].Include(tileX, tileY);
                }
            }

            List<ToneMapRegion> regions = new List<ToneMapRegion>();

            for (int componentIndex = 1; componentIndex < components.Length; componentIndex++)
            {
                TileComponent component = components[componentIndex];

                if (component == null || component.SeedTileCount < MinimumRegionSeedTiles)
                {
                    continue;
                }

                regions.Add(CreateToneMapRegion(
                    source,
                    sourceRowPitch,
                    width,
                    height,
                    paperWhiteScRgb,
                    component));
            }

            return regions;
        }

        private static ToneMapRegion CreateToneMapRegion(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            TileComponent component)
        {
            int initialLeft = component.Left * TileSize;
            int initialTop = component.Top * TileSize;
            int initialRight = Math.Min(width, (component.Right + 1) * TileSize);
            int initialBottom = Math.Min(height, (component.Bottom + 1) * TileSize);

            BoundaryResult top = FindHorizontalBoundary(
                source,
                sourceRowPitch,
                paperWhiteScRgb,
                Math.Max(0, initialLeft - BoundarySearchDistance),
                Math.Min(width, initialRight + BoundarySearchDistance),
                Math.Max(1, initialTop - BoundarySearchDistance),
                Math.Min(height - 1, initialTop + TileSize));
            BoundaryResult bottom = FindHorizontalBoundary(
                source,
                sourceRowPitch,
                paperWhiteScRgb,
                Math.Max(0, initialLeft - BoundarySearchDistance),
                Math.Min(width, initialRight + BoundarySearchDistance),
                Math.Max(1, initialBottom - TileSize),
                Math.Min(height - 1, initialBottom + BoundarySearchDistance));

            int verticalStart = top.IsConfident ? top.Position : initialTop;
            int verticalEnd = bottom.IsConfident ? bottom.Position : initialBottom;
            BoundaryResult left = FindVerticalBoundary(
                source,
                sourceRowPitch,
                paperWhiteScRgb,
                verticalStart,
                verticalEnd,
                Math.Max(1, initialLeft - BoundarySearchDistance),
                Math.Min(width - 1, initialLeft + TileSize));
            BoundaryResult right = FindVerticalBoundary(
                source,
                sourceRowPitch,
                paperWhiteScRgb,
                verticalStart,
                verticalEnd,
                Math.Max(1, initialRight - TileSize),
                Math.Min(width - 1, initialRight + BoundarySearchDistance));

            return ToneMapRegion.Create(
                width,
                height,
                initialLeft,
                initialTop,
                initialRight,
                initialBottom,
                left,
                top,
                right,
                bottom);
        }

        private static BoundaryResult FindHorizontalBoundary(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int left,
            int right,
            int searchStart,
            int searchEnd)
        {
            List<float> strengths = new List<float>(Math.Max(0, searchEnd - searchStart + 1));
            float strongest = 0f;
            int strongestPosition = searchStart;

            for (int y = searchStart; y <= searchEnd; y++)
            {
                float strength = GetHorizontalBoundaryStrength(
                    source,
                    sourceRowPitch,
                    paperWhiteScRgb,
                    left,
                    right,
                    y);
                strengths.Add(strength);

                if (strength > strongest)
                {
                    strongest = strength;
                    strongestPosition = y;
                }
            }

            return CreateBoundaryResult(strengths, strongest, strongestPosition);
        }

        private static BoundaryResult FindVerticalBoundary(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int top,
            int bottom,
            int searchStart,
            int searchEnd)
        {
            List<float> strengths = new List<float>(Math.Max(0, searchEnd - searchStart + 1));
            float strongest = 0f;
            int strongestPosition = searchStart;

            for (int x = searchStart; x <= searchEnd; x++)
            {
                float strength = GetVerticalBoundaryStrength(
                    source,
                    sourceRowPitch,
                    paperWhiteScRgb,
                    top,
                    bottom,
                    x);
                strengths.Add(strength);

                if (strength > strongest)
                {
                    strongest = strength;
                    strongestPosition = x;
                }
            }

            return CreateBoundaryResult(strengths, strongest, strongestPosition);
        }

        private static BoundaryResult CreateBoundaryResult(
            List<float> strengths,
            float strongest,
            int strongestPosition)
        {
            strengths.Sort();
            float median = strengths.Count > 0 ? strengths[strengths.Count / 2] : 0f;
            bool isConfident = strongest >= MinimumBoundaryStrength &&
                strongest >= Math.Max(median * BoundaryStrengthRatio, MinimumBoundaryStrength);

            return new BoundaryResult(strongestPosition, isConfident);
        }

        private static float GetHorizontalBoundaryStrength(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int left,
            int right,
            int y)
        {
            ushort* previous = (ushort*)(source + (y - 1) * sourceRowPitch) + left * 4;
            ushort* current = (ushort*)(source + y * sourceRowPitch) + left * 4;
            List<float> differences = new List<float>((right - left + 1) / 2);

            for (int x = left; x < right; x += 2)
            {
                differences.Add(GetPixelDifference(previous, current) / paperWhiteScRgb);
                previous += 8;
                current += 8;
            }

            return GetMedian(differences);
        }

        private static float GetVerticalBoundaryStrength(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int top,
            int bottom,
            int x)
        {
            List<float> differences = new List<float>((bottom - top + 1) / 2);

            for (int y = top; y < bottom; y += 2)
            {
                ushort* previous = (ushort*)(source + y * sourceRowPitch) + (x - 1) * 4;
                ushort* current = previous + 4;
                differences.Add(GetPixelDifference(previous, current) / paperWhiteScRgb);
            }

            return GetMedian(differences);
        }

        private static float GetMedian(List<float> values)
        {
            if (values.Count == 0)
            {
                return 0f;
            }

            values.Sort();
            return values[values.Count / 2];
        }

        private static float GetPixelDifference(ushort* first, ushort* second)
        {
            float red = Math.Abs(
                SanitizeLinear((float)BitConverter.UInt16BitsToHalf(first[0])) -
                SanitizeLinear((float)BitConverter.UInt16BitsToHalf(second[0])));
            float green = Math.Abs(
                SanitizeLinear((float)BitConverter.UInt16BitsToHalf(first[1])) -
                SanitizeLinear((float)BitConverter.UInt16BitsToHalf(second[1])));
            float blue = Math.Abs(
                SanitizeLinear((float)BitConverter.UInt16BitsToHalf(first[2])) -
                SanitizeLinear((float)BitConverter.UInt16BitsToHalf(second[2])));

            return (red + green + blue) / 3f;
        }

        private static int[] LabelTileComponents(
            bool[] tiles,
            int tilesX,
            int tilesY,
            out int componentCount)
        {
            int[] labels = new int[tiles.Length];
            Queue<int> pending = new Queue<int>();
            componentCount = 0;

            for (int tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
            {
                if (!tiles[tileIndex] || labels[tileIndex] != 0)
                {
                    continue;
                }

                componentCount++;
                int componentLabel = componentCount;
                labels[tileIndex] = componentLabel;
                pending.Enqueue(tileIndex);

                while (pending.Count > 0)
                {
                    int current = pending.Dequeue();
                    int currentX = current % tilesX;
                    int currentY = current / tilesX;

                    TryLabel(currentX - 1, currentY);
                    TryLabel(currentX + 1, currentY);
                    TryLabel(currentX, currentY - 1);
                    TryLabel(currentX, currentY + 1);

                    void TryLabel(int tileX, int tileY)
                    {
                        if (tileX < 0 || tileX >= tilesX || tileY < 0 || tileY >= tilesY)
                        {
                            return;
                        }

                        int neighbor = tileY * tilesX + tileX;

                        if (tiles[neighbor] && labels[neighbor] == 0)
                        {
                            labels[neighbor] = componentLabel;
                            pending.Enqueue(neighbor);
                        }
                    }
                }
            }

            return labels;
        }

        private static float GetRec2020Max(float red, float green, float blue)
        {
            // Linear Rec.709/scRGB to linear Rec.2020. RWTMO evaluates its
            // common gain from max RGB in Rec.2020 and applies that gain to
            // every channel, avoiding the hue shifts caused by channel clips.
            float rec2020Red = red * 0.6274039f + green * 0.3292830f + blue * 0.0433131f;
            float rec2020Green = red * 0.0690973f + green * 0.9195404f + blue * 0.0113623f;
            float rec2020Blue = red * 0.0163914f + green * 0.0880133f + blue * 0.8955953f;

            return Math.Max(rec2020Red, Math.Max(rec2020Green, rec2020Blue));
        }

        private sealed class HdrFrameAnalysis
        {
            public static HdrFrameAnalysis Empty { get; } = new HdrFrameAnalysis(new List<ToneMapRegion>());

            private readonly List<ToneMapRegion> regions;

            public HdrFrameAnalysis(List<ToneMapRegion> regions)
            {
                this.regions = regions;
            }

            public float GetToneMapAmount(int x, int y)
            {
                float amount = 0f;

                foreach (ToneMapRegion region in regions)
                {
                    amount = Math.Max(amount, region.GetAmount(x, y));

                    if (amount >= 1f)
                    {
                        break;
                    }
                }

                return amount;
            }
        }

        private sealed class VisibleWindowRegion
        {
            public IReadOnlyList<Rectangle> Rectangles { get; }
            public IReadOnlyList<Rectangle> ContentRectangles { get; }
            public HdrWindowRegion Region { get; }

            public VisibleWindowRegion(
                IReadOnlyList<Rectangle> rectangles,
                IReadOnlyList<Rectangle> contentRectangles,
                HdrWindowRegion region)
            {
                Rectangles = rectangles;
                ContentRectangles = contentRectangles;
                Region = region;
            }
        }

        private readonly record struct PromotedWindowIdentity(
            long WindowHandle,
            int ProcessId,
            long ProcessStartTimeUtcTicks);

        private readonly struct BoundaryResult
        {
            public int Position { get; }
            public bool IsConfident { get; }

            public BoundaryResult(int position, bool isConfident)
            {
                Position = position;
                IsConfident = isConfident;
            }
        }

        private readonly struct ToneMapRegion
        {
            private readonly int outerLeft;
            private readonly int outerTop;
            private readonly int outerRight;
            private readonly int outerBottom;
            private readonly int innerLeft;
            private readonly int innerTop;
            private readonly int innerRight;
            private readonly int innerBottom;

            private ToneMapRegion(
                int outerLeft,
                int outerTop,
                int outerRight,
                int outerBottom,
                int innerLeft,
                int innerTop,
                int innerRight,
                int innerBottom)
            {
                this.outerLeft = outerLeft;
                this.outerTop = outerTop;
                this.outerRight = outerRight;
                this.outerBottom = outerBottom;
                this.innerLeft = innerLeft;
                this.innerTop = innerTop;
                this.innerRight = innerRight;
                this.innerBottom = innerBottom;
            }

            public static ToneMapRegion Create(
                int width,
                int height,
                int initialLeft,
                int initialTop,
                int initialRight,
                int initialBottom,
                BoundaryResult left,
                BoundaryResult top,
                BoundaryResult right,
                BoundaryResult bottom)
            {
                bool useLeft = left.IsConfident;
                bool useTop = top.IsConfident;
                bool useRight = right.IsConfident;
                bool useBottom = bottom.IsConfident;
                int innerLeft = useLeft ? left.Position : initialLeft;
                int innerTop = useTop ? top.Position : initialTop;
                int innerRight = useRight ? right.Position : initialRight;
                int innerBottom = useBottom ? bottom.Position : initialBottom;

                if (innerLeft >= innerRight)
                {
                    useLeft = useRight = false;
                    innerLeft = initialLeft;
                    innerRight = initialRight;
                }

                if (innerTop >= innerBottom)
                {
                    useTop = useBottom = false;
                    innerTop = initialTop;
                    innerBottom = initialBottom;
                }

                // If an edge is visually clear, start at that content edge.
                // Otherwise feather outward from the seed bounds so uncertain
                // classification can never make another hard tile rectangle.
                int outerLeft = useLeft
                    ? innerLeft
                    : Math.Max(0, innerLeft - UncertainBoundaryFeather);
                int outerTop = useTop
                    ? innerTop
                    : Math.Max(0, innerTop - UncertainBoundaryFeather);
                int outerRight = useRight
                    ? innerRight
                    : Math.Min(width, innerRight + UncertainBoundaryFeather);
                int outerBottom = useBottom
                    ? innerBottom
                    : Math.Min(height, innerBottom + UncertainBoundaryFeather);

                return new ToneMapRegion(
                    outerLeft,
                    outerTop,
                    outerRight,
                    outerBottom,
                    innerLeft,
                    innerTop,
                    innerRight,
                    innerBottom);
            }

            public float GetAmount(int x, int y)
            {
                if (x < outerLeft || x >= outerRight || y < outerTop || y >= outerBottom)
                {
                    return 0f;
                }

                float amount = 1f;

                if (x < innerLeft)
                {
                    amount = Math.Min(amount, GetRamp(x, outerLeft, innerLeft));
                }
                else if (x >= innerRight)
                {
                    amount = Math.Min(amount, GetRamp(outerRight - 1 - x, 0, outerRight - innerRight));
                }

                if (y < innerTop)
                {
                    amount = Math.Min(amount, GetRamp(y, outerTop, innerTop));
                }
                else if (y >= innerBottom)
                {
                    amount = Math.Min(amount, GetRamp(outerBottom - 1 - y, 0, outerBottom - innerBottom));
                }

                return amount;
            }

            private static float GetRamp(int value, int start, int end)
            {
                if (end <= start)
                {
                    return 1f;
                }

                float position = Math.Clamp((float)(value - start) / (end - start), 0f, 1f);
                return position * position * (3f - 2f * position);
            }
        }

        private sealed class TileComponent
        {
            public int SeedTileCount { get; private set; }
            public int Left { get; private set; } = int.MaxValue;
            public int Top { get; private set; } = int.MaxValue;
            public int Right { get; private set; } = int.MinValue;
            public int Bottom { get; private set; } = int.MinValue;

            public void Include(int x, int y)
            {
                SeedTileCount++;
                Left = Math.Min(Left, x);
                Top = Math.Min(Top, y);
                Right = Math.Max(Right, x);
                Bottom = Math.Max(Bottom, y);
            }
        }

        internal readonly struct ToneMapInputAnalysis
        {
            public ToneMapParameters Parameters { get; }
            public byte[] ToneMapMask { get; }
            public ContentPeakMeasurement ContentPeak { get; }

            public ToneMapInputAnalysis(
                ToneMapParameters parameters,
                byte[] toneMapMask,
                ContentPeakMeasurement contentPeak)
            {
                Parameters = parameters;
                ToneMapMask = toneMapMask;
                ContentPeak = contentPeak;
            }
        }

        internal readonly struct ContentPeakMeasurement
        {
            public static ContentPeakMeasurement NotMeasured { get; } = new ContentPeakMeasurement(
                float.NaN,
                float.NaN,
                0,
                0,
                "Custom source peak override.");

            public float EffectivePeakNits { get; }
            public float ObservedMaximumNits { get; }
            public int SampleCount { get; }
            public int SampleStep { get; }
            public string Reason { get; }

            public ContentPeakMeasurement(
                float effectivePeakNits,
                float observedMaximumNits,
                int sampleCount,
                int sampleStep,
                string reason)
            {
                EffectivePeakNits = effectivePeakNits;
                ObservedMaximumNits = observedMaximumNits;
                SampleCount = sampleCount;
                SampleStep = sampleStep;
                Reason = reason;
            }
        }

        internal readonly struct ToneMapParameters
        {
            public float PaperWhiteNits { get; }
            public float PaperWhiteScRgb { get; }
            public float SourcePeakNits { get; }
            public float InputMaximum { get; }
            public float OutputWhite { get; }
            public float CurveXA { get; }
            public float CurveXB { get; }
            public float CurveYA { get; }
            public float CurveYB { get; }

            public ToneMapParameters(float paperWhiteNits, float paperWhiteScRgb, float sourcePeakNits)
            {
                float inputMaximum = sourcePeakNits / paperWhiteNits;
                ReferenceWhiteToneMapper mapper = new ReferenceWhiteToneMapper(inputMaximum);
                PaperWhiteNits = paperWhiteNits;
                PaperWhiteScRgb = paperWhiteScRgb;
                SourcePeakNits = sourcePeakNits;
                InputMaximum = mapper.inputMaximum;
                OutputWhite = mapper.outputWhite;
                CurveXA = mapper.xA;
                CurveXB = mapper.xB;
                CurveYA = mapper.yA;
                CurveYB = mapper.yB;
            }
        }

        private readonly struct ReferenceWhiteToneMapper
        {
            // SMPTE ST 2094-50 RWTMO curve construction. Skia uses the same
            // reference-white and quadratic Bezier control-point formulation:
            // https://skia.googlesource.com/skia/+/refs/heads/main/src/codec/SkHdrAgtm.cpp
            private const float Kappa = 0.65f;
            private static readonly float ReferenceHeadroom = MathF.Log2(1000f / ReferenceHdrWhiteNits);

            internal readonly float inputMaximum;
            internal readonly float outputWhite;
            internal readonly float xA;
            internal readonly float xB;
            internal readonly float yA;
            internal readonly float yB;

            public ReferenceWhiteToneMapper(float sourcePeakRelativeToWhite)
            {
                inputMaximum = Math.Clamp(sourcePeakRelativeToWhite, 1.0001f, 64f);
                float baselineHeadroom = MathF.Log2(inputMaximum);
                float compression = Math.Min(baselineHeadroom / ReferenceHeadroom, 1f);
                outputWhite = 1f - 0.5f * compression;

                float xMiddle = (1f - Kappa) + Kappa / outputWhite;
                float yMiddle = (1f - Kappa) * outputWhite + Kappa;
                xA = 1f - 2f * xMiddle + inputMaximum;
                yA = outputWhite - 2f * yMiddle + 1f;
                xB = 2f * xMiddle - 2f;
                yB = 2f * yMiddle - 2f * outputWhite;
            }

            public float GetGain(float input)
            {
                if (input <= 0f)
                {
                    return 0f;
                }

                if (input <= 1f)
                {
                    return outputWhite;
                }

                if (input >= inputMaximum)
                {
                    return 1f / input;
                }

                float position;

                if (Math.Abs(xA) < 0.00001f)
                {
                    position = (input - 1f) / xB;
                }
                else
                {
                    float discriminant = xB * xB - 4f * xA * (1f - input);
                    position = (-xB + MathF.Sqrt(Math.Max(discriminant, 0f))) / (2f * xA);
                }

                position = Math.Clamp(position, 0f, 1f);
                float output = outputWhite + position * (yB + position * yA);
                return output / input;
            }
        }

        private static byte ToSrgbByte(float linear)
        {
            int index = Math.Clamp((int)MathF.Round(linear * SrgbLutMaximum), 0, SrgbLutMaximum);
            return SrgbEncodingLut[index];
        }

        private static byte[] CreateSrgbEncodingLut()
        {
            byte[] result = new byte[SrgbLutMaximum + 1];

            for (int index = 0; index < result.Length; index++)
            {
                float linear = (float)index / SrgbLutMaximum;
                float srgb = linear <= 0.0031308f
                    ? linear * 12.92f
                    : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
                result[index] = ToByte(srgb);
            }

            return result;
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
