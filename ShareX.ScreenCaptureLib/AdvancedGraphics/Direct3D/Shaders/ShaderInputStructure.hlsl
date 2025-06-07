struct VS_INPUT
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD;
};

struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 lum      : COLOR1; // constant_buffer->luminance_scale
    float  hdr_img  : COLOR2; // 1 to indicate HDR content
    float  srgb_img : COLOR3; // 1 to indicate sRGB (SDR) content
};
