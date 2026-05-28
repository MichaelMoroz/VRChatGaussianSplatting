// Upgrade NOTE: replaced 'defined before' with 'defined (before)'

#ifndef __GS_SH_EVAL_CGINC__
#define __GS_SH_EVAL_CGINC__

#ifndef GS_DECODE_SH
    #error GS_DECODE_SH must be defined (before) including GaussianSplatShEval.cginc
#endif

#ifndef GS_DECODE_SH_PARAMETERS
    #define GS_DECODE_SH_PARAMETERS
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

float3 GSEvaluateDecodedSHColor(
    GS_DECODE_SH_PARAMETERS
    float3 sh0Color,
    float3 positionObject,
    float3 cameraPosObject,
    float shBand,
    uint id)
{
    float3 color = sh0Color;
    float3 viewDir = positionObject - cameraPosObject;
    float invLen = rsqrt(max(dot(viewDir, viewDir), 1e-8));
    float3 dir = viewDir * invLen;
    float x = dir.x;
    float y = dir.y;
    float z = dir.z;
    int resolvedBand = (int)round(saturate(shBand / 3.0) * 3.0);

    if (resolvedBand >= 1)
    {
        float3 sh1 = GS_DECODE_SH(id, 0);
        float3 sh2 = GS_DECODE_SH(id, 1);
        float3 sh3 = GS_DECODE_SH(id, 2);
        color += -SH_C1 * y * sh1
            + SH_C1 * z * sh2
            - SH_C1 * x * sh3;
    }

    float xx = x * x;
    float yy = y * y;
    float zz = z * z;
    float xy = x * y;
    float yz = y * z;
    float xz = x * z;

    if (resolvedBand >= 2)
    {
        float3 sh4 = GS_DECODE_SH(id, 3);
        float3 sh5 = GS_DECODE_SH(id, 4);
        float3 sh6 = GS_DECODE_SH(id, 5);
        float3 sh7 = GS_DECODE_SH(id, 6);
        float3 sh8 = GS_DECODE_SH(id, 7);
        color += SH_C2_0 * xy * sh4
            + SH_C2_1 * yz * sh5
            + SH_C2_2 * (2.0 * zz - xx - yy) * sh6
            + SH_C2_3 * xz * sh7
            + SH_C2_4 * (xx - yy) * sh8;
    }

    if (resolvedBand >= 3)
    {
        float3 sh9 = GS_DECODE_SH(id, 8);
        float3 shA = GS_DECODE_SH(id, 9);
        float3 shB = GS_DECODE_SH(id, 10);
        float3 shC = GS_DECODE_SH(id, 11);
        float3 shD = GS_DECODE_SH(id, 12);
        float3 shE = GS_DECODE_SH(id, 13);
        float3 shF = GS_DECODE_SH(id, 14);
        color += SH_C3_0 * y * (3.0 * x * x - y * y) * sh9
            + SH_C3_1 * x * y * z * shA
            + SH_C3_2 * y * (4.0 * z * z - x * x - y * y) * shB
            + SH_C3_3 * z * (2.0 * z * z - 3.0 * x * x - 3.0 * y * y) * shC
            + SH_C3_4 * x * (4.0 * z * z - x * x - y * y) * shD
            + SH_C3_5 * z * (x * x - y * y) * shE
            + SH_C3_6 * x * (x * x - 3.0 * y * y) * shF;
    }

    return saturate(color);
}

#endif