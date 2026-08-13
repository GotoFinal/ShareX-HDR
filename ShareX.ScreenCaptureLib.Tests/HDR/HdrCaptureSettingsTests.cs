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
        Assert.True(screenshot.UseHDRSupport);

        settings.ProcessingBackend = (HdrProcessingBackend)99;

        Assert.Equal(HdrProcessingBackend.Gpu, settings.ProcessingBackend);
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
