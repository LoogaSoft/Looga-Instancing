#ifndef LOOGA_VEGETATION_GROUND_DATA_INCLUDED
#define LOOGA_VEGETATION_GROUND_DATA_INCLUDED
int _LoogaGroundCount;
float4 _LoogaGroundOrigins[16];
float4 _LoogaGroundSizes[16];
float4 _LoogaGroundTints[16];
SamplerState sampler_LoogaGround_linear_clamp;
Texture2D<float4> _LoogaGroundHeight0;
Texture2D<float4> _LoogaGroundColor0;
Texture2D<float4> _LoogaGroundHeight1;
Texture2D<float4> _LoogaGroundColor1;
Texture2D<float4> _LoogaGroundHeight2;
Texture2D<float4> _LoogaGroundColor2;
Texture2D<float4> _LoogaGroundHeight3;
Texture2D<float4> _LoogaGroundColor3;
Texture2D<float4> _LoogaGroundHeight4;
Texture2D<float4> _LoogaGroundColor4;
Texture2D<float4> _LoogaGroundHeight5;
Texture2D<float4> _LoogaGroundColor5;
Texture2D<float4> _LoogaGroundHeight6;
Texture2D<float4> _LoogaGroundColor6;
Texture2D<float4> _LoogaGroundHeight7;
Texture2D<float4> _LoogaGroundColor7;
Texture2D<float4> _LoogaGroundHeight8;
Texture2D<float4> _LoogaGroundColor8;
Texture2D<float4> _LoogaGroundHeight9;
Texture2D<float4> _LoogaGroundColor9;
Texture2D<float4> _LoogaGroundHeight10;
Texture2D<float4> _LoogaGroundColor10;
Texture2D<float4> _LoogaGroundHeight11;
Texture2D<float4> _LoogaGroundColor11;
Texture2D<float4> _LoogaGroundHeight12;
Texture2D<float4> _LoogaGroundColor12;
Texture2D<float4> _LoogaGroundHeight13;
Texture2D<float4> _LoogaGroundColor13;
Texture2D<float4> _LoogaGroundHeight14;
Texture2D<float4> _LoogaGroundColor14;
Texture2D<float4> _LoogaGroundHeight15;
Texture2D<float4> _LoogaGroundColor15;
float GroundHeightTexture(int index, float2 uv)
{
    float value = 0;
    if (index == 0) value = _LoogaGroundHeight0.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 1) value = _LoogaGroundHeight1.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 2) value = _LoogaGroundHeight2.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 3) value = _LoogaGroundHeight3.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 4) value = _LoogaGroundHeight4.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 5) value = _LoogaGroundHeight5.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 6) value = _LoogaGroundHeight6.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 7) value = _LoogaGroundHeight7.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 8) value = _LoogaGroundHeight8.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 9) value = _LoogaGroundHeight9.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 10) value = _LoogaGroundHeight10.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 11) value = _LoogaGroundHeight11.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 12) value = _LoogaGroundHeight12.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 13) value = _LoogaGroundHeight13.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 14) value = _LoogaGroundHeight14.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 15) value = _LoogaGroundHeight15.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    return value;
}
Texture2D<float4> _LoogaGroundPreviousHeight0;
Texture2D<float4> _LoogaGroundPreviousHeight1;
Texture2D<float4> _LoogaGroundPreviousHeight2;
Texture2D<float4> _LoogaGroundPreviousHeight3;
Texture2D<float4> _LoogaGroundPreviousHeight4;
Texture2D<float4> _LoogaGroundPreviousHeight5;
Texture2D<float4> _LoogaGroundPreviousHeight6;
Texture2D<float4> _LoogaGroundPreviousHeight7;
Texture2D<float4> _LoogaGroundPreviousHeight8;
Texture2D<float4> _LoogaGroundPreviousHeight9;
Texture2D<float4> _LoogaGroundPreviousHeight10;
Texture2D<float4> _LoogaGroundPreviousHeight11;
Texture2D<float4> _LoogaGroundPreviousHeight12;
Texture2D<float4> _LoogaGroundPreviousHeight13;
Texture2D<float4> _LoogaGroundPreviousHeight14;
Texture2D<float4> _LoogaGroundPreviousHeight15;
float GroundPreviousHeightTexture(int index, float2 uv)
{
    float value = 0;
    if (index == 0) value = _LoogaGroundPreviousHeight0.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 1) value = _LoogaGroundPreviousHeight1.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 2) value = _LoogaGroundPreviousHeight2.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 3) value = _LoogaGroundPreviousHeight3.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 4) value = _LoogaGroundPreviousHeight4.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 5) value = _LoogaGroundPreviousHeight5.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 6) value = _LoogaGroundPreviousHeight6.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 7) value = _LoogaGroundPreviousHeight7.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 8) value = _LoogaGroundPreviousHeight8.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 9) value = _LoogaGroundPreviousHeight9.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 10) value = _LoogaGroundPreviousHeight10.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 11) value = _LoogaGroundPreviousHeight11.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 12) value = _LoogaGroundPreviousHeight12.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 13) value = _LoogaGroundPreviousHeight13.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 14) value = _LoogaGroundPreviousHeight14.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    else if (index == 15) value = _LoogaGroundPreviousHeight15.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).r;
    return value;
}
float3 GroundColorTexture(int index, float2 uv)
{
    float3 value = 1;
    if (index == 0) value = _LoogaGroundColor0.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 1) value = _LoogaGroundColor1.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 2) value = _LoogaGroundColor2.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 3) value = _LoogaGroundColor3.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 4) value = _LoogaGroundColor4.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 5) value = _LoogaGroundColor5.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 6) value = _LoogaGroundColor6.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 7) value = _LoogaGroundColor7.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 8) value = _LoogaGroundColor8.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 9) value = _LoogaGroundColor9.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 10) value = _LoogaGroundColor10.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 11) value = _LoogaGroundColor11.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 12) value = _LoogaGroundColor12.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 13) value = _LoogaGroundColor13.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 14) value = _LoogaGroundColor14.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    else if (index == 15) value = _LoogaGroundColor15.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0).rgb;
    return value;
}
bool VegetationGroundSampleAtTime(float2 world, out float height, out float3 tint, bool previous)
{
    height = 0;
    tint = 0;
    float weight = 0;
    [loop] for (int i = 0; i < min(_LoogaGroundCount, 16); i++)
    {
        float2 uv = (world - _LoogaGroundOrigins[i].xz) / _LoogaGroundSizes[i].xz;
        if (any(uv < 0) || any(uv > 1)) continue;
        float resolution = _LoogaGroundOrigins[i].w;
        float2 sampleUV = (uv * (resolution - 1) + 0.5) / resolution;
        height += _LoogaGroundOrigins[i].y + (previous ? GroundPreviousHeightTexture(i, sampleUV) : GroundHeightTexture(i, sampleUV)) * (65535.0 / 32766.0) * _LoogaGroundSizes[i].y;
        tint += GroundColorTexture(i, uv) * _LoogaGroundTints[i].rgb;
        weight++;
    }
    if (weight <= 0) return false;
    height /= weight;
    tint /= weight;
    return true;
}
bool VegetationGroundSample(float2 world, out float height, out float3 tint)
{
    return VegetationGroundSampleAtTime(world, height, tint, false);
}
float3 VegetationGroundNormal(float2 world, float height)
{
    float left, right, back, front;
    float3 tint;
    if (!VegetationGroundSample(world - float2(0.25, 0), left, tint)) left = height;
    if (!VegetationGroundSample(world + float2(0.25, 0), right, tint)) right = height;
    if (!VegetationGroundSample(world - float2(0, 0.25), back, tint)) back = height;
    if (!VegetationGroundSample(world + float2(0, 0.25), front, tint)) front = height;
    return normalize(float3((left - right) * 2, 1, (back - front) * 2));
}
float4 _LoogaGroundExtraData[16];
Texture2D<float4> _LoogaGroundExtra0;
Texture2D<float4> _LoogaGroundExtra1;
Texture2D<float4> _LoogaGroundExtra2;
Texture2D<float4> _LoogaGroundExtra3;
Texture2D<float4> _LoogaGroundExtra4;
Texture2D<float4> _LoogaGroundExtra5;
Texture2D<float4> _LoogaGroundExtra6;
Texture2D<float4> _LoogaGroundExtra7;
Texture2D<float4> _LoogaGroundExtra8;
Texture2D<float4> _LoogaGroundExtra9;
Texture2D<float4> _LoogaGroundExtra10;
Texture2D<float4> _LoogaGroundExtra11;
Texture2D<float4> _LoogaGroundExtra12;
Texture2D<float4> _LoogaGroundExtra13;
Texture2D<float4> _LoogaGroundExtra14;
Texture2D<float4> _LoogaGroundExtra15;
float4 VegetationGroundExtraTexture(int index, float2 uv)
{
    if (index == 0) return _LoogaGroundExtra0.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 1) return _LoogaGroundExtra1.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 2) return _LoogaGroundExtra2.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 3) return _LoogaGroundExtra3.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 4) return _LoogaGroundExtra4.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 5) return _LoogaGroundExtra5.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 6) return _LoogaGroundExtra6.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 7) return _LoogaGroundExtra7.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 8) return _LoogaGroundExtra8.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 9) return _LoogaGroundExtra9.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 10) return _LoogaGroundExtra10.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 11) return _LoogaGroundExtra11.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 12) return _LoogaGroundExtra12.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 13) return _LoogaGroundExtra13.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 14) return _LoogaGroundExtra14.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    if (index == 15) return _LoogaGroundExtra15.SampleLevel(sampler_LoogaGround_linear_clamp, uv, 0);
    return 1;
}
// Map channels and per-source values remain independent receiver inputs.
bool VegetationGroundExtra(float2 world, out float4 map, out float4 data)
{
    map = data = 0;
    float count = 0;
    [loop] for (int i = 0; i < min(_LoogaGroundCount, 16); i++)
    {
        float2 uv = (world - _LoogaGroundOrigins[i].xz) / _LoogaGroundSizes[i].xz;
        if (any(uv < 0) || any(uv > 1)) continue;
        map += VegetationGroundExtraTexture(i, uv);
        data += _LoogaGroundExtraData[i];
        count++;
    }
    if (count <= 0) return false;
    map /= count;
    data /= count;
    return true;
}
#endif
