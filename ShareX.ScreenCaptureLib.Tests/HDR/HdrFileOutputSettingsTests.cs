using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrFileOutputSettingsTests
{
    [Fact]
    public void NewSettings_DefaultToBytePreservingUploads()
    {
        var settings = new HdrFileOutputSettings();

        Assert.True(settings.UploadWithFileUploader);
        Assert.False(settings.FlattenTransparencyForUltraHdr);
        Assert.Equal(HdrOutputMode.SdrOnly, settings.OutputMode);
        Assert.Equal(HdrFileFormat.HdrPng, settings.FileFormat);
        Assert.Equal(HdrClipboardOutputMode.HdrAndSdr, settings.ClipboardOutputMode);
        Assert.Equal(HdrFileFormat.HdrPng, settings.ClipboardFileFormat);
        Assert.Equal(OpenExrExposureMode.DisplayReferenced, settings.OpenExrExposureMode);
        Assert.Equal(90, settings.AvifQuality);
        Assert.Equal(6, settings.AvifSpeed);

        settings.FileFormat = (HdrFileFormat)99;

        Assert.Equal(HdrFileFormat.HdrPng, settings.FileFormat);

        settings.ClipboardFileFormat = (HdrFileFormat)99;

        Assert.Equal(HdrFileFormat.HdrPng, settings.ClipboardFileFormat);
    }

    [Fact]
    public void ClipboardOutputMode_AcceptsAllModesAndRejectsUnknownValues()
    {
        var settings = new HdrFileOutputSettings();

        foreach (HdrClipboardOutputMode mode in Enum.GetValues<HdrClipboardOutputMode>())
        {
            settings.ClipboardOutputMode = mode;
            Assert.Equal(mode, settings.ClipboardOutputMode);
        }

        settings.ClipboardOutputMode = (HdrClipboardOutputMode)99;

        Assert.Equal(HdrClipboardOutputMode.HdrAndSdr, settings.ClipboardOutputMode);
    }

    [Fact]
    public void ClipboardFileFormat_IsIndependentFromDiskFormat()
    {
        var settings = new HdrFileOutputSettings
        {
            FileFormat = HdrFileFormat.UltraHdrJpeg,
            ClipboardFileFormat = HdrFileFormat.Avif
        };

        Assert.Equal(HdrFileFormat.UltraHdrJpeg, settings.FileFormat);
        Assert.Equal(HdrFileFormat.Avif, settings.ClipboardFileFormat);
    }

    [Fact]
    public void AvifSettings_AreClampedToCodecRanges()
    {
        var settings = new HdrFileOutputSettings
        {
            AvifQuality = 0,
            AvifSpeed = -1
        };

        Assert.Equal(1, settings.AvifQuality);
        Assert.Equal(0, settings.AvifSpeed);

        settings.AvifQuality = 101;
        settings.AvifSpeed = 11;
        Assert.Equal(100, settings.AvifQuality);
        Assert.Equal(10, settings.AvifSpeed);
    }
}
