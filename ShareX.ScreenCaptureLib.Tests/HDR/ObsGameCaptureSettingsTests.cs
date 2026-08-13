using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class ObsGameCaptureSettingsTests
{
    [Fact]
    public void SetProcessNames_NormalizesExtensionsQuotesSeparatorsAndDuplicates()
    {
        var settings = new ObsGameCaptureSettings
        {
            Enabled = true
        };

        settings.SetProcessNames("""
            "Game.exe"
            game
            AnotherGame.EXE;third-game.exe, FOURTH
            """);

        Assert.Equal(
            new[] { "AnotherGame", "FOURTH", "Game", "third-game" },
            settings.ProcessNames);
        Assert.True(settings.MatchesProcess("GAME"));
        Assert.True(settings.MatchesProcess("unrelated", @"C:\Games\AnotherGame.exe"));
        Assert.False(settings.MatchesProcess("not-configured"));
    }

    [Fact]
    public void MatchesProcess_RequiresFeatureToBeEnabled()
    {
        var settings = new ObsGameCaptureSettings
        {
            ProcessNames = new List<string> { "game.exe" }
        };

        Assert.False(settings.MatchesProcess("game"));

        settings.Enabled = true;

        Assert.True(settings.MatchesProcess("game.exe"));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(120, 120)]
    [InlineData(600, 600)]
    [InlineData(601, 600)]
    public void SessionIdleTimeoutSeconds_IsClamped(int value, int expected)
    {
        var settings = new ObsGameCaptureSettings
        {
            SessionIdleTimeoutSeconds = value
        };

        Assert.Equal(expected, settings.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public void InvalidRgb10A2Mode_FallsBackToAutomatic()
    {
        var settings = new ObsGameCaptureSettings
        {
            Rgb10A2Interpretation = (ObsGameCaptureRgb10A2Interpretation)1234
        };

        Assert.Equal(
            ObsGameCaptureRgb10A2Interpretation.Automatic,
            settings.Rgb10A2Interpretation);
    }

    [Fact]
    public void CaptureOptions_DefaultToObsOpaqueWithoutThirdPartyOverlays()
    {
        var settings = new ObsGameCaptureSettings();

        ObsGameCaptureEffectiveOptions options = settings.ResolveOptions("game.exe");

        Assert.Equal(ObsGameCaptureAlphaMode.Opaque, options.AlphaMode);
        Assert.False(options.CaptureThirdPartyOverlays);
        Assert.Equal(ObsGameCaptureRgb10A2Interpretation.Automatic, options.Rgb10A2Interpretation);
        Assert.Equal(ObsGameCaptureFrameRate.Fps15, options.CaptureFrameRate);
        Assert.True(options.ReuseExistingHook);
        Assert.Equal(15, options.SessionIdleTimeoutSeconds);
        Assert.Equal(ObsGameCaptureCursorMode.UseShareXSetting, options.CursorMode);
    }

    [Fact]
    public void PerGameOptions_OverrideDefaultsAndNormalizeProcessName()
    {
        var settings = new ObsGameCaptureSettings
        {
            AlphaMode = ObsGameCaptureAlphaMode.Straight,
            CaptureThirdPartyOverlays = false,
            Rgb10A2Interpretation = ObsGameCaptureRgb10A2Interpretation.Automatic,
            CaptureFrameRate = ObsGameCaptureFrameRate.Fps60,
            ReuseExistingHook = true,
            SessionIdleTimeoutSeconds = 120,
            CursorMode = ObsGameCaptureCursorMode.UseShareXSetting
        };
        settings.SetProcessOptions(new ObsGameCaptureProcessOptions
        {
            ProcessName = "\"ExampleGame.exe\"",
            AlphaMode = ObsGameCaptureAlphaModeOverride.Premultiplied,
            CaptureThirdPartyOverlays = ObsGameCaptureOptionOverride.Enabled,
            Rgb10A2Interpretation = ObsGameCaptureRgb10A2Override.Rec2100Pq,
            CaptureFrameRate = ObsGameCaptureFrameRateOverride.Fps30,
            ReuseExistingHook = ObsGameCaptureOptionOverride.Disabled,
            SessionIdleTimeoutSeconds = 30,
            CursorMode = ObsGameCaptureCursorModeOverride.Exclude
        });

        ObsGameCaptureEffectiveOptions overridden = settings.ResolveOptions(
            "unrelated",
            @"C:\Games\ExampleGame.exe");
        ObsGameCaptureEffectiveOptions defaults = settings.ResolveOptions("another-game");

        Assert.Equal(ObsGameCaptureAlphaMode.Premultiplied, overridden.AlphaMode);
        Assert.True(overridden.CaptureThirdPartyOverlays);
        Assert.Equal(ObsGameCaptureRgb10A2Interpretation.Rec2100Pq, overridden.Rgb10A2Interpretation);
        Assert.Equal(ObsGameCaptureFrameRate.Fps30, overridden.CaptureFrameRate);
        Assert.False(overridden.ReuseExistingHook);
        Assert.Equal(30, overridden.SessionIdleTimeoutSeconds);
        Assert.Equal(ObsGameCaptureCursorMode.Exclude, overridden.CursorMode);
        Assert.Equal(ObsGameCaptureAlphaMode.Straight, defaults.AlphaMode);
        Assert.False(defaults.CaptureThirdPartyOverlays);
        Assert.Equal("ExampleGame", Assert.Single(settings.ProcessOptions).ProcessName);
    }

    [Fact]
    public void PerGameOptions_UseDefaultRemovesRedundantEntry()
    {
        var settings = new ObsGameCaptureSettings();
        settings.SetProcessOptions(new ObsGameCaptureProcessOptions
        {
            ProcessName = "game",
            AlphaMode = ObsGameCaptureAlphaModeOverride.Straight,
            CaptureThirdPartyOverlays = ObsGameCaptureOptionOverride.Disabled
        });

        settings.SetProcessOptions(new ObsGameCaptureProcessOptions
        {
            ProcessName = "game.exe"
        });

        Assert.Empty(settings.ProcessOptions);
    }

    [Fact]
    public void TargetProcessAndWindowGlobs_MapLauncherProfileToRenderWindow()
    {
        var settings = new ObsGameCaptureSettings
        {
            Enabled = true,
            ProcessNames = new List<string> { "launcher.exe" }
        };
        settings.SetProcessOptions(new ObsGameCaptureProcessOptions
        {
            ProcessName = "launcher",
            TargetProcessName = "render.exe",
            WindowTitlePattern = "My Game -*",
            WindowClassPattern = "GameWindow?",
            CaptureFrameRate = ObsGameCaptureFrameRateOverride.Fps120
        });

        Assert.True(settings.MatchesProcess("render"));
        Assert.True(settings.MatchesWindow("render", null, "My Game - Level 1", "GameWindow1"));
        Assert.False(settings.MatchesWindow("render", null, "Launcher", "GameWindow1"));
        Assert.False(settings.MatchesWindow("render", null, "My Game - Level 1", "OtherWindow"));
        Assert.Equal(
            ObsGameCaptureFrameRate.Fps120,
            settings.ResolveOptions("render.exe").CaptureFrameRate);
    }

    [Fact]
    public void RemovedLauncherProfile_CannotKeepRenderProcessAllowlisted()
    {
        var settings = new ObsGameCaptureSettings
        {
            Enabled = true,
            ProcessNames = new List<string> { "launcher" }
        };
        settings.SetProcessOptions(new ObsGameCaptureProcessOptions
        {
            ProcessName = "launcher",
            TargetProcessName = "render"
        });

        settings.ProcessNames = new List<string> { "another-game" };

        Assert.False(settings.MatchesProcess("render"));
        Assert.Null(settings.GetProcessOptionsForTarget("render"));
    }

    [Fact]
    public void LegacyTransparencySettings_MigrateToExplicitAlphaModes()
    {
        var settings = new ObsGameCaptureSettings
        {
            AllowTransparency = true
        };

        var profile = new ObsGameCaptureProcessOptions
        {
            ProcessName = "game",
            AllowTransparency = ObsGameCaptureOptionOverride.Disabled
        };
        settings.SetProcessOptions(profile);

        Assert.Equal(ObsGameCaptureAlphaMode.Straight, settings.AlphaMode);
        Assert.Equal(ObsGameCaptureAlphaMode.Opaque, settings.ResolveOptions("game").AlphaMode);
    }
}
