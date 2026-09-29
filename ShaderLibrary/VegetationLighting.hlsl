#ifndef LOOGA_VEGETATION_LIGHTING_INCLUDED
#define LOOGA_VEGETATION_LIGHTING_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

half3 LoogaSampleSH(half3 value, half3 normal)
{
    return SampleSHPixel(value, normal) * _LoogaLightmapColor.rgb;
}
half3 LoogaSampleLightmap(float2 uv, float2 dynamicUV, half3 normal)
{
    return SampleLightmap(uv, dynamicUV, normal) * _LoogaLightmapColor.rgb;
}
#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
half3 LoogaSampleProbe(half3 value, float3 position, float3 normal, float3 view, float2 screen, float4 vertexOcclusion, out float4 occlusion)
{
    return SampleProbeVolumePixel(value, position, normal, view, screen, vertexOcclusion, occlusion) * _LoogaLightmapColor.rgb;
}
half3 LoogaSampleProbe(half3 value, float3 position, float3 normal, float3 view, float2 screen)
{
    return SampleProbeVolumePixel(value, position, normal, view, screen) * _LoogaLightmapColor.rgb;
}
#define SampleProbeVolumePixel LoogaSampleProbe
#endif
#define SampleSHPixel LoogaSampleSH
#define SampleLightmap LoogaSampleLightmap
#endif
