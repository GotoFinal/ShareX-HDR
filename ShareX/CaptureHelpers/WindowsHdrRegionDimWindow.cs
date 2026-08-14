#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX;

/// <summary>
/// Composes a translucent black mask above the native HDR surface. Its window
/// region excludes selected shapes, so the retained scRGB pixels remain visible
/// at full brightness inside the selection without CPU tone mapping.
/// </summary>
internal sealed class WindowsHdrRegionDimWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int BlackBrush = 4;

    private static readonly object WindowClassSync = new();
    private static readonly WindowProc DimWindowProc = WindowProcedure;
    private static readonly string WindowClassName = $"ShareX.HdrRegionDim.{Environment.ProcessId}";
    private static bool windowClassRegistered;

    private IntPtr windowHandle;
    private bool disposed;
    public int EffectiveDimStrength { get; }

    public WindowsHdrRegionDimWindow(int dimStrength)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The HDR region dim window requires Windows.");
        }

        double configuredStrength = Math.Clamp(dimStrength, 0, 100) / 100d;
        // A linear black overlay looks perceptually weaker over HDR highlights.
        // Gently curve it while retaining exact 0% and 100% endpoints.
        double effectiveStrength = 1d - Math.Pow(1d - configuredStrength, 1.5d);
        EffectiveDimStrength = (int)Math.Round(effectiveStrength * 100d);
        byte opacity = (byte)Math.Round(255d * effectiveStrength);
        EnsureWindowClass();
        windowHandle = CreateWindowExW(
            WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate,
            WindowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the HDR region dim window.");
        }

        if (!SetLayeredWindowAttributes(windowHandle, 0, opacity, LwaAlpha))
        {
            int error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "Failed to configure the HDR region dim window.");
        }
    }

    public void Show(Rectangle screenBounds, IntPtr selectorHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (selectorHandle == IntPtr.Zero || screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            Hide();
            return;
        }

        if (!SetWindowPos(
            windowHandle,
            selectorHandle,
            screenBounds.X,
            screenBounds.Y,
            screenBounds.Width,
            screenBounds.Height,
            SwpNoActivate | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to position the HDR region dim window.");
        }
    }

    public void UpdateRegion(Region dimRegion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(dimRegion);

        using Graphics graphics = Graphics.FromHwnd(windowHandle);
        IntPtr nativeRegion = dimRegion.GetHrgn(graphics);
        if (nativeRegion == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the HDR dim region.");
        }

        if (SetWindowRgn(windowHandle, nativeRegion, true) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            DeleteObject(nativeRegion);
            throw new Win32Exception(error, "Failed to update the HDR dim region.");
        }

        // SetWindowRgn owns nativeRegion after a successful call.
    }

    public void Hide()
    {
        if (windowHandle != IntPtr.Zero)
        {
            ShowWindow(windowHandle, SwHide);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (windowHandle != IntPtr.Zero)
        {
            DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }
    }

    private static void EnsureWindowClass()
    {
        if (windowClassRegistered)
        {
            return;
        }

        lock (WindowClassSync)
        {
            if (windowClassRegistered)
            {
                return;
            }

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(DimWindowProc),
                Instance = GetModuleHandleW(null),
                BackgroundBrush = GetStockObject(BlackBrush),
                ClassName = WindowClassName
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register the HDR region dim window class.");
            }

            windowClassRegistered = true;
        }
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hwnd, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        public string MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectIndex);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);
}
