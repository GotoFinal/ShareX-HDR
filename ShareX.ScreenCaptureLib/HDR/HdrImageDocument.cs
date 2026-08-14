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

using ShareX.HelpersLib;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// CPU-owned premultiplied RGBA16F pixels in linear scRGB. One scRGB unit is 80 nits.
    /// </summary>
    public sealed class HdrRgba16FloatBuffer : IDisposable
    {
        public const int BytesPerPixel = 8;
        public const float ReferenceWhiteNits = 80f;

        private byte[] pixels;

        public int Width { get; }
        public int Height { get; }
        public int RowBytes { get; }
        public bool IsDisposed => pixels == null;

        public ReadOnlyMemory<byte> PixelBytes
        {
            get
            {
                ObjectDisposedException.ThrowIf(pixels == null, this);
                return pixels;
            }
        }

        public HdrRgba16FloatBuffer(int width, int height, int rowBytes = 0)
        {
            ValidateDimensions(width, height);

            int minimumRowBytes = checked(width * BytesPerPixel);
            RowBytes = rowBytes == 0 ? minimumRowBytes : rowBytes;

            if (RowBytes < minimumRowBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(rowBytes));
            }

            Width = width;
            Height = height;
            pixels = new byte[checked(RowBytes * height)];
        }

        public static HdrRgba16FloatBuffer CopyFrom(
            ReadOnlySpan<byte> source,
            int sourceRowBytes,
            int width,
            int height)
        {
            ValidateDimensions(width, height);
            int activeRowBytes = checked(width * BytesPerPixel);
            ValidateSource(source.Length, sourceRowBytes, activeRowBytes, height);

            var result = new HdrRgba16FloatBuffer(width, height);

            for (int y = 0; y < height; y++)
            {
                source.Slice(y * sourceRowBytes, activeRowBytes)
                    .CopyTo(result.pixels.AsSpan(y * result.RowBytes, activeRowBytes));
            }

            result.SanitizeNonFinite();
            return result;
        }

        internal static unsafe HdrRgba16FloatBuffer CopyFrom(
            IntPtr source,
            int sourceRowBytes,
            int width,
            int height)
        {
            if (source == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(source));
            }

            ValidateDimensions(width, height);
            var result = new HdrRgba16FloatBuffer(width, height);
            result.CopyFrom(
                source,
                sourceRowBytes,
                width,
                height,
                Point.Empty);
            return result;
        }

        internal unsafe void CopyFrom(
            IntPtr source,
            int sourceRowBytes,
            int width,
            int height,
            Point destinationLocation,
            float rgbScale = 1f)
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            if (source == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(source));
            }

            ValidateDimensions(width, height);
            int activeRowBytes = checked(width * BytesPerPixel);
            if (sourceRowBytes < activeRowBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRowBytes));
            }

            Rectangle destinationRectangle = new Rectangle(
                destinationLocation,
                new Size(width, height));
            ValidateRectangle(destinationRectangle, Width, Height, nameof(destinationLocation));

            fixed (byte* destinationBase = pixels)
            {
                if (Math.Abs(rgbScale - 1f) > 0.0001f && height >= 64)
                {
                    nint sourceAddress = (nint)source;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, height, y =>
                    {
                        byte* destinationRow = (byte*)destinationAddress +
                            (destinationLocation.Y + y) * RowBytes +
                            destinationLocation.X * BytesPerPixel;
                        CopySanitizedRow(
                            (byte*)sourceAddress + y * sourceRowBytes,
                            destinationRow,
                            width,
                            rgbScale);
                    });
                    return;
                }

                for (int y = 0; y < height; y++)
                {
                    byte* destinationRow = destinationBase +
                        (destinationLocation.Y + y) * RowBytes +
                        destinationLocation.X * BytesPerPixel;
                    CopySanitizedRow(
                        (byte*)source + y * sourceRowBytes,
                        destinationRow,
                        width,
                        rgbScale);
                }
            }
        }

        public ReadOnlySpan<byte> GetRowSpan(int y)
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
            return pixels.AsSpan(y * RowBytes, Width * BytesPerPixel);
        }

        public HdrRgba16FloatBuffer Clone()
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            var clone = new HdrRgba16FloatBuffer(Width, Height, RowBytes);
            pixels.CopyTo(clone.pixels, 0);
            return clone;
        }

        internal Span<byte> GetWritablePixelSpan()
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            return pixels;
        }

        internal Span<byte> GetWritableRowSpan(int y)
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
            return pixels.AsSpan(y * RowBytes, Width * BytesPerPixel);
        }

        internal void CopyScaledRegionFrom(
            HdrRgba16FloatBuffer source,
            Rectangle sourceRectangle,
            Rectangle destinationRectangle)
        {
            ArgumentNullException.ThrowIfNull(source);
            ObjectDisposedException.ThrowIf(pixels == null, this);

            ValidateRectangle(sourceRectangle, source.Width, source.Height, nameof(sourceRectangle));
            ValidateRectangle(destinationRectangle, Width, Height, nameof(destinationRectangle));

            if (sourceRectangle.Size == destinationRectangle.Size)
            {
                int copyBytes = checked(sourceRectangle.Width * BytesPerPixel);

                for (int y = 0; y < destinationRectangle.Height; y++)
                {
                    source.GetRowSpan(sourceRectangle.Y + y)
                        .Slice(sourceRectangle.X * BytesPerPixel, copyBytes)
                        .CopyTo(pixels.AsSpan(
                            (destinationRectangle.Y + y) * RowBytes + destinationRectangle.X * BytesPerPixel,
                            copyBytes));
                }

                return;
            }

            for (int y = 0; y < destinationRectangle.Height; y++)
            {
                int sourceY = sourceRectangle.Y +
                    (int)((long)y * sourceRectangle.Height / destinationRectangle.Height);
                ReadOnlySpan<byte> sourceRow = source.GetRowSpan(sourceY);
                Span<byte> destinationRow = pixels.AsSpan(
                    (destinationRectangle.Y + y) * RowBytes,
                    Width * BytesPerPixel);

                for (int x = 0; x < destinationRectangle.Width; x++)
                {
                    int sourceX = sourceRectangle.X +
                        (int)((long)x * sourceRectangle.Width / destinationRectangle.Width);
                    sourceRow.Slice(sourceX * BytesPerPixel, BytesPerPixel)
                        .CopyTo(destinationRow.Slice(
                            (destinationRectangle.X + x) * BytesPerPixel,
                            BytesPerPixel));
                }
            }
        }

        private unsafe void SanitizeNonFinite()
        {
            fixed (byte* pixelBase = pixels)
            {
                int valuesPerRow = checked(Width * 4);
                for (int y = 0; y < Height; y++)
                {
                    SanitizeNonFinite(pixelBase + y * RowBytes, valuesPerRow);
                }
            }
        }

        private static unsafe void SanitizeNonFinite(byte* values, int valueCount)
        {
            ushort* halfValues = (ushort*)values;
            int valueIndex = 0;

            if (Avx2.IsSupported)
            {
                for (; valueIndex <= valueCount - Vector256<ushort>.Count;
                    valueIndex += Vector256<ushort>.Count)
                {
                    Vector256<ushort> source = Unsafe.ReadUnaligned<Vector256<ushort>>(
                        halfValues + valueIndex);
                    Unsafe.WriteUnaligned(
                        halfValues + valueIndex,
                        SanitizeHalfVector(source));
                }
            }

            for (; valueIndex < valueCount; valueIndex++)
            {
                halfValues[valueIndex] = SanitizeHalfBits(halfValues[valueIndex]);
            }
        }

        private static unsafe void CopySanitizedRow(
            byte* source,
            byte* destination,
            int pixelCount,
            float rgbScale)
        {
            int valueCount = checked(pixelCount * 4);
            ushort* sourceValues = (ushort*)source;
            ushort* destinationValues = (ushort*)destination;
            bool scaleRgb = Math.Abs(rgbScale - 1f) > 0.0001f;
            int valueIndex = 0;

            if (Avx2.IsSupported)
            {
                Vector256<float> scale = Vector256.Create(
                    rgbScale,
                    rgbScale,
                    rgbScale,
                    1f,
                    rgbScale,
                    rgbScale,
                    rgbScale,
                    1f);
                float* scaledValues = stackalloc float[Vector256<ushort>.Count];

                for (; valueIndex <= valueCount - Vector256<ushort>.Count;
                    valueIndex += Vector256<ushort>.Count)
                {
                    Vector256<ushort> sanitized = SanitizeHalfVector(
                        Unsafe.ReadUnaligned<Vector256<ushort>>(sourceValues + valueIndex));

                    if (!scaleRgb)
                    {
                        Unsafe.WriteUnaligned(destinationValues + valueIndex, sanitized);
                        continue;
                    }

                    Vector256<float> lower = Avx.Multiply(
                        ConvertFiniteHalf8ToSingle(sanitized.GetLower()),
                        scale);
                    Vector256<float> upper = Avx.Multiply(
                        ConvertFiniteHalf8ToSingle(sanitized.GetUpper()),
                        scale);
                    Avx.Store(scaledValues, lower);
                    Avx.Store(scaledValues + Vector256<float>.Count, upper);

                    for (int lane = 0; lane < Vector256<ushort>.Count; lane++)
                    {
                        destinationValues[valueIndex + lane] =
                            BitConverter.HalfToUInt16Bits((Half)ClampFiniteHalf(scaledValues[lane]));
                    }
                }
            }

            for (; valueIndex < valueCount; valueIndex++)
            {
                ushort sanitized = SanitizeHalfBits(sourceValues[valueIndex]);
                if (!scaleRgb || valueIndex % 4 == 3)
                {
                    destinationValues[valueIndex] = sanitized;
                    continue;
                }

                float value = (float)BitConverter.UInt16BitsToHalf(sanitized);
                destinationValues[valueIndex] = BitConverter.HalfToUInt16Bits(
                    (Half)ClampFiniteHalf(value * rgbScale));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort SanitizeHalfBits(ushort bits)
        {
            const ushort exponentMask = 0x7C00;
            const ushort mantissaMask = 0x03FF;
            const ushort signMask = 0x8000;
            const ushort maximumFinite = 0x7BFF;

            if ((bits & exponentMask) != exponentMask)
            {
                return bits;
            }

            return (bits & mantissaMask) != 0
                ? (ushort)0
                : (ushort)((bits & signMask) | maximumFinite);
        }

        private static Vector256<ushort> SanitizeHalfVector(Vector256<ushort> values)
        {
            Vector256<ushort> exponentMask = Vector256.Create((ushort)0x7C00);
            Vector256<ushort> mantissaMask = Vector256.Create((ushort)0x03FF);
            Vector256<ushort> signMask = Vector256.Create((ushort)0x8000);
            Vector256<ushort> maximumFinite = Vector256.Create((ushort)0x7BFF);
            Vector256<ushort> nonFinite = Avx2.CompareEqual(
                Avx2.And(values, exponentMask),
                exponentMask);
            Vector256<ushort> nan = Avx2.CompareGreaterThan(
                Avx2.And(values, mantissaMask).AsInt16(),
                Vector256<short>.Zero).AsUInt16();
            Vector256<ushort> infinityReplacement = Avx2.Or(
                Avx2.And(values, signMask),
                maximumFinite);
            Vector256<ushort> replacement = Avx2.BlendVariable(
                infinityReplacement,
                Vector256<ushort>.Zero,
                nan);
            return Avx2.BlendVariable(values, replacement, nonFinite);
        }

        private static Vector256<float> ConvertFiniteHalf8ToSingle(Vector128<ushort> values)
        {
            Vector256<uint> expanded = Avx2.ConvertToVector256Int32(values).AsUInt32();
            Vector256<uint> sign = Avx2.ShiftLeftLogical(
                Avx2.And(expanded, Vector256.Create(0x8000u)),
                16);
            Vector256<uint> magnitude = Avx2.And(expanded, Vector256.Create(0x7FFFu));
            Vector256<uint> exponentIsZero = Avx2.CompareEqual(
                Avx2.And(magnitude, Vector256.Create(0x7C00u)),
                Vector256<uint>.Zero);
            Vector256<uint> normalBits = Avx2.Add(
                Avx2.ShiftLeftLogical(magnitude, 13),
                Vector256.Create(0x38000000u));
            Vector256<float> subnormal = Avx.Subtract(
                Avx2.Add(normalBits, Vector256.Create(0x00800000u)).AsSingle(),
                Vector256.Create(BitConverter.Int32BitsToSingle(0x38800000)));
            Vector256<float> magnitudeValues = Avx.BlendVariable(
                normalBits.AsSingle(),
                subnormal,
                exponentIsZero.AsSingle());
            return Avx2.Or(magnitudeValues.AsUInt32(), sign).AsSingle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ClampFiniteHalf(float value)
        {
            return float.IsFinite(value)
                ? Math.Clamp(value, -(float)Half.MaxValue, (float)Half.MaxValue)
                : 0f;
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        }

        private static void ValidateSource(int length, int rowBytes, int activeRowBytes, int height)
        {
            if (rowBytes < activeRowBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(rowBytes));
            }

            int requiredLength = checked((height - 1) * rowBytes + activeRowBytes);
            if (length < requiredLength)
            {
                throw new ArgumentException(null, nameof(length));
            }
        }

        private static void ValidateRectangle(Rectangle rectangle, int width, int height, string paramName)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0 || rectangle.X < 0 || rectangle.Y < 0 ||
                rectangle.Right > width || rectangle.Bottom > height)
            {
                throw new ArgumentOutOfRangeException(paramName);
            }
        }

        public void Dispose()
        {
            pixels = null;
        }
    }

    public sealed class HdrCaptureSourceSegment
    {
        public Rectangle DestinationRectangle { get; }
        public string DisplayDeviceName { get; }
        public bool WasHdrActive { get; }
        public float SdrWhiteNits { get; }
        public float DisplayPeakNits { get; }

        internal HdrCaptureSourceSegment(
            Rectangle destinationRectangle,
            string displayDeviceName,
            bool wasHdrActive,
            float sdrWhiteNits,
            float displayPeakNits)
        {
            DestinationRectangle = destinationRectangle;
            DisplayDeviceName = displayDeviceName;
            WasHdrActive = wasHdrActive;
            SdrWhiteNits = sdrWhiteNits;
            DisplayPeakNits = displayPeakNits;
        }
    }

    /// <summary>
    /// First native-HDR document boundary. The master remains linear scRGB FP16;
    /// SDR previews and encoded files are derivatives owned by later stages.
    /// </summary>
    public sealed class HdrImageDocument : IDisposable
    {
        public const float MaximumSupportedBlurSigma = 200f;
        public const int MaximumSupportedBoxBlurRange = 199;
        public const float MaximumSupportedMagnification = 10f;
        private const int MinimumPreviewAnalysisCacheEntries = 2;

        private HdrRgba16FloatBuffer masterPixels;
        private readonly List<HdrCaptureSourceSegment> sourceSegments;
        private readonly List<HdrWindowRegion> windowRegions;
        private readonly object previewAnalysisSync = new object();
        private readonly Dictionary<PreviewAnalysisCacheKey, HdrToSdrToneMapper.ToneMapInputAnalysis>
            previewAnalysisCache = new Dictionary<PreviewAnalysisCacheKey, HdrToSdrToneMapper.ToneMapInputAnalysis>();
        private int previewAnalysisCacheRevision = -1;
        private int previewAnalysisGeneration;
        private int previewAnalysisCacheGeneration = -1;

        private enum OrthogonalTransform
        {
            Rotate90Clockwise,
            Rotate90CounterClockwise,
            Rotate180,
            FlipHorizontal,
            FlipVertical
        }

        public Guid DocumentId { get; } = Guid.NewGuid();
        public Rectangle RequestedBounds { get; }
        public DateTimeOffset CaptureTimestamp { get; private set; }
        public int Revision { get; private set; }
        public HdrRgba16FloatBuffer MasterPixels => masterPixels ??
            throw new ObjectDisposedException(nameof(HdrImageDocument));
        public IReadOnlyList<HdrCaptureSourceSegment> SourceSegments => sourceSegments;
        internal IReadOnlyList<HdrWindowRegion> WindowRegions => windowRegions;

        internal HdrImageDocument(
            Rectangle requestedBounds,
            HdrRgba16FloatBuffer masterPixels,
            IEnumerable<HdrCaptureSourceSegment> sourceSegments,
            IEnumerable<HdrWindowRegion> windowRegions = null)
        {
            RequestedBounds = requestedBounds;
            this.masterPixels = masterPixels ?? throw new ArgumentNullException(nameof(masterPixels));
            this.sourceSegments = new List<HdrCaptureSourceSegment>(
                sourceSegments ?? throw new ArgumentNullException(nameof(sourceSegments)));
            this.windowRegions = windowRegions != null
                ? new List<HdrWindowRegion>(windowRegions)
                : new List<HdrWindowRegion>();
            CaptureTimestamp = DateTimeOffset.UtcNow;
        }

        public HdrImageDocument Clone()
        {
            var clone = new HdrImageDocument(
                RequestedBounds,
                MasterPixels.Clone(),
                sourceSegments,
                windowRegions);
            clone.CaptureTimestamp = CaptureTimestamp;
            clone.Revision = Revision;
            return clone;
        }

        /// <summary>
        /// Normalizes visible SDR-display pixels into the paper-white range used
        /// by HDR displays in the same document. Source segments are processed
        /// back-to-front so a foreground HDR/game segment is never scaled merely
        /// because it overlaps an SDR desktop segment below it.
        /// </summary>
        internal void NormalizeMixedMonitorBrightness(HdrCaptureSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (settings.MixedMonitorBrightnessMode == HdrMixedMonitorBrightnessMode.Preserve ||
                sourceSegments.Count == 0)
            {
                return;
            }

            var hdrSegments = sourceSegments
                .FindAll(segment => segment.WasHdrActive &&
                    float.IsFinite(segment.SdrWhiteNits) &&
                    segment.SdrWhiteNits > 0f);
            if (settings.MixedMonitorBrightnessMode == HdrMixedMonitorBrightnessMode.MatchHdrDisplay &&
                hdrSegments.Count == 0)
            {
                return;
            }

            if (!sourceSegments.Exists(segment => !segment.WasHdrActive))
            {
                return;
            }

            // Normalization can update pixels without changing the document's
            // editor revision, so invalidate preview analysis explicitly.
            InvalidatePreviewAnalysisCache();

            if (!SourceSegmentsOverlap())
            {
                NormalizeNonOverlappingSdrSegments(settings, hdrSegments);
                return;
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            byte[] coveredPixels = new byte[checked(pixels.Width * pixels.Height)];

            for (int segmentIndex = sourceSegments.Count - 1; segmentIndex >= 0; segmentIndex--)
            {
                HdrCaptureSourceSegment segment = sourceSegments[segmentIndex];
                Rectangle rectangle = Rectangle.Intersect(
                    segment.DestinationRectangle,
                    new Rectangle(0, 0, pixels.Width, pixels.Height));
                if (rectangle.Width <= 0 || rectangle.Height <= 0)
                {
                    continue;
                }

                float targetWhiteNits = segment.SdrWhiteNits;
                if (!segment.WasHdrActive)
                {
                    targetWhiteNits = settings.MixedMonitorBrightnessMode ==
                        HdrMixedMonitorBrightnessMode.Custom
                        ? settings.MixedMonitorCustomSdrWhiteNits
                        : FindNearestHdrWhiteNits(segment.DestinationRectangle, hdrSegments);
                }

                float sourceWhiteNits = float.IsFinite(segment.SdrWhiteNits) &&
                    segment.SdrWhiteNits > 0f
                    ? segment.SdrWhiteNits
                    : HdrRgba16FloatBuffer.ReferenceWhiteNits;
                float scale = !segment.WasHdrActive
                    ? targetWhiteNits / sourceWhiteNits
                    : 1f;
                bool scaleRgb = Math.Abs(scale - 1f) > 0.0001f;

                Parallel.For(rectangle.Top, rectangle.Bottom, y =>
                {
                    Span<byte> row = pixels.GetWritableRowSpan(y);
                    int maskOffset = y * pixels.Width;

                    for (int x = rectangle.Left; x < rectangle.Right; x++)
                    {
                        int maskIndex = maskOffset + x;
                        if (coveredPixels[maskIndex] != 0)
                        {
                            continue;
                        }

                        coveredPixels[maskIndex] = 1;
                        if (scaleRgb)
                        {
                            int pixelOffset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                            WriteHalf(row, pixelOffset, ScaleFiniteHalf(ReadHalf(row, pixelOffset), scale));
                            WriteHalf(row, pixelOffset + 2, ScaleFiniteHalf(ReadHalf(row, pixelOffset + 2), scale));
                            WriteHalf(row, pixelOffset + 4, ScaleFiniteHalf(ReadHalf(row, pixelOffset + 4), scale));
                        }
                    }
                });

                if (!segment.WasHdrActive)
                {
                    sourceSegments[segmentIndex] = new HdrCaptureSourceSegment(
                        segment.DestinationRectangle,
                        segment.DisplayDeviceName,
                        false,
                        targetWhiteNits,
                        segment.DisplayPeakNits);
                    DebugHelper.WriteLine(
                        $"HDR mixed-monitor normalization | display={segment.DisplayDeviceName} " +
                        $"sourceWhite={sourceWhiteNits:F1}nits targetWhite={targetWhiteNits:F1}nits " +
                        $"scale={scale:F3} bounds={segment.DestinationRectangle}");
                }
            }
        }

        private bool SourceSegmentsOverlap()
        {
            for (int firstIndex = 0; firstIndex < sourceSegments.Count; firstIndex++)
            {
                Rectangle first = sourceSegments[firstIndex].DestinationRectangle;
                for (int secondIndex = firstIndex + 1; secondIndex < sourceSegments.Count; secondIndex++)
                {
                    if (first.IntersectsWith(sourceSegments[secondIndex].DestinationRectangle))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void NormalizeNonOverlappingSdrSegments(
            HdrCaptureSettings settings,
            IReadOnlyList<HdrCaptureSourceSegment> hdrSegments)
        {
            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle pixelBounds = new Rectangle(0, 0, pixels.Width, pixels.Height);

            for (int segmentIndex = 0; segmentIndex < sourceSegments.Count; segmentIndex++)
            {
                HdrCaptureSourceSegment segment = sourceSegments[segmentIndex];
                if (segment.WasHdrActive)
                {
                    continue;
                }

                Rectangle rectangle = Rectangle.Intersect(segment.DestinationRectangle, pixelBounds);
                if (rectangle.Width <= 0 || rectangle.Height <= 0)
                {
                    continue;
                }

                float targetWhiteNits = settings.MixedMonitorBrightnessMode ==
                    HdrMixedMonitorBrightnessMode.Custom
                    ? settings.MixedMonitorCustomSdrWhiteNits
                    : FindNearestHdrWhiteNits(segment.DestinationRectangle, hdrSegments);
                float sourceWhiteNits = float.IsFinite(segment.SdrWhiteNits) &&
                    segment.SdrWhiteNits > 0f
                    ? segment.SdrWhiteNits
                    : HdrRgba16FloatBuffer.ReferenceWhiteNits;
                float scale = targetWhiteNits / sourceWhiteNits;

                if (Math.Abs(scale - 1f) > 0.0001f)
                {
                    Parallel.For(rectangle.Top, rectangle.Bottom, y =>
                    {
                        Span<byte> row = pixels.GetWritableRowSpan(y);
                        for (int x = rectangle.Left; x < rectangle.Right; x++)
                        {
                            int pixelOffset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                            WriteHalf(row, pixelOffset, ScaleFiniteHalf(ReadHalf(row, pixelOffset), scale));
                            WriteHalf(row, pixelOffset + 2, ScaleFiniteHalf(ReadHalf(row, pixelOffset + 2), scale));
                            WriteHalf(row, pixelOffset + 4, ScaleFiniteHalf(ReadHalf(row, pixelOffset + 4), scale));
                        }
                    });
                }

                sourceSegments[segmentIndex] = new HdrCaptureSourceSegment(
                    segment.DestinationRectangle,
                    segment.DisplayDeviceName,
                    false,
                    targetWhiteNits,
                    segment.DisplayPeakNits);
                DebugHelper.WriteLine(
                    $"HDR mixed-monitor normalization | display={segment.DisplayDeviceName} " +
                    $"sourceWhite={sourceWhiteNits:F1}nits targetWhite={targetWhiteNits:F1}nits " +
                    $"scale={scale:F3} bounds={segment.DestinationRectangle} fastPath=True");
            }
        }

        private static float FindNearestHdrWhiteNits(
            Rectangle sourceRectangle,
            IReadOnlyList<HdrCaptureSourceSegment> hdrSegments)
        {
            HdrCaptureSourceSegment nearest = hdrSegments[0];
            long nearestDistance = GetRectangleDistanceSquared(
                sourceRectangle,
                nearest.DestinationRectangle);

            for (int index = 1; index < hdrSegments.Count; index++)
            {
                HdrCaptureSourceSegment candidate = hdrSegments[index];
                long distance = GetRectangleDistanceSquared(
                    sourceRectangle,
                    candidate.DestinationRectangle);
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            return Math.Clamp(
                nearest.SdrWhiteNits,
                HdrCaptureSettings.MinimumBrightnessNits,
                HdrCaptureSettings.MaximumPaperWhiteNits);
        }

        private static long GetRectangleDistanceSquared(Rectangle first, Rectangle second)
        {
            long horizontal = first.Right < second.Left
                ? second.Left - first.Right
                : second.Right < first.Left
                    ? first.Left - second.Right
                    : 0;
            long vertical = first.Bottom < second.Top
                ? second.Top - first.Bottom
                : second.Bottom < first.Top
                    ? first.Top - second.Bottom
                    : 0;
            return horizontal * horizontal + vertical * vertical;
        }

        private static float ScaleFiniteHalf(float value, float scale)
        {
            float result = value * scale;
            return float.IsFinite(result)
                ? Math.Clamp(result, -(float)Half.MaxValue, (float)Half.MaxValue)
                : 0f;
        }

        internal void CaptureWindowRegions(HdrCaptureSettings settings = null)
        {
            windowRegions.Clear();
            windowRegions.AddRange(HdrWindowRegion.Capture(RequestedBounds, settings));
            InvalidatePreviewAnalysisCache();
        }

        public HdrImageDocument CropToScreenRectangle(Rectangle screenRectangle)
        {
            Rectangle croppedBounds = Rectangle.Intersect(RequestedBounds, screenRectangle);

            if (croppedBounds.Width <= 0 || croppedBounds.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(screenRectangle),
                    "The crop rectangle must intersect the HDR document bounds.");
            }

            Rectangle sourceRectangle = new Rectangle(
                croppedBounds.X - RequestedBounds.X,
                croppedBounds.Y - RequestedBounds.Y,
                croppedBounds.Width,
                croppedBounds.Height);
            var croppedPixels = new HdrRgba16FloatBuffer(croppedBounds.Width, croppedBounds.Height);

            try
            {
                croppedPixels.CopyScaledRegionFrom(
                    MasterPixels,
                    sourceRectangle,
                    new Rectangle(Point.Empty, croppedBounds.Size));

                var croppedSegments = new List<HdrCaptureSourceSegment>();

                foreach (HdrCaptureSourceSegment segment in sourceSegments)
                {
                    Rectangle segmentIntersection = Rectangle.Intersect(
                        segment.DestinationRectangle,
                        sourceRectangle);

                    if (segmentIntersection.Width > 0 && segmentIntersection.Height > 0)
                    {
                        segmentIntersection.Offset(-sourceRectangle.X, -sourceRectangle.Y);
                        croppedSegments.Add(new HdrCaptureSourceSegment(
                            segmentIntersection,
                            segment.DisplayDeviceName,
                            segment.WasHdrActive,
                            segment.SdrWhiteNits,
                            segment.DisplayPeakNits));
                    }
                }

                List<HdrWindowRegion> croppedWindowRegions = CropWindowRegions(sourceRectangle);
                var croppedDocument = new HdrImageDocument(
                    croppedBounds,
                    croppedPixels,
                    croppedSegments,
                    croppedWindowRegions);
                croppedDocument.CaptureTimestamp = CaptureTimestamp;
                croppedDocument.Revision = checked(Revision + 1);
                return croppedDocument;
            }
            catch
            {
                croppedPixels.Dispose();
                throw;
            }
        }

        private List<HdrWindowRegion> CropWindowRegions(Rectangle sourceRectangle)
        {
            var result = new List<HdrWindowRegion>();

            foreach (HdrWindowRegion windowRegion in windowRegions)
            {
                Rectangle intersection = Rectangle.Intersect(
                    windowRegion.Bounds,
                    sourceRectangle);
                if (intersection.Width <= 0 || intersection.Height <= 0)
                {
                    continue;
                }

                intersection.Offset(-sourceRectangle.X, -sourceRectangle.Y);
                Rectangle contentIntersection = Rectangle.Intersect(
                    windowRegion.ContentBounds,
                    sourceRectangle);
                if (contentIntersection.Width > 0 && contentIntersection.Height > 0)
                {
                    contentIntersection.Offset(-sourceRectangle.X, -sourceRectangle.Y);
                }
                else
                {
                    contentIntersection = Rectangle.Empty;
                }
                result.Add(windowRegion.WithBounds(intersection, contentIntersection));
            }

            return result;
        }

        public HdrImageDocument CropToLocalRectangle(Rectangle localRectangle)
        {
            Rectangle documentRectangle = new Rectangle(
                Point.Empty, new Size(MasterPixels.Width, MasterPixels.Height));

            if (localRectangle.Width <= 0 || localRectangle.Height <= 0 ||
                !documentRectangle.Contains(localRectangle))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localRectangle),
                    "The local crop rectangle must be fully contained by the HDR document.");
            }

            return CropToScreenRectangle(new Rectangle(
                RequestedBounds.X + localRectangle.X,
                RequestedBounds.Y + localRectangle.Y,
                localRectangle.Width,
                localRectangle.Height));
        }

        public HdrImageDocument Rotate90Clockwise() =>
            TransformOrthogonal(OrthogonalTransform.Rotate90Clockwise);

        public HdrImageDocument Rotate90CounterClockwise() =>
            TransformOrthogonal(OrthogonalTransform.Rotate90CounterClockwise);

        public HdrImageDocument Rotate180() =>
            TransformOrthogonal(OrthogonalTransform.Rotate180);

        public HdrImageDocument FlipHorizontal() =>
            TransformOrthogonal(OrthogonalTransform.FlipHorizontal);

        public HdrImageDocument FlipVertical() =>
            TransformOrthogonal(OrthogonalTransform.FlipVertical);

        public HdrImageDocument CutOutVertical(int x, int width)
        {
            HdrRgba16FloatBuffer source = MasterPixels;
            if (x < 0 || width <= 0 || x > source.Width - width || width == source.Width)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            int cutEnd = checked(x + width);
            var result = new HdrRgba16FloatBuffer(source.Width - width, source.Height);

            try
            {
                Span<byte> resultBytes = result.GetWritablePixelSpan();
                int leftBytes = checked(x * HdrRgba16FloatBuffer.BytesPerPixel);
                int rightBytes = checked((source.Width - cutEnd) * HdrRgba16FloatBuffer.BytesPerPixel);

                for (int y = 0; y < source.Height; y++)
                {
                    ReadOnlySpan<byte> sourceRow = source.GetRowSpan(y);
                    Span<byte> resultRow = resultBytes.Slice(y * result.RowBytes, result.RowBytes);
                    sourceRow.Slice(0, leftBytes).CopyTo(resultRow);
                    sourceRow.Slice(
                            cutEnd * HdrRgba16FloatBuffer.BytesPerPixel,
                            rightBytes)
                        .CopyTo(resultRow.Slice(leftBytes));
                }

                return CreateCutOutDocument(result, x, width, vertical: true);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public HdrImageDocument CutOutHorizontal(int y, int height)
        {
            HdrRgba16FloatBuffer source = MasterPixels;
            if (y < 0 || height <= 0 || y > source.Height - height || height == source.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            int cutEnd = checked(y + height);
            var result = new HdrRgba16FloatBuffer(source.Width, source.Height - height);

            try
            {
                Span<byte> resultBytes = result.GetWritablePixelSpan();
                for (int sourceY = 0; sourceY < source.Height; sourceY++)
                {
                    if (sourceY >= y && sourceY < cutEnd)
                    {
                        continue;
                    }

                    int resultY = sourceY < y ? sourceY : sourceY - height;
                    source.GetRowSpan(sourceY).CopyTo(
                        resultBytes.Slice(resultY * result.RowBytes, result.RowBytes));
                }

                return CreateCutOutDocument(result, y, height, vertical: false);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private HdrImageDocument CreateCutOutDocument(
            HdrRgba16FloatBuffer pixels,
            int start,
            int length,
            bool vertical)
        {
            int sourceWidth = MasterPixels.Width;
            int sourceHeight = MasterPixels.Height;
            int end = checked(start + length);
            Rectangle firstArea = vertical
                ? new Rectangle(0, 0, start, sourceHeight)
                : new Rectangle(0, 0, sourceWidth, start);
            Rectangle secondArea = vertical
                ? new Rectangle(end, 0, sourceWidth - end, sourceHeight)
                : new Rectangle(0, end, sourceWidth, sourceHeight - end);
            var segments = new List<HdrCaptureSourceSegment>();

            foreach (HdrCaptureSourceSegment segment in sourceSegments)
            {
                AddCutOutSegment(segments, segment, firstArea, 0, 0);
                AddCutOutSegment(
                    segments,
                    segment,
                    secondArea,
                    vertical ? -length : 0,
                    vertical ? 0 : -length);
            }

            var document = new HdrImageDocument(
                new Rectangle(RequestedBounds.Location, new Size(pixels.Width, pixels.Height)),
                pixels,
                segments);
            document.CaptureTimestamp = CaptureTimestamp;
            document.Revision = checked(Revision + 1);
            return document;
        }

        private static void AddCutOutSegment(
            ICollection<HdrCaptureSourceSegment> result,
            HdrCaptureSourceSegment segment,
            Rectangle retainedArea,
            int offsetX,
            int offsetY)
        {
            Rectangle intersection = Rectangle.Intersect(segment.DestinationRectangle, retainedArea);
            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                return;
            }

            intersection.Offset(offsetX, offsetY);
            result.Add(new HdrCaptureSourceSegment(
                intersection,
                segment.DisplayDeviceName,
                segment.WasHdrActive,
                segment.SdrWhiteNits,
                segment.DisplayPeakNits));

        }

        /// <summary>
        /// Finalizes a source-effect mutation through editor-rasterized 8-bit
        /// coverage. Zero coverage restores the original pixel exactly. When
        /// requested, the current pixel is first source-over composited onto the
        /// original because editor effect bitmaps are ordinary premultiplied layers.
        /// This helper intentionally does not advance the revision; the mutation did.
        /// </summary>
        public void CompositeCurrentEffectThroughCoverage(
            HdrRgba16FloatBuffer originalPixels,
            Rectangle affectedRectangle,
            int maskX,
            int maskY,
            int maskWidth,
            int maskHeight,
            ReadOnlyMemory<byte> coverageMask,
            bool compositeCurrentOverOriginal)
        {
            ArgumentNullException.ThrowIfNull(originalPixels);
            if (originalPixels.Width != MasterPixels.Width ||
                originalPixels.Height != MasterPixels.Height)
            {
                throw new ArgumentException("Original pixels must match the current HDR document.", nameof(originalPixels));
            }

            if (affectedRectangle.Width <= 0 || affectedRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(affectedRectangle));
            }

            long expectedMaskLength = (long)maskWidth * maskHeight;
            if (maskX < 0 || maskY < 0 || maskWidth < 0 || maskHeight < 0 ||
                expectedMaskLength > int.MaxValue ||
                coverageMask.Length != expectedMaskLength)
            {
                throw new ArgumentException("Coverage dimensions do not match its byte count.", nameof(coverageMask));
            }

            long maskRight = (long)maskX + maskWidth;
            long maskBottom = (long)maskY + maskHeight;

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                affectedRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            Parallel.For(affected.Top, affected.Bottom, y =>
            {
                ReadOnlySpan<byte> originalRow = originalPixels.GetRowSpan(y);
                Span<byte> currentRow = pixels.GetWritableRowSpan(y);
                ReadOnlySpan<byte> mask = coverageMask.Span;

                for (int x = affected.Left; x < affected.Right; x++)
                {
                    byte coverage = x >= maskX && y >= maskY &&
                        x < maskRight && y < maskBottom
                        ? mask[(y - maskY) * maskWidth + x - maskX]
                        : (byte)0;
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;

                    if (coverage == 0)
                    {
                        originalRow.Slice(offset, HdrRgba16FloatBuffer.BytesPerPixel)
                            .CopyTo(currentRow.Slice(offset, HdrRgba16FloatBuffer.BytesPerPixel));
                        continue;
                    }

                    if (!compositeCurrentOverOriginal && coverage == 255)
                    {
                        continue;
                    }

                    float originalRed = ReadHalf(originalRow, offset);
                    float originalGreen = ReadHalf(originalRow, offset + 2);
                    float originalBlue = ReadHalf(originalRow, offset + 4);
                    float originalAlpha = Math.Clamp(ReadHalf(originalRow, offset + 6), 0f, 1f);
                    float resultRed = ReadHalf(currentRow, offset);
                    float resultGreen = ReadHalf(currentRow, offset + 2);
                    float resultBlue = ReadHalf(currentRow, offset + 4);
                    float resultAlpha = Math.Clamp(ReadHalf(currentRow, offset + 6), 0f, 1f);

                    if (compositeCurrentOverOriginal)
                    {
                        float remainingOriginal = 1f - resultAlpha;
                        resultRed += originalRed * remainingOriginal;
                        resultGreen += originalGreen * remainingOriginal;
                        resultBlue += originalBlue * remainingOriginal;
                        resultAlpha += originalAlpha * remainingOriginal;
                    }

                    float amount = coverage / 255f;
                    WriteHalf(currentRow, offset, Lerp(originalRed, resultRed, amount));
                    WriteHalf(currentRow, offset + 2, Lerp(originalGreen, resultGreen, amount));
                    WriteHalf(currentRow, offset + 4, Lerp(originalBlue, resultBlue, amount));
                    WriteHalf(currentRow, offset + 6, Math.Clamp(Lerp(originalAlpha, resultAlpha, amount), 0f, 1f));
                }
            });
        }

        /// <summary>
        /// Replays the editor's axis-aligned rectangular Magnify tool by taking
        /// a centered source crop and scaling it with a Mitchell cubic filter.
        /// Sampling stays in premultiplied linear scRGB and only the capture
        /// rectangle is copied, so an effect cannot overwrite pixels it still reads.
        /// </summary>
        public void MagnifyRectangle(Rectangle localRectangle, float zoom)
        {
            if (localRectangle.Width <= 0 || localRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localRectangle));
            }

            if (!float.IsFinite(zoom) || zoom < 1f || zoom > MaximumSupportedMagnification)
            {
                throw new ArgumentOutOfRangeException(nameof(zoom));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                localRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            float captureWidth = affected.Width / zoom;
            float captureHeight = affected.Height / zoom;
            float centerX = affected.Left + affected.Width / 2f;
            float centerY = affected.Top + affected.Height / 2f;
            float captureX = centerX - captureWidth / 2f;
            float captureY = centerY - captureHeight / 2f;
            Rectangle captureRectangle = Rectangle.FromLTRB(
                (int)captureX,
                (int)captureY,
                (int)(captureX + captureWidth),
                (int)(captureY + captureHeight));
            captureRectangle.Intersect(new Rectangle(0, 0, pixels.Width, pixels.Height));

            // This matches the editor's transparent no-op result when integer
            // conversion collapses a very small capture rectangle.
            if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
            {
                return;
            }

            using var capture = new HdrRgba16FloatBuffer(
                captureRectangle.Width,
                captureRectangle.Height);
            int captureRowBytes = checked(captureRectangle.Width * HdrRgba16FloatBuffer.BytesPerPixel);
            for (int y = 0; y < captureRectangle.Height; y++)
            {
                pixels.GetRowSpan(captureRectangle.Top + y)
                    .Slice(captureRectangle.Left * HdrRgba16FloatBuffer.BytesPerPixel, captureRowBytes)
                    .CopyTo(capture.GetWritableRowSpan(y));
            }

            CreateMitchellSamples(capture.Width, affected.Width, out int[] xIndices, out float[] xWeights);
            CreateMitchellSamples(capture.Height, affected.Height, out int[] yIndices, out float[] yWeights);

            Parallel.For(0, affected.Height, localY =>
            {
                Span<byte> destinationRow = pixels.GetWritableRowSpan(affected.Top + localY);
                int ySampleOffset = localY * 4;

                for (int localX = 0; localX < affected.Width; localX++)
                {
                    int xSampleOffset = localX * 4;
                    float red = 0f;
                    float green = 0f;
                    float blue = 0f;
                    float alpha = 0f;

                    for (int yTap = 0; yTap < 4; yTap++)
                    {
                        ReadOnlySpan<byte> sourceRow = capture.GetRowSpan(yIndices[ySampleOffset + yTap]);
                        float yWeight = yWeights[ySampleOffset + yTap];

                        for (int xTap = 0; xTap < 4; xTap++)
                        {
                            int sourceOffset = xIndices[xSampleOffset + xTap] * HdrRgba16FloatBuffer.BytesPerPixel;
                            float weight = yWeight * xWeights[xSampleOffset + xTap];
                            red += ReadHalf(sourceRow, sourceOffset) * weight;
                            green += ReadHalf(sourceRow, sourceOffset + 2) * weight;
                            blue += ReadHalf(sourceRow, sourceOffset + 4) * weight;
                            alpha += ReadHalf(sourceRow, sourceOffset + 6) * weight;
                        }
                    }

                    int destinationOffset = (affected.Left + localX) * HdrRgba16FloatBuffer.BytesPerPixel;
                    WriteHalf(destinationRow, destinationOffset, red);
                    WriteHalf(destinationRow, destinationOffset + 2, green);
                    WriteHalf(destinationRow, destinationOffset + 4, blue);
                    WriteHalf(destinationRow, destinationOffset + 6, Math.Clamp(alpha, 0f, 1f));
                }
            });

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Produces the editor's rotated-Magnify content before its visual coverage
        /// mask is applied. The editor rotates the sampled source inversely inside
        /// the effect bitmap and rotates the control forward, so content resolves
        /// to a world-aligned zoom about the annotation center. Samples outside the
        /// source are transparent, matching an un-tiled Skia image draw.
        /// </summary>
        public void MagnifyAroundPoint(
            HdrRgba16FloatBuffer sourcePixels,
            Rectangle affectedRectangle,
            float centerX,
            float centerY,
            float zoom)
        {
            ArgumentNullException.ThrowIfNull(sourcePixels);
            if (sourcePixels.Width != MasterPixels.Width ||
                sourcePixels.Height != MasterPixels.Height)
            {
                throw new ArgumentException("Magnify source pixels must match the HDR document.", nameof(sourcePixels));
            }

            if (affectedRectangle.Width <= 0 || affectedRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(affectedRectangle));
            }

            if (!float.IsFinite(centerX) || !float.IsFinite(centerY))
            {
                throw new ArgumentOutOfRangeException(nameof(centerX));
            }

            if (!float.IsFinite(zoom) || zoom < 1f || zoom > MaximumSupportedMagnification)
            {
                throw new ArgumentOutOfRangeException(nameof(zoom));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                affectedRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            Parallel.For(affected.Top, affected.Bottom, y =>
            {
                Span<byte> destinationRow = pixels.GetWritableRowSpan(y);
                float sourceY = centerY + ((y + 0.5f - centerY) / zoom) - 0.5f;
                int firstY = (int)MathF.Floor(sourceY) - 1;

                for (int x = affected.Left; x < affected.Right; x++)
                {
                    float sourceX = centerX + ((x + 0.5f - centerX) / zoom) - 0.5f;
                    int firstX = (int)MathF.Floor(sourceX) - 1;
                    float red = 0f;
                    float green = 0f;
                    float blue = 0f;
                    float alpha = 0f;

                    for (int yTap = 0; yTap < 4; yTap++)
                    {
                        int sampleY = firstY + yTap;
                        if ((uint)sampleY >= (uint)sourcePixels.Height)
                        {
                            continue;
                        }

                        ReadOnlySpan<byte> sourceRow = sourcePixels.GetRowSpan(sampleY);
                        float yWeight = MitchellWeight(sourceY - sampleY);
                        for (int xTap = 0; xTap < 4; xTap++)
                        {
                            int sampleX = firstX + xTap;
                            if ((uint)sampleX >= (uint)sourcePixels.Width)
                            {
                                continue;
                            }

                            int sourceOffset = sampleX * HdrRgba16FloatBuffer.BytesPerPixel;
                            float weight = yWeight * MitchellWeight(sourceX - sampleX);
                            red += ReadHalf(sourceRow, sourceOffset) * weight;
                            green += ReadHalf(sourceRow, sourceOffset + 2) * weight;
                            blue += ReadHalf(sourceRow, sourceOffset + 4) * weight;
                            alpha += ReadHalf(sourceRow, sourceOffset + 6) * weight;
                        }
                    }

                    int destinationOffset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    WriteHalf(destinationRow, destinationOffset, red);
                    WriteHalf(destinationRow, destinationOffset + 2, green);
                    WriteHalf(destinationRow, destinationOffset + 4, blue);
                    WriteHalf(destinationRow, destinationOffset + 6, Math.Clamp(alpha, 0f, 1f));
                }
            });

            Revision = checked(Revision + 1);
        }

        private static void CreateMitchellSamples(
            int sourceLength,
            int destinationLength,
            out int[] indices,
            out float[] weights)
        {
            indices = new int[checked(destinationLength * 4)];
            weights = new float[indices.Length];

            for (int destination = 0; destination < destinationLength; destination++)
            {
                float sourcePosition = ((destination + 0.5f) * sourceLength / destinationLength) - 0.5f;
                int first = (int)MathF.Floor(sourcePosition) - 1;
                int sampleOffset = destination * 4;

                for (int tap = 0; tap < 4; tap++)
                {
                    int sample = first + tap;
                    indices[sampleOffset + tap] = Math.Clamp(sample, 0, sourceLength - 1);
                    weights[sampleOffset + tap] = MitchellWeight(sourcePosition - sample);
                }
            }
        }

        private static float MitchellWeight(float distance)
        {
            float x = MathF.Abs(distance);
            if (x < 1f)
            {
                return ((7f * x - 12f) * x * x) / 6f + 8f / 9f;
            }

            return x < 2f
                ? (((-7f * x + 36f) * x - 60f) * x + 32f) / 18f
                : 0f;
        }

        /// <summary>
        /// Applies a separable Gaussian blur to one local rectangle. Sampling uses
        /// clamped full-canvas edges and a three-sigma kernel, while the intermediate
        /// allocation is bounded to the affected columns and required context rows.
        /// </summary>
        public void BlurRectangle(Rectangle localRectangle, float sigma)
        {
            if (localRectangle.Width <= 0 || localRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localRectangle));
            }

            if (!float.IsFinite(sigma) || sigma <= 0f || sigma > MaximumSupportedBlurSigma)
            {
                throw new ArgumentOutOfRangeException(nameof(sigma));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                localRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            int radius = checked((int)Math.Ceiling(sigma * 3f));
            float[] kernel = CreateGaussianKernel(radius, sigma);
            int contextHeight = checked(affected.Height + radius * 2);
            using var horizontal = new HdrRgba16FloatBuffer(affected.Width, contextHeight);

            Parallel.For(0, contextHeight, contextY =>
            {
                int sourceY = Math.Clamp(affected.Top + contextY - radius, 0, pixels.Height - 1);
                ReadOnlySpan<byte> sourceRow = pixels.GetRowSpan(sourceY);
                Span<byte> horizontalRow = horizontal.GetWritableRowSpan(contextY);

                for (int localX = 0; localX < affected.Width; localX++)
                {
                    int sourceX = affected.Left + localX;
                    float red = 0f;
                    float green = 0f;
                    float blue = 0f;
                    float alpha = 0f;

                    for (int tap = -radius; tap <= radius; tap++)
                    {
                        int sampleX = Math.Clamp(sourceX + tap, 0, pixels.Width - 1);
                        int sampleOffset = sampleX * HdrRgba16FloatBuffer.BytesPerPixel;
                        float weight = kernel[tap + radius];
                        red += ReadHalf(sourceRow, sampleOffset) * weight;
                        green += ReadHalf(sourceRow, sampleOffset + 2) * weight;
                        blue += ReadHalf(sourceRow, sampleOffset + 4) * weight;
                        alpha += ReadHalf(sourceRow, sampleOffset + 6) * weight;
                    }

                    int destinationOffset = localX * HdrRgba16FloatBuffer.BytesPerPixel;
                    WriteHalf(horizontalRow, destinationOffset, red);
                    WriteHalf(horizontalRow, destinationOffset + 2, green);
                    WriteHalf(horizontalRow, destinationOffset + 4, blue);
                    WriteHalf(horizontalRow, destinationOffset + 6, Math.Clamp(alpha, 0f, 1f));
                }
            });

            Parallel.For(0, affected.Height, localY =>
            {
                Span<byte> destinationRow = pixels.GetWritableRowSpan(affected.Top + localY);

                for (int localX = 0; localX < affected.Width; localX++)
                {
                    float red = 0f;
                    float green = 0f;
                    float blue = 0f;
                    float alpha = 0f;
                    int sampleOffset = localX * HdrRgba16FloatBuffer.BytesPerPixel;

                    for (int tap = 0; tap < kernel.Length; tap++)
                    {
                        ReadOnlySpan<byte> horizontalRow = horizontal.GetRowSpan(localY + tap);
                        float weight = kernel[tap];
                        red += ReadHalf(horizontalRow, sampleOffset) * weight;
                        green += ReadHalf(horizontalRow, sampleOffset + 2) * weight;
                        blue += ReadHalf(horizontalRow, sampleOffset + 4) * weight;
                        alpha += ReadHalf(horizontalRow, sampleOffset + 6) * weight;
                    }

                    int destinationOffset = (affected.Left + localX) * HdrRgba16FloatBuffer.BytesPerPixel;
                    WriteHalf(destinationRow, destinationOffset, red);
                    WriteHalf(destinationRow, destinationOffset + 2, green);
                    WriteHalf(destinationRow, destinationOffset + 4, blue);
                    WriteHalf(destinationRow, destinationOffset + 6, Math.Clamp(alpha, 0f, 1f));
                }
            });

            Revision = checked(Revision + 1);
        }

        private static float[] CreateGaussianKernel(int radius, float sigma)
        {
            float[] kernel = new float[checked(radius * 2 + 1)];
            double denominator = 2d * sigma * sigma;
            double sum = 0d;

            for (int tap = -radius; tap <= radius; tap++)
            {
                double weight = Math.Exp(-(tap * (double)tap) / denominator);
                kernel[tap + radius] = (float)weight;
                sum += weight;
            }

            float inverseSum = (float)(1d / sum);
            for (int index = 0; index < kernel.Length; index++)
            {
                kernel[index] *= inverseSum;
            }

            return kernel;
        }

        /// <summary>
        /// Applies two horizontal/vertical box-blur passes to one local rectangle.
        /// This matches the legacy region editor's blur strength and clipped-edge
        /// sampling while retaining premultiplied linear scRGB values.
        /// </summary>
        public void BoxBlurRectangle(Rectangle localRectangle, int range)
        {
            if (localRectangle.Width <= 0 || localRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localRectangle));
            }

            if (range <= 1 || range > MaximumSupportedBoxBlurRange)
            {
                throw new ArgumentOutOfRangeException(nameof(range));
            }

            if ((range & 1) == 0)
            {
                range++;
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                localRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            using var first = new HdrRgba16FloatBuffer(affected.Width, affected.Height);
            using var second = new HdrRgba16FloatBuffer(affected.Width, affected.Height);
            int rowBytes = checked(affected.Width * HdrRgba16FloatBuffer.BytesPerPixel);

            for (int y = 0; y < affected.Height; y++)
            {
                pixels.GetRowSpan(affected.Top + y)
                    .Slice(affected.Left * HdrRgba16FloatBuffer.BytesPerPixel, rowBytes)
                    .CopyTo(first.GetWritableRowSpan(y));
            }

            for (int pass = 0; pass < 2; pass++)
            {
                BoxBlurHorizontal(first, second, range);
                BoxBlurVertical(second, first, range);
            }

            for (int y = 0; y < affected.Height; y++)
            {
                first.GetRowSpan(y).CopyTo(
                    pixels.GetWritableRowSpan(affected.Top + y)
                        .Slice(affected.Left * HdrRgba16FloatBuffer.BytesPerPixel, rowBytes));
            }

            Revision = checked(Revision + 1);
        }

        private static void BoxBlurHorizontal(
            HdrRgba16FloatBuffer source,
            HdrRgba16FloatBuffer destination,
            int range)
        {
            int halfRange = range / 2;

            Parallel.For(0, source.Height, y =>
            {
                ReadOnlySpan<byte> sourceRow = source.GetRowSpan(y);
                Span<byte> destinationRow = destination.GetWritableRowSpan(y);
                double red = 0d;
                double green = 0d;
                double blue = 0d;
                double alpha = 0d;
                int count = 0;

                for (int sampleX = 0; sampleX <= Math.Min(halfRange, source.Width - 1); sampleX++)
                {
                    int offset = sampleX * HdrRgba16FloatBuffer.BytesPerPixel;
                    red += ReadHalf(sourceRow, offset);
                    green += ReadHalf(sourceRow, offset + 2);
                    blue += ReadHalf(sourceRow, offset + 4);
                    alpha += ReadHalf(sourceRow, offset + 6);
                    count++;
                }

                for (int x = 0; x < source.Width; x++)
                {
                    int destinationOffset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float inverseCount = 1f / count;
                    WriteHalf(destinationRow, destinationOffset, (float)red * inverseCount);
                    WriteHalf(destinationRow, destinationOffset + 2, (float)green * inverseCount);
                    WriteHalf(destinationRow, destinationOffset + 4, (float)blue * inverseCount);
                    WriteHalf(destinationRow, destinationOffset + 6, Math.Clamp((float)alpha * inverseCount, 0f, 1f));

                    int removeX = x - halfRange;
                    if (removeX >= 0)
                    {
                        int offset = removeX * HdrRgba16FloatBuffer.BytesPerPixel;
                        red -= ReadHalf(sourceRow, offset);
                        green -= ReadHalf(sourceRow, offset + 2);
                        blue -= ReadHalf(sourceRow, offset + 4);
                        alpha -= ReadHalf(sourceRow, offset + 6);
                        count--;
                    }

                    int addX = x + halfRange + 1;
                    if (addX < source.Width)
                    {
                        int offset = addX * HdrRgba16FloatBuffer.BytesPerPixel;
                        red += ReadHalf(sourceRow, offset);
                        green += ReadHalf(sourceRow, offset + 2);
                        blue += ReadHalf(sourceRow, offset + 4);
                        alpha += ReadHalf(sourceRow, offset + 6);
                        count++;
                    }
                }
            });
        }

        private static void BoxBlurVertical(
            HdrRgba16FloatBuffer source,
            HdrRgba16FloatBuffer destination,
            int range)
        {
            int halfRange = range / 2;

            Parallel.For(0, source.Width, x =>
            {
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                double red = 0d;
                double green = 0d;
                double blue = 0d;
                double alpha = 0d;
                int count = 0;

                for (int sampleY = 0; sampleY <= Math.Min(halfRange, source.Height - 1); sampleY++)
                {
                    ReadOnlySpan<byte> row = source.GetRowSpan(sampleY);
                    red += ReadHalf(row, offset);
                    green += ReadHalf(row, offset + 2);
                    blue += ReadHalf(row, offset + 4);
                    alpha += ReadHalf(row, offset + 6);
                    count++;
                }

                for (int y = 0; y < source.Height; y++)
                {
                    Span<byte> destinationRow = destination.GetWritableRowSpan(y);
                    float inverseCount = 1f / count;
                    WriteHalf(destinationRow, offset, (float)red * inverseCount);
                    WriteHalf(destinationRow, offset + 2, (float)green * inverseCount);
                    WriteHalf(destinationRow, offset + 4, (float)blue * inverseCount);
                    WriteHalf(destinationRow, offset + 6, Math.Clamp((float)alpha * inverseCount, 0f, 1f));

                    int removeY = y - halfRange;
                    if (removeY >= 0)
                    {
                        ReadOnlySpan<byte> row = source.GetRowSpan(removeY);
                        red -= ReadHalf(row, offset);
                        green -= ReadHalf(row, offset + 2);
                        blue -= ReadHalf(row, offset + 4);
                        alpha -= ReadHalf(row, offset + 6);
                        count--;
                    }

                    int addY = y + halfRange + 1;
                    if (addY < source.Height)
                    {
                        ReadOnlySpan<byte> row = source.GetRowSpan(addY);
                        red += ReadHalf(row, offset);
                        green += ReadHalf(row, offset + 2);
                        blue += ReadHalf(row, offset + 4);
                        alpha += ReadHalf(row, offset + 6);
                        count++;
                    }
                }
            });
        }

        /// <summary>
        /// Applies one combined black spotlight overlay while leaving the union
        /// of the supplied axis-aligned rectangles unchanged.
        /// </summary>
        public void DarkenOutsideRectangles(
            IReadOnlyList<Rectangle> localRectangles,
            byte darkenOpacity) =>
            ApplySpotlightRectangles(localRectangles, darkenOpacity, blurSigma: 0f);

        /// <summary>
        /// Applies the editor's combined Spotlight overlay. When blur is enabled,
        /// a full-canvas linear-scRGB blur is drawn into the overlay before the
        /// black darkening pass; the rectangular spotlight union then clears both.
        /// Source-over composition is retained for transparent canvas pixels.
        /// </summary>
        public void ApplySpotlightRectangles(
            IReadOnlyList<Rectangle> localRectangles,
            byte darkenOpacity,
            float blurSigma)
        {
            ArgumentNullException.ThrowIfNull(localRectangles);
            if (localRectangles.Count == 0)
            {
                throw new ArgumentException("At least one spotlight rectangle is required.", nameof(localRectangles));
            }

            ApplySpotlightCore(localRectangles, darkenOpacity, blurSigma);
        }

        /// <summary>
        /// Produces the fully affected Spotlight result before editor coverage is
        /// applied. Used for antialiased rectangular and elliptical mask replay.
        /// </summary>
        public void ApplySpotlightEverywhere(byte darkenOpacity, float blurSigma) =>
            ApplySpotlightCore(Array.Empty<Rectangle>(), darkenOpacity, blurSigma);

        private void ApplySpotlightCore(
            IReadOnlyList<Rectangle> localRectangles,
            byte darkenOpacity,
            float blurSigma)
        {
            if (!float.IsFinite(blurSigma) || blurSigma < 0f || blurSigma > MaximumSupportedBlurSigma)
            {
                throw new ArgumentOutOfRangeException(nameof(blurSigma));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle canvas = new Rectangle(0, 0, pixels.Width, pixels.Height);
            var retainedRectangles = new List<Rectangle>(localRectangles.Count);

            foreach (Rectangle rectangle in localRectangles)
            {
                if (rectangle.Width <= 0 || rectangle.Height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(localRectangles));
                }

                Rectangle intersection = Rectangle.Intersect(canvas, rectangle);
                if (intersection.Width > 0 && intersection.Height > 0)
                {
                    retainedRectangles.Add(intersection);
                }
            }

            float darkenAmount = darkenOpacity / 255f;
            float darkenMultiplier = 1f - darkenAmount;
            using HdrImageDocument blurredDocument = blurSigma > 0f
                ? new HdrImageDocument(RequestedBounds, pixels.Clone(), sourceSegments)
                : null;
            if (blurredDocument != null)
            {
                blurredDocument.BlurRectangle(canvas, blurSigma);
            }

            HdrRgba16FloatBuffer blurredPixels = blurredDocument?.MasterPixels;
            Parallel.For(0, pixels.Height, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                ReadOnlySpan<byte> blurredRow = blurredPixels == null
                    ? ReadOnlySpan<byte>.Empty
                    : blurredPixels.GetRowSpan(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    bool retained = false;
                    foreach (Rectangle rectangle in retainedRectangles)
                    {
                        if (rectangle.Contains(x, y))
                        {
                            retained = true;
                            break;
                        }
                    }

                    if (!retained)
                    {
                        int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                        float sourceRed = ReadHalf(row, offset);
                        float sourceGreen = ReadHalf(row, offset + 2);
                        float sourceBlue = ReadHalf(row, offset + 4);
                        float sourceAlpha = Math.Clamp(ReadHalf(row, offset + 6), 0f, 1f);
                        float resultRed = sourceRed * darkenMultiplier;
                        float resultGreen = sourceGreen * darkenMultiplier;
                        float resultBlue = sourceBlue * darkenMultiplier;
                        float resultAlpha = darkenAmount + sourceAlpha * darkenMultiplier;

                        if (blurredPixels != null)
                        {
                            float blurredAlpha = Math.Clamp(ReadHalf(blurredRow, offset + 6), 0f, 1f);
                            float overlayAlpha = darkenAmount + blurredAlpha * darkenMultiplier;
                            float remainingSource = 1f - overlayAlpha;
                            resultRed = ReadHalf(blurredRow, offset) * darkenMultiplier +
                                sourceRed * remainingSource;
                            resultGreen = ReadHalf(blurredRow, offset + 2) * darkenMultiplier +
                                sourceGreen * remainingSource;
                            resultBlue = ReadHalf(blurredRow, offset + 4) * darkenMultiplier +
                                sourceBlue * remainingSource;
                            resultAlpha = overlayAlpha + sourceAlpha * remainingSource;
                        }

                        WriteHalf(row, offset, resultRed);
                        WriteHalf(row, offset + 2, resultGreen);
                        WriteHalf(row, offset + 4, resultBlue);
                        WriteHalf(row, offset + 6, Math.Clamp(resultAlpha, 0f, 1f));
                    }
                }
            });

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Applies the editor Highlight tool as a per-channel upper bound in
        /// premultiplied linear scRGB. sRGB min() and linear min() are equivalent
        /// under the monotonic transfer function, while this path retains HDR precision.
        /// </summary>
        public void ApplyHighlightRectangle(
            Rectangle localRectangle,
            byte red,
            byte green,
            byte blue,
            float highlightWhiteNits)
        {
            if (localRectangle.Width <= 0 || localRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localRectangle));
            }

            if (!float.IsFinite(highlightWhiteNits) ||
                highlightWhiteNits <= 0f ||
                highlightWhiteNits > 10000f)
            {
                throw new ArgumentOutOfRangeException(nameof(highlightWhiteNits));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                localRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            float whiteScale = highlightWhiteNits / HdrRgba16FloatBuffer.ReferenceWhiteNits;
            float straightRedMaximum = DecodeSrgb(red) * whiteScale;
            float straightGreenMaximum = DecodeSrgb(green) * whiteScale;
            float straightBlueMaximum = DecodeSrgb(blue) * whiteScale;

            Parallel.For(affected.Top, affected.Bottom, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                for (int x = affected.Left; x < affected.Right; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float alpha = Math.Clamp(ReadHalf(row, offset + 6), 0f, 1f);
                    WriteHalf(row, offset, Math.Min(ReadHalf(row, offset), straightRedMaximum * alpha));
                    WriteHalf(row, offset + 2, Math.Min(ReadHalf(row, offset + 2), straightGreenMaximum * alpha));
                    WriteHalf(row, offset + 4, Math.Min(ReadHalf(row, offset + 4), straightBlueMaximum * alpha));
                }
            });

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Pixelates an axis-aligned local region. The block grid is anchored to
        /// the full canvas, matching the editor's cached pixelate preview, while
        /// filtering remains in premultiplied linear scRGB.
        /// </summary>
        public void PixelateRectangle(Rectangle localRectangle, int pixelSize)
        {
            if (localRectangle.Width <= 0 || localRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localRectangle));
            }

            if (pixelSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelSize));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle canvas = new Rectangle(0, 0, pixels.Width, pixels.Height);
            Rectangle affected = Rectangle.Intersect(canvas, localRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            int reducedWidth = Math.Max(1, pixels.Width / pixelSize);
            int reducedHeight = Math.Max(1, pixels.Height / pixelSize);
            using var reduced = new HdrRgba16FloatBuffer(reducedWidth, reducedHeight);

            Parallel.For(0, reducedHeight, y =>
                ResizeBilinearRow(pixels, reduced, y));

            for (int y = affected.Top; y < affected.Bottom; y++)
            {
                int reducedY = GetNearestResizeSample(y, pixels.Height, reducedHeight);
                ReadOnlySpan<byte> reducedRow = reduced.GetRowSpan(reducedY);
                Span<byte> destinationRow = pixels.GetWritableRowSpan(y);

                for (int x = affected.Left; x < affected.Right; x++)
                {
                    int reducedX = GetNearestResizeSample(x, pixels.Width, reducedWidth);
                    reducedRow.Slice(
                            reducedX * HdrRgba16FloatBuffer.BytesPerPixel,
                            HdrRgba16FloatBuffer.BytesPerPixel)
                        .CopyTo(destinationRow.Slice(
                            x * HdrRgba16FloatBuffer.BytesPerPixel,
                            HdrRgba16FloatBuffer.BytesPerPixel));
                }
            }

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Pixelates a rectangle using blocks anchored to that rectangle, matching
        /// the legacy region editor while averaging premultiplied linear HDR values.
        /// </summary>
        public void PixelateBlocksRectangle(Rectangle localRectangle, int pixelSize)
        {
            if (localRectangle.Width <= 0 || localRectangle.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localRectangle));
            }

            if (pixelSize <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelSize));
            }

            HdrRgba16FloatBuffer pixels = MasterPixels;
            Rectangle affected = Rectangle.Intersect(
                new Rectangle(0, 0, pixels.Width, pixels.Height),
                localRectangle);
            if (affected.Width <= 0 || affected.Height <= 0)
            {
                return;
            }

            int blockRows = (affected.Height + pixelSize - 1) / pixelSize;
            Parallel.For(0, blockRows, blockRow =>
            {
                int top = affected.Top + blockRow * pixelSize;
                int bottom = Math.Min(top + pixelSize, affected.Bottom);

                for (int left = affected.Left; left < affected.Right; left += pixelSize)
                {
                    int right = Math.Min(left + pixelSize, affected.Right);
                    double red = 0d;
                    double green = 0d;
                    double blue = 0d;
                    double alpha = 0d;
                    int count = checked((right - left) * (bottom - top));

                    for (int y = top; y < bottom; y++)
                    {
                        ReadOnlySpan<byte> row = pixels.GetRowSpan(y);
                        for (int x = left; x < right; x++)
                        {
                            int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                            red += ReadHalf(row, offset);
                            green += ReadHalf(row, offset + 2);
                            blue += ReadHalf(row, offset + 4);
                            alpha += ReadHalf(row, offset + 6);
                        }
                    }

                    float inverseCount = 1f / count;
                    float averageRed = (float)red * inverseCount;
                    float averageGreen = (float)green * inverseCount;
                    float averageBlue = (float)blue * inverseCount;
                    float averageAlpha = Math.Clamp((float)alpha * inverseCount, 0f, 1f);

                    for (int y = top; y < bottom; y++)
                    {
                        Span<byte> row = pixels.GetWritableRowSpan(y);
                        for (int x = left; x < right; x++)
                        {
                            int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                            WriteHalf(row, offset, averageRed);
                            WriteHalf(row, offset + 2, averageGreen);
                            WriteHalf(row, offset + 4, averageBlue);
                            WriteHalf(row, offset + 6, averageAlpha);
                        }
                    }
                }
            });

            Revision = checked(Revision + 1);
        }

        private static void ResizeBilinearRow(
            HdrRgba16FloatBuffer source,
            HdrRgba16FloatBuffer destination,
            int destinationY)
        {
            GetLinearResizeSamples(
                destinationY,
                source.Height,
                destination.Height,
                out int top,
                out int bottom,
                out float verticalAmount);
            ReadOnlySpan<byte> topRow = source.GetRowSpan(top);
            ReadOnlySpan<byte> bottomRow = source.GetRowSpan(bottom);
            Span<byte> destinationRow = destination.GetWritableRowSpan(destinationY);

            for (int destinationX = 0; destinationX < destination.Width; destinationX++)
            {
                GetLinearResizeSamples(
                    destinationX,
                    source.Width,
                    destination.Width,
                    out int left,
                    out int right,
                    out float horizontalAmount);
                int topLeftOffset = left * HdrRgba16FloatBuffer.BytesPerPixel;
                int topRightOffset = right * HdrRgba16FloatBuffer.BytesPerPixel;
                int destinationOffset = destinationX * HdrRgba16FloatBuffer.BytesPerPixel;

                for (int channelOffset = 0; channelOffset < HdrRgba16FloatBuffer.BytesPerPixel; channelOffset += 2)
                {
                    float topValue = Lerp(
                        ReadHalf(topRow, topLeftOffset + channelOffset),
                        ReadHalf(topRow, topRightOffset + channelOffset),
                        horizontalAmount);
                    float bottomValue = Lerp(
                        ReadHalf(bottomRow, topLeftOffset + channelOffset),
                        ReadHalf(bottomRow, topRightOffset + channelOffset),
                        horizontalAmount);
                    WriteHalf(
                        destinationRow,
                        destinationOffset + channelOffset,
                        Lerp(topValue, bottomValue, verticalAmount));
                }
            }
        }

        private static void GetLinearResizeSamples(
            int destination,
            int sourceLength,
            int destinationLength,
            out int first,
            out int second,
            out float amount)
        {
            float sourcePosition = ((destination + 0.5f) * sourceLength / destinationLength) - 0.5f;
            int floor = (int)MathF.Floor(sourcePosition);
            first = Math.Clamp(floor, 0, sourceLength - 1);
            second = Math.Clamp(floor + 1, 0, sourceLength - 1);
            amount = Math.Clamp(sourcePosition - floor, 0f, 1f);
        }

        private static int GetNearestResizeSample(int destination, int destinationLength, int sourceLength) =>
            Math.Clamp((int)MathF.Floor((destination + 0.5f) * sourceLength / destinationLength), 0, sourceLength - 1);

        private static float Lerp(float first, float second, float amount) =>
            first + (second - first) * amount;

        /// Reproduces the editor's signed canvas margins without flattening the
        /// retained FP16 source. Positive margins add sRGB canvas pixels and
        /// negative margins crop the corresponding source edge.
        /// </summary>
        public HdrImageDocument ResizeCanvas(
            int top,
            int right,
            int bottom,
            int left,
            byte red,
            byte green,
            byte blue,
            byte alpha,
            float backgroundWhiteNits)
        {
            if (!float.IsFinite(backgroundWhiteNits) ||
                backgroundWhiteNits <= 0f ||
                backgroundWhiteNits > 10000f)
            {
                throw new ArgumentOutOfRangeException(nameof(backgroundWhiteNits));
            }

            HdrRgba16FloatBuffer source = MasterPixels;
            int destinationWidth = checked(source.Width + left + right);
            int destinationHeight = checked(source.Height + top + bottom);

            if (destinationWidth <= 0 || destinationHeight <= 0)
            {
                throw new ArgumentException("Canvas size after applying margins must be positive.");
            }

            var destination = new HdrRgba16FloatBuffer(destinationWidth, destinationHeight);

            try
            {
                FillCanvasBackground(
                    destination,
                    red,
                    green,
                    blue,
                    alpha,
                    backgroundWhiteNits);

                int sourceStartX = Math.Max(0, -left);
                int sourceStartY = Math.Max(0, -top);
                int destinationStartX = Math.Max(0, left);
                int destinationStartY = Math.Max(0, top);
                int copyWidth = Math.Max(0, Math.Min(
                    source.Width - sourceStartX,
                    destinationWidth - destinationStartX));
                int copyHeight = Math.Max(0, Math.Min(
                    source.Height - sourceStartY,
                    destinationHeight - destinationStartY));
                Rectangle copiedDestination = Rectangle.Empty;

                if (copyWidth > 0 && copyHeight > 0)
                {
                    int copyBytes = checked(copyWidth * HdrRgba16FloatBuffer.BytesPerPixel);
                    for (int y = 0; y < copyHeight; y++)
                    {
                        source.GetRowSpan(sourceStartY + y)
                            .Slice(sourceStartX * HdrRgba16FloatBuffer.BytesPerPixel, copyBytes)
                            .CopyTo(destination.GetWritableRowSpan(destinationStartY + y)
                                .Slice(destinationStartX * HdrRgba16FloatBuffer.BytesPerPixel, copyBytes));
                    }

                    copiedDestination = new Rectangle(
                        destinationStartX,
                        destinationStartY,
                        copyWidth,
                        copyHeight);
                }

                var destinationSegments = new List<HdrCaptureSourceSegment>();
                Rectangle retainedSource = new Rectangle(
                    sourceStartX,
                    sourceStartY,
                    copyWidth,
                    copyHeight);

                if (copyWidth > 0 && copyHeight > 0)
                {
                    foreach (HdrCaptureSourceSegment segment in sourceSegments)
                    {
                        Rectangle intersection = Rectangle.Intersect(
                            segment.DestinationRectangle,
                            retainedSource);
                        if (intersection.Width <= 0 || intersection.Height <= 0)
                        {
                            continue;
                        }

                        intersection.Offset(left, top);
                        destinationSegments.Add(new HdrCaptureSourceSegment(
                            intersection,
                            segment.DisplayDeviceName,
                            segment.WasHdrActive,
                            segment.SdrWhiteNits,
                            segment.DisplayPeakNits));
                    }
                }

                AddCanvasBackgroundSegments(
                    destinationSegments,
                    destinationWidth,
                    destinationHeight,
                    copiedDestination,
                    backgroundWhiteNits);

                var document = new HdrImageDocument(
                    new Rectangle(
                        RequestedBounds.Location,
                        new Size(destinationWidth, destinationHeight)),
                    destination,
                    destinationSegments);
                document.CaptureTimestamp = CaptureTimestamp;
                document.Revision = checked(Revision + 1);
                return document;
            }
            catch
            {
                destination.Dispose();
                throw;
            }
        }

        private static void FillCanvasBackground(
            HdrRgba16FloatBuffer destination,
            byte red,
            byte green,
            byte blue,
            byte alpha,
            float backgroundWhiteNits)
        {
            if (alpha == 0)
            {
                return;
            }

            float normalizedAlpha = alpha / 255f;
            float whiteScale = backgroundWhiteNits / HdrRgba16FloatBuffer.ReferenceWhiteNits;
            ushort redHalf = BitConverter.HalfToUInt16Bits(
                (Half)(DecodeSrgb(red) * normalizedAlpha * whiteScale));
            ushort greenHalf = BitConverter.HalfToUInt16Bits(
                (Half)(DecodeSrgb(green) * normalizedAlpha * whiteScale));
            ushort blueHalf = BitConverter.HalfToUInt16Bits(
                (Half)(DecodeSrgb(blue) * normalizedAlpha * whiteScale));
            ushort alphaHalf = BitConverter.HalfToUInt16Bits((Half)normalizedAlpha);

            Parallel.For(0, destination.Height, y =>
            {
                Span<byte> row = destination.GetWritableRowSpan(y);
                for (int offset = 0; offset < row.Length; offset += HdrRgba16FloatBuffer.BytesPerPixel)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(offset, 2), redHalf);
                    BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(offset + 2, 2), greenHalf);
                    BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(offset + 4, 2), blueHalf);
                    BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(offset + 6, 2), alphaHalf);
                }
            });
        }

        private static float DecodeSrgb(byte value)
        {
            float encoded = value / 255f;
            return encoded <= 0.04045f
                ? encoded / 12.92f
                : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        private static void AddCanvasBackgroundSegments(
            ICollection<HdrCaptureSourceSegment> segments,
            int width,
            int height,
            Rectangle copiedSource,
            float backgroundWhiteNits)
        {
            if (copiedSource.Width <= 0 || copiedSource.Height <= 0)
            {
                AddCanvasBackgroundSegment(segments, new Rectangle(0, 0, width, height), backgroundWhiteNits);
                return;
            }

            AddCanvasBackgroundSegment(segments, new Rectangle(0, 0, width, copiedSource.Top), backgroundWhiteNits);
            AddCanvasBackgroundSegment(segments, new Rectangle(0, copiedSource.Bottom, width, height - copiedSource.Bottom), backgroundWhiteNits);
            AddCanvasBackgroundSegment(segments, new Rectangle(0, copiedSource.Top, copiedSource.Left, copiedSource.Height), backgroundWhiteNits);
            AddCanvasBackgroundSegment(segments, new Rectangle(copiedSource.Right, copiedSource.Top, width - copiedSource.Right, copiedSource.Height), backgroundWhiteNits);
        }

        private static void AddCanvasBackgroundSegment(
            ICollection<HdrCaptureSourceSegment> segments,
            Rectangle rectangle,
            float backgroundWhiteNits)
        {
            if (rectangle.Width > 0 && rectangle.Height > 0)
            {
                segments.Add(new HdrCaptureSourceSegment(
                    rectangle,
                    "Editor canvas",
                    false,
                    backgroundWhiteNits,
                    backgroundWhiteNits));
            }
        }

        public HdrImageDocument ResizeCatmullRom(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            HdrRgba16FloatBuffer source = MasterPixels;
            CreateCatmullRomSamples(source.Width, width, out int[] xIndices, out float[] xWeights);
            CreateCatmullRomSamples(source.Height, height, out int[] yIndices, out float[] yWeights);
            var resizedPixels = new HdrRgba16FloatBuffer(width, height);

            try
            {
                Parallel.For(0, height, destinationY =>
                    ResizeCatmullRomRow(
                        source,
                        resizedPixels,
                        destinationY,
                        xIndices,
                        xWeights,
                        yIndices,
                        yWeights));

                var resizedSegments = new List<HdrCaptureSourceSegment>(sourceSegments.Count);
                foreach (HdrCaptureSourceSegment segment in sourceSegments)
                {
                    Rectangle rectangle = segment.DestinationRectangle;
                    int left = ScaleBoundary(rectangle.Left, source.Width, width);
                    int top = ScaleBoundary(rectangle.Top, source.Height, height);
                    int right = ScaleBoundary(rectangle.Right, source.Width, width);
                    int bottom = ScaleBoundary(rectangle.Bottom, source.Height, height);

                    if (right > left && bottom > top)
                    {
                        resizedSegments.Add(new HdrCaptureSourceSegment(
                            Rectangle.FromLTRB(left, top, right, bottom),
                            segment.DisplayDeviceName,
                            segment.WasHdrActive,
                            segment.SdrWhiteNits,
                            segment.DisplayPeakNits));
                    }
                }

                var document = new HdrImageDocument(
                    new Rectangle(RequestedBounds.Location, new Size(width, height)),
                    resizedPixels,
                    resizedSegments);
                document.CaptureTimestamp = CaptureTimestamp;
                document.Revision = checked(Revision + 1);
                return document;
            }
            catch
            {
                resizedPixels.Dispose();
                throw;
            }
        }

        private static void ResizeCatmullRomRow(
            HdrRgba16FloatBuffer source,
            HdrRgba16FloatBuffer destination,
            int destinationY,
            int[] xIndices,
            float[] xWeights,
            int[] yIndices,
            float[] yWeights)
        {
            Span<byte> destinationRow = destination.GetWritableRowSpan(destinationY);
            int ySampleOffset = destinationY * 4;

            for (int destinationX = 0; destinationX < destination.Width; destinationX++)
            {
                int xSampleOffset = destinationX * 4;
                float red = 0f;
                float green = 0f;
                float blue = 0f;
                float alpha = 0f;

                for (int yTap = 0; yTap < 4; yTap++)
                {
                    ReadOnlySpan<byte> sourceRow = source.GetRowSpan(yIndices[ySampleOffset + yTap]);
                    float yWeight = yWeights[ySampleOffset + yTap];

                    for (int xTap = 0; xTap < 4; xTap++)
                    {
                        int sourceOffset = xIndices[xSampleOffset + xTap] * HdrRgba16FloatBuffer.BytesPerPixel;
                        float weight = yWeight * xWeights[xSampleOffset + xTap];
                        red += ReadHalf(sourceRow, sourceOffset) * weight;
                        green += ReadHalf(sourceRow, sourceOffset + 2) * weight;
                        blue += ReadHalf(sourceRow, sourceOffset + 4) * weight;
                        alpha += ReadHalf(sourceRow, sourceOffset + 6) * weight;
                    }
                }

                int destinationOffset = destinationX * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(destinationRow, destinationOffset, red);
                WriteHalf(destinationRow, destinationOffset + 2, green);
                WriteHalf(destinationRow, destinationOffset + 4, blue);
                WriteHalf(destinationRow, destinationOffset + 6, Math.Clamp(alpha, 0f, 1f));
            }
        }

        private static void CreateCatmullRomSamples(
            int sourceLength,
            int destinationLength,
            out int[] indices,
            out float[] weights)
        {
            indices = new int[checked(destinationLength * 4)];
            weights = new float[indices.Length];

            for (int destination = 0; destination < destinationLength; destination++)
            {
                float sourcePosition = ((destination + 0.5f) * sourceLength / destinationLength) - 0.5f;
                int first = (int)MathF.Floor(sourcePosition) - 1;
                int sampleOffset = destination * 4;

                for (int tap = 0; tap < 4; tap++)
                {
                    int sample = first + tap;
                    indices[sampleOffset + tap] = Math.Clamp(sample, 0, sourceLength - 1);
                    weights[sampleOffset + tap] = CatmullRomWeight(sourcePosition - sample);
                }
            }
        }

        private static float CatmullRomWeight(float distance)
        {
            float x = MathF.Abs(distance);
            if (x < 1f)
            {
                return ((1.5f * x - 2.5f) * x * x) + 1f;
            }

            return x < 2f
                ? (((-0.5f * x + 2.5f) * x - 4f) * x) + 2f
                : 0f;
        }

        private static int ScaleBoundary(int value, int sourceLength, int destinationLength) =>
            checked((int)(((long)value * destinationLength + sourceLength / 2L) / sourceLength));

        private static float ReadHalf(ReadOnlySpan<byte> row, int offset) =>
            (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset, 2)));

        private static void WriteHalf(Span<byte> row, int offset, float value)
        {
            value = float.IsNaN(value) ? 0f : Math.Clamp(value, -65504f, 65504f);
            BinaryPrimitives.WriteUInt16LittleEndian(
                row.Slice(offset, 2),
                BitConverter.HalfToUInt16Bits((Half)value));
        }
        private HdrImageDocument TransformOrthogonal(OrthogonalTransform transform)
        {
            HdrRgba16FloatBuffer source = MasterPixels;
            bool swapsDimensions = transform == OrthogonalTransform.Rotate90Clockwise ||
                transform == OrthogonalTransform.Rotate90CounterClockwise;
            int destinationWidth = swapsDimensions ? source.Height : source.Width;
            int destinationHeight = swapsDimensions ? source.Width : source.Height;
            var transformedPixels = new HdrRgba16FloatBuffer(destinationWidth, destinationHeight);

            try
            {
                Span<byte> destinationBytes = transformedPixels.GetWritablePixelSpan();

                for (int sourceY = 0; sourceY < source.Height; sourceY++)
                {
                    ReadOnlySpan<byte> sourceRow = source.GetRowSpan(sourceY);

                    for (int sourceX = 0; sourceX < source.Width; sourceX++)
                    {
                        (int destinationX, int destinationY) = TransformPoint(
                            sourceX,
                            sourceY,
                            source.Width,
                            source.Height,
                            transform);
                        sourceRow.Slice(
                                sourceX * HdrRgba16FloatBuffer.BytesPerPixel,
                                HdrRgba16FloatBuffer.BytesPerPixel)
                            .CopyTo(destinationBytes.Slice(
                                destinationY * transformedPixels.RowBytes +
                                    destinationX * HdrRgba16FloatBuffer.BytesPerPixel,
                                HdrRgba16FloatBuffer.BytesPerPixel));
                    }
                }

                var transformedSegments = new List<HdrCaptureSourceSegment>(sourceSegments.Count);
                foreach (HdrCaptureSourceSegment segment in sourceSegments)
                {
                    transformedSegments.Add(new HdrCaptureSourceSegment(
                        TransformRectangle(
                            segment.DestinationRectangle,
                            source.Width,
                            source.Height,
                            transform),
                        segment.DisplayDeviceName,
                        segment.WasHdrActive,
                        segment.SdrWhiteNits,
                        segment.DisplayPeakNits));
                }

                var transformedDocument = new HdrImageDocument(
                    new Rectangle(RequestedBounds.Location, new Size(destinationWidth, destinationHeight)),
                    transformedPixels,
                    transformedSegments);
                transformedDocument.CaptureTimestamp = CaptureTimestamp;
                transformedDocument.Revision = checked(Revision + 1);
                return transformedDocument;
            }
            catch
            {
                transformedPixels.Dispose();
                throw;
            }
        }

        private static (int X, int Y) TransformPoint(
            int x,
            int y,
            int width,
            int height,
            OrthogonalTransform transform) => transform switch
            {
                OrthogonalTransform.Rotate90Clockwise => (height - 1 - y, x),
                OrthogonalTransform.Rotate90CounterClockwise => (y, width - 1 - x),
                OrthogonalTransform.Rotate180 => (width - 1 - x, height - 1 - y),
                OrthogonalTransform.FlipHorizontal => (width - 1 - x, y),
                OrthogonalTransform.FlipVertical => (x, height - 1 - y),
                _ => throw new ArgumentOutOfRangeException(nameof(transform))
            };

        private static Rectangle TransformRectangle(
            Rectangle rectangle,
            int width,
            int height,
            OrthogonalTransform transform) => transform switch
            {
                OrthogonalTransform.Rotate90Clockwise => new Rectangle(
                    height - rectangle.Bottom,
                    rectangle.X,
                    rectangle.Height,
                    rectangle.Width),
                OrthogonalTransform.Rotate90CounterClockwise => new Rectangle(
                    rectangle.Y,
                    width - rectangle.Right,
                    rectangle.Height,
                    rectangle.Width),
                OrthogonalTransform.Rotate180 => new Rectangle(
                    width - rectangle.Right,
                    height - rectangle.Bottom,
                    rectangle.Width,
                    rectangle.Height),
                OrthogonalTransform.FlipHorizontal => new Rectangle(
                    width - rectangle.Right,
                    rectangle.Y,
                    rectangle.Width,
                    rectangle.Height),
                OrthogonalTransform.FlipVertical => new Rectangle(
                    rectangle.X,
                    height - rectangle.Bottom,
                    rectangle.Width,
                    rectangle.Height),
                _ => throw new ArgumentOutOfRangeException(nameof(transform))
            };

        /// <summary>
        /// Creates an owned SDR derivative for the existing editor and legacy consumers.
        /// Each display segment retains its captured SDR-white calibration.
        /// </summary>
        public unsafe Bitmap CreateSdrPreview(HdrCaptureSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            HdrRgba16FloatBuffer pixels = MasterPixels;
            Bitmap preview = new Bitmap(pixels.Width, pixels.Height, PixelFormat.Format32bppArgb);

            try
            {
                GpuHdrToSdrToneMapper.Session gpuSession = null;
                GpuHdrToSdrToneMapper.SessionLease gpuSessionLease = null;

                if (settings.ProcessingBackend == HdrProcessingBackend.Gpu)
                {
                    try
                    {
                        gpuSessionLease = GpuHdrToSdrToneMapper.AcquireSharedSession();
                        gpuSession = gpuSessionLease.Session;
                    }
                    catch (Exception exception)
                    {
                        DebugHelper.WriteException(
                            exception,
                            "GPU HDR preview initialization failed. Falling back to CPU tone mapping.");
                    }
                }

                try
                {
                    using (Graphics graphics = Graphics.FromImage(preview))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.Clear(Color.Transparent);

                        fixed (byte* sourceBase = pixels.GetWritablePixelSpan())
                        {
                            RenderSdrSegments(graphics, sourceBase, pixels, settings, ref gpuSession);
                        }
                    }
                }
                finally
                {
                    gpuSessionLease?.Dispose();
                }

                return preview;
            }
            catch
            {
                preview.Dispose();
                throw;
            }
        }

        private unsafe void RenderSdrSegments(
            Graphics graphics,
            byte* sourceBase,
            HdrRgba16FloatBuffer pixels,
            HdrCaptureSettings settings,
            ref GpuHdrToSdrToneMapper.Session gpuSession)
        {
            for (int segmentIndex = 0; segmentIndex < sourceSegments.Count; segmentIndex++)
            {
                RenderSdrSegment(
                    graphics,
                    sourceBase,
                    pixels,
                    settings,
                    segmentIndex,
                    sourceSegments[segmentIndex],
                    ref gpuSession);
            }
        }

        private unsafe void RenderSdrSegment(
            Graphics graphics,
            byte* sourceBase,
            HdrRgba16FloatBuffer pixels,
            HdrCaptureSettings settings,
            int segmentIndex,
            HdrCaptureSourceSegment segment,
            ref GpuHdrToSdrToneMapper.Session gpuSession)
        {
            Rectangle rectangle = segment.DestinationRectangle;
            IntPtr segmentSource = (IntPtr)(sourceBase +
                rectangle.Y * pixels.RowBytes +
                rectangle.X * HdrRgba16FloatBuffer.BytesPerPixel);
            IReadOnlyList<HdrWindowRegion> segmentWindowRegions = segment.WasHdrActive
                ? GetSegmentWindowRegions(rectangle)
                : null;
            PreviewAnalysisCacheKey? analysisKey = segment.WasHdrActive
                ? CreatePreviewAnalysisCacheKey(segmentIndex, rectangle, segment, settings)
                : null;
            HdrToSdrToneMapper.ToneMapInputAnalysis? analysis = null;
            if (analysisKey.HasValue &&
                TryGetPreviewAnalysis(analysisKey.Value, segment, out HdrToSdrToneMapper.ToneMapInputAnalysis cached))
            {
                analysis = cached;
            }

            Bitmap segmentPreview = null;
            bool usedGpu = false;
            Stopwatch segmentTimer = Stopwatch.StartNew();
            Stopwatch toneMapTimer = Stopwatch.StartNew();

            if (gpuSession != null)
            {
                try
                {
                    if (!segment.WasHdrActive)
                    {
                        segmentPreview = gpuSession.ToneMapKnownSdr(
                            segmentSource,
                            pixels.RowBytes,
                            rectangle.Width,
                            rectangle.Height,
                            segment.SdrWhiteNits,
                            preserveAlpha: true);
                    }
                    else if (analysis.HasValue)
                    {
                        HdrToSdrToneMapper.LogAnalysis(
                            "GPU",
                            settings,
                            analysis.Value,
                            segment.DisplayPeakNits);
                        segmentPreview = gpuSession.ToneMapAnalyzed(
                                segmentSource,
                                pixels.RowBytes,
                                rectangle.Width,
                                rectangle.Height,
                                analysis.Value,
                                preserveAlpha: true);
                    }
                    else
                    {
                        segmentPreview = gpuSession.ToneMap(
                            segmentSource,
                            pixels.RowBytes,
                            rectangle.Width,
                            rectangle.Height,
                            settings,
                            segment.SdrWhiteNits,
                            segment.DisplayPeakNits,
                            preserveAlpha: true,
                            segmentWindowRegions,
                            out HdrToSdrToneMapper.ToneMapInputAnalysis gpuAnalysis);
                        analysis = gpuAnalysis;
                        StorePreviewAnalysis(analysisKey.Value, gpuAnalysis, segment);
                    }
                    usedGpu = true;
                }
                catch (Exception exception)
                {
                    DebugHelper.WriteException(
                        exception,
                        "GPU HDR preview failed. Falling back to CPU tone mapping.");
                    gpuSession.Dispose();
                    gpuSession = null;
                }
            }

            if (segmentPreview == null && segment.WasHdrActive && !analysis.HasValue)
            {
                analysis = GetOrCreatePreviewAnalysis(
                    segmentIndex,
                    segmentSource,
                    pixels.RowBytes,
                    rectangle,
                    segment,
                    settings,
                    segmentWindowRegions);
            }

            if (segmentPreview == null && segment.WasHdrActive && analysis.HasValue)
            {
                HdrToSdrToneMapper.LogAnalysis(
                    "CPU",
                    settings,
                    analysis.Value,
                    segment.DisplayPeakNits);
            }

            segmentPreview ??= segment.WasHdrActive
                ? analysis.HasValue
                    ? HdrToSdrToneMapper.ToneMapRgba16FloatAnalyzed(
                        segmentSource,
                        pixels.RowBytes,
                        rectangle.Width,
                        rectangle.Height,
                        analysis.Value,
                        preserveAlpha: true)
                    : HdrToSdrToneMapper.ToneMapRgba16Float(
                    segmentSource,
                    pixels.RowBytes,
                    rectangle.Width,
                    rectangle.Height,
                    settings,
                    segment.SdrWhiteNits,
                    segment.DisplayPeakNits,
                    preserveAlpha: true,
                    windowRegions: segmentWindowRegions)
                : HdrToSdrToneMapper.ToneMapKnownSdrRgba16Float(
                    segmentSource,
                    pixels.RowBytes,
                    rectangle.Width,
                    rectangle.Height,
                    segment.SdrWhiteNits,
                    preserveAlpha: true);
            toneMapTimer.Stop();

            Stopwatch gdiCompositionTimer = Stopwatch.StartNew();
            using (segmentPreview)
            {
                graphics.DrawImageUnscaled(segmentPreview, rectangle.Location);
            }
            gdiCompositionTimer.Stop();

            segmentTimer.Stop();
            DebugHelper.WriteLine(
                $"HDR preview segment | display={segment.DisplayDeviceName} hdr={segment.WasHdrActive} " +
                $"backend={(usedGpu ? "GPU" : "CPU")} size={rectangle.Width}x{rectangle.Height} " +
                $"toneMapMs={toneMapTimer.Elapsed.TotalMilliseconds:F1} " +
                $"gdiCompositionMs={gdiCompositionTimer.Elapsed.TotalMilliseconds:F1} " +
                $"elapsedMs={segmentTimer.Elapsed.TotalMilliseconds:F1}");
        }

        private HdrToSdrToneMapper.ToneMapInputAnalysis GetOrCreatePreviewAnalysis(
            int segmentIndex,
            IntPtr source,
            int sourceRowPitch,
            Rectangle rectangle,
            HdrCaptureSourceSegment segment,
            HdrCaptureSettings settings,
            IReadOnlyList<HdrWindowRegion> segmentWindowRegions)
        {
            PreviewAnalysisCacheKey key = CreatePreviewAnalysisCacheKey(
                segmentIndex,
                rectangle,
                segment,
                settings);
            if (TryGetPreviewAnalysis(
                key,
                segment,
                out HdrToSdrToneMapper.ToneMapInputAnalysis cached))
            {
                return cached;
            }

            HdrToSdrToneMapper.ToneMapInputAnalysis analysis =
                HdrToSdrToneMapper.AnalyzeToneMapInput(
                    source,
                    sourceRowPitch,
                    rectangle.Width,
                    rectangle.Height,
                    settings,
                    segment.SdrWhiteNits,
                    segment.DisplayPeakNits,
                    preserveAlpha: true,
                    segmentWindowRegions);

            StorePreviewAnalysis(key, analysis, segment);
            return analysis;
        }

        private PreviewAnalysisCacheKey CreatePreviewAnalysisCacheKey(
            int segmentIndex,
            Rectangle rectangle,
            HdrCaptureSourceSegment segment,
            HdrCaptureSettings settings) => new PreviewAnalysisCacheKey(
                Revision,
                previewAnalysisGeneration,
                segmentIndex,
                rectangle,
                segment.SdrWhiteNits,
                segment.DisplayPeakNits,
                settings.HdrBrightnessNits,
                settings.PeakBrightnessMode,
                settings.ToneMappingMode,
                settings.PaperWhiteMode,
                settings.PaperWhiteNits);

        private bool TryGetPreviewAnalysis(
            PreviewAnalysisCacheKey key,
            HdrCaptureSourceSegment segment,
            out HdrToSdrToneMapper.ToneMapInputAnalysis analysis)
        {
            lock (previewAnalysisSync)
            {
                PreparePreviewAnalysisCache(key);
                bool hit = previewAnalysisCache.TryGetValue(key, out analysis);
                if (hit)
                {
                    DebugHelper.WriteLine(
                        $"HDR preview analysis cache | display={segment.DisplayDeviceName} hit=True");
                }
                return hit;
            }
        }

        private void StorePreviewAnalysis(
            PreviewAnalysisCacheKey key,
            HdrToSdrToneMapper.ToneMapInputAnalysis analysis,
            HdrCaptureSourceSegment segment)
        {
            lock (previewAnalysisSync)
            {
                PreparePreviewAnalysisCache(key);
                int maximumEntries = Math.Max(
                    MinimumPreviewAnalysisCacheEntries,
                    sourceSegments.Count * 2);
                if (previewAnalysisCache.Count >= maximumEntries &&
                    !previewAnalysisCache.ContainsKey(key))
                {
                    previewAnalysisCache.Clear();
                }
                previewAnalysisCache[key] = analysis;
            }

            DebugHelper.WriteLine(
                $"HDR preview analysis cache | display={segment.DisplayDeviceName} hit=False");
        }

        private void PreparePreviewAnalysisCache(PreviewAnalysisCacheKey key)
        {
            if (previewAnalysisCacheRevision != key.DocumentRevision ||
                previewAnalysisCacheGeneration != key.Generation)
            {
                previewAnalysisCache.Clear();
                previewAnalysisCacheRevision = key.DocumentRevision;
                previewAnalysisCacheGeneration = key.Generation;
            }
        }

        private void InvalidatePreviewAnalysisCache()
        {
            lock (previewAnalysisSync)
            {
                previewAnalysisGeneration = checked(previewAnalysisGeneration + 1);
                previewAnalysisCache.Clear();
                previewAnalysisCacheGeneration = previewAnalysisGeneration;
                previewAnalysisCacheRevision = Revision;
            }
        }

        private readonly record struct PreviewAnalysisCacheKey(
            int DocumentRevision,
            int Generation,
            int SegmentIndex,
            Rectangle Rectangle,
            float SdrWhiteNits,
            float DisplayPeakNits,
            float ConfiguredPeakNits,
            HdrPeakBrightnessMode PeakBrightnessMode,
            HdrToneMappingMode ToneMappingMode,
            HdrPaperWhiteMode PaperWhiteMode,
            float PaperWhiteNits);

        private IReadOnlyList<HdrWindowRegion> GetSegmentWindowRegions(Rectangle segmentRectangle)
        {
            var result = new List<HdrWindowRegion>();

            foreach (HdrWindowRegion windowRegion in windowRegions)
            {
                Rectangle intersection = Rectangle.Intersect(
                    windowRegion.Bounds,
                    segmentRectangle);
                if (intersection.Width <= 0 || intersection.Height <= 0)
                {
                    continue;
                }

                Rectangle contentIntersection = Rectangle.Intersect(
                    windowRegion.ContentBounds,
                    segmentRectangle);
                intersection.Offset(-segmentRectangle.X, -segmentRectangle.Y);
                if (contentIntersection.Width > 0 && contentIntersection.Height > 0)
                {
                    contentIntersection.Offset(-segmentRectangle.X, -segmentRectangle.Y);
                }
                else
                {
                    contentIntersection = Rectangle.Empty;
                }
                result.Add(windowRegion.WithBounds(intersection, contentIntersection));
            }

            return result;
        }

        /// <summary>
        /// Applies the editor Alpha adjustment while preserving premultiplied
        /// linear scRGB. RGB and alpha must be scaled together; changing only
        /// alpha would leave invalid premultiplied pixels and bright fringes.
        /// </summary>
        public void ApplyAlphaAdjustment(float percent)
        {
            if (!float.IsFinite(percent) || percent < 0f || percent > 100f)
            {
                throw new ArgumentOutOfRangeException(nameof(percent));
            }

            float scale = percent / 100f;
            HdrRgba16FloatBuffer pixels = MasterPixels;
            Parallel.For(0, pixels.Height, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    WriteHalf(row, offset, ReadHalf(row, offset) * scale);
                    WriteHalf(row, offset + 2, ReadHalf(row, offset + 2) * scale);
                    WriteHalf(row, offset + 4, ReadHalf(row, offset + 4) * scale);
                    WriteHalf(
                        row,
                        offset + 6,
                        Math.Clamp(ReadHalf(row, offset + 6) * scale, 0f, 1f));
                }
            });

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Applies exposure as a linear-light stop gain. Alpha is unchanged and
        /// unclipped HDR values remain representable up to the finite HALF limit.
        /// This intentionally improves on the SDR preview's encoded 8-bit math.
        /// </summary>
        public void ApplyExposureAdjustment(float stops)
        {
            if (!float.IsFinite(stops))
            {
                throw new ArgumentOutOfRangeException(nameof(stops));
            }

            float gain = MathF.Pow(2f, Math.Clamp(stops, -10f, 10f));
            HdrRgba16FloatBuffer pixels = MasterPixels;
            Parallel.For(0, pixels.Height, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    WriteHalf(row, offset, ReadHalf(row, offset) * gain);
                    WriteHalf(row, offset + 2, ReadHalf(row, offset + 2) * gain);
                    WriteHalf(row, offset + 4, ReadHalf(row, offset + 4) * gain);
                }
            });

            Revision = checked(Revision + 1);
        }
        /// <summary>
        /// Adjusts saturation in linear scRGB around Rec.709 luminance. The
        /// homogeneous matrix is valid for premultiplied pixels, preserves alpha,
        /// retains HDR values above one, and permits scRGB negative excursions.
        /// </summary>
        public void ApplySaturationAdjustment(float saturation)
        {
            if (!float.IsFinite(saturation) || saturation < 0f || saturation > 2f)
            {
                throw new ArgumentOutOfRangeException(nameof(saturation));
            }

            const float luminanceRed = 0.2126f;
            const float luminanceGreen = 0.7152f;
            const float luminanceBlue = 0.0722f;
            HdrRgba16FloatBuffer pixels = MasterPixels;

            Parallel.For(0, pixels.Height, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float red = ReadHalf(row, offset);
                    float green = ReadHalf(row, offset + 2);
                    float blue = ReadHalf(row, offset + 4);
                    float luminance =
                        red * luminanceRed +
                        green * luminanceGreen +
                        blue * luminanceBlue;

                    WriteHalf(row, offset, luminance + (red - luminance) * saturation);
                    WriteHalf(row, offset + 2, luminance + (green - luminance) * saturation);
                    WriteHalf(row, offset + 4, luminance + (blue - luminance) * saturation);
                }
            });

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Applies a three-channel affine color transform to straight linear
        /// scRGB while retaining premultiplied storage. The matrix is three rows
        /// of red, green, blue and an SDR-white-relative offset.
        /// </summary>
        public void ApplyLinearColorMatrixAdjustment(float[] matrix, float sdrWhiteNits)
        {
            ArgumentNullException.ThrowIfNull(matrix);
            if (matrix.Length != 12)
            {
                throw new ArgumentException("A finite 3x4 color matrix is required.", nameof(matrix));
            }

            for (int index = 0; index < matrix.Length; index++)
            {
                if (!float.IsFinite(matrix[index]))
                {
                    throw new ArgumentException("A finite 3x4 color matrix is required.", nameof(matrix));
                }
            }

            if (!float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0f || sdrWhiteNits > 10000f)
            {
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
            }

            float[] ownedMatrix = (float[])matrix.Clone();
            float whiteScale = sdrWhiteNits / 80f;
            HdrRgba16FloatBuffer pixels = MasterPixels;
            Parallel.For(0, pixels.Height, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float alpha = Math.Clamp(ReadHalf(row, offset + 6), 0f, 1f);
                    if (alpha <= 0f)
                    {
                        WriteHalf(row, offset, 0f);
                        WriteHalf(row, offset + 2, 0f);
                        WriteHalf(row, offset + 4, 0f);
                        continue;
                    }

                    float inverseAlpha = 1f / alpha;
                    float red = ReadHalf(row, offset) * inverseAlpha;
                    float green = ReadHalf(row, offset + 2) * inverseAlpha;
                    float blue = ReadHalf(row, offset + 4) * inverseAlpha;
                    float transformedRed =
                        ownedMatrix[0] * red + ownedMatrix[1] * green + ownedMatrix[2] * blue + ownedMatrix[3] * whiteScale;
                    float transformedGreen =
                        ownedMatrix[4] * red + ownedMatrix[5] * green + ownedMatrix[6] * blue + ownedMatrix[7] * whiteScale;
                    float transformedBlue =
                        ownedMatrix[8] * red + ownedMatrix[9] * green + ownedMatrix[10] * blue + ownedMatrix[11] * whiteScale;

                    WriteHalf(row, offset, transformedRed * alpha);
                    WriteHalf(row, offset + 2, transformedGreen * alpha);
                    WriteHalf(row, offset + 4, transformedBlue * alpha);
                }
            });

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Applies gamma to straight linear scRGB relative to the configured SDR
        /// white. Sign-preserving power keeps legal negative scRGB excursions.
        /// </summary>
        public void ApplyGammaAdjustment(float gamma, float sdrWhiteNits)
        {
            if (!float.IsFinite(gamma) || gamma < 0.1f || gamma > 5f)
            {
                throw new ArgumentOutOfRangeException(nameof(gamma));
            }

            ApplyStraightRgbAdjustment(
                sdrWhiteNits,
                (red, green, blue, whiteScale) =>
                {
                    float exponent = 1f / gamma;
                    return (
                        ApplySignedPower(red / whiteScale, exponent) * whiteScale,
                        ApplySignedPower(green / whiteScale, exponent) * whiteScale,
                        ApplySignedPower(blue / whiteScale, exponent) * whiteScale);
                });
        }

        /// <summary>
        /// Applies a luminance threshold relative to SDR white. This effect is
        /// intentionally clipping because that is the operation selected by the user.
        /// </summary>
        public void ApplyThresholdAdjustment(float threshold, float sdrWhiteNits)
        {
            if (!float.IsFinite(threshold) || threshold < 0f || threshold > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(threshold));
            }

            ApplyStraightRgbAdjustment(
                sdrWhiteNits,
                (red, green, blue, whiteScale) =>
                {
                    float luminance = red * 0.2126f + green * 0.7152f + blue * 0.0722f;
                    float output = luminance >= threshold * whiteScale ? whiteScale : 0f;
                    return (output, output, output);
                });
        }

        /// <summary>
        /// Boosts less saturated colors more strongly, using straight linear scRGB
        /// and retaining above-white values rather than quantizing through BGRA8.
        /// </summary>
        public void ApplyVibranceAdjustment(float amount, float sdrWhiteNits)
        {
            if (!float.IsFinite(amount) || amount < -1f || amount > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }

            ApplyStraightRgbAdjustment(
                sdrWhiteNits,
                (red, green, blue, whiteScale) =>
                {
                    float maximum = MathF.Max(red, MathF.Max(green, blue));
                    float minimum = MathF.Min(red, MathF.Min(green, blue));
                    float saturation = Math.Clamp((maximum - minimum) / whiteScale, 0f, 1f);
                    float gray = (red + green + blue) / 3f;
                    float factor = amount >= 0f
                        ? 1f + amount * (1f - saturation)
                        : 1f + amount;
                    return (
                        gray + (red - gray) * factor,
                        gray + (green - gray) * factor,
                        gray + (blue - gray) * factor);
                });
        }

        private void ApplyStraightRgbAdjustment(
            float sdrWhiteNits,
            Func<float, float, float, float, (float Red, float Green, float Blue)> transform)
        {
            if (!float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0f || sdrWhiteNits > 10000f)
            {
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
            }

            ArgumentNullException.ThrowIfNull(transform);
            float whiteScale = sdrWhiteNits / 80f;
            HdrRgba16FloatBuffer pixels = MasterPixels;
            Parallel.For(0, pixels.Height, y =>
            {
                Span<byte> row = pixels.GetWritableRowSpan(y);
                for (int x = 0; x < pixels.Width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float alpha = Math.Clamp(ReadHalf(row, offset + 6), 0f, 1f);
                    if (alpha <= 0f)
                    {
                        WriteHalf(row, offset, 0f);
                        WriteHalf(row, offset + 2, 0f);
                        WriteHalf(row, offset + 4, 0f);
                        continue;
                    }

                    float inverseAlpha = 1f / alpha;
                    (float red, float green, float blue) = transform(
                        ReadHalf(row, offset) * inverseAlpha,
                        ReadHalf(row, offset + 2) * inverseAlpha,
                        ReadHalf(row, offset + 4) * inverseAlpha,
                        whiteScale);
                    WriteHalf(row, offset, red * alpha);
                    WriteHalf(row, offset + 2, green * alpha);
                    WriteHalf(row, offset + 4, blue * alpha);
                }
            });

            Revision = checked(Revision + 1);
        }

        private static float ApplySignedPower(float value, float exponent) =>
            MathF.CopySign(MathF.Pow(MathF.Abs(value), exponent), value);


        public void CompositeSdrAnnotationOverlay(
            ReadOnlySpan<byte> overlayBgra8,
            int overlayRowBytes,
            float annotationWhiteNits)
        {
            HdrRgba16FloatBuffer pixels = MasterPixels;
            SdrAnnotationOverlayCompositor.CompositeBgra8Premultiplied(
                pixels.GetWritablePixelSpan(),
                pixels.RowBytes,
                overlayBgra8,
                overlayRowBytes,
                pixels.Width,
                pixels.Height,
                annotationWhiteNits);
            Revision++;
        }

        public unsafe void CompositeSdrAnnotationOverlay(
            Bitmap overlay,
            float annotationWhiteNits)
        {
            ArgumentNullException.ThrowIfNull(overlay);

            if (overlay.Width != MasterPixels.Width || overlay.Height != MasterPixels.Height)
            {
                throw new ArgumentException(
                    "The annotation overlay dimensions must match the HDR document.",
                    nameof(overlay));
            }

            Bitmap convertedOverlay = null;
            Bitmap readableOverlay = overlay;
            if (overlay.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                convertedOverlay = new Bitmap(overlay.Width, overlay.Height, PixelFormat.Format32bppPArgb);
                using (Graphics graphics = Graphics.FromImage(convertedOverlay))
                {
                    graphics.DrawImageUnscaled(overlay, Point.Empty);
                }

                readableOverlay = convertedOverlay;
            }

            BitmapData bitmapData = null;
            try
            {
                bitmapData = readableOverlay.LockBits(
                    new Rectangle(0, 0, readableOverlay.Width, readableOverlay.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);

                if (bitmapData.Stride <= 0)
                {
                    throw new InvalidOperationException("The annotation overlay has an unsupported row layout.");
                }

                int byteLength = checked(
                    (readableOverlay.Height - 1) * bitmapData.Stride + readableOverlay.Width * 4);
                CompositeSdrAnnotationOverlay(
                    new ReadOnlySpan<byte>(bitmapData.Scan0.ToPointer(), byteLength),
                    bitmapData.Stride,
                    annotationWhiteNits);
            }
            finally
            {
                if (bitmapData != null)
                {
                    readableOverlay.UnlockBits(bitmapData);
                }

                convertedOverlay?.Dispose();
            }
        }

        public void CompositeSdrAsset(
            ReadOnlySpan<byte> straightBgra8,
            int assetRowBytes,
            int assetWidth,
            int assetHeight,
            int destinationX,
            int destinationY,
            float assetWhiteNits)
        {
            HdrRgba16FloatBuffer pixels = MasterPixels;
            SdrAnnotationOverlayCompositor.CompositeBgra8Straight(
                pixels.GetWritablePixelSpan(),
                pixels.RowBytes,
                pixels.Width,
                pixels.Height,
                straightBgra8,
                assetRowBytes,
                assetWidth,
                assetHeight,
                destinationX,
                destinationY,
                assetWhiteNits);
            Revision++;
        }

        /// <summary>
        /// Creates a retained FP16 document from an ordinary SDR bitmap. This is
        /// intended for SDR-only decorations such as editor chrome, backgrounds,
        /// shadows and margins which are later combined with an HDR source.
        /// </summary>
        public static HdrImageDocument CreateFromSdrBitmap(Bitmap bitmap, float sdrWhiteNits)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            if (!float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0f || sdrWhiteNits > 10000f)
            {
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
            }

            var pixels = new HdrRgba16FloatBuffer(bitmap.Width, bitmap.Height);
            var document = new HdrImageDocument(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                pixels,
                new[]
                {
                    new HdrCaptureSourceSegment(
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        string.Empty,
                        false,
                        sdrWhiteNits,
                        sdrWhiteNits)
                });

            try
            {
                document.CompositeSdrAnnotationOverlay(bitmap, sdrWhiteNits);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Multiplies premultiplied FP16 pixels by an 8-bit bitmap alpha mask.
        /// </summary>
        public unsafe void ApplyAlphaMask(Bitmap mask)
        {
            ArgumentNullException.ThrowIfNull(mask);
            if (mask.Width != MasterPixels.Width || mask.Height != MasterPixels.Height)
            {
                throw new ArgumentException("The alpha mask dimensions must match the HDR document.", nameof(mask));
            }

            Bitmap convertedMask = null;
            Bitmap readableMask = mask;
            if (mask.PixelFormat != PixelFormat.Format32bppArgb &&
                mask.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                convertedMask = new Bitmap(mask.Width, mask.Height, PixelFormat.Format32bppArgb);
                using Graphics graphics = Graphics.FromImage(convertedMask);
                graphics.DrawImageUnscaled(mask, Point.Empty);
                readableMask = convertedMask;
            }

            BitmapData bitmapData = null;
            try
            {
                bitmapData = readableMask.LockBits(
                    new Rectangle(0, 0, readableMask.Width, readableMask.Height),
                    ImageLockMode.ReadOnly,
                    readableMask.PixelFormat);
                if (bitmapData.Stride <= 0)
                {
                    throw new InvalidOperationException("The alpha mask has an unsupported row layout.");
                }

                HdrRgba16FloatBuffer pixels = MasterPixels;
                Parallel.For(0, pixels.Height, y =>
                {
                    ReadOnlySpan<byte> maskRow = new ReadOnlySpan<byte>(
                        (byte*)bitmapData.Scan0 + y * bitmapData.Stride,
                        pixels.Width * 4);
                    Span<byte> destinationRow = pixels.GetWritableRowSpan(y);
                    for (int x = 0; x < pixels.Width; x++)
                    {
                        float coverage = maskRow[x * 4 + 3] / 255f;
                        int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                        WriteHalf(destinationRow, offset, ReadHalf(destinationRow, offset) * coverage);
                        WriteHalf(destinationRow, offset + 2, ReadHalf(destinationRow, offset + 2) * coverage);
                        WriteHalf(destinationRow, offset + 4, ReadHalf(destinationRow, offset + 4) * coverage);
                        WriteHalf(destinationRow, offset + 6, ReadHalf(destinationRow, offset + 6) * coverage);
                    }
                });
            }
            finally
            {
                if (bitmapData != null)
                {
                    readableMask.UnlockBits(bitmapData);
                }

                convertedMask?.Dispose();
            }

            Revision = checked(Revision + 1);
        }

        /// <summary>
        /// Source-over composites another premultiplied FP16 document at a local
        /// destination offset without converting either document through SDR.
        /// </summary>
        public void CompositeHdrDocument(HdrImageDocument source, int destinationX, int destinationY)
        {
            ArgumentNullException.ThrowIfNull(source);
            Rectangle destinationBounds = new Rectangle(
                destinationX,
                destinationY,
                source.MasterPixels.Width,
                source.MasterPixels.Height);
            Rectangle intersection = Rectangle.Intersect(
                new Rectangle(0, 0, MasterPixels.Width, MasterPixels.Height),
                destinationBounds);
            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                return;
            }

            int sourceStartX = intersection.X - destinationX;
            int sourceStartY = intersection.Y - destinationY;
            Parallel.For(0, intersection.Height, relativeY =>
            {
                ReadOnlySpan<byte> sourceRow = source.MasterPixels.GetRowSpan(sourceStartY + relativeY);
                Span<byte> destinationRow = MasterPixels.GetWritableRowSpan(intersection.Y + relativeY);
                for (int relativeX = 0; relativeX < intersection.Width; relativeX++)
                {
                    int sourceOffset = (sourceStartX + relativeX) * HdrRgba16FloatBuffer.BytesPerPixel;
                    int destinationOffset = (intersection.X + relativeX) * HdrRgba16FloatBuffer.BytesPerPixel;
                    float sourceAlpha = Math.Clamp(ReadHalf(sourceRow, sourceOffset + 6), 0f, 1f);
                    float inverseSourceAlpha = 1f - sourceAlpha;
                    WriteHalf(
                        destinationRow,
                        destinationOffset,
                        ReadHalf(sourceRow, sourceOffset) + ReadHalf(destinationRow, destinationOffset) * inverseSourceAlpha);
                    WriteHalf(
                        destinationRow,
                        destinationOffset + 2,
                        ReadHalf(sourceRow, sourceOffset + 2) + ReadHalf(destinationRow, destinationOffset + 2) * inverseSourceAlpha);
                    WriteHalf(
                        destinationRow,
                        destinationOffset + 4,
                        ReadHalf(sourceRow, sourceOffset + 4) + ReadHalf(destinationRow, destinationOffset + 4) * inverseSourceAlpha);
                    WriteHalf(
                        destinationRow,
                        destinationOffset + 6,
                        sourceAlpha + Math.Clamp(ReadHalf(destinationRow, destinationOffset + 6), 0f, 1f) * inverseSourceAlpha);
                }
            });

            foreach (HdrCaptureSourceSegment segment in source.SourceSegments)
            {
                Rectangle adjusted = segment.DestinationRectangle;
                adjusted.Offset(destinationX, destinationY);
                adjusted = Rectangle.Intersect(adjusted, intersection);
                if (adjusted.Width > 0 && adjusted.Height > 0)
                {
                    sourceSegments.Add(new HdrCaptureSourceSegment(
                        adjusted,
                        segment.DisplayDeviceName,
                        segment.WasHdrActive,
                        segment.SdrWhiteNits,
                        segment.DisplayPeakNits));
                }
            }

            CaptureTimestamp = source.CaptureTimestamp;
            Revision = checked(Revision + 1);
        }

        public void Dispose()
        {
            masterPixels?.Dispose();
            masterPixels = null;
            sourceSegments.Clear();
            lock (previewAnalysisSync)
            {
                previewAnalysisCache.Clear();
            }
        }
    }
}
