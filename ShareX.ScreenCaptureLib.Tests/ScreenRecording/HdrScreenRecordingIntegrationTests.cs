using System.Drawing;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.ScreenRecording;

public sealed class HdrScreenRecordingIntegrationTests
{
    [Fact]
    public async Task HdrDesktopRecording_ProducesVideoAndStopsPromptly()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_VIDEO_TESTS"),
            "1",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? ffmpegPath = Environment.GetEnvironmentVariable("SHAREX_FFMPEG_PATH");
        Assert.True(File.Exists(ffmpegPath), "SHAREX_FFMPEG_PATH must point to ffmpeg.exe.");

        Screen? hdrScreen = Screen.AllScreens.FirstOrDefault(screen =>
            WindowsGraphicsCapture.HasActiveHdrDisplay(screen.Bounds));
        Assert.NotNull(hdrScreen);

        string? configuredOutputPath = Environment.GetEnvironmentVariable("SHAREX_HDR_VIDEO_TEST_OUTPUT");
        string outputPath = configuredOutputPath ??
            Path.Combine(Path.GetTempPath(), $"ShareX-HDR-video-{Guid.NewGuid():N}.mp4");
        File.Delete(outputPath);
        int fps = int.TryParse(
            Environment.GetEnvironmentVariable("SHAREX_HDR_VIDEO_TEST_FPS"),
            out int configuredFps)
            ? Math.Clamp(configuredFps, 1, 60)
            : 5;
        bool recordNativeHdr10 = string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_HDR_VIDEO_TEST_NATIVE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        FFmpegVideoCodec videoCodec = Environment.GetEnvironmentVariable("SHAREX_HDR_VIDEO_TEST_ENCODER")?
            .ToLowerInvariant() switch
        {
            "nvenc" => FFmpegVideoCodec.h264_nvenc,
            "qsv" => FFmpegVideoCodec.h264_qsv,
            _ => FFmpegVideoCodec.libx264
        };

        var options = new ScreenRecordingOptions
        {
            IsRecording = true,
            FPS = fps,
            Duration = 0,
            CaptureArea = hdrScreen.Bounds,
            OutputPath = outputPath,
            DrawCursor = false,
            HdrMode = recordNativeHdr10
                ? ScreenRecordingHdrMode.NativeHdr10
                : ScreenRecordingHdrMode.ToneMapToSdr,
            FFmpeg = new FFmpegOptions
            {
                OverrideCLIPath = true,
                CLIPath = ffmpegPath!,
                VideoSource = FFmpegCaptureDevice.GDIGrab.Value,
                AudioSource = FFmpegCaptureDevice.None.Value,
                VideoCodec = videoCodec,
                UserArgs = "-init_hw_device vulkan=vk -filter_hw_device vk",
                x264_Preset = FFmpegPreset.ultrafast,
                x264_CRF = 35
            }
        };
        var screenshot = new Screenshot
        {
            CaptureCursor = false,
            UseHDRSupport = true,
            HdrSettings = new HdrCaptureSettings
            {
                ProcessingBackend = HdrProcessingBackend.Gpu
            }
        };

        try
        {
            using (var recorder = new ScreenRecorder(
                ScreenRecordOutput.FFmpeg,
                options,
                screenshot,
                hdrScreen.Bounds))
            {
                using var recordingStarted = new ManualResetEventSlim();
                recorder.RecordingStarted += recordingStarted.Set;
                CancellationToken cancellationToken = TestContext.Current.CancellationToken;
                Task recordingTask = Task.Run(recorder.StartRecording, cancellationToken);

                try
                {
                    Assert.True(
                        recordingStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken),
                        "The recorder did not start within five seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                    Stopwatch stopTimer = Stopwatch.StartNew();
                    recorder.StopRecording();
                    stopTimer.Stop();

                    Assert.True(
                        stopTimer.Elapsed < TimeSpan.FromSeconds(1),
                        $"StopRecording blocked for {stopTimer.Elapsed.TotalMilliseconds:F0} ms.");
                    await recordingTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                finally
                {
                    if (!recordingTask.IsCompleted)
                    {
                        recorder.StopRecording();
                        await recordingTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    }
                }
            }

            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 1024);

            if (recordNativeHdr10)
            {
                string ffprobePath = Path.Combine(
                    Path.GetDirectoryName(ffmpegPath!)!,
                    "ffprobe.exe");
                if (File.Exists(ffprobePath))
                {
                    string metadata = ProbeFirstVideoFrame(ffprobePath, outputPath);
                    Assert.Contains("\"color_transfer\": \"smpte2084\"", metadata);
                    Assert.Contains("\"color_primaries\": \"bt2020\"", metadata);
                    Assert.Contains("\"side_data_type\": \"Mastering display metadata\"", metadata);
                    Assert.Contains("\"side_data_type\": \"Content light level metadata\"", metadata);
                }
            }
        }
        finally
        {
            if (configuredOutputPath == null)
            {
                File.Delete(outputPath);
            }
        }
    }

    private static string ProbeFirstVideoFrame(string ffprobePath, string videoPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-select_streams");
        process.StartInfo.ArgumentList.Add("v:0");
        process.StartInfo.ArgumentList.Add("-read_intervals");
        process.StartInfo.ArgumentList.Add("%+#1");
        process.StartInfo.ArgumentList.Add("-show_frames");
        process.StartInfo.ArgumentList.Add("-show_entries");
        process.StartInfo.ArgumentList.Add(
            "frame=color_space,color_primaries,color_transfer,side_data_list,pix_fmt");
        process.StartInfo.ArgumentList.Add("-of");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add(videoPath);

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"ffprobe exited with code {process.ExitCode}: {error}");
        return output;
    }
}
