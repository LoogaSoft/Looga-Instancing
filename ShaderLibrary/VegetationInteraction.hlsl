#ifndef LOOGA_VEGETATION_INTERACTION_INCLUDED
#define LOOGA_VEGETATION_INTERACTION_INCLUDED
float4 _LoogaInteractionSpheres[16];
float4 _LoogaInteractionPreviousSpheres[16];
float4 _LoogaInteractionStrengths[16];
float4 _LoogaInteractionPreviousStrengths[16];
int _LoogaInteractionCount;
int _LoogaInteractionPreviousCount;

float2 VegetationInteraction(float3 pivot, bool previous)
{
    float3 push = 0;
    int count = min(16, previous ? _LoogaInteractionPreviousCount : _LoogaInteractionCount);
    for (int i = 0; i < count; i++)
    {
        float4 sphere = previous ? _LoogaInteractionPreviousSpheres[i] : _LoogaInteractionSpheres[i];
        float strength = previous ? _LoogaInteractionPreviousStrengths[i].x : _LoogaInteractionStrengths[i].x;
        float3 delta = pivot - sphere.xyz;
        float falloff = saturate(1 - length(delta) / max(sphere.w, 0.001));
        delta.y = 0;
        push += delta / max(length(delta), 0.001) * falloff * falloff * strength;
    }
    float3 local = previous ? mul((float3x3)UNITY_PREV_MATRIX_I_M, push) : mul((float3x3)UNITY_MATRIX_I_M, push);
    return local.xz / max(length(local.xz), 1) * max(_InteractionParams.x, 0);
}
#endif
