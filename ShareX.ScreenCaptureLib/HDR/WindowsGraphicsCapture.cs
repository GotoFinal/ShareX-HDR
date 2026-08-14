#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using WinRtDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;
using D3DBox = Vortice.Mathematics.Box;
using static Vortice.Direct3D11.D3D11;

namespace ShareX.ScreenCaptureLib
{
    internal static class WindowsGraphicsCapture
    {
        private const int CaptureTimeoutMilliseconds = 1500;
        private const int FreshFrameGraceMilliseconds = 100;
        private const int NormalSessionIdleMilliseconds = 10000;
        private const int RpcEChangedMode = unchecked((int)0x80010106);
        private const uint MonitorDefaultToNearest = 2;

        private static readonly Guid GraphicsCaptureItemInteropGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid GraphicsCaptureItemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid Direct3DDxgiInterfaceAccessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private const string GraphicsCaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        private static readonly object normalCaptureWorkerSync = new object();
        private static NormalCaptureWorker normalCaptureWorker;
        internal static Action<string> PerformanceLogSink { get; set; }

        public static bool TryCapture(Rectangle captureRectangle, HdrCaptureSettings settings, out Bitmap bitmap)
        {
            return TryCapture(captureRectangle, settings, null, out bitmap);
        }

        public static bool TryCapture(
            Rectangle captureRectangle,
            HdrCaptureSettings settings,
            CaptureContext captureContext,
            out Bitmap bitmap)
        {
            bitmap = null;
            settings ??= new HdrCaptureSettings();
            Stopwatch totalTimer = Stopwatch.StartNew();
            bool ownsCaptureContext = captureContext == null;

            if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
            {
                Log($"fallback=GDI reason=invalid-rectangle bounds={FormatRectangle(captureRectangle)}");
                return false;
            }

            try
            {
                Stopwatch metadataTimer = Stopwatch.StartNew();
                List<MonitorCaptureTarget> targets = GetCaptureTargets(captureRectangle);
                metadataTimer.Stop();

                Log(
                    $"start bounds={FormatRectangle(captureRectangle)} backend={settings.ProcessingBackend} mode={settings.ToneMappingMode} gamePromotion={settings.GameWindowPromotionMode} peakMode={settings.PeakBrightnessMode} configuredPeak={settings.HdrBrightnessNits:F1}nits reusable={captureContext?.IsReusable == true}");

                foreach (MonitorCaptureTarget target in targets)
                {
                    Log(
                        $"display={target.DeviceName} bounds={FormatRectangle(target.MonitorBounds)} intersection={FormatRectangle(target.Intersection)} hdr={target.IsHdrActive} sdrWhite={target.SdrWhiteNits:F1}nits displayPeak={target.MaxLuminanceNits:F1}nits");
                }

                // Preserve ShareX's established GDI capture path when Windows HDR
                // is not active on any monitor touched by this capture.
                if (!targets.Exists(target => target.IsHdrActive))
                {
                    Log(
                        $"fallback=GDI reason=no-active-hdr-display metadataMs={metadataTimer.Elapsed.TotalMilliseconds:F1} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                    return false;
                }

                captureContext ??= new CaptureContext(false);
                bitmap = Capture(captureRectangle, settings, targets, captureContext);
                Log(
                    $"success bounds={FormatRectangle(captureRectangle)} metadataMs={metadataTimer.Elapsed.TotalMilliseconds:F1} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                return bitmap != null;
            }
            catch (Exception e)
            {
                bitmap?.Dispose();
                bitmap = null;
                try
                {
                    captureContext?.ResetAfterFailure();
                }
                catch (Exception resetException)
                {
                    DebugHelper.WriteException(resetException, "HDR capture context reset failed.");
                }
                Log(
                    $"fallback=GDI reason={e.GetType().Name} hresult=0x{e.HResult:X8} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                DebugHelper.WriteException(e, "HDR capture failed. Falling back to GDI capture.");
                return false;
            }
            finally
            {
                if (ownsCaptureContext)
                {
                    try
                    {
                        captureContext?.Dispose();
                    }
                    catch (Exception disposeException)
                    {
                        DebugHelper.WriteException(disposeException, "HDR capture context disposal failed.");
                    }
                }
            }
        }

        internal static bool TryCaptureHdr(
            Rectangle captureRectangle,
            CaptureContext captureContext,
            out HdrImageDocument document)
        {
            return TryCaptureHdr(
                captureRectangle,
                captureContext,
                settings: null,
                out document);
        }

        internal static bool TryCaptureHdr(
            Rectangle captureRectangle,
            CaptureContext captureContext,
            HdrCaptureSettings settings,
            out HdrImageDocument document)
        {
            if (captureContext == null)
            {
                return GetNormalCaptureWorker().TryCapture(
                    captureRectangle,
                    settings,
                    out document);
            }

            return TryCaptureHdrCore(
                captureRectangle,
                captureContext,
                settings,
                out document);
        }

        private static bool TryCaptureHdrCore(
            Rectangle captureRectangle,
            CaptureContext captureContext,
            HdrCaptureSettings settings,
            out HdrImageDocument document)
        {
            document = null;
            bool ownsCaptureContext = captureContext == null;
            Stopwatch totalTimer = Stopwatch.StartNew();

            if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
            {
                return false;
            }

            try
            {
                Stopwatch metadataTimer = Stopwatch.StartNew();
                List<MonitorCaptureTarget> targets = GetCaptureTargets(captureRectangle);
                metadataTimer.Stop();

                if (!targets.Exists(target => target.IsHdrActive))
                {
                    Log(
                        $"hdr-document fallback=GDI reason=no-active-hdr-display metadataMs={metadataTimer.Elapsed.TotalMilliseconds:F1} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                    return false;
                }

                captureContext ??= new CaptureContext(false);
                Stopwatch captureTimer = Stopwatch.StartNew();
                document = CaptureHdr(captureRectangle, targets, captureContext, settings);
                captureTimer.Stop();
                Log(
                    $"hdr-document success bounds={FormatRectangle(captureRectangle)} displays={targets.Count} reusable={captureContext.IsReusable} metadataMs={metadataTimer.Elapsed.TotalMilliseconds:F1} captureMs={captureTimer.Elapsed.TotalMilliseconds:F1} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                return document != null;
            }
            catch (Exception e)
            {
                document?.Dispose();
                document = null;

                try
                {
                    captureContext?.ResetAfterFailure();
                }
                catch (Exception resetException)
                {
                    DebugHelper.WriteException(resetException);
                }

                DebugHelper.WriteException(e);
                Log(
                    $"hdr-document fallback=GDI reason={e.GetType().Name} hresult=0x{e.HResult:X8} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                return false;
            }
            finally
            {
                if (ownsCaptureContext)
                {
                    captureContext?.Dispose();
                }
            }
        }

        private static Bitmap Capture(
            Rectangle captureRectangle,
            HdrCaptureSettings settings,
            IReadOnlyList<MonitorCaptureTarget> targets,
            CaptureContext captureContext)
        {
            Bitmap canvas = new Bitmap(captureRectangle.Width, captureRectangle.Height, PixelFormat.Format32bppArgb);

            try
            {
                bool capturedAnyMonitor = false;
                TimeSpan gdiCompositionTime = TimeSpan.Zero;

                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    Stopwatch clearTimer = Stopwatch.StartNew();
                    graphics.Clear(Color.Black);
                    clearTimer.Stop();
                    gdiCompositionTime += clearTimer.Elapsed;

                    foreach (MonitorCaptureTarget target in targets)
                    {
                        if (target.IsHdrActive)
                        {
                            using Bitmap monitorBitmap = captureContext.CaptureMonitor(
                                target.Monitor,
                                settings,
                                target.SdrWhiteNits,
                                target.MaxLuminanceNits);
                            Stopwatch compositionTimer = Stopwatch.StartNew();
                            DrawMonitorIntersection(
                                graphics,
                                monitorBitmap,
                                target.MonitorBounds,
                                target.Intersection,
                                captureRectangle);
                            compositionTimer.Stop();
                            gdiCompositionTime += compositionTimer.Elapsed;
                        }
                        else
                        {
                            // Do not tone-map SDR monitor pixels in a mixed-monitor
                            // capture. Capture those regions with the regular GDI path.
                            using Bitmap sdrBitmap = Screenshot.CaptureRectangleNative(target.Intersection);
                            Stopwatch compositionTimer = Stopwatch.StartNew();
                            graphics.DrawImageUnscaled(
                                sdrBitmap,
                                target.Intersection.X - captureRectangle.X,
                                target.Intersection.Y - captureRectangle.Y);
                            compositionTimer.Stop();
                            gdiCompositionTime += compositionTimer.Elapsed;
                        }

                        capturedAnyMonitor = true;
                    }
                }

                if (!capturedAnyMonitor)
                {
                    canvas.Dispose();
                    return null;
                }

                Log(
                    $"sdr-preview-composition size={canvas.Width}x{canvas.Height} gdiCompositionMs={gdiCompositionTime.TotalMilliseconds:F1}");
                return canvas;
            }
            catch
            {
                canvas.Dispose();
                throw;
            }
        }

        private static HdrImageDocument CaptureHdr(
            Rectangle captureRectangle,
            IReadOnlyList<MonitorCaptureTarget> targets,
            CaptureContext captureContext,
            HdrCaptureSettings settings)
        {
            if (targets.Count == 1 && targets[0].Intersection == captureRectangle)
            {
                return CaptureSingleTargetHdr(captureRectangle, targets[0], captureContext);
            }

            var canvas = new HdrRgba16FloatBuffer(captureRectangle.Width, captureRectangle.Height);
            var segments = new List<HdrCaptureSourceSegment>(targets.Count);

            try
            {
                foreach (MonitorCaptureTarget target in targets)
                {
                    var destinationRectangle = new Rectangle(
                        target.Intersection.X - captureRectangle.X,
                        target.Intersection.Y - captureRectangle.Y,
                        target.Intersection.Width,
                        target.Intersection.Height);
                    float normalizedWhiteNits = ResolveNormalizedWhiteNits(
                        target,
                        targets,
                        settings);
                    float sourceWhiteNits = float.IsFinite(target.SdrWhiteNits) &&
                        target.SdrWhiteNits > 0f
                        ? target.SdrWhiteNits
                        : HdrRgba16FloatBuffer.ReferenceWhiteNits;
                    float rgbScale = target.IsHdrActive
                        ? 1f
                        : normalizedWhiteNits / sourceWhiteNits;
                    captureContext.CaptureMonitorHdrInto(
                        target.Monitor,
                        target.MonitorBounds,
                        target.Intersection,
                        canvas,
                        destinationRectangle,
                        rgbScale);
                    segments.Add(new HdrCaptureSourceSegment(
                        destinationRectangle,
                        target.DeviceName,
                        target.IsHdrActive,
                        normalizedWhiteNits,
                        target.MaxLuminanceNits));

                    if (!target.IsHdrActive && Math.Abs(rgbScale - 1f) > 0.0001f)
                    {
                        Log(
                            $"normalization=fused display={target.DeviceName} sourceWhite={sourceWhiteNits:F1}nits targetWhite={normalizedWhiteNits:F1}nits scale={rgbScale:F3}");
                    }
                }

                return new HdrImageDocument(captureRectangle, canvas, segments);
            }
            catch
            {
                canvas.Dispose();
                throw;
            }
        }

        private static float ResolveNormalizedWhiteNits(
            MonitorCaptureTarget target,
            IReadOnlyList<MonitorCaptureTarget> targets,
            HdrCaptureSettings settings)
        {
            if (target.IsHdrActive || settings == null ||
                settings.MixedMonitorBrightnessMode == HdrMixedMonitorBrightnessMode.Preserve)
            {
                return target.SdrWhiteNits;
            }

            if (settings.MixedMonitorBrightnessMode == HdrMixedMonitorBrightnessMode.Custom)
            {
                return Math.Clamp(
                    settings.MixedMonitorCustomSdrWhiteNits,
                    HdrCaptureSettings.MinimumBrightnessNits,
                    HdrCaptureSettings.MaximumPaperWhiteNits);
            }

            MonitorCaptureTarget nearestHdr = null;
            long nearestDistance = long.MaxValue;

            foreach (MonitorCaptureTarget candidate in targets)
            {
                if (!candidate.IsHdrActive || !float.IsFinite(candidate.SdrWhiteNits) ||
                    candidate.SdrWhiteNits <= 0f)
                {
                    continue;
                }

                long distance = GetRectangleDistanceSquared(
                    target.Intersection,
                    candidate.Intersection);
                if (distance < nearestDistance)
                {
                    nearestHdr = candidate;
                    nearestDistance = distance;
                }
            }

            return nearestHdr != null
                ? Math.Clamp(
                    nearestHdr.SdrWhiteNits,
                    HdrCaptureSettings.MinimumBrightnessNits,
                    HdrCaptureSettings.MaximumPaperWhiteNits)
                : target.SdrWhiteNits;
        }

        private static long GetRectangleDistanceSquared(Rectangle first, Rectangle second)
        {
            long horizontal = first.Right < second.Left
                ? second.Left - first.Right
                : second.Right < first.Left
                    ? first.Left - second.Right
                    : 0;
            long vertical = first.Bottom < second.Top
                ? second.Top - first.Bottom
                : second.Bottom < first.Top
                    ? first.Top - second.Bottom
                    : 0;
            return horizontal * horizontal + vertical * vertical;
        }

        public static void Prewarm()
        {
            GpuHdrToSdrToneMapper.PrewarmShaders();
            GetNormalCaptureWorker().Prewarm();
        }

        public static void Shutdown()
        {
            NormalCaptureWorker worker;

            lock (normalCaptureWorkerSync)
            {
                worker = normalCaptureWorker;
                normalCaptureWorker = null;
            }

            worker?.Dispose();
            GpuHdrToSdrToneMapper.ShutdownSharedSession();
        }

        private static NormalCaptureWorker GetNormalCaptureWorker()
        {
            lock (normalCaptureWorkerSync)
            {
                return normalCaptureWorker ??= new NormalCaptureWorker();
            }
        }

        private static HdrImageDocument CaptureSingleTargetHdr(
            Rectangle captureRectangle,
            MonitorCaptureTarget target,
            CaptureContext captureContext)
        {
            HdrRgba16FloatBuffer capturedPixels = captureContext.CaptureMonitorHdr(
                target.Monitor,
                target.MonitorBounds,
                target.Intersection);

            try
            {
                HdrRgba16FloatBuffer documentPixels;
                if (capturedPixels.Width == captureRectangle.Width &&
                    capturedPixels.Height == captureRectangle.Height)
                {
                    documentPixels = capturedPixels;
                    capturedPixels = null;
                }
                else
                {
                    documentPixels = new HdrRgba16FloatBuffer(
                        captureRectangle.Width,
                        captureRectangle.Height);
                    try
                    {
                        documentPixels.CopyScaledRegionFrom(
                            capturedPixels,
                            new Rectangle(Point.Empty, new Size(capturedPixels.Width, capturedPixels.Height)),
                            new Rectangle(Point.Empty, captureRectangle.Size));
                    }
                    catch
                    {
                        documentPixels.Dispose();
                        throw;
                    }
                }

                var segment = new HdrCaptureSourceSegment(
                    new Rectangle(Point.Empty, captureRectangle.Size),
                    target.DeviceName,
                    target.IsHdrActive,
                    target.SdrWhiteNits,
                    target.MaxLuminanceNits);
                return new HdrImageDocument(captureRectangle, documentPixels, new[] { segment });
            }
            finally
            {
                capturedPixels?.Dispose();
            }
        }

        private static void Log(FormattableString message)
        {
            string formatted = "HDR capture | " + FormattableString.Invariant(message);
            DebugHelper.WriteLine(formatted);
            PerformanceLogSink?.Invoke(formatted);
        }

        private static string FormatRectangle(Rectangle rectangle)
        {
            return $"{rectangle.X},{rectangle.Y},{rectangle.Width}x{rectangle.Height}";
        }

        private static void DrawMonitorIntersection(
            Graphics graphics,
            Bitmap monitorBitmap,
            Rectangle monitorBounds,
            Rectangle intersection,
            Rectangle captureRectangle)
        {
            float sourceScaleX = monitorBitmap.Width / (float)monitorBounds.Width;
            float sourceScaleY = monitorBitmap.Height / (float)monitorBounds.Height;

            RectangleF sourceRectangle = new RectangleF(
                (intersection.X - monitorBounds.X) * sourceScaleX,
                (intersection.Y - monitorBounds.Y) * sourceScaleY,
                intersection.Width * sourceScaleX,
                intersection.Height * sourceScaleY);
            Rectangle destinationRectangle = new Rectangle(
                intersection.X - captureRectangle.X,
                intersection.Y - captureRectangle.Y,
                intersection.Width,
                intersection.Height);

            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(monitorBitmap, destinationRectangle, sourceRectangle, GraphicsUnit.Pixel);
        }

        private static Point GetCenter(Rectangle rectangle)
        {
            return new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
        }

        private static List<MonitorCaptureTarget> GetCaptureTargets(Rectangle captureRectangle)
        {
            Dictionary<IntPtr, HdrDisplayMetadata> metadataByMonitor = HdrDisplayMetadata.GetByMonitor();
            List<MonitorCaptureTarget> targets = new List<MonitorCaptureTarget>();

            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle intersection = Rectangle.Intersect(captureRectangle, screen.Bounds);

                if (intersection.Width <= 0 || intersection.Height <= 0)
                {
                    continue;
                }

                IntPtr monitor = MonitorFromPoint(GetCenter(screen.Bounds), MonitorDefaultToNearest);

                if (monitor == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Could not resolve the monitor for {screen.DeviceName}.");
                }

                HdrDisplayMetadata metadata = metadataByMonitor.TryGetValue(monitor, out HdrDisplayMetadata value)
                    ? value
                    : null;
                targets.Add(new MonitorCaptureTarget(
                    screen.DeviceName,
                    screen.Bounds,
                    intersection,
                    monitor,
                    metadata?.IsHdrActive == true,
                    metadata?.SdrWhiteNits ?? 203f,
                    metadata?.MaxLuminanceNits ?? 1000f));
            }

            return targets;
        }

        private sealed class MonitorCaptureTarget
        {
            public string DeviceName { get; }
            public Rectangle MonitorBounds { get; }
            public Rectangle Intersection { get; }
            public IntPtr Monitor { get; }
            public bool IsHdrActive { get; }
            public float SdrWhiteNits { get; }
            public float MaxLuminanceNits { get; }

            public MonitorCaptureTarget(
                string deviceName,
                Rectangle monitorBounds,
                Rectangle intersection,
                IntPtr monitor,
                bool isHdrActive,
                float sdrWhiteNits,
                float maxLuminanceNits)
            {
                DeviceName = deviceName;
                MonitorBounds = monitorBounds;
                Intersection = intersection;
                Monitor = monitor;
                IsHdrActive = isHdrActive;
                SdrWhiteNits = sdrWhiteNits;
                MaxLuminanceNits = maxLuminanceNits;
            }
        }

        internal sealed class CaptureContext : IDisposable
        {
            private readonly Dictionary<IntPtr, D3D11CaptureDevice.MonitorCaptureSession> monitorSessions =
                new Dictionary<IntPtr, D3D11CaptureDevice.MonitorCaptureSession>();
            private D3D11CaptureDevice captureDevice;
            private int ownerThreadId;
            private bool uninitializeWinRt;
            private bool disposed;

            public bool IsReusable { get; }
            public bool AllowStaleFrameFallback { get; }

            public CaptureContext(
                bool isReusable,
                bool allowStaleFrameFallback = false)
            {
                IsReusable = isReusable;
                AllowStaleFrameFallback = allowStaleFrameFallback;
            }

            public Bitmap CaptureMonitor(
                IntPtr monitor,
                HdrCaptureSettings settings,
                float sdrWhiteNits,
                float maxLuminanceNits)
            {
                EnsureInitialized();
                D3D11CaptureDevice.MonitorCaptureSession session = GetOrCreateMonitorSession(monitor);
                return session.Capture(settings, sdrWhiteNits, maxLuminanceNits);
            }

            public HdrRgba16FloatBuffer CaptureMonitorHdr(
                IntPtr monitor,
                Rectangle monitorBounds,
                Rectangle intersection)
            {
                EnsureInitialized();
                D3D11CaptureDevice.MonitorCaptureSession session = GetOrCreateMonitorSession(monitor);
                return session.CaptureHdr(monitorBounds, intersection);
            }

            public void CaptureMonitorHdrInto(
                IntPtr monitor,
                Rectangle monitorBounds,
                Rectangle intersection,
                HdrRgba16FloatBuffer destination,
                Rectangle destinationRectangle,
                float rgbScale = 1f)
            {
                EnsureInitialized();
                D3D11CaptureDevice.MonitorCaptureSession session = GetOrCreateMonitorSession(monitor);
                session.CaptureHdrInto(
                    monitorBounds,
                    intersection,
                    destination,
                    destinationRectangle,
                    rgbScale);
            }

            private D3D11CaptureDevice.MonitorCaptureSession GetOrCreateMonitorSession(IntPtr monitor)
            {
                if (!monitorSessions.TryGetValue(
                    monitor,
                    out D3D11CaptureDevice.MonitorCaptureSession session))
                {
                    session = captureDevice.CreateMonitorSession(
                        monitor,
                        AllowStaleFrameFallback);
                    monitorSessions.Add(monitor, session);
                    Log(
                        $"session=create monitor=0x{monitor.ToInt64():X} reusable={IsReusable}");
                }

                return session;
            }

            public void ResetAfterFailure()
            {
                if (!disposed)
                {
                    ReleaseOwnedResources();
                }
            }

            public void Prewarm()
            {
                EnsureInitialized();
            }

            private void EnsureInitialized()
            {
                ObjectDisposedException.ThrowIf(disposed, this);

                if (captureDevice != null)
                {
                    EnsureOwnerThread();
                    return;
                }

                ownerThreadId = Environment.CurrentManagedThreadId;
                int initializeResult = RoInitialize(1);

                if (initializeResult >= 0)
                {
                    uninitializeWinRt = true;
                }
                else if (initializeResult != RpcEChangedMode)
                {
                    Marshal.ThrowExceptionForHR(initializeResult);
                }

                try
                {
                    if (!GraphicsCaptureSession.IsSupported())
                    {
                        throw new PlatformNotSupportedException("Windows Graphics Capture is not supported.");
                    }

                    captureDevice = new D3D11CaptureDevice();
                    Log($"device=create reusable={IsReusable} thread={ownerThreadId}");
                }
                catch
                {
                    ReleaseOwnedResources();
                    throw;
                }
            }

            private void EnsureOwnerThread()
            {
                if (ownerThreadId != Environment.CurrentManagedThreadId)
                {
                    throw new InvalidOperationException(
                        "A reusable HDR capture context must be used and disposed on its owning thread.");
                }
            }

            private void ReleaseOwnedResources()
            {
                if (ownerThreadId != 0)
                {
                    EnsureOwnerThread();
                }

                foreach (D3D11CaptureDevice.MonitorCaptureSession session in monitorSessions.Values)
                {
                    session.Dispose();
                }

                monitorSessions.Clear();
                captureDevice?.Dispose();
                captureDevice = null;

                if (uninitializeWinRt)
                {
                    RoUninitialize();
                    uninitializeWinRt = false;
                }

                ownerThreadId = 0;
            }

            public void Dispose()
            {
                if (!disposed)
                {
                    ReleaseOwnedResources();
                    disposed = true;
                }
            }
        }

        private sealed class NormalCaptureWorker : IDisposable
        {
            private readonly BlockingCollection<NormalCaptureRequest> requests =
                new BlockingCollection<NormalCaptureRequest>();
            private readonly Thread thread;
            private bool disposed;

            public NormalCaptureWorker()
            {
                thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "ShareX HDR capture"
                };
                thread.Start();
            }

            public bool TryCapture(
                Rectangle captureRectangle,
                HdrCaptureSettings settings,
                out HdrImageDocument document)
            {
                using var request = new NormalCaptureRequest(
                    captureRectangle,
                    settings,
                    prewarmOnly: false);
                EnqueueAndWait(request);
                document = request.Document;
                return request.Succeeded;
            }

            public void Prewarm()
            {
                using var request = new NormalCaptureRequest(
                    default,
                    settings: null,
                    prewarmOnly: true);
                EnqueueAndWait(request);
            }

            private void EnqueueAndWait(NormalCaptureRequest request)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                requests.Add(request);
                request.Completion.Wait();

                if (request.Exception != null)
                {
                    throw new InvalidOperationException(
                        "The HDR capture worker failed.",
                        request.Exception);
                }
            }

            private void Run()
            {
                CaptureContext captureContext = null;
                long lastUseTimestamp = 0;

                try
                {
                    while (!requests.IsCompleted)
                    {
                        if (requests.TryTake(out NormalCaptureRequest request, 250))
                        {
                            try
                            {
                                captureContext ??= new CaptureContext(
                                    true,
                                    allowStaleFrameFallback: true);

                                if (request.PrewarmOnly)
                                {
                                    captureContext.Prewarm();
                                    request.Succeeded = true;
                                }
                                else
                                {
                                    HdrImageDocument document;
                                    request.Succeeded = TryCaptureHdrCore(
                                        request.CaptureRectangle,
                                        captureContext,
                                        request.Settings,
                                        out document);
                                    request.Document = document;
                                }

                                lastUseTimestamp = Stopwatch.GetTimestamp();
                            }
                            catch (Exception e)
                            {
                                request.Exception = e;
                            }
                            finally
                            {
                                request.Completion.Set();
                            }
                        }
                        else if (captureContext != null &&
                            Stopwatch.GetElapsedTime(lastUseTimestamp).TotalMilliseconds >=
                                NormalSessionIdleMilliseconds)
                        {
                            captureContext.Dispose();
                            captureContext = null;
                            Log($"session-cache=expired idleMs={NormalSessionIdleMilliseconds}");
                        }
                    }
                }
                finally
                {
                    captureContext?.Dispose();

                    while (requests.TryTake(out NormalCaptureRequest request))
                    {
                        request.Exception = new ObjectDisposedException(nameof(NormalCaptureWorker));
                        request.Completion.Set();
                    }
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                requests.CompleteAdding();
                if (thread.Join(CaptureTimeoutMilliseconds * 4))
                {
                    requests.Dispose();
                }
                else
                {
                    Log($"session-cache shutdown timed out; worker resources will be released by its background thread");
                }
            }
        }

        private sealed class NormalCaptureRequest : IDisposable
        {
            public Rectangle CaptureRectangle { get; }
            public HdrCaptureSettings Settings { get; }
            public bool PrewarmOnly { get; }
            public ManualResetEventSlim Completion { get; } = new ManualResetEventSlim();
            public HdrImageDocument Document { get; set; }
            public bool Succeeded { get; set; }
            public Exception Exception { get; set; }

            public NormalCaptureRequest(
                Rectangle captureRectangle,
                HdrCaptureSettings settings,
                bool prewarmOnly)
            {
                CaptureRectangle = captureRectangle;
                Settings = settings;
                PrewarmOnly = prewarmOnly;
            }

            public void Dispose()
            {
                Completion.Dispose();
            }
        }

        private sealed class D3D11CaptureDevice : IDisposable
        {
            private readonly ID3D11Device device;
            private readonly ID3D11DeviceContext context;
            private readonly WinRtDirect3DDevice winRtDevice;

            public D3D11CaptureDevice()
            {
                try
                {
                    D3D11CreateDevice(
                        null,
                        DriverType.Hardware,
                        DeviceCreationFlags.BgraSupport,
                        null,
                        out device,
                        out _,
                        out context).CheckError();

                    using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
                    CreateDirect3D11DeviceFromDXGIDeviceNative(dxgiDevice.NativePointer, out IntPtr winRtDevicePointer).ThrowIfFailed();

                    try
                    {
                        winRtDevice = WinRT.MarshalInterface<WinRtDirect3DDevice>.FromAbi(winRtDevicePointer);
                    }
                    finally
                    {
                        WinRT.MarshalInterface<WinRtDirect3DDevice>.DisposeAbi(winRtDevicePointer);
                    }
                }
                catch
                {
                    context?.Dispose();
                    device?.Dispose();
                    throw;
                }
            }

            public Bitmap CaptureMonitor(
                IntPtr monitor,
                HdrCaptureSettings settings,
                float sdrWhiteNits,
                float maxLuminanceNits)
            {
                using MonitorCaptureSession session = CreateMonitorSession(monitor);
                return session.Capture(settings, sdrWhiteNits, maxLuminanceNits);
            }

            public HdrRgba16FloatBuffer CaptureMonitorHdr(
                IntPtr monitor,
                Rectangle monitorBounds,
                Rectangle intersection)
            {
                using MonitorCaptureSession session = CreateMonitorSession(monitor);
                return session.CaptureHdr(monitorBounds, intersection);
            }

            public MonitorCaptureSession CreateMonitorSession(IntPtr monitor)
            {
                return CreateMonitorSession(monitor, allowStaleFrameFallback: false);
            }

            public MonitorCaptureSession CreateMonitorSession(
                IntPtr monitor,
                bool allowStaleFrameFallback)
            {
                return new MonitorCaptureSession(
                    this,
                    monitor,
                    allowStaleFrameFallback);
            }

            private Bitmap CopyAndToneMap(
                Direct3D11CaptureFrame frame,
                HdrCaptureSettings settings,
                float sdrWhiteNits,
                float maxLuminanceNits)
            {
                using ID3D11Texture2D sourceTexture = GetTexture(frame.Surface);
                Texture2DDescription sourceDescription = sourceTexture.Description;

                if (sourceDescription.Format != Format.R16G16B16A16_Float)
                {
                    throw new InvalidOperationException($"Windows Graphics Capture returned the unexpected format {sourceDescription.Format}.");
                }

                int width = Math.Min(frame.ContentSize.Width, (int)sourceDescription.Width);
                int height = Math.Min(frame.ContentSize.Height, (int)sourceDescription.Height);
                if (settings.ProcessingBackend == HdrProcessingBackend.Gpu)
                {
                    Stopwatch gpuTimer = Stopwatch.StartNew();

                    try
                    {
                        Bitmap gpuBitmap = GpuHdrToSdrToneMapper.ToneMap(
                            device,
                            context,
                            sourceTexture,
                            width,
                            height,
                            settings,
                            sdrWhiteNits,
                            maxLuminanceNits);
                        LogToneMap("GPU", width, height, maxLuminanceNits, gpuTimer.Elapsed);
                        return gpuBitmap;
                    }
                    catch (Exception e)
                    {
                        Log(
                            $"fallback=CPU reason={e.GetType().Name} hresult=0x{e.HResult:X8} gpuMs={gpuTimer.Elapsed.TotalMilliseconds:F1}");
                        DebugHelper.WriteException(e, "GPU HDR tone mapping failed. Falling back to CPU tone mapping.");
                    }
                }

                Texture2DDescription stagingDescription = new Texture2DDescription
                {
                    Width = sourceDescription.Width,
                    Height = sourceDescription.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = sourceDescription.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };

                using ID3D11Texture2D stagingTexture = device.CreateTexture2D(stagingDescription);
                Stopwatch cpuTimer = Stopwatch.StartNew();
                context.CopyResource(stagingTexture, sourceTexture);

                MappedSubresource mapped = context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    Bitmap cpuBitmap = HdrToSdrToneMapper.ToneMapRgba16Float(
                        mapped.DataPointer,
                        (int)mapped.RowPitch,
                        width,
                        height,
                        settings,
                        sdrWhiteNits,
                        maxLuminanceNits);
                    LogToneMap("CPU", width, height, maxLuminanceNits, cpuTimer.Elapsed);
                    return cpuBitmap;
                }
                finally
                {
                    context.Unmap(stagingTexture, 0);
                }
            }

            private HdrRgba16FloatBuffer CopyToHdrBuffer(
                Direct3D11CaptureFrame frame,
                Rectangle monitorBounds,
                Rectangle intersection,
                HdrRgba16FloatBuffer destination = null,
                Rectangle destinationRectangle = default,
                float rgbScale = 1f)
            {
                using ID3D11Texture2D sourceTexture = GetTexture(frame.Surface);
                Texture2DDescription sourceDescription = sourceTexture.Description;

                if (sourceDescription.Format != Format.R16G16B16A16_Float)
                {
                    throw new InvalidOperationException();
                }

                int frameWidth = Math.Min(frame.ContentSize.Width, (int)sourceDescription.Width);
                int frameHeight = Math.Min(frame.ContentSize.Height, (int)sourceDescription.Height);
                float sourceScaleX = frameWidth / (float)monitorBounds.Width;
                float sourceScaleY = frameHeight / (float)monitorBounds.Height;
                int sourceLeft = Math.Clamp(
                    (int)MathF.Floor((intersection.Left - monitorBounds.Left) * sourceScaleX),
                    0,
                    frameWidth - 1);
                int sourceTop = Math.Clamp(
                    (int)MathF.Floor((intersection.Top - monitorBounds.Top) * sourceScaleY),
                    0,
                    frameHeight - 1);
                int sourceRight = Math.Clamp(
                    (int)MathF.Ceiling((intersection.Right - monitorBounds.Left) * sourceScaleX),
                    sourceLeft + 1,
                    frameWidth);
                int sourceBottom = Math.Clamp(
                    (int)MathF.Ceiling((intersection.Bottom - monitorBounds.Top) * sourceScaleY),
                    sourceTop + 1,
                    frameHeight);
                int width = sourceRight - sourceLeft;
                int height = sourceBottom - sourceTop;
                Texture2DDescription stagingDescription = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = sourceDescription.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };

                using ID3D11Texture2D stagingTexture = device.CreateTexture2D(stagingDescription);
                Stopwatch gpuReadbackTimer = Stopwatch.StartNew();
                context.CopySubresourceRegion(
                    stagingTexture,
                    0,
                    0,
                    0,
                    0,
                    sourceTexture,
                    0,
                    new D3DBox(sourceLeft, sourceTop, 0, sourceRight, sourceBottom, 1));
                MappedSubresource mapped = context.Map(
                    stagingTexture,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None);
                gpuReadbackTimer.Stop();

                try
                {
                    Stopwatch cpuCopyTimer = Stopwatch.StartNew();
                    bool copiedIntoDestination = destination != null;
                    bool scaledIntoDestination = copiedIntoDestination &&
                        (destinationRectangle.Width != width ||
                            destinationRectangle.Height != height);
                    HdrRgba16FloatBuffer result;

                    if (copiedIntoDestination)
                    {
                        if (scaledIntoDestination)
                        {
                            destination.CopyScaledFrom(
                                mapped.DataPointer,
                                (int)mapped.RowPitch,
                                width,
                                height,
                                destinationRectangle,
                                rgbScale);
                        }
                        else
                        {
                            destination.CopyFrom(
                                mapped.DataPointer,
                                (int)mapped.RowPitch,
                                width,
                                height,
                                destinationRectangle.Location,
                                rgbScale);
                        }

                        result = null;
                    }
                    else
                    {
                        result = new HdrRgba16FloatBuffer(width, height);
                        result.CopyFrom(
                            mapped.DataPointer,
                            (int)mapped.RowPitch,
                            width,
                            height,
                            Point.Empty,
                            rgbScale);
                    }

                    cpuCopyTimer.Stop();
                    Log(
                        $"hdr-readback frame={frameWidth}x{frameHeight} crop={sourceLeft},{sourceTop},{width}x{height} destination={(copiedIntoDestination ? scaledIntoDestination ? "scaled" : "direct" : "owned")} rgbScale={rgbScale:F3} gpuWaitMs={gpuReadbackTimer.Elapsed.TotalMilliseconds:F1} cpuCopyMs={cpuCopyTimer.Elapsed.TotalMilliseconds:F1}");
                    return result;
                }
                finally
                {
                    context.Unmap(stagingTexture, 0);
                }
            }

            private static void LogToneMap(
                string backend,
                int width,
                int height,
                float displayPeakNits,
                TimeSpan elapsed)
            {
                Log(
                    $"tone-map backend={backend} size={width}x{height} displayPeak={displayPeakNits:F1}nits elapsedMs={elapsed.TotalMilliseconds:F1}");
            }

            public sealed class MonitorCaptureSession : IDisposable
            {
                private readonly D3D11CaptureDevice owner;
                private readonly bool allowStaleFrameFallback;
                private GraphicsCaptureItem item;
                private Direct3D11CaptureFramePool framePool;
                private GraphicsCaptureSession session;
                private TimeSpan? lastFrameTime;

                public MonitorCaptureSession(
                    D3D11CaptureDevice owner,
                    IntPtr monitor,
                    bool allowStaleFrameFallback)
                {
                    this.owner = owner;
                    this.allowStaleFrameFallback = allowStaleFrameFallback;

                    try
                    {
                        item = CreateItemForMonitor(monitor);
                        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                            owner.winRtDevice,
                            DirectXPixelFormat.R16G16B16A16Float,
                            1,
                            item.Size);
                        session = framePool.CreateCaptureSession(item);
                        session.IsCursorCaptureEnabled = false;

                        try
                        {
                            session.IsBorderRequired = false;
                        }
                        catch
                        {
                            // Borderless capture can require explicit OS permission.
                            // Cursor suppression remains mandatory and independent.
                        }

                        session.StartCapture();
                    }
                    catch
                    {
                        Dispose();
                        throw;
                    }
                }

                public Bitmap Capture(
                    HdrCaptureSettings settings,
                    float sdrWhiteNits,
                    float maxLuminanceNits)
                {
                    ObjectDisposedException.ThrowIf(session == null, this);
                    Stopwatch acquisitionTimer = Stopwatch.StartNew();
                    using Direct3D11CaptureFrame frame = WaitForFrame(
                        framePool,
                        lastFrameTime,
                        allowStaleFrameFallback);
                    lastFrameTime = frame.SystemRelativeTime;
                    acquisitionTimer.Stop();
                    Log(
                        $"frame size={frame.ContentSize.Width}x{frame.ContentSize.Height} acquisitionMs={acquisitionTimer.Elapsed.TotalMilliseconds:F1}");
                    return owner.CopyAndToneMap(frame, settings, sdrWhiteNits, maxLuminanceNits);
                }

                public HdrRgba16FloatBuffer CaptureHdr(
                    Rectangle monitorBounds,
                    Rectangle intersection)
                {
                    ObjectDisposedException.ThrowIf(session == null, this);
                    Stopwatch acquisitionTimer = Stopwatch.StartNew();
                    using Direct3D11CaptureFrame frame = WaitForFrame(
                        framePool,
                        lastFrameTime,
                        allowStaleFrameFallback);
                    lastFrameTime = frame.SystemRelativeTime;
                    acquisitionTimer.Stop();
                    Log(
                        $"hdr-frame size={frame.ContentSize.Width}x{frame.ContentSize.Height} acquisitionMs={acquisitionTimer.Elapsed.TotalMilliseconds:F1}");
                    return owner.CopyToHdrBuffer(frame, monitorBounds, intersection);
                }

                public void CaptureHdrInto(
                    Rectangle monitorBounds,
                    Rectangle intersection,
                    HdrRgba16FloatBuffer destination,
                    Rectangle destinationRectangle,
                    float rgbScale = 1f)
                {
                    ArgumentNullException.ThrowIfNull(destination);
                    ObjectDisposedException.ThrowIf(session == null, this);
                    Stopwatch acquisitionTimer = Stopwatch.StartNew();
                    using Direct3D11CaptureFrame frame = WaitForFrame(
                        framePool,
                        lastFrameTime,
                        allowStaleFrameFallback);
                    lastFrameTime = frame.SystemRelativeTime;
                    acquisitionTimer.Stop();
                    Log(
                        $"hdr-frame size={frame.ContentSize.Width}x{frame.ContentSize.Height} acquisitionMs={acquisitionTimer.Elapsed.TotalMilliseconds:F1}");
                    using HdrRgba16FloatBuffer scaledSource = owner.CopyToHdrBuffer(
                        frame,
                        monitorBounds,
                        intersection,
                        destination,
                        destinationRectangle,
                        rgbScale);

                    if (scaledSource != null)
                    {
                        destination.CopyScaledRegionFrom(
                            scaledSource,
                            new Rectangle(Point.Empty, new Size(scaledSource.Width, scaledSource.Height)),
                            destinationRectangle);
                    }
                }

                public void Dispose()
                {
                    session?.Dispose();
                    session = null;
                    framePool?.Dispose();
                    framePool = null;
                    DisposeWinRtObject(item);
                    item = null;
                }
            }

            public void Dispose()
            {
                DisposeWinRtObject(winRtDevice);

                context?.Dispose();
                device?.Dispose();
            }
        }

