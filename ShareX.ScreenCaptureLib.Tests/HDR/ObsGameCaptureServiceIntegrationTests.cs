using ShareX.HelpersLib;
using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

[Collection("OBS hook integration")]
public class ObsGameCaptureServiceIntegrationTests
{
    [Fact]
    public async Task ShareXOwnedSession_IsReusedWithoutReinjection()
    {
        if (!IntegrationTestsEnabled())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ObsGameCaptureCompatibilityReport compatibility =
            await GetCompatibleObsAsync(cancellationToken);
        using HarnessContext harness = await HarnessContext.StartAsync(cancellationToken);
        HdrCaptureSettings settings = CreateSettings(harness.Process.ProcessName);
        using var service = new ObsGameCaptureService();

        Assert.True(
            service.TryCapture(
                harness.ClientBounds,
                settings,
                harness.Window,
                out HdrImageDocument firstDocument,
                out ObsGameCaptureAttempt firstAttempt),
            firstAttempt.Message);

        using (firstDocument)
        {
            Assert.Equal(
                ObsGameCaptureSessionSource.StartedOwnedHook,
                firstAttempt.SessionSource);
            Assert.Equal(harness.ClientBounds, firstDocument.RequestedBounds);
            Assert.Equal(harness.ClientBounds.Width, firstDocument.MasterPixels.Width);
            Assert.Equal(harness.ClientBounds.Height, firstDocument.MasterPixels.Height);
        }

        Rectangle cropBounds = new Rectangle(
            harness.ClientBounds.X + 64,
            harness.ClientBounds.Y + 36,
            320,
            180);

        for (int capture = 0; capture < 3; capture++)
        {
            Assert.True(
                service.TryCapture(
                    cropBounds,
                    settings,
                    harness.Window,
                    out HdrImageDocument reusedDocument,
                    out ObsGameCaptureAttempt reusedAttempt),
                reusedAttempt.Message);

            using (reusedDocument)
            {
                Assert.Equal(
                    ObsGameCaptureSessionSource.ReusedOwnedHook,
                    reusedAttempt.SessionSource);
                Assert.Equal(cropBounds, reusedDocument.RequestedBounds);
                Assert.Equal(320, reusedDocument.MasterPixels.Width);
                Assert.Equal(180, reusedDocument.MasterPixels.Height);
            }
        }
        Rectangle framedBounds = Rectangle.Inflate(harness.ClientBounds, 12, 12);
        Assert.True(
            service.TryCapture(
                framedBounds,
                settings,
                harness.Window,
                out HdrImageDocument framedDocument,
                out ObsGameCaptureAttempt framedAttempt),
            framedAttempt.Message);

        using (framedDocument)
        {
            Assert.Equal(
                ObsGameCaptureSessionSource.ReusedOwnedHook,
                framedAttempt.SessionSource);
            Assert.Equal(framedBounds, framedDocument.RequestedBounds);
            Assert.Equal(framedBounds.Width, framedDocument.MasterPixels.Width);
            Assert.Equal(framedBounds.Height, framedDocument.MasterPixels.Height);
            Assert.True(framedDocument.SourceSegments.Count >= 2);
            Assert.Equal(
                new Rectangle(12, 12, harness.ClientBounds.Width, harness.ClientBounds.Height),
                framedDocument.SourceSegments.Last().DestinationRectangle);
            ReadOnlySpan<byte> centerRow = framedDocument.MasterPixels.GetRowSpan(
                12 + harness.ClientBounds.Height / 2);
            int centerPixel = (12 + harness.ClientBounds.Width / 2) *
                HdrRgba16FloatBuffer.BytesPerPixel;
            Assert.InRange(ReadHalf(centerRow, centerPixel), 1.9f, 4.1f);
        }


        Assert.True(compatibility.IsCompatible);
    }

