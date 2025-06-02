using System.Numerics;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    static class ShaderConstants
    {
        public static string ResourcePrefix => "D3D11Shaders";
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct PixelShaderConstants
    {
        public float HdrMaxLuminance;
        public float DisplayMaxLuminance;
        public float UserBrightnessScale;
        public float SdrWhiteLevel;
        public uint TonemapType;
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