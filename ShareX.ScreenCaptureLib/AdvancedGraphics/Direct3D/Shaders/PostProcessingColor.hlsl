#include "ShaderInputStructure.hlsl"
#include "ColorSpace.hlsl"

Texture2D screenTexture : register(t0);
sampler   sampler0 : register (s0);

// Pass through from C# to configure the tone mapping process
cbuffer HdrMetadata : register(b0)
{
    float user_brightness_scale;
    float display_max_luminance;
    float hdr_max_luminance;
    uint tonemap_type;
    float MaxYInPQ;
};

// float4 main(VS_OUTPUT p) : SV_TARGET
// {
//     if (EnableHdrProcessing)
//     {
//         float4 hdr = screenTexture.Sample(simpleSampler, p.TexCoord);
//         float3 rgb_lin = hdr.xyz;
//         float alpha = hdr.w;
//
//         // If it's too bright, don't bother trying to tonemap the full range...
//         static const float _maxNitsToTonemap = 10000.0f;
//
//         float dML = LinearToPQY ( MonHdrDispNits / 80); // display_max_luminance
//         float cML = LinearToPQY (min (MaxYInPQ, _maxNitsToTonemap));
//         float3 ICtCp = Rec709toICtCp(rgb_lin);
//
//         float Y_in = max(ICtCp.x, 0.0f);
//         float Y_out = TonemapHDR(Y_in, cML, dML);
//
//         if ((Y_out + Y_in) > 0.0f)
//         {
//             float I0 = pow(Y_in, 1.18f);
//             float scaleFactor = 0.0f;
//             if (Y_in > 0.0f)
//             {
//                 scaleFactor = max(Y_out / Y_in, 0.0f);
//             }
//
//             float I1 = pow(Y_in, 1.18f) * scaleFactor;
//
//             float I_scale = 0.0f;
//             if ((I0 != 0.0f) && (I1 != 0.0f))
//             {
//                 I_scale = min(I0 / I1, I1 / I0);
//             }
//
//             ICtCp.x = I1;
//             ICtCp.yz *= I_scale;
//         }
//
//         float3 rgb_tonemapped_lin = ICtCptoRec709(ICtCp);
//         return float4(rgb_tonemapped_lin, alpha);
//     }
//
//     return PsPassthrough(p);
// }

float4 DrawMaxClipPattern(float x, float2 uv);