    [Fact]
    public async Task ExistingObsOwnedPublication_IsReusedReadOnly()
    {
        if (!IntegrationTestsEnabled())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ObsGameCaptureCompatibilityReport compatibility =
            await GetCompatibleObsAsync(cancellationToken);
        using HarnessContext harness = await HarnessContext.StartAsync(cancellationToken);
        using ObsGameCaptureBootstrapSession obsOwner =
            await ObsGameCaptureBootstrapSession.StartAsync(
                compatibility,
                checked((uint)harness.Process.Id),
                harness.ThreadId,
                harness.Window,
                cancellationToken: cancellationToken);
        ObsGameCapturePublication ownerPublication =
            await obsOwner.WaitForPublicationAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await Task.Delay(100, cancellationToken);

        HdrCaptureSettings settings = CreateSettings(harness.Process.ProcessName);
        settings.ObsGameCapture.ReuseExistingHook = true;

        using (var service = new ObsGameCaptureService())
        {
            Assert.True(
                service.TryCapture(
                    harness.ClientBounds,
                    settings,
                    harness.Window,
                    out HdrImageDocument document,
                    out ObsGameCaptureAttempt attempt),
                attempt.Message);

            using (document)
            {
                Assert.Equal(
                    ObsGameCaptureSessionSource.ReusedExistingHostReadOnly,
                    attempt.SessionSource);
                Assert.Equal(harness.ClientBounds.Width, document.MasterPixels.Width);
                Assert.Equal(harness.ClientBounds.Height, document.MasterPixels.Height);
            }
        }

        // The read-only consumer must not signal, restart, or stop the actual
        // OBS-owned hook. Its original publication must remain usable.
        using HdrRgba16FloatBuffer ownerBuffer =
            ObsGameCaptureTextureReader.CopyRgba16FloatToOwnedBuffer(ownerPublication);
        Assert.Equal(640, ownerBuffer.Width);
        Assert.Equal(360, ownerBuffer.Height);
        harness.Process.Refresh();
        Assert.False(harness.Process.HasExited);
    }

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset)
    {
        return (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
    }

    private static bool IntegrationTestsEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_OBS_HOOK_INTEGRATION_TESTS"),
            "1",
            StringComparison.Ordinal);
    }

    private static async Task<ObsGameCaptureCompatibilityReport> GetCompatibleObsAsync(
        CancellationToken cancellationToken)
    {
        ObsGameCaptureCompatibilityReport compatibility =
            await ObsGameCaptureCompatibilityProbe.ProbeAsync(
                ObsBinaryArchitecture.X64,
                cancellationToken: cancellationToken);
        Assert.True(
            compatibility.IsCompatible,
            $"{compatibility.Status}: {compatibility.Message}");
        return compatibility;
    }

    private static HdrCaptureSettings CreateSettings(string processName)
    {
        var settings = new HdrCaptureSettings();
        settings.ObsGameCapture.Enabled = true;
        settings.ObsGameCapture.ReuseExistingHook = true;
        settings.ObsGameCapture.ProcessNames = new List<string> { processName };
        settings.ObsGameCapture.SessionIdleTimeoutSeconds = 120;
        return settings;
    }

    private sealed class HarnessContext : IDisposable
    {
        public Process Process { get; }
        public uint ThreadId { get; }
        public IntPtr Window { get; }
        public Rectangle ClientBounds { get; }

        private HarnessContext(
            Process process,
            uint threadId,
            IntPtr window,
            Rectangle clientBounds)
        {
            Process = process;
            ThreadId = threadId;
            Window = window;
            ClientBounds = clientBounds;
        }

        public static async Task<HarnessContext> StartAsync(CancellationToken cancellationToken)
        {
            string workspaceRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", ".."));
            string harnessPath = Path.Combine(
                workspaceRoot,
                "ShareX.ScreenCaptureLib.ObsHookHarness",
                "bin",
                "Release",
                "win-x64",
                "ShareX.ScreenCaptureLib.ObsHookHarness.exe");
            Assert.True(File.Exists(harnessPath), $"Build the OBS hook harness first: {harnessPath}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = harnessPath,
                    WorkingDirectory = Path.GetDirectoryName(harnessPath)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            try
            {
                Assert.True(process.Start());
                string? readyLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
                Assert.False(string.IsNullOrWhiteSpace(readyLine));
                string[] ready = readyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(4, ready.Length);
                Assert.Equal("READY", ready[0]);
                Assert.Equal(process.Id, int.Parse(ready[1], CultureInfo.InvariantCulture));

                uint threadId = uint.Parse(ready[2], CultureInfo.InvariantCulture);
                IntPtr window = new IntPtr(long.Parse(ready[3], CultureInfo.InvariantCulture));
                await Task.Delay(100, cancellationToken);
                Rectangle clientBounds = NativeMethods.GetClientRect(window);
                Assert.True(clientBounds.Width > 0);
                Assert.True(clientBounds.Height > 0);
                return new HarnessContext(process, threadId, window, clientBounds);
            }
            catch
            {
                TryStop(process);
                process.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            TryStop(Process);
            Process.Dispose();
        }

        private static void TryStop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();

                    if (!process.WaitForExit(3000))
                    {
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}

[CollectionDefinition("OBS hook integration", DisableParallelization = true)]
public sealed class ObsGameCaptureIntegrationCollection
{
}
