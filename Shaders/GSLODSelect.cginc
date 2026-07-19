#ifndef GSLOD_SELECT_INCLUDED
#define GSLOD_SELECT_INCLUDED

// Scene-global LOD chunk resolution: maps a LOD-region output index to a source splat (chunk + object +
// global fused source index) via a 2D mip-pyramid quadtree descent over all objects' concatenated chunks.
// This file is the strippable seam for the LOD feature; the generic source load/dequant/transform lives in
// the including shader.

#define GSLOD_OFFSET_BASE_UINT 4096u

Texture2D _LODChunkSelection;
Texture2D _LODChunkBounds;   // 2D, stacked: rows [0,metaH) = min (xyz, w=splatCount), [metaH,2metaH) = max (xyz, w=globalFileId)
Texture2D _LODChunkRange;    // 2D: rows [0,metaH) = range; optional rows [metaH,2metaH) = density stats
Texture2D _LODFileBase;      // per global file: fusedBaseHi, fusedBaseLo, fileSplatCount, 0 (base = hi*4096 + lo)
Texture2D _LODObjectParams;  // per-object params, texel (col, objectId); col 3 = computed params + active flag
SamplerState sampler_LODChunkSelection;

float4 _LODUnifiedLayout; // x = selection side (POT), y = log2(metaWidth) (metaWidth is POT), z = total chunk count, w = max mip

// metaWidth is POT -> decode chunk index with shift/mask (no % / ÷).
uint GSLODMetaHeight()
{
    uint s = (uint)_LODUnifiedLayout.y;
    uint w = 1u << s;
    return ((uint)_LODUnifiedLayout.z + w - 1u) >> s;
}

uint2 GSLODUnifiedMetaCoord(uint chunkIndex)
{
    uint s = (uint)_LODUnifiedLayout.y;
    return uint2(chunkIndex & ((1u << s) - 1u), chunkIndex >> s);
}

float4 GSLODChunkMin(uint chunkIndex) { return _LODChunkBounds[GSLODUnifiedMetaCoord(chunkIndex)]; }
float4 GSLODChunkMax(uint chunkIndex) { return _LODChunkBounds[GSLODUnifiedMetaCoord(chunkIndex) + uint2(0u, GSLODMetaHeight())]; }

// Discrete computed-LOD level fitting. comp = (enabled, minClusterCount, reusePercent, unused).
uint GSLODComputedOutputTarget(uint lod0Count, uint level)
{
    if (level == 0u) return lod0Count;
    return min((uint)floor((float)lod0Count / exp2((float)level) + 0.5), lod0Count);
}

uint GSLODReusePercentP(float4 comp) { return (uint)round(comp.z <= 0.0 ? 50.0 : clamp(comp.z, 1.0, 99.0)); }

uint GSLODClusterCountP(uint lod0Count, uint level, float4 comp)
{
    if (level == 0u) return lod0Count;
    uint outputCount = GSLODComputedOutputTarget(lod0Count, level);
    uint reuseCount = min((uint)floor((float)outputCount * ((float)GSLODReusePercentP(comp) / 100.0) + 0.5), outputCount);
    uint clusterCount = outputCount - reuseCount;
    uint minClusterCount = (uint)max(1.0, round(comp.y));
    if (clusterCount < minClusterCount || clusterCount >= lod0Count) return 0u;
    return clusterCount;
}

uint GSLODReuseCountP(uint lod0Count, uint level, float4 comp)
{
    if (level == 0u) return lod0Count;
    uint clusterCount = GSLODClusterCountP(lod0Count, level, comp);
    if (clusterCount == 0u) return lod0Count;
    uint outputCount = GSLODComputedOutputTarget(lod0Count, level);
    return min(outputCount - clusterCount, lod0Count);
}

uint GSLODClusterOffsetP(uint lod0Count, uint level, float4 comp)
{
    uint offset = lod0Count;
    [loop]
    for (int cur = 1; cur < 30; cur++)
    {
        if ((uint)cur >= level) break;
        uint cl = GSLODClusterCountP(lod0Count, (uint)cur, comp);
        if (cl == 0u) break;
        offset += cl;
    }
    return offset;
}

