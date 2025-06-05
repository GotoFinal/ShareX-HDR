using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class Direct3DCapture
{
    public Direct3DCapture(IntPtr handle, Rectangle rect, bool captureCursor = false)
    {
        // this rectangle might span over multiple screen, but each screen should produce only one rectangle region

    }
}