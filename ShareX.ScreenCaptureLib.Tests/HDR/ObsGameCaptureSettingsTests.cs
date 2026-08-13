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
}
