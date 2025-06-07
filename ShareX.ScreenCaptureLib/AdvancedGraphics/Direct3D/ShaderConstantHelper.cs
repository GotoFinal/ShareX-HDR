using System.Numerics;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public static class ShaderConstantHelper // naming is hard
{
    public static void GetShaderConstants(MonitorInfo monitorInfo, HdrSettings settings, ImageInfo imageInfo, out VertexShaderConstants vertexShader, out PixelShaderConstants pixelShader)
    {
        // white level, isHDR, is10bpc, is16bpc
        vertexShader = new VertexShaderConstants
        {
            LuminanceScale = new Vector4(1.0f, 0.0f, 0.0f, 0.0f)
        };

        bool isHdr = false;
        uint bitsPerColor = 8;
        uint sdrWhiteLevel = 80;
        float maxFullFrameLuminance = 600;
        float maxLuminance = 600;
        float minLuminance = 0.0f;
        float maxContentLuminance = settings.Use99ThPercentileMaxCll ? imageInfo.P99Nits : imageInfo.MaxNits;

        pixelShader = new PixelShaderConstants()
        {
            DisplayMaxLuminance = maxLuminance / 80,
            HdrMaxLuminance = maxContentLuminance / 80,
            UserBrightnessScale = settings.BrightnessScale / 100,
            TonemapType = (uint)HdrToneMapType.MapCllToDisplay,
            // MaxYInPQ = imageInfo.MaxYInPQ,
        };

        monitorInfo.QueryMonitorData((colorInfoNullable, sdrInfoNullable, output6) =>
        {
            if (colorInfoNullable.HasValue)
            {
                var colorInfo = colorInfoNullable.Value;
                isHdr = (colorInfo.AdvancedColorStatus & AdvancedColorStatus.AdvancedColorEnabled) == AdvancedColorStatus.AdvancedColorEnabled;
                bitsPerColor = colorInfo.BitsPerColorChannel;
            }

            if (sdrInfoNullable.HasValue)
            {
                var sdrInfo = sdrInfoNullable.Value;
                sdrWhiteLevel = sdrInfo.SDRWhiteLevel;
            }

            if (output6 != null)
            {
                bitsPerColor = output6.Description1.BitsPerColor;
                maxFullFrameLuminance = output6.Description1.MaxFullFrameLuminance;
                maxLuminance = output6.Description1.MaxLuminance;
                minLuminance = output6.Description1.MinLuminance;
            }
        });

        if (isHdr)
        {
            vertexShader.LuminanceScale.Y = 1.0f; // is hdr

            // scRGB HDR 16 bpc
            if (settings.HdrMode == HdrMode.Hdr16Bpc)
            {
                vertexShader.LuminanceScale.X = settings.HdrBrightnessNits / 80.0f;
                vertexShader.LuminanceScale.W = 1.0f;
            }

            // HDR10
            else
            {
                vertexShader.LuminanceScale.X = -settings.HdrBrightnessNits;
                vertexShader.LuminanceScale.Z = 1.0f;
            }
        }

        // TODO
        // scRGB 16 bpc special handling
        else // if (SKIF_ImplDX11_ViewPort_GetDXGIFormat (vp) == DXGI_FORMAT_R16G16B16A16_FLOAT)
        {
            // SDR 16 bpc on HDR display
            if (sdrWhiteLevel > 80.0f)
                vertexShader.LuminanceScale.X = (sdrWhiteLevel / 80.0f);

            // SDR 16 bpc on SDR display
            vertexShader.LuminanceScale.W = 1.0f;
        }
        // TODO: maybe support later?
        // else if (SKIF_ImplDX11_ViewPort_GetDXGIFormat (vp) == DXGI_FORMAT_R10G10B10A2_UNORM)
        // {
        //     // SDR 10 bpc on SDR display
        //     vertexShader.LuminanceScale.Z = 1.0f;
        // }


        // TODO: edit when actually supporting hdr flow, for now we always want to get sdr result
        vertexShader.LuminanceScale = new Vector4(1, 0, 0, 0);
        vertexShader.LuminanceScale.X = (sdrWhiteLevel / 80.0f);

        pixelShader.DisplayMaxLuminance = maxLuminance / 80;
        if (pixelShader.UserBrightnessScale * maxContentLuminance > maxLuminance)
            pixelShader.TonemapType = (uint)settings.HdrToneMapType;
        else
            pixelShader.TonemapType = (uint)HdrToneMapType.None;

        pixelShader.TonemapType = 8;
        pixelShader.DisplayMaxLuminance = 3.375f;
        pixelShader.HdrMaxLuminance = 3.05f;



        pixelShader.font_dims = Vector4.Zero;
        pixelShader.hdr_visualization_flags = new UInt4(0, 0, 0, 4294967295);
        pixelShader.hdr_visualization = 0;
        pixelShader.HdrMaxLuminance = 3.05269f;
        pixelShader.sdr_reference_white = 80;
        pixelShader.DisplayMaxLuminance = 3.375f;
        pixelShader.UserBrightnessScale = 1;
        pixelShader.TonemapType = 8;
        pixelShader.content_max_cll = new Vector2(0,0);
        pixelShader.rec709_gamut_hue = Vector4.One;
        pixelShader.dcip3_gamut_hue = new Vector4(0,1,1,1);
        pixelShader.rec2020_gamut_hue = new Vector4(0,1,0,1);
        pixelShader.ap1_gamut_hue = new Vector4(1,1,0,1);
        pixelShader.ap0_gamut_hue = new Vector4(1,0,1,1);
        pixelShader.invalid_gamut_hue = new Vector4(1,0,0,1);

        vertexShader.LuminanceScale = new Vector4(1, 0, 0, 0);
    }
}