// Keep this curve in sync with HdrToSdrToneMapper.ReferenceWhiteToneMapper.

Texture2D<float4> SourceTexture : register(t0);
Texture2D<float> ToneMapMask : register(t1);

cbuffer ToneMapConstants : register(b0)
{
    float PaperWhiteScRgb;
    float InputMaximum;
    float OutputWhite;
    float CurveXA;
    float CurveXB;
    float CurveYA;
    float CurveYB;
    uint UseToneMapMask;
    uint PreserveAlpha;
    uint Padding0;
    uint Padding1;
    uint Padding2;
};

struct VertexOutput
{
    float4 Position : SV_Position;
};

VertexOutput VertexMain(uint vertexId : SV_VertexID)
{
    float2 corner = float2((vertexId << 1) & 2, vertexId & 2);
    VertexOutput output;
    output.Position = float4(corner * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return output;
}

float SanitizeLinear(float value)
{
    return isfinite(value) && value > 0.0f ? value : 0.0f;
}

float GetRec2020Max(float3 color)
{
    float rec2020Red = dot(color, float3(0.6274039f, 0.3292830f, 0.0433131f));
    float rec2020Green = dot(color, float3(0.0690973f, 0.9195404f, 0.0113623f));
    float rec2020Blue = dot(color, float3(0.0163914f, 0.0880133f, 0.8955953f));
    return max(rec2020Red, max(rec2020Green, rec2020Blue));
}

float GetToneMapGain(float input)
{
    if (input <= 0.0f) return 0.0f;
    if (input <= 1.0f) return OutputWhite;
    if (input >= InputMaximum) return 1.0f / input;

    float position;

    if (abs(CurveXA) < 0.00001f)
    {
        position = (input - 1.0f) / CurveXB;
    }
    else
    {
        float discriminant = CurveXB * CurveXB - 4.0f * CurveXA * (1.0f - input);
        position = (-CurveXB + sqrt(max(discriminant, 0.0f))) / (2.0f * CurveXA);
    }

    position = saturate(position);
    float output = OutputWhite + position * (CurveYB + position * CurveYA);
    return output / input;
}

float3 EncodeSrgb(float3 linearColor)
{
    float3 low = linearColor * 12.92f;
    float3 high = 1.055f * pow(linearColor, 1.0f / 2.4f) - 0.055f;
    return lerp(high, low, step(linearColor, 0.0031308f));
}

float4 PixelMain(VertexOutput input) : SV_Target
{
    int2 pixel = int2(input.Position.xy);
    float4 source = SourceTexture.Load(int3(pixel, 0));
    float alpha = saturate(SanitizeLinear(source.a));
    float3 color = float3(
        SanitizeLinear(source.r),
        SanitizeLinear(source.g),
        SanitizeLinear(source.b));

    if (PreserveAlpha != 0)
    {
        if (alpha <= 0.0f)
        {
            color = 0.0f;
        }
        else if (alpha < 1.0f)
        {
            color /= alpha;
        }
    }

    color /= PaperWhiteScRgb;
    float maxRgb = GetRec2020Max(color);
    float toneMapAmount = UseToneMapMask != 0
        ? ToneMapMask.Load(int3(pixel, 0))
        : 1.0f;

    if (toneMapAmount > 0.0f)
    {
        float gain = GetToneMapGain(maxRgb);
        color *= 1.0f + (gain - 1.0f) * toneMapAmount;
    }

    color = saturate(color);
    return float4(EncodeSrgb(color), PreserveAlpha != 0 ? alpha : 1.0f);
}
