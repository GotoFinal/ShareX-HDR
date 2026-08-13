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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    internal static unsafe class HdrToSdrToneMapper
    {
        // In scRGB, 1.0 represents 80 nits. Windows exposes the actual SDR
        // white level for each active HDR monitor through DisplayConfig.
        private const float ScRgbNitsPerUnit = 80f;
        private const float HdrDetectionMargin = 1.02f;
        private const float AutomaticHdrPeakNits = 1000f;
        private const float ReferenceHdrWhiteNits = 203f;
        private const int TileSize = 16;
        private const int TileLinkRadius = 6;
        private const int TileInfluenceFullRadius = TileLinkRadius - 2;
        private const float TileTraversalBoundaryStrength = 0.06f;
        private const float TileBarrierStrengthRatio = 3f;
        private const int MinimumRegionSeedTiles = 4;
        private const int BoundarySearchDistance = TileSize * 4;
        private const int UncertainBoundaryFeather = TileSize * 2;
        private const float MinimumBoundaryStrength = 0.015f;
        private const float BoundaryStrengthRatio = 4f;
        private const int SrgbLutMaximum = 65535;
        private static readonly byte[] SrgbEncodingLut = CreateSrgbEncodingLut();

        public static Bitmap ToneMapRgba16Float(
            IntPtr source,
            int sourceRowPitch,
            int width,
            int height,
            HdrCaptureSettings settings,
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            bool preserveAlpha = false)
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
                        sdrWhiteNits,
                        displayMaxLuminanceNits,
                        preserveAlpha);
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
            float displayMaxLuminanceNits)
        {
            float paperWhiteNits = Math.Clamp(
                sdrWhiteNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumBrightnessNits);
            float sourcePeakNits = ResolveSourcePeakNits(
                configuredPeakNits,
                peakBrightnessMode,
                paperWhiteNits,
                displayMaxLuminanceNits);

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
            float paperWhiteScRgb)
        {
            if (source == IntPtr.Zero)
            {
                throw new ArgumentException(nameof(source));
            }

            if (sourceRowPitch < width * sizeof(ushort) * 4)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRowPitch));
            }

            HdrFrameAnalysis analysis = AnalyzeFrame(
                (byte*)source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb);
            byte[] mask = new byte[checked(width * height)];

            Parallel.For(0, height, y =>
            {
                int rowOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    mask[rowOffset + x] = ToByte(analysis.GetToneMapAmount(x, y));
                }
            });

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
            float sdrWhiteNits,
            float displayMaxLuminanceNits,
            bool preserveAlpha)
        {
            ToneMapParameters parameters = CreateToneMapParameters(
                configuredPeakNits,
                peakBrightnessMode,
                toneMappingMode,
                sdrWhiteNits,
                displayMaxLuminanceNits);
            float paperWhiteScRgb = parameters.PaperWhiteScRgb;
            bool useContentDetection = toneMappingMode == HdrToneMappingMode.ContentAware;
            HdrFrameAnalysis analysis = useContentDetection
                ? AnalyzeFrame(
                    source,
                    sourceRowPitch,
                    width,
                    height,
                    paperWhiteScRgb)
                : HdrFrameAnalysis.Empty;
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
                        useContentDetection ? analysis.GetToneMapAmount(x, y) : 1f);

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
            float displayMaxLuminanceNits)
        {
            float sourcePeakNits = Math.Clamp(
                configuredPeakNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumBrightnessNits);
            if (peakBrightnessMode == HdrPeakBrightnessMode.Custom)
            {
                return sourcePeakNits;
            }

            // DXGI MaxLuminance describes the display, not necessarily the
            // content values retained in the scRGB compositor surface. Using
            // it as the content maximum clipped every value above the panel
            // peak to identical SDR white. Automatic mode keeps the standard
            // 1000-nit mastering headroom while paper white remains calibrated
            // from DISPLAYCONFIG_SDR_WHITE_LEVEL metadata.
            return AutomaticHdrPeakNits;
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

            // Isolated specular highlights still need roll-off, but mapping
            // only those pixels avoids producing a visible tile-shaped patch.
            if (maxRgb > HdrDetectionMargin)
            {
                toneMapAmount = 1f;
            }

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

            List<ToneMapRegion> regions = BuildToneMapRegions(
                source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb,
                seedTiles,
                tilesX,
                tilesY);
            if (regions.Count == 0)
            {
                return HdrFrameAnalysis.Empty;
            }

            float[] tileSupport = BuildToneMapTileSupport(
                source,
                sourceRowPitch,
                width,
                height,
                paperWhiteScRgb,
                seedTiles,
                tilesX,
                tilesY);
            return new HdrFrameAnalysis(regions, tileSupport, tilesX, tilesY);
        }

        private static float[] BuildToneMapTileSupport(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            bool[] seedTiles,
            int tilesX,
            int tilesY)
        {
            bool[] barrierTiles = new bool[seedTiles.Length];
            nint sourceAddress = (nint)source;

            Parallel.For(0, tilesY, tileY =>
            {
                int top = tileY * TileSize;
                int bottom = Math.Min(height, top + TileSize);

                for (int tileX = 0; tileX < tilesX; tileX++)
                {
                    int left = tileX * TileSize;
                    int right = Math.Min(width, left + TileSize);
                    barrierTiles[tileY * tilesX + tileX] = HasStrongInternalTileBoundary(
                        (byte*)sourceAddress,
                        sourceRowPitch,
                        paperWhiteScRgb,
                        left,
                        top,
                        right,
                        bottom);
                }
            });

            int[] distances = new int[seedTiles.Length];
            Array.Fill(distances, -1);
            Queue<int> pending = new Queue<int>();

            for (int tileIndex = 0; tileIndex < seedTiles.Length; tileIndex++)
            {
                if (!seedTiles[tileIndex])
                {
                    continue;
                }

                distances[tileIndex] = 0;
                if (!barrierTiles[tileIndex])
                {
                    pending.Enqueue(tileIndex);
                }
            }

            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                int currentDistance = distances[current];
                if (currentDistance >= TileLinkRadius)
                {
                    continue;
                }

                int currentX = current % tilesX;
                int currentY = current / tilesX;
                TryExpand(currentX - 1, currentY);
                TryExpand(currentX + 1, currentY);
                TryExpand(currentX, currentY - 1);
                TryExpand(currentX, currentY + 1);

                void TryExpand(int tileX, int tileY)
                {
                    if (tileX < 0 || tileX >= tilesX || tileY < 0 || tileY >= tilesY)
                    {
                        return;
                    }

                    int neighbor = tileY * tilesX + tileX;
                    int nextDistance = currentDistance + 1;
                    if ((distances[neighbor] >= 0 && distances[neighbor] <= nextDistance) ||
                        !CanTraverseTileBoundary(
                            source,
                            sourceRowPitch,
                            width,
                            height,
                            paperWhiteScRgb,
                            currentX,
                            currentY,
                            tileX,
                            tileY))
                    {
                        return;
                    }

                    distances[neighbor] = nextDistance;

                    // A straight, high-contrast edge inside a tile is commonly an
                    // image/window boundary. Give that tile feathered influence,
                    // but do not let HDR support flood through it into an SDR gap
                    // or an occluding window whose edge is not tile-aligned.
                    if (!barrierTiles[neighbor])
                    {
                        pending.Enqueue(neighbor);
                    }
                }
            }

            float[] support = new float[distances.Length];
            int featherSteps = TileLinkRadius - TileInfluenceFullRadius + 1;
            for (int index = 0; index < distances.Length; index++)
            {
                int distance = distances[index];
                if (distance < 0)
                {
                    continue;
                }

                support[index] = distance <= TileInfluenceFullRadius
                    ? 1f
                    : (float)(TileLinkRadius + 1 - distance) / featherSteps;
            }

            return support;
        }

        private static bool CanTraverseTileBoundary(
            byte* source,
            int sourceRowPitch,
            int width,
            int height,
            float paperWhiteScRgb,
            int firstX,
            int firstY,
            int secondX,
            int secondY)
        {
            float strength;
            if (firstY != secondY)
            {
                int boundaryY = Math.Max(firstY, secondY) * TileSize;
                int left = firstX * TileSize;
                int right = Math.Min(width, left + TileSize);
                strength = GetHorizontalTileBoundaryStrength(
                    source, sourceRowPitch, paperWhiteScRgb, left, right, boundaryY);
            }
            else
            {
                int boundaryX = Math.Max(firstX, secondX) * TileSize;
                int top = firstY * TileSize;
                int bottom = Math.Min(height, top + TileSize);
                strength = GetVerticalTileBoundaryStrength(
                    source, sourceRowPitch, paperWhiteScRgb, top, bottom, boundaryX);
            }

            return strength < TileTraversalBoundaryStrength;
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

        private static bool HasStrongInternalTileBoundary(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int left,
            int top,
            int right,
            int bottom)
        {
            Span<float> strengths = stackalloc float[(TileSize - 1) * 2];
            int count = 0;

            for (int y = top + 1; y < bottom; y++)
            {
                strengths[count++] = GetHorizontalTileBoundaryStrength(
                    source, sourceRowPitch, paperWhiteScRgb, left, right, y);
            }

            for (int x = left + 1; x < right; x++)
            {
                strengths[count++] = GetVerticalTileBoundaryStrength(
                    source, sourceRowPitch, paperWhiteScRgb, top, bottom, x);
            }

            if (count == 0)
            {
                return false;
            }

            Span<float> populated = strengths.Slice(0, count);
            populated.Sort();
            float strongest = populated[^1];
            float median = populated[count / 2];
            return strongest >= TileTraversalBoundaryStrength &&
                strongest >= Math.Max(
                    median * TileBarrierStrengthRatio,
                    TileTraversalBoundaryStrength);
        }

        private static float GetHorizontalTileBoundaryStrength(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int left,
            int right,
            int y)
        {
            ushort* previous = (ushort*)(source + (y - 1) * sourceRowPitch) + left * 4;
            ushort* current = (ushort*)(source + y * sourceRowPitch) + left * 4;
            float total = 0f;
            int count = 0;

            for (int x = left; x < right; x += 2)
            {
                total += GetPixelDifference(previous, current) / paperWhiteScRgb;
                count++;
                previous += 8;
                current += 8;
            }

            return count > 0 ? total / count : 0f;
        }

        private static float GetVerticalTileBoundaryStrength(
            byte* source,
            int sourceRowPitch,
            float paperWhiteScRgb,
            int top,
            int bottom,
            int x)
        {
            float total = 0f;
            int count = 0;

            for (int y = top; y < bottom; y += 2)
            {
                ushort* previous = (ushort*)(source + y * sourceRowPitch) + (x - 1) * 4;
                ushort* current = previous + 4;
                total += GetPixelDifference(previous, current) / paperWhiteScRgb;
                count++;
            }

            return count > 0 ? total / count : 0f;
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
            private readonly float[] tileSupport;
            private readonly int tilesX;
            private readonly int tilesY;

            public HdrFrameAnalysis(
                List<ToneMapRegion> regions,
                float[] tileSupport = null,
                int tilesX = 0,
                int tilesY = 0)
            {
                this.regions = regions;
                this.tileSupport = tileSupport;
                this.tilesX = tilesX;
                this.tilesY = tilesY;
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

                return amount > 0f ? amount * GetTileSupport(x, y) : 0f;
            }

            private float GetTileSupport(int x, int y)
            {
                if (tileSupport == null || tileSupport.Length == 0 || tilesX <= 0 || tilesY <= 0)
                {
                    return 1f;
                }

                float gridX = (x + 0.5f) / TileSize - 0.5f;
                float gridY = (y + 0.5f) / TileSize - 0.5f;
                int left = (int)MathF.Floor(gridX);
                int top = (int)MathF.Floor(gridY);
                float horizontal = gridX - left;
                float vertical = gridY - top;
                float topLeft = SampleTileSupport(left, top);
                float topRight = SampleTileSupport(left + 1, top);
                float bottomLeft = SampleTileSupport(left, top + 1);
                float bottomRight = SampleTileSupport(left + 1, top + 1);
                float topValue = topLeft + (topRight - topLeft) * horizontal;
                float bottomValue = bottomLeft + (bottomRight - bottomLeft) * horizontal;
                return Math.Clamp(topValue + (bottomValue - topValue) * vertical, 0f, 1f);
            }

            private float SampleTileSupport(int x, int y)
            {
                x = Math.Clamp(x, 0, tilesX - 1);
                y = Math.Clamp(y, 0, tilesY - 1);
                return tileSupport[y * tilesX + x];
            }
        }

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
