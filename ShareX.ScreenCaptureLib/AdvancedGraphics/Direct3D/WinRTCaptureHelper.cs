using System;
using Windows.Graphics;
using Windows.Graphics.Capture;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D
{
    public static class WinRTCaptureHelper
    {
        public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
        {
            return GraphicsCaptureItem.TryCreateFromDisplayId(new DisplayId((ulong)hmon.ToInt64()));
        }
    }
}