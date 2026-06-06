#define UNITY_SHADER_NO_UPGRADE 1
#ifdef GS_NO_GEOM
#pragma target 3.5
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

#ifdef GS_NO_GEOM
g2f vert(appdata v) {
#else
[maxvertexcount(GS_MAX_VERTEX_COUNT)]
[instance(32)]
void geo(point v2g input[1], inout TriangleStream<g2f> triStream, uint instanceID : SV_GSInstanceID, uint geoPrimID : SV_PrimitiveID) {
#endif
    g2f o;
#ifdef GS_NO_GEOM
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(g2f, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
#else
    UNITY_SETUP_INSTANCE_ID(input[0]);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input[0]);
    UNITY_INITIALIZE_OUTPUT(g2f, o);
    UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input[0], o);
#endif

    uint splatCount = (uint)max(_SplatCount, 0);
    uint splatOffset = (uint)max(_SplatOffset, 0);
    uint actualSplatCount = (uint)max(_ActualSplatCount, 0);
#ifdef GS_NO_GEOM
    uint id = v.vertexID >> 2u;
    if (id >= splatCount) {
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
    }
    id += splatOffset;
    if (id >= actualSplatCount) {
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
    }
#else
    uint id = geoPrimID * 32 + instanceID;
    if (id >= splatCount) return; // check if id is within bounds
    id += splatOffset; // offset for the current batch
    if (id >= actualSplatCount) return;
#endif
    #ifdef _BACK_TO_FRONT
        id = actualSplatCount - id - 1u; // flip the order for back-to-front rendering
    #endif

#if defined(DEBUG_PROJECTED_POINTS) || defined(DEBUG_RAW_SPLAT_ORDER)
    SplatData splat = LoadSplatData(id);
    splat.id = id;
    splat.valid = true;
    #elif defined(_PRECOMPUTED_SORTING_ON)
    float3 cam_dir = mul(transpose(UNITY_MATRIX_IT_MV), float4(0, 0, 1, 0)).xyz; // camera direction in object space
    SplatData splat = LoadSplatDataPrecomputedOrder(id, cam_dir);
    #else
    SplatData splat = LoadSplatDataRenderOrder(id);
    #endif

#ifdef GS_NO_GEOM
    if (!splat.valid || (splat.color.a < _AlphaCutoff) || (splat.color.a < _AlphaCull) || any(splat.scale > _ScaleCutoff)) {
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
    }
#else
    if (!splat.valid || (splat.color.a < _AlphaCutoff) || (splat.color.a < _AlphaCull) || any(splat.scale > _ScaleCutoff)) return;
#endif

    float3 splatWorldPos = mul(unity_ObjectToWorld, float4(splat.mean, 1)).xyz;

    float3 camToSplat = splatWorldPos - _WorldSpaceCameraPos;
    float lodMaxScale = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
#ifdef GS_NO_GEOM
    if (lodMaxScale * lodMaxScale < (_LODCull * _LODCull) * dot(camToSplat, camToSplat)) {
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
    }
#else
    if (lodMaxScale * lodMaxScale < (_LODCull * _LODCull) * dot(camToSplat, camToSplat)) return; // distance-based LOD cull (no projection)
#endif

    float4 splatClipPos = mul(UNITY_MATRIX_VP, float4(splatWorldPos, 1));
#ifdef GS_NO_GEOM
    if (splatClipPos.w <= 0) {
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
    }
#else
    if (splatClipPos.w <= 0) return; // behind camera
#endif
    splatClipPos.xyz /= splatClipPos.w; // perspective divide
#ifdef GS_NO_GEOM
    if (all(splatClipPos.xy < -1.0) || all(splatClipPos.xy > 1.0)) {
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
    }
#else
    if (all(splatClipPos.xy < -1.0) || all(splatClipPos.xy > 1.0)) return; // outside of view frustum
#endif

    o.color = splat.color;
    float peakAlpha = o.color.a;
    float cutoffSigmaRadius = sqrt(max(-2.0 * log(_AlphaCutoff / peakAlpha), 0.0));
    float scale_max = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
    float3 clamped_scale = clamp(splat.scale, scale_max * _ThinThreshold, scale_max);
    float3 projection_scale = max(clamped_scale, scale_max / PROJECTION_MAX_ANISOTROPY);
    float supportScale = _GaussianMul * cutoffSigmaRadius;
    float3 splatSupport = supportScale * projection_scale;

    if (o.color.a < _AlphaCutoff) {
#ifdef GS_NO_GEOM
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
#else
        return; // skip splats with too small area or invalid alpha
#endif
    }

#ifdef DEBUG_PROJECTED_POINTS
#ifdef GS_NO_GEOM
    o.position = float4(0.0, 0.0, 0.0, 1.0);
    o.quadPos = float2(2.0, 2.0);
    o.color = 0.0;
    o.gaussianExp = 0.0;
    return o;
#else
    float2 centerNdc;
    float2 projectedPoints[5];
    GetProjectedEllipsoidOutline(splat.mean, splatSupport, splat.quat, projectedPoints, centerNdc);

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
#endif

    // Project the ellipsoid onto the screen
    Ellipse ell = GetProjectedEllipsoid(splat.mean, splatSupport, splat.quat);

    if(!valid_ellipse(ell) || any(ell.size > 1.75)) {
#ifdef GS_NO_GEOM
        o.position = float4(0.0, 0.0, 0.0, 1.0);
        o.quadPos = float2(2.0, 2.0);
        o.color = 0.0;
        o.gaussianExp = 0.0;
        return o;
#else
        return;
#endif
    }

    float4x4 clipToView = GSCreateClipToViewMatrix();

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

#ifdef GS_NO_GEOM
    o.quadPos = float2(v.vertexID & 1u, (v.vertexID >> 1u) & 1u) * 2.0 - 1.0;
    float2x2 rot = float2x2(ell.axis.x, -ell.axis.y, ell.axis.y, ell.axis.x);
    float2 ndc = ell.center + mul(rot, o.quadPos * ell.size);
    float cornerDepth = splatClipPos.z;
    GSTryGetRaySplatDepth(splat.mean, splatSupport, splat.quat, ndc, clipToView, cornerDepth);
    o.position = float4(ndc, cornerDepth, 1.0);
    return o;
#else
    [unroll] for (uint vtxID = 0; vtxID < 4; vtxID ++)
    {
        o.quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
        float2x2 rot = float2x2(ell.axis.x, -ell.axis.y, ell.axis.y, ell.axis.x);
        float2 ndc = ell.center + mul(rot, o.quadPos * ell.size);
        float cornerDepth = splatClipPos.z;
        GSTryGetRaySplatDepth(splat.mean, splatSupport, splat.quat, ndc, clipToView, cornerDepth);
        o.position = float4(ndc, cornerDepth, 1.0);
        triStream.Append(o);
    }
#endif
}

//#define DEBUG_OUTLINES

float4 frag(g2f input) : SV_Target {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float dist2 = dot(input.quadPos, input.quadPos);
#ifdef DEBUG_PROJECTED_POINTS
    return input.color;
#endif
#ifdef DEBUG_OUTLINES
    return (dist2 < 1.0) ? float4(1, 0, 0, 1) : float4(0, 0, 0, 1);
#endif
    if (dist2 > 1.0)
    {
        discard;
    }
    float rho = input.color.a * exp(-input.gaussianExp * dist2);
    return float4(input.color.rgb * rho, rho);
}