bool GSLODResolveComputedLocalSourceUP(uint level, float lod0CountF, float chunkLocalIndex, float4 comp, out uint localSourceIndex)
{
    localSourceIndex = 0u;
    uint outputLocalIndex = (uint)round(chunkLocalIndex);
    uint lod0Count = (uint)round(lod0CountF);
    if (level == 0u)
    {
        if (outputLocalIndex >= lod0Count) return false;
        localSourceIndex = outputLocalIndex; return true;
    }
    uint clusterCount = GSLODClusterCountP(lod0Count, level, comp);
    uint reuseCount = GSLODReuseCountP(lod0Count, level, comp);
    if (clusterCount == 0u || outputLocalIndex >= reuseCount + clusterCount) return false;
    if (outputLocalIndex < reuseCount) { localSourceIndex = outputLocalIndex; return true; }
    localSourceIndex = GSLODClusterOffsetP(lod0Count, level, comp) + (outputLocalIndex - reuseCount);
    return true;
}

// Resolve a LOD-region output index -> chunk + objectId + global fused source index. The descent walks the
// 2D selection pyramid in Z-order (leaf (x,y) -> chunkIndex = y*side + x); selected splats are depth-sorted
// afterward, so only emit-order self-consistency matters.
bool GSLODResolveChunkUnified(uint outputLocalIndex, out uint chunkIndex, out uint objId, out uint globalSourceIndex, out float lodLevel)
{
    chunkIndex = 0u; objId = 0u; globalSourceIndex = 0u; lodLevel = 0.0;

    uint side = (uint)_LODUnifiedLayout.x;
    int maxMip = (int)_LODUnifiedLayout.w;
    uint totalChunks = (uint)_LODUnifiedLayout.z;
    float oli = (float)outputLocalIndex;

    float total = _LODChunkSelection.SampleLevel(sampler_LODChunkSelection, float2(0.5, 0.5), (float)maxMip).x * (float)(side * side);
    if (oli >= total) return false;

    uint x = 0u, y = 0u; float prefix = 0.0;
    [loop]
    for (int mip = maxMip; mip > 0; mip--)
    {
        int childMip = mip - 1;
        uint childSpan = 1u << (uint)childMip;
        float childArea = (float)(childSpan * childSpan);
        float invDim = 1.0 / (float)(side >> (uint)childMip);
        bool placed = false;
        [unroll]
        for (uint q = 0u; q < 4u; q++)
        {
            uint dx = q & 1u; uint dy = (q >> 1) & 1u;
            uint cx = x + dx * childSpan; uint cy = y + dy * childSpan;
            float u = ((float)(cx >> (uint)childMip) + 0.5) * invDim;
            float v = ((float)(cy >> (uint)childMip) + 0.5) * invDim;
            float sum = _LODChunkSelection.SampleLevel(sampler_LODChunkSelection, float2(u, v), (float)childMip).x * childArea;
            if (oli < prefix + sum) { x = cx; y = cy; placed = true; break; }
            prefix += sum;
        }
        if (!placed) return false;
    }

    chunkIndex = y * side + x;
    if (chunkIndex >= totalChunks) return false;

    float4 sel = _LODChunkSelection[uint2(x, y)];
    float count = sel.x;
    float chunkLocalIndex = oli - prefix;
    if (chunkLocalIndex < 0.0 || chunkLocalIndex >= count) return false;

    uint2 mc = GSLODUnifiedMetaCoord(chunkIndex);
    float4 range = _LODChunkRange[mc];
    objId = (uint)round(range.w);
    uint globalFileId = (uint)round(GSLODChunkMax(chunkIndex).w);

    float4 comp = _LODObjectParams[uint2(3, objId)];
    if (comp.w < 0.5) return false;
    // Discrete computed-LOD level, carried by the selection metadata (used for debug tinting).
    lodLevel = sel.z;
    uint localSourceIndex = 0u;
    {
        uint level = (uint)round(sel.z);
        if (!GSLODResolveComputedLocalSourceUP(level, range.z, chunkLocalIndex, comp, localSourceIndex)) return false;
    }

    uint inFileOffset = (uint)round(range.x) * GSLOD_OFFSET_BASE_UINT + (uint)round(range.y);
    float4 fb = _LODFileBase[uint2(globalFileId, 0)];
    uint fusedBase = (uint)round(fb.x) * GSLOD_OFFSET_BASE_UINT + (uint)round(fb.y);
    globalSourceIndex = fusedBase + inFileOffset + localSourceIndex;
    return true;
}

#endif // GSLOD_SELECT_INCLUDED
