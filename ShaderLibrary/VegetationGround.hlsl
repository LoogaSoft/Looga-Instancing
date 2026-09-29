#ifndef LOOGA_VEGETATION_GROUND_INCLUDED
#define LOOGA_VEGETATION_GROUND_INCLUDED
#include "VegetationGroundData.hlsl"
void VegetationGroundConform(inout float3 local, float3 pivot, bool previous)
{
    if (_GroundParams.z <= 0) return;
    float4x4 model = previous ? UNITY_PREV_MATRIX_M : UNITY_MATRIX_M;
    float3 world = mul(model, float4(local, 1)).xyz;
    float ground, originHeight;
    float3 tint;
    if (!VegetationGroundSampleAtTime(world.xz, ground, tint, previous) || !VegetationGroundSampleAtTime(pivot.xz, originHeight, tint, previous)) return;
    float3 deltaWorld = float3(0, ground - originHeight, 0);
    float3 delta = previous ? mul((float3x3)UNITY_PREV_MATRIX_I_M, deltaWorld) : mul((float3x3)UNITY_MATRIX_I_M, deltaWorld);
    local += delta * min(1, _GroundParams.z / max(length(delta), 0.00001));
}
#include "VegetationGroundCache.hlsl"

void VegetationGroundBlend(float3 world, inout half3 normalWS, inout SurfaceData surface)
{
    if (_GroundParams.x <= 0 && _GroundParams.y <= 0) return;
    float height;
    float3 tint;
    if (!VegetationGroundSample(world.xz, height, tint)) return;
    float weight = saturate(1 - abs(world.y - height) / max(_GroundParams.w, 0.001));
    float4 cachedColor, cachedNormal, cachedEmission;
    if (VegetationGroundCache(world.xz, cachedColor, cachedNormal, cachedEmission))
    {
        tint = cachedColor.rgb;
        float blend = weight * saturate(_GroundParams.x);
        surface.smoothness = lerp(surface.smoothness, cachedColor.a, blend);
        surface.occlusion = lerp(surface.occlusion, cachedNormal.a, blend);
        surface.emission = lerp(surface.emission, cachedEmission.rgb, blend);
        surface.metallic = lerp(surface.metallic, cachedEmission.a, blend);
    }
    surface.albedo = lerp(surface.albedo, tint, weight * saturate(_GroundParams.x));

}

void VegetationGroundFrame(float3 local, inout float3 normalOS, inout float4 tangentOS)
{
    if (_GroundParams.y <= 0) return;
    float3 world = TransformObjectToWorld(local);
    float height;
    float3 tint;
    if (!VegetationGroundSample(world.xz, height, tint)) return;
    float weight = saturate(1 - abs(world.y - height) / max(_GroundParams.w, 0.001)) * saturate(_GroundParams.y);
    float3 groundNormal = VegetationGroundNormal(world.xz, height);
    float4 cachedColor, cachedNormal, cachedEmission;
    bool worldNormal;
    if (VegetationGroundCache(world.xz, cachedColor, cachedNormal, cachedEmission, worldNormal) && dot(cachedNormal.xyz, cachedNormal.xyz) > 0.000001)
    {
        if (worldNormal)
        {
            groundNormal = normalize(cachedNormal.xyz);
        }
        else
        {
            float3 axis = abs(groundNormal.z) < 0.99 ? float3(0,0,1) : float3(0,1,0);
            float3 tangent = normalize(cross(groundNormal, axis));
            float3 bitangent = -normalize(cross(groundNormal, tangent));
            groundNormal = normalize(mul(cachedNormal.xyz, float3x3(tangent, bitangent, groundNormal)));
        }
    }
    float3 blended = lerp(TransformObjectToWorldNormal(normalOS), groundNormal, weight);
    float3 normal = dot(blended, blended) > 0.000001 ? normalize(blended) : groundNormal;
    normalOS = TransformWorldToObjectNormal(normal);
    float3 tangent = tangentOS.xyz - normalOS * dot(normalOS, tangentOS.xyz);
    if (dot(tangent, tangent) < 0.000001)
    {
        tangent = cross(normalOS, abs(normalOS.y) < 0.9 ? float3(0,1,0) : float3(1,0,0));
    }
    tangentOS.xyz = normalize(tangent);
}
#endif
