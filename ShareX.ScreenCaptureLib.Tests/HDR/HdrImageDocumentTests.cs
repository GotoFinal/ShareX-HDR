using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Drawing;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrImageDocumentTests
{
    [Fact]
    public void CropToScreenRectangle_CopiesExactPixelsAndRebasesDisplaySegments()
    {
        const int width = 3;
        const int height = 2;
        byte[] sourceBytes = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WriteHalf(sourceBytes, (y * width + x) * 8, y * 10 + x);
                WriteHalf(sourceBytes, (y * width + x) * 8 + 6, 1f);
            }
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        using var document = new HdrImageDocument(
            new Rectangle(-100, 50, width, height),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, 2, 2), "HDR", true, 203f, 1000f),
                new HdrCaptureSourceSegment(new Rectangle(2, 0, 1, 2), "SDR", false, 80f, 80f)
            });

        DateTimeOffset captureTimestamp = document.CaptureTimestamp;
        using HdrImageDocument cropped = document.CropToScreenRectangle(
            new Rectangle(-99, 50, 2, 2));

        Assert.Equal(captureTimestamp, cropped.CaptureTimestamp);
        Assert.Equal(document.Revision + 1, cropped.Revision);
        Assert.Equal(new Rectangle(-99, 50, 2, 2), cropped.RequestedBounds);
        Assert.Equal(2, cropped.MasterPixels.Width);
        Assert.Equal(2, cropped.MasterPixels.Height);
        Assert.Equal(1f, ReadHalf(cropped.MasterPixels.GetRowSpan(0), 0));
        Assert.Equal(2f, ReadHalf(cropped.MasterPixels.GetRowSpan(0), 8));
        Assert.Equal(11f, ReadHalf(cropped.MasterPixels.GetRowSpan(1), 0));
        Assert.Equal(12f, ReadHalf(cropped.MasterPixels.GetRowSpan(1), 8));
        Assert.Collection(
            cropped.SourceSegments,
            segment => Assert.Equal(new Rectangle(0, 0, 1, 2), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(1, 0, 1, 2), segment.DestinationRectangle));
    }

    [Fact]
    public void CropToScreenRectangle_RejectsNonIntersectingRectangle()
    {
        using var pixels = new HdrRgba16FloatBuffer(1, 1);
        using var document = new HdrImageDocument(
            new Rectangle(10, 20, 1, 1),
            pixels.Clone(),
            Array.Empty<HdrCaptureSourceSegment>());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.CropToScreenRectangle(new Rectangle(0, 0, 1, 1)));
    }

    [Theory]
    [InlineData(EditorTransform.Rotate90Clockwise, 2, 3, 10, 0, 11, 1, 12, 2)]
    [InlineData(EditorTransform.Rotate90CounterClockwise, 2, 3, 2, 12, 1, 11, 0, 10)]
    [InlineData(EditorTransform.Rotate180, 3, 2, 12, 11, 10, 2, 1, 0)]
    [InlineData(EditorTransform.FlipHorizontal, 3, 2, 2, 1, 0, 12, 11, 10)]
    [InlineData(EditorTransform.FlipVertical, 3, 2, 10, 11, 12, 0, 1, 2)]
    public void OrthogonalTransforms_CopyExactPixelsAndPreserveMetadata(
        EditorTransform transform,
        int expectedWidth,
        int expectedHeight,
        params int[] expectedValues)
    {
        using HdrImageDocument document = CreatePatternDocument();
        DateTimeOffset timestamp = document.CaptureTimestamp;
        using HdrImageDocument transformed = transform switch
        {
            EditorTransform.Rotate90Clockwise => document.Rotate90Clockwise(),
            EditorTransform.Rotate90CounterClockwise => document.Rotate90CounterClockwise(),
            EditorTransform.Rotate180 => document.Rotate180(),
            EditorTransform.FlipHorizontal => document.FlipHorizontal(),
            EditorTransform.FlipVertical => document.FlipVertical(),
            _ => throw new ArgumentOutOfRangeException(nameof(transform))
        };

        Assert.Equal(expectedWidth, transformed.MasterPixels.Width);
        Assert.Equal(expectedHeight, transformed.MasterPixels.Height);
        Assert.Equal(timestamp, transformed.CaptureTimestamp);
        Assert.Equal(document.Revision + 1, transformed.Revision);

        int valueIndex = 0;
        for (int y = 0; y < expectedHeight; y++)
        {
            for (int x = 0; x < expectedWidth; x++)
            {
                Assert.Equal(
                    expectedValues[valueIndex++],
                    ReadHalf(transformed.MasterPixels.GetRowSpan(y), x * 8));
            }
        }
    }

    [Fact]
    public void Rotate90Clockwise_TransformsDisplaySegments()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using HdrImageDocument transformed = document.Rotate90Clockwise();

        Assert.Collection(
            transformed.SourceSegments,
            segment => Assert.Equal(new Rectangle(1, 0, 1, 2), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(0, 0, 1, 3), segment.DestinationRectangle));
    }

    [Fact]
    public void CropToLocalRectangle_UsesCurrentDocumentCoordinates()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using HdrImageDocument rotated = document.Rotate90Clockwise();
        using HdrImageDocument cropped = rotated.CropToLocalRectangle(new Rectangle(0, 1, 1, 2));

        Assert.Equal(11f, ReadHalf(cropped.MasterPixels.GetRowSpan(0), 0));
        Assert.Equal(12f, ReadHalf(cropped.MasterPixels.GetRowSpan(1), 0));
        Assert.Equal(rotated.Revision + 1, cropped.Revision);
    }

    [Fact]
    public void CutOutVertical_RemovesExactColumnsAndSplitsDisplaySegments()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using HdrImageDocument cut = document.CutOutVertical(1, 1);

        Assert.Equal(2, cut.MasterPixels.Width);
        Assert.Equal(2, cut.MasterPixels.Height);
        Assert.Equal(0f, ReadHalf(cut.MasterPixels.GetRowSpan(0), 0));
        Assert.Equal(2f, ReadHalf(cut.MasterPixels.GetRowSpan(0), 8));
        Assert.Equal(10f, ReadHalf(cut.MasterPixels.GetRowSpan(1), 0));
        Assert.Equal(12f, ReadHalf(cut.MasterPixels.GetRowSpan(1), 8));
        Assert.Collection(
            cut.SourceSegments,
            segment => Assert.Equal(new Rectangle(0, 0, 1, 1), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(0, 1, 1, 1), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(1, 1, 1, 1), segment.DestinationRectangle));
    }

    [Fact]
    public void CutOutHorizontal_RemovesExactRows()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using HdrImageDocument cut = document.CutOutHorizontal(0, 1);

        Assert.Equal(3, cut.MasterPixels.Width);
        Assert.Equal(1, cut.MasterPixels.Height);
        Assert.Equal(10f, ReadHalf(cut.MasterPixels.GetRowSpan(0), 0));
        Assert.Equal(11f, ReadHalf(cut.MasterPixels.GetRowSpan(0), 8));
        Assert.Equal(12f, ReadHalf(cut.MasterPixels.GetRowSpan(0), 16));
        Assert.Equal(document.Revision + 1, cut.Revision);
        Assert.Collection(
            cut.SourceSegments,
            segment => Assert.Equal(new Rectangle(0, 0, 3, 1), segment.DestinationRectangle));
    }

    [Fact]
    public void ResizeCatmullRom_InterpolatesLinearHdrAndScalesSegments()
    {
        byte[] sourceBytes = new byte[2 * HdrRgba16FloatBuffer.BytesPerPixel];
        WriteHalf(sourceBytes, 0, 0f);
        WriteHalf(sourceBytes, 6, 1f);
        WriteHalf(sourceBytes, 8, 4f);
        WriteHalf(sourceBytes, 14, 1f);
        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            2,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(-10, 20, 2, 1),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, 1, 1), "HDR", true, 203f, 1000f),
                new HdrCaptureSourceSegment(new Rectangle(1, 0, 1, 1), "SDR", false, 80f, 80f)
            });
        DateTimeOffset timestamp = document.CaptureTimestamp;

        using HdrImageDocument resized = document.ResizeCatmullRom(3, 1);

        Assert.Equal(3, resized.MasterPixels.Width);
        Assert.Equal(1, resized.MasterPixels.Height);
        Assert.Equal(2f, ReadHalf(resized.MasterPixels.GetRowSpan(0), 8));
        Assert.Equal(timestamp, resized.CaptureTimestamp);
        Assert.Equal(document.Revision + 1, resized.Revision);
        Assert.Equal(new Rectangle(-10, 20, 3, 1), resized.RequestedBounds);
        Assert.Collection(
            resized.SourceSegments,
            segment => Assert.Equal(new Rectangle(0, 0, 2, 1), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(2, 0, 1, 1), segment.DestinationRectangle));
    }

    [Fact]
    public void ResizeCanvas_ReplaysSignedMarginsInLinearPremultipliedFp16()
    {
        using HdrImageDocument document = CreatePatternDocument();
        DateTimeOffset timestamp = document.CaptureTimestamp;

        using HdrImageDocument resized = document.ResizeCanvas(
            top: 1,
            right: 2,
            bottom: -1,
            left: -1,
            red: 255,
            green: 0,
            blue: 0,
            alpha: 128,
            backgroundWhiteNits: 80f);

        Assert.Equal(4, resized.MasterPixels.Width);
        Assert.Equal(2, resized.MasterPixels.Height);
        Assert.Equal(timestamp, resized.CaptureTimestamp);
        Assert.Equal(document.Revision + 1, resized.Revision);
        Assert.Equal(new Rectangle(-100, 50, 4, 2), resized.RequestedBounds);

        float expectedAlpha = 128f / 255f;
        Assert.Equal(expectedAlpha, ReadHalf(resized.MasterPixels.GetRowSpan(0), 0), 3);
        Assert.Equal(expectedAlpha, ReadHalf(resized.MasterPixels.GetRowSpan(0), 6), 3);
        Assert.Equal(1f, ReadHalf(resized.MasterPixels.GetRowSpan(1), 0));
        Assert.Equal(2f, ReadHalf(resized.MasterPixels.GetRowSpan(1), 8));
        Assert.Equal(expectedAlpha, ReadHalf(resized.MasterPixels.GetRowSpan(1), 16), 3);

        Assert.Collection(
            resized.SourceSegments,
            segment => Assert.Equal(new Rectangle(0, 1, 1, 1), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(0, 0, 4, 1), segment.DestinationRectangle),
            segment => Assert.Equal(new Rectangle(2, 1, 2, 1), segment.DestinationRectangle));
    }

    [Fact]
    public void CreateSdrPreview_PreservesTransparentCanvasAlpha()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using HdrImageDocument resized = document.ResizeCanvas(
            top: 1,
            right: 0,
            bottom: 0,
            left: 0,
            red: 0,
            green: 0,
            blue: 0,
            alpha: 0,
            backgroundWhiteNits: 80f);
        using Bitmap preview = resized.CreateSdrPreview(new HdrCaptureSettings());

        Assert.Equal(0, preview.GetPixel(0, 0).A);
        Assert.Equal(255, preview.GetPixel(0, 1).A);
    }

    [Fact]
    public void CreateSdrPreview_ReusedAnalysisMatchesFirstPreview()
    {
        using HdrImageDocument document = CreatePatternDocument();
        var settings = new HdrCaptureSettings
        {
            ProcessingBackend = HdrProcessingBackend.Gpu
        };

        using Bitmap first = document.CreateSdrPreview(settings);
        using Bitmap second = document.CreateSdrPreview(settings);

        AssertBitmapsEqual(first, second);
    }

    [Fact]
    public void CreateSdrPreview_PixelRevisionInvalidatesReusedAnalysis()
    {
        using HdrImageDocument document = CreatePatternDocument();
        var settings = new HdrCaptureSettings
        {
            ProcessingBackend = HdrProcessingBackend.Gpu
        };

        using Bitmap before = document.CreateSdrPreview(settings);
        document.ApplyExposureAdjustment(1f);
        using Bitmap after = document.CreateSdrPreview(settings);

        Assert.NotEqual(before.GetPixel(1, 0), after.GetPixel(1, 0));
    }

    [Fact]
    public void ResizeCanvas_RejectsNonPositiveResultDimensions()
    {
        using HdrImageDocument document = CreatePatternDocument();

        Assert.Throws<ArgumentException>(() => document.ResizeCanvas(
            0, -3, 0, 0, 0, 0, 0, 0, 80f));
    }

    [Fact]
    public void PixelateRectangle_UsesCanvasAnchoredLinearHdrBlocks()
    {
        const int width = 4;
        const int height = 4;
        byte[] sourceBytes = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(sourceBytes, offset, y * 10 + x);
                WriteHalf(sourceBytes, offset + 6, 1f);
            }
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, width, height),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, width, height), "HDR", true, 203f, 1000f)
            });

        document.PixelateRectangle(new Rectangle(1, 1, 2, 2), pixelSize: 2);

        Assert.Equal(0f, ReadHalf(document.MasterPixels.GetRowSpan(0), 0));
        Assert.Equal(5.5f, ReadHalf(document.MasterPixels.GetRowSpan(1), 8));
        Assert.Equal(7.5f, ReadHalf(document.MasterPixels.GetRowSpan(1), 16));
        Assert.Equal(25.5f, ReadHalf(document.MasterPixels.GetRowSpan(2), 8));
        Assert.Equal(27.5f, ReadHalf(document.MasterPixels.GetRowSpan(2), 16));
        Assert.Equal(33f, ReadHalf(document.MasterPixels.GetRowSpan(3), 24));
        Assert.Equal(1, document.Revision);
        Assert.Equal(new Rectangle(0, 0, width, height), Assert.Single(document.SourceSegments).DestinationRectangle);
    }

    [Fact]
    public void PixelateRectangle_RejectsInvalidGeometryAndStrength()
    {
        using HdrImageDocument document = CreatePatternDocument();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.PixelateRectangle(Rectangle.Empty, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.PixelateRectangle(new Rectangle(0, 0, 1, 1), 0));
    }

    [Fact]
    public void CompositeCurrentEffectThroughCoverage_RestoresZeroAndBlendsSourceOverEffect()
    {
        byte[] sourceBytes = new byte[2 * HdrRgba16FloatBuffer.BytesPerPixel];
        WritePixel(sourceBytes, 0, 1f, 0.5f, 0.25f, 0.5f);
        WritePixel(sourceBytes, 8, 1f, 0.5f, 0.25f, 0.5f);
        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            2,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, 2, 1),
            pixels.Clone(),
            Array.Empty<HdrCaptureSourceSegment>());
        using HdrRgba16FloatBuffer original = document.MasterPixels.Clone();

        Span<byte> mutated = document.MasterPixels.GetWritableRowSpan(0);
        WritePixel(mutated, 0, 9f, 9f, 9f, 1f);
        WritePixel(mutated, 8, 2f, 1f, 0.5f, 0.25f);

        document.CompositeCurrentEffectThroughCoverage(
            original,
            new Rectangle(0, 0, 2, 1),
            maskX: 1,
            maskY: 0,
            maskWidth: 1,
            maskHeight: 1,
            new byte[] { 128 },
            compositeCurrentOverOriginal: true);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(1f, ReadHalf(row, 0));
        Assert.Equal(0.5f, ReadHalf(row, 6));
        float coverage = 128f / 255f;
        float expectedRed = 1f + (2.75f - 1f) * coverage;
        float expectedAlpha = 0.5f + (0.625f - 0.5f) * coverage;
        Assert.InRange(ReadHalf(row, 8), expectedRed - 0.001f, expectedRed + 0.001f);
        Assert.InRange(
            ReadHalf(row, 14),
            expectedAlpha - 0.001f,
            expectedAlpha + 0.001f);
        Assert.Equal(0, document.Revision);
    }

    [Fact]
    public void CompositeCurrentEffectThroughCoverage_RejectsMismatchedInputs()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using var wrongSize = new HdrRgba16FloatBuffer(1, 1);
        using HdrRgba16FloatBuffer original = document.MasterPixels.Clone();

        Assert.Throws<ArgumentException>(() =>
            document.CompositeCurrentEffectThroughCoverage(
                wrongSize,
                new Rectangle(0, 0, 1, 1),
                0, 0, 1, 1, new byte[] { 255 }, false));
        Assert.Throws<ArgumentException>(() =>
            document.CompositeCurrentEffectThroughCoverage(
                original,
                new Rectangle(0, 0, 1, 1),
                0, 0, 2, 1, new byte[] { 255 }, false));
        Assert.Throws<ArgumentException>(() =>
            document.CompositeCurrentEffectThroughCoverage(
                original,
                new Rectangle(0, 0, 1, 1),
                -1, 0, 1, 1, new byte[] { 255 }, false));
    }

    [Fact]
    public void MagnifyRectangle_UsesCenteredMitchellSamplingAndPreservesOutside()
    {
        const int width = 7;
        const int height = 3;
        byte[] sourceBytes = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WritePixel(
                    sourceBytes,
                    (y * width + x) * HdrRgba16FloatBuffer.BytesPerPixel,
                    x,
                    x / 2f,
                    x / 4f,
                    0.5f);
            }
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, width, height),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, width, height), "HDR", true, 203f, 1000f)
            });

        document.MagnifyRectangle(new Rectangle(1, 0, 5, height), zoom: 2f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(1);
        Assert.Equal(0f, ReadHalf(row, 0));
        Assert.InRange(ReadHalf(row, 8), 1.9f, 2.1f);
        Assert.InRange(ReadHalf(row, 24), 2.49f, 2.51f);
        Assert.InRange(ReadHalf(row, 40), 2.9f, 3.1f);
        Assert.Equal(6f, ReadHalf(row, 48));
        Assert.Equal(0.5f, ReadHalf(row, 30), 3);
        Assert.Equal(1, document.Revision);
        Assert.Equal(new Rectangle(0, 0, width, height), Assert.Single(document.SourceSegments).DestinationRectangle);
    }

    [Fact]
    public void MagnifyRectangle_RejectsInvalidGeometryAndZoom()
    {
        using HdrImageDocument document = CreatePatternDocument();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.MagnifyRectangle(Rectangle.Empty, 2f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.MagnifyRectangle(new Rectangle(0, 0, 1, 1), 0.99f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.MagnifyRectangle(
                new Rectangle(0, 0, 1, 1),
                HdrImageDocument.MaximumSupportedMagnification + 0.01f));
    }

    [Fact]
    public void MagnifyAroundPoint_UsesWorldAlignedCenterSamplingAndPreservesOutside()
    {
        const int width = 7;
        const int height = 3;
        byte[] sourceBytes = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WritePixel(sourceBytes, (y * width + x) * 8, x, x / 2f, x / 4f, 1f);
            }
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, width, height),
            pixels.Clone(),
            Array.Empty<HdrCaptureSourceSegment>());
        using HdrRgba16FloatBuffer source = document.MasterPixels.Clone();

        document.MagnifyAroundPoint(
            source,
            new Rectangle(1, 0, 5, height),
            centerX: 3.5f,
            centerY: 1.5f,
            zoom: 2f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(1);
        Assert.Equal(0f, ReadHalf(row, 0));
        Assert.Equal(2f, ReadHalf(row, 8), 3);
        Assert.Equal(3f, ReadHalf(row, 24), 3);
        Assert.Equal(4f, ReadHalf(row, 40), 3);
        Assert.Equal(6f, ReadHalf(row, 48));
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void MagnifyAroundPoint_RejectsInvalidSourceOrCenter()
    {
        using HdrImageDocument document = CreatePatternDocument();
        using var wrongSize = new HdrRgba16FloatBuffer(1, 1);
        using HdrRgba16FloatBuffer source = document.MasterPixels.Clone();

        Assert.Throws<ArgumentException>(() =>
            document.MagnifyAroundPoint(wrongSize, new Rectangle(0, 0, 1, 1), 0.5f, 0.5f, 2f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.MagnifyAroundPoint(source, new Rectangle(0, 0, 1, 1), float.NaN, 0.5f, 2f));
    }

    [Fact]
    public void ApplyHighlightRectangle_CapsStraightColorInPremultipliedLinearHdr()
    {
        byte[] sourceBytes = new byte[2 * HdrRgba16FloatBuffer.BytesPerPixel];
        WritePixel(sourceBytes, 0, 2f, 0.5f, 1f, 1f);
        WritePixel(sourceBytes, 8, 0.5f, 0.25f, 0.25f, 0.5f);
        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            2,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, 2, 1),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, 2, 1), "HDR", true, 203f, 1000f)
            });

        document.ApplyHighlightRectangle(
            new Rectangle(0, 0, 2, 1),
            red: 64,
            green: 128,
            blue: 255,
            highlightWhiteNits: 80f);

        float redLimit = DecodeSrgb(64);
        float greenLimit = DecodeSrgb(128);
        Assert.Equal(redLimit, ReadHalf(document.MasterPixels.GetRowSpan(0), 0), 3);
        Assert.Equal(greenLimit, ReadHalf(document.MasterPixels.GetRowSpan(0), 2), 3);
        Assert.Equal(1f, ReadHalf(document.MasterPixels.GetRowSpan(0), 4));
        Assert.Equal(redLimit * 0.5f, ReadHalf(document.MasterPixels.GetRowSpan(0), 8), 3);
        Assert.Equal(greenLimit * 0.5f, ReadHalf(document.MasterPixels.GetRowSpan(0), 10), 3);
        Assert.Equal(0.25f, ReadHalf(document.MasterPixels.GetRowSpan(0), 12));
        Assert.Equal(0.5f, ReadHalf(document.MasterPixels.GetRowSpan(0), 14));
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void ApplyHighlightRectangle_RejectsInvalidInputs()
    {
        using HdrImageDocument document = CreatePatternDocument();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.ApplyHighlightRectangle(Rectangle.Empty, 255, 255, 0, 80f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.ApplyHighlightRectangle(new Rectangle(0, 0, 1, 1), 255, 255, 0, 0f));
    }

    [Fact]
    public void NormalizeMixedMonitorBrightness_MatchesHdrWhiteWithoutScalingForegroundHdrPixels()
    {
        byte[] sourceBytes = new byte[4 * HdrRgba16FloatBuffer.BytesPerPixel];
        WritePixel(sourceBytes, 0, 1f, 1f, 1f, 1f);
        WritePixel(sourceBytes, 8, 4f, 3f, 2f, 1f);
        WritePixel(sourceBytes, 16, 5f, 4f, 3f, 1f);
        WritePixel(sourceBytes, 24, 1f, 1f, 1f, 1f);
        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            4,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, 4, 1),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, 4, 1), "SDR", false, 80f, 300f),
                new HdrCaptureSourceSegment(new Rectangle(1, 0, 2, 1), "HDR game", true, 203f, 1000f)
            });

        document.NormalizeMixedMonitorBrightness(new HdrCaptureSettings());

        float expectedSdrWhite = 203f / HdrRgba16FloatBuffer.ReferenceWhiteNits;
        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(expectedSdrWhite, ReadHalf(row, 0), 2);
        Assert.Equal(4f, ReadHalf(row, 8));
        Assert.Equal(5f, ReadHalf(row, 16));
        Assert.Equal(expectedSdrWhite, ReadHalf(row, 24), 2);
        Assert.Equal(203f, document.SourceSegments[0].SdrWhiteNits);
        Assert.Equal(203f, document.SourceSegments[1].SdrWhiteNits);
        Assert.Equal(0, document.Revision);
    }

    [Fact]
    public void NormalizeMixedMonitorBrightness_CustomAndPreserveModesAreExplicit()
    {
        static HdrImageDocument CreateDocument()
        {
            byte[] bytes = new byte[2 * HdrRgba16FloatBuffer.BytesPerPixel];
            WritePixel(bytes, 0, 1f, 1f, 1f, 1f);
            WritePixel(bytes, 8, 2f, 2f, 2f, 1f);
            using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
                bytes,
                bytes.Length,
                2,
                1);
            return new HdrImageDocument(
                new Rectangle(0, 0, 2, 1),
                pixels.Clone(),
                new[]
                {
                    new HdrCaptureSourceSegment(new Rectangle(0, 0, 1, 1), "SDR", false, 80f, 300f),
                    new HdrCaptureSourceSegment(new Rectangle(1, 0, 1, 1), "HDR", true, 203f, 1000f)
                });
        }

        using HdrImageDocument preserved = CreateDocument();
        preserved.NormalizeMixedMonitorBrightness(new HdrCaptureSettings
        {
            MixedMonitorBrightnessMode = HdrMixedMonitorBrightnessMode.Preserve
        });
        Assert.Equal(1f, ReadHalf(preserved.MasterPixels.GetRowSpan(0), 0));
        Assert.Equal(80f, preserved.SourceSegments[0].SdrWhiteNits);

        using HdrImageDocument custom = CreateDocument();
        custom.NormalizeMixedMonitorBrightness(new HdrCaptureSettings
        {
            MixedMonitorBrightnessMode = HdrMixedMonitorBrightnessMode.Custom,
            MixedMonitorCustomSdrWhiteNits = 250f
        });
        Assert.Equal(250f / 80f, ReadHalf(custom.MasterPixels.GetRowSpan(0), 0), 2);
        Assert.Equal(250f, custom.SourceSegments[0].SdrWhiteNits);
        Assert.Equal(2f, ReadHalf(custom.MasterPixels.GetRowSpan(0), 8));
    }

    private static void WritePixel(
        Span<byte> destination,
        int offset,
        float red,
        float green,
        float blue,
        float alpha)
    {
        WriteHalf(destination, offset, red);
        WriteHalf(destination, offset + 2, green);
        WriteHalf(destination, offset + 4, blue);
        WriteHalf(destination, offset + 6, alpha);
    }

    private static float DecodeSrgb(byte value)
    {
        float encoded = value / 255f;
        return encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }


    [Fact]
    public void DarkenOutsideRectangles_CompositesBlackOutsideUnionAndPreservesRetainedPixels()
    {
        const int width = 3;
        byte[] sourceBytes = new byte[width * HdrRgba16FloatBuffer.BytesPerPixel];
        WritePixel(sourceBytes, 0, 4f, 2f, 1f, 0.25f);
        WritePixel(sourceBytes, 8, 4f, 2f, 1f, 0.5f);
        WritePixel(sourceBytes, 16, 4f, 2f, 1f, 1f);
        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            width,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, width, 1),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, width, 1), "HDR", true, 203f, 1000f)
            });

        document.DarkenOutsideRectangles(
            new[]
            {
                new Rectangle(1, 0, 1, 1),
                new Rectangle(20, 20, 1, 1)
            },
            darkenOpacity: 128);

        float multiplier = 127f / 255f;
        Assert.Equal(4f * multiplier, ReadHalf(document.MasterPixels.GetRowSpan(0), 0), 3);
        Assert.Equal(128f / 255f + 0.25f * multiplier, ReadHalf(document.MasterPixels.GetRowSpan(0), 6), 3);
        Assert.Equal(4f, ReadHalf(document.MasterPixels.GetRowSpan(0), 8));
        Assert.Equal(0.5f, ReadHalf(document.MasterPixels.GetRowSpan(0), 14));
        Assert.Equal(4f * multiplier, ReadHalf(document.MasterPixels.GetRowSpan(0), 16), 3);
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void ApplySpotlightRectangles_BlursOutsideUnionInLinearHdr()
    {
        const int width = 5;
        byte[] sourceBytes = new byte[width * HdrRgba16FloatBuffer.BytesPerPixel];
        for (int x = 0; x < width; x++)
        {
            float value = x == 2 ? 10f : 0f;
            WritePixel(sourceBytes, x * 8, value, value, value, 1f);
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            width,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, width, 1),
            pixels.Clone(),
            Array.Empty<HdrCaptureSourceSegment>());

        document.ApplySpotlightRectangles(
            new[] { new Rectangle(2, 0, 1, 1) },
            darkenOpacity: 0,
            blurSigma: 1f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.True(ReadHalf(row, 8) > 0f);
        Assert.Equal(10f, ReadHalf(row, 16));
        Assert.True(ReadHalf(row, 24) > 0f);
        Assert.Equal(1f, ReadHalf(row, 14), 3);
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void DarkenOutsideRectangles_RejectsEmptyOrInvalidRegions()
    {
        using HdrImageDocument document = CreatePatternDocument();

        Assert.Throws<ArgumentException>(() =>
            document.DarkenOutsideRectangles(Array.Empty<Rectangle>(), 128));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.DarkenOutsideRectangles(new[] { Rectangle.Empty }, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.ApplySpotlightRectangles(
                new[] { new Rectangle(0, 0, 1, 1) },
                128,
                HdrImageDocument.MaximumSupportedBlurSigma + 0.01f));
    }
    [Fact]
    public void BlurRectangle_BlursInLinearHdrWithClampedEdgesAndPreservesOutside()
    {
        const int width = 5;
        byte[] sourceBytes = new byte[width * HdrRgba16FloatBuffer.BytesPerPixel];
        for (int x = 0; x < width; x++)
        {
            float value = x == 2 ? 10f : 0f;
            WritePixel(sourceBytes, x * HdrRgba16FloatBuffer.BytesPerPixel, value, value, value, 1f);
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            sourceBytes.Length,
            width,
            1);
        using var document = new HdrImageDocument(
            new Rectangle(0, 0, width, 1),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, width, 1), "HDR", true, 203f, 1000f)
            });

        document.BlurRectangle(new Rectangle(1, 0, 3, 1), sigma: 1f);

        float left = ReadHalf(document.MasterPixels.GetRowSpan(0), 8);
        float center = ReadHalf(document.MasterPixels.GetRowSpan(0), 16);
        float right = ReadHalf(document.MasterPixels.GetRowSpan(0), 24);
        Assert.Equal(0f, ReadHalf(document.MasterPixels.GetRowSpan(0), 0));
        Assert.True(left > 0f);
        Assert.True(center > left);
        Assert.Equal(left, right, 3);
        Assert.Equal(0f, ReadHalf(document.MasterPixels.GetRowSpan(0), 32));
        Assert.Equal(1f, ReadHalf(document.MasterPixels.GetRowSpan(0), 14));
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void BlurRectangle_ConstantPremultipliedColorRemainsConstant()
    {
        byte[] sourceBytes = new byte[3 * HdrRgba16FloatBuffer.BytesPerPixel];
        for (int x = 0; x < 3; x++)
        {
            WritePixel(sourceBytes, x * 8, 2f, 1f, 0.5f, 0.5f);
        }
        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, sourceBytes.Length, 3, 1);
        using var document = new HdrImageDocument(new Rectangle(0, 0, 3, 1), pixels.Clone(), Array.Empty<HdrCaptureSourceSegment>());

        document.BlurRectangle(new Rectangle(0, 0, 3, 1), 2f);

        Assert.Equal(2f, ReadHalf(document.MasterPixels.GetRowSpan(0), 8), 3);
        Assert.Equal(0.5f, ReadHalf(document.MasterPixels.GetRowSpan(0), 14), 3);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.BlurRectangle(new Rectangle(0, 0, 1, 1), 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.BlurRectangle(new Rectangle(0, 0, 1, 1), HdrImageDocument.MaximumSupportedBlurSigma + 0.01f));
    }

    private static HdrImageDocument CreatePatternDocument()
    {
        const int width = 3;
        const int height = 2;
        byte[] sourceBytes = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WriteHalf(sourceBytes, (y * width + x) * 8, y * 10 + x);
                WriteHalf(sourceBytes, (y * width + x) * 8 + 6, 1f);
            }
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        return new HdrImageDocument(
            new Rectangle(-100, 50, width, height),
            pixels.Clone(),
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(0, 0, 2, 1), "HDR", true, 203f, 1000f),
                new HdrCaptureSourceSegment(new Rectangle(0, 1, 3, 1), "SDR", false, 80f, 80f)
            });
    }

    private static void AssertBitmapsEqual(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            }
        }
    }

    public enum EditorTransform
    {
        Rotate90Clockwise,
        Rotate90CounterClockwise,
        Rotate180,
        FlipHorizontal,
        FlipVertical
    }

    private static void WriteHalf(Span<byte> bytes, int offset, float value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));
    }

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset)
    {
        return (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
    }
}
