Texture2D<float4> AnalysisSourceTexture : register(t0);
RWTexture2D<uint> HeadroomBits : register(u0);

cbuffer HeadroomConstants : register(b0)
{
    float HeadroomThreshold;
    uint ImageWidth;
    uint ImageHeight;
    uint WordsPerRow;
};

float SanitizeAnalysisLinear(float value)
{
    // Test exponent bits directly so compiler finite-math assumptions cannot
    // fold away the NaN/infinity check.
    bool finite = (asuint(value) & 0x7F800000u) != 0x7F800000u;
    return finite && value > 0.0f ? value : 0.0f;
}

float GetAnalysisRec2020Max(float3 color)
{
    float rec2020Red = dot(color, float3(0.6274039f, 0.3292830f, 0.0433131f));
    float rec2020Green = dot(color, float3(0.0690973f, 0.9195404f, 0.0113623f));
    float rec2020Blue = dot(color, float3(0.0163914f, 0.0880133f, 0.8955953f));
    return max(rec2020Red, max(rec2020Green, rec2020Blue));
}

[numthreads(8, 8, 1)]
void HeadroomMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint wordX = dispatchThreadId.x;
    uint y = dispatchThreadId.y;
    if (wordX >= WordsPerRow || y >= ImageHeight)
    {
        return;
    }

    uint firstX = wordX * 32;
    uint bits = 0;

    [unroll]
    for (uint bit = 0; bit < 32; bit++)
    {
        uint x = firstX + bit;
        if (x >= ImageWidth)
        {
            break;
        }

        float4 source = AnalysisSourceTexture.Load(int3(x, y, 0));
        float3 color = float3(
            SanitizeAnalysisLinear(source.r),
            SanitizeAnalysisLinear(source.g),
            SanitizeAnalysisLinear(source.b));
        if (GetAnalysisRec2020Max(color) > HeadroomThreshold)
        {
            bits |= 1u << bit;
        }
    }

    HeadroomBits[uint2(wordX, y)] = bits;
}
