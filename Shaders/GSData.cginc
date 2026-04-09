#include "../RadixSort/Utils.cginc"

Texture2D _GS_Positions, _GS_Scales, _GS_Rotations, _GS_Colors;
#if defined(_SHBAND_SH1) || defined(_SHBAND_SH2) || defined(_SHBAND_SH3)
Texture2D _GS_SH1, _GS_SH2, _GS_SH3;
float4 _GS_SH1_Min, _GS_SH2_Min, _GS_SH3_Min;
float4 _GS_SH1_Range, _GS_SH2_Range, _GS_SH3_Range;
#endif
#if defined(_SHBAND_SH2) || defined(_SHBAND_SH3)
Texture2D _GS_SH4, _GS_SH5, _GS_SH6, _GS_SH7, _GS_SH8;
float4 _GS_SH4_Min, _GS_SH5_Min, _GS_SH6_Min, _GS_SH7_Min, _GS_SH8_Min;
float4 _GS_SH4_Range, _GS_SH5_Range, _GS_SH6_Range, _GS_SH7_Range, _GS_SH8_Range;
#endif
#if defined(_SHBAND_SH3)
Texture2D _GS_SH9, _GS_SHA, _GS_SHB, _GS_SHC, _GS_SHD, _GS_SHE, _GS_SHF;
float4 _GS_SH9_Min, _GS_SHA_Min, _GS_SHB_Min, _GS_SHC_Min, _GS_SHD_Min, _GS_SHE_Min, _GS_SHF_Min;
float4 _GS_SH9_Range, _GS_SHA_Range, _GS_SHB_Range, _GS_SHC_Range, _GS_SHD_Range, _GS_SHE_Range, _GS_SHF_Range;
#endif
Texture2DArray<float> _GS_RenderOrder;
Texture2DArray<float> _GS_RenderOrderPrecomputed;
Texture2D<float> _GS_RenderOrderMirror;
float4 _GS_Positions_TexelSize;
float4 _GS_RenderOrder_TexelSize;
float4 _GS_RenderOrderPrecomputed_TexelSize;
float _VRChatCameraMode;
float _VRChatMirrorMode;
float3 _MirrorCameraPos, _VRChatMirrorCameraPos;
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
int _SplatOffset;

float3 _OKLCHShift;

#if !defined(_SHBAND_SH0) && !defined(_SHBAND_SH1) && !defined(_SHBAND_SH2) && !defined(_SHBAND_SH3)
    #define _SHBAND_SH0
#endif

#if defined(_SHBAND_SH1) || defined(_SHBAND_SH2) || defined(_SHBAND_SH3)
    #define GS_HAS_SH_BAND1
#endif
#if defined(_SHBAND_SH2) || defined(_SHBAND_SH3)
    #define GS_HAS_SH_BAND2
#endif
#if defined(_SHBAND_SH3)
    #define GS_HAS_SH_BAND3
#endif

static const float SH_C1 = 0.4886025119029199;
static const float SH_C2_0 = 1.0925484305920792;
static const float SH_C2_1 = -1.0925484305920792;
static const float SH_C2_2 = 0.31539156525252005;
static const float SH_C2_3 = -1.0925484305920792;
static const float SH_C2_4 = 0.5462742152960396;
static const float SH_C3_0 = -0.5900435899266435;
static const float SH_C3_1 = 2.890611442640554;
static const float SH_C3_2 = -0.4570457994644658;
static const float SH_C3_3 = 0.3731763325901154;
static const float SH_C3_4 = -0.4570457994644658;
static const float SH_C3_5 = 1.445305721320277;
static const float SH_C3_6 = -0.5900435899266435;

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

struct SplatData {
    float3 mean;
    float3 scale;
    float4 quat;
    float4 color;
    uint id; // for debugging purposes
    bool valid;
};

uint2 GetSplatCoord(uint id)
{
    return uint2(id % uint(_GS_Positions_TexelSize.z), id / uint(_GS_Positions_TexelSize.z));
}

SplatData LoadSplatData(uint id) {
    uint2 coord = GetSplatCoord(id);

    SplatData o;
    o.mean = _GS_Positions[coord].xyz;
    // Without a low pass filter some splats can look too "thin", so we try to correct for this.
    // Only necessary if splats are trained without mip-splatting.
    o.scale = max(exp2(_Log2MinScale), _GS_Scales[coord].xyz);
    o.quat = normalize(lerp(-1.0, 1.0, _GS_Rotations[coord]));
    o.color = _GS_Colors[coord];
    o.color.a *= _Opacity;
    return o;
}

