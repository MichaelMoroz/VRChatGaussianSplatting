#define UNITY_SHADER_NO_UPGRADE 1 
#ifdef GS_NO_GEOM
#pragma target 4.5
#else
#pragma target 5.0
#pragma exclude_renderers gles
#endif
#pragma shader_feature_local _PRECOMPUTED_SORTING_ON
#pragma multi_compile_local __ _VRC_LIGHT_VOLUMES_ON
#pragma vertex vert
#pragma fragment frag
#ifndef GS_NO_GEOM
#pragma geometry geo
#endif

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

#define GS_RAY_DEPTH_ABS_LIMIT 1e6
#define GS_RAY_DEPTH_SQ_LIMIT 1e12

struct appdata {
    float4 position : POSITION;
#ifdef GS_NO_GEOM
    uint vertexID : SV_VertexID;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

#ifndef GS_NO_GEOM
struct v2g {
    float4 position : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};
#endif

struct g2f {
    float4 position: SV_POSITION;
    float2 quadPos: TEXCOORD0;
    nointerpolation float4 color: TEXCOORD1;
    nointerpolation float gaussianExp: TEXCOORD2;
    UNITY_VERTEX_OUTPUT_STEREO
};

#ifndef GS_NO_GEOM
v2g vert(appdata v) {
    v2g o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2g, o);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    return o;
}
#endif

float4x4 GSCreateClipToViewMatrix()
{
    float4x4 flipZ = float4x4(1, 0, 0, 0,
                              0, 1, 0, 0,
                              0, 0, -1, 1,
                              0, 0, 0, 1);
    float4x4 scaleZ = float4x4(1, 0, 0, 0,
                               0, 1, 0, 0,
                               0, 0, 2, -1,
                               0, 0, 0, 1);
    float4x4 flipY = float4x4(1, 0, 0, 0,
                              0, _ProjectionParams.x, 0, 0,
                              0, 0, 1, 0,
                              0, 0, 0, 1);

    float4x4 clipToView = mul(scaleZ, flipZ);
    clipToView = mul(unity_CameraInvProjection, clipToView);
    clipToView = mul(flipY, clipToView);
    clipToView._24 *= _ProjectionParams.x;
    clipToView._42 *= -1;
    return clipToView;
}

float3 GSClipToWorld(float2 clipPos, float depth, float4x4 clipToView)
{
    float4 viewPos = mul(clipToView, float4(clipPos, depth, 1.0));
    float invViewW = safe_divide(1.0, viewPos.w);
    return mul(UNITY_MATRIX_I_V, float4(viewPos.xyz * invViewW, 1.0)).xyz;
}

float2 GSClipNdcToScreenUV(float2 clipNdc)
{
    float4 screenPos = ComputeScreenPos(float4(clipNdc, 0.0, 1.0));
    return screenPos.xy / screenPos.w;
}

bool GSTryGetRaySplatDepth(float3 splatPos, float3 splatScale, float4 splatRotation, float2 clipPos, float4x4 clipToView, out float projectedDepth)
{
    projectedDepth = 0.0;

    float3 rayOrigin = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
    float3 rayDirWorld = GSClipToWorld(clipPos, 1.0, clipToView) - _WorldSpaceCameraPos;
    float rayDirWorldLenSq = dot(rayDirWorld, rayDirWorld);
    if (rayDirWorldLenSq <= DIV_EPSILON || !(rayDirWorldLenSq < GS_RAY_DEPTH_SQ_LIMIT))
    {
        return false;
    }
    rayDirWorld *= rsqrt(rayDirWorldLenSq);

    float3 rayDir = mul((float3x3)unity_WorldToObject, rayDirWorld);
    float rayDirLenSq = dot(rayDir, rayDir);
    if (rayDirLenSq <= DIV_EPSILON || !(rayDirLenSq < GS_RAY_DEPTH_SQ_LIMIT))
    {
        return false;
    }
    rayDir *= rsqrt(rayDirLenSq);

    float3 invScale = 1.0 / max(splatScale, float3(DIV_EPSILON, DIV_EPSILON, DIV_EPSILON));
    invScale /= max(invScale.x, max(invScale.y, invScale.z));

    float4 invRotation = conj_q(splatRotation);
    float3 rayMeanLocal = q_rotate(splatPos - rayOrigin, invRotation) * invScale;
    float3 rayDirLocal = q_rotate(rayDir, invRotation) * invScale;

    if (!all(abs(rayMeanLocal) < GS_RAY_DEPTH_ABS_LIMIT))
    {
        return false;
    }

    float a = dot(rayDirLocal, rayDirLocal);
    if (a <= DIV_EPSILON || !(a < GS_RAY_DEPTH_ABS_LIMIT))
    {
        return false;
    }

    float t = dot(rayDirLocal, rayMeanLocal) / a;
    if (t <= DIV_EPSILON || !(abs(t) < GS_RAY_DEPTH_ABS_LIMIT))
    {
        return false;
    }

    float4 hitClipPos = UnityObjectToClipPos(float4(rayOrigin + rayDir * t, 1.0));
    if (hitClipPos.w <= DIV_EPSILON || !(hitClipPos.w < GS_RAY_DEPTH_ABS_LIMIT))
    {
        return false;
    }

    projectedDepth = hitClipPos.z / hitClipPos.w;
    return abs(projectedDepth) < SAFE_NDC_LIMIT;
}

