using ShareX.ScreenCaptureLib;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrEncoderCapabilitiesTests
{
    [Theory]
    [InlineData(HdrFileFormat.OpenExr)]
    [InlineData(HdrFileFormat.HdrPng)]
    public void ManagedEncoders_AreArchitectureIndependent(HdrFileFormat format)
    {
        Assert.True(HdrEncoderCapabilities.TryGetAvailability(format, out string? reason), reason);
        Assert.Null(reason);
    }

    [Fact]
    public void UltraHdrAvailability_MatchesPackagedProcessArchitecture()
    {
        bool available = HdrEncoderCapabilities.TryGetAvailability(
            HdrFileFormat.UltraHdrJpeg,
            out string? reason);

        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            Assert.True(available, reason);
            Assert.Null(reason);
        }
        else
        {
            Assert.False(available);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public void AvifAvailability_MatchesPackagedProcessArchitecture()
    {
        bool available = HdrEncoderCapabilities.TryGetAvailability(
            HdrFileFormat.Avif,
            out string? reason);

        bool nativeLibraryPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "ShareX.Avif.dll"));

        if (OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64 &&
            nativeLibraryPresent)
        {
            Assert.True(available, reason);
            Assert.Null(reason);
        }
        else
        {
            Assert.False(available);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public void UnknownEncoder_IsUnavailableWithReason()
    {
        Assert.False(HdrEncoderCapabilities.TryGetAvailability((HdrFileFormat)int.MaxValue, out string? reason));
        Assert.Contains("Unknown HDR file format", reason);
        Assert.Throws<NotSupportedException>(() =>
            HdrEncoderCapabilities.EnsureAvailable((HdrFileFormat)int.MaxValue));
    }
}
