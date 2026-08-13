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
using static Vortice.Direct3D11.D3D11;

namespace ShareX.ScreenCaptureLib
{
    internal static class WindowsGraphicsCapture
    {
        private const int CaptureTimeoutMilliseconds = 1500;
        private const int RpcEChangedMode = unchecked((int)0x80010106);
        private const uint MonitorDefaultToNearest = 2;

        private static readonly Guid GraphicsCaptureItemInteropGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid GraphicsCaptureItemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid Direct3DDxgiInterfaceAccessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private const string GraphicsCaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

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
                    $"start bounds={FormatRectangle(captureRectangle)} backend={settings.ProcessingBackend} mode={settings.ToneMappingMode} peakMode={settings.PeakBrightnessMode} configuredPeak={settings.HdrBrightnessNits:F1}nits reusable={captureContext?.IsReusable == true}");

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
            document = null;
            bool ownsCaptureContext = captureContext == null;

            if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
            {
                return false;
            }

            try
            {
                List<MonitorCaptureTarget> targets = GetCaptureTargets(captureRectangle);

                if (!targets.Exists(target => target.IsHdrActive))
                {
                    return false;
                }

                captureContext ??= new CaptureContext(false);
                document = CaptureHdr(captureRectangle, targets, captureContext);
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

                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.Clear(Color.Black);

                    foreach (MonitorCaptureTarget target in targets)
                    {
                        if (target.IsHdrActive)
                        {
                            using Bitmap monitorBitmap = captureContext.CaptureMonitor(
                                target.Monitor,
                                settings,
                                target.SdrWhiteNits,
                                target.MaxLuminanceNits);
                            DrawMonitorIntersection(
                                graphics,
                                monitorBitmap,
                                target.MonitorBounds,
                                target.Intersection,
                                captureRectangle);
                        }
                        else
                        {
                            // Do not tone-map SDR monitor pixels in a mixed-monitor
                            // capture. Capture those regions with the regular GDI path.
                            using Bitmap sdrBitmap = Screenshot.CaptureRectangleNative(target.Intersection);
                            graphics.DrawImageUnscaled(
                                sdrBitmap,
                                target.Intersection.X - captureRectangle.X,
                                target.Intersection.Y - captureRectangle.Y);
                        }

                        capturedAnyMonitor = true;
                    }
                }