        private static Direct3D11CaptureFrame WaitForFrame(
            Direct3D11CaptureFramePool framePool,
            TimeSpan? minimumExclusiveFrameTime = null,
            bool allowStaleFrameFallback = false)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Direct3D11CaptureFrame cachedFallback = null;

            try
            {
                while (stopwatch.ElapsedMilliseconds < CaptureTimeoutMilliseconds)
                {
                    Direct3D11CaptureFrame frame = GetLatestAvailableFrame(
                        framePool,
                        minimumExclusiveFrameTime,
                        allowStaleFrameFallback,
                        ref cachedFallback);

                    if (frame != null)
                    {
                        cachedFallback?.Dispose();
                        cachedFallback = null;
                        return frame;
                    }

                    if (cachedFallback != null &&
                        stopwatch.ElapsedMilliseconds >= FreshFrameGraceMilliseconds)
                    {
                        Direct3D11CaptureFrame result = cachedFallback;
                        cachedFallback = null;
                        Log(
                            $"frame=fallback-cached freshWaitMs={stopwatch.Elapsed.TotalMilliseconds:F1}");
                        return result;
                    }

                    Thread.Sleep(5);
                }

                throw new TimeoutException("Windows Graphics Capture did not provide a frame in time.");
            }
            finally
            {
                cachedFallback?.Dispose();
            }
        }

        private static Direct3D11CaptureFrame GetLatestAvailableFrame(
            Direct3D11CaptureFramePool framePool,
            TimeSpan? minimumExclusiveFrameTime,
            bool allowStaleFrameFallback,
            ref Direct3D11CaptureFrame cachedFallback)
        {
            Direct3D11CaptureFrame latest = null;

            // A reusable session can have a queued frame from before the most
            // recent scroll. Drain the one-frame pool and keep only the newest
            // available frame, with a hard bound in case the producer races us.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Direct3D11CaptureFrame next = framePool.TryGetNextFrame();

                if (next == null)
                {
                    break;
                }

                TimeSpan? frameTime = next.SystemRelativeTime;

                if (minimumExclusiveFrameTime.HasValue &&
                    (!frameTime.HasValue || frameTime.Value <= minimumExclusiveFrameTime.Value))
                {
                    if (allowStaleFrameFallback)
                    {
                        cachedFallback?.Dispose();
                        cachedFallback = next;
                    }
                    else
                    {
                        next.Dispose();
                    }
                    continue;
                }

                latest?.Dispose();
                latest = next;
            }

            return latest;
        }

        private static unsafe ID3D11Texture2D GetTexture(WinRtDirect3DSurface surface)
        {
            IntPtr surfacePointer = WinRT.MarshalInterface<WinRtDirect3DSurface>.FromManaged(surface);
            IntPtr accessPointer = IntPtr.Zero;

            try
            {
                Guid accessGuid = Direct3DDxgiInterfaceAccessGuid;
                Marshal.QueryInterface(surfacePointer, in accessGuid, out accessPointer).ThrowIfFailed();

                IntPtr* vtable = *(IntPtr**)accessPointer;
                delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> getInterface =
                    (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
                Guid textureGuid = typeof(ID3D11Texture2D).GUID;
                IntPtr texturePointer = IntPtr.Zero;
                int result = getInterface(accessPointer, &textureGuid, &texturePointer);
                Marshal.ThrowExceptionForHR(result);

                return new ID3D11Texture2D(texturePointer);
            }
            finally
            {
                if (accessPointer != IntPtr.Zero)
                {
                    Marshal.Release(accessPointer);
                }

                WinRT.MarshalInterface<WinRtDirect3DSurface>.DisposeAbi(surfacePointer);
            }
        }

        private static unsafe GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
        {
            IntPtr className = IntPtr.Zero;
            IntPtr factory = IntPtr.Zero;
            IntPtr itemPointer = IntPtr.Zero;

            try
            {
                WindowsCreateString(GraphicsCaptureItemRuntimeClass, GraphicsCaptureItemRuntimeClass.Length, out className).ThrowIfFailed();
                RoGetActivationFactory(className, GraphicsCaptureItemInteropGuid, out factory).ThrowIfFailed();

                IntPtr* vtable = *(IntPtr**)factory;
                delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int> createForMonitor =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[4];

                Guid itemGuid = GraphicsCaptureItemGuid;
                int result = createForMonitor(factory, monitor, &itemGuid, &itemPointer);
                Marshal.ThrowExceptionForHR(result);

                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
            }
            finally
            {
                if (itemPointer != IntPtr.Zero)
                {
                    WinRT.MarshalInterface<GraphicsCaptureItem>.DisposeAbi(itemPointer);
                }

                if (factory != IntPtr.Zero)
                {
                    Marshal.Release(factory);
                }

                if (className != IntPtr.Zero)
                {
                    WindowsDeleteString(className);
                }
            }
        }

        private static void ThrowIfFailed(this int result)
        {
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }

        private static void DisposeWinRtObject(object value)
        {
            if (value is WinRT.IWinRTObject winRtObject)
            {
                winRtObject.NativeObject.Dispose();
            }
            else if (value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        [DllImport("combase.dll")]
        private static extern int RoInitialize(int initializationType);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDeviceNative(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("combase.dll")]
        private static extern void RoUninitialize();

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr value);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr value);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    }
}
