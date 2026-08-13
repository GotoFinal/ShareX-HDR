using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrToSdrContentAwareMaskTests
{
    private const int Width = 256;
    private const int Height = 256;
    private const float PaperWhiteScRgb = 203f / 80f;

    [Fact]
    public void Mask_DoesNotBridgeDarkGapBetweenHdrImages()
    {
        byte[] pixels = CreateSolidFrame(0.02f);
        FillRectangle(pixels, new Rectangle(32, 16, 192, 64), 4f);
        FillRectangle(pixels, new Rectangle(32, 176, 192, 64), 4f);

        byte[] mask = CreateMask(pixels);

        Assert.InRange(mask[48 * Width + 128], 240, 255);
        Assert.InRange(mask[208 * Width + 128], 240, 255);
        Assert.InRange(mask[128 * Width + 128], 0, 15);
    }

    [Fact]
    public void Mask_PreservesHoleForNonTileAlignedSdrOccluder()
    {
        byte[] pixels = CreateSolidFrame(4f);
        FillRectangle(pixels, new Rectangle(93, 91, 125, 133), 0.5f);

        byte[] mask = CreateMask(pixels);

        Assert.InRange(mask[48 * Width + 48], 240, 255);
        Assert.InRange(mask[160 * Width + 160], 0, 15);
        Assert.InRange(mask[240 * Width + 240], 240, 255);
    }

    [Fact]
    public void Mask_FourKPerformanceProbeKeepsSeparatedContent()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_MASK_PERFORMANCE_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        const int width = 3840;
        const int height = 2160;
        byte[] pixels = CreateSolidFrame(width, height, 0.02f);
        FillRectangle(pixels, width, new Rectangle(160, 120, 1600, 760), 4f);
        FillRectangle(pixels, width, new Rectangle(160, 1240, 1600, 760), 4f);
        FillRectangle(pixels, width, new Rectangle(917, 1391, 701, 489), 0.5f);

        var stopwatch = Stopwatch.StartNew();
        byte[] mask = CreateMask(pixels, width, height);
        stopwatch.Stop();

        Assert.InRange(mask[500 * width + 500], 240, 255);
        Assert.InRange(mask[1050 * width + 500], 0, 15);
        Assert.InRange(mask[1600 * width + 1200], 0, 15);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"4K content-aware mask creation took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
        Console.WriteLine($"4K content-aware mask: {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static byte[] CreateMask(byte[] pixels)
    {
        return CreateMask(pixels, Width, Height);
    }

    private static byte[] CreateMask(byte[] pixels, int width, int height)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return HdrToSdrToneMapper.CreateToneMapMaskR8(
                handle.AddrOfPinnedObject(),
                width * HdrRgba16FloatBuffer.BytesPerPixel,
                width,
                height,
                PaperWhiteScRgb);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] CreateSolidFrame(float value)
    {
        return CreateSolidFrame(Width, Height, value);
    }

    private static byte[] CreateSolidFrame(int width, int height, float value)
    {
        byte[] pixels = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];
        FillRectangle(pixels, width, new Rectangle(0, 0, width, height), value);
        return pixels;
    }

    private static void FillRectangle(byte[] pixels, Rectangle rectangle, float value)
    {
        FillRectangle(pixels, Width, rectangle, value);
    }

    private static void FillRectangle(byte[] pixels, int width, Rectangle rectangle, float value)
    {
        ushort color = BitConverter.HalfToUInt16Bits((Half)value);
        ushort alpha = BitConverter.HalfToUInt16Bits((Half)1f);

        for (int y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            for (int x = rectangle.Left; x < rectangle.Right; x++)
            {
                int offset = (y * width + x) * HdrRgba16FloatBuffer.BytesPerPixel;
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset), color);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 2), color);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 4), color);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 6), alpha);
            }
        }
    }
}
