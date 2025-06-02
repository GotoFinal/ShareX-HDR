//
// Copyright 2024 Andon "Kaldaien" Coleman
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

#pragma warning ( disable : 3571 )

struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 lum : COLOR1; // constant_buffer->luminance_scale
};

cbuffer imgui_cbuffer : register (b0)
{
    float hdr_max_luminance;
    float display_max_luminance;
    float user_brightness_scale;
    float sdr_reference_white;
    uint tonemap_type;
};

sampler sampler0 : register (s0);
Texture2D texture0 : register (t0);

#include "colorspaces.hlsli"
#include "tone_mapping.hlsli"
#include "calibration.hlsli"

float4 main(PS_INPUT input) : SV_Target
{
    float4 input_col = (1.0f).xxxx;


    bool hdr_img = true; // input.hdr_img
    bool srgb_img = false; // srgb_img

    float4 out_col =
        texture0.Sample(sampler0, input.uv);

    if (hdr_img)
    {
        if (display_max_luminance < 0)
        {
            return DrawMaxClipPattern(-display_max_luminance, input.uv);
        }
    }


    // When sampling FP textures, special FP bit patterns like NaN or Infinity
    //   may be returned. The same image represented using UNORM would replace
    //     these special values with 0.0, and that is the behavior we want...
    out_col = SanitizeFP(out_col);

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
            float4(hdr_img
                       ? RemoveGammaExp(input_col.rgb, 2.2f) *
                       out_col.rgb
                       : RemoveGammaExp(input_col.rgb *
                                        ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
                   saturate(out_col.a) *
                   saturate(input_col.a)
            );

        // sRGB (SDR) Content
        if (srgb_img)
        {
            out_col =
                float4((input_col.rgb) *
                       (out_col.rgb),
                       saturate(out_col.a) *
                       saturate(input_col.a));

            out_col.rgb = RemoveSRGBCurve(out_col.rgb);
        }

        float hdr_scale = input.lum.x;

        if (!hdr_img)
            out_col.rgb = saturate(out_col.rgb) * hdr_scale;
        else
            out_col.a = 1.0f; // Opaque

     }

    // 10 bpc SDR
    // ColSpace:  DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709
    // Gamma:     2.2
    // Primaries: BT.709
    else if (is10bpc)
    {
        // sRGB (SDR) Content
        if (srgb_img)
        {
            out_col =
                float4((input_col.rgb) *
                       (out_col.rgb),
                       saturate(out_col.a) *
                       saturate(input_col.a));

            out_col.rgb = RemoveSRGBCurve(out_col.rgb);
        }

        else if (!hdr_img)
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
        // sRGB (SDR) Content
        if (srgb_img)
        {
            out_col =
                float4((input_col.rgb) *
                       (out_col.rgb),
                       saturate(out_col.a) *
                       saturate(input_col.a));

            out_col.rgb = RemoveSRGBCurve(out_col.rgb);
        }

        else if (!hdr_img)
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

    if (hdr_img)
    {
        uint implied_tonemap_type = tonemap_type;

        out_col.rgb *=
            isHDR ? user_brightness_scale : max(user_brightness_scale, 0.001f);


        // If it's too bright, don't bother trying to tonemap the full range...
        static const float _maxNitsToTonemap = 10000.0f;

        float dML = LinearToPQY(display_max_luminance);
        float cML = LinearToPQY(min(hdr_max_luminance, _maxNitsToTonemap));

        if (implied_tonemap_type != SKIV_TONEMAP_TYPE_NONE && (!isHDR))
        {
            implied_tonemap_type = SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY;
            dML = LinearToPQY(1.5f);
        }

        else if (implied_tonemap_type == SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY && sdr_reference_white > 0)
        {
            // out_col.rgb *=
            //   1.0f / max (1.0f, 80.0f / (2.0f * sdr_reference_white));
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
