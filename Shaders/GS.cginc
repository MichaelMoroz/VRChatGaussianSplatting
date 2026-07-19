#define UNITY_SHADER_NO_UPGRADE 1
#ifdef GS_NO_GEOM
#pragma target 3.5
#else
#pragma target 5.0
#pragma exclude_renderers gles
#endif
#pragma shader_feature_local _PRECOMPUTED_SORTING_ON
#pragma shader_feature_local _GS_PACKED_POSITIONS
#pragma multi_compile_local __ _VRC_LIGHT_VOLUMES_ON
#pragma vertex vert
#pragma fragment frag
#ifndef GS_NO_GEOM
#pragma geometry geo
#endif

//#define DEBUG_RAW_SPLAT_ORDER
#define PROJECTION_MAX_ANISOTROPY 32.0

#include "UnityCG.cginc"
#include "GSData.cginc"
#include "GSMath.cginc"

#ifdef _VRC_LIGHT_VOLUMES_ON
#include "LightVolumes.cginc"
float _LightVolumeIntensity;
#endif

#ifdef GS_COLLIDER_DEPTH_WEIGHT
float4x4 _GS_ColliderWorldToBox;
float _GS_ColliderBoxHeight;
float4 _GS_ColliderScreenParams;
float _GS_ColliderOpacityLogMultiplier;
#endif

#define GS_MAX_VERTEX_COUNT 4

#define GS_RAY_DEPTH_ABS_LIMIT 1e6
#define GS_RAY_DEPTH_SQ_LIMIT 1e12

struct appdata {
    float4 position : POSITION;
#if defined(GS_NO_GEOM)
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
    float2 clipPos: TEXCOORD3;
    nointerpolation float3 splatMean: TEXCOORD4;
    nointerpolation float3 splatSupport: TEXCOORD5;
    nointerpolation float4 splatRotation: TEXCOORD6;
    nointerpolation float3 debugColor: TEXCOORD7;
#ifdef GS_COLLIDER_DEPTH_WEIGHT
    nointerpolation float colliderDepthNorm: TEXCOORD8;
#endif
    UNITY_VERTEX_OUTPUT_STEREO
};

