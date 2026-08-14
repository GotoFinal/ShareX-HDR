using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrImageDocumentAdjustmentTests
{
    [Fact]
    public void ApplyAlphaAdjustment_ScalesPremultipliedColorAndAlphaTogether()
    {
        using HdrImageDocument document = CreateDocument(4f, 2f, 1f, 0.5f);

        document.ApplyAlphaAdjustment(25f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(1f, ReadHalf(row, 0), 3);
        Assert.Equal(0.5f, ReadHalf(row, 2), 3);
        Assert.Equal(0.25f, ReadHalf(row, 4), 3);
        Assert.Equal(0.125f, ReadHalf(row, 6), 3);
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void ApplyExposureAdjustment_PreservesAlphaAndAboveSdrValues()
    {
        using HdrImageDocument document = CreateDocument(4f, 1.5f, -0.25f, 0.5f);

        document.ApplyExposureAdjustment(2f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(16f, ReadHalf(row, 0), 3);
        Assert.Equal(6f, ReadHalf(row, 2), 3);
        Assert.Equal(-1f, ReadHalf(row, 4), 3);
        Assert.Equal(0.5f, ReadHalf(row, 6), 3);
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void Adjustments_RejectInvalidParametersAndClampFiniteHalfOverflow()
    {
        using HdrImageDocument document = CreateDocument(1024f, 1f, 1f, 1f);

        Assert.Throws<ArgumentOutOfRangeException>(() => document.ApplyAlphaAdjustment(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ApplyExposureAdjustment(float.NaN));

        document.ApplyExposureAdjustment(100f);
        Assert.Equal(65504f, ReadHalf(document.MasterPixels.GetRowSpan(0), 0));
    }

    [Fact]
    public void ApplySaturationAdjustment_ZeroProducesLinearRec709Grayscale()
    {
        using HdrImageDocument document = CreateDocument(4f, 2f, 1f, 0.5f);

        document.ApplySaturationAdjustment(0f);

        float expectedLuminance = 4f * 0.2126f + 2f * 0.7152f + 1f * 0.0722f;
        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(expectedLuminance, ReadHalf(row, 0), 2);
        Assert.Equal(expectedLuminance, ReadHalf(row, 2), 2);
        Assert.Equal(expectedLuminance, ReadHalf(row, 4), 2);
        Assert.Equal(0.5f, ReadHalf(row, 6), 3);
    }

    [Fact]
    public void ApplySaturationAdjustment_PreservesLuminanceAndHdrExcursions()
    {
        using HdrImageDocument document = CreateDocument(4f, 2f, 1f, 1f);
        const float saturation = 2f;
        float beforeLuminance = 4f * 0.2126f + 2f * 0.7152f + 1f * 0.0722f;

        document.ApplySaturationAdjustment(saturation);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        float red = ReadHalf(row, 0);
        float green = ReadHalf(row, 2);
        float blue = ReadHalf(row, 4);
        float afterLuminance = red * 0.2126f + green * 0.7152f + blue * 0.0722f;
        Assert.True(red > 4f);
        Assert.True(blue < 0f);
        Assert.Equal(beforeLuminance, afterLuminance, 2);
        Assert.Equal(1f, ReadHalf(row, 6));
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(2.01f)]
    [InlineData(float.NaN)]
    public void ApplySaturationAdjustment_RejectsInvalidRange(float saturation)
    {
        using HdrImageDocument document = CreateDocument(1f, 1f, 1f, 1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ApplySaturationAdjustment(saturation));
    }

    [Fact]
    public void ApplyLinearColorMatrixAdjustment_UsesStraightColorAndSdrWhiteOffset()
    {
        using HdrImageDocument document = CreateDocument(2f, 1f, 0.5f, 0.5f);

        document.ApplyLinearColorMatrixAdjustment(
            new float[]
            {
                1f, 0f, 0f, 0.25f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f
            },
            160f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(2.25f, ReadHalf(row, 0), 3);
        Assert.Equal(1f, ReadHalf(row, 2), 3);
        Assert.Equal(0.5f, ReadHalf(row, 4), 3);
        Assert.Equal(0.5f, ReadHalf(row, 6), 3);
    }

    [Fact]
    public void ApplyGammaAdjustment_PreservesHdrAndNegativeExcursions()
    {
        using HdrImageDocument document = CreateDocument(4f, -1f, 1f, 1f);

        document.ApplyGammaAdjustment(2f, 80f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(2f, ReadHalf(row, 0), 3);
        Assert.Equal(-1f, ReadHalf(row, 2), 3);
        Assert.Equal(1f, ReadHalf(row, 4), 3);
    }

    [Fact]
    public void ApplyThresholdAdjustment_UsesConfiguredSdrWhite()
    {
        using HdrImageDocument document = CreateDocument(1.2f, 1.2f, 1.2f, 0.5f);

        document.ApplyThresholdAdjustment(0.5f, 160f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(1f, ReadHalf(row, 0), 3);
        Assert.Equal(1f, ReadHalf(row, 2), 3);
        Assert.Equal(1f, ReadHalf(row, 4), 3);
        Assert.Equal(0.5f, ReadHalf(row, 6), 3);
    }

    [Fact]
    public void CreateFromSdrBitmapAndCompositeHdrDocument_PreserveHdrSourceOverSdrDecoration()
    {
        using var background = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        background.SetPixel(0, 0, Color.FromArgb(255, 0, 255, 0));
        using HdrImageDocument destination = HdrImageDocument.CreateFromSdrBitmap(background, 80f);
        using HdrImageDocument source = CreateDocument(2f, 0f, 0f, 0.5f);

        destination.CompositeHdrDocument(source, 0, 0);

        ReadOnlySpan<byte> row = destination.MasterPixels.GetRowSpan(0);
        Assert.Equal(2f, ReadHalf(row, 0), 3);
        Assert.Equal(0.5f, ReadHalf(row, 2), 3);
        Assert.Equal(0f, ReadHalf(row, 4), 3);
        Assert.Equal(1f, ReadHalf(row, 6), 3);
    }

    [Fact]
    public void ApplyAlphaMask_ScalesPremultipliedHdrPixels()
    {
        using HdrImageDocument document = CreateDocument(4f, 2f, 1f, 0.5f);
        using var mask = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        mask.SetPixel(0, 0, Color.FromArgb(128, 255, 255, 255));

        document.ApplyAlphaMask(mask);

        float coverage = 128f / 255f;
        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(4f * coverage, ReadHalf(row, 0), 2);
        Assert.Equal(2f * coverage, ReadHalf(row, 2), 2);
        Assert.Equal(coverage, ReadHalf(row, 4), 2);
        Assert.Equal(0.5f * coverage, ReadHalf(row, 6), 2);
    }

    private static HdrImageDocument CreateDocument(float red, float green, float blue, float alpha)
    {
        var pixels = new HdrRgba16FloatBuffer(1, 1);
        Span<byte> row = pixels.GetWritableRowSpan(0);
        WriteHalf(row, 0, red);
        WriteHalf(row, 2, green);
        WriteHalf(row, 4, blue);
        WriteHalf(row, 6, alpha);

        return new HdrImageDocument(
            new Rectangle(0, 0, 1, 1),
            pixels,
            Array.Empty<HdrCaptureSourceSegment>());
    }

    private static void WriteHalf(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
}
