#include "../RadixSort/Utils.cginc"
#include "Utilities.cginc"

Texture2D<float4> _GS_PackedPositions;
Texture2D<float4> _GS_PackedColors;
float4 _GS_PackedPositions_TexelSize;
float4 _GS_PackedColors_TexelSize;

Texture2DArray<float> _GS_RenderOrder;
Texture2DArray<float> _GS_RenderOrderPrecomputed;
//Texture2D<float> _GS_RenderOrderMirror;
float4 _GS_RenderOrder_TexelSize;
float4 _GS_RenderOrderPrecomputed_TexelSize;
float _VRChatCameraMode;
float _VRChatMirrorMode;
float3 _MirrorCameraPos, _VRChatMirrorCameraPos;
float _QuadScale;
float _GaussianMul;
float _ThinThreshold;
float _AntiAliasing;
float _Log2MinScale;
float _AlphaCutoff;
float _Exposure;
float _Gamma;
float _Opacity;
float _ScaleCutoff;
float2 _MinMaxSortDistance;
int _SplatCount;
int _ActualSplatCount;
int _ActualSplatCountSqrt;
int _SplatOffset;
float3 _OKLCHShift;
float4 _SplatScalesLOG2; // x,y = position scale log2, z,w = RS scale log2

float3 shift_color(float3 rgb) {
    return pow(shift_color_oklch(rgb, _OKLCHShift), 1.0 / _Gamma);
}

GaussianData LoadPackedSplatData(uint id) {
    uint2 coord = uint2(id % uint(_GS_PackedPositions_TexelSize.z), id / uint(_GS_PackedPositions_TexelSize.z));
    GaussianData data = UnpackGaussianData(asuint(_GS_PackedPositions[coord]), _SplatScalesLOG2);
    data.C = _GS_PackedColors[coord];
    return data;
}

int GetPrecomputedRenderOrderIndex(uint id, float3 cam_dir) {
    float3 dirs[10] = {
        float3(0.57735027, 0.57735027, 0.57735027), float3(0.57735027, 0.57735027, -0.57735027), float3(0.57735027, -0.57735027, 0.57735027),
        float3(0.57735027, -0.57735027, -0.57735027), float3(0.00000000, 0.35682209, 0.93417236), float3(0.00000000, 0.35682209, -0.93417236),
        float3(0.35682209, 0.93417236, 0.00000000), float3(0.35682209, -0.93417236, 0.00000000), float3(0.93417236, 0.00000000, 0.35682209),
        float3(0.93417236, 0.00000000, -0.35682209)
    };
    float3 cam_dir_normalized = normalize(cam_dir);
    float best_dot = 0.0;
    int best_index = 0;
    [unroll] for(int i = 0; i < 10; i++) {
        float dot_product = dot(dirs[i], cam_dir_normalized);
        if(abs(dot_product) > abs(best_dot)) {
            best_dot = dot_product;
            best_index = i;
        }
    }
    if(best_dot > 0.0) {
        id = _ActualSplatCount - id - 1; // flip the order for positive directions
    }
    uint2 coord = uint2(id % uint(_GS_RenderOrderPrecomputed_TexelSize.z), id / uint(_GS_RenderOrderPrecomputed_TexelSize.z));
    return _GS_RenderOrderPrecomputed[int3(coord, best_index)];
}

GaussianData LoadSplatDataRenderOrder(uint id) {
    bool validOrder = _GS_RenderOrder_TexelSize.z >= _GS_PackedPositions_TexelSize.z; 
    uint reordered_id = id;
    bool valid = true;
    if(validOrder) { // if valid order texture
        uint2 coord1 = IndexToUV(id);
        bool inMirror = false;//_VRChatMirrorMode > 0 && all(abs(_VRChatMirrorCameraPos - _MirrorCameraPos) < 1e-4);
        if(inMirror) {
            valid = false;
            //reordered_id = _GS_RenderOrderMirror[coord1];
        } else {
            uint slice = (_VRChatCameraMode > 0);
            reordered_id = _GS_RenderOrder[uint3(coord1, slice)];
        }
    } else {
        reordered_id = pcg(reordered_id) % _ActualSplatCount; // randomize order for alpha blending to somewhat work
    }
    GaussianData data = LoadPackedSplatData(reordered_id);
   // data.id = reordered_id; // store the original ID for debugging purposes
   // data.valid = valid;
    return data;
}

GaussianData LoadSplatDataPrecomputedOrder(uint id, float3 cam_dir) {
    int precomputedIndex = GetPrecomputedRenderOrderIndex(id, cam_dir);
    GaussianData data = LoadPackedSplatData(precomputedIndex);
   // data.id = precomputedIndex; // store the original ID for debugging purposes
   // data.valid = true; // precomputed order is always valid
    return data;
}