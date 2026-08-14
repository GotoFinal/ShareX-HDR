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

using ShareX.ImageEditor.Hosting;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ShareX.ImageEditor.Presentation.Rendering;

/// <summary>
/// Presents retained RGBA16F scRGB pixels through a dedicated flip-model DXGI
/// swap chain. The no-activate popup sits immediately below its interactive
/// overlay window, allowing SDR controls and annotations to remain above the
/// HDR source image.
/// </summary>
public sealed class WindowsHdrPreviewPresenter : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;

    private static readonly object WindowClassSync = new();
    private static readonly WindowProc PreviewWindowProc = WindowProcedure;
    private static readonly string WindowClassName = $"ShareX.HdrEditorPreview.{Environment.ProcessId}";
    private static bool windowClassRegistered;

    private IntPtr windowHandle;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDXGISwapChain1? swapChain;
    private ID3D11Texture2D? backBuffer;
    private Rectangle lastClipRectangle = Rectangle.Empty;
    private bool disposed;

    public WindowsHdrPreviewPresenter(EditorHdrPreviewSource source)
        : this(
            source?.PixelBytes ?? throw new ArgumentNullException(nameof(source)),
            source.RowBytes,
            source.Width,
            source.Height)
    {
    }

    public WindowsHdrPreviewPresenter(
        ReadOnlySpan<byte> pixels,
        int rowBytes,
        int width,
        int height)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native HDR preview requires Windows.");
        }

        int activeRowBytes = checked(width * EditorHdrPreviewSource.BytesPerPixel);
        if (width <= 0 || height <= 0 || rowBytes < activeRowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The HDR preview dimensions are invalid.");
        }

        int requiredBytes = checked((height - 1) * rowBytes + activeRowBytes);
        if (pixels.Length < requiredBytes)
        {
            throw new ArgumentException("The HDR preview buffer is too small.", nameof(pixels));
        }

        try
        {
            EnsureWindowClass();
            windowHandle = CreateWindowExW(
                WsExToolWindow | WsExNoActivate,
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the HDR preview window.");
            }

            CreateDeviceAndSwapChain(pixels, rowBytes, width, height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateDeviceAndSwapChain(
        ReadOnlySpan<byte> pixels,
        int rowBytes,
        int width,
        int height)
    {
        D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out device,
            out _,
            out context).CheckError();

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.R16G16B16A16_Float,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore
        };

        swapChain = factory.CreateSwapChainForHwnd(device, windowHandle, description);
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);
        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);

        using IDXGISwapChain3 swapChain3 = swapChain.QueryInterface<IDXGISwapChain3>();
        SwapChainColorSpaceSupportFlags colorSpaceSupport =
            swapChain3.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709);
        if ((colorSpaceSupport & SwapChainColorSpaceSupportFlags.Present) == 0)
        {
            throw new NotSupportedException("The active display path cannot present an scRGB swap chain.");
        }

        swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);

        context.UpdateSubresource(
            pixels,
            backBuffer,
            0,
            (uint)rowBytes);
        swapChain.Present(1, PresentFlags.None).CheckError();
    }

    public void UpdateLayout(
        Rectangle imageScreenRectangle,
        Rectangle visibleScreenRectangle,
        IntPtr editorWindowHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        Rectangle clipped = Rectangle.Intersect(imageScreenRectangle, visibleScreenRectangle);
        if (editorWindowHandle == IntPtr.Zero || imageScreenRectangle.Width <= 0 ||
            imageScreenRectangle.Height <= 0 || clipped.Width <= 0 || clipped.Height <= 0)
        {
            Hide();
            return;
        }

        if (!SetWindowPos(
            windowHandle,
            editorWindowHandle,
            imageScreenRectangle.X,
            imageScreenRectangle.Y,
            imageScreenRectangle.Width,
            imageScreenRectangle.Height,
            SwpNoActivate | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to position the HDR preview window.");
        }

        Rectangle localClip = new Rectangle(
            clipped.X - imageScreenRectangle.X,
            clipped.Y - imageScreenRectangle.Y,
            clipped.Width,
            clipped.Height);

        if (localClip != lastClipRectangle)
        {
            ApplyWindowRegion(localClip);
            lastClipRectangle = localClip;
        }
    }

    public void Hide()
    {
        if (windowHandle != IntPtr.Zero)
        {
            ShowWindow(windowHandle, SwHide);
        }
    }

    private void ApplyWindowRegion(Rectangle rectangle)
    {
        IntPtr region = CreateRectRgn(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);

        if (region == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the HDR preview clip region.");
        }

        if (SetWindowRgn(windowHandle, region, true) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            DeleteObject(region);
            throw new Win32Exception(error, "Failed to clip the HDR preview window.");
        }

        // SetWindowRgn transfers ownership of the region to Windows on success.
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        context?.ClearState();
        context?.Flush();
        backBuffer?.Dispose();
        backBuffer = null;
        swapChain?.Dispose();
        swapChain = null;
        context?.Dispose();
        context = null;
        device?.Dispose();
        device = null;

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
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(PreviewWindowProc),
                Instance = GetModuleHandleW(null),
                ClassName = WindowClassName
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register the HDR preview window class.");
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
        public string? MenuName;
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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
