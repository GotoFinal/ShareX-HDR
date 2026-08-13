using ShareX.ScreenCaptureLib;
using System.Drawing;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrWindowRegionDocumentTests
{
    [Fact]
    public void Crop_PreservesFrontToBackWindowOrderAndLocalCoordinates()
    {
        var bounds = new Rectangle(100, 200, 200, 120);
        using var pixels = new HdrRgba16FloatBuffer(bounds.Width, bounds.Height);
        var segment = new HdrCaptureSourceSegment(
            new Rectangle(Point.Empty, bounds.Size),
            "DISPLAY",
            true,
            203f,
            1000f);
        HdrWindowRegion[] windows =
        {
            new HdrWindowRegion(new Rectangle(80, 30, 100, 80)),
            new HdrWindowRegion(new Rectangle(0, 0, 200, 120))
        };
        using var document = new HdrImageDocument(
            bounds,
            pixels.Clone(),
            new[] { segment },
            windows);

        using HdrImageDocument cropped = document.CropToLocalRectangle(
            new Rectangle(50, 20, 100, 80));

        Assert.Equal(2, cropped.WindowRegions.Count);
        Assert.Equal(new Rectangle(30, 10, 70, 70), cropped.WindowRegions[0].Bounds);
        Assert.Equal(new Rectangle(0, 0, 100, 80), cropped.WindowRegions[1].Bounds);
    }

    [Fact]
    public void Clone_PreservesWindowSnapshot()
    {
        var bounds = new Rectangle(0, 0, 32, 32);
        using var pixels = new HdrRgba16FloatBuffer(bounds.Width, bounds.Height);
        var segment = new HdrCaptureSourceSegment(
            new Rectangle(Point.Empty, bounds.Size),
            "DISPLAY",
            true,
            203f,
            1000f);
        using var document = new HdrImageDocument(
            bounds,
            pixels.Clone(),
            new[] { segment },
            new[] { new HdrWindowRegion(new Rectangle(3, 4, 20, 21)) });

        using HdrImageDocument clone = document.Clone();

        Assert.Single(clone.WindowRegions);
        Assert.Equal(new Rectangle(3, 4, 20, 21), clone.WindowRegions[0].Bounds);
    }

    [Fact]
    public void Crop_PreservesWindowIdentityPromotionAndClientBounds()
    {
        var bounds = new Rectangle(100, 200, 200, 120);
        using var pixels = new HdrRgba16FloatBuffer(bounds.Width, bounds.Height);
        var segment = new HdrCaptureSourceSegment(
            new Rectangle(Point.Empty, bounds.Size),
            "DISPLAY",
            true,
            203f,
            1000f);
        var window = new HdrWindowRegion(
            new Rectangle(80, 30, 100, 80),
            new Rectangle(90, 40, 70, 50),
            42,
            123,
            456,
            HdrWindowPromotionKind.ForceHdr);
        using var document = new HdrImageDocument(
            bounds,
            pixels.Clone(),
            new[] { segment },
            new[] { window });

        using HdrImageDocument cropped = document.CropToLocalRectangle(
            new Rectangle(50, 20, 100, 80));

        HdrWindowRegion result = Assert.Single(cropped.WindowRegions);
        Assert.Equal(new Rectangle(30, 10, 70, 70), result.Bounds);
        Assert.Equal(new Rectangle(40, 20, 60, 50), result.ContentBounds);
        Assert.Equal(42, result.WindowHandle);
        Assert.Equal(123, result.ProcessId);
        Assert.Equal(456, result.ProcessStartTimeUtcTicks);
        Assert.Equal(HdrWindowPromotionKind.ForceHdr, result.PromotionKind);
    }
}
