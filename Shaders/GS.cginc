#define UNITY_SHADER_NO_UPGRADE 1 
#pragma target 5.0
#pragma exclude_renderers gles
#pragma shader_feature_local _PRECOMPUTED_SORTING_ON
#pragma shader_feature_local _VRC_LIGHT_VOLUMES_ON
#pragma vertex vert
#pragma fragment frag
#pragma geometry geo

//#define DEBUG_PROJECTED_POINTS
//#define DEBUG_RAW_SPLAT_ORDER
#define PROJECTION_MAX_ANISOTROPY 32.0

#include "UnityCG.cginc"
#include "GSData.cginc"
#include "GSMath.cginc"

#ifdef _VRC_LIGHT_VOLUMES_ON
#include "LightVolumes.cginc"
float _LightVolumeIntensity;
#endif

#ifdef DEBUG_PROJECTED_POINTS
#define GS_MAX_VERTEX_COUNT 20
#else
#define GS_MAX_VERTEX_COUNT 4
#endif

struct appdata {
    float4 position : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2g {
    float4 position : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct g2f {
    float4 position: SV_POSITION;
    float2 quadPos: TEXCOORD0;
    nointerpolation float4 color: TEXCOORD1;
    nointerpolation float gaussianExp: TEXCOORD2;
#ifdef _LEGACY_RANDOMIZED_ORDER
    nointerpolation uint splatID: TEXCOORD3;
#endif
    UNITY_VERTEX_OUTPUT_STEREO
};

v2g vert(appdata v) {
    v2g o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2g, o);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    return o;
}

[maxvertexcount(GS_MAX_VERTEX_COUNT)]
[instance(32)]
void geo(point v2g input[1], inout TriangleStream<g2f> triStream, uint instanceID : SV_GSInstanceID, uint geoPrimID : SV_PrimitiveID) {
    uint id = geoPrimID * 32 + instanceID;
    if (id >= _SplatCount) return; // check if id is within bounds
    id += _SplatOffset; // offset for the current batch
    #ifdef _BACK_TO_FRONT
        id = _ActualSplatCount - id - 1; // flip the order for back-to-front rendering
    #endif
    if (id >= _ActualSplatCount) return;
    
    g2f o;
    UNITY_SETUP_INSTANCE_ID(input[0]);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input[0]);
    UNITY_INITIALIZE_OUTPUT(g2f, o);
    UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o);

#if defined(DEBUG_PROJECTED_POINTS) || defined(DEBUG_RAW_SPLAT_ORDER)
    SplatData splat = LoadSplatData(id);
    splat.id = id;
    splat.valid = true;
    #elif defined(_LEGACY_RANDOMIZED_ORDER)
    SplatData splat = LoadSplatDataRandomized(id);
    #elif defined(_PRECOMPUTED_SORTING_ON)
    float3 cam_dir = mul(transpose(UNITY_MATRIX_IT_MV), float4(0, 0, 1, 0)).xyz; // camera direction in object space
    SplatData splat = LoadSplatDataPrecomputedOrder(id, cam_dir);
    #else 
    SplatData splat = LoadSplatDataRenderOrder(id);
    #endif

    if (!splat.valid || (splat.color.a < _AlphaCutoff) || any(splat.scale > _ScaleCutoff)) return; 

    float3 splatWorldPos = mul(unity_ObjectToWorld, float4(splat.mean, 1)).xyz;

    float4 splatClipPos = mul(UNITY_MATRIX_VP, float4(splatWorldPos, 1));
    if (splatClipPos.w <= 0) return; // behind camera
    splatClipPos.xyz /= splatClipPos.w; // perspective divide
    if (all(splatClipPos.xy < -1.0) || all(splatClipPos.xy > 1.0)) return; // outside of view frustum

    o.color = splat.color;
    float peakAlpha = o.color.a;
    float cutoffSigmaRadius = sqrt(max(-2.0 * log(_AlphaCutoff / peakAlpha), 0.0));
    float scale_max = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
    float3 clamped_scale = clamp(splat.scale, scale_max * _ThinThreshold, scale_max);
    float3 projection_scale = max(clamped_scale, scale_max / PROJECTION_MAX_ANISOTROPY);
    float supportScale = _GaussianMul * cutoffSigmaRadius;

    if (o.color.a < _AlphaCutoff) {
        return; // skip splats with too small area or invalid alpha
    }

#ifdef DEBUG_PROJECTED_POINTS
    float2 centerNdc;
    float2 projectedPoints[5];
    GetProjectedEllipsoidOutline(splat.mean, supportScale * projection_scale, splat.quat, projectedPoints, centerNdc);

    o.color = float4(1.0, 0.1, 0.0, 1.0);
    o.gaussianExp = 0.0;
    float2 debugHalfSize = 4.0 / _ScreenParams.xy;

    [unroll] for (uint pointID = 0; pointID < 5; pointID++)
    {
        [unroll] for (uint vtxID = 0; vtxID < 4; vtxID++)
        {
            o.quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
            float2 ndc = projectedPoints[pointID] + o.quadPos * debugHalfSize;
            o.position = float4(ndc, splatClipPos.z, 1.0);
            triStream.Append(o);
        }
        triStream.RestartStrip();
    }
    return;
