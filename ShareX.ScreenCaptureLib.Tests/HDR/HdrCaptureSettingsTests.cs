using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrCaptureSettingsTests
{
    [Fact]
    public void NewSettings_DefaultToGpuWithCpuFallbackAvailable()
    {
        var settings = new HdrCaptureSettings();
        var screenshot = new Screenshot();

        Assert.Equal(HdrProcessingBackend.Gpu, settings.ProcessingBackend);
        Assert.Equal(HdrPeakBrightnessMode.Automatic, settings.PeakBrightnessMode);
        Assert.Equal(HdrPaperWhiteMode.Automatic, settings.PaperWhiteMode);
        Assert.Equal(HdrCaptureSettings.DefaultBrightnessNits, settings.PaperWhiteNits);
        Assert.Equal(
            HdrMixedMonitorBrightnessMode.MatchHdrDisplay,
            settings.MixedMonitorBrightnessMode);
        Assert.Equal(
            HdrCaptureSettings.DefaultBrightnessNits,
            settings.MixedMonitorCustomSdrWhiteNits);
        Assert.True(screenshot.UseHDRSupport);

        settings.ProcessingBackend = (HdrProcessingBackend)99;

        Assert.Equal(HdrProcessingBackend.Gpu, settings.ProcessingBackend);
    }

    [Fact]
    public void MixedMonitorBrightness_AcceptsManualWhiteAndRejectsInvalidValues()
    {
        var settings = new HdrCaptureSettings
        {
            MixedMonitorBrightnessMode = HdrMixedMonitorBrightnessMode.Custom,
            MixedMonitorCustomSdrWhiteNits = 250f
        };

        Assert.Equal(HdrMixedMonitorBrightnessMode.Custom, settings.MixedMonitorBrightnessMode);
        Assert.Equal(250f, settings.MixedMonitorCustomSdrWhiteNits);

        settings.MixedMonitorCustomSdrWhiteNits = 2000f;
        settings.MixedMonitorBrightnessMode = (HdrMixedMonitorBrightnessMode)99;

        Assert.Equal(
            HdrCaptureSettings.MaximumPaperWhiteNits,
            settings.MixedMonitorCustomSdrWhiteNits);
        Assert.Equal(
            HdrMixedMonitorBrightnessMode.MatchHdrDisplay,
            settings.MixedMonitorBrightnessMode);
    }

    [Fact]
    public void AdvancedManualBrightnessValues_AreClampedToHdrRange()
    {
        var settings = new HdrCaptureSettings
        {
            PeakBrightnessMode = HdrPeakBrightnessMode.Custom,
            HdrBrightnessNits = 4000f,
            PaperWhiteMode = HdrPaperWhiteMode.Custom,
            PaperWhiteNits = 250f
        };

        Assert.Equal(4000f, settings.HdrBrightnessNits);
        Assert.Equal(250f, settings.PaperWhiteNits);

        settings.HdrBrightnessNits = 20000f;
        settings.PaperWhiteNits = 20f;

        Assert.Equal(HdrCaptureSettings.MaximumBrightnessNits, settings.HdrBrightnessNits);
        Assert.Equal(HdrCaptureSettings.MinimumBrightnessNits, settings.PaperWhiteNits);

        settings.PaperWhiteNits = 2000f;

        Assert.Equal(HdrCaptureSettings.MaximumPaperWhiteNits, settings.PaperWhiteNits);
    }

    [Fact]
    public void ToneMappingModes_KeepLegacyNumericValues()
    {
        Assert.Equal(0, (int)HdrToneMappingMode.ContentAware);
        Assert.Equal(1, (int)HdrToneMappingMode.Uniform);
        Assert.Equal(2, (int)HdrToneMappingMode.PerWindow);
    }

    [Fact]
    public void ToneMappingMode_AcceptsPerWindowAndRejectsUnknownValues()
    {
        var settings = new HdrCaptureSettings
        {
            ToneMappingMode = HdrToneMappingMode.PerWindow
        };

        Assert.Equal(HdrToneMappingMode.PerWindow, settings.ToneMappingMode);

        settings.ToneMappingMode = (HdrToneMappingMode)99;

        Assert.Equal(HdrToneMappingMode.ContentAware, settings.ToneMappingMode);
    }

    [Fact]
    public void GameWindowPromotion_DefaultsToAutomaticAndRejectsUnknownValues()
    {
        var settings = new HdrCaptureSettings();

        Assert.Equal(
            HdrGameWindowPromotionMode.AutomaticFullscreenHdr,
            settings.GameWindowPromotionMode);

        settings.GameWindowPromotionMode = HdrGameWindowPromotionMode.KnownGamesOnly;
        Assert.Equal(
            HdrGameWindowPromotionMode.KnownGamesOnly,
            settings.GameWindowPromotionMode);

        settings.GameWindowPromotionMode = (HdrGameWindowPromotionMode)99;
        Assert.Equal(
            HdrGameWindowPromotionMode.AutomaticFullscreenHdr,
            settings.GameWindowPromotionMode);
    }
}