float3 DecodeSH(Texture2D tex, float4 shMin, float4 shRange, uint2 coord)
{
    return shMin.xyz + tex[coord].rgb * shRange.xyz;
}

float3 EvaluateSplatSHColor(uint id, float3 sh0Color, float3 positionObject, float3 cameraPosObject)
{
    float3 color = sh0Color;
#if defined(GS_HAS_SH_BAND1) || defined(GS_HAS_SH_BAND2) || defined(GS_HAS_SH_BAND3)
    uint2 coord = GetSplatCoord(id);
    float3 viewDir = positionObject - cameraPosObject;
    float invLen = rsqrt(max(dot(viewDir, viewDir), 1e-8));
    float3 dir = viewDir * invLen;
    float x = dir.x;
    float y = dir.y;
    float z = dir.z;
#endif
#if defined(GS_HAS_SH_BAND1)
    float3 sh1 = DecodeSH(_GS_SH1, _GS_SH1_Min, _GS_SH1_Range, coord);
    float3 sh2 = DecodeSH(_GS_SH2, _GS_SH2_Min, _GS_SH2_Range, coord);
    float3 sh3 = DecodeSH(_GS_SH3, _GS_SH3_Min, _GS_SH3_Range, coord);
    color += -SH_C1 * y * sh1
        + SH_C1 * z * sh2
        - SH_C1 * x * sh3;
#endif
#if defined(GS_HAS_SH_BAND2)
    float xx = x * x;
    float yy = y * y;
    float zz = z * z;
    float xy = x * y;
    float yz = y * z;
    float xz = x * z;
    float3 sh4 = DecodeSH(_GS_SH4, _GS_SH4_Min, _GS_SH4_Range, coord);
    float3 sh5 = DecodeSH(_GS_SH5, _GS_SH5_Min, _GS_SH5_Range, coord);
    float3 sh6 = DecodeSH(_GS_SH6, _GS_SH6_Min, _GS_SH6_Range, coord);
    float3 sh7 = DecodeSH(_GS_SH7, _GS_SH7_Min, _GS_SH7_Range, coord);
    float3 sh8 = DecodeSH(_GS_SH8, _GS_SH8_Min, _GS_SH8_Range, coord);
    color += SH_C2_0 * xy * sh4
        + SH_C2_1 * yz * sh5
        + SH_C2_2 * (2.0 * zz - xx - yy) * sh6
        + SH_C2_3 * xz * sh7
        + SH_C2_4 * (xx - yy) * sh8;
#endif
#if defined(GS_HAS_SH_BAND3)
    float3 sh9 = DecodeSH(_GS_SH9, _GS_SH9_Min, _GS_SH9_Range, coord);
    float3 shA = DecodeSH(_GS_SHA, _GS_SHA_Min, _GS_SHA_Range, coord);
    float3 shB = DecodeSH(_GS_SHB, _GS_SHB_Min, _GS_SHB_Range, coord);
    float3 shC = DecodeSH(_GS_SHC, _GS_SHC_Min, _GS_SHC_Range, coord);
    float3 shD = DecodeSH(_GS_SHD, _GS_SHD_Min, _GS_SHD_Range, coord);
    float3 shE = DecodeSH(_GS_SHE, _GS_SHE_Min, _GS_SHE_Range, coord);
    float3 shF = DecodeSH(_GS_SHF, _GS_SHF_Min, _GS_SHF_Range, coord);
    color += SH_C3_0 * y * (3.0 * x * x - y * y) * sh9
        + SH_C3_1 * x * y * z * shA
        + SH_C3_2 * y * (4.0 * z * z - x * x - y * y) * shB
        + SH_C3_3 * z * (2.0 * z * z - 3.0 * x * x - 3.0 * y * y) * shC
        + SH_C3_4 * x * (4.0 * z * z - x * x - y * y) * shD
        + SH_C3_5 * z * (x * x - y * y) * shE
        + SH_C3_6 * x * (x * x - 3.0 * y * y) * shF;
#endif
    return saturate(color);
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

SplatData LoadSplatDataRenderOrder(uint id) {
    bool validOrder = _GS_RenderOrder_TexelSize.z >= _GS_Positions_TexelSize.z;
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
    SplatData data = LoadSplatData(reordered_id);
    data.id = reordered_id; // store the original ID for debugging purposes
    data.valid = valid;
    return data;
}

SplatData LoadSplatDataPrecomputedOrder(uint id, float3 cam_dir) {
    int precomputedIndex = GetPrecomputedRenderOrderIndex(id, cam_dir);
    SplatData data = LoadSplatData(precomputedIndex);
    data.id = precomputedIndex; // store the original ID for debugging purposes
    data.valid = true; // precomputed order is always valid
    return data;
}
