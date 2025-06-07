#pragma warning ( disable : 3571 )

#define SKIF_Shaders

#ifndef SKIF_Shaders

cbuffer vertexBuffer : register(b0)
{
  float4x4 ProjectionMatrix;
};
struct VS_INPUT
{
  float2 pos : POSITION;
  float4 col : COLOR0;
  float2 uv  : TEXCOORD0;
};

struct PS_INPUT
{
  float4 pos : SV_POSITION;
  float4 col : COLOR0;
  float2 uv  : TEXCOORD0;
};

PS_INPUT main(VS_INPUT input)
{
  PS_INPUT output;
  output.pos = mul( ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
  output.col = input.col;
  output.uv  = input.uv;
  return output;
}

#else

cbuffer vertexBuffer : register (b0)
{
  float4   Luminance;
};

struct VS_INPUT
{
  float2 pos : POSITION;
  float2 uv  : TEXCOORD0;
};

struct PS_INPUT
{
  float4 pos      : SV_POSITION;
  float2 uv       : TEXCOORD0;
  float4 col      : COLOR0; // ImU32
  float4 lum      : COLOR1; // constant_buffer->luminance_scale
  float  hdr_img  : COLOR2; // 1 to indicate HDR content
  float  srgb_img : COLOR3; // 1 to indicate sRGB (SDR) content
};

PS_INPUT main (VS_INPUT input)
{
  PS_INPUT output;

  output.pos  =  float4 (input.pos.xy, 0.f, 1.f);
  output.lum = Luminance.xyzw;

  // Reserved texcoords for sRGB (SDR) content passthrough
  if (all(input.uv <= -4096.0f))
  {
    output.uv.x = (input.uv.x == -4096.0f ? 0.0f : 1.0f);
    output.uv.y = (input.uv.y == -4096.0f ? 0.0f : 1.0f);

      output.col      = float4 (1.0f, 1.0f, 1.0f, 1.0f);
    output.srgb_img = 1.0f;
    output.hdr_img  = 0.0f;
  }

  // Reserved texcoords for HDR content passthrough
  else if (all (input.uv <= -1024.0f))
  {
    output.uv.x = (input.uv.x == -1024.0f ? 0.0f : 1.0f);
    output.uv.y = (input.uv.y == -1024.0f ? 0.0f : 1.0f);

    output.col      = float4 (1.0f, 1.0f, 1.0f, 1.0f);
    output.srgb_img = 0.0f;
    output.hdr_img  = 1.0f;
  }

  else
  {
    output.uv       = input.uv; // Texture coordinates
    output.col      = float4 (1.0f, 1.0f, 1.0f, 1.0f);
    output.srgb_img = 0.0f;
    output.hdr_img  = 0.0f;
  }

  return output;
}

#endif