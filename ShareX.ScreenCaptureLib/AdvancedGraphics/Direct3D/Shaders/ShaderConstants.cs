using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders
{
    static class ShaderConstants
    {
        public static string ResourcePrefix => "D3D11Shaders";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PixelShaderConstants
    {
        // float4 font_dims;
        //
        // uint4  hdr_visualization_flags;
        // uint   hdr_visualization;
        //
        // float  hdr_max_luminance;
        // float  sdr_reference_white;
        // float  display_max_luminance;
        // float  user_brightness_scale;
        // uint   tonemap_type;
        // float2 content_max_cll;
        //
        // float4 rec709_gamut_hue;
        // float4 dcip3_gamut_hue;
        // float4 rec2020_gamut_hue;
        // float4 ap1_gamut_hue;
        // float4 ap0_gamut_hue;
        // float4 invalid_gamut_hue;

        public Vector4 font_dims;
        public UInt4 hdr_visualization_flags;
        public uint   hdr_visualization;

        public float  HdrMaxLuminance;
        public float  sdr_reference_white;
        public float  DisplayMaxLuminance;
        public float  UserBrightnessScale;
        public uint   TonemapType;
        public Vector2 content_max_cll;

        public Vector4 rec709_gamut_hue;
        public Vector4 dcip3_gamut_hue;
        public Vector4 rec2020_gamut_hue;
        public Vector4 ap1_gamut_hue;
        public Vector4 ap0_gamut_hue;
        public Vector4 invalid_gamut_hue;




        // public float UserBrightnessScale;
        // public float DisplayMaxLuminance;
        // public float HdrMaxLuminance;
        // public uint TonemapType;
        // public float MaxYInPQ;
        // public float MaxYInPQs;
        // public float MaxYInPQss;
        // public float MaxYInPQsss;
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