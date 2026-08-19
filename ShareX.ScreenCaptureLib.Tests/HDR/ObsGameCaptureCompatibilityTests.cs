using ShareX.ScreenCaptureLib;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class ObsGameCaptureCompatibilityTests
{
    private const string OffsetOutput64 = """
        [d3d8]
        present=0x0
        [d3d9]
        present=0x64030
        present_ex=0x1c6a0
        present_swap=0x81a30
        d3d9_clsoff=0x4030
        is_d3d9ex_clsoff=0x55a0
        [dxgi]
        present=0x2ca0
        present1=0x3140
        resize=0x38c20
        release=0x30750
        """;

    private const string OffsetOutput32 = """
        [d3d8] present=0x2ac80
        [d3d9] present=0xe32a0 present_ex=0xe3330 present_swap=0x43e40 d3d9_clsoff=0x2b3c is_d3d9ex_clsoff=0x4f44
        [dxgi] present=0xaecb0 present1=0x46360 resize=0x26620 release=0x4af70
        """;

    [Fact]
    public void HookInfo_MatchesObs18AbiLayout()
    {
        Assert.Equal(648, Marshal.SizeOf<ObsHookInfo>());
        Assert.Equal(76, Marshal.SizeOf<ObsGraphicsOffsets>());
        Assert.Equal(0, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.HookVersionMajor)));
        Assert.Equal(8, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.CaptureType)));
        Assert.Equal(48, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.Flip)));
        Assert.Equal(56, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.FrameInterval)));
        Assert.Equal(64, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.UnusedUseScale)));
        Assert.Equal(68, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.Offsets)));
        Assert.Equal(144, OffsetOf<ObsHookInfo>(nameof(ObsHookInfo.Reserved)));
    }

    [Fact]
    public void NamedObjects_MatchObsPidAndWindowConventions()
    {
        const uint processId = 1234;

        Assert.Equal("CaptureHook_KeepAlive1234", ObsHookProtocol.KeepAliveName(processId));
        Assert.Equal("CaptureHook_Pipe1234", ObsHookProtocol.PipeName(processId));
        Assert.Equal(@"\\.\pipe\CaptureHook_Pipe1234", ObsHookProtocol.PipePath(processId));
        Assert.Equal("CaptureHook_HookInfo1234", ObsHookProtocol.HookInfoName(processId));
        Assert.Equal("CaptureHook_Restart1234", ObsHookProtocol.RestartEventName(processId));
        Assert.Equal("CaptureHook_Stop1234", ObsHookProtocol.StopEventName(processId));
        Assert.Equal("CaptureHook_Initialize1234", ObsHookProtocol.InitializeEventName(processId));
        Assert.Equal("CaptureHook_HookReady1234", ObsHookProtocol.HookReadyEventName(processId));
        Assert.Equal("CaptureHook_Exit1234", ObsHookProtocol.HookExitEventName(processId));
        Assert.Equal("CaptureHook_TextureMutex11234", ObsHookProtocol.TextureMutexName(processId, 1));
        Assert.Equal("CaptureHook_TextureMutex21234", ObsHookProtocol.TextureMutexName(processId, 2));
        Assert.Equal("CaptureHook_Texture_4294972974_9", ObsHookProtocol.TextureMappingName(4_294_972_974UL, 9));
    }

    [Fact]
    public void OffsetParser_ParsesCaptured64BitHelperOutput()
    {
        ObsGraphicsOffsetValues offsets = ObsGraphicsOffsetParser.Parse(OffsetOutput64);

        Assert.Equal(0U, offsets.D3D8Present);
        Assert.Equal(0x64030U, offsets.D3D9Present);
        Assert.Equal(0x1C6A0U, offsets.D3D9PresentEx);
        Assert.Equal(0x81A30U, offsets.D3D9PresentSwap);
        Assert.Equal(0x4030U, offsets.D3D9ClassOffset);
        Assert.Equal(0x55A0U, offsets.IsD3D9ExClassOffset);
        Assert.Equal(0x2CA0U, offsets.DxgiPresent);
        Assert.Equal(0x3140U, offsets.DxgiPresent1);
        Assert.Equal(0x38C20U, offsets.DxgiResize);
        Assert.Equal(0x30750U, offsets.DxgiRelease);

        ObsGraphicsOffsets native = offsets.ToNative();
        Assert.Equal(offsets.DxgiResize, native.Dxgi.Resize);
        Assert.Equal(offsets.DxgiPresent1, native.Dxgi.Present1);
        Assert.Equal(offsets.DxgiRelease, native.Dxgi2.Release);
    }

    [Fact]
    public void OffsetParser_ParsesCaptured32BitHelperOutput()
    {
        ObsGraphicsOffsetValues offsets = ObsGraphicsOffsetParser.Parse(OffsetOutput32);

        Assert.Equal(0x2AC80U, offsets.D3D8Present);
        Assert.Equal(0xE32A0U, offsets.D3D9Present);
        Assert.Equal(0xAECB0U, offsets.DxgiPresent);
        Assert.Equal(0x4AF70U, offsets.DxgiRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[d3d8] present=0x0")]
    [InlineData("[d3d8] present=0x0\n[d3d9] present=0x1 present_ex=0x2 present_swap=0x3 d3d9_clsoff=0x4 is_d3d9ex_clsoff=0x5\n[dxgi] present=0x1 present1=0x2 resize=0x3 release=0x4 extra=0x5")]
    [InlineData("[d3d8] present=0x0\n[d3d9] present=0x1 present=0x2 present_swap=0x3 d3d9_clsoff=0x4 is_d3d9ex_clsoff=0x5\n[dxgi] present=0x1 present1=0x2 resize=0x3 release=0x4")]
    [InlineData("[d3d8] present=0x100000000\n[d3d9] present=0x1 present_ex=0x2 present_swap=0x3 d3d9_clsoff=0x4 is_d3d9ex_clsoff=0x5\n[dxgi] present=0x1 present1=0x2 resize=0x3 release=0x4")]
    public void OffsetParser_RejectsMalformedOrUnexpectedOutput(string output)
    {
        Assert.Throws<FormatException>(() => ObsGraphicsOffsetParser.Parse(output));
    }

    [Fact]
    public void PeReader_ReportsCurrentProcessArchitecture()
    {
        string processPath = Assert.IsType<string>(Environment.ProcessPath);
        ObsBinaryArchitecture expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => ObsBinaryArchitecture.X86,
            Architecture.X64 => ObsBinaryArchitecture.X64,
            Architecture.Arm64 => ObsBinaryArchitecture.Arm64,
            _ => ObsBinaryArchitecture.Unsupported
        };

        Assert.Equal(expected, ObsGameCaptureBinaryInspector.ReadArchitecture(processPath));
    }

    [Fact]
    public void Discovery_PrioritizesExplicitInstallationRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ShareX-ObsDiscovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data", "obs-plugins", "win-capture"));

        try
        {
            IReadOnlyList<string> roots = ObsGameCaptureDiscovery.FindInstallationRoots(root);

            Assert.NotEmpty(roots);
            Assert.Equal(Path.GetFullPath(root), roots[0], ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(ObsBinaryArchitecture.X86, "32")]
    [InlineData(ObsBinaryArchitecture.X64, "64")]
    [InlineData(ObsBinaryArchitecture.Arm64, "64")]
    public void Discovery_ResolvesHookNamesForSupportedArchitectures(
        ObsBinaryArchitecture architecture,
        string suffix)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ShareX-ObsArchitecture-{Guid.NewGuid():N}");
        string captureDirectory = Path.Combine(root, "data", "obs-plugins", "win-capture");
        Directory.CreateDirectory(captureDirectory);

        try
        {
            foreach (string name in new[]
            {
                $"graphics-hook{suffix}.dll",
                $"inject-helper{suffix}.exe",
                $"get-graphics-offsets{suffix}.exe"
            })
            {
                File.WriteAllBytes(Path.Combine(captureDirectory, name), [0]);
            }

            Assert.True(ObsGameCaptureDiscovery.TryResolveBinaryPaths(
                root,
                architecture,
                out ObsGameCaptureBinaryPaths? paths,
                out string error), error);
            Assert.NotNull(paths);
            Assert.Equal(architecture, paths.TargetArchitecture);
            Assert.EndsWith($"graphics-hook{suffix}.dll", paths.GraphicsHookPath);
            Assert.EndsWith($"inject-helper{suffix}.exe", paths.InjectHelperPath);
            Assert.EndsWith($"get-graphics-offsets{suffix}.exe", paths.GraphicsOffsetsHelperPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(ObsBinaryArchitecture.X86)]
    [InlineData(ObsBinaryArchitecture.X64)]
    [InlineData(ObsBinaryArchitecture.Arm64)]
    public async Task InstalledObsProbe_IsCompatibleWhenMatchingBinariesArePresent(ObsBinaryArchitecture architecture)
    {
        IReadOnlyList<string> roots = ObsGameCaptureDiscovery.FindInstallationRoots();

        if (!roots.Any(root =>
            ObsGameCaptureDiscovery.TryResolveBinaryPaths(
                root,
                architecture,
                out ObsGameCaptureBinaryPaths? paths,
                out _) &&
            ObsGameCaptureBinaryInspector.ReadArchitecture(paths.GraphicsHookPath) == architecture &&
            ObsGameCaptureBinaryInspector.ReadArchitecture(paths.InjectHelperPath) == architecture &&
            ObsGameCaptureBinaryInspector.ReadArchitecture(paths.GraphicsOffsetsHelperPath) == architecture))
        {
            return;
        }

        ObsGameCaptureCompatibilityReport report = await ObsGameCaptureCompatibilityProbe.ProbeAsync(
            architecture,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.IsCompatible, $"{report.Status}: {report.Message}");
        Assert.NotNull(report.BinaryPaths);
        Assert.Equal(3, report.Binaries.Count);
        Assert.All(report.Binaries, binary => Assert.True(binary.Trust.IsTrusted));
        Assert.NotNull(report.Offsets);
        Assert.NotNull(report.OffsetHelper);
        Assert.False(report.OffsetHelper.TimedOut);
    }

    private static int OffsetOf<T>(string fieldName) where T : struct
    {
        return Marshal.OffsetOf<T>(fieldName).ToInt32();
    }
}
