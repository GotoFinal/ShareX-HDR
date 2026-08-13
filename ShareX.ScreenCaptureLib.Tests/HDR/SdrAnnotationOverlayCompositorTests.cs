using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class SdrAnnotationOverlayCompositorTests
{
    [Fact]
    public void TransparentOverlay_PreservesHdrPixelsAndPaddingExactly()
    {
        byte[] destination = Enumerable.Repeat((byte)0xA5, 24).ToArray();
        WriteHalf(destination, 0, -0.25f);
        WriteHalf(destination, 2, 4f);
        WriteHalf(destination, 4, 12.5f);
        WriteHalf(destination, 6, 1f);
        byte[] expected = (byte[])destination.Clone();

        SdrAnnotationOverlayCompositor.CompositeBgra8Premultiplied(
            destination, 24, new byte[12], 12, 1, 1, 203f);

        Assert.Equal(expected, destination);
    }

    [Fact]
    public void OpaqueWhite_UsesConfiguredAnnotationLuminance()
    {
        byte[] destination = new byte[8];
        byte[] overlay = { 255, 255, 255, 255 };

        SdrAnnotationOverlayCompositor.CompositeBgra8Premultiplied(
            destination, 8, overlay, 4, 1, 1, 203f);

        float expected = 203f / 80f;
        AssertClose(ReadHalf(destination, 0), expected, 0.002f);
        AssertClose(ReadHalf(destination, 2), expected, 0.002f);
        AssertClose(ReadHalf(destination, 4), expected, 0.002f);
        Assert.Equal(1f, ReadHalf(destination, 6));
    }

    [Fact]
    public void PremultipliedHalfAlphaWhite_BlendsInLinearLight()
    {
        byte[] destination = CreateOpaqueGrayDestination(1f);
        byte[] overlay = { 128, 128, 128, 128 };

        SdrAnnotationOverlayCompositor.CompositeBgra8Premultiplied(
            destination, 8, overlay, 4, 1, 1, 203f);

        float alpha = 128f / 255f;
        float expected = alpha * (203f / 80f) + (1f - alpha);
        AssertClose(ReadHalf(destination, 0), expected, 0.002f);
        Assert.Equal(1f, ReadHalf(destination, 6));
    }

    [Fact]
    public void OpaqueSrgbGray_IsDecodedBeforeComposition()
    {
        byte[] destination = new byte[8];
        byte[] overlay = { 128, 128, 128, 255 };

        SdrAnnotationOverlayCompositor.CompositeBgra8Premultiplied(
            destination, 8, overlay, 4, 1, 1, 80f);

        float encoded = 128f / 255f;
        float expected = MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        AssertClose(ReadHalf(destination, 0), expected, 0.0003f);
    }

    [Fact]
    public void StraightAlphaAsset_IsClippedAtCanvasEdgeWithoutShifting()
    {
        byte[] destination = new byte[16];
        byte[] source =
        {
            0, 0, 255, 255,
            0, 255, 0, 128
        };

        SdrAnnotationOverlayCompositor.CompositeBgra8Straight(
            destination, 16, 2, 1,
            source, 8, 2, 1,
            -1, 0, 80f);

        Assert.Equal(0f, ReadHalf(destination, 0));
        AssertClose(ReadHalf(destination, 2), 128f / 255f, 0.001f);
        AssertClose(ReadHalf(destination, 6), 128f / 255f, 0.001f);
        Assert.Equal(0f, ReadHalf(destination, 8));
    }

    private static byte[] CreateOpaqueGrayDestination(float value)
    {
        byte[] destination = new byte[8];
        WriteHalf(destination, 0, value);
        WriteHalf(destination, 2, value);
        WriteHalf(destination, 4, value);
        WriteHalf(destination, 6, 1f);
        return destination;
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

    private static void AssertClose(float actual, float expected, float tolerance)
    {
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }
}