struct GSProjectedSplat
{
    Ellipse ell;
    float4 color;
    float gaussianExp;
    float3 mean;
    float3 support;
    float4 quat;
    float centerDepth;
};

float2 GSQuadPos(uint vtxID)
{
    return float2(vtxID & 1u, (vtxID >> 1u) & 1u) * 2.0 - 1.0;
}

bool GSTryResolveSplatID(uint localSplatID, out uint id)
{
    uint splatCount = (uint)max(_SplatCount, 0);
    uint splatOffset = (uint)max(_SplatOffset, 0);
    uint actualSplatCount = (uint)max(_ActualSplatCount, 0);

    id = localSplatID;
    if (id >= splatCount) return false;
    id += splatOffset;
    if (id >= actualSplatCount) return false;
#ifdef _BACK_TO_FRONT
    id = actualSplatCount - id - 1u;
#endif
    return true;
}

SplatData GSLoadSplatForDrawID(uint id)
{
#if defined(DEBUG_PROJECTED_POINTS) || defined(DEBUG_RAW_SPLAT_ORDER)
    SplatData splat = LoadSplatData(id);
    splat.id = id;
    splat.valid = true;
#elif defined(_PRECOMPUTED_SORTING_ON)
    float3 cam_dir = mul(transpose(UNITY_MATRIX_IT_MV), float4(0, 0, 1, 0)).xyz;
    SplatData splat = LoadSplatDataPrecomputedOrder(id, cam_dir);
#else
    SplatData splat = LoadSplatDataRenderOrder(id);
#endif
    return splat;
}

bool GSTryPrepareProjectedSplat(uint localSplatID, out GSProjectedSplat projected)
{
    UNITY_INITIALIZE_OUTPUT(GSProjectedSplat, projected);

    uint id;
    if (!GSTryResolveSplatID(localSplatID, id)) return false;

    SplatData splat = GSLoadSplatForDrawID(id);
    if (!splat.valid || (splat.color.a < _AlphaCutoff) || (splat.color.a < _AlphaCull) || any(splat.scale > _ScaleCutoff)) return false;

    float3 splatWorldPos = mul(unity_ObjectToWorld, float4(splat.mean, 1.0)).xyz;
    float3 camToSplat = splatWorldPos - _WorldSpaceCameraPos;
    float lodMaxScale = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
    if (lodMaxScale * lodMaxScale < (_LODCull * _LODCull) * dot(camToSplat, camToSplat)) return false;

    float4 splatClipPos = mul(UNITY_MATRIX_VP, float4(splatWorldPos, 1.0));
    if (splatClipPos.w <= 0.0) return false;
    splatClipPos.xyz /= splatClipPos.w;
    if (all(splatClipPos.xy < -1.0) || all(splatClipPos.xy > 1.0)) return false;

    float peakAlpha = splat.color.a;
    float cutoffSigmaRadius = sqrt(max(-2.0 * log(_AlphaCutoff / peakAlpha), 0.0));
    float scaleMax = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
    float3 clampedScale = clamp(splat.scale, scaleMax * _ThinThreshold, scaleMax);
    float3 projectionScale = max(clampedScale, scaleMax / PROJECTION_MAX_ANISOTROPY);
    projected.support = _GaussianMul * cutoffSigmaRadius * projectionScale;
    projected.ell = GetProjectedEllipsoid(splat.mean, projected.support, splat.quat);

    if (!valid_ellipse(projected.ell) || any(projected.ell.size > 1.75)) return false;

    projected.color = splat.color;
    float3 cameraPosObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
    projected.color.rgb = EvaluateSplatSHColor(splat.id, splat.color.rgb, splat.mean, cameraPosObject);
    projected.color.rgb = shift_color(projected.color.rgb) * _Exposure;
#ifdef _FAKE_SRGB
    projected.color.rgb = GammaToLinearSpace(projected.color.rgb);
#endif

    float area = projected.ell.size.x * projected.ell.size.y;
    projected.ell.size = max(projected.ell.size * _ScreenParams.xy, 1.75 * _AntiAliasing) / _ScreenParams.xy;
    float areaPost = projected.ell.size.x * projected.ell.size.y;
    projected.color.a *= area / areaPost;
    projected.gaussianExp = 0.5 * cutoffSigmaRadius * cutoffSigmaRadius;
    projected.mean = splat.mean;
    projected.quat = splat.quat;
    projected.centerDepth = splatClipPos.z;

#ifdef _VRC_LIGHT_VOLUMES_ON
    if (LightVolumesEnabled())
    {
        float3 L0, L1r, L1g, L1b;
        LightVolumeSH(splatWorldPos, L0, L1r, L1g, L1b);
        float3 emissivePart = max(projected.color.rgb - 1.0, 0.0);
        float3 albedoPart = min(projected.color.rgb, 1.0);
        projected.color.rgb = albedoPart * LinearToGammaSpace(abs(L0)) * _LightVolumeIntensity + emissivePart;
    }
#endif

    return true;
}

