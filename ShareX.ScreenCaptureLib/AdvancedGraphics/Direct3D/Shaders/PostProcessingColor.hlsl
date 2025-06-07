#include "ShaderInputStructure.hlsl"
#include "ColorSpace.hlsl"

Texture2D screenTexture : register(t0);
SamplerState simpleSampler : register(s0);

// Pass through from C# to configure the tone mapping process
cbuffer HdrMetadata : register(b0)
{
    bool EnableHdrProcessing;
    float MonHdrDispNits;
    float MonSdrDispNits;
    float MaxYInPQ;
};

float4 PsPassthrough(VS_OUTPUT p) : SV_TARGET
{
    return screenTexture.Sample(simpleSampler, p.TexCoord);
}

float4 main(VS_OUTPUT p) : SV_TARGET
{
    if (EnableHdrProcessing)
    {
        float4 hdr = screenTexture.Sample(simpleSampler, p.TexCoord);
        float3 rgb_lin = hdr.xyz;
        float alpha = hdr.w;

        // If it's too bright, don't bother trying to tonemap the full range...
        static const float _maxNitsToTonemap = 10000.0f;

        float dML = LinearToPQY ( MonHdrDispNits / 80); // display_max_luminance
        float cML = LinearToPQY (min (MaxYInPQ, _maxNitsToTonemap));
        float3 ICtCp = Rec709toICtCp(rgb_lin);

        float Y_in = max(ICtCp.x, 0.0f);
        float Y_out = TonemapHDR(Y_in, cML, dML);

        if ((Y_out + Y_in) > 0.0f)
        {
            float I0 = pow(Y_in, 1.18f);
            float scaleFactor = 0.0f;
            if (Y_in > 0.0f)
            {
                scaleFactor = max(Y_out / Y_in, 0.0f);
            }

            float I1 = pow(Y_in, 1.18f) * scaleFactor;

            float I_scale = 0.0f;
            if ((I0 != 0.0f) && (I1 != 0.0f))
            {
                I_scale = min(I0 / I1, I1 / I0);
            }

            ICtCp.x = I1;
            ICtCp.yz *= I_scale;
        }

        float3 rgb_tonemapped_lin = ICtCptoRec709(ICtCp);
        return float4(rgb_tonemapped_lin, alpha);
    }

    return PsPassthrough(p);
}