// TODO: as this code is from SKIV project it probably can be simplifed a lot more, as we dont need many of the features
float4 main(PS_INPUT input) : SV_Target
{
    float4 input_col = (1.0f).xxxx;

    float4 out_col = screenTexture.Sample(sampler0, input.uv);

    if (input.hdr_img)
    {
        if (display_max_luminance < 0)
        {
            return
                DrawMaxClipPattern(-display_max_luminance, input.uv);
        }
    }


    // When sampling FP textures, special FP bit patterns like NaN or Infinity
    //   may be returned. The same image represented using UNORM would replace
    //     these special values with 0.0, and that is the behavior we want...
    out_col =
        SanitizeFP(out_col);


    out_col.a = 1.0f;

    float4 orig_col = out_col;

    // input.lum.x        // Luminance (white point)
    bool isHDR = input.lum.y > 0.0; // HDR (10 bpc or 16 bpc)
    bool is10bpc = input.lum.z > 0.0; // 10 bpc
    bool is16bpc = input.lum.w > 0.0; // 16 bpc (scRGB)

    // 16 bpc scRGB (SDR/HDR)
    // ColSpace:  DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
    // Gamma:     1.0
    // Primaries: BT.709
    if (is16bpc)
    {
        out_col =
            float4(input.hdr_img
                       ? RemoveGammaExp(input_col.rgb, 2.2f) *
                       out_col.rgb
                       : RemoveGammaExp(input_col.rgb *
                                        ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
                   saturate(out_col.a) *
                   saturate(input_col.a)
            );

        // sRGB (SDR) Content
        if (input.srgb_img)
        {
            out_col =
                float4((input_col.rgb) *
                       (out_col.rgb),
                       saturate(out_col.a) *
                       saturate(input_col.a));

            out_col.rgb = RemoveSRGBCurve(out_col.rgb);
        }

        float hdr_scale = input.lum.x;

        if (!input.hdr_img)
            out_col.rgb = saturate(out_col.rgb) * hdr_scale;
        else
            out_col.a = 1.0f;
    }

    // 10 bpc SDR
    // ColSpace:  DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709
    // Gamma:     2.2
    // Primaries: BT.709
    else if (is10bpc)
    {
        // sRGB (SDR) Content
        if (input.srgb_img)
        {
            out_col =
                float4((input_col.rgb) *
                       (out_col.rgb),
                       saturate(out_col.a) *
                       saturate(input_col.a));

            out_col.rgb = RemoveSRGBCurve(out_col.rgb);
        }

        else if (!input.hdr_img)
        {
            out_col =
                float4(RemoveGammaExp(input_col.rgb *
                                      ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
                       saturate(out_col.a) *
                       saturate(input_col.a)
                );
        }

        else
        {
            out_col =
                float4(RemoveGammaExp(input_col.rgb, 2.2f) *
                       out_col.rgb,
                       saturate(out_col.a) *
                       saturate(input_col.a)
                );

            out_col.a = 1.0f; // Opaque
        }
    }

    // 8 bpc SDR (sRGB)
    else
    {
#ifdef _SRGB
    out_col =
      float4 (   (           input_col.rgb) *
                 (             out_col.rgb),
                                  saturate (  out_col.a)  *
                                  saturate (input_col.a)
              );

    out_col.rgb = RemoveSRGBCurve (out_col.rgb);

    // Manipulate the alpha channel a bit...
    out_col.a = 1.0f - RemoveSRGBCurve (1.0f - out_col.a);
#else

        // sRGB (SDR) Content
        if (input.srgb_img)
        {
            out_col =
                float4((input_col.rgb) *
                       (out_col.rgb),
                       saturate(out_col.a) *
                       saturate(input_col.a));

            out_col.rgb = RemoveSRGBCurve(out_col.rgb);
        }

        else if (!input.hdr_img)
        {
            out_col =
                float4(RemoveGammaExp(input_col.rgb *
                                      ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
                       saturate(out_col.a) *
                       saturate(input_col.a)
                );
        }

        else
        {
            out_col =
                float4(RemoveGammaExp(input_col.rgb, 2.2f) *
                       out_col.rgb,
                       saturate(out_col.a) *
                       saturate(input_col.a)
                );

            out_col.a = 1.0f; // Opaque
        }
#endif
    }

    if (input.hdr_img)
    {
        uint implied_tonemap_type = tonemap_type;

        out_col.rgb *=
            isHDR
                ? user_brightness_scale
                : max(user_brightness_scale, 0.001f);


        // If it's too bright, don't bother trying to tonemap the full range...
        static const float _maxNitsToTonemap = 10000.0f;

        float dML = LinearToPQY(display_max_luminance);
        // float cML = LinearToPQY(min(hdr_max_luminance, _maxNitsToTonemap));
        float cML = LinearToPQY(min(MaxYInPQ, _maxNitsToTonemap));

        if (implied_tonemap_type != SKIV_TONEMAP_TYPE_NONE && (!isHDR))
        {
            implied_tonemap_type = SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY;
            dML = LinearToPQY(1.5f);
        }

        else if (implied_tonemap_type == SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY)
        {
            ///out_col.rgb *=
            ///  1.0f / max (1.0f, 80.0f / (2.0f * sdr_reference_white));
        }

        float3 ICtCp = Rec709toICtCp(out_col.rgb);
        float Y_in = max(ICtCp.x, 0.0f);
        float Y_out = 1.0f;

        switch (implied_tonemap_type)
        {
        // This tonemap type is not necessary, we always know content range
        //SKIV_TONEMAP_TYPE_INFINITE_ROLLOFF

        default:
        case SKIV_TONEMAP_TYPE_NONE: Y_out = TonemapNone(Y_in);
            break;
        case SKIV_TONEMAP_TYPE_CLIP: Y_out = TonemapClip(Y_in, dML);
            break;
        case SKIV_TONEMAP_TYPE_NORMALIZE_TO_CLL: Y_out = TonemapSDR(Y_in, cML, 1.0f);
            break;
        case SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY: Y_out = TonemapHDR(Y_in, cML, dML);
            break;
        }

        if (Y_out + Y_in > 0.0)
        {
            if (implied_tonemap_type == SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY)
            {
                if ((!isHDR))
                    ICtCp.x = pow(ICtCp.x, 1.18f);
            }

            float I0 = ICtCp.x;
            float I_scale = 0.0f;

            ICtCp.x *=
                max((Y_out / Y_in), 0.0f);

            if (ICtCp.x != 0.0f && I0 != 0.0f)
            {
                I_scale =
                    min(I0 / ICtCp.x, ICtCp.x / I0);
            }

            ICtCp.yz *= I_scale;
        }

        else
            ICtCp.x = 0.0;

        out_col.rgb =
            ICtCptoRec709(ICtCp);
    }

    if (!is16bpc)
    {
        out_col.rgb =
            ApplySRGBCurve(saturate(out_col.rgb));
    }

    if (dot(orig_col * user_brightness_scale, (1.0f).xxxx) <= FP16_MIN)
        out_col.rgb = 0.0f;

    out_col.rgb *=
        out_col.a;

    return
        SanitizeFP(out_col);
}

float4 DrawMaxClipPattern(float clipLevel_scRGB, float2 uv)
{
    float2 texDims;

    screenTexture.
        GetDimensions(texDims.x,
                      texDims.y);

    float2 scale =
        float2(texDims.x / 10.0,
               texDims.y / 10.0);

    float2 size = texDims.xy / scale;
    float total =
        floor(uv.x * size.x) +
        floor(uv.y * size.y);

    bool isEven =
        fmod(total, 2.0f) == 0.0f;

    float4 color1 = float4((clipLevel_scRGB).xxx, 1.0);
    float4 color2 = float4((125.0).xxx, 1.0);

    return isEven ? color1 : color2;
}