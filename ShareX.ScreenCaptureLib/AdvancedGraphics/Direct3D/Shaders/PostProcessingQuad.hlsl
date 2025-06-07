#include "ShaderInputStructure.hlsl"

// PS_INPUT VxQuadEntry(VS_INPUT v)
// {
//     PS_INPUT vout;
//
//     vout.TexCoord = v.TexCoord;
//     vout.pos = float4(v.Position, 0.0f, 1.0f);
//
//     return vout;
// }

cbuffer vertexBuffer : register (b0)
{
    float4 Luminance;
};

PS_INPUT main(VS_INPUT input)
{
    PS_INPUT output;

    // output.pos  = mul ( ProjectionMatrix,
    //                       float4 (input.Position.xy, 0.f, 1.f) );
    output.pos = float4(input.Position, 0.f, 1.f);
    output.lum = Luminance.xyzw;

    // Reserved texcoords for sRGB (SDR) content passthrough
    if (all(input.TexCoord <= -4096.0f))
    {
        output.uv.x = (input.TexCoord.x == -4096.0f ? 0.0f : 1.0f);
        output.uv.y = (input.TexCoord.y == -4096.0f ? 0.0f : 1.0f);

        output.srgb_img = 1.0f;
        output.hdr_img  = 0.0f;
    }

    // Reserved texcoords for HDR content passthrough
    else if (all (input.TexCoord <= -1024.0f))
    {
        output.uv.x = (input.TexCoord.x == -1024.0f ? 0.0f : 1.0f);
        output.uv.y = (input.TexCoord.y == -1024.0f ? 0.0f : 1.0f);

        output.srgb_img = 0.0f;
        output.hdr_img  = 1.0f;
    }

    else
    {
        output.uv       = input.TexCoord; // Texture coordinates
        output.srgb_img = 0.0f;
        output.hdr_img  = 0.0f;
    }

    return output;
}
