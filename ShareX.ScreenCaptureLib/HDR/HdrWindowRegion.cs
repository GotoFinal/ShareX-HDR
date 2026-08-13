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
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib
{
    internal enum HdrWindowPromotionKind
    {
        Precise,
        ForceHdr,
        DetectFullscreenHdr
    }

    /// <summary>
    /// A top-level window rectangle in capture-local coordinates. Entries are
    /// stored in front-to-back z-order, matching EnumWindows enumeration.
    /// </summary>
    internal readonly struct HdrWindowRegion
    {
        public Rectangle Bounds { get; }
        public Rectangle ContentBounds { get; }
        public long WindowHandle { get; }
        public int ProcessId { get; }
        public long ProcessStartTimeUtcTicks { get; }
        public HdrWindowPromotionKind PromotionKind { get; }

        public bool HasStableIdentity =>
            WindowHandle != 0 && ProcessId > 0 && ProcessStartTimeUtcTicks > 0;

        public HdrWindowRegion(Rectangle bounds)
            : this(bounds, bounds, 0, 0, 0, HdrWindowPromotionKind.Precise)
        {
        }

        internal HdrWindowRegion(
            Rectangle bounds,
            Rectangle contentBounds,
            long windowHandle,
            int processId,
            long processStartTimeUtcTicks,
            HdrWindowPromotionKind promotionKind)
        {
            Bounds = bounds;
            ContentBounds = contentBounds;
            WindowHandle = windowHandle;
            ProcessId = processId;
            ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
            PromotionKind = Enum.IsDefined(promotionKind)
                ? promotionKind
                : HdrWindowPromotionKind.Precise;
        }

        internal HdrWindowRegion WithBounds(Rectangle bounds, Rectangle contentBounds)
        {
            return new HdrWindowRegion(
                bounds,
                contentBounds,
                WindowHandle,
                ProcessId,
                ProcessStartTimeUtcTicks,
                PromotionKind);
        }

        public static IReadOnlyList<HdrWindowRegion> Capture(
            Rectangle captureBounds,
            HdrCaptureSettings settings = null)
        {
            var result = new List<HdrWindowRegion>();

            if (captureBounds.Width <= 0 || captureBounds.Height <= 0)
            {
                return result;
            }

            try
            {
                bool AddWindow(IntPtr handle, IntPtr _)
                {
                    try
                    {
                        var window = new WindowInfo(handle);
                        if (!window.IsVisible || window.IsCloaked || window.IsMinimized)
                        {
                            return true;
                        }

                        // The desktop hosts are the uncovered background, not
                        // ordinary occluding windows. The taskbar and desktop
                        // application windows remain in the snapshot.
                        string className = window.ClassName;
                        if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                            string.Equals(className, "WorkerW", StringComparison.Ordinal))
                        {
                            return true;
                        }

                        Rectangle windowBounds = CaptureHelpers.GetWindowRectangle(handle);
                        Rectangle intersection = Rectangle.Intersect(windowBounds, captureBounds);
                        if (intersection.Width <= 0 || intersection.Height <= 0)
                        {
                            return true;
                        }

                        intersection.Offset(-captureBounds.X, -captureBounds.Y);
                        Rectangle clientIntersection = Rectangle.Intersect(
                            window.ClientRectangle,
                            captureBounds);
                        int processId = 0;
                        long processStartTimeUtcTicks = 0;
                        string processName = string.Empty;

                        try
                        {
                            using Process process = window.Process;
                            if (process != null)
                            {
                                processId = process.Id;
                                processName = process.ProcessName;
                                processStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;

                            }
                        }
                        catch
                        {
                        }

                        HdrWindowPromotionKind promotionKind = ResolvePromotionKind(
                            handle,
                            window,
                            processId,
                            processName,
                            settings);

                        if (promotionKind != HdrWindowPromotionKind.Precise)
                        {
                            DebugHelper.WriteLine(
                                $"HDR window classification | process={processName} pid={processId} " +
                                $"hwnd=0x{handle.ToInt64():X} promotion={promotionKind}");
                        }
                        Rectangle contentIntersection = promotionKind == HdrWindowPromotionKind.Precise
                            ? Rectangle.Intersect(windowBounds, captureBounds)
                            : clientIntersection;

                        if (contentIntersection.Width > 0 && contentIntersection.Height > 0)
                        {
                            contentIntersection.Offset(-captureBounds.X, -captureBounds.Y);
                        }
                        else
                        {
                            contentIntersection = Rectangle.Empty;
                        }

                        result.Add(new HdrWindowRegion(
                            intersection,
                            contentIntersection,
                            handle.ToInt64(),
                            processId,
                            processStartTimeUtcTicks,
                            promotionKind));
                    }
                    catch
                    {
                    }

                    return true;
                }

                NativeMethods.EnumWindows(AddWindow, IntPtr.Zero);
            }
            catch
            {
            }

            return result;
        }

        private static HdrWindowPromotionKind ResolvePromotionKind(
            IntPtr handle,
            WindowInfo window,
            int processId,
            string processName,
            HdrCaptureSettings settings)
        {
            HdrGameWindowPromotionMode mode = settings?.GameWindowPromotionMode ??
                HdrGameWindowPromotionMode.AutomaticFullscreenHdr;

            if (mode == HdrGameWindowPromotionMode.Disabled)
            {
                return HdrWindowPromotionKind.Precise;
            }

            ObsGameCaptureSettings gameCapture = settings?.ObsGameCapture;
            if (gameCapture?.MatchesWindow(
                processName,
                null,
                window.Text,
                window.ClassName) == true)
            {
                return HdrWindowPromotionKind.ForceHdr;
            }

            if (mode == HdrGameWindowPromotionMode.AutomaticFullscreenHdr &&
                processId != Environment.ProcessId &&
                NativeMethods.IsActive(handle) &&
                IsNearFullscreen(window.ClientRectangle, Screen.FromHandle(handle).Bounds))
            {
                return HdrWindowPromotionKind.DetectFullscreenHdr;
            }

            return HdrWindowPromotionKind.Precise;
        }

        private static bool IsNearFullscreen(Rectangle clientRectangle, Rectangle screenBounds)
        {
            if (clientRectangle.Width <= 0 || clientRectangle.Height <= 0 ||
                screenBounds.Width <= 0 || screenBounds.Height <= 0)
            {
                return false;
            }

            Rectangle intersection = Rectangle.Intersect(clientRectangle, screenBounds);
            double coverage = (double)intersection.Width * intersection.Height /
                ((double)screenBounds.Width * screenBounds.Height);
            return clientRectangle.Width >= screenBounds.Width * 0.9 &&
                clientRectangle.Height >= screenBounds.Height * 0.9 &&
                coverage >= 0.8;
        }
    }
}
