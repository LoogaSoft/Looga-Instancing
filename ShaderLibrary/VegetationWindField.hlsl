#ifndef LOOGA_VEGETATION_WIND_FIELD_INCLUDED
#define LOOGA_VEGETATION_WIND_FIELD_INCLUDED
float4 _LoogaFieldDirection;
float4 _LoogaFieldGust;
float4 _LoogaFieldWave;
float4 _LoogaFieldPreviousDirection;
float4 _LoogaFieldPreviousGust;
float4 _LoogaFieldPreviousWave;
int _LoogaFieldVolumeCount;
int _LoogaFieldPreviousVolumeCount;
float4 _LoogaFieldPositions[8];
float4 _LoogaFieldDirections[8];
float4 _LoogaFieldPreviousPositions[8];
float4 _LoogaFieldPreviousDirections[8];

float VegetationFieldHash(float2 cell)
{
    float3 value = frac(cell.xyx * 0.1031);
    value += dot(value, value.yzx + 33.33);
    return frac((value.x + value.y) * value.z);
}

// Smooth value noise in [0, 1].
float VegetationFieldNoise(float2 position)
{
    float2 cell = floor(position);
    float2 f = position - cell;
    f = f * f * (3 - 2 * f);
    float bottom = lerp(VegetationFieldHash(cell), VegetationFieldHash(cell + float2(1, 0)), f.x);
    float top = lerp(VegetationFieldHash(cell + float2(0, 1)), VegetationFieldHash(cell + float2(1, 1)), f.x);
    return lerp(bottom, top, f.y);
}

// Gust fronts in [0, 1] that travel downwind. The noise moves with the wind, so the fronts curve and change strength
// along their length but keep their shape while they travel.
float VegetationFieldGust(float2 position, float2 flow, float time, float frequency, float wavelength)
{
    float speed = frequency * wavelength / 6.283185;
    float2 drift = position - flow * (speed * time);
    float bend = VegetationFieldNoise(drift / (wavelength * 2.3));
    float patch = VegetationFieldNoise(drift / (wavelength * 3.7) + 17.3);
    float front = 0.5 + 0.5 * sin((dot(drift, flow) / wavelength + bend * 0.6) * 6.283185);
    return front * lerp(0.35, 1, patch);
}

// World direction at the pivot after the local volumes.
float3 VegetationFieldWorldDirection(float3 direction, float3 pivot, bool previous)
{
    int count = previous ? _LoogaFieldPreviousVolumeCount : _LoogaFieldVolumeCount;
    for (int i = 0; i < count; i++)
    {
        float4 sphere = previous ? _LoogaFieldPreviousPositions[i] : _LoogaFieldPositions[i];
        float4 local = previous ? _LoogaFieldPreviousDirections[i] : _LoogaFieldDirections[i];
        float falloff = saturate(1 - distance(pivot, sphere.xyz) / max(sphere.w, 0.001));
        falloff = falloff * falloff * (3 - 2 * falloff) * saturate(local.w);
        direction = lerp(direction, local.xyz, falloff);
    }
    return direction;
}

// Returns the local bend direction. Strength scales the material amplitudes. Lean is the part of the trunk
// amplitude that bends steadily downwind; it is zero without a field.
float2 VegetationField(float2 materialDirection, float3 pivot, float time, bool previous, out float strength, out float lean)
{
    strength = 1;
    lean = 0;
    float4 field = previous ? _LoogaFieldPreviousDirection : _LoogaFieldDirection;
    if (field.w == 0) return materialDirection;
    float4 gust = previous ? _LoogaFieldPreviousGust : _LoogaFieldGust;
    float4 wave = previous ? _LoogaFieldPreviousWave : _LoogaFieldWave;
    float3 worldDirection = VegetationFieldWorldDirection(field.xyz, pivot, previous);
    float3 localDirection = previous ? mul((float3x3)UNITY_PREV_MATRIX_I_M, worldDirection) : mul((float3x3)UNITY_MATRIX_I_M, worldDirection);
    float2 direction = localDirection.xz / max(length(localDirection.xz), 0.001);
    float2 flow = worldDirection.xz / max(length(worldDirection.xz), 0.001);
    float front = VegetationFieldGust(pivot.xz, flow, time, gust.z, max(wave.x, 1));
    strength = gust.x * lerp(1, front, gust.y);
    lean = saturate(wave.y);
    return direction;
}

// The extra bend of the passing gust front at the pivot in [0, 1], for surface effects. Flow is the world XZ wind
// direction. Both are zero without a field.
float VegetationGust(float3 pivot, float time, out float2 flow)
{
    flow = 0;
    if (_LoogaFieldDirection.w == 0) return 0;
    float3 worldDirection = VegetationFieldWorldDirection(_LoogaFieldDirection.xyz, pivot, false);
    flow = worldDirection.xz / max(length(worldDirection.xz), 0.001);
    float front = VegetationFieldGust(pivot.xz, flow, time, _LoogaFieldGust.z, max(_LoogaFieldWave.x, 1));
    return saturate(_LoogaFieldGust.x) * saturate(_LoogaFieldGust.y) * front;
}
#endif
