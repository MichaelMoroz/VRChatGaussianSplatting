#include "../RadixSort/Utils.cginc"
#include "Random.cginc"
#include "Packing.cginc"
#include "Quaternion.cginc"
#include "Linalg.cginc"

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

float3 rgb_to_oklab(float3 c) 
{
    float l = 0.4121656120f * c.r + 0.5362752080f * c.g + 0.0514575653f * c.b;
    float m = 0.2118591070f * c.r + 0.6807189584f * c.g + 0.1074065790f * c.b;
    float s = 0.0883097947f * c.r + 0.2818474174f * c.g + 0.6302613616f * c.b;

    float l_ = pow(max(l, 0.0), 1./3.);
    float m_ = pow(max(m, 0.0), 1./3.);
    float s_ = pow(max(s, 0.0), 1./3.);

    float3 labResult;
    labResult.x = 0.2104542553f*l_ + 0.7936177850f*m_ - 0.0040720468f*s_;
    labResult.y = 1.9779984951f*l_ - 2.4285922050f*m_ + 0.4505937099f*s_;
    labResult.z = 0.0259040371f*l_ + 0.7827717662f*m_ - 0.8086757660f*s_;
    return labResult;
}

float3 oklab_to_rgb(float3 c) 
{
    //c.yz *= c.x;
    float l_ = c.x + 0.3963377774f * c.y + 0.2158037573f * c.z;
    float m_ = c.x - 0.1055613458f * c.y - 0.0638541728f * c.z;
    float s_ = c.x - 0.0894841775f * c.y - 1.2914855480f * c.z;

    float l = l_*l_*l_;
    float m = m_*m_*m_;
    float s = s_*s_*s_;

    float3 rgbResult;
    rgbResult.r = + 4.0767245293f*l - 3.3072168827f*m + 0.2307590544f*s;
    rgbResult.g = - 1.2681437731f*l + 2.6093323231f*m - 0.3411344290f*s;
    rgbResult.b = - 0.0041119885f*l - 0.7034763098f*m + 1.7068625689f*s;
    return rgbResult;
}

#define TAU 6.28318530718 // 2 * PI

float3 oklch2oklab(float3 lch) {
    return float3(lch.x, lch.y * cos(lch.z * TAU), lch.y * sin(lch.z * TAU));
}

float3 oklab2oklch(float3 lab) {
    float h = (lab.y != 0.0) ? atan2(lab.z, lab.y) : 0.0; // atan2 handles the case when lab.y is zero
    float c = sqrt(lab.y * lab.y + lab.z * lab.z);
    return float3(lab.x, c, h / TAU);
}

float3 shift_color(float3 rgb)
{
    // Convert RGB to Oklab
    float3 oklab = rgb_to_oklab(rgb);

    // Convert Oklab to Oklch
    float3 oklch = oklab2oklch(oklab);

    // Apply the shift
    oklch += _OKLCHShift;
    oklch.y = max(0.0, oklch.y); // Ensure chroma is non-negative

    // Convert Oklch back to Oklab
    oklab = oklch2oklab(oklch);

    // Convert Oklab back to RGB
    rgb = max(oklab_to_rgb(oklab), 0.0); // Ensure RGB values are non-negative
    return pow(rgb, 1.0 / _Gamma);
}

struct Gaussian {
    float3 p; // position
    float3 s; // scale
    float4 q; // quaternion (rotation)
    float a; //density
};

struct SplatData {
    Gaussian g;
    float4 color;
    uint id; // for debugging purposes
    bool valid;
};

uint4 PackGaussian(Gaussian g) {
    uint4 data;
    data.xy = packF3U2(g.p);
    data.z = packF3U1(g.s);
    data.w = packF3U1(quaternionToAxisAngle(g.q));
    return data;
}

Gaussian UnpackGaussian(uint4 data) {
    Gaussian o;
    o.p = unpackF3U2(data.xy);
    o.s = unpackF3U1(data.z);
    o.q = axisAngleToQuaternion(unpackF3U1(data.w));
    return o;
}

GaussianData SplatDataToData(SplatData g) {
    GaussianData data;
    data.P = g.g.p;
    data.RS = CholeskyFromQS(g.g.q, g.g.s);
    data.C = g.color;
    data = UnpackGaussianData(PackGaussianData(data)); //test if packing works correctly
    return data;
}

GaussianData LoadPackedSplatData(uint id) {
    uint2 coord = uint2(id % uint(_GS_PackedPositions_TexelSize.z), id / uint(_GS_PackedPositions_TexelSize.z));

    return UnpackGaussianData(asuint(_GS_PackedPositions[coord]));
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