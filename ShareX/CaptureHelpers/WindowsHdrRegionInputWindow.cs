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
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX;

/// <summary>
/// Keeps the color-keyed HDR selector from passing clicks through to desktop
/// applications. It sits between the selector and its HDR swap chain and
/// forwards input to the still-active WinForms selector.
/// </summary>
internal sealed class WindowsHdrRegionInputWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseFirst = 0x0200;
    private const uint WmMouseLast = 0x020E;
    private const int HtClient = 1;
    private const int MaNoActivate = 3;
    private const int BlackBrush = 4;

    private static readonly object WindowClassSync = new();
    private static readonly object InstancesSync = new();
    private static readonly Dictionary<IntPtr, WindowsHdrRegionInputWindow> Instances = new();
    private static readonly WindowProc InputWindowProc = WindowProcedure;
    private static readonly string WindowClassName = $"ShareX.HdrRegionInput.{Environment.ProcessId}";
    private static bool windowClassRegistered;

    private IntPtr windowHandle;
    private IntPtr targetHandle;
    private bool disposed;

    public WindowsHdrRegionInputWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The HDR region input window requires Windows.");
        }

        EnsureWindowClass();
        windowHandle = CreateWindowExW(
            WsExToolWindow | WsExLayered | WsExNoActivate,
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the HDR region input window.");
        }

        lock (InstancesSync)
        {
            Instances.Add(windowHandle, this);
        }

        if (!SetLayeredWindowAttributes(windowHandle, 0, 1, LwaAlpha))
        {
            int error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "Failed to configure the HDR region input window.");
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

        targetHandle = selectorHandle;
        if (!SetWindowPos(
            windowHandle,
            selectorHandle,
            screenBounds.X,
            screenBounds.Y,
            screenBounds.Width,
            screenBounds.Height,
            SwpNoActivate | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to position the HDR region input window.");
        }
    }

    public void Hide()
    {
        targetHandle = IntPtr.Zero;
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
        targetHandle = IntPtr.Zero;
        if (windowHandle != IntPtr.Zero)
        {
            lock (InstancesSync)
            {
                Instances.Remove(windowHandle);
            }

            DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        WindowsHdrRegionInputWindow instance;
        lock (InstancesSync)
        {
            Instances.TryGetValue(hwnd, out instance);
        }

        if (message == WmNcHitTest)
        {
            return (IntPtr)HtClient;
        }

        if (message == WmMouseActivate)
        {
            return (IntPtr)MaNoActivate;
        }

        if (instance?.targetHandle != IntPtr.Zero &&
            (message == WmSetCursor || (message >= WmMouseFirst && message <= WmMouseLast)))
        {
            return SendMessageW(instance.targetHandle, message, wParam, lParam);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
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
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(InputWindowProc),
                Instance = GetModuleHandleW(null),
                BackgroundBrush = GetStockObject(BlackBrush),
                ClassName = WindowClassName
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register the HDR region input window class.");
            }

            windowClassRegistered = true;
        }
    }

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

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

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

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);
}
