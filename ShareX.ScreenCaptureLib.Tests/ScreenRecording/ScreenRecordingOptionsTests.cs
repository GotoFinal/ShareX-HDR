using System.Drawing;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.ScreenRecording;

public sealed class ScreenRecordingOptionsTests
{
    [Fact]
    public void SharedFrameInput_UsesRawBgraPipeAndExistingEncoder()
    {
        ScreenRecordingOptions options = CreateOptions();

        string args = options.GetFFmpegArgs(frameInputMode: ShareXFrameInputMode.ToneMappedBgra);

        Assert.Contains("-f rawvideo", args);
        Assert.Contains("-pixel_format bgra", args);
        Assert.Contains("-video_size 640x360", args);
        Assert.Contains("-framerate 30", args);
        Assert.Contains("-i pipe:0", args);
        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-color_primaries bt709 -color_trc bt709 -colorspace bt709", args);
        Assert.DoesNotContain("-f gdigrab", args);
    }

    [Fact]
    public void SharedFrameInput_MapsDirectShowAudioAndStopsAtVideoEof()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.FFmpeg.AudioSource = "virtual-audio-capturer";

        string args = options.GetFFmpegArgs(frameInputMode: ShareXFrameInputMode.ToneMappedBgra);

        Assert.Contains("-i audio=\"virtual-audio-capturer\"", args);
        Assert.Contains("-map 1:v:0 -map 0:a:0", args);
        Assert.Contains("-shortest", args);
    }

    [Fact]
    public void SharedFrameInput_RejectsCustomCommandsAndNonDesktopSources()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.FFmpeg.UseCustomCommands = true;

        Assert.False(options.SupportsShareXFrameInput);
        Assert.Throws<InvalidOperationException>(() =>
            options.GetFFmpegArgs(frameInputMode: ShareXFrameInputMode.ToneMappedBgra));

        options.FFmpeg.UseCustomCommands = false;
        options.FFmpeg.VideoSource = "Camera";

        Assert.False(options.SupportsShareXFrameInput);
    }

    [Fact]
    public void Hdr10FrameInput_UsesP010HevcMain10AndHdrMetadata()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.HdrMasteringMaximumNits = 1000f;
        options.HdrMasteringMinimumNits = 0.0005f;

        string args = options.GetFFmpegArgs(frameInputMode: ShareXFrameInputMode.Hdr10P010);

        Assert.Equal(
            FFmpegVideoCodec.libx265,
            options.GetEffectiveVideoCodec(ShareXFrameInputMode.Hdr10P010));
        Assert.Contains("-pixel_format p010le", args);
        Assert.Contains("-c:v libx265", args);
        Assert.Contains("-profile:v main10", args);
        Assert.Contains("-pix_fmt p010le", args);
        Assert.Contains("-color_primaries bt2020", args);
        Assert.Contains("-color_trc smpte2084", args);
        Assert.Contains("-colorspace bt2020nc", args);
        Assert.Contains(
            "-mastering_display \"G(8500,39850)B(6550,2300)R(35400,14600)" +
            "WP(15635,16450)L(10000000,5)\"",
            args);
        Assert.Contains("-content_light 1000,400", args);
        Assert.Contains("master-display=G(8500,39850)B(6550,2300)R(35400,14600)", args);
        Assert.Contains("max-cll=1000,400", args);

        int metadataIndex = args.IndexOf("-mastering_display", StringComparison.Ordinal);
        int inputIndex = args.IndexOf("-i pipe:0", StringComparison.Ordinal);
        Assert.True(metadataIndex >= 0 && metadataIndex < inputIndex);
    }

    [Fact]
    public void Hdr10FrameInput_MapsNvidiaH264SelectionToHevc()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.FFmpeg.VideoCodec = FFmpegVideoCodec.h264_nvenc;

        string args = options.GetFFmpegArgs(frameInputMode: ShareXFrameInputMode.Hdr10P010);

        Assert.Equal(
            FFmpegVideoCodec.hevc_nvenc,
            options.GetEffectiveVideoCodec(ShareXFrameInputMode.Hdr10P010));
        Assert.Contains("-c:v hevc_nvenc", args);
        Assert.Contains("-profile:v main10 -pix_fmt p010le", args);
    }

    [Fact]
    public void Hdr10FrameInput_WithoutNewStaticMetadataOptions_RetainsPqColorSignaling()
    {
        ScreenRecordingOptions options = CreateOptions();

        string args = options.GetFFmpegArgs(
            frameInputMode: ShareXFrameInputMode.Hdr10P010,
            includeHdrStaticMetadata: false);

        Assert.DoesNotContain("-mastering_display", args);
        Assert.DoesNotContain("-content_light", args);
        Assert.Contains("-color_primaries bt2020", args);
        Assert.Contains("-color_trc smpte2084", args);
        Assert.Contains("-colorspace bt2020nc", args);

        int colorIndex = args.IndexOf("-color_primaries bt2020", StringComparison.Ordinal);
        int inputIndex = args.IndexOf("-i pipe:0", StringComparison.Ordinal);
        Assert.True(colorIndex >= 0 && colorIndex < inputIndex);
    }

    [Fact]
    public void SharedFrameInput_RemovesCaptureHardwareDeviceArguments()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.FFmpeg.UserArgs =
            "-init_hw_device vulkan=vk -filter_hw_device vk -metadata title=ShareX";

        string args = options.GetFFmpegArgs(frameInputMode: ShareXFrameInputMode.Hdr10P010);

        Assert.DoesNotContain("-init_hw_device", args);
        Assert.DoesNotContain("-filter_hw_device", args);
        Assert.Contains("-metadata title=ShareX", args);
    }

    [Fact]
    public void FfmpegOwnedCapture_PreservesHardwareDeviceArguments()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.FFmpeg.UserArgs = "-init_hw_device vulkan=vk -filter_hw_device vk";

        string args = options.GetFFmpegArgs();

        Assert.Contains("-init_hw_device vulkan=vk", args);
        Assert.Contains("-filter_hw_device vk", args);
    }

    [Theory]
    [InlineData(true, false, ShareXFrameInputMode.None)]
    [InlineData(false, true, ShareXFrameInputMode.None)]
    public void Hdr10FrameInput_FallsBackWhenNativeCaptureIsUnavailable(
        bool hdrSupportEnabled,
        bool hdrCaptureRequired,
        ShareXFrameInputMode expected)
    {
        ScreenRecordingOptions options = CreateOptions();
        options.HdrMode = ScreenRecordingHdrMode.NativeHdr10;

        ShareXFrameInputMode actual = options.ResolveShareXFrameInputMode(
            hdrSupportEnabled,
            hdrCaptureRequired,
            out string fallbackReason);

        Assert.Equal(expected, actual);
        Assert.False(string.IsNullOrWhiteSpace(fallbackReason));
    }

    [Theory]
    [InlineData(FFmpegVideoCodec.gif)]
    [InlineData(FFmpegVideoCodec.libwebp)]
    [InlineData(FFmpegVideoCodec.apng)]
    public void Hdr10FrameInput_UsesToneMappedFallbackForAnimatedImages(FFmpegVideoCodec codec)
    {
        ScreenRecordingOptions options = CreateOptions();
        options.HdrMode = ScreenRecordingHdrMode.NativeHdr10;
        options.FFmpeg.VideoCodec = codec;

        ShareXFrameInputMode actual = options.ResolveShareXFrameInputMode(
            hdrSupportEnabled: true,
            hdrCaptureRequired: true,
            out string fallbackReason);

        Assert.Equal(ShareXFrameInputMode.ToneMappedBgra, actual);
        Assert.Contains("animated image", fallbackReason);
    }

    [Fact]
    public void Hdr10FrameInput_UsesToneMappedFallbackForLegacyTwoPassEncoding()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.HdrMode = ScreenRecordingHdrMode.NativeHdr10;
        options.IsLossless = true;

        ShareXFrameInputMode actual = options.ResolveShareXFrameInputMode(
            hdrSupportEnabled: true,
            hdrCaptureRequired: true,
            out string fallbackReason);

        Assert.Equal(ShareXFrameInputMode.ToneMappedBgra, actual);
        Assert.Contains("two-pass", fallbackReason);
    }

    [Fact]
    public void Hdr10FrameInput_FallsBackToSelectedSourceWhenShareXCannotOwnFrames()
    {
        ScreenRecordingOptions options = CreateOptions();
        options.HdrMode = ScreenRecordingHdrMode.NativeHdr10;
        options.FFmpeg.UseCustomCommands = true;

        ShareXFrameInputMode actual = options.ResolveShareXFrameInputMode(
            hdrSupportEnabled: true,
            hdrCaptureRequired: true,
            out string fallbackReason);

        Assert.Equal(ShareXFrameInputMode.None, actual);
        Assert.Contains("custom FFmpeg", fallbackReason);
    }

    [Fact]
    public void CopyFramePixels_WritesPackedBgraRows()
    {
        using Bitmap bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(40, 10, 20, 30));
        bitmap.SetPixel(1, 0, Color.FromArgb(80, 50, 60, 70));
        byte[] pixels = new byte[8];

        ScreenRecorder.CopyFramePixels(bitmap, pixels, 8, bitmap.Size);

        Assert.Equal(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80 }, pixels);
    }

    private static ScreenRecordingOptions CreateOptions()
    {
        return new ScreenRecordingOptions
        {
            IsRecording = true,
            FPS = 30,
            CaptureArea = new Rectangle(100, 200, 640, 360),
            OutputPath = "capture.mp4",
            FFmpeg = new FFmpegOptions
            {
                VideoSource = FFmpegCaptureDevice.GDIGrab.Value,
                VideoCodec = FFmpegVideoCodec.libx264
            }
        };
    }
}
