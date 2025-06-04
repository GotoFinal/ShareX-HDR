#include "ShaderInputStructure.hlsl"

VS_OUTPUT VxQuadEntry(VS_INPUT v)
{
    VS_OUTPUT vout;

    vout.TexCoord = v.TexCoord;
    vout.Position = float4(v.Position, 0.0f, 1.0f);

    return vout;
}

VS_OUTPUT main(VS_INPUT v)
{
    return VxQuadEntry(v);
}
