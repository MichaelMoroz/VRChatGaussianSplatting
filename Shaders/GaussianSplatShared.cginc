#ifndef __GS_SHARED_CGINC__
#define __GS_SHARED_CGINC__

struct SplatData {
    float3 mean;
    float3 scale;
    float4 quat;
    float4 color;
    uint id;
    bool valid;
};

uint2 GetBlockCoord(uint index, uint mask, uint shift)
{
    uint blockIndex = index >> 4;
    uint blockX = blockIndex & mask;
    uint blockY = blockIndex >> shift;
    return uint2((blockX << 2) | (index & 3u), (blockY << 2) | ((index >> 2) & 3u));
}

#endif