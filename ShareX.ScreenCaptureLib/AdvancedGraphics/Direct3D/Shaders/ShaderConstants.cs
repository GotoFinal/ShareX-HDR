using System.Numerics;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    static class ShaderConstants
    {
        public static string ResourcePrefix => "D3D11Shaders";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PixelShaderConstants
    {
        public float UserBrightnessScale;
        public float DisplayMaxLuminance;
        public float HdrMaxLuminance;
        public uint TonemapType;
        public float MaxYInPQ;
        public float MaxYInPQs;
        public float MaxYInPQss;
        public float MaxYInPQsss;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VertexShaderConstants
    {
        // scRGB allows values > 1.0, sRGB (SDR) simply clamps them
        // x = Luminance/Brightness -- For HDR displays, 1.0 = 80 Nits, For SDR displays, >= 1.0 = 80 Nits
        // y = isHDR
        // z = is10bpc
        // w = is16bpc
        public Vector4 LuminanceScale;
    }
}