                if (!capturedAnyMonitor)
                {
                    canvas.Dispose();
                    return null;
                }

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
            CaptureContext captureContext)
        {
            var canvas = new HdrRgba16FloatBuffer(captureRectangle.Width, captureRectangle.Height);
            var segments = new List<HdrCaptureSourceSegment>(targets.Count);

            try
            {
                foreach (MonitorCaptureTarget target in targets)
                {
                    using HdrRgba16FloatBuffer monitorPixels = captureContext.CaptureMonitorHdr(target.Monitor);
                    float sourceScaleX = monitorPixels.Width / (float)target.MonitorBounds.Width;
                    float sourceScaleY = monitorPixels.Height / (float)target.MonitorBounds.Height;

                    int sourceLeft = Math.Clamp(
                        (int)MathF.Floor((target.Intersection.Left - target.MonitorBounds.Left) * sourceScaleX),
                        0,
                        monitorPixels.Width - 1);
                    int sourceTop = Math.Clamp(
                        (int)MathF.Floor((target.Intersection.Top - target.MonitorBounds.Top) * sourceScaleY),
                        0,
                        monitorPixels.Height - 1);
                    int sourceRight = Math.Clamp(
                        (int)MathF.Ceiling((target.Intersection.Right - target.MonitorBounds.Left) * sourceScaleX),
                        sourceLeft + 1,
                        monitorPixels.Width);
                    int sourceBottom = Math.Clamp(
                        (int)MathF.Ceiling((target.Intersection.Bottom - target.MonitorBounds.Top) * sourceScaleY),
                        sourceTop + 1,
                        monitorPixels.Height);

                    var sourceRectangle = Rectangle.FromLTRB(sourceLeft, sourceTop, sourceRight, sourceBottom);
                    var destinationRectangle = new Rectangle(
                        target.Intersection.X - captureRectangle.X,
                        target.Intersection.Y - captureRectangle.Y,
                        target.Intersection.Width,
                        target.Intersection.Height);

                    canvas.CopyScaledRegionFrom(monitorPixels, sourceRectangle, destinationRectangle);
                    segments.Add(new HdrCaptureSourceSegment(
                        destinationRectangle,
                        target.DeviceName,
                        target.IsHdrActive,
                        target.SdrWhiteNits,
                        target.MaxLuminanceNits));
                }

                return new HdrImageDocument(captureRectangle, canvas, segments);
            }
            catch
            {
                canvas.Dispose();
                throw;
            }
        }

        private static void Log(FormattableString message)
        {
            DebugHelper.WriteLine("HDR capture | " + FormattableString.Invariant(message));
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

            public CaptureContext(bool isReusable)
            {
                IsReusable = isReusable;
            }

            public Bitmap CaptureMonitor(
                IntPtr monitor,
                HdrCaptureSettings settings,
                float sdrWhiteNits,
                float maxLuminanceNits)
            {
                EnsureInitialized();

                if (!IsReusable)
                {
                    return captureDevice.CaptureMonitor(
                        monitor,
                        settings,
                        sdrWhiteNits,
                        maxLuminanceNits);
                }

                if (!monitorSessions.TryGetValue(monitor, out D3D11CaptureDevice.MonitorCaptureSession session))
                {
                    session = captureDevice.CreateMonitorSession(monitor);
                    monitorSessions.Add(monitor, session);
                    Log($"session=create monitor=0x{monitor.ToInt64():X} reusable=true");
                }

                return session.Capture(settings, sdrWhiteNits, maxLuminanceNits);
            }

            public HdrRgba16FloatBuffer CaptureMonitorHdr(IntPtr monitor)
            {
                EnsureInitialized();

                if (!IsReusable)
                {
                    return captureDevice.CaptureMonitorHdr(monitor);
                }

                if (!monitorSessions.TryGetValue(monitor, out D3D11CaptureDevice.MonitorCaptureSession session))
                {
                    session = captureDevice.CreateMonitorSession(monitor);
                    monitorSessions.Add(monitor, session);
                }

                return session.CaptureHdr();
            }

            public void ResetAfterFailure()
            {
                if (!disposed)
                {
                    ReleaseOwnedResources();
                }
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

            public HdrRgba16FloatBuffer CaptureMonitorHdr(IntPtr monitor)
            {
                using MonitorCaptureSession session = CreateMonitorSession(monitor);
                return session.CaptureHdr();
            }

            public MonitorCaptureSession CreateMonitorSession(IntPtr monitor)
            {
                return new MonitorCaptureSession(this, monitor);
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
                HdrToSdrToneMapper.ToneMapParameters parameters =
                    HdrToSdrToneMapper.CreateToneMapParameters(
                        settings.HdrBrightnessNits,
                        settings.PeakBrightnessMode,
                        settings.ToneMappingMode,
                        sdrWhiteNits,
                        maxLuminanceNits);

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
                        LogToneMap("GPU", width, height, parameters, maxLuminanceNits, gpuTimer.Elapsed);
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
                    LogToneMap("CPU", width, height, parameters, maxLuminanceNits, cpuTimer.Elapsed);
                    return cpuBitmap;
                }
                finally
                {
                    context.Unmap(stagingTexture, 0);
                }
            }

            private HdrRgba16FloatBuffer CopyToHdrBuffer(Direct3D11CaptureFrame frame)
            {
                using ID3D11Texture2D sourceTexture = GetTexture(frame.Surface);
                Texture2DDescription sourceDescription = sourceTexture.Description;

                if (sourceDescription.Format != Format.R16G16B16A16_Float)
                {
                    throw new InvalidOperationException();
                }

                int width = Math.Min(frame.ContentSize.Width, (int)sourceDescription.Width);
                int height = Math.Min(frame.ContentSize.Height, (int)sourceDescription.Height);
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
                context.CopyResource(stagingTexture, sourceTexture);
                MappedSubresource mapped = context.Map(
                    stagingTexture,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None);

                try
                {
                    return HdrRgba16FloatBuffer.CopyFrom(
                        mapped.DataPointer,
                        (int)mapped.RowPitch,
                        width,
                        height);
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
                HdrToSdrToneMapper.ToneMapParameters parameters,
                float displayPeakNits,
                TimeSpan elapsed)
            {
                Log(
                    $"tone-map backend={backend} size={width}x{height} paperWhite={parameters.PaperWhiteNits:F1}nits sourcePeak={parameters.SourcePeakNits:F1}nits displayPeak={displayPeakNits:F1}nits elapsedMs={elapsed.TotalMilliseconds:F1}");
            }

            public sealed class MonitorCaptureSession : IDisposable
            {
                private readonly D3D11CaptureDevice owner;
                private GraphicsCaptureItem item;
                private Direct3D11CaptureFramePool framePool;
                private GraphicsCaptureSession session;
                private TimeSpan? lastFrameTime;

                public MonitorCaptureSession(D3D11CaptureDevice owner, IntPtr monitor)
                {
                    this.owner = owner;

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
                    using Direct3D11CaptureFrame frame = WaitForFrame(framePool, lastFrameTime);
                    lastFrameTime = frame.SystemRelativeTime;
                    acquisitionTimer.Stop();
                    Log(
                        $"frame size={frame.ContentSize.Width}x{frame.ContentSize.Height} acquisitionMs={acquisitionTimer.Elapsed.TotalMilliseconds:F1}");
                    return owner.CopyAndToneMap(frame, settings, sdrWhiteNits, maxLuminanceNits);
                }

                public HdrRgba16FloatBuffer CaptureHdr()
                {
                    ObjectDisposedException.ThrowIf(session == null, this);
                    using Direct3D11CaptureFrame frame = WaitForFrame(framePool, lastFrameTime);
                    lastFrameTime = frame.SystemRelativeTime;
                    return owner.CopyToHdrBuffer(frame);
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
            TimeSpan? minimumExclusiveFrameTime = null)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < CaptureTimeoutMilliseconds)
            {
                Direct3D11CaptureFrame frame = GetLatestAvailableFrame(framePool, minimumExclusiveFrameTime);

                if (frame != null)
                {
                    return frame;
                }

                Thread.Sleep(5);
            }

            throw new TimeoutException("Windows Graphics Capture did not provide a frame in time.");
        }

        private static Direct3D11CaptureFrame GetLatestAvailableFrame(
            Direct3D11CaptureFramePool framePool,
            TimeSpan? minimumExclusiveFrameTime)
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
                    next.Dispose();
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