using System.Drawing;
using System.Windows.Forms;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.ScreenRecording;

public sealed class HdrScreenRecordingIntegrationTests
{
    [Fact]
    public void HdrDesktopRecording_ProducesAnEncodedSdrVideo()
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
            Duration = 2,
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
                recorder.StartRecording();
            }

            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 1024);
        }
        finally
        {
            if (configuredOutputPath == null)
            {
                File.Delete(outputPath);
            }
        }
    }
}
