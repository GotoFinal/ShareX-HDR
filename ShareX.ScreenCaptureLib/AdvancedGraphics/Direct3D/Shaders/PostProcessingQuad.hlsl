#pragma warning ( disable : 3571 )

cbuffer vertexBuffer : register (b0)
{
    float4 Luminance;
};

struct VS_INPUT
{
    float2 pos : POSITION;
    float2 uv : TEXCOORD0;
};

struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 lum : COLOR1; // constant_buffer->luminance_scale
};

PS_INPUT main(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = float4(input.pos.xy, 0.f, 1.f);
    output.uv = input.uv;
    output.lum = Luminance.xyzw;
    return output;
}