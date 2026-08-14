using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrRgba16FloatBufferTests
{
    [Fact]
    public void CopyFrom_UsesStrideAndPreservesFiniteScRgbValues()
    {
        const int sourceStride = 24;
        byte[] source = Enumerable.Repeat((byte)0x7A, sourceStride * 2).ToArray();
        WriteHalf(source, 0, -0.5f);
        WriteHalf(source, 2, 3.25f);
        WriteHalf(source, 4, 0.5f);
        WriteHalf(source, 6, 1f);
        WriteHalf(source, sourceStride, 1f);
        WriteHalf(source, sourceStride + 2, 2f);
        WriteHalf(source, sourceStride + 4, 3f);
        WriteHalf(source, sourceStride + 6, 1f);

        using HdrRgba16FloatBuffer buffer = HdrRgba16FloatBuffer.CopyFrom(
            source, sourceStride, 1, 2);

        Assert.Equal(8, buffer.RowBytes);
        Assert.Equal(-0.5f, ReadHalf(buffer.GetRowSpan(0), 0));
        Assert.Equal(3.25f, ReadHalf(buffer.GetRowSpan(0), 2));
        Assert.Equal(1f, ReadHalf(buffer.GetRowSpan(1), 0));
        Assert.Equal(3f, ReadHalf(buffer.GetRowSpan(1), 4));
    }

    [Fact]
    public void CopyFrom_SanitizesOnlyNonFiniteHalfValues()
    {
        byte[] source = new byte[8];
        WriteHalf(source, 0, float.NaN);
        WriteHalf(source, 2, float.PositiveInfinity);
        WriteHalf(source, 4, float.NegativeInfinity);
        WriteHalf(source, 6, 1f);

        using HdrRgba16FloatBuffer buffer = HdrRgba16FloatBuffer.CopyFrom(source, 8, 1, 1);

        Assert.Equal(0f, ReadHalf(buffer.GetRowSpan(0), 0));
        Assert.Equal(65504f, ReadHalf(buffer.GetRowSpan(0), 2));
        Assert.Equal(-65504f, ReadHalf(buffer.GetRowSpan(0), 4));
        Assert.Equal(1f, ReadHalf(buffer.GetRowSpan(0), 6));
    }

    [Fact]
    public void Clone_HasIndependentPixelOwnership()
    {
        byte[] source = new byte[8];
        WriteHalf(source, 0, 4f);

        using HdrRgba16FloatBuffer original = HdrRgba16FloatBuffer.CopyFrom(source, 8, 1, 1);
        using HdrRgba16FloatBuffer clone = original.Clone();
        original.Dispose();

        Assert.Equal(4f, ReadHalf(clone.GetRowSpan(0), 0));
    }

    [Fact]
    public void CopyFromPointer_CopiesIntoDestinationRectangleAndSanitizesRows()
    {
        const int sourceStride = 24;
        byte[] source = Enumerable.Repeat((byte)0x7A, sourceStride * 2).ToArray();
        WriteHalf(source, 0, 2f);
        WriteHalf(source, 2, float.NaN);
        WriteHalf(source, sourceStride, float.PositiveInfinity);
        WriteHalf(source, sourceStride + 2, 3f);
        GCHandle sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);

        try
        {
            using var destination = new HdrRgba16FloatBuffer(3, 4);
            destination.CopyFrom(
                sourceHandle.AddrOfPinnedObject(),
                sourceStride,
                1,
                2,
                new Point(1, 1));

            Assert.Equal(0f, ReadHalf(destination.GetRowSpan(0), 0));
            Assert.Equal(2f, ReadHalf(destination.GetRowSpan(1), 8));
            Assert.Equal(0f, ReadHalf(destination.GetRowSpan(1), 10));
            Assert.Equal(65504f, ReadHalf(destination.GetRowSpan(2), 8));
            Assert.Equal(3f, ReadHalf(destination.GetRowSpan(2), 10));
            Assert.Equal(0f, ReadHalf(destination.GetRowSpan(3), 0));
        }
        finally
        {
            sourceHandle.Free();
        }
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
