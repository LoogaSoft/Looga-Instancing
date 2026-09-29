#ifndef LOOGA_VEGETATION_DEFORMATION_INCLUDED
#define LOOGA_VEGETATION_DEFORMATION_INCLUDED
// Wind, interaction and ground deformation for materials with the LoogaInstanceDeformation = Vegetation tag.
// Include it after the URP surface input. The including shader declares these material properties:
// _WindParams, _WindDirection, _WindTime, _BranchWind, _LeafWind, _VertexWindMask, _InteractionParams, _GroundParams
// and _LoogaInstanceData. InstancePrototype reads the same properties to extend the instance bounds.
#include "VegetationWindField.hlsl"
#include "VegetationInteraction.hlsl"
#include "VegetationGround.hlsl"

// Displacement stays within the sum of the three material amplitudes.
float2 VegetationBendAtTime(inout float3 position, float4 mask, float time, float3 pivot, bool previous = false)
{
    VegetationGroundConform(position, pivot, previous);
    float height = max(abs(_WindParams.z), 0.001);
    float h = (position.y - _WindParams.w) / height;
    float weight = saturate(h);
    float phase = dot(pivot.xz, float2(0.173, 0.317)) + time * _WindParams.y + _LoogaInstanceData.x * 6.283185;
    float wave = sin(phase) * 0.7 + sin(phase * 1.73 + 1.1) * 0.3;
    float2 direction = _WindDirection.xy / max(length(_WindDirection.xy), 0.001);
    float strength, lean;
    direction = VegetationField(direction, pivot, time, previous, strength, lean);
    float3 weights = strength * lerp(float3(1, 1, 1), saturate(mask.rgb), saturate(_VertexWindMask));
    float trunk = _WindParams.x * (lean + (1 - lean) * wave) * weights.r;
    float branchPhase = phase * _BranchWind.y + h * _BranchWind.z;
    float leafPhase = phase * _LeafWind.y + mask.a * 6.283185 + h * _LeafWind.z;
    float branch = _BranchWind.x * sin(branchPhase) * weights.g;
    float leaf = _LeafWind.x * sin(leafPhase) * weights.b;
    float2 crossDirection = float2(-direction.y, direction.x);
    float2 bend = direction * (trunk + branch) + crossDirection * leaf + VegetationInteraction(pivot, previous);
    position.xz += bend * weight * weight;
    float2 derivative = h > 0 && h < 1 ? bend * (2 * weight / height) : float2(0, 0);
    derivative += direction * (_BranchWind.x * cos(branchPhase) * _BranchWind.z * weights.g * weight * weight / height);
    derivative += crossDirection * (_LeafWind.x * cos(leafPhase) * _LeafWind.z * weights.b * weight * weight / height);
    return derivative;
}

float2 VegetationBend(inout float3 position, float4 mask)
{
    float time = _WindTime >= 0 ? _WindTime : _Time.y;
    return VegetationBendAtTime(position, mask, time, TransformObjectToWorld(float3(0, 0, 0)));
}

void VegetationNormal(inout float3 normal, float2 derivative)
{
    normal.y -= dot(normal.xz, derivative);
    normal = normalize(normal);
}

void VegetationTangent(inout float4 tangent, float2 derivative)
{
    tangent.xz += derivative * tangent.y;
    tangent.xyz = normalize(tangent.xyz);
}

// Brightens and glosses the upper part of the plant while a gust front bends it, like the light bands that pass over
// a meadow. The front is the same one that VegetationBend uses. Sheen: brightness, gloss and view dependence, each 0 to
// 1. With full view dependence, the sheen shows only where the plants bend away from the camera. Zero values and a
// missing wind field change nothing. Call it in the fragment stage after the instance ID setup.
void VegetationGustSheen(float3 positionWS, float4 sheen, inout SurfaceData surface)
{
    if (sheen.x <= 0 && sheen.y <= 0) return;
    float2 flow;
    float time = _WindTime >= 0 ? _WindTime : _Time.y;
    float gust = VegetationGust(TransformObjectToWorld(float3(0, 0, 0)), time, flow);
    if (gust <= 0) return;
    float2 away = positionWS.xz - GetCameraPositionWS().xz;
    float facing = saturate(dot(flow, away / max(length(away), 0.001)));
    float height = saturate((TransformWorldToObject(positionWS).y - _WindParams.w) / max(abs(_WindParams.z), 0.001));
    float amount = gust * lerp(1, facing, saturate(sheen.z)) * smoothstep(0.15, 0.9, height);
    surface.albedo = lerp(surface.albedo, min(1, surface.albedo * 1.8 + 0.04), amount * saturate(sheen.x));
    surface.smoothness = lerp(surface.smoothness, max(surface.smoothness, 0.6), amount * saturate(sheen.y));
}
#endif
