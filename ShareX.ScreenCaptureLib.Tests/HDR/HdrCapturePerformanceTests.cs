using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

            HdrContentProbe content = MeasureHdrContent(document);
            output.WriteLine(
                $"HDR content probe: sampled={content.SampleCount}, " +
                $"headroom={content.HeadroomSampleCount} ({content.HeadroomPercentage:F2}%), " +
                $"peak={content.ObservedPeakNits:F1} nits.");
            Assert.True(
                content.HeadroomSampleCount >= 20,
                "The performance probe requires visible HDR content with at least 20 sampled pixels above SDR paper white.");
            Assert.Contains(document.SourceSegments, segment => segment.WasHdrActive);
            Assert.Contains(document.SourceSegments, segment => !segment.WasHdrActive);

            var normalizeTimer = Stopwatch.StartNew();
            document.NormalizeMixedMonitorBrightness(settings);
            normalizeTimer.Stop();
            document.CaptureWindowRegions(settings);
            var previewTimes = new List<double>();
            IReadOnlyList<PreviewSegmentProbe> previewContent =
                Array.Empty<PreviewSegmentProbe>();
            for (int previewIndex = 0; previewIndex < 3; previewIndex++)
            {
                var previewTimer = Stopwatch.StartNew();
                using Bitmap preview = document.CreateSdrPreview(settings);
                previewTimer.Stop();
                previewTimes.Add(previewTimer.Elapsed.TotalMilliseconds);

                if (previewIndex == 2)
                {
                    previewContent = MeasurePreviewSegments(preview, document.SourceSegments);
                }
            }

            foreach (PreviewSegmentProbe segment in previewContent)
            {
                output.WriteLine(
                    $"Mixed preview probe: display={segment.DisplayDeviceName}, hdr={segment.WasHdrActive}, " +
                    $"samples={segment.SampleCount}, meanLuma={segment.MeanLuma:F1}, " +
                    $"nearWhite={segment.NearWhitePercentage:F2}%.");
            }

            output.WriteLine(
                $"HDR pipeline probe: {name} {bounds.Width}x{bounds.Height}, " +
                $"capture={captureTimer.Elapsed.TotalMilliseconds:F1} ms, " +
                $"normalize={normalizeTimer.Elapsed.TotalMilliseconds:F1} ms, " +
                $"preview=[{string.Join(", ", previewTimes.Select(x => $"{x:F1}"))}] ms, " +
                $"warmTotal={captureTimer.Elapsed.TotalMilliseconds + normalizeTimer.Elapsed.TotalMilliseconds + previewTimes[^1]:F1} ms.");
        }
    }

    private static HdrContentProbe MeasureHdrContent(HdrImageDocument document)
    {
        const int sampleStep = 4;
        int sampleCount = 0;
        int headroomSampleCount = 0;
        float observedPeakNits = 0f;

        foreach (HdrCaptureSourceSegment segment in document.SourceSegments)
        {
            if (!segment.WasHdrActive)
            {
                continue;
            }

            float paperWhiteScRgb = segment.SdrWhiteNits / HdrRgba16FloatBuffer.ReferenceWhiteNits;
            float headroomThreshold = paperWhiteScRgb * 1.02f;
            Rectangle bounds = Rectangle.Intersect(
                segment.DestinationRectangle,
                new Rectangle(Point.Empty, new Size(
                    document.MasterPixels.Width,
                    document.MasterPixels.Height)));

            for (int y = bounds.Top; y < bounds.Bottom; y += sampleStep)
            {
                ReadOnlySpan<byte> row = document.MasterPixels.GetRowSpan(y);

                for (int x = bounds.Left; x < bounds.Right; x += sampleStep)
                {
                    int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                    float red = ReadHalf(row, offset);
                    float green = ReadHalf(row, offset + 2);
                    float blue = ReadHalf(row, offset + 4);
                    float maximum = Math.Max(red, Math.Max(green, blue));
                    sampleCount++;

                    if (maximum > headroomThreshold)
                    {
                        headroomSampleCount++;
                    }

                    observedPeakNits = Math.Max(
                        observedPeakNits,
                        maximum * HdrRgba16FloatBuffer.ReferenceWhiteNits);
                }
            }
        }

        return new HdrContentProbe(sampleCount, headroomSampleCount, observedPeakNits);
    }

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));

    private static IReadOnlyList<PreviewSegmentProbe> MeasurePreviewSegments(
        Bitmap preview,
        IReadOnlyList<HdrCaptureSourceSegment> segments)
    {
        const int sampleStep = 8;
        var result = new List<PreviewSegmentProbe>();
        Rectangle imageBounds = new Rectangle(Point.Empty, preview.Size);
        BitmapData data = preview.LockBits(
            imageBounds,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int absoluteStride = Math.Abs(data.Stride);

            foreach (HdrCaptureSourceSegment segment in segments)
            {
                Rectangle bounds = Rectangle.Intersect(segment.DestinationRectangle, imageBounds);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                byte[] row = new byte[checked(bounds.Width * 4)];
                long sampleCount = 0;
                long nearWhiteCount = 0;
                double lumaSum = 0d;

                for (int y = bounds.Top; y < bounds.Bottom; y += sampleStep)
                {
                    int memoryY = data.Stride >= 0 ? y : preview.Height - 1 - y;
                    IntPtr rowStart = IntPtr.Add(
                        data.Scan0,
                        memoryY * absoluteStride + bounds.Left * 4);
                    Marshal.Copy(rowStart, row, 0, row.Length);

                    for (int localX = 0; localX < bounds.Width; localX += sampleStep)
                    {
                        int offset = localX * 4;
                        byte blue = row[offset];
                        byte green = row[offset + 1];
                        byte red = row[offset + 2];
                        lumaSum += red * 0.2126d + green * 0.7152d + blue * 0.0722d;
                        if (Math.Max(red, Math.Max(green, blue)) >= 250)
                        {
                            nearWhiteCount++;
                        }

                        sampleCount++;
                    }
                }

                result.Add(new PreviewSegmentProbe(
                    segment.DisplayDeviceName,
                    segment.WasHdrActive,
                    sampleCount,
                    sampleCount > 0 ? lumaSum / sampleCount : 0d,
                    sampleCount > 0 ? nearWhiteCount * 100d / sampleCount : 0d));
            }
        }
        finally
        {
            preview.UnlockBits(data);
        }

        return result;
    }

    private readonly record struct HdrContentProbe(
        int SampleCount,
        int HeadroomSampleCount,
        float ObservedPeakNits)
    {
        public double HeadroomPercentage => SampleCount > 0
            ? HeadroomSampleCount * 100d / SampleCount
            : 0d;
    }

    private readonly record struct PreviewSegmentProbe(
        string DisplayDeviceName,
        bool WasHdrActive,
        long SampleCount,
        double MeanLuma,
        double NearWhitePercentage);

    private static Rectangle CenterRectangle(Rectangle bounds, int width, int height)
    {
        return new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
    }
}
