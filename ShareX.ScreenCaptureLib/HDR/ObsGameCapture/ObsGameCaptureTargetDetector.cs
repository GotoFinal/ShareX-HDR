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
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    internal sealed record ObsGameCaptureTarget(
        IntPtr Window,
        uint ProcessId,
        uint ThreadId,
        long ProcessStartTimeUtcTicks,
        string ProcessName,
        string ProcessPath,
        string WindowTitle,
        string WindowClass,
        ObsBinaryArchitecture Architecture,
        Rectangle WindowBounds,
        IntPtr Monitor,
        bool IsForeground);

    internal static class ObsGameCaptureTargetDetector
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint GaRoot = 2;
        private const uint MonitorDefaultToNearest = 2;
        private const ushort ImageFileMachineUnknown = 0x0000;
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileMachineArm64 = 0xaa64;

        private static readonly string[] DeniedProcessNames =
        {
            "sharex",
            "obs32",
            "obs64",
            "inject-helper32",
            "inject-helper64",
            "get-graphics-offsets32",
            "get-graphics-offsets64",
            "explorer",
            "dwm"
        };

        public static bool TryFindTarget(
            Rectangle requestedBounds,
            ObsGameCaptureSettings settings,
            IntPtr preferredWindow,
            out ObsGameCaptureTarget target,
            out string reason)
        {
            target = null;

            if (settings?.Enabled != true)
            {
                reason = "OBS Game Capture is disabled.";
                return false;
            }

            if (settings.ProcessNames.Count == 0)
            {
                reason = "No OBS Game Capture process names are configured.";
                return false;
            }

            if (requestedBounds.Width <= 0 || requestedBounds.Height <= 0)
            {
                reason = "The screenshot bounds are empty.";
                return false;
            }

            IntPtr foregroundWindow = GetAncestor(GetForegroundWindow(), GaRoot);
            IntPtr preferredRootWindow = GetAncestor(preferredWindow, GaRoot);
            var handles = new List<IntPtr>();
            AddUnique(handles, preferredRootWindow);

            // A selected-window capture must never substitute an unrelated
            // configured game that happens to overlap it on the desktop.
            if (preferredRootWindow == IntPtr.Zero)
            {
                AddUnique(handles, foregroundWindow);

                foreach (WindowInfo window in new WindowsList().GetVisibleWindowsList())
                {
                    AddUnique(handles, GetAncestor(window.Handle, GaRoot));
                }
            }

            var candidates = new List<ObsGameCaptureTarget>();

            foreach (IntPtr window in handles)
            {
                if (TryCreateTarget(window, foregroundWindow, requestedBounds, settings, out ObsGameCaptureTarget candidate))
                {
                    candidates.Add(candidate);
                }
            }

            target = candidates
                .OrderByDescending(x => x.Window == preferredRootWindow)
                .ThenByDescending(x => x.IsForeground)
                .ThenByDescending(x =>
                {
                    Rectangle intersection = Rectangle.Intersect(x.WindowBounds, requestedBounds);
                    return (long)intersection.Width * intersection.Height;
                })
                .ThenBy(x => (long)x.WindowBounds.Width * x.WindowBounds.Height)
                .FirstOrDefault();

            if (target == null)
            {
                reason = "No visible configured game window intersects the requested screenshot area.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryCreateTarget(
            IntPtr window,
            IntPtr foregroundWindow,
            Rectangle requestedBounds,
            ObsGameCaptureSettings settings,
            out ObsGameCaptureTarget target)
        {
            target = null;

            if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window) ||
                NativeMethods.IsIconic(window) || NativeMethods.IsWindowCloaked(window))
            {
                return false;
            }

            // The hook publishes the render surface, not the non-client frame.
            // Pixels outside this client rectangle are composed from WGC/GDI.
            Rectangle windowBounds = NativeMethods.GetClientRect(window);

            if (windowBounds.Width <= 0 || windowBounds.Height <= 0 || !windowBounds.IntersectsWith(requestedBounds))
            {
                return false;
            }

            uint threadId = NativeMethods.GetWindowThreadProcessId(window, out uint processId);

            if (threadId == 0 || processId == 0 || processId == Environment.ProcessId)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(checked((int)processId));
                string processName = process.ProcessName;

                if (DeniedProcessNames.Any(x => x.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                string processPath = TryGetProcessPath(process);
                string windowTitle = NativeMethods.GetWindowText(window);
                string windowClass = NativeMethods.GetClassName(window);

                if (!settings.MatchesWindow(processName, processPath, windowTitle, windowClass))
                {
                    return false;
                }

                ObsBinaryArchitecture architecture = GetProcessArchitecture(processId, processPath);

                if (architecture != ObsBinaryArchitecture.X86 && architecture != ObsBinaryArchitecture.X64)
                {
                    return false;
                }

                long startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                target = new ObsGameCaptureTarget(
                    window,
                    processId,
                    threadId,
                    startTimeUtcTicks,
                    processName,
                    processPath,
                    windowTitle,
                    windowClass,
                    architecture,
                    windowBounds,
                    MonitorFromWindow(window, MonitorDefaultToNearest),
                    window == foregroundWindow);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                Win32Exception or NotSupportedException)
            {
                return false;
            }
        }

        private static string TryGetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return string.Empty;
            }
        }

        private static ObsBinaryArchitecture GetProcessArchitecture(uint processId, string processPath)
        {
            IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);

            if (processHandle != IntPtr.Zero)
            {
                try
                {
                    if (IsWow64Process2(processHandle, out ushort processMachine, out ushort nativeMachine))
                    {
                        ushort machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;

                        return machine switch
                        {
                            ImageFileMachineI386 => ObsBinaryArchitecture.X86,
                            ImageFileMachineAmd64 => ObsBinaryArchitecture.X64,
                            ImageFileMachineArm64 => ObsBinaryArchitecture.Arm64,
                            _ => ObsBinaryArchitecture.Unsupported
                        };
                    }
                }
                finally
                {
                    CloseHandle(processHandle);
                }
            }

            return !string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath)
                ? ObsGameCaptureBinaryInspector.ReadArchitecture(processPath)
                : ObsBinaryArchitecture.Unsupported;
        }

        private static void AddUnique(List<IntPtr> handles, IntPtr handle)
        {
            if (handle != IntPtr.Zero && !handles.Contains(handle))
            {
                handles.Add(handle);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(
            IntPtr process,
            out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
