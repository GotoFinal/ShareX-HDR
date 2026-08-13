using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

[Collection("OBS hook integration")]
public class ObsGameCaptureBootstrapIntegrationTests
{
    [Fact]
    public async Task ExactSignedHook_CapturesOwnedRgba16FloatHarness()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_OBS_HOOK_INTEGRATION_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
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
        bool useRgb10A2 = string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_OBS_HOOK_HARNESS_FORMAT"),
            "rgb10a2",
            StringComparison.OrdinalIgnoreCase);

        ObsGameCaptureCompatibilityReport compatibility =
            await ObsGameCaptureCompatibilityProbe.ProbeAsync(
                ObsBinaryArchitecture.X64,
                cancellationToken: cancellationToken);
        Assert.True(compatibility.IsCompatible, $"{compatibility.Status}: {compatibility.Message}");

        using var harness = new Process
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

        if (useRgb10A2)
        {
            harness.StartInfo.ArgumentList.Add("--rgb10a2");
        }

        Assert.True(harness.Start());

        try
        {
            string? readyLine = await harness.StandardOutput.ReadLineAsync(cancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(readyLine));
            string[] ready = readyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, ready.Length);
            Assert.Equal("READY", ready[0]);

            uint processId = uint.Parse(ready[1], CultureInfo.InvariantCulture);
            uint threadId = uint.Parse(ready[2], CultureInfo.InvariantCulture);
            IntPtr rootWindow = new IntPtr(long.Parse(ready[3], CultureInfo.InvariantCulture));
            Assert.Equal((uint)harness.Id, processId);

            using ObsGameCaptureBootstrapSession session = await ObsGameCaptureBootstrapSession.StartAsync(
                compatibility,
                processId,
                threadId,
                rootWindow,
                cancellationToken: cancellationToken);

            ObsGameCapturePublication publication = await session.WaitForPublicationAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);

            Assert.Equal(ObsHookCaptureType.Texture, publication.HookInfo.CaptureType);
            Assert.Equal(640U, publication.HookInfo.Width);
            Assert.Equal(360U, publication.HookInfo.Height);
            Assert.NotEqual(0U, publication.SharedTextureHandle);

            await Task.Delay(100, cancellationToken);
            var metadata = new ObsGameCaptureFrameMetadata(
                "OBS Hook HDR Test Harness",
                true,
                203f,
                1000f,
                useRgb10A2 ? ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq : null);
            using HdrImageDocument document =
                ObsGameCaptureDocumentFactory.CopyToOwnedDocument(publication, metadata);
            HdrRgba16FloatBuffer buffer = document.MasterPixels;

            Assert.Equal(640, buffer.Width);
            Assert.Equal(360, buffer.Height);
            HdrCaptureSourceSegment sourceSegment = Assert.Single(document.SourceSegments);
            Assert.True(sourceSegment.WasHdrActive);
            Assert.Equal(203f, sourceSegment.SdrWhiteNits);
            Assert.Equal(1000f, sourceSegment.DisplayPeakNits);
            ReadOnlySpan<byte> centerRow = buffer.GetRowSpan(buffer.Height / 2);
            int centerPixel = (buffer.Width / 2) * HdrRgba16FloatBuffer.BytesPerPixel;
            float red = ReadHalf(centerRow, centerPixel);
            float green = ReadHalf(centerRow, centerPixel + 2);
            float blue = ReadHalf(centerRow, centerPixel + 4);
            float alpha = ReadHalf(centerRow, centerPixel + 6);

            Assert.InRange(red, 1.9f, 4.1f);
            Assert.InRange(green, 0.2f, 0.8f);
            Assert.InRange(blue, 0.1f, 0.15f);
            Assert.Equal(1f, alpha);
            await Task.Delay(100, cancellationToken);
            using HdrRgba16FloatBuffer nextBuffer =
                CopyFrame(publication, useRgb10A2);
            ReadOnlySpan<byte> nextCenterRow = nextBuffer.GetRowSpan(nextBuffer.Height / 2);
            float nextRed = ReadHalf(nextCenterRow, centerPixel);
            Assert.True(Math.Abs(nextRed - red) > 0.01f,
                "The shared texture did not advance while the harness continued presenting.");

            Assert.False(session.UsedExistingHook);

            session.Dispose();
            await Task.Delay(250, cancellationToken);
            harness.Refresh();
            int baselineHandleCount = harness.HandleCount;
            long baselinePrivateBytes = harness.PrivateMemorySize64;
            using Process hostProcess = Process.GetCurrentProcess();
            hostProcess.Refresh();
            int baselineHostHandleCount = hostProcess.HandleCount;
            long baselineHostPrivateBytes = hostProcess.PrivateMemorySize64;

            int restartCount = int.TryParse(
                Environment.GetEnvironmentVariable("SHAREX_OBS_HOOK_STRESS_COUNT"),
                out int configuredCount)
                ? Math.Clamp(configuredCount, 1, 100)
                : 3;

            for (int restart = 0; restart < restartCount; restart++)
            {
                using ObsGameCaptureBootstrapSession restartedSession =
                    await ObsGameCaptureBootstrapSession.StartAsync(
                        compatibility,
                        processId,
                        threadId,
                        rootWindow,
                        cancellationToken: cancellationToken);
                Assert.True(restartedSession.UsedExistingHook);

                ObsGameCapturePublication restartedPublication =
                    await restartedSession.WaitForPublicationAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Assert.Equal(ObsHookCaptureType.Texture, restartedPublication.HookInfo.CaptureType);
                Assert.Equal(640U, restartedPublication.HookInfo.Width);

                using HdrRgba16FloatBuffer restartedBuffer =
                    CopyFrame(restartedPublication, useRgb10A2);
                Assert.Equal(360, restartedBuffer.Height);
            }

            await Task.Delay(250, cancellationToken);
            harness.Refresh();
            hostProcess.Refresh();
            Assert.InRange(harness.HandleCount, 0, baselineHandleCount + 16);
            Assert.InRange(harness.PrivateMemorySize64, 0, baselinePrivateBytes + 32L * 1024 * 1024);
            Assert.InRange(hostProcess.HandleCount, 0, baselineHostHandleCount + 32);
            Assert.InRange(hostProcess.PrivateMemorySize64, 0, baselineHostPrivateBytes + 64L * 1024 * 1024);
        }
        finally
        {
            TryStopHarness(harness);
        }
    }

    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset)
    {
        return (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
    }


    private static HdrRgba16FloatBuffer CopyFrame(
        ObsGameCapturePublication publication,
        bool useRgb10A2)
    {
        return useRgb10A2
            ? ObsGameCaptureTextureReader.CopyRgba16FloatToOwnedBuffer(
                publication,
                ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq)
            : ObsGameCaptureTextureReader.CopyRgba16FloatToOwnedBuffer(publication);
    }
    private static void TryStopHarness(Process harness)
    {
        try
        {
            if (!harness.HasExited)
            {
                harness.CloseMainWindow();

                if (!harness.WaitForExit(3000))
                {
                    harness.Kill(true);
                    harness.WaitForExit(3000);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