#ifdef GS_NO_GEOM
g2f GSInvalidVertex()
{
    g2f o;
    UNITY_INITIALIZE_OUTPUT(g2f, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.position = float4(0.0, 0.0, 0.0, 1.0);
    o.quadPos = float2(2.0, 2.0);
    o.color = 0.0;
    o.gaussianExp = 0.0;
    return o;
}
#define GS_RETURN_INVALID return GSInvalidVertex()
#else
#define GS_RETURN_INVALID return
#endif

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

// Camera ray (world-space origin + direction) through an NDC point, derived analytically from
// the forward projection (UNITY_MATRIX_P) and the inverse view matrix. This avoids
// unity_CameraInvProjection (unreliable in single-pass stereo) and a full 4x4 inverse, while
// staying correct for every Unity projection: perspective and orthographic, off-center, and
// oblique. Unity projection matrices have no x/y skew and a w-row of only (0,0,m32,m33), so
// ndc.{x,y} = clip.{x,y}/clip.w makes the view-space (vx,vy) affine in vz: a point at vz=0 plus
// a direction per unit vz (negated to point into the scene, -z). Perspective (m32=-1,m33=0)
// collapses the origin onto the camera; orthographic (m32=0,m33=1) yields parallel rays with a
// per-pixel origin. Oblique only changes the clip Z-row, which the rows used here never touch.
// Inverting the exact per-eye GPU matrix keeps it correct across graphics APIs and stereo eyes.
void GSGetWorldRay(float2 ndc, out float3 originWS, out float3 dirWS)
{
    float m00 = UNITY_MATRIX_P._m00, m02 = UNITY_MATRIX_P._m02, m03 = UNITY_MATRIX_P._m03;
    float m11 = UNITY_MATRIX_P._m11, m12 = UNITY_MATRIX_P._m12, m13 = UNITY_MATRIX_P._m13;
    float m32 = UNITY_MATRIX_P._m32, m33 = UNITY_MATRIX_P._m33;

    float3 viewOrigin = float3((ndc.x * m33 - m03) / m00, (ndc.y * m33 - m13) / m11, 0.0);
    float3 viewDir    = float3((m02 - ndc.x * m32) / m00, (m12 - ndc.y * m32) / m11, -1.0);

    originWS = mul(UNITY_MATRIX_I_V, float4(viewOrigin, 1.0)).xyz;
    dirWS    = mul((float3x3)UNITY_MATRIX_I_V, viewDir);
}

bool GSTryGetRaySplatDepth(float3 splatPos, float3 splatScale, float4 splatRotation, float2 clipPos, out float projectedDepth)
{
    projectedDepth = 0.0;

    float3 rayOriginWorld, rayDirWorld;
    GSGetWorldRay(clipPos, rayOriginWorld, rayDirWorld);
    float3 rayOrigin = mul(unity_WorldToObject, float4(rayOriginWorld, 1.0)).xyz;
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

bool GSTryGetRayEllipsoidSurfaceDepth(float3 splatPos, float3 splatScale, float4 splatRotation, float2 clipPos, out float projectedDepth)
{
    projectedDepth = 0.0;

    float3 rayOriginWorld, rayDirWorld;
    GSGetWorldRay(clipPos, rayOriginWorld, rayDirWorld);
    float3 rayOrigin = mul(unity_WorldToObject, float4(rayOriginWorld, 1.0)).xyz;
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
    float4 invRotation = conj_q(splatRotation);
    float3 rayOriginLocal = q_rotate(rayOrigin - splatPos, invRotation) * invScale;
    float3 rayDirLocal = q_rotate(rayDir, invRotation) * invScale;

    if (!all(abs(rayOriginLocal) < GS_RAY_DEPTH_ABS_LIMIT))
    {
        return false;
    }

    float a = dot(rayDirLocal, rayDirLocal);
    float b = 2.0 * dot(rayOriginLocal, rayDirLocal);
    float c = dot(rayOriginLocal, rayOriginLocal) - 1.0;
    if (a <= DIV_EPSILON || !(a < GS_RAY_DEPTH_ABS_LIMIT) || !valid_float(b) || !valid_float(c))
    {
        return false;
    }

    float discriminant = b * b - 4.0 * a * c;
    if (discriminant < 0.0 || !valid_float(discriminant))
    {
        return false;
    }

    float sqrtDiscriminant = sqrt(discriminant);
    float invDenom = 0.5 / a;
    float tNear = (-b - sqrtDiscriminant) * invDenom;
    float tFar = (-b + sqrtDiscriminant) * invDenom;
    float t = tNear > DIV_EPSILON ? tNear : tFar;
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
#else
    uint id = geoPrimID * 32 + instanceID;
#endif
    if (id >= splatCount) {
        GS_RETURN_INVALID;
    }
    id += splatOffset; // offset for the current batch
    if (id >= actualSplatCount) {
        GS_RETURN_INVALID;
    }
    #ifdef _BACK_TO_FRONT
        id = actualSplatCount - id - 1u; // flip the order for back-to-front rendering
    #endif

#if defined(GS_COLLIDER_SOURCE_LOAD)
    SplatData splat = LoadSplatDataColliderSource(id);
    #elif defined(DEBUG_RAW_SPLAT_ORDER)
    SplatData splat = LoadSplatData(id);
    splat.id = id;
    splat.valid = true;
    #elif defined(_PRECOMPUTED_SORTING_ON)
    float3 cam_dir = mul(transpose(UNITY_MATRIX_IT_MV), float4(0, 0, 1, 0)).xyz; // camera direction in object space
    SplatData splat = LoadSplatDataPrecomputedOrder(id, cam_dir);
    #else
    SplatData splat = LoadSplatDataRenderOrder(id);
    #endif

    if (!splat.valid || (splat.color.a < _AlphaCutoff) || (splat.color.a < _AlphaCull) || any(splat.scale > _ScaleCutoff)) {
        GS_RETURN_INVALID;
    }

    float3 splatWorldPos = mul(unity_ObjectToWorld, float4(splat.mean, 1)).xyz;

#ifdef GS_COLLIDER_DEPTH_WEIGHT
    float3 colliderBoxPos = mul(_GS_ColliderWorldToBox, float4(splatWorldPos, 1.0)).xyz;
    float colliderBoxHeight = max(_GS_ColliderBoxHeight, DIV_EPSILON);
    float colliderDepth = colliderBoxHeight * 0.5 - colliderBoxPos.y;
    if (colliderDepth < 0.0 || colliderDepth > colliderBoxHeight)
    {
        GS_RETURN_INVALID;
    }
    float colliderDepthNorm = saturate(colliderDepth / colliderBoxHeight);
#endif

    float3 camToSplat = splatWorldPos - _WorldSpaceCameraPos;
    float lodMaxScale = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
    if (lodMaxScale * lodMaxScale < (_LODCull * _LODCull) * dot(camToSplat, camToSplat)) {
        GS_RETURN_INVALID;
    }

    float4 splatClipPos = mul(UNITY_MATRIX_VP, float4(splatWorldPos, 1));
    if (splatClipPos.w <= 0) {
        GS_RETURN_INVALID;
    }
    splatClipPos.xyz /= splatClipPos.w; // perspective divide
    if (all(splatClipPos.xy < -1.0) || all(splatClipPos.xy > 1.0)) {
        GS_RETURN_INVALID;
    }

    o.color = splat.color;
    o.debugColor = _GS_Colors[GetSplatCoord(splat.id)].rgb;
    #ifdef _FAKE_SRGB
        o.debugColor = GammaToLinearSpace(o.debugColor);
    #endif
    o.splatMean = splat.mean;
    o.splatRotation = splat.quat;
#ifdef GS_COLLIDER_DEPTH_WEIGHT
    o.colliderDepthNorm = colliderDepthNorm;
#endif
    float peakAlpha = o.color.a;
    float cutoffSigmaRadius = sqrt(max(-2.0 * log(_AlphaCutoff / peakAlpha), 0.0));
    float scale_max = max(splat.scale.x, max(splat.scale.y, splat.scale.z));
    float3 clamped_scale = clamp(splat.scale, scale_max * _ThinThreshold, scale_max);
    float3 projection_scale = max(clamped_scale, scale_max / PROJECTION_MAX_ANISOTROPY);
    float supportScale = _GaussianMul * cutoffSigmaRadius;
    float3 splatSupport = supportScale * projection_scale;
    o.splatSupport = splatSupport;

    if (o.color.a < _AlphaCutoff) {
        GS_RETURN_INVALID;
    }

    // Project the ellipsoid onto the screen
    Ellipse ell = GetProjectedEllipsoid(splat.mean, splatSupport, splat.quat);

    if(!valid_ellipse(ell) || any(ell.size > 1.75)) {
        GS_RETURN_INVALID;
    }

#if !defined(GS_COLLIDER_DEPTH_WEIGHT)
    float3 cameraPosObject = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
    o.color.rgb = EvaluateSplatSHColor(splat.id, splat.color.rgb, splat.mean, cameraPosObject);
    o.color.rgb = shift_color(o.color.rgb) * _Exposure;
    #ifdef _FAKE_SRGB
        o.color.rgb = GammaToLinearSpace(o.color.rgb);
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
#endif

    float area = ell.size.x * ell.size.y;
#ifdef GS_COLLIDER_DEPTH_WEIGHT
    float2 screenParams = max(_GS_ColliderScreenParams.xy, 1.0.xx);
#else
    float2 screenParams = _ScreenParams.xy;
#endif
    ell.size = max(ell.size * screenParams, 1.75 * _AntiAliasing) / screenParams; // ensure minimum size
    float areaPost = ell.size.x * ell.size.y;
    float areaScale = area / areaPost;
    o.color.a *= areaScale; // scale alpha by area ratio
    o.gaussianExp = 0.5 * cutoffSigmaRadius * cutoffSigmaRadius;

#ifdef GS_NO_GEOM
    uint vtxID = v.vertexID & 3u;
#else
    [unroll] for (uint vtxID = 0; vtxID < 4; vtxID ++)
    {
#endif
        o.quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
        float2x2 rot = float2x2(ell.axis.x, -ell.axis.y, ell.axis.y, ell.axis.x);
        float2 ndc = ell.center + mul(rot, o.quadPos * ell.size);
        o.clipPos = ndc;
#if defined(GS_COLLIDER_DEPTH_WEIGHT)
        o.position = float4(ndc, 0.5, 1.0);
#else
        float cornerDepth = splatClipPos.z;
        float rayDepth;
        if (GSTryGetRaySplatDepth(splat.mean, splatSupport, splat.quat, ndc, rayDepth))
        {
            cornerDepth = rayDepth;
        }
        o.position = float4(ndc, cornerDepth, 1.0);
#endif
#ifdef GS_NO_GEOM
        return o;
#else
        triStream.Append(o);
    }
#endif
}

//#define DEBUG_OUTLINES

#ifdef GS_DEBUG_ELLIPSOID_PASS
struct GSFragmentOutput
{
    float4 color : SV_Target;
    float depth : SV_Depth;
};

GSFragmentOutput frag(g2f input) {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    // Editor-only debug ellipsoid shader: always renders (it is only ever assigned while debugging).
    float dist2 = dot(input.quadPos, input.quadPos);
    if (dist2 > 1.0)
    {
        discard;
    }

    float surfaceDepth;
    if (!GSTryGetRayEllipsoidSurfaceDepth(input.splatMean, input.splatSupport, input.splatRotation, input.clipPos, surfaceDepth))
    {
        discard;
    }

    GSFragmentOutput output;
    output.color = float4(input.debugColor, 1.0);
    output.depth = surfaceDepth;
    return output;
}
#else
float4 frag(g2f input) : SV_Target {
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float dist2 = dot(input.quadPos, input.quadPos);
#ifdef DEBUG_OUTLINES
    return (dist2 < 1.0) ? float4(1, 0, 0, 1) : float4(0, 0, 0, 1);
#endif
    if (dist2 > 1.0)
    {
        discard;
    }
    float rho = input.color.a * exp(-input.gaussianExp * dist2);
#ifdef GS_COLLIDER_DEPTH_WEIGHT
    if (rho > 0.0)
    {
        rho = saturate(exp(log(max(rho, 1e-8)) + _GS_ColliderOpacityLogMultiplier));
    }
    return float4(rho * input.colliderDepthNorm, 0.0, 0.0, rho);
#else
    return float4(input.color.rgb * rho, rho);
#endif
}
#endif