#endif

    // Project the ellipsoid onto the screen
    Ellipse ell = GetProjectedEllipsoid(splat.mean, supportScale * projection_scale, splat.quat);

    if(!valid_ellipse(ell) || any(ell.size > 1.75)) {
        return;
    }

    float3 cameraPosObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
    o.color.rgb = EvaluateSplatSHColor(splat.id, splat.color.rgb, splat.mean, cameraPosObject);
    o.color.rgb = shift_color(o.color.rgb) * _Exposure;
    #ifdef _FAKE_SRGB
        o.color.rgb = GammaToLinearSpace(o.color.rgb);
    #endif

    float area = ell.size.x * ell.size.y;
    ell.size = max(ell.size * _ScreenParams, 1.75 * _AntiAliasing) / _ScreenParams; // ensure minimum size
    float areaPost = ell.size.x * ell.size.y;
    float areaScale = area / areaPost;
    o.color.a *= areaScale; // scale alpha by area ratio
    o.gaussianExp = 0.5 * cutoffSigmaRadius * cutoffSigmaRadius;
#ifdef _LEGACY_RANDOMIZED_ORDER
    o.splatID = id;
#endif

#ifdef _VRC_LIGHT_VOLUMES_ON
    if (LightVolumesEnabled())
    {
        float3 L0, L1r, L1g, L1b;
        LightVolumeSH(splatWorldPos, L0, L1r, L1g, L1b);
        float3 emissivePart = max(o.color.rgb - 1.0, 0.0);
        float3 albedoPart = min(o.color.rgb, 1.0);
        o.color.rgb = albedoPart * LinearToGammaSpace(abs(L0)) * _LightVolumeIntensity + emissivePart;
    }
#endif

    [unroll] for (uint vtxID = 0; vtxID < 4; vtxID ++)
    {
        o.quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
        float2x2 rot = float2x2(ell.axis.x, -ell.axis.y, ell.axis.y, ell.axis.x);
        float2 ndc = ell.center + mul(rot, o.quadPos * ell.size);
        o.position = float4(ndc, splatClipPos.z, 1.0);
        triStream.Append(o);
    }
}

//#define DEBUG_OUTLINES

uint2 pcg2d(uint2 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    v.x += v.y * 2246822519u;
    v.y += v.x * 3266489917u;
    return v;
}

float InterleavedGradientNoise(float2 pixel, uint frameIndex)
{
    pixel += ((float)frameIndex) * 5.588238;
    return frac(52.9829189 * frac(0.06711056 * pixel.x + 0.00583715 * pixel.y));
}

float InterleavedGradientNoiseInt(uint2 pixel, uint frameIndex)
{
    return InterleavedGradientNoise(float2(pixel), frameIndex);
}

#ifdef _LEGACY_RANDOMIZED_ORDER
uint EvaluateCoverageMask(g2f input, float rho)
{
    uint2 pixel = uint2(input.position.xy);
    uint eyeIndex = 0u;
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED) || defined(UNITY_SINGLE_PASS_STEREO)
    eyeIndex = unity_StereoEyeIndex;
#endif
    uint frameIndex = (uint)floor(_Time.y / max(unity_DeltaTime.x, 1e-6));
    rho = saturate(rho);
    if (rho <= 0.0) return 0u;

    uint2 hash = pcg2d(uint2(input.splatID, eyeIndex));
    uint sampleCount = (uint)max(GetRenderTargetSampleCount(), 1);
    uint2 randomOffset = uint2(hash.x % 4096u, hash.y % 4096u);
    uint coverage = 0u;
    for (uint i = 0u; i < sampleCount; i++)
    {
        float rand = InterleavedGradientNoiseInt(pixel + randomOffset + i*13, frameIndex);
        if (rand < rho) coverage |= (1u << i);
    }
    return coverage;

}
#endif

uint GetFullCoverageMask()
{
    uint sampleCount = (uint)max(GetRenderTargetSampleCount(), 1);
    return (sampleCount >= 32u) ? 0xffffffffu : ((1u << sampleCount) - 1u);
}

float4 frag(g2f input, out uint coverage : SV_Coverage) : SV_Target {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float dist2 = dot(input.quadPos, input.quadPos);
#ifdef DEBUG_PROJECTED_POINTS
    coverage = GetFullCoverageMask();
    return input.color;
#endif
#ifdef DEBUG_OUTLINES
    coverage = GetFullCoverageMask();
    return (dist2 < 1.0) ? float4(1, 0, 0, 1) : float4(0, 0, 0, 1); // red outline for debugging
#endif
    if (dist2 > 1.0)
    {
        coverage = 0u;
        discard;
    }  // skip outside of the cutoff ellipse
    float rho = input.color.a * exp(-input.gaussianExp * dist2);
#ifdef _LEGACY_RANDOMIZED_ORDER
    coverage = EvaluateCoverageMask(input, rho);
    if (coverage == 0u) discard;
    return float4(input.color.rgb, 1.0);
#else
    coverage = GetFullCoverageMask();
    return float4(input.color.rgb * rho, rho);
#endif
}
