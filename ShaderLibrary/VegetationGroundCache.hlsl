#ifndef LOOGA_GROUND_CACHE_INCLUDED
#define LOOGA_GROUND_CACHE_INCLUDED
#if defined(LOOGA_GROUND_CACHE)
int _LoogaGroundCacheCount;
float4 _LoogaGroundCacheRegions[16];
float4 _LoogaGroundCacheSlots[16];
float4 _LoogaGroundCacheInfo;
StructuredBuffer<uint> _LoogaGroundCacheTable;
Texture2DArray<float4> _LoogaGroundCacheAlbedo;
Texture2DArray<float4> _LoogaGroundCacheNormal;
Texture2DArray<float4> _LoogaGroundCacheEmission;
SamplerState sampler_LoogaGroundCache_linear_clamp;
bool VegetationGroundCache(float2 world, out float4 color, out float4 normal, out float4 emission, out bool worldNormal)
{
    color = normal = emission = 0;
    worldNormal = false;
    float4 info = _LoogaGroundCacheInfo;
    if (info.y < 1 || info.y > 256 || info.z < 0 || info.z > 8) return false;
    uint entries, stride;
    _LoogaGroundCacheTable.GetDimensions(entries, stride);
    uint textureWidth, textureHeight, textureLayers;
    _LoogaGroundCacheAlbedo.GetDimensions(textureWidth, textureHeight, textureLayers);
    [loop] for (int i = 0; i < min(_LoogaGroundCacheCount, 16); i++)
    {
        float4 slot = _LoogaGroundCacheSlots[i];
        if (slot.x == 0) continue;
        float2 uv = (world - _LoogaGroundCacheRegions[i].xy) / _LoogaGroundCacheRegions[i].zw;
        if (any(uv < 0) || any(uv > 1)) continue;
        [loop] for (uint mip = 0; mip < 9; mip++)
        {
            if (mip > (uint)info.z) break;
            uint width = (uint)info.y >> mip;
            if (width == 0) break;
            float2 grid = clamp(uv, 0, 0.99999994) * width;
            uint2 page = (uint2)floor(grid);
            uint offset = ((uint)info.y * (uint)info.y * 4 - width * width * 4) / 3;
            uint localAddress = offset + page.y * width + page.x;
            uint address = (uint)slot.y + localAddress;
            if (localAddress >= (uint)slot.z || address >= entries) break;
            uint entry = _LoogaGroundCacheTable[address];
            if (entry == 0 || entry > textureLayers) continue;
            float3 location = float3((frac(grid) * 128 + 4) / 136, entry - 1);
            color = _LoogaGroundCacheAlbedo.SampleLevel(sampler_LoogaGroundCache_linear_clamp, location, 0);
            normal = _LoogaGroundCacheNormal.SampleLevel(sampler_LoogaGroundCache_linear_clamp, location, 0);
            emission = _LoogaGroundCacheEmission.SampleLevel(sampler_LoogaGroundCache_linear_clamp, location, 0);
            if (info.w != 0) normal.xyz = normal.xyz * 2 - 1;
            worldNormal = slot.w != 0;
            return true;
        }
    }
    return false;
}
#else
bool VegetationGroundCache(float2 world, out float4 color, out float4 normal, out float4 emission, out bool worldNormal)
{
    color = normal = emission = 0;
    worldNormal = false;
    return false;
}
#endif
bool VegetationGroundCache(float2 world, out float4 color, out float4 normal, out float4 emission)
{
    bool worldNormal;
    return VegetationGroundCache(world, color, normal, emission, worldNormal);
}
#endif
