﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2025 ShareX Team

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
using ShareX.ScreenCaptureLib.AdvancedGraphics;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using Veldrid;

namespace ShareX.ScreenCaptureLib
{
    public partial class Screenshot
    {
        public bool CaptureCursor { get; set; } = false;
        public bool CaptureClientArea { get; set; } = false;
        public bool RemoveOutsideScreenArea { get; set; } = true;
        public bool CaptureShadow { get; set; } = false;
        public int ShadowOffset { get; set; } = 20;
        public bool AutoHideTaskbar { get; set; } = false;
        public bool UseWinRTCaptureAPI { get; set; } = true;
        public HdrSettings HdrSettings { get; set; } = new HdrSettings();

        public static Screenshot FromRegionCapture(RegionCaptureOptions regionCaptureOptions)
        {
            Screenshot screenshot = new Screenshot();
            screenshot.UseWinRTCaptureAPI = regionCaptureOptions.UseHdr;
            screenshot.HdrSettings = regionCaptureOptions.HdrSettings;
            return screenshot;
        }

        public Bitmap CaptureRectangle(Rectangle rect)
        {
            if (RemoveOutsideScreenArea)
            {
                Rectangle bounds = CaptureHelpers.GetScreenBounds();
                rect = Rectangle.Intersect(bounds, rect);
            }

            return CaptureRectangleNative(rect, CaptureCursor);
        }

        public Bitmap CaptureFullscreen()
        {
            Rectangle bounds = CaptureHelpers.GetScreenBounds();

            return CaptureRectangle(bounds);
        }

        public Bitmap CaptureWindow(IntPtr handle)
        {
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

                    return CaptureRectangle(rect);
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

        public Bitmap CaptureActiveMonitor()
        {
            Rectangle bounds = CaptureHelpers.GetActiveScreenBounds();

            return CaptureRectangle(bounds);
        }

        private Bitmap CaptureRectangleNative(Rectangle rect, bool captureCursor = false)
        {
            IntPtr handle = NativeMethods.GetDesktopWindow();
            return CaptureRectangleNative(handle, rect, captureCursor);
        }

        private Bitmap CaptureRectangleNative(IntPtr handle, Rectangle rect, bool captureCursor = false)
        {
            if (rect.Width == 0 || rect.Height == 0)
            {
                return null;
            }

            // TODO: some setting?
            if (UseWinRTCaptureAPI)
            {
                // TODO: only in debug?
                SharpGen.Runtime.Configuration.EnableObjectTracking = true;
                SharpGen.Runtime.Configuration.EnableReleaseOnFinalizer = true;
                return CaptureRectangleDirect3D11(handle, rect, captureCursor);
            }
            else
            {
                IntPtr hdcSrc = NativeMethods.GetWindowDC(handle);
                IntPtr hdcDest = NativeMethods.CreateCompatibleDC(hdcSrc);
                IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
                IntPtr hOld = NativeMethods.SelectObject(hdcDest, hBitmap);
                NativeMethods.BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc, rect.X, rect.Y, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

                if (captureCursor)
                {
                    try
                    {
                        CursorData cursorData = new CursorData();
                        cursorData.DrawCursor(hdcDest, rect.Location);
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, "Cursor capture failed.");
                    }
                }

                NativeMethods.SelectObject(hdcDest, hOld);
                NativeMethods.DeleteDC(hdcDest);
                NativeMethods.ReleaseDC(handle, hdcSrc);
                Bitmap bmp = Image.FromHbitmap(hBitmap);
                NativeMethods.DeleteObject(hBitmap);

                return bmp;
            }
        }

        private static ModernCapture _captureInstance;
        private static Lock _captureInstanceLock = new Lock();
        private Bitmap CaptureRectangleDirect3D11(IntPtr handle, Rectangle rect, bool captureCursor = false)
        {
            var captureMonRegions = new List<ModernCaptureMonitorDescription>();
            Bitmap bmp;

            if (rect.Width == 0 || rect.Height == 0)
            {
                return null;
            }

            // 1. Get regions and the HDR metadata information
            foreach (var monitor in MonitorEnumerationHelper.GetMonitors())
            {
                if (monitor.MonitorArea.IntersectsWith(rect))
                {
                    var screenBoundCopy = monitor.MonitorArea.Copy();
                    screenBoundCopy.Intersect(rect);
                    captureMonRegions.Add(new ModernCaptureMonitorDescription
                    {
                        DestGdiRect = screenBoundCopy,
                        MonitorInfo = monitor,
                        CaptureCursor = captureCursor,
                    });
                }
            }

            // 2. Compose a list of rects for capture
            var catpureItem = new ModernCaptureItemDescription(rect, captureMonRegions);

            // 3. Request capture and wait for bitmap
            // 3.1 Determine rects and transform them to DirectX coordinate system
            // 3.2 Capture and wait for content
            // 3.3 Shader and draw passes
            // 3.4 Datastream pass, copy
            lock (_captureInstanceLock)
            {
                if (_captureInstance == null) _captureInstance = new ModernCapture(HdrSettings);
                bmp = _captureInstance.CaptureAndProcess(HdrSettings, catpureItem);
            }

            return bmp;
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