#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    public partial class Screenshot
    {
        private static readonly object obsGameCaptureServiceSync = new object();
        private static ObsGameCaptureService obsGameCaptureService;

        public bool CaptureCursor { get; set; } = false;
        public bool CaptureClientArea { get; set; } = false;
        public bool RemoveOutsideScreenArea { get; set; } = true;
        public bool CaptureShadow { get; set; } = false;
        public int ShadowOffset { get; set; } = 20;
        public bool AutoHideTaskbar { get; set; } = false;
        public bool UseHDRSupport { get; set; } = true;
        public HdrCaptureSettings HdrSettings { get; set; } = new HdrCaptureSettings();

        public Bitmap CaptureRectangle(Rectangle rect)
        {
            return CaptureRectangle(rect, null);
        }

        public Bitmap CaptureRectangle(Rectangle rect, out HdrImageDocument document)
        {
            return CaptureRectangle(rect, null, default, out document);
        }

        public bool TryCaptureHdr(Rectangle rect, out HdrImageDocument document)
        {
            if (RemoveOutsideScreenArea)
            {
                rect = Rectangle.Intersect(CaptureHelpers.GetScreenBounds(), rect);
            }

            if (TryCaptureObsGame(rect, IntPtr.Zero, out document, out ObsGameCaptureAttempt obsAttempt))
            {
                document.NormalizeMixedMonitorBrightness(HdrSettings);

                if (ShouldCaptureObsCursor(obsAttempt.CursorMode))
                {
                    CompositeCursor(document, rect);
                }

                return true;
            }

            bool captured = WindowsGraphicsCapture.TryCaptureHdr(rect, null, out document);

            if (captured)
            {
                document.NormalizeMixedMonitorBrightness(HdrSettings);

                if (CaptureCursor)
                {
                    CompositeCursor(document, rect);
                }
            }

            return captured;
        }

        private static void CompositeCursor(HdrImageDocument document, Rectangle captureRectangle)
        {
            try
            {
                CompositeCursorCore(document, captureRectangle);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void CompositeCursorCore(HdrImageDocument document, Rectangle captureRectangle)
        {
            var cursor = new CursorData();
            if (!cursor.IsVisible)
            {
                return;
            }

            using Bitmap cursorBitmap = cursor.ToBitmap();
            Rectangle bitmapBounds = new Rectangle(Point.Empty, cursorBitmap.Size);
            BitmapData bitmapData = cursorBitmap.LockBits(
                bitmapBounds,
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                CompositeCursorPixels(document, captureRectangle, cursor, cursorBitmap, bitmapData);
            }
            finally
            {
                cursorBitmap.UnlockBits(bitmapData);
            }
        }

        private static void CompositeCursorPixels(
            HdrImageDocument document,
            Rectangle captureRectangle,
            CursorData cursor,
            Bitmap cursorBitmap,
            BitmapData bitmapData)
        {
            int destinationX = cursor.DrawPosition.X - captureRectangle.X;
            int destinationY = cursor.DrawPosition.Y - captureRectangle.Y;
            var cursorBounds = new Rectangle(destinationX, destinationY, cursorBitmap.Width, cursorBitmap.Height);
            var documentBounds = new Rectangle(
                0,
                0,
                document.MasterPixels.Width,
                document.MasterPixels.Height);

            if (!cursorBounds.IntersectsWith(documentBounds))
            {
                return;
            }

            int packedRowBytes = checked(cursorBitmap.Width * 4);
            byte[] pixels = new byte[checked(packedRowBytes * cursorBitmap.Height)];

            for (int y = 0; y < cursorBitmap.Height; y++)
            {
                IntPtr sourceRow = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                Marshal.Copy(sourceRow, pixels, y * packedRowBytes, packedRowBytes);
            }

            Point hotspotInDocument = new Point(
                cursor.Position.X - captureRectangle.X,
                cursor.Position.Y - captureRectangle.Y);
            float cursorWhiteNits = 203f;

            foreach (HdrCaptureSourceSegment segment in document.SourceSegments)
            {
                if (segment.DestinationRectangle.Contains(hotspotInDocument))
                {
                    cursorWhiteNits = segment.SdrWhiteNits;
                    break;
                }
            }

            document.CompositeSdrAsset(
                pixels,
                packedRowBytes,
                cursorBitmap.Width,
                cursorBitmap.Height,
                destinationX,
                destinationY,
                cursorWhiteNits);
        }

        private Bitmap CaptureRectangle(
            Rectangle rect,
            WindowsGraphicsCapture.CaptureContext captureContext,
            IntPtr preferredWindow = default)
        {
            Bitmap bitmap = CaptureRectangle(
                rect,
                captureContext,
                captureScope: null,
                preferredWindow,
                out HdrImageDocument document);
            document?.Dispose();
            return bitmap;
        }

        private Bitmap CaptureRectangle(
            Rectangle rect,
            WindowsGraphicsCapture.CaptureContext captureContext,
            ObsGameCaptureCaptureScope captureScope,
            IntPtr preferredWindow = default)
        {
            Bitmap bitmap = CaptureRectangle(
                rect,
                captureContext,
                captureScope,
                preferredWindow,
                out HdrImageDocument document);
            document?.Dispose();
            return bitmap;
        }

        private Bitmap CaptureRectangle(
            Rectangle rect,
            WindowsGraphicsCapture.CaptureContext captureContext,
            IntPtr preferredWindow,
            out HdrImageDocument document)
        {
            return CaptureRectangle(
                rect,
                captureContext,
                captureScope: null,
                preferredWindow,
                out document);
        }

        private Bitmap CaptureRectangle(
            Rectangle rect,
            WindowsGraphicsCapture.CaptureContext captureContext,
            ObsGameCaptureCaptureScope captureScope,
            IntPtr preferredWindow,
            out HdrImageDocument document)
        {
            document = null;

            if (RemoveOutsideScreenArea)
            {
                Rectangle bounds = CaptureHelpers.GetScreenBounds();
                rect = Rectangle.Intersect(bounds, rect);
            }

            if (TryCaptureObsGame(
                rect,
                preferredWindow,
                captureScope,
                out HdrImageDocument obsDocument,
                out ObsGameCaptureAttempt obsAttempt))
            {
                return CreateSdrPreviewAndRetain(
                    obsDocument,
                    rect,
                    ShouldCaptureObsCursor(obsAttempt.CursorMode),
                    out document);
            }

            if (UseHDRSupport &&
                WindowsGraphicsCapture.TryCaptureHdr(rect, captureContext, out HdrImageDocument hdrDocument))
            {
                return CreateSdrPreviewAndRetain(hdrDocument, rect, CaptureCursor, out document);
            }

            return CaptureRectangleNative(rect, CaptureCursor);
        }

        private Bitmap CreateSdrPreviewAndRetain(
            HdrImageDocument capturedDocument,
            Rectangle captureRectangle,
            bool captureCursor,
            out HdrImageDocument document)
        {
            document = null;
            Stopwatch totalTimer = Stopwatch.StartNew();

            try
            {
                Stopwatch normalizeTimer = Stopwatch.StartNew();
                capturedDocument.NormalizeMixedMonitorBrightness(HdrSettings);
                normalizeTimer.Stop();

                Stopwatch windowsTimer = Stopwatch.StartNew();
                if (HdrSettings.ToneMappingMode != HdrToneMappingMode.Uniform)
                {
                    capturedDocument.CaptureWindowRegions(HdrSettings);
                }
                windowsTimer.Stop();

                Stopwatch cursorTimer = Stopwatch.StartNew();
                if (captureCursor)
                {
                    CompositeCursor(capturedDocument, captureRectangle);
                }
                cursorTimer.Stop();

                Stopwatch previewTimer = Stopwatch.StartNew();
                Bitmap preview = capturedDocument.CreateSdrPreview(HdrSettings);
                previewTimer.Stop();
                document = capturedDocument;
                DebugHelper.WriteLine(
                    $"HDR preview pipeline | size={captureRectangle.Width}x{captureRectangle.Height} " +
                    $"normalizeMs={normalizeTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"windowsMs={windowsTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"cursorMs={cursorTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"previewMs={previewTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                return preview;
            }
            catch
            {
                capturedDocument.Dispose();
                throw;
            }
        }

        private bool TryCaptureObsGame(
            Rectangle rect,
            IntPtr preferredWindow,
            out HdrImageDocument document,
            out ObsGameCaptureAttempt attempt)
        {
            return TryCaptureObsGame(
                rect,
                preferredWindow,
                captureScope: null,
                out document,
                out attempt);
        }

        private bool TryCaptureObsGame(
            Rectangle rect,
            IntPtr preferredWindow,
            ObsGameCaptureCaptureScope captureScope,
            out HdrImageDocument document,
            out ObsGameCaptureAttempt attempt)
        {
            document = null;
            attempt = null;

            if (!UseHDRSupport || HdrSettings?.ObsGameCapture?.Enabled != true)
            {
                return false;
            }

            ObsGameCaptureService service = GetObsGameCaptureService();
            bool captured = service.TryCaptureScoped(
                rect,
                HdrSettings,
                preferredWindow,
                captureScope,
                out document,
                out attempt);
            DebugHelper.WriteLine(
                $"OBS Game Capture: success={attempt.Succeeded} process={attempt.ProcessName} " +
                $"pid={attempt.ProcessId} source={attempt.SessionSource} format={attempt.DxgiFormat} " +
                $"color={attempt.ColorInterpretation} alpha={attempt.AlphaMode} " +
                $"overlays={attempt.CaptureThirdPartyOverlays} rate={attempt.CaptureFrameRate} " +
                $"cursor={attempt.CursorMode} retained={attempt.OwnedSessionRetained} " +
                $"elapsed={attempt.Duration.TotalMilliseconds:F1}ms " +
                $"message={attempt.Message}");
            return captured;
        }

        private bool ShouldCaptureObsCursor(ObsGameCaptureCursorMode cursorMode)
        {
            return cursorMode switch
            {
                ObsGameCaptureCursorMode.Include => true,
                ObsGameCaptureCursorMode.Exclude => false,
                _ => CaptureCursor
            };
        }

        private static ObsGameCaptureService GetObsGameCaptureService()
        {
            lock (obsGameCaptureServiceSync)
            {
                return obsGameCaptureService ??= new ObsGameCaptureService();
            }
        }

        public static void ShutdownObsGameCapture()
        {
            ObsGameCaptureService service;

            lock (obsGameCaptureServiceSync)
            {
                service = obsGameCaptureService;
                obsGameCaptureService = null;
            }

            service?.Dispose();
        }
        internal CaptureSession CreateCaptureSession()
        {
            return new CaptureSession(this);
        }

        internal sealed class CaptureSession : IDisposable
        {
            private Screenshot screenshot;
            private WindowsGraphicsCapture.CaptureContext captureContext;
            private ObsGameCaptureCaptureScope obsGameCaptureScope;

            public CaptureSession(Screenshot screenshot)
            {
                this.screenshot = screenshot ?? throw new ArgumentNullException(nameof(screenshot));
                captureContext = new WindowsGraphicsCapture.CaptureContext(true);

                if (screenshot.UseHDRSupport && screenshot.HdrSettings?.ObsGameCapture?.Enabled == true)
                {
                    obsGameCaptureScope = GetObsGameCaptureService().CreateCaptureScope();
                }
            }

            public Bitmap CaptureRectangle(Rectangle rect)
            {
                ObjectDisposedException.ThrowIf(screenshot == null, this);
                return screenshot.CaptureRectangle(rect, captureContext, obsGameCaptureScope);
            }

            public void Dispose()
            {
                obsGameCaptureScope?.Dispose();
                obsGameCaptureScope = null;
                captureContext?.Dispose();
                captureContext = null;
                screenshot = null;
            }
        }

        public Bitmap CaptureFullscreen()
        {
            Rectangle bounds = CaptureHelpers.GetScreenBounds();

            return CaptureRectangle(bounds);
        }

        public Bitmap CaptureFullscreen(out HdrImageDocument document)
        {
            Rectangle bounds = CaptureHelpers.GetScreenBounds();

            return CaptureRectangle(bounds, out document);
        }

        public Bitmap CaptureWindow(IntPtr handle)
        {
            Bitmap bitmap = CaptureWindow(handle, out HdrImageDocument document);
            document?.Dispose();
            return bitmap;
        }

        public Bitmap CaptureWindow(IntPtr handle, out HdrImageDocument document)
        {
            document = null;

            if (handle.ToInt32() > 0)
            {
                Rectangle rect;

                if (CaptureClientArea)
                {
                    rect = NativeMethods.GetClientRect(handle);
                }
                else
                {
                    rect = CaptureHelpers.GetWindowRectangle(handle);
                }

                bool isTaskbarHide = false;

                try
                {
                    if (AutoHideTaskbar)
                    {
                        isTaskbarHide = NativeMethods.SetTaskbarVisibilityIfIntersect(false, rect);
                    }

                    return CaptureRectangle(rect, null, handle, out document);
                }
                finally
                {
                    if (isTaskbarHide)
                    {
                        NativeMethods.SetTaskbarVisibility(true);
                    }
                }
            }

            return null;
        }

        public Bitmap CaptureActiveWindow()
        {
            IntPtr handle = NativeMethods.GetForegroundWindow();

            return CaptureWindow(handle);
        }

        public Bitmap CaptureActiveWindow(out HdrImageDocument document)
        {
            IntPtr handle = NativeMethods.GetForegroundWindow();

            return CaptureWindow(handle, out document);
        }

        public Bitmap CaptureActiveMonitor()
        {
            Rectangle bounds = CaptureHelpers.GetActiveScreenBounds();

            return CaptureRectangle(bounds);
        }

        public Bitmap CaptureActiveMonitor(out HdrImageDocument document)
        {
            Rectangle bounds = CaptureHelpers.GetActiveScreenBounds();

            return CaptureRectangle(bounds, out document);
        }

        internal static Bitmap CaptureRectangleNative(Rectangle rect, bool captureCursor = false)
        {
            IntPtr handle = NativeMethods.GetDesktopWindow();
            return CaptureRectangleNative(handle, rect, captureCursor);
        }

        private static Bitmap CaptureRectangleNative(IntPtr handle, Rectangle rect, bool captureCursor = false)
        {
            if (rect.Width == 0 || rect.Height == 0)
            {
                return null;
            }

            IntPtr hdcSrc = NativeMethods.GetWindowDC(handle);
            IntPtr hdcDest = NativeMethods.CreateCompatibleDC(hdcSrc);
            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
            IntPtr hOld = NativeMethods.SelectObject(hdcDest, hBitmap);
            NativeMethods.BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc, rect.X, rect.Y, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

            if (captureCursor)
            {
                DrawCursor(hdcDest, rect.Location);
            }

            NativeMethods.SelectObject(hdcDest, hOld);
            NativeMethods.DeleteDC(hdcDest);
            NativeMethods.ReleaseDC(handle, hdcSrc);
            Bitmap bmp = Image.FromHbitmap(hBitmap);
            NativeMethods.DeleteObject(hBitmap);

            return bmp;
        }

        private static void DrawCursor(IntPtr destinationHdc, Point captureOrigin)
        {
            try
            {
                CursorData cursorData = new CursorData();
                cursorData.DrawCursor(destinationHdc, captureOrigin);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Cursor capture failed.");
            }
        }

        private Bitmap CaptureRectangleManaged(Rectangle rect)
        {
            if (rect.Width == 0 || rect.Height == 0)
            {
                return null;
            }

            Bitmap bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Managed can't use SourceCopy | CaptureBlt because of .NET bug
                g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
            }

            return bmp;
        }
    }
}
