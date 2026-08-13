using ShareX.ScreenCaptureLib;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShareX.ScreenCaptureLib.Tests;

public sealed class RegionCaptureTasksTests
{
    [Fact]
    public void GetRegionPathArea_UsesRetainedSelectionPathBounds()
    {
        using var selectedRegion = new GraphicsPath();
        selectedRegion.AddRectangle(new Rectangle(97, 23, 903, 1047));

        Rectangle result = RegionCaptureTasks.GetRegionPathArea(
            selectedRegion,
            new Size(2560, 1440));

        Assert.Equal(new Rectangle(97, 23, 903, 1047), result);
    }

    [Fact]
    public void GetRegionPathArea_ClipsToTheCapturedCanvasLikeResultRendering()
    {
        using var selectedRegion = new GraphicsPath();
        selectedRegion.AddRectangle(new Rectangle(-20, 30, 100, 90));

        Rectangle result = RegionCaptureTasks.GetRegionPathArea(
            selectedRegion,
            new Size(60, 80));

        Assert.Equal(new Rectangle(0, 30, 60, 50), result);
    }
}