void GSSetInvalidVertex(inout g2f o)
{
    o.position = float4(0.0, 0.0, 0.0, 1.0);
    o.quadPos = float2(2.0, 2.0);
    o.color = 0.0;
    o.gaussianExp = 0.0;
}

void GSFillProjectedSplatVertex(GSProjectedSplat projected, uint vtxID, float4x4 clipToView, inout g2f o)
{
    o.quadPos = GSQuadPos(vtxID);
    o.color = projected.color;
    o.gaussianExp = projected.gaussianExp;

    float2x2 rot = float2x2(projected.ell.axis.x, -projected.ell.axis.y, projected.ell.axis.y, projected.ell.axis.x);
    float2 ndc = projected.ell.center + mul(rot, o.quadPos * projected.ell.size);
    float cornerDepth = projected.centerDepth;
    GSTryGetRaySplatDepth(projected.mean, projected.support, projected.quat, ndc, clipToView, cornerDepth);
    o.position = float4(ndc, cornerDepth, 1.0);
}

#ifdef GS_NO_GEOM
g2f vert(appdata v)
{
    g2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(g2f, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    GSProjectedSplat projected;
    if (!GSTryPrepareProjectedSplat(v.vertexID >> 2u, projected))
    {
        GSSetInvalidVertex(o);
        return o;
    }

    GSFillProjectedSplatVertex(projected, v.vertexID & 3u, GSCreateClipToViewMatrix(), o);
    return o;
}
#else
[maxvertexcount(GS_MAX_VERTEX_COUNT)]
[instance(32)]
void geo(point v2g input[1], inout TriangleStream<g2f> triStream, uint instanceID : SV_GSInstanceID, uint geoPrimID : SV_PrimitiveID) {
    g2f o;
    UNITY_SETUP_INSTANCE_ID(input[0]);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input[0]);
    UNITY_INITIALIZE_OUTPUT(g2f, o);
    UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o);

    GSProjectedSplat projected;
    if (!GSTryPrepareProjectedSplat(geoPrimID * 32u + instanceID, projected)) return;
    float4x4 clipToView = GSCreateClipToViewMatrix();

    [unroll] for (uint vtxID = 0; vtxID < 4; vtxID ++)
    {
        GSFillProjectedSplatVertex(projected, vtxID, clipToView, o);
        triStream.Append(o);
    }
}
#endif

//#define DEBUG_OUTLINES

float4 frag(g2f input) : SV_Target {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float dist2 = dot(input.quadPos, input.quadPos);
#ifdef DEBUG_PROJECTED_POINTS
    return input.color;
#endif
#ifdef DEBUG_OUTLINES
    return (dist2 < 1.0) ? float4(1, 0, 0, 1) : float4(0, 0, 0, 1); // red outline for debugging
#endif
    if (dist2 > 1.0)
    {
        discard;
    }  // skip outside of the cutoff ellipse
    float rho = input.color.a * exp(-input.gaussianExp * dist2);
    return float4(input.color.rgb * rho, rho);
}
