using ShareX.HistoryLib;

namespace ShareX.ScreenCaptureLib.Tests.History;

public class HistoryArtifactMetadataTests
{
    [Theory]
    [InlineData("capture-SDR.png")]
    [InlineData("capture-SDR (2).jpg")]
    [InlineData("capture-SDR (19).png")]
    public void TryGetOwnedCompanionFilePath_AcceptsGeneratedNames(string companionFileName)
    {
        string directory = CreateTestDirectoryPath();
        HistoryItem historyItem = CreateHistoryItem(
            Path.Combine(directory, "capture.exr"),
            Path.Combine(directory, companionFileName));

        bool result = HistoryArtifactMetadata.TryGetOwnedCompanionFilePath(
            historyItem,
            out string? companionFilePath);

        Assert.True(result);
        Assert.Equal(Path.Combine(directory, companionFileName), companionFilePath);
    }

    [Theory]
    [InlineData("capture-SDR-copy.png")]
    [InlineData("capture-SDR (1).png")]
    [InlineData("capture-SDR (two).png")]
    [InlineData("different-SDR.png")]
    public void TryGetOwnedCompanionFilePath_RejectsNamesShareXDoesNotGenerate(string companionFileName)
    {
        string directory = CreateTestDirectoryPath();
        HistoryItem historyItem = CreateHistoryItem(
            Path.Combine(directory, "capture.exr"),
            Path.Combine(directory, companionFileName));

        Assert.False(HistoryArtifactMetadata.TryGetOwnedCompanionFilePath(historyItem, out _));
    }

    [Fact]
    public void TryGetOwnedCompanionFilePath_RejectsAnotherDirectory()
    {
        string directory = CreateTestDirectoryPath();
        HistoryItem historyItem = CreateHistoryItem(
            Path.Combine(directory, "capture.exr"),
            Path.Combine(directory, "other", "capture-SDR.png"));

        Assert.False(HistoryArtifactMetadata.TryGetOwnedCompanionFilePath(historyItem, out _));
    }

    [Fact]
    public void GetOwnedFilePaths_ReturnsMainAndValidatedCompanion()
    {
        string directory = CreateTestDirectoryPath();
        string mainPath = Path.Combine(directory, "capture.png");
        string companionPath = Path.Combine(directory, "capture-SDR.png");
        HistoryItem historyItem = CreateHistoryItem(mainPath, companionPath);

        string[] ownedPaths = HistoryArtifactMetadata.GetOwnedFilePaths(historyItem).ToArray();

        Assert.Equal([mainPath, companionPath], ownedPaths);
    }

    [Fact]
    public void SetCompanionFilePath_RemovesEmptyTag()
    {
        HistoryItem historyItem = new();
        HistoryArtifactMetadata.SetCompanionFilePath(historyItem, @"C:\captures\capture-SDR.png");

        HistoryArtifactMetadata.SetCompanionFilePath(historyItem, null);

        Assert.False(historyItem.Tags.ContainsKey(HistoryArtifactMetadata.CompanionFilePathTag));
    }

    private static HistoryItem CreateHistoryItem(string mainPath, string companionPath) => new()
    {
        FilePath = mainPath,
        Tags = new Dictionary<string, string>
        {
            [HistoryArtifactMetadata.CompanionFilePathTag] = companionPath
        }
    };

    private static string CreateTestDirectoryPath() =>
        Path.Combine(Path.GetTempPath(), "ShareX-HistoryTests", Guid.NewGuid().ToString("N"));
}
