using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class ObsGameCaptureRgb10A2ConverterTests
{
    [Fact]
    public void SrgbWhite_MapsToScRgbReferenceWhite()
    {
        using HdrRgba16FloatBuffer result = Convert(Pack(1023, 1023, 1023, 3),
            ObsGameCaptureRgb10A2ColorSpace.Srgb);

        Assert.Equal(1f, ReadHalf(result, 0));
        Assert.Equal(1f, ReadHalf(result, 2));
        Assert.Equal(1f, ReadHalf(result, 4));
        Assert.Equal(1f, ReadHalf(result, 6));
    }

    [Fact]
    public void SrgbAlpha_IsPremultiplied()
    {
        using HdrRgba16FloatBuffer result = Convert(Pack(1023, 1023, 1023, 1),
            ObsGameCaptureRgb10A2ColorSpace.Srgb);

        Assert.InRange(ReadHalf(result, 0), 0.332f, 0.334f);
        Assert.InRange(ReadHalf(result, 2), 0.332f, 0.334f);
        Assert.InRange(ReadHalf(result, 4), 0.332f, 0.334f);
        Assert.InRange(ReadHalf(result, 6), 0.332f, 0.334f);
    }

    [Fact]
    public void OpaqueMode_IgnoresFramebufferAlphaWithoutDiscardingRgb()
    {
        byte[] source = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(source, Pack(1023, 512, 256, 0));

        using HdrRgba16FloatBuffer result =
            ObsGameCaptureRgb10A2Converter.ConvertToRgba16Float(
                source,
                4,
                1,
                1,
                ObsGameCaptureRgb10A2ColorSpace.Srgb,
                allowTransparency: false);

        Assert.Equal(1f, ReadHalf(result, 0));
        Assert.InRange(ReadHalf(result, 2), 0.21f, 0.22f);
        Assert.InRange(ReadHalf(result, 4), 0.05f, 0.052f);
        Assert.Equal(1f, ReadHalf(result, 6));
    }

    [Fact]
    public void PremultipliedMode_DoesNotMultiplyRgbAgain()
    {
        byte[] source = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(source, Pack(1023, 1023, 1023, 1));

        using HdrRgba16FloatBuffer result =
            ObsGameCaptureRgb10A2Converter.ConvertToRgba16Float(
                source,
                4,
                1,
                1,
                ObsGameCaptureRgb10A2ColorSpace.Srgb,
                ObsGameCaptureAlphaMode.Premultiplied);

        Assert.Equal(1f, ReadHalf(result, 0));
        Assert.Equal(1f, ReadHalf(result, 2));
        Assert.Equal(1f, ReadHalf(result, 4));
        Assert.InRange(ReadHalf(result, 6), 0.332f, 0.334f);
    }

    [Fact]
    public void PqNeutral80Nits_MapsToScRgbReferenceWhite()
    {
        uint code = EncodePqCode(80f);
        using HdrRgba16FloatBuffer result = Convert(Pack(code, code, code, 3),
            ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq);

        Assert.InRange(ReadHalf(result, 0), 0.98f, 1.02f);
        Assert.InRange(ReadHalf(result, 2), 0.98f, 1.02f);
        Assert.InRange(ReadHalf(result, 4), 0.98f, 1.02f);
    }

    [Fact]
    public void PqRec2020Primary_ConvertsToLinearScRgbPrimaries()
    {
        uint code = EncodePqCode(80f);
        using HdrRgba16FloatBuffer result = Convert(Pack(code, 0, 0, 3),
            ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq);

        Assert.InRange(ReadHalf(result, 0), 1.62f, 1.70f);
        Assert.InRange(ReadHalf(result, 2), -0.14f, -0.11f);
        Assert.InRange(ReadHalf(result, 4), -0.03f, -0.01f);
    }

    private static HdrRgba16FloatBuffer Convert(uint packed, ObsGameCaptureRgb10A2ColorSpace colorSpace)
    {
        byte[] source = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(source, packed);
        return ObsGameCaptureRgb10A2Converter.ConvertToRgba16Float(source, 4, 1, 1, colorSpace);
    }

    private static uint Pack(uint red, uint green, uint blue, uint alpha)
    {
        return red | (green << 10) | (blue << 20) | (alpha << 30);
    }

    private static uint EncodePqCode(float nits)
    {
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 32f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 128f;
        const float c3 = 2392f / 128f;

        float linear = nits / 10_000f;
        float power = MathF.Pow(linear, m1);
        float encoded = MathF.Pow((c1 + c2 * power) / (1f + c3 * power), m2);
        return (uint)Math.Clamp(MathF.Round(encoded * 1023f), 0f, 1023f);
    }

    private static float ReadHalf(HdrRgba16FloatBuffer buffer, int offset)
    {
        return (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.GetRowSpan(0).Slice(offset, 2)));
    }
}
