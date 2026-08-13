using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class ObsGameCaptureAlphaTests
{
    [Fact]
    public void OpaqueMode_PreservesRgbAndForcesAlphaToOne()
    {
        using var buffer = new HdrRgba16FloatBuffer(1, 1);
        WritePixel(buffer, 2f, 1f, 0.5f, 0f);

        ObsGameCaptureTextureReader.NormalizeStraightAlpha(buffer, allowTransparency: false);

        Assert.Equal(2f, ReadHalf(buffer, 0));
        Assert.Equal(1f, ReadHalf(buffer, 2));
        Assert.Equal(0.5f, ReadHalf(buffer, 4));
        Assert.Equal(1f, ReadHalf(buffer, 6));
    }

    [Fact]
    public void TransparencyMode_PremultipliesStraightFramebufferAlpha()
    {
        using var buffer = new HdrRgba16FloatBuffer(1, 1);
        WritePixel(buffer, 2f, 1f, 0.5f, 0.5f);

        ObsGameCaptureTextureReader.NormalizeStraightAlpha(buffer, allowTransparency: true);

        Assert.Equal(1f, ReadHalf(buffer, 0));
        Assert.Equal(0.5f, ReadHalf(buffer, 2));
        Assert.Equal(0.25f, ReadHalf(buffer, 4));
        Assert.Equal(0.5f, ReadHalf(buffer, 6));
    }

    [Fact]
    public void PremultipliedMode_PreservesRgbAndFramebufferAlpha()
    {
        using var buffer = new HdrRgba16FloatBuffer(1, 1);
        WritePixel(buffer, 1f, 0.5f, 0.25f, 0.5f);

        ObsGameCaptureTextureReader.NormalizeAlpha(
            buffer,
            ObsGameCaptureAlphaMode.Premultiplied);

        Assert.Equal(1f, ReadHalf(buffer, 0));
        Assert.Equal(0.5f, ReadHalf(buffer, 2));
        Assert.Equal(0.25f, ReadHalf(buffer, 4));
        Assert.Equal(0.5f, ReadHalf(buffer, 6));
    }

    private static void WritePixel(
        HdrRgba16FloatBuffer buffer,
        float red,
        float green,
        float blue,
        float alpha)
    {
        Span<byte> pixel = buffer.GetWritableRowSpan(0);
        WriteHalf(pixel, 0, red);
        WriteHalf(pixel, 2, green);
        WriteHalf(pixel, 4, blue);
        WriteHalf(pixel, 6, alpha);
    }

    private static void WriteHalf(Span<byte> destination, int offset, float value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));
    }

    private static float ReadHalf(HdrRgba16FloatBuffer buffer, int offset)
    {
        return (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.GetRowSpan(0).Slice(offset, 2)));
    }
}
