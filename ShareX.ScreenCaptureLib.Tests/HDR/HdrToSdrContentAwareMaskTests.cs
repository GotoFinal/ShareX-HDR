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
    public void PreciseMask_DoesNotCarveHoleInsideDetectedHdrRegion()
    {
        byte[] pixels = CreateSolidFrame(4f);
        FillRectangle(pixels, new Rectangle(93, 91, 125, 133), 0.5f);

        byte[] mask = CreateMask(pixels, HdrToneMappingMode.ContentAware);

        Assert.InRange(mask[48 * Width + 48], 240, 255);
        Assert.InRange(mask[160 * Width + 160], 240, 255);
        Assert.InRange(mask[240 * Width + 240], 240, 255);
    }

    [Fact]
    public void PreciseMask_UsesOccludingWindowAsHardBoundary()
    {
        var occluder = new Rectangle(93, 91, 125, 133);
        byte[] pixels = CreateSolidFrame(4f);
        FillRectangle(pixels, occluder, 0.5f);
        Rectangle[] windows =
        {
            occluder,
            new Rectangle(0, 0, Width, Height)
        };

        byte[] mask = CreateMask(
            pixels,
            HdrToneMappingMode.ContentAware,
            windows);

        Assert.InRange(mask[48 * Width + 48], 240, 255);
        Assert.InRange(mask[160 * Width + 160], 0, 5);
        Assert.InRange(mask[240 * Width + 240], 240, 255);
    }

    [Fact]
    public void PerWindowMask_MapsWholeVisibleHdrWindowButNotOccluder()
    {
        var occluder = new Rectangle(96, 80, 112, 144);
        byte[] pixels = CreateSolidFrame(0.5f);
        FillRectangle(pixels, new Rectangle(16, 16, 64, 64), 4f);
        Rectangle[] windows =
        {
            occluder,
            new Rectangle(0, 0, Width, Height)
        };

        byte[] mask = CreateMask(
            pixels,
            HdrToneMappingMode.PerWindow,
            windows);

        Assert.Equal(byte.MaxValue, mask[32 * Width + 32]);
        Assert.Equal(byte.MaxValue, mask[240 * Width + 240]);
        Assert.Equal((byte)0, mask[160 * Width + 160]);
    }

    [Fact]
    public void PreciseMask_DoesNotSpillFromHdrWindowIntoAdjacentSdrWindow()
    {
        byte[] pixels = CreateSolidFrame(0.5f);
        FillRectangle(pixels, new Rectangle(0, 0, Width / 2, Height), 4f);
        Rectangle[] windows =
        {
            new Rectangle(0, 0, Width / 2, Height),
            new Rectangle(Width / 2, 0, Width / 2, Height)
        };

        byte[] mask = CreateMask(
            pixels,
            HdrToneMappingMode.ContentAware,
            windows);

        Assert.InRange(mask[128 * Width + 120], 240, 255);
        Assert.InRange(mask[128 * Width + 136], 0, 5);
    }

    [Fact]
    public void PerWindowMask_FallsBackToPreciseWhenWindowMetadataIsUnavailable()
    {
        byte[] pixels = CreateSolidFrame(0.5f);
        FillRectangle(pixels, new Rectangle(48, 48, 160, 160), 4f);

        byte[] mask = CreateMask(pixels, HdrToneMappingMode.PerWindow);

        Assert.InRange(mask[128 * Width + 128], 240, 255);
        Assert.InRange(mask[8 * Width + 8], 0, 15);
    }

    [Fact]
    public void PreciseMask_ForceHdrMapsVisibleGameClientButNotTitleBarOrOccluder()
    {
        HdrToSdrToneMapper.ClearWindowPromotionCacheForTests();
        byte[] pixels = CreateSolidFrame(0.5f);
        var occluder = new Rectangle(96, 80, 112, 144);
        HdrWindowRegion[] windows =
        {
            new HdrWindowRegion(occluder),
            new HdrWindowRegion(
                new Rectangle(0, 0, Width, Height),
                new Rectangle(0, 24, Width, Height - 24),
                100,
                1000,
                10000,
                HdrWindowPromotionKind.ForceHdr)
        };

        byte[] mask = CreateMaskWithWindowRegions(
            pixels,
            HdrToneMappingMode.ContentAware,
            windows);

        Assert.Equal((byte)0, mask[12 * Width + 32]);
        Assert.Equal(byte.MaxValue, mask[48 * Width + 32]);
        Assert.Equal((byte)0, mask[160 * Width + 160]);
        Assert.Equal(byte.MaxValue, mask[240 * Width + 240]);
    }

    [Fact]
    public void PreciseMask_AutomaticFullscreenPromotionRequiresCoherentHdrEvidenceAndCachesIdentity()
    {
        HdrToSdrToneMapper.ClearWindowPromotionCacheForTests();
        var candidate = new HdrWindowRegion(
            new Rectangle(0, 0, Width, Height),
            new Rectangle(0, 0, Width, Height),
            200,
            2000,
            20000,
            HdrWindowPromotionKind.DetectFullscreenHdr);
        byte[] firstFrame = CreateSolidFrame(0.5f);
        FillRectangle(firstFrame, new Rectangle(2, 2, 4, 4), 4f);

        byte[] detectedMask = CreateMaskWithWindowRegions(
            firstFrame,
            HdrToneMappingMode.ContentAware,
            new[] { candidate });
        byte[] cachedMask = CreateMaskWithWindowRegions(
            CreateSolidFrame(0.5f),
            HdrToneMappingMode.ContentAware,
            new[] { candidate });

        Assert.Equal(byte.MaxValue, detectedMask[240 * Width + 240]);
        Assert.Equal(byte.MaxValue, cachedMask[240 * Width + 240]);
    }

    [Fact]
    public void PreciseMask_AutomaticPromotionDoesNotUseSparseEvidenceOrDifferentProcessLifetime()
    {
        HdrToSdrToneMapper.ClearWindowPromotionCacheForTests();
        var firstLifetime = new HdrWindowRegion(
            new Rectangle(0, 0, Width, Height),
            new Rectangle(0, 0, Width, Height),
            300,
            3000,
            30000,
            HdrWindowPromotionKind.DetectFullscreenHdr);
        byte[] sparseFrame = CreateSolidFrame(0.5f);
        FillRectangle(sparseFrame, new Rectangle(2, 2, 1, 2), 4f);

        byte[] sparseMask = CreateMaskWithWindowRegions(
            sparseFrame,
            HdrToneMappingMode.ContentAware,
            new[] { firstLifetime });

        byte[] strongFrame = CreateSolidFrame(0.5f);
        FillRectangle(strongFrame, new Rectangle(2, 2, 4, 4), 4f);
        _ = CreateMaskWithWindowRegions(
            strongFrame,
            HdrToneMappingMode.ContentAware,
            new[] { firstLifetime });
        var restartedProcess = new HdrWindowRegion(
            firstLifetime.Bounds,
            firstLifetime.ContentBounds,
            firstLifetime.WindowHandle,
            firstLifetime.ProcessId,
            firstLifetime.ProcessStartTimeUtcTicks + 1,
            firstLifetime.PromotionKind);
        byte[] restartedMask = CreateMaskWithWindowRegions(
            CreateSolidFrame(0.5f),
            HdrToneMappingMode.ContentAware,
            new[] { restartedProcess });

        Assert.InRange(sparseMask[240 * Width + 240], 0, 5);
        Assert.InRange(restartedMask[240 * Width + 240], 0, 5);
    }

    [Fact]
    public void WindowAwareMask_FourKPerformanceProbe()
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
        byte[] pixels = CreateSolidFrame(width, height, 4f);
        var occluder = new Rectangle(917, 691, 1401, 989);
        FillRectangle(pixels, width, occluder, 0.5f);

        var stopwatch = Stopwatch.StartNew();
        byte[] mask = CreateMask(
            pixels,
            width,
            height,
            HdrToneMappingMode.ContentAware,
            new[] { occluder, new Rectangle(0, 0, width, height) });
        stopwatch.Stop();

        Assert.InRange(mask[500 * width + 500], 240, 255);
        Assert.InRange(mask[1000 * width + 1200], 0, 5);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"4K window-aware mask creation took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
        Console.WriteLine($"4K window-aware mask: {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static byte[] CreateMask(
        byte[] pixels,
        HdrToneMappingMode mode,
        IReadOnlyList<Rectangle>? windows = null)
    {
        return CreateMask(pixels, Width, Height, mode, windows);
    }

    private static byte[] CreateMaskWithWindowRegions(
        byte[] pixels,
        HdrToneMappingMode mode,
        IReadOnlyList<HdrWindowRegion> windows)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return HdrToSdrToneMapper.CreateToneMapMaskR8WithWindowRegions(
                handle.AddrOfPinnedObject(),
                Width * HdrRgba16FloatBuffer.BytesPerPixel,
                Width,
                Height,
                PaperWhiteScRgb,
                mode,
                windows);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] CreateMask(
        byte[] pixels,
        int width,
        int height,
        HdrToneMappingMode mode,
        IReadOnlyList<Rectangle>? windows = null)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return HdrToSdrToneMapper.CreateToneMapMaskR8(
                handle.AddrOfPinnedObject(),
                width * HdrRgba16FloatBuffer.BytesPerPixel,
                width,
                height,
                PaperWhiteScRgb,
                mode,
                windows);
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
