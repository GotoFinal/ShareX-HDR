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
    private const int DxgiStatusOccluded = unchecked((int)0x087A0001);
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint WmNcHitTest = 0x0084;
    private const int SwHide = 0;
    private const int HtTransparent = -1;

    private static readonly object WindowClassSync = new();
    private static readonly WindowProc PreviewWindowProc = WindowProcedure;
    private static readonly string WindowClassName = $"ShareX.HdrEditorPreview.{Environment.ProcessId}";
    private static bool windowClassRegistered;

    private byte[]? sourcePixels;
    private readonly int sourceRowBytes;
    private readonly int sourceWidth;
    private readonly int sourceHeight;
    private readonly bool requireHdrOutput;
    private IntPtr windowHandle;
    private IntPtr targetMonitor;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDXGISwapChain3? swapChain;
    private ID3D11Texture2D? sourceTexture;
    private Rectangle lastClipRectangle = Rectangle.Empty;
    private bool disposed;

    public string AdapterDescription { get; private set; } = "Unavailable";
    public string OutputDeviceName { get; private set; } = "Unavailable";
    public string OutputColorSpace { get; private set; } = "Unavailable";
    public int LastPresentResultCode { get; private set; }

    public WindowsHdrPreviewPresenter(EditorHdrPreviewSource source)
        : this(
            source?.PixelBytes ?? throw new ArgumentNullException(nameof(source)),
            source.RowBytes,
            source.Width,
            source.Height,
            requireHdrOutput: false)
    {
    }

    public WindowsHdrPreviewPresenter(
        ReadOnlySpan<byte> pixels,
        int rowBytes,
        int width,
        int height,
        bool requireHdrOutput = false)
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

        EnsureWindowClass();
        sourcePixels = pixels.Slice(0, requiredBytes).ToArray();
        sourceRowBytes = rowBytes;
        sourceWidth = width;
        sourceHeight = height;
        this.requireHdrOutput = requireHdrOutput;
    }

    private void CreatePresentationResources(Rectangle screenRectangle, IntPtr monitor)
    {
        if (sourcePixels == null)
        {
            throw new InvalidOperationException("The HDR preview source pixels are no longer available.");
        }

        try
        {
            using IDXGIAdapter1 adapter = FindAdapterForMonitor(
                monitor,
                out string adapterDescription,
                out string outputDeviceName,
                out ColorSpaceType outputColorSpace);

            if (requireHdrOutput && outputColorSpace != ColorSpaceType.RgbFullG2084NoneP2020)
            {
                throw new NotSupportedException(
                    $"The target output is not currently using an HDR color space ({outputColorSpace}).");
            }

            AdapterDescription = adapterDescription;
            OutputDeviceName = outputDeviceName;
            OutputColorSpace = outputColorSpace.ToString();

            // The HWND must start on its destination display. DXGI and DWM use
            // the window location when determining Advanced Color behavior.
            windowHandle = CreateWindowExW(
                WsExToolWindow | WsExNoActivate,
                WindowClassName,
                string.Empty,
                WsPopup,
                screenRectangle.X,
                screenRectangle.Y,
                screenRectangle.Width,
                screenRectangle.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandleW(null),
                IntPtr.Zero);

            if (windowHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the HDR preview window.");
            }

            D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                null!,
                out device,
                out _,
                out context).CheckError();

            var textureDescription = new Texture2DDescription
            {
                Width = (uint)sourceWidth,
                Height = (uint)sourceHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            sourceTexture = device.CreateTexture2D(textureDescription);
            context.UpdateSubresource(
                sourcePixels.AsSpan(),
                sourceTexture,
                0,
                (uint)sourceRowBytes);

            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter deviceAdapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = deviceAdapter.GetParent<IDXGIFactory2>();
            var swapChainDescription = new SwapChainDescription1
            {
                Width = (uint)sourceWidth,
                Height = (uint)sourceHeight,
                Format = Format.R16G16B16A16_Float,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore
            };

            using IDXGISwapChain1 createdSwapChain =
                factory.CreateSwapChainForHwnd(device, windowHandle, swapChainDescription);
            factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);
            swapChain = createdSwapChain.QueryInterface<IDXGISwapChain3>();

            SwapChainColorSpaceSupportFlags colorSpaceSupport =
                swapChain.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709);
            if ((colorSpaceSupport & SwapChainColorSpaceSupportFlags.Present) == 0)
            {
                throw new NotSupportedException("The target display path cannot present an scRGB swap chain.");
            }

            swapChain.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            targetMonitor = monitor;
            sourcePixels = null;
        }
        catch
        {
            ReleasePresentationResources();
            throw;
        }
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

        IntPtr destinationMonitor = GetMonitorForRectangle(imageScreenRectangle);
        if (windowHandle == IntPtr.Zero)
        {
            CreatePresentationResources(imageScreenRectangle, destinationMonitor);
        }
        else if (destinationMonitor != targetMonitor)
        {
            throw new InvalidOperationException(
                "The HDR preview moved to a different display; switching to the SDR preview is required.");
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

        PresentFrame();
    }

    private void PresentFrame()
    {
        if (swapChain == null || context == null || sourceTexture == null)
        {
            throw new InvalidOperationException("The HDR preview presentation resources are unavailable.");
        }

        // D3D11 rotates the identity exposed as buffer zero for flip-model swap
        // chains. Reacquire it and copy the retained source before every present
        // so a newly visible or resized window never exposes an uninitialized
        // second flip buffer.
        using ID3D11Texture2D currentBackBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        context.CopyResource(currentBackBuffer, sourceTexture);
        var presentResult = swapChain.Present(1, PresentFlags.None);
        LastPresentResultCode = presentResult.Code;
        if (presentResult.Code == DxgiStatusOccluded)
        {
            throw new InvalidOperationException(
                "The HDR preview frame was occluded instead of being presented.");
        }

        presentResult.CheckError();
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
        sourcePixels = null;
        ReleasePresentationResources();
    }

    private void ReleasePresentationResources()
    {
        context?.ClearState();
        context?.Flush();
        sourceTexture?.Dispose();
        sourceTexture = null;
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

        targetMonitor = IntPtr.Zero;
        lastClipRectangle = Rectangle.Empty;
    }

    private static IntPtr GetMonitorForRectangle(Rectangle rectangle)
    {
        var point = new NativePoint
        {
            X = rectangle.Left + rectangle.Width / 2,
            Y = rectangle.Top + rectangle.Height / 2
        };
        IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to resolve the HDR preview monitor.");
        }

        return monitor;
    }

    private static IDXGIAdapter1 FindAdapterForMonitor(
        IntPtr monitor,
        out string adapterDescription,
        out string outputDeviceName,
        out ColorSpaceType outputColorSpace)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0;
            factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success;
            adapterIndex++)
        {
            bool selected = false;
            try
            {
                for (uint outputIndex = 0;
                    adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success;
                    outputIndex++)
                {
                    using (output)
                    using (IDXGIOutput6? output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                    {
                        if (output6 == null)
                        {
                            continue;
                        }

                        OutputDescription1 description = output6.Description1;
                        if (description.Monitor != monitor)
                        {
                            continue;
                        }

                        selected = true;
                        adapterDescription = adapter.Description1.Description.TrimEnd('\0');
                        outputDeviceName = description.DeviceName;
                        outputColorSpace = description.ColorSpace;
                        return adapter;
                    }
                }
            }
            finally
            {
                if (!selected)
                {
                    adapter.Dispose();
                }
            }
        }

        throw new NotSupportedException("No DXGI adapter owns the HDR preview monitor.");
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

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // This window only presents pixels. Mouse input must continue to the
        // editor/region-selector overlay even when this swap chain happens to
        // be above its dedicated input proxy in the composed Z order.
        if (message == WmNcHitTest)
        {
            return (IntPtr)HtTransparent;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

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
