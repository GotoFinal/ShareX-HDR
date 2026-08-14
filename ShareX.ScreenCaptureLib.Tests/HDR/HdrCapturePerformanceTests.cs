using ShareX.ScreenCaptureLib;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrCapturePerformanceTests
{
    private readonly ITestOutputHelper output;

    public HdrCapturePerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

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
        MeasurePipeline("virtual-screen-pipeline", virtualScreen);
    }

    private void Measure(string name, Rectangle bounds)
    {
        var stopwatch = Stopwatch.StartNew();
        bool captured = WindowsGraphicsCapture.TryCaptureHdr(
            bounds,
            null,
            new HdrCaptureSettings(),
            out HdrImageDocument document);
        stopwatch.Stop();

        using (document)
        {
            output.WriteLine(
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

    private void MeasurePipeline(string name, Rectangle bounds)
    {
        var settings = new HdrCaptureSettings();
        var captureTimer = Stopwatch.StartNew();
        bool captured = WindowsGraphicsCapture.TryCaptureHdr(
            bounds,
            null,
            settings,
            out HdrImageDocument document);
        captureTimer.Stop();

        using (document)
        {
            if (!captured)
            {
                output.WriteLine($"HDR pipeline probe: {name}, captured=False.");
                return;
            }

            var normalizeTimer = Stopwatch.StartNew();
            document.NormalizeMixedMonitorBrightness(settings);
            normalizeTimer.Stop();
            document.CaptureWindowRegions(settings);
            var previewTimes = new List<double>();
            for (int previewIndex = 0; previewIndex < 3; previewIndex++)
            {
                var previewTimer = Stopwatch.StartNew();
                using Bitmap preview = document.CreateSdrPreview(settings);
                previewTimer.Stop();
                previewTimes.Add(previewTimer.Elapsed.TotalMilliseconds);
            }

            output.WriteLine(
                $"HDR pipeline probe: {name} {bounds.Width}x{bounds.Height}, " +
                $"capture={captureTimer.Elapsed.TotalMilliseconds:F1} ms, " +
                $"normalize={normalizeTimer.Elapsed.TotalMilliseconds:F1} ms, " +
                $"preview=[{string.Join(", ", previewTimes.Select(x => $"{x:F1}"))}] ms, " +
                $"warmTotal={captureTimer.Elapsed.TotalMilliseconds + normalizeTimer.Elapsed.TotalMilliseconds + previewTimes[^1]:F1} ms.");
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
