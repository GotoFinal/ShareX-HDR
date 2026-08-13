using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Drawing;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrRegionEffectReplayerTests
{
    [Fact]
    public void Replay_BoxBlurUsesSelectionLocalCoordinatesAndPreservesHdr()
    {
        using HdrImageDocument document = CreateDocument(0f, 0f, 0f, 12f, 0f, 0f, 0f);
        var blur = new BlurEffectShape
        {
            Rectangle = new Rectangle(102, 50, 3, 1),
            BlurRadius = 3
        };

        HdrRegionEffectReplayer.Replay(
            document,
            new BaseEffectShape[] { blur },
            new Rectangle(100, 50, 7, 1),
            annotationWhiteNits: 203f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(0f, ReadRed(row, 0));
        Assert.Equal(0f, ReadRed(row, 1));
        Assert.True(ReadRed(row, 2) > 1f);
        Assert.True(ReadRed(row, 3) > 1f);
        Assert.True(ReadRed(row, 4) > 1f);
        Assert.Equal(0f, ReadRed(row, 5));
        Assert.Equal(0f, ReadRed(row, 6));
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void Replay_PixelateUsesBlocksAnchoredToLegacyEffectRectangle()
    {
        using HdrImageDocument document = CreateDocument(0f, 2f, 4f, 6f);
        var pixelate = new PixelateEffectShape
        {
            Rectangle = new Rectangle(11, 20, 3, 1),
            PixelSize = 2
        };

        HdrRegionEffectReplayer.Replay(
            document,
            new BaseEffectShape[] { pixelate },
            new Rectangle(10, 20, 4, 1),
            annotationWhiteNits: 203f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(0f, ReadRed(row, 0));
        Assert.Equal(3f, ReadRed(row, 1), 3);
        Assert.Equal(3f, ReadRed(row, 2), 3);
        Assert.Equal(6f, ReadRed(row, 3), 3);
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void Replay_HighlightCapsLinearHdrWithoutDiscardingMaster()
    {
        using HdrImageDocument document = CreateDocument(4f, 4f);
        var highlight = new HighlightEffectShape
        {
            Rectangle = new Rectangle(31, 40, 1, 1),
            HighlightColor = Color.Yellow
        };

        HdrRegionEffectReplayer.Replay(
            document,
            new BaseEffectShape[] { highlight },
            new Rectangle(30, 40, 2, 1),
            annotationWhiteNits: 80f);

        ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(0);
        Assert.Equal(4f, ReadRed(row, 0));
        Assert.Equal(1f, ReadRed(row, 1), 3);
        Assert.Equal(0f, ReadChannel(row, 1, channelOffset: 4), 3);
        Assert.Equal(1, document.Revision);
    }

    private static HdrImageDocument CreateDocument(params float[] redValues)
    {
        byte[] bytes = new byte[redValues.Length * HdrRgba16FloatBuffer.BytesPerPixel];
        for (int x = 0; x < redValues.Length; x++)
        {
            int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
            WriteHalf(bytes, offset, redValues[x]);
            WriteHalf(bytes, offset + 2, redValues[x]);
            WriteHalf(bytes, offset + 4, redValues[x]);
            WriteHalf(bytes, offset + 6, 1f);
        }

        using HdrRgba16FloatBuffer pixels = HdrRgba16FloatBuffer.CopyFrom(
            bytes,
            bytes.Length,
            redValues.Length,
            1);
        return new HdrImageDocument(
            new Rectangle(0, 0, redValues.Length, 1),
            pixels.Clone(),
            Array.Empty<HdrCaptureSourceSegment>());
    }

    private static float ReadRed(ReadOnlySpan<byte> row, int x) =>
        ReadChannel(row, x, channelOffset: 0);

    private static float ReadChannel(ReadOnlySpan<byte> row, int x, int channelOffset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(
                row.Slice(x * HdrRgba16FloatBuffer.BytesPerPixel + channelOffset, 2)));

    private static void WriteHalf(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));
}
