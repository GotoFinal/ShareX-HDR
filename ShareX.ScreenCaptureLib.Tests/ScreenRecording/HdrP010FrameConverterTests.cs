using System.Buffers.Binary;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.ScreenRecording;

public sealed class HdrP010FrameConverterTests
{
    [Fact]
    public void Convert_BlackFrame_WritesLimitedRangeBlackAndNeutralChroma()
    {
        using HdrRgba16FloatBuffer source = CreateSolidFrame(0f, 0f, 0f);
        var converter = new HdrP010FrameConverter(2, 2, 1000f);
        byte[] destination = new byte[converter.FrameByteCount];

        HdrVideoFrameLightLevels levels = converter.Convert(source, destination);

        Assert.Equal(12, destination.Length);
        Assert.All(ReadWords(destination.AsSpan(0, 8)), value => Assert.Equal((ushort)(64 << 6), value));
        Assert.All(ReadWords(destination.AsSpan(8, 4)), value => Assert.Equal((ushort)(512 << 6), value));
        Assert.Equal(0f, levels.MaxCll);
        Assert.Equal(0f, levels.MaxFall);
    }

    [Fact]
    public void Convert_ScRgbWhite_PreservesNitsAndNeutralChroma()
    {
        using HdrRgba16FloatBuffer source = CreateSolidFrame(1f, 1f, 1f);
        var converter = new HdrP010FrameConverter(2, 2, 1000f);
        byte[] destination = new byte[converter.FrameByteCount];

        HdrVideoFrameLightLevels levels = converter.Convert(source, destination);
        ushort[] luma = ReadWords(destination.AsSpan(0, 8));
        ushort[] chroma = ReadWords(destination.AsSpan(8, 4));

        Assert.All(luma, value => Assert.InRange(value >> 6, 488, 492));
        Assert.All(chroma, value => Assert.Equal(512, value >> 6));
        Assert.InRange(levels.MaxCll, 79.9f, 80.1f);
        Assert.InRange(levels.MaxFall, 79.9f, 80.1f);
    }

    [Fact]
    public void ConvertSdrBgra_MapsSdrWhiteToGraphicsWhiteInHdr10()
    {
        byte[] source =
        {
            255, 255, 255, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
            255, 255, 255, 255
        };
        var converter = new HdrP010FrameConverter(2, 2, 1000f);
        byte[] destination = new byte[converter.FrameByteCount];

        HdrVideoFrameLightLevels levels = converter.ConvertSdrBgra(source, 8, destination);
        ushort[] chroma = ReadWords(destination.AsSpan(8, 4));

        Assert.All(chroma, value => Assert.Equal(512, value >> 6));
        Assert.InRange(levels.MaxCll, 202.9f, 203.1f);
        Assert.InRange(levels.MaxFall, 202.9f, 203.1f);
    }

    private static HdrRgba16FloatBuffer CreateSolidFrame(float red, float green, float blue)
    {
        byte[] pixels = new byte[2 * 2 * HdrRgba16FloatBuffer.BytesPerPixel];

        for (int offset = 0; offset < pixels.Length; offset += HdrRgba16FloatBuffer.BytesPerPixel)
        {
            WriteHalf(pixels, offset, red);
            WriteHalf(pixels, offset + 2, green);
            WriteHalf(pixels, offset + 4, blue);
            WriteHalf(pixels, offset + 6, 1f);
        }

        return HdrRgba16FloatBuffer.CopyFrom(pixels, 2 * HdrRgba16FloatBuffer.BytesPerPixel, 2, 2);
    }

    private static void WriteHalf(byte[] destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.AsSpan(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static ushort[] ReadWords(ReadOnlySpan<byte> source)
    {
        ushort[] result = new ushort[source.Length / sizeof(ushort)];

        for (int i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(i * 2, 2));
        }

        return result;
    }
}
