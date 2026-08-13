#region License Information (GPL v3)

/*
    ShareX - A program to capture and share images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    public enum ObsGameCaptureCompatibilityStatus
    {
        Compatible,
        InstallationNotFound,
        BinarySetInvalid,
        SignatureInvalid,
        SignerMismatch,
        ArchitectureMismatch,
        HookVersionUnsupported,
        OffsetHelperFailed,
        OffsetOutputInvalid
    }

    public sealed record ObsOffsetHelperExecutionResult(
        bool Started,
        bool TimedOut,
        int ExitCode,
        string StandardOutput,
        string StandardError,
        TimeSpan Duration);

    public sealed record ObsGameCaptureCompatibilityReport(
        ObsGameCaptureCompatibilityStatus Status,
        string Message,
        ObsGameCaptureBinaryPaths BinaryPaths,
        IReadOnlyList<ObsGameCaptureBinaryIdentity> Binaries,
        ObsGraphicsOffsetValues Offsets,
        ObsOffsetHelperExecutionResult OffsetHelper,
        IReadOnlyList<string> Diagnostics)
    {
        public bool IsCompatible => Status == ObsGameCaptureCompatibilityStatus.Compatible;
    }

    public static class ObsGameCaptureCompatibilityProbe
    {
        private static readonly TimeSpan DefaultHelperTimeout = TimeSpan.FromSeconds(10);
        private const int MaximumHelperOutputCharacters = 16 * 1024;

        public static async Task<ObsGameCaptureCompatibilityReport> ProbeAsync(
            ObsBinaryArchitecture targetArchitecture,
            string explicitInstallationPath = null,
            TimeSpan? helperTimeout = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> roots = ObsGameCaptureDiscovery.FindInstallationRoots(explicitInstallationPath);

            if (roots.Count == 0)
            {
                return Failure(
                    ObsGameCaptureCompatibilityStatus.InstallationNotFound,
                    "No installed OBS Studio Game Capture binary set was found.");
            }

            var diagnostics = new List<string>();
            ObsGameCaptureCompatibilityReport lastFailure = null;

            foreach (string root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ObsGameCaptureDiscovery.TryResolveBinaryPaths(
                    root, targetArchitecture, out ObsGameCaptureBinaryPaths paths, out string resolveError))
                {
                    diagnostics.Add($"{root}: {resolveError}");
                    continue;
                }

                ObsGameCaptureBinaryIdentity[] identities;

                try
                {
                    identities = new[]
                    {
                        ObsGameCaptureBinaryInspector.Inspect(paths.GraphicsHookPath, ObsGameCaptureArtifactKind.GraphicsHook),
                        ObsGameCaptureBinaryInspector.Inspect(paths.InjectHelperPath, ObsGameCaptureArtifactKind.InjectHelper),
                        ObsGameCaptureBinaryInspector.Inspect(paths.GraphicsOffsetsHelperPath, ObsGameCaptureArtifactKind.GraphicsOffsetsHelper)
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    diagnostics.Add($"{root}: binary inspection failed ({ex.GetType().Name}).");
                    continue;
                }

                ObsGameCaptureCompatibilityReport validationFailure = ValidateBinarySet(paths, identities, diagnostics);

                if (validationFailure != null)
                {
                    diagnostics.Add($"{root}: {validationFailure.Message}");
                    lastFailure = validationFailure;
                    continue;
                }

                ObsOffsetHelperExecutionResult execution = await RunOffsetHelperAsync(
                    paths.GraphicsOffsetsHelperPath,
                    helperTimeout ?? DefaultHelperTimeout,
                    cancellationToken).ConfigureAwait(false);

                if (!execution.Started || execution.TimedOut || execution.ExitCode != 0)
                {
                    string reason = execution.TimedOut
                        ? "The signed OBS graphics-offset helper timed out."
                        : $"The signed OBS graphics-offset helper exited with code {execution.ExitCode}.";

                    diagnostics.Add($"{root}: {reason}");
                    lastFailure = new ObsGameCaptureCompatibilityReport(
                        ObsGameCaptureCompatibilityStatus.OffsetHelperFailed,
                        reason,
                        paths,
                        identities,
                        null,
                        execution,
                        diagnostics);
                    continue;
                }

                if (execution.StandardOutput.Length > MaximumHelperOutputCharacters ||
                    execution.StandardError.Length > MaximumHelperOutputCharacters)
                {
                    diagnostics.Add($"{root}: The signed OBS graphics-offset helper returned more output than allowed.");
                    lastFailure = new ObsGameCaptureCompatibilityReport(
                        ObsGameCaptureCompatibilityStatus.OffsetOutputInvalid,
                        "The signed OBS graphics-offset helper returned more output than allowed.",
                        paths,
                        identities,
                        null,
                        execution,
                        diagnostics);
                    continue;
                }

                ObsGraphicsOffsetValues offsets;

                try
                {
                    offsets = ObsGraphicsOffsetParser.Parse(execution.StandardOutput);
                }
                catch (FormatException)
                {
                    diagnostics.Add($"{root}: The signed OBS graphics-offset helper returned an unsupported output format.");
                    lastFailure = new ObsGameCaptureCompatibilityReport(
                        ObsGameCaptureCompatibilityStatus.OffsetOutputInvalid,
                        "The signed OBS graphics-offset helper returned an unsupported output format.",
                        paths,
                        identities,
                        null,
                        execution,
                        diagnostics);
                    continue;
                }

                diagnostics.Add($"Compatible OBS Game Capture hook {identities[0].FileVersion} found at {root}.");

                return new ObsGameCaptureCompatibilityReport(
                    ObsGameCaptureCompatibilityStatus.Compatible,
                    "The installed OBS Game Capture binaries are trusted and protocol-compatible.",
                    paths,
                    identities,
                    offsets,
                    execution,
                    diagnostics.ToArray());
            }

            if (lastFailure != null)
            {
                return lastFailure with { Diagnostics = diagnostics.ToArray() };
            }

            return new ObsGameCaptureCompatibilityReport(
                ObsGameCaptureCompatibilityStatus.BinarySetInvalid,
                "OBS Studio was found, but no complete Game Capture binary set matched the target architecture.",
                null,
                Array.Empty<ObsGameCaptureBinaryIdentity>(),
                null,
                null,
                diagnostics);
        }

        public static async Task<ObsOffsetHelperExecutionResult> RunOffsetHelperAsync(
            string helperPath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            string fullPath = Path.GetFullPath(helperPath);
            var stopwatch = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fullPath,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                return new ObsOffsetHelperExecutionResult(false, false, -1, string.Empty, string.Empty, stopwatch.Elapsed);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);

                return new ObsOffsetHelperExecutionResult(
                    true,
                    false,
                    process.ExitCode,
                    output,
                    error,
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);

                return new ObsOffsetHelperExecutionResult(
                    true,
                    true,
                    process.HasExited ? process.ExitCode : -1,
                    GetCompletedText(outputTask),
                    GetCompletedText(errorTask),
                    stopwatch.Elapsed);
            }
            catch
            {
                TryKillProcessTree(process);
                throw;
            }
        }

        private static ObsGameCaptureCompatibilityReport ValidateBinarySet(
            ObsGameCaptureBinaryPaths paths,
            IReadOnlyList<ObsGameCaptureBinaryIdentity> identities,
            IReadOnlyList<string> diagnostics)
        {
            ObsGameCaptureBinaryIdentity architectureMismatch = identities.FirstOrDefault(
                x => x.Architecture != paths.TargetArchitecture);

            if (architectureMismatch != null)
            {
                return new ObsGameCaptureCompatibilityReport(
                    ObsGameCaptureCompatibilityStatus.ArchitectureMismatch,
                    $"{Path.GetFileName(architectureMismatch.Path)} does not match the target architecture.",
                    paths,
                    identities,
                    null,
                    null,
                    diagnostics);
            }

            ObsGameCaptureBinaryIdentity untrusted = identities.FirstOrDefault(x => !x.Trust.IsTrusted);

            if (untrusted != null)
            {
                return new ObsGameCaptureCompatibilityReport(
                    ObsGameCaptureCompatibilityStatus.SignatureInvalid,
                    $"{Path.GetFileName(untrusted.Path)} failed Windows Authenticode verification ({untrusted.Trust.NativeStatusHex}).",
                    paths,
                    identities,
                    null,
                    null,
                    diagnostics);
            }

            bool signerIsObs = identities.All(x =>
                !string.IsNullOrWhiteSpace(x.Trust.SignerThumbprint) &&
                x.Trust.SignerSubject.Contains("OBS Project, LLC", StringComparison.OrdinalIgnoreCase));

            if (!signerIsObs)
            {
                return new ObsGameCaptureCompatibilityReport(
                    ObsGameCaptureCompatibilityStatus.SignerMismatch,
                    "One or more OBS Game Capture binaries do not identify the OBS Project as signer.",
                    paths,
                    identities,
                    null,
                    null,
                    diagnostics);
            }

            ObsGameCaptureBinaryIdentity hook = identities.Single(x => x.Kind == ObsGameCaptureArtifactKind.GraphicsHook);

            if (!Version.TryParse(hook.FileVersion, out Version version) ||
                !ObsHookProtocol.IsSupportedVersion((uint)version.Major, (uint)version.Minor))
            {
                return new ObsGameCaptureCompatibilityReport(
                    ObsGameCaptureCompatibilityStatus.HookVersionUnsupported,
                    $"OBS Game Capture hook version '{hook.FileVersion}' is not supported.",
                    paths,
                    identities,
                    null,
                    null,
                    diagnostics);
            }

            return null;
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static async Task WaitForExitAfterKillAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
            }
        }

        private static string GetCompletedText(Task<string> task)
        {
            return task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;
        }

        private static ObsGameCaptureCompatibilityReport Failure(
            ObsGameCaptureCompatibilityStatus status,
            string message)
        {
            return new ObsGameCaptureCompatibilityReport(
                status,
                message,
                null,
                Array.Empty<ObsGameCaptureBinaryIdentity>(),
                null,
                null,
                Array.Empty<string>());
        }
    }
}
