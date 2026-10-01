using ShareX.HelpersLib;
using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrCapturePerformanceTests
{
    private readonly ITestOutputHelper output;

    public HdrCapturePerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void WindowsGraphicsCapture_MixedDesktopRepeatedPerformanceProbe()
    {
        if (Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_CAPTURE_PERFORMANCE_TESTS") != "1")
        {
            return;
        }

        // Initialize the test process before Screen or the capture worker caches
        // coordinates. Otherwise a mixed-DPI desktop can be measured at a
        // smaller, virtualized size instead of its physical pixel dimensions.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Dictionary<string, Rectangle> physicalBounds = GetPhysicalDisplayBounds();
        foreach (Screen screen in Screen.AllScreens)
        {
            Assert.Equal(physicalBounds[screen.DeviceName], screen.Bounds);
            output.WriteLine($"Physical display probe: {screen.DeviceName} bounds={screen.Bounds} " +
                $"hdr={WindowsGraphicsCapture.HasActiveHdrDisplay(screen.Bounds)}.");
        }

        Screen? hdrScreen = Screen.AllScreens.FirstOrDefault(screen =>
            WindowsGraphicsCapture.HasActiveHdrDisplay(screen.Bounds));
        Assert.True(hdrScreen != null, "Enable an HDR display before running the mixed-HDR performance comparison.");
        Assert.Contains(Screen.AllScreens, screen =>
            !WindowsGraphicsCapture.HasActiveHdrDisplay(screen.Bounds));
        WindowsGraphicsCapture.Prewarm();

        if (Environment.GetEnvironmentVariable("SHAREX_HDR_CAPTURE_PROBE_SCOPE") != "mixed")
        {
            Rectangle small = CenterRectangle(hdrScreen!.Bounds,
                Math.Min(1280, hdrScreen.Bounds.Width), Math.Min(720, hdrScreen.Bounds.Height));
            MeasureRepeatedPipeline("hdr-region", small, requireHeadroom: false);
            MeasureRepeatedPipeline("hdr-monitor", hdrScreen.Bounds, requireHeadroom: true);
        }
        MeasureRepeatedPipeline("mixed-desktop", SystemInformation.VirtualScreen, requireHeadroom: true);
    }

    private void MeasureRepeatedPipeline(string name, Rectangle bounds, bool requireHeadroom)
    {
        const int repetitions = 7;
        var captureStages = new ConcurrentQueue<string>();
        using Process process = Process.GetCurrentProcess();
        foreach (HdrProcessingBackend backend in Enum.GetValues<HdrProcessingBackend>())
        {
            var settings = new HdrCaptureSettings { ProcessingBackend = backend };
            var captures = new List<double>();
            var freshPreviews = new List<double>();
            var cachedPreviews = new List<double>();
            var cpuTimes = new List<double>();
            var allocations = new List<double>();
            for (int iteration = -3; iteration < repetitions; iteration++)
            {
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                TimeSpan cpuBefore = process.TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                Action<string> previousSink = WindowsGraphicsCapture.PerformanceLogSink;
                WindowsGraphicsCapture.PerformanceLogSink = message =>
                {
                    if (message.Contains("sdr-native", StringComparison.Ordinal) ||
                        message.Contains("hdr-readback", StringComparison.Ordinal))
                    {
                        captureStages.Enqueue(message);
                    }
                };
                bool captured;
                HdrImageDocument document;
                try
                {
                    captured = WindowsGraphicsCapture.TryCaptureHdr(bounds, null, settings, out document);
                }
                finally
                {
                    WindowsGraphicsCapture.PerformanceLogSink = previousSink;
                }
                double captureMs = timer.Elapsed.TotalMilliseconds;
                Assert.True(captured);
                using (document)
                {
                    Assert.Equal(bounds.Size, new Size(document.MasterPixels.Width, document.MasterPixels.Height));
                    timer.Restart();
                    document.NormalizeMixedMonitorBrightness(settings);
                    double normalizeMs = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    document.CaptureWindowRegions(settings);
                    double windowsMs = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    using Bitmap fresh = CreateVerifiedPreview(document, settings, out int freshGpuSegments);
                    double freshMs = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    using Bitmap cached = CreateVerifiedPreview(document, settings, out int cachedGpuSegments);
                    double cachedMs = timer.Elapsed.TotalMilliseconds;
                    double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                    if (iteration < 0)
                    {
                        captureStages.Clear();
                        continue;
                    }

                    captures.Add(captureMs);
                    freshPreviews.Add(freshMs);
                    cachedPreviews.Add(cachedMs);
                    cpuTimes.Add(cpuMs);
                    allocations.Add(allocated / 1048576d);
                    output.WriteLine($"Repeated HDR probe: name={name} requestedBackend={backend} iteration={iteration} " +
                        $"size={bounds.Width}x{bounds.Height} capture={captureMs:F2} normalize={normalizeMs:F2} " +
                        $"windows={windowsMs:F2} freshPreview={freshMs:F2} cachedPreview={cachedMs:F2} " +
                        $"cpu={cpuMs:F2}ms allocated={allocated / 1048576d:F2}MiB " +
                        $"gpuSegments={freshGpuSegments}/{cachedGpuSegments}.");
                    while (captureStages.TryDequeue(out string? stage))
                    {
                        output.WriteLine($"Repeated capture stage: name={name} backend={backend} iteration={iteration} {stage}");
                    }
                    if (iteration == 0 || iteration == repetitions - 1)
                    {
                        HdrContentProbe content = MeasureHdrContent(document);
                        output.WriteLine($"Repeated HDR content: name={name} backend={backend} " +
                            $"iteration={iteration} sampled={content.SampleCount} headroom={content.HeadroomSampleCount} " +
                            $"({content.HeadroomPercentage:F3}%) peak={content.ObservedPeakNits:F2}nits " +
                            $"hdrFingerprint={content.HeadroomFingerprint:X16}.");
                        if (requireHeadroom)
                        {
                            Assert.True(content.HeadroomSampleCount >= 20,
                                "The comparison requires visible HDR content, not merely an HDR-enabled monitor.");
                        }
                        foreach (PreviewSegmentProbe segment in MeasurePreviewSegments(fresh, document.SourceSegments))
                        {
                            output.WriteLine($"Repeated preview content: name={name} backend={backend} " +
                                $"iteration={iteration} display={segment.DisplayDeviceName} hdr={segment.WasHdrActive} " +
                                $"meanLuma={segment.MeanLuma:F3} nearWhite={segment.NearWhitePercentage:F3}%.");
                        }
                    }
                }
            }

            output.WriteLine($"Repeated HDR summary: name={name} backend={backend} " +
                $"capture={Summarize(captures)} freshPreview={Summarize(freshPreviews)} " +
                $"cachedPreview={Summarize(cachedPreviews)} cpu={Summarize(cpuTimes)} " +
                $"allocatedMiB={Summarize(allocations)} (median/min/max).");
        }
    }

    [Fact]
    public void WindowsGraphicsCapture_NativeSdrCorrectnessProbe()
    {
        if (Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_CAPTURE_PERFORMANCE_TESTS") != "1")
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Screen sdrScreen = Screen.AllScreens.First(screen =>
            !WindowsGraphicsCapture.HasActiveHdrDisplay(screen.Bounds));
        using Bitmap before = Screenshot.CaptureRectangleNative(sdrScreen.Bounds);
        Rectangle bounds = SystemInformation.VirtualScreen;
        Assert.True(WindowsGraphicsCapture.TryCaptureHdr(bounds, null, new HdrCaptureSettings(),
            out HdrImageDocument document));
        using (document)
        {
            Assert.True(MeasureHdrContent(document).HeadroomSampleCount >= 20,
                "The live correctness probe requires visible HDR content.");
            using Bitmap after = Screenshot.CaptureRectangleNative(sdrScreen.Bounds);
            byte[] beforePixels = ReadBgrx(before, new Rectangle(Point.Empty, before.Size));
            byte[] afterPixels = ReadBgrx(after, new Rectangle(Point.Empty, after.Size));
            Rectangle destination = new Rectangle(sdrScreen.Bounds.X - bounds.X,
                sdrScreen.Bounds.Y - bounds.Y, sdrScreen.Bounds.Width, sdrScreen.Bounds.Height);
            foreach (HdrProcessingBackend backend in Enum.GetValues<HdrProcessingBackend>())
            {
                using Bitmap preview = CreateVerifiedPreview(document,
                    new HdrCaptureSettings { ProcessingBackend = backend }, out int gpuSegments);
                byte[] previewPixels = ReadBgrx(preview, destination);
                int stablePixels = 0, mismatchedPixels = 0, changedPixels = 0, maxDifference = 0;
                for (int offset = 0; offset < beforePixels.Length; offset += 4 * 16)
                {
                    if (!beforePixels.AsSpan(offset, 3).SequenceEqual(afterPixels.AsSpan(offset, 3)))
                    {
                        changedPixels++;
                        continue;
                    }
                    stablePixels++;
                    int difference = 0;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        difference = Math.Max(difference, Math.Abs(beforePixels[offset + channel] - previewPixels[offset + channel]));
                    }
                    if (difference != 0)
                    {
                        mismatchedPixels++;
                    }
                    maxDifference = Math.Max(maxDifference, difference);
                }
                output.WriteLine($"Native SDR correctness: requestedBackend={backend} gpuSegments={gpuSegments} " +
                    $"stablePixels={stablePixels} changedPixels={changedPixels} " +
                    $"mismatchedPixels={mismatchedPixels} maxChannelDifference={maxDifference}.");
                Assert.True(stablePixels >= 1000);
                // Matching endpoint references cannot exclude a transient frame
                // during capture. Report live differences; the deterministic
                // importer tests enforce exact SDR byte preservation.
            }
        }
    }

    private static Bitmap CreateVerifiedPreview(HdrImageDocument document, HdrCaptureSettings settings,
        out int gpuSegments)
    {
        int successfulGpuSegments = 0;
        Action<string> previousSink = GpuHdrToSdrToneMapper.PerformanceLogSink;
        GpuHdrToSdrToneMapper.PerformanceLogSink = message =>
        {
            if (message.StartsWith("HDR GPU preview input |", StringComparison.Ordinal))
            {
                successfulGpuSegments++;
            }
        };
        Bitmap preview = null!;
        try
        {
            preview = document.CreateSdrPreview(settings);
            if (settings.ProcessingBackend == HdrProcessingBackend.Gpu)
            {
                Assert.Equal(document.SourceSegments.Count, successfulGpuSegments);
            }
            gpuSegments = successfulGpuSegments;
            return preview;
        }
        catch
        {
            preview?.Dispose();
            throw;
        }
        finally
        {
            GpuHdrToSdrToneMapper.PerformanceLogSink = previousSink;
        }
    }

    private static byte[] ReadBgrx(Bitmap bitmap, Rectangle bounds)
    {
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = checked(bounds.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * bounds.Height)];
            for (int y = 0; y < bounds.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static string Summarize(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return $"{sorted[sorted.Length / 2]:F2}/{sorted[0]:F2}/{sorted[^1]:F2}";
    }

    private static Dictionary<string, Rectangle> GetPhysicalDisplayBounds()
    {
        var bounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput display).Success; outputIndex++)
                {
                    using (display)
                    {
                        OutputDescription description = display.Description;
                        if (description.AttachedToDesktop)
                        {
                            bounds[description.DeviceName] = Rectangle.FromLTRB(
                                description.DesktopCoordinates.Left, description.DesktopCoordinates.Top,
                                description.DesktopCoordinates.Right, description.DesktopCoordinates.Bottom);
                        }
                    }
                }
            }
        }
        return bounds;
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

        Action<string> previousCaptureSink = WindowsGraphicsCapture.PerformanceLogSink;
        WindowsGraphicsCapture.PerformanceLogSink = message => output.WriteLine(message);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            // ShareX performs this asynchronously at startup. Keep shader/device
            // initialization out of the capture-path numbers while retaining the
            // first allocation and analysis cost for each captured size.
            WindowsGraphicsCapture.Prewarm();

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
        finally
        {
            WindowsGraphicsCapture.PerformanceLogSink = previousCaptureSink;
        }
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
            Action<string> previousPerformanceSink =
                GpuHdrToSdrToneMapper.PerformanceLogSink;
            GpuHdrToSdrToneMapper.PerformanceLogSink = message =>
                output.WriteLine(message);
            try
            {
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
            }
            finally
            {
                GpuHdrToSdrToneMapper.PerformanceLogSink = previousPerformanceSink;
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

            MeasureBackendComparison(document);
            MeasureOutputConversionComparison(document);
        }
    }

    [Fact]
    public void WindowsGraphicsCapture_OutputConversionPerformanceProbe()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_OUTPUT_PERFORMANCE_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        bool captured = WindowsGraphicsCapture.TryCaptureHdr(
            virtualScreen,
            null,
            new HdrCaptureSettings(),
            out HdrImageDocument document);
        using (document)
        {
            Assert.True(captured);
            MeasureOutputConversionComparison(document);
            MeasureOutputEncodingComparison(document);
            MeasureAvifSpeedAndThreadComparison(document);
            MeasureConcurrentClipboardPreparation(document);
        }
    }

    private void MeasureOutputConversionComparison(HdrImageDocument source)
    {
        foreach (HdrPqPixelLayout layout in Enum.GetValues<HdrPqPixelLayout>())
        {
            foreach (HdrProcessingBackend backend in Enum.GetValues<HdrProcessingBackend>())
            {
                var timer = Stopwatch.StartNew();
                HdrPqPixelConversionResult conversion = HdrPqPixelConverter.Convert(
                    source.MasterPixels,
                    1000f,
                    layout,
                    backend);
                timer.Stop();
                output.WriteLine(
                    $"HDR output conversion probe: requested={backend}, actual={conversion.Backend}, " +
                    $"layout={layout}, size={source.MasterPixels.Width}x{source.MasterPixels.Height}, " +
                    $"bytes={conversion.Pixels.Length}, maxCll={conversion.MaxCll:F1}, " +
                    $"maxFall={conversion.MaxFall:F1}, elapsed={timer.Elapsed.TotalMilliseconds:F1} ms.");
            }
        }
    }

    private void MeasureOutputEncodingComparison(HdrImageDocument source)
    {
        foreach (IHdrImageEncoder encoder in new IHdrImageEncoder[]
        {
            new AvifHdrImageEncoder(),
            new HdrPngImageEncoder()
        })
        {
            if (!HdrEncoderCapabilities.TryGetAvailability(encoder.Format, out _))
            {
                continue;
            }

            foreach (HdrProcessingBackend backend in Enum.GetValues<HdrProcessingBackend>())
            {
                using var encoded = new MemoryStream();
                var options = new HdrImageEncodingOptions
                {
                    ProcessingBackend = backend,
                    MasteringDisplayMaximumNits = 1000f,
                    AvifQuality = 90,
                    AvifSpeed = 8
                };
                var timer = Stopwatch.StartNew();
                HdrEncodedImageInfo info = encoder.Encode(
                    source.MasterPixels,
                    encoded,
                    options);
                timer.Stop();
                Assert.True(HdrEncodedImageVerifier.Verify(
                    encoded,
                    info.Format,
                    source.MasterPixels.Width,
                    source.MasterPixels.Height));
                output.WriteLine(
                    $"HDR output encode probe: requested={backend}, format={info.Format}, " +
                    $"size={source.MasterPixels.Width}x{source.MasterPixels.Height}, " +
                    $"bytes={encoded.Length}, elapsed={timer.Elapsed.TotalMilliseconds:F1} ms.");
            }
        }
    }

    private void MeasureAvifSpeedAndThreadComparison(HdrImageDocument source)
    {
        if (!HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.Avif, out _))
        {
            return;
        }

        string? previousThreadLimit = Environment.GetEnvironmentVariable("SHAREX_AVIF_MAX_THREADS");
        try
        {
            foreach (int threadLimit in new[] { 16, 32 })
            {
                Environment.SetEnvironmentVariable(
                    "SHAREX_AVIF_MAX_THREADS",
                    threadLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (int speed in new[] { 8, 10 })
                {
                    using var encoded = new MemoryStream();
                    var options = new HdrImageEncodingOptions
                    {
                        ProcessingBackend = HdrProcessingBackend.Gpu,
                        MasteringDisplayMaximumNits = 1000f,
                        AvifQuality = 90,
                        AvifSpeed = speed
                    };
                    var timer = Stopwatch.StartNew();
                    new AvifHdrImageEncoder().Encode(source.MasterPixels, encoded, options);
                    timer.Stop();
                    output.WriteLine(
                        $"HDR AVIF tuning probe: threads={threadLimit}, speed={speed}, " +
                        $"bytes={encoded.Length}, elapsed={timer.Elapsed.TotalMilliseconds:F1} ms.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHAREX_AVIF_MAX_THREADS", previousThreadLimit);
        }
    }

    private void MeasureConcurrentClipboardPreparation(HdrImageDocument source)
    {
        if (!HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.Avif, out _))
        {
            return;
        }

        using Bitmap sdrPreview = source.CreateSdrPreview(new HdrCaptureSettings
        {
            ProcessingBackend = HdrProcessingBackend.Gpu
        });
        var options = new HdrImageEncodingOptions
        {
            ProcessingBackend = HdrProcessingBackend.Gpu,
            MasteringDisplayMaximumNits = 1000f,
            AvifQuality = 90,
            AvifSpeed = 8
        };

        var sequentialTimer = Stopwatch.StartNew();
        using (var encoded = new MemoryStream())
        {
            new AvifHdrImageEncoder().Encode(source.MasterPixels, encoded, options);
        }
        using (HdrClipboardFallbackData fallback = PrepareFallback())
        {
        }
        sequentialTimer.Stop();

        var concurrentTimer = Stopwatch.StartNew();
        Task<HdrClipboardFallbackData> preparationTask = Task.Run(PrepareFallback);
        using (var encoded = new MemoryStream())
        {
            new AvifHdrImageEncoder().Encode(source.MasterPixels, encoded, options);
        }
        using (HdrClipboardFallbackData fallback = preparationTask.GetAwaiter().GetResult())
        {
        }
        concurrentTimer.Stop();

        output.WriteLine(
            $"HDR clipboard overlap probe: size={source.MasterPixels.Width}x{source.MasterPixels.Height}, " +
            $"sequential={sequentialTimer.Elapsed.TotalMilliseconds:F1} ms, " +
            $"concurrent={concurrentTimer.Elapsed.TotalMilliseconds:F1} ms, " +
            $"saved={sequentialTimer.Elapsed.TotalMilliseconds - concurrentTimer.Elapsed.TotalMilliseconds:F1} ms.");

        HdrClipboardFallbackData PrepareFallback()
        {
            using var companionPng = new MemoryStream();
            sdrPreview.Save(companionPng, ImageFormat.Png);
            return ClipboardHelpers.PrepareHdrClipboardFallback(sdrPreview, companionPng);
        }
    }

    private void MeasureBackendComparison(HdrImageDocument source)
    {
        using HdrImageDocument cpuDocument = source.Clone();
        using HdrImageDocument gpuDocument = source.Clone();
        var cpuSettings = new HdrCaptureSettings
        {
            ProcessingBackend = HdrProcessingBackend.Cpu
        };
        var gpuSettings = new HdrCaptureSettings
        {
            ProcessingBackend = HdrProcessingBackend.Gpu
        };

        var cpuTimer = Stopwatch.StartNew();
        using Bitmap cpuPreview = cpuDocument.CreateSdrPreview(cpuSettings);
        cpuTimer.Stop();

        var gpuTimer = Stopwatch.StartNew();
        using Bitmap gpuPreview = gpuDocument.CreateSdrPreview(gpuSettings);
        gpuTimer.Stop();

        PreviewDifference difference = MeasurePreviewDifference(cpuPreview, gpuPreview);
        output.WriteLine(
            $"HDR backend comparison: cpuFirst={cpuTimer.Elapsed.TotalMilliseconds:F1} ms, " +
            $"gpuFirst={gpuTimer.Elapsed.TotalMilliseconds:F1} ms, " +
            $"sampledChannels={difference.SampleCount}, " +
            $"meanAbsDifference={difference.MeanAbsoluteDifference:F3}, " +
            $"maxAbsDifference={difference.MaximumAbsoluteDifference}.");
    }

    private static PreviewDifference MeasurePreviewDifference(Bitmap first, Bitmap second)
    {
        Assert.Equal(first.Size, second.Size);
        const int sampleStep = 4;
        Rectangle bounds = new Rectangle(Point.Empty, first.Size);
        BitmapData firstData = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData secondData = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int rowLength = checked(first.Width * 4);
            byte[] firstRow = new byte[rowLength];
            byte[] secondRow = new byte[rowLength];
            long sampleCount = 0;
            long absoluteDifference = 0;
            int maximumDifference = 0;

            for (int y = 0; y < first.Height; y += sampleStep)
            {
                int firstY = firstData.Stride >= 0 ? y : first.Height - 1 - y;
                int secondY = secondData.Stride >= 0 ? y : second.Height - 1 - y;
                Marshal.Copy(
                    IntPtr.Add(firstData.Scan0, firstY * Math.Abs(firstData.Stride)),
                    firstRow,
                    0,
                    rowLength);
                Marshal.Copy(
                    IntPtr.Add(secondData.Scan0, secondY * Math.Abs(secondData.Stride)),
                    secondRow,
                    0,
                    rowLength);

                for (int x = 0; x < first.Width; x += sampleStep)
                {
                    int offset = x * 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int difference = Math.Abs(firstRow[offset + channel] - secondRow[offset + channel]);
                        absoluteDifference += difference;
                        maximumDifference = Math.Max(maximumDifference, difference);
                        sampleCount++;
                    }
                }
            }

            return new PreviewDifference(
                sampleCount,
                sampleCount > 0 ? absoluteDifference / (double)sampleCount : 0d,
                maximumDifference);
        }
        finally
        {
            second.UnlockBits(secondData);
            first.UnlockBits(firstData);
        }
    }

    private static HdrContentProbe MeasureHdrContent(HdrImageDocument document)
    {
        const int sampleStep = 4;
        int sampleCount = 0;
        int headroomSampleCount = 0;
        float observedPeakNits = 0f;
        ulong headroomFingerprint = 14695981039346656037;

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
                        // Check the actual retained HDR highlights, including
                        // placement, rather than comparing only an enabled flag.
                        headroomFingerprint = unchecked((headroomFingerprint ^
                            BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(offset, 8))) * 1099511628211);
                        headroomFingerprint = unchecked((headroomFingerprint ^ (uint)x ^ ((ulong)(uint)y << 32)) * 1099511628211);
                    }

                    observedPeakNits = Math.Max(
                        observedPeakNits,
                        maximum * HdrRgba16FloatBuffer.ReferenceWhiteNits);
                }
            }
        }

        return new HdrContentProbe(sampleCount, headroomSampleCount, observedPeakNits, headroomFingerprint);
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
        float ObservedPeakNits,
        ulong HeadroomFingerprint)
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

    private readonly record struct PreviewDifference(
        long SampleCount,
        double MeanAbsoluteDifference,
        int MaximumAbsoluteDifference);

    private static Rectangle CenterRectangle(Rectangle bounds, int width, int height)
    {
        return new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
    }
}
