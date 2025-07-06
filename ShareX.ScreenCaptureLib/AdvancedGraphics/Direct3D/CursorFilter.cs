using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

// TODO: temp solution until i can get correct cross GPU capture, but need a setup to test it
public class CursorFilter
{
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    internal static List<ModernCaptureMonitorDescription> FilterByCursorGpu(
        DeviceCache cache,
        IDXGIFactory1 factory,
        IEnumerable<ModernCaptureMonitorDescription> monitors)
    {
        IntPtr cursorHmon = IntPtr.Zero;
        if (GetCursorPos(out POINT pt))
        {
            cursorHmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }
        if (cursorHmon == IntPtr.Zero)
        {
            // lets just assume its the first one...
            cursorHmon = monitors.First().MonitorInfo.Hmon;
        }


        var cursorAccess = cache.GetOutputForScreen(factory, cursorHmon, false);
        var cursorDeviceId = cursorAccess.Adapter.Description.DeviceId;

        var filtered = new List<ModernCaptureMonitorDescription>();
        foreach (var desc in monitors)
        {
            var access = cache.GetOutputForScreen(factory, desc.MonitorInfo.Hmon, false);
            if (access.Adapter.Description.DeviceId == cursorDeviceId)
                filtered.Add(desc);
        }
        return filtered;
    }
}