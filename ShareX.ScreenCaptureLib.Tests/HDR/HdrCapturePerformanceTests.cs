using ShareX.ScreenCaptureLib;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrCapturePerformanceTests
{
    [Fact]
    public void WindowsGraphicsCapture_RepresentativeRegionsPerformanceProbe()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_CAPTURE_PERFORMANCE_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Rectangle monitor = Screen.PrimaryScreen?.Bounds ?? virtualScreen;
        Rectangle region = CenterRectangle(
            monitor,
            Math.Min(1280, monitor.Width),
            Math.Min(720, monitor.Height));

        Measure("small-region", region);
        Measure("single-monitor", monitor);
        Measure("virtual-screen", virtualScreen);
    }

    private static void Measure(string name, Rectangle bounds)
    {
        var stopwatch = Stopwatch.StartNew();
        bool captured = WindowsGraphicsCapture.TryCaptureHdr(bounds, null, out HdrImageDocument document);
        stopwatch.Stop();

        using (document)
        {
            Console.WriteLine(
                $"HDR capture probe: {name} {bounds.Width}x{bounds.Height}, " +
                $"captured={captured}, elapsed={stopwatch.Elapsed.TotalMilliseconds:F1} ms.");

            if (captured)
            {
                Assert.Equal(bounds.Size, new Size(
                    document.MasterPixels.Width,
                    document.MasterPixels.Height));
            }
        }
    }

    private static Rectangle CenterRectangle(Rectangle bounds, int width, int height)
    {
        return new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
    }
}
