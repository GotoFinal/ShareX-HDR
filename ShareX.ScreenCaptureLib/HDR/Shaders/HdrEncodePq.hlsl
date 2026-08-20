Texture2D<float4> SourceTexture : register(t0);
RWTexture2D<uint4> PackedPq : register(u0);
RWTexture2D<float2> GroupStats : register(u1);

cbuffer EncodeConstants : register(b0)
{
    float MasteringMaximumNits;
    uint MaximumSample;
    uint ImageWidth;
    uint ImageHeight;
};

groupshared float GroupMaximum[64];
groupshared float GroupLuminance[64];

float SanitizeLinear(float value)
{
    bool finite = (asuint(value) & 0x7F800000u) != 0x7F800000u;
    return finite ? value : 0.0f;
}

float3 ConvertRec709ToRec2020(float3 color)
{
    return float3(
        dot(color, float3(0.6274039f, 0.3292830f, 0.0433131f)),
        dot(color, float3(0.0690973f, 0.9195404f, 0.0113623f)),
        dot(color, float3(0.0163914f, 0.0880133f, 0.8955953f)));
}

float3 EncodePq(float3 nits)
{
    const float m1 = 2610.0f / 16384.0f;
    const float m2 = 2523.0f / 32.0f;
    const float c1 = 3424.0f / 4096.0f;
    const float c2 = 2413.0f / 128.0f;
    const float c3 = 2392.0f / 128.0f;
    float3 luminancePower = pow(saturate(nits / 10000.0f), m1);
    return pow(
        (c1 + c2 * luminancePower) / (1.0f + c3 * luminancePower),
        m2);
}

[numthreads(8, 8, 1)]
void EncodeMain(
    uint3 dispatchThreadId : SV_DispatchThreadID,
    uint3 groupId : SV_GroupID,
    uint groupIndex : SV_GroupIndex)
{
    bool inBounds = dispatchThreadId.x < ImageWidth && dispatchThreadId.y < ImageHeight;
    float maximum = 0.0f;
    float luminance = 0.0f;

    if (inBounds)
    {
        float4 source = SourceTexture.Load(int3(dispatchThreadId.xy, 0));
        float alpha = saturate(SanitizeLinear(source.a));
        float3 color = float3(
            SanitizeLinear(source.r),
            SanitizeLinear(source.g),
            SanitizeLinear(source.b));

        color = alpha > 0.0f ? color / alpha : 0.0f;
        float3 nits = clamp(
            ConvertRec709ToRec2020(color) * 80.0f,
            0.0f,
            MasteringMaximumNits);
        maximum = max(nits.r, max(nits.g, nits.b));
        luminance = dot(nits, float3(0.2627f, 0.6780f, 0.0593f));
        uint3 pq = (uint3)round(saturate(EncodePq(nits)) * MaximumSample);
        uint encodedAlpha = (uint)round(alpha * MaximumSample);
        PackedPq[dispatchThreadId.xy] = uint4(pq, encodedAlpha);
    }

    GroupMaximum[groupIndex] = maximum;
    GroupLuminance[groupIndex] = luminance;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint offset = 32; offset > 0; offset >>= 1)
    {
        if (groupIndex < offset)
        {
            GroupMaximum[groupIndex] = max(
                GroupMaximum[groupIndex],
                GroupMaximum[groupIndex + offset]);
            GroupLuminance[groupIndex] += GroupLuminance[groupIndex + offset];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        GroupStats[groupId.xy] = float2(GroupMaximum[0], GroupLuminance[0]);
    }
}
