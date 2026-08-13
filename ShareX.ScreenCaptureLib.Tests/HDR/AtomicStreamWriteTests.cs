using ShareX.HelpersLib;
using System.Text;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class AtomicStreamWriteTests
{
    [Fact]
    public void WriteToFileAtomic_PublishesOnlyVerifiedBytes()
    {
        string directory = CreateTemporaryDirectory();
        string destination = Path.Combine(directory, "capture.exr");
        byte[] expected = Encoding.UTF8.GetBytes("verified HDR artifact");

        try
        {
            using var stream = new MemoryStream(expected);
            bool verifierCalled = false;

            bool result = stream.WriteToFileAtomic(destination, temporaryPath =>
            {
                verifierCalled = true;
                Assert.NotEqual(destination, temporaryPath);
                Assert.Equal(expected, File.ReadAllBytes(temporaryPath));
                return true;
            });

            Assert.True(result);
            Assert.True(verifierCalled);
            Assert.Equal(expected, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WriteToFileAtomic_VerificationFailurePreservesExistingDestination()
    {
        string directory = CreateTemporaryDirectory();
        string destination = Path.Combine(directory, "capture.png");
        byte[] original = Encoding.UTF8.GetBytes("original artifact");
        File.WriteAllBytes(destination, original);

        try
        {
            using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("corrupt replacement"));

            Assert.Throws<InvalidDataException>(() =>
                replacement.WriteToFileAtomic(destination, _ => false));

            Assert.Equal(original, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ShareX-AtomicWrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
