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
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    public sealed class ObsGameCaptureBootstrapOptions
    {
        public ulong FrameIntervalNanoseconds { get; init; } = 16_666_667;
        public bool ForceSharedMemory { get; init; }
        public bool CaptureOverlay { get; init; }
        public bool AllowSrgbAlias { get; init; } = true;
        public TimeSpan InjectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan HookInitializationTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(10);

        internal void Validate()
        {
            if (FrameIntervalNanoseconds == 0 || InjectionTimeout <= TimeSpan.Zero ||
                HookInitializationTimeout <= TimeSpan.Zero || FirstFrameTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ObsGameCaptureBootstrapOptions));
            }
        }
    }

    public sealed record ObsGameCapturePublication(
        ObsHookInfo HookInfo,
        uint SharedTextureHandle,
        string TextureMappingName);

    /// <summary>
    /// Owns the host-side bootstrap objects for an exact OBS-signed Game Capture hook.
    /// This type does not copy the published D3D texture; callers must keep the session
    /// alive until they have copied the resource into ShareX-owned storage.
    /// </summary>
    public sealed class ObsGameCaptureBootstrapSession : IDisposable
    {
        private const int PipeBufferSize = 4096;
        private const int MaximumPipeLogMessages = 128;
        private const uint MaximumDimension = 32_768;
        private const uint MaximumMappingSize = 1024 * 1024 * 1024;

        private readonly uint processId;
        private readonly TimeSpan firstFrameTimeout;
        private readonly ulong rootWindow;
        private readonly Mutex keepAliveMutex;
        private readonly NamedPipeServerStream pipe;
        private readonly CancellationTokenSource pipeCancellation;
        private Task pipeTask;
        private readonly object logSync = new object();
        private readonly List<string> hookLog = new List<string>();

        private MemoryMappedFile hookInfoMapping;
        private MemoryMappedViewAccessor hookInfoView;
        private Mutex textureMutex1;
        private Mutex textureMutex2;
        private EventWaitHandle restartEvent;
        private EventWaitHandle stopEvent;
        private EventWaitHandle initializeEvent;
        private EventWaitHandle hookReadyEvent;
        private EventWaitHandle hookExitEvent;
        private MemoryMappedFile textureMapping;
        private MemoryMappedViewAccessor textureView;
        private bool usedExistingHook;
        private bool disposed;

        private ObsGameCaptureBootstrapSession(
            uint processId,
            ulong rootWindow,
            TimeSpan firstFrameTimeout,
            Mutex keepAliveMutex,
            NamedPipeServerStream pipe,
            CancellationTokenSource pipeCancellation,
            Task pipeTask)
        {
            this.processId = processId;
            this.rootWindow = rootWindow;
            this.firstFrameTimeout = firstFrameTimeout;
            this.keepAliveMutex = keepAliveMutex;
            this.pipe = pipe;
            this.pipeCancellation = pipeCancellation;
            this.pipeTask = pipeTask;
        }

        public uint ProcessId => processId;
        public ulong RootWindow => rootWindow;
        public bool UsedExistingHook => usedExistingHook;

        public IReadOnlyList<string> HookLog
        {
            get
            {
                lock (logSync)
                {
                    return hookLog.ToArray();
                }
            }
        }

        public static async Task<ObsGameCaptureBootstrapSession> StartAsync(
            ObsGameCaptureCompatibilityReport compatibility,
            uint processId,
            uint threadId,
            IntPtr rootWindow,
            ObsGameCaptureBootstrapOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(compatibility);

            if (!compatibility.IsCompatible || compatibility.BinaryPaths == null || compatibility.Offsets == null)
            {
                throw new InvalidOperationException("A compatible, non-injecting OBS binary probe is required before bootstrap.");
            }

            if (processId == 0 || threadId == 0 || rootWindow == IntPtr.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(processId));
            }

            uint actualThreadId = GetWindowThreadProcessId(rootWindow, out uint actualProcessId);

            if (actualProcessId != processId || actualThreadId != threadId)
            {
                throw new InvalidOperationException("The target window, process, and thread do not identify the same owner.");
            }

            RevalidateExactBinaries(compatibility);
            options ??= new ObsGameCaptureBootstrapOptions();
            options.Validate();

            Mutex keepAlive = null;
            NamedPipeServerStream pipe = null;
            CancellationTokenSource pipeCancellation = null;
            Task pipeTask = Task.CompletedTask;
            ObsGameCaptureBootstrapSession session = null;

            try
            {
                keepAlive = new Mutex(false, ObsHookProtocol.KeepAliveName(processId), out bool createdNew);

                if (!createdNew)
                {
                    throw new InvalidOperationException("Another OBS-compatible host already owns the target process.");
                }

                pipe = new NamedPipeServerStream(
                    ObsHookProtocol.PipeName(processId),
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous,
                    PipeBufferSize,
                    PipeBufferSize);
                pipeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                session = new ObsGameCaptureBootstrapSession(
                    processId,
                    unchecked((ulong)rootWindow.ToInt64()),
                    options.FirstFrameTimeout,
                    keepAlive,
                    pipe,
                    pipeCancellation,
                    pipeTask);
                pipeTask = session.ReadHookPipeAsync(pipeCancellation.Token);
                session.pipeTask = pipeTask;

                session.usedExistingHook = EventWaitHandle.TryOpenExisting(
                    ObsHookProtocol.RestartEventName(processId),
                    out session.restartEvent);

                if (session.usedExistingHook)
                {
                    if (!session.restartEvent.Set())
                    {
                        throw new InvalidOperationException("Failed to signal the existing OBS hook restart event.");
                    }
                }
                else
                {
                    ObsOffsetHelperExecutionResult injection = await RunInjectorAsync(
                        compatibility.BinaryPaths.InjectHelperPath,
                        compatibility.BinaryPaths.GraphicsHookPath,
                        threadId,
                        options.InjectionTimeout,
                        cancellationToken).ConfigureAwait(false);

                    if (!injection.Started || injection.TimedOut || injection.ExitCode != 0)
                    {
                        throw new InvalidOperationException(injection.TimedOut
                            ? "The exact OBS injector helper timed out."
                            : $"The exact OBS injector helper exited with code {injection.ExitCode}.");
                    }
                }

                await session.OpenHookObjectsAsync(options.HookInitializationTimeout, cancellationToken)
                    .ConfigureAwait(false);
                session.WriteHostOptions(compatibility.Offsets, options);

                if (!session.initializeEvent.Set())
                {
                    throw new InvalidOperationException("Failed to signal the OBS hook initialize event.");
                }

                return session;
            }
            catch
            {
                session?.Dispose();

                if (session == null)
                {
                    pipeCancellation?.Dispose();
                    pipe?.Dispose();
                    keepAlive?.Dispose();
                }

                throw;
            }
        }

        public async Task<ObsGameCapturePublication> WaitForPublicationAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (textureView != null)
            {
                throw new InvalidOperationException("This OBS hook session has already published a frame mapping.");
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            bool ready = await WaitForSignalAsync(hookReadyEvent, timeout, cancellationToken).ConfigureAwait(false);

            if (!ready)
            {
                throw new TimeoutException("The OBS hook did not publish a frame before the deadline.");
            }

            ObsHookInfo info = ReadHookInfo();
            ValidatePublication(info);

            string mappingName = OpenTextureMapping(info);
            textureView = textureMapping.CreateViewAccessor(0, info.MapSize, MemoryMappedFileAccess.Read);

            uint sharedTextureHandle = info.CaptureType == ObsHookCaptureType.Texture
                ? textureView.ReadUInt32(0)
                : 0;

            return new ObsGameCapturePublication(info, sharedTextureHandle, mappingName);
        }


        public Task<ObsGameCapturePublication> WaitForPublicationAsync(
            CancellationToken cancellationToken = default)
        {
            return WaitForPublicationAsync(firstFrameTimeout, cancellationToken);
        }
        private string OpenTextureMapping(ObsHookInfo info)
        {
            string mappingName = ObsHookProtocol.TextureMappingName(rootWindow, info.MapId);

            try
            {
                textureMapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read);
                return mappingName;
            }
            catch (FileNotFoundException) when (info.Window != 0 && info.Window != rootWindow)
            {
                mappingName = ObsHookProtocol.TextureMappingName(info.Window, info.MapId);
                textureMapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read);
                return mappingName;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                stopEvent?.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            pipeCancellation.Cancel();
            textureView?.Dispose();
            textureMapping?.Dispose();
            hookInfoView?.Dispose();
            hookInfoMapping?.Dispose();
            hookExitEvent?.Dispose();
            hookReadyEvent?.Dispose();
            initializeEvent?.Dispose();
            stopEvent?.Dispose();
            restartEvent?.Dispose();
            textureMutex2?.Dispose();
            textureMutex1?.Dispose();
            pipe.Dispose();
            keepAliveMutex.Dispose();
            pipeCancellation.Dispose();
        }

        private async Task OpenHookObjectsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Stopwatch timer = Stopwatch.StartNew();

            while (timer.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TryOpenHookInfo();
                TryOpenMutex(ref textureMutex1, ObsHookProtocol.TextureMutexName(processId, 1));
                TryOpenMutex(ref textureMutex2, ObsHookProtocol.TextureMutexName(processId, 2));
                TryOpenEvent(ref restartEvent, ObsHookProtocol.RestartEventName(processId));
                TryOpenEvent(ref stopEvent, ObsHookProtocol.StopEventName(processId));
                TryOpenEvent(ref initializeEvent, ObsHookProtocol.InitializeEventName(processId));
                TryOpenEvent(ref hookReadyEvent, ObsHookProtocol.HookReadyEventName(processId));
                TryOpenEvent(ref hookExitEvent, ObsHookProtocol.HookExitEventName(processId));

                if (hookInfoView != null && textureMutex1 != null && textureMutex2 != null &&
                    restartEvent != null && stopEvent != null && initializeEvent != null &&
                    hookReadyEvent != null && hookExitEvent != null)
                {
                    return;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for the OBS hook's named bootstrap objects.");
        }

        private void TryOpenHookInfo()
        {
            if (hookInfoMapping != null)
            {
                return;
            }

            try
            {
                hookInfoMapping = MemoryMappedFile.OpenExisting(
                    ObsHookProtocol.HookInfoName(processId),
                    MemoryMappedFileRights.ReadWrite);
                hookInfoView = hookInfoMapping.CreateViewAccessor(
                    0,
                    ObsHookProtocol.HookInfoSize,
                    MemoryMappedFileAccess.ReadWrite);
            }
            catch (FileNotFoundException)
            {
                hookInfoView?.Dispose();
                hookInfoView = null;
                hookInfoMapping?.Dispose();
                hookInfoMapping = null;
            }
        }

        private static void TryOpenMutex(ref Mutex mutex, string name)
        {
            if (mutex == null)
            {
                Mutex.TryOpenExisting(name, out mutex);
            }
        }

        private static void TryOpenEvent(ref EventWaitHandle handle, string name)
        {
            if (handle == null)
            {
                EventWaitHandle.TryOpenExisting(name, out handle);
            }
        }

        private void WriteHostOptions(ObsGraphicsOffsetValues offsets, ObsGameCaptureBootstrapOptions options)
        {
            ObsHookInfo info = ReadHookInfo();
            info.Offsets = offsets.ToNative();
            info.UnusedUseScale = false;
            info.ForceSharedMemory = options.ForceSharedMemory;
            info.CaptureOverlay = options.CaptureOverlay;
            info.AllowSrgbAlias = options.AllowSrgbAlias;
            info.FrameInterval = options.FrameIntervalNanoseconds;
            hookInfoView.Write(0, ref info);
            hookInfoView.Flush();
        }

        private ObsHookInfo ReadHookInfo()
        {
            hookInfoView.Read(0, out ObsHookInfo info);
            return info;
        }

        private static void ValidatePublication(ObsHookInfo info)
        {
            if (!ObsHookProtocol.IsSupportedVersion(info.HookVersionMajor, info.HookVersionMinor))
            {
                throw new InvalidDataException(
                    $"OBS hook protocol {info.HookVersionMajor}.{info.HookVersionMinor} is unsupported.");
            }

            if (info.Width == 0 || info.Height == 0 || info.Width > MaximumDimension || info.Height > MaximumDimension)
            {
                throw new InvalidDataException("OBS hook published invalid frame dimensions.");
            }

            if (info.MapSize < 4 || info.MapSize > MaximumMappingSize)
            {
                throw new InvalidDataException("OBS hook published an invalid mapping size.");
            }

            if (info.CaptureType != ObsHookCaptureType.Texture && info.CaptureType != ObsHookCaptureType.Memory)
            {
                throw new InvalidDataException("OBS hook published an unsupported capture transport.");
            }
        }

        private static void RevalidateExactBinaries(ObsGameCaptureCompatibilityReport compatibility)
        {
            foreach (ObsGameCaptureBinaryIdentity expected in compatibility.Binaries)
            {
                ObsGameCaptureBinaryIdentity current = ObsGameCaptureBinaryInspector.Inspect(expected.Path, expected.Kind);

                if (!current.Trust.IsTrusted ||
                    !current.Trust.SignerSubject.Contains("OBS Project, LLC", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    current.Architecture != expected.Architecture)
                {
                    throw new InvalidOperationException(
                        $"The verified OBS binary '{Path.GetFileName(expected.Path)}' changed before injection.");
                }
            }
        }

        private async Task ReadHookPipeAsync(CancellationToken cancellationToken)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                byte[] buffer = new byte[PipeBufferSize];

                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    using var message = new MemoryStream();
                    int bytesRead;

                    do
                    {
                        bytesRead = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                        if (bytesRead > 0 && message.Length < PipeBufferSize * 4)
                        {
                            int accepted = Math.Min(bytesRead, PipeBufferSize * 4 - (int)message.Length);
                            message.Write(buffer, 0, accepted);
                        }
                    }
                    while (bytesRead > 0 && !pipe.IsMessageComplete);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    string text = Encoding.UTF8.GetString(message.ToArray()).TrimEnd('\0', '\r', '\n');

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lock (logSync)
                        {
                            if (hookLog.Count == MaximumPipeLogMessages)
                            {
                                hookLog.RemoveAt(0);
                            }

                            hookLog.Add(text);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private static async Task<ObsOffsetHelperExecutionResult> RunInjectorAsync(
            string injectorPath,
            string hookPath,
            uint threadId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = injectorPath,
                    WorkingDirectory = Path.GetDirectoryName(injectorPath) ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add(hookPath);
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add(threadId.ToString(CultureInfo.InvariantCulture));

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

                return new ObsOffsetHelperExecutionResult(
                    true,
                    false,
                    process.ExitCode,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false),
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                return new ObsOffsetHelperExecutionResult(true, true, -1, string.Empty, string.Empty, stopwatch.Elapsed);
            }
            catch
            {
                TryKillProcessTree(process);
                throw;
            }
        }

        private static async Task<bool> WaitForSignalAsync(
            EventWaitHandle handle,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            int timeoutMilliseconds = checked((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));

            return await Task.Run(() =>
            {
                int result = WaitHandle.WaitAny(
                    new WaitHandle[] { handle, cancellationToken.WaitHandle },
                    timeoutMilliseconds);

                if (result == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return result == 0;
            }, CancellationToken.None).ConfigureAwait(false);
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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
