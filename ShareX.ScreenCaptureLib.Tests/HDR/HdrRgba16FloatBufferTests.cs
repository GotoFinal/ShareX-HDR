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

    [Fact]
    public void CopyFromPointer_FusesRgbScalingWithSanitizationAndPreservesAlpha()
    {
        const int pixelCount = 8;
        byte[] source = new byte[pixelCount * HdrRgba16FloatBuffer.BytesPerPixel];

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * HdrRgba16FloatBuffer.BytesPerPixel;
            WriteHalf(source, offset, 1f + pixel);
            WriteHalf(source, offset + 2, -0.5f * pixel);
            WriteHalf(source, offset + 4, pixel == 2 ? float.NaN : 3f);
            WriteHalf(source, offset + 6, 0.5f);
        }

        WriteHalf(source, 3 * HdrRgba16FloatBuffer.BytesPerPixel, float.PositiveInfinity);
        WriteHalf(source, 4 * HdrRgba16FloatBuffer.BytesPerPixel, float.NegativeInfinity);
        WriteHalf(source, 5 * HdrRgba16FloatBuffer.BytesPerPixel, 40000f);
        GCHandle sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);

        try
        {
            using var destination = new HdrRgba16FloatBuffer(pixelCount, 1);
            destination.CopyFrom(
                sourceHandle.AddrOfPinnedObject(),
                source.Length,
                pixelCount,
                1,
                Point.Empty,
                rgbScale: 2f);

            ReadOnlySpan<byte> row = destination.GetRowSpan(0);
            Assert.Equal(2f, ReadHalf(row, 0));
            Assert.Equal(6f, ReadHalf(row, 4));
            Assert.Equal(0.5f, ReadHalf(row, 6));
            Assert.Equal(0f, ReadHalf(row, 2 * HdrRgba16FloatBuffer.BytesPerPixel + 4));
            Assert.Equal(65504f, ReadHalf(row, 3 * HdrRgba16FloatBuffer.BytesPerPixel));
            Assert.Equal(-65504f, ReadHalf(row, 4 * HdrRgba16FloatBuffer.BytesPerPixel));
            Assert.Equal(65504f, ReadHalf(row, 5 * HdrRgba16FloatBuffer.BytesPerPixel));
        }
        finally
        {
            sourceHandle.Free();
        }
    }

    [Fact]
    public void CopyFromPointer_ParallelScaledRowsMatchExpectedValues()
    {
        const int width = 8;
        const int height = 64;
        const int sourceStride = width * HdrRgba16FloatBuffer.BytesPerPixel + 16;
        byte[] source = new byte[sourceStride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * sourceStride + x * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(source, offset, y + x + 0.5f);
                WriteHalf(source, offset + 2, -(x + 0.25f));
                WriteHalf(source, offset + 4, y == 17 && x == 3 ? float.NaN : 2f);
                WriteHalf(source, offset + 6, 0.75f);
            }
        }

        GCHandle sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);

        try
        {
            using var destination = new HdrRgba16FloatBuffer(width, height);
            destination.CopyFrom(
                sourceHandle.AddrOfPinnedObject(),
                sourceStride,
                width,
                height,
                Point.Empty,
                rgbScale: 0.5f);

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = destination.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    Assert.Equal((y + x + 0.5f) * 0.5f, ReadHalf(row, offset));
                    Assert.Equal(-(x + 0.25f) * 0.5f, ReadHalf(row, offset + 2));
                    Assert.Equal(y == 17 && x == 3 ? 0f : 1f, ReadHalf(row, offset + 4));
                    Assert.Equal(0.75f, ReadHalf(row, offset + 6));
                }
            }
        }
        finally
        {
            sourceHandle.Free();
        }
    }

    [Fact]
    public void CopyFromPointer_ParallelUnscaledRowsPreserveExactFiniteValues()
    {
        const int width = 32;
        const int height = 64;
        const int sourceStride = width * HdrRgba16FloatBuffer.BytesPerPixel + 32;
        byte[] source = new byte[sourceStride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * sourceStride + x * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(source, offset, y + x * 0.5f);
                WriteHalf(source, offset + 2, -x);
                WriteHalf(source, offset + 4, y == 31 && x == 7 ? float.NaN : 4f);
                WriteHalf(source, offset + 6, 0.75f);
            }
        }

        GCHandle sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            using var destination = new HdrRgba16FloatBuffer(width + 2, height + 2);
            destination.CopyFrom(
                sourceHandle.AddrOfPinnedObject(),
                sourceStride,
                width,
                height,
                new Point(1, 1));

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = destination.GetRowSpan(y + 1);
                for (int x = 0; x < width; x++)
                {
                    int offset = (x + 1) * HdrRgba16FloatBuffer.BytesPerPixel;
                    Assert.Equal((float)(Half)(y + x * 0.5f), ReadHalf(row, offset));
                    Assert.Equal((float)(Half)(-x), ReadHalf(row, offset + 2));
                    Assert.Equal(y == 31 && x == 7 ? 0f : 4f, ReadHalf(row, offset + 4));
                    Assert.Equal(0.75f, ReadHalf(row, offset + 6));
                }
            }
        }
        finally
        {
            sourceHandle.Free();
        }
    }

    [Fact]
    public void CopyScaledFromPointer_FusesNearestNeighborScalingAndRgbNormalization()
    {
        const int sourceWidth = 5;
        const int sourceHeight = 3;
        const int sourceStride = sourceWidth * HdrRgba16FloatBuffer.BytesPerPixel + 16;
        byte[] source = new byte[sourceStride * sourceHeight];

        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                int offset = y * sourceStride + x * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(source, offset, y * 10f + x + 0.5f);
                WriteHalf(source, offset + 2, -(y * 2f + x + 0.25f));
                WriteHalf(source, offset + 4, y == 1 && x == 3 ? float.NaN : 3f);
                WriteHalf(source, offset + 6, 0.5f);
            }
        }

        GCHandle sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            var destinationRectangle = new Rectangle(1, 2, 4, 5);
            using var destination = new HdrRgba16FloatBuffer(7, 9);
            destination.CopyScaledFrom(
                sourceHandle.AddrOfPinnedObject(),
                sourceStride,
                sourceWidth,
                sourceHeight,
                destinationRectangle,
                rgbScale: 2f);

            for (int destinationY = 0; destinationY < destinationRectangle.Height; destinationY++)
            {
                int sourceY = destinationY * sourceHeight / destinationRectangle.Height;
                ReadOnlySpan<byte> row = destination.GetRowSpan(destinationRectangle.Y + destinationY);
                for (int destinationX = 0; destinationX < destinationRectangle.Width; destinationX++)
                {
                    int sourceX = destinationX * sourceWidth / destinationRectangle.Width;
                    int offset = (destinationRectangle.X + destinationX) *
                        HdrRgba16FloatBuffer.BytesPerPixel;
                    Assert.Equal(
                        (float)(Half)((sourceY * 10f + sourceX + 0.5f) * 2f),
                        ReadHalf(row, offset));
                    Assert.Equal(
                        (float)(Half)(-(sourceY * 2f + sourceX + 0.25f) * 2f),
                        ReadHalf(row, offset + 2));
                    Assert.Equal(
                        sourceY == 1 && sourceX == 3 ? 0f : 6f,
                        ReadHalf(row, offset + 4));
                    Assert.Equal(0.5f, ReadHalf(row, offset + 6));
                }
            }
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
