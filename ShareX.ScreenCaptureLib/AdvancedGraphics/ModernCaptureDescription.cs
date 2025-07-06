using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics
{
    public class ModernCaptureMonitorDescription
    {
        // For GDI use
        public Rectangle DestGdiRect { get; set; }
        public MonitorInfo MonitorInfo { get; set; }

        // For WinRT use
        public bool CaptureCursor { get; set; }

        public ModernCaptureMonitorDescription()
        {
        }
    }

    public class ModernCaptureItemDescription
    {
        public List<ModernCaptureMonitorDescription> Regions { get; set; }
        public Rectangle CanvasRect { get; private set; }

        public ModernCaptureItemDescription(Rectangle canvas, List<ModernCaptureMonitorDescription> monRegions)
        {
            CanvasRect = canvas;
            Regions = monRegions;

        }
    }
}
