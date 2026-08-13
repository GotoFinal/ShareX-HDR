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

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    public enum ObsGameCaptureSessionSource
    {
        None,
        StartedOwnedHook,
        RestartedOwnedHook,
        ReusedOwnedHook,
        ReusedExistingHostReadOnly
    }

    public sealed record ObsGameCaptureAttempt(
        bool Succeeded,
        ObsGameCaptureSessionSource SessionSource,
        uint ProcessId,
        string ProcessName,
        uint DxgiFormat,
        string ColorInterpretation,
        TimeSpan Duration,
        string Message);

    /// <summary>
    /// Application-owned coordinator for configured OBS Game Capture targets.
    /// Owned hooks are cached briefly; foreign/OBS-owned hooks are read only.
    /// </summary>
    public sealed class ObsGameCaptureService : IDisposable
    {
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);

        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<uint, CachedOwnedSession> ownedSessions =
            new Dictionary<uint, CachedOwnedSession>();
        private readonly Dictionary<string, ObsGameCaptureCompatibilityReport> compatibilityCache =
            new Dictionary<string, ObsGameCaptureCompatibilityReport>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer cleanupTimer;
        private bool disposed;

        public ObsGameCaptureService()
        {
            cleanupTimer = new Timer(
                _ => CleanupExpiredSessions(),
                null,
                CleanupInterval,
                CleanupInterval);
        }

        public bool TryCapture(
            Rectangle requestedBounds,
            HdrCaptureSettings hdrSettings,
            IntPtr preferredWindow,
            out HdrImageDocument document,
            out ObsGameCaptureAttempt attempt)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            document = null;
            Stopwatch stopwatch = Stopwatch.StartNew();
            ObsGameCaptureSettings settings = hdrSettings?.ObsGameCapture;

            if (!ObsGameCaptureTargetDetector.TryFindTarget(
                requestedBounds,
                settings,
                preferredWindow,
                out ObsGameCaptureTarget target,
                out string targetReason))
            {
                attempt = Failure(stopwatch, targetReason);
                return false;
            }

            gate.Wait();

            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                CleanupExpiredSessionsCore(DateTimeOffset.UtcNow);

                if (TryCaptureOwnedSession(
                    target,
                    requestedBounds,
                    settings,
                    stopwatch,
                    out document,
                    out attempt))
                {
                    return true;
                }

                bool existingHost = ObsGameCapturePassiveSession.IsHostPresent(target.ProcessId);

                if (existingHost)
                {
                    if (!settings.ReuseExistingHook)
                    {
                        attempt = Failure(stopwatch,
                            "An OBS-compatible host already owns this game's hook and read-only reuse is disabled.",
                            target);
                        return false;
                    }

                    if (!ObsGameCapturePassiveSession.TryOpen(
                        target.ProcessId,
                        unchecked((ulong)target.Window.ToInt64()),
                        out ObsGameCapturePassiveSession passiveSession,
                        out string passiveReason))
                    {
                        attempt = Failure(stopwatch,
                            $"An existing OBS-compatible host was found, but its publication could not be opened: {passiveReason}",
                            target);
                        return false;
                    }

                    using (passiveSession)
                    {
                        document = CopyPublicationToRequestedDocument(
                            passiveSession.Publication,
                            target,
                            requestedBounds,
                            settings,
                            out string colorInterpretation);
                        attempt = Success(
                            stopwatch,
                            target,
                            passiveSession.Publication,
                            ObsGameCaptureSessionSource.ReusedExistingHostReadOnly,
                            colorInterpretation,
                            "Copied an existing OBS-compatible publication read-only.");
                        return true;
                    }
                }

                ObsGameCaptureCompatibilityReport compatibility = GetCompatibility(target, settings);

                if (!compatibility.IsCompatible)
                {
                    attempt = Failure(stopwatch,
                        $"Installed OBS Game Capture is unavailable: {compatibility.Status}: {compatibility.Message}",
                        target);
                    return false;
                }

                var options = new ObsGameCaptureBootstrapOptions
                {
                    FirstFrameTimeout = TimeSpan.FromSeconds(10),
                    HookInitializationTimeout = TimeSpan.FromSeconds(10),
                    InjectionTimeout = TimeSpan.FromSeconds(10)
                };
                ObsGameCaptureBootstrapSession session = ObsGameCaptureBootstrapSession.StartAsync(
                    compatibility,
                    target.ProcessId,
                    target.ThreadId,
                    target.Window,
                    options).GetAwaiter().GetResult();

                try
                {
                    ObsGameCapturePublication publication = session.WaitForPublicationAsync().GetAwaiter().GetResult();
                    var cached = new CachedOwnedSession(
                        target,
                        session,
                        publication,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromSeconds(settings.SessionIdleTimeoutSeconds));
                    ownedSessions[target.ProcessId] = cached;
                    document = CopyPublicationToRequestedDocument(
                        publication,
                        target,
                        requestedBounds,
                        settings,
                        out string colorInterpretation);
                    cached.LastUsedUtc = DateTimeOffset.UtcNow;
                    attempt = Success(
                        stopwatch,
                        target,
                        publication,
                        session.UsedExistingHook
                            ? ObsGameCaptureSessionSource.RestartedOwnedHook
                            : ObsGameCaptureSessionSource.StartedOwnedHook,
                        colorInterpretation,
                        session.UsedExistingHook
                            ? "Restarted an inactive exact OBS hook and retained the owned session."
                            : "Started the exact installed OBS hook and retained the owned session.");
                    return true;
                }
                catch
                {
                    session.Dispose();
                    ownedSessions.Remove(target.ProcessId);
                    throw;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or
                UnauthorizedAccessException or NotSupportedException or ArgumentException or
                System.Runtime.InteropServices.COMException)
            {
                document?.Dispose();
                document = null;
                attempt = Failure(stopwatch, $"OBS Game Capture failed safely: {ex.Message}", target);
                DebugHelper.WriteException(ex, "OBS Game Capture failed; falling back to the normal screenshot backend.");
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        private bool TryCaptureOwnedSession(
            ObsGameCaptureTarget target,
            Rectangle requestedBounds,
            ObsGameCaptureSettings settings,
            Stopwatch stopwatch,
            out HdrImageDocument document,
            out ObsGameCaptureAttempt attempt)
        {
            document = null;
            attempt = null;

            if (!ownedSessions.TryGetValue(target.ProcessId, out CachedOwnedSession cached))
            {
                return false;
            }

            if (!cached.Matches(target))
            {
                RemoveOwnedSession(target.ProcessId);
                return false;
            }

            try
            {
                document = CopyPublicationToRequestedDocument(
                    cached.Publication,
                    target,
                    requestedBounds,
                    settings,
                    out string colorInterpretation);
                cached.LastUsedUtc = DateTimeOffset.UtcNow;
                cached.IdleTimeout = TimeSpan.FromSeconds(settings.SessionIdleTimeoutSeconds);
                attempt = Success(
                    stopwatch,
                    target,
                    cached.Publication,
                    ObsGameCaptureSessionSource.ReusedOwnedHook,
                    colorInterpretation,
                    "Reused the ShareX-owned OBS hook publication without reinjection.");
                return true;
            }
            catch
            {
                RemoveOwnedSession(target.ProcessId);
                throw;
            }
        }

        private ObsGameCaptureCompatibilityReport GetCompatibility(
            ObsGameCaptureTarget target,
            ObsGameCaptureSettings settings)
        {
            string explicitPath = string.IsNullOrWhiteSpace(settings.ObsInstallationPath)
                ? string.Empty
                : Path.GetFullPath(settings.ObsInstallationPath);
            string key = $"{target.Architecture}|{explicitPath}";

            if (!compatibilityCache.TryGetValue(key, out ObsGameCaptureCompatibilityReport compatibility) ||
                !compatibility.IsCompatible)
            {
                compatibility = ObsGameCaptureCompatibilityProbe.ProbeAsync(
                    target.Architecture,
                    string.IsNullOrWhiteSpace(explicitPath) ? null : explicitPath).GetAwaiter().GetResult();
                compatibilityCache[key] = compatibility;
            }

            return compatibility;
        }

        private static HdrImageDocument CopyPublicationToRequestedDocument(
            ObsGameCapturePublication publication,
            ObsGameCaptureTarget target,
            Rectangle requestedBounds,
            ObsGameCaptureSettings settings,
            out string colorInterpretation)
        {
            HdrDisplayMetadata displayMetadata = GetDisplayMetadata(target.Monitor);
            ObsGameCaptureRgb10A2ColorSpace? colorSpace = ResolveColorSpace(
                publication,
                settings,
                displayMetadata,
                out colorInterpretation);
            string displayName = System.Windows.Forms.Screen.FromHandle(target.Window).DeviceName;
            bool wasHdrActive = displayMetadata?.IsHdrActive == true ||
                colorSpace == ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq ||
                (Format)publication.HookInfo.Format == Format.R16G16B16A16_Float;
            var metadata = new ObsGameCaptureFrameMetadata(
                string.IsNullOrWhiteSpace(displayName) ? target.ProcessName : displayName,
                wasHdrActive,
                displayMetadata?.SdrWhiteNits ?? 203f,
                displayMetadata?.MaxLuminanceNits ?? 1000f,
                colorSpace);

            using HdrImageDocument source = ObsGameCaptureDocumentFactory.CopyToOwnedDocument(publication, metadata);
            return ComposeToRequestedBounds(source, target.WindowBounds, requestedBounds);
        }

        private static ObsGameCaptureRgb10A2ColorSpace? ResolveColorSpace(
            ObsGameCapturePublication publication,
            ObsGameCaptureSettings settings,
            HdrDisplayMetadata displayMetadata,
            out string interpretation)
        {
            Format format = (Format)publication.HookInfo.Format;

            if (format != Format.R10G10B10A2_UNorm)
            {
                interpretation = format.ToString();
                return null;
            }

            ObsGameCaptureRgb10A2ColorSpace colorSpace = settings.Rgb10A2Interpretation switch
            {
                ObsGameCaptureRgb10A2Interpretation.Rec2100Pq => ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq,
                ObsGameCaptureRgb10A2Interpretation.Srgb => ObsGameCaptureRgb10A2ColorSpace.Srgb,
                _ => displayMetadata?.IsHdrActive == true
                    ? ObsGameCaptureRgb10A2ColorSpace.Rec2100Pq
                    : ObsGameCaptureRgb10A2ColorSpace.Srgb
            };
            interpretation = settings.Rgb10A2Interpretation == ObsGameCaptureRgb10A2Interpretation.Automatic
                ? $"Automatic -> {colorSpace} (display HDR={displayMetadata?.IsHdrActive == true})"
                : colorSpace.ToString();
            return colorSpace;
        }

        private static HdrDisplayMetadata GetDisplayMetadata(IntPtr monitor)
        {
            try
            {
                Dictionary<IntPtr, HdrDisplayMetadata> metadata = HdrDisplayMetadata.GetByMonitor();
                return metadata.TryGetValue(monitor, out HdrDisplayMetadata value) ? value : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        private static HdrImageDocument ComposeToRequestedBounds(
            HdrImageDocument source,
            Rectangle windowBounds,
            Rectangle requestedBounds)
        {
            Rectangle gameIntersection = Rectangle.Intersect(windowBounds, requestedBounds);

            if (gameIntersection.Width <= 0 || gameIntersection.Height <= 0)
            {
                throw new InvalidOperationException("The configured game client no longer intersects the screenshot area.");
            }

            HdrImageDocument background = null;
            HdrRgba16FloatBuffer destination = null;

            try
            {
                var segments = new List<HdrCaptureSourceSegment>();

                if (gameIntersection == requestedBounds)
                {
                    destination = new HdrRgba16FloatBuffer(requestedBounds.Width, requestedBounds.Height);
                }
                else
                {
                    background = CaptureBackgroundDocument(requestedBounds);
                    destination = background.MasterPixels.Clone();
                    segments.AddRange(background.SourceSegments);
                }

                HdrRgba16FloatBuffer sourcePixels = source.MasterPixels;
                double scaleX = sourcePixels.Width / (double)windowBounds.Width;
                double scaleY = sourcePixels.Height / (double)windowBounds.Height;
                int sourceLeft = Math.Clamp(
                    (int)Math.Floor((gameIntersection.Left - windowBounds.Left) * scaleX),
                    0,
                    sourcePixels.Width - 1);
                int sourceTop = Math.Clamp(
                    (int)Math.Floor((gameIntersection.Top - windowBounds.Top) * scaleY),
                    0,
                    sourcePixels.Height - 1);
                int sourceRight = Math.Clamp(
                    (int)Math.Ceiling((gameIntersection.Right - windowBounds.Left) * scaleX),
                    sourceLeft + 1,
                    sourcePixels.Width);
                int sourceBottom = Math.Clamp(
                    (int)Math.Ceiling((gameIntersection.Bottom - windowBounds.Top) * scaleY),
                    sourceTop + 1,
                    sourcePixels.Height);
                var sourceRectangle = Rectangle.FromLTRB(sourceLeft, sourceTop, sourceRight, sourceBottom);
                var destinationRectangle = new Rectangle(
                    gameIntersection.X - requestedBounds.X,
                    gameIntersection.Y - requestedBounds.Y,
                    gameIntersection.Width,
                    gameIntersection.Height);

                destination.CopyScaledRegionFrom(sourcePixels, sourceRectangle, destinationRectangle);
                HdrCaptureSourceSegment sourceSegment = source.SourceSegments.First();
                segments.Add(new HdrCaptureSourceSegment(
                    destinationRectangle,
                    sourceSegment.DisplayDeviceName,
                    sourceSegment.WasHdrActive,
                    sourceSegment.SdrWhiteNits,
                    sourceSegment.DisplayPeakNits));

                var result = new HdrImageDocument(requestedBounds, destination, segments);
                destination = null;
                return result;
            }
            finally
            {
                destination?.Dispose();
                background?.Dispose();
            }
        }

        private static HdrImageDocument CaptureBackgroundDocument(Rectangle requestedBounds)
        {
            if (WindowsGraphicsCapture.TryCaptureHdr(
                requestedBounds,
                null,
                out HdrImageDocument hdrBackground))
            {
                return hdrBackground;
            }

            return CaptureSdrBackgroundDocument(requestedBounds);
        }

        private static HdrImageDocument CaptureSdrBackgroundDocument(Rectangle requestedBounds)
        {
            using Bitmap captured = Screenshot.CaptureRectangleNative(requestedBounds);

            if (captured == null)
            {
                throw new InvalidOperationException("The desktop fallback could not capture the non-game pixels.");
            }

            using var bitmap = new Bitmap(
                requestedBounds.Width,
                requestedBounds.Height,
                PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.DrawImageUnscaled(captured, Point.Empty);
            }

            int packedRowBytes = checked(bitmap.Width * 4);
            byte[] pixels = new byte[checked(packedRowBytes * bitmap.Height)];
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(Point.Empty, bitmap.Size),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                int sourceStride = bitmapData.Stride;
                int absoluteStride = Math.Abs(sourceStride);

                for (int y = 0; y < bitmap.Height; y++)
                {
                    int sourceY = sourceStride >= 0 ? y : bitmap.Height - 1 - y;
                    IntPtr sourceRow = IntPtr.Add(bitmapData.Scan0, sourceY * absoluteStride);
                    Marshal.Copy(sourceRow, pixels, y * packedRowBytes, packedRowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            const float sdrWhiteNits = HdrCaptureSettings.DefaultBrightnessNits;
            var destination = new HdrRgba16FloatBuffer(bitmap.Width, bitmap.Height);
            string deviceName = System.Windows.Forms.Screen.FromRectangle(requestedBounds).DeviceName;
            var segment = new HdrCaptureSourceSegment(
                new Rectangle(Point.Empty, requestedBounds.Size),
                deviceName,
                false,
                sdrWhiteNits,
                sdrWhiteNits);
            var document = new HdrImageDocument(requestedBounds, destination, new[] { segment });

            try
            {
                document.CompositeSdrAsset(
                    pixels,
                    packedRowBytes,
                    bitmap.Width,
                    bitmap.Height,
                    0,
                    0,
                    sdrWhiteNits);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        private void CleanupExpiredSessions()
        {
            if (disposed || !gate.Wait(0))
            {
                return;
            }

            try
            {
                if (!disposed)
                {
                    CleanupExpiredSessionsCore(DateTimeOffset.UtcNow);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private void CleanupExpiredSessionsCore(DateTimeOffset now)
        {
            uint[] expired = ownedSessions
                .Where(x => now - x.Value.LastUsedUtc >= x.Value.IdleTimeout || !x.Value.IsTargetAlive())
                .Select(x => x.Key)
                .ToArray();

            foreach (uint processId in expired)
            {
                RemoveOwnedSession(processId);
            }
        }

        private void RemoveOwnedSession(uint processId)
        {
            if (ownedSessions.Remove(processId, out CachedOwnedSession cached))
            {
                cached.Dispose();
            }
        }

        private static ObsGameCaptureAttempt Success(
            Stopwatch stopwatch,
            ObsGameCaptureTarget target,
            ObsGameCapturePublication publication,
            ObsGameCaptureSessionSource source,
            string colorInterpretation,
            string message)
        {
            return new ObsGameCaptureAttempt(
                true,
                source,
                target.ProcessId,
                target.ProcessName,
                publication.HookInfo.Format,
                colorInterpretation,
                stopwatch.Elapsed,
                message);
        }

        private static ObsGameCaptureAttempt Failure(
            Stopwatch stopwatch,
            string message,
            ObsGameCaptureTarget target = null)
        {
            return new ObsGameCaptureAttempt(
                false,
                ObsGameCaptureSessionSource.None,
                target?.ProcessId ?? 0,
                target?.ProcessName ?? string.Empty,
                0,
                string.Empty,
                stopwatch.Elapsed,
                message);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            gate.Wait();

            try
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                cleanupTimer.Dispose();

                foreach (CachedOwnedSession cached in ownedSessions.Values)
                {
                    cached.Dispose();
                }

                ownedSessions.Clear();
                compatibilityCache.Clear();
            }
            finally
            {
                gate.Release();
            }
        }

        private sealed class CachedOwnedSession : IDisposable
        {
            public ObsGameCaptureTarget Target { get; }
            public ObsGameCaptureBootstrapSession Session { get; }
            public ObsGameCapturePublication Publication { get; }
            public DateTimeOffset LastUsedUtc { get; set; }
            public TimeSpan IdleTimeout { get; set; }

            public CachedOwnedSession(
                ObsGameCaptureTarget target,
                ObsGameCaptureBootstrapSession session,
                ObsGameCapturePublication publication,
                DateTimeOffset lastUsedUtc,
                TimeSpan idleTimeout)
            {
                Target = target;
                Session = session;
                Publication = publication;
                LastUsedUtc = lastUsedUtc;
                IdleTimeout = idleTimeout;
            }

            public bool Matches(ObsGameCaptureTarget target)
            {
                return Target.ProcessId == target.ProcessId &&
                    Target.ProcessStartTimeUtcTicks == target.ProcessStartTimeUtcTicks &&
                    Target.Window == target.Window &&
                    Target.WindowBounds == target.WindowBounds;
            }

            public bool IsTargetAlive()
            {
                try
                {
                    using Process process = Process.GetProcessById(checked((int)Target.ProcessId));
                    return !process.HasExited &&
                        process.StartTime.ToUniversalTime().Ticks == Target.ProcessStartTimeUtcTicks;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return false;
                }
            }

            public void Dispose()
            {
                Session.Dispose();
            }
        }
    }
}
