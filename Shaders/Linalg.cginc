#ifndef LINALG_CGINC
#define LINALG_CGINC

#include "Quaternion.cginc"

float3x3 Diag3x3(float3 d) {
    return float3x3(d.x, 0, 0, 0, d.y, 0, 0, 0, d.z);
}

float FrobeniusNorm3x3(float3x3 m) {
    return sqrt(dot(m[0], m[0]) + dot(m[1], m[1]) + dot(m[2], m[2]));
}

// ============================================================================
// 1)  Cholesky factor — lower-triangular     (A = L · Lᵀ)
// ============================================================================
float3x3 Cholesky3x3_L(float3x3 A)          // SPD symmetric 3×3
{
    const float eps = 1e-8;

    float l00 = sqrt(A[0][0]);                             if (l00 < eps) return 0;
    float l10 = A[1][0] / l00;

    float l11sq = A[1][1] - l10 * l10;                     if (l11sq < eps) return 0;
    float l11   = sqrt(l11sq);

    float l20 = A[2][0] / l00;
    float l21 = (A[2][1] - l20 * l10) / l11;

    float l22sq = A[2][2] - l20*l20 - l21*l21;             if (l22sq < eps) return 0;
    float l22   = sqrt(l22sq);

    return float3x3(
        l00, 0.0, 0.0,
        l10, l11, 0.0,
        l20, l21, l22
    );
}

float3x3 Triangularize3x3_L(float3x3 M)  // rows → lower-triangular
{
    const float eps = 1e-8;
    float3 r0 = M[0];
    float  l00 = length(r0);                     if (l00<eps) return 0;
    float3 q0  = r0 / l00;

    float3 r1 = M[1];
    float  l10 = dot(r1, q0);
    float3 v1  = r1 - l10*q0;
    float  l11 = length(v1);                     if (l11<eps) return 0;
    float3 q1  = v1 / l11;

    float3 r2 = M[2];
    float  l20 = dot(r2, q0);
    float  l21 = dot(r2, q1);
    float3 v2  = r2 - l20*q0 - l21*q1;
    float  l22 = length(v2);                     if (l22<eps) return 0;

    return float3x3(
        l00, 0.0, 0.0,
        l10, l11, 0.0,
        l20, l21, l22
    );
}


float3x3 Scale(float3 s) {
    return float3x3(s.x, 0, 0, 0, s.y, 0, 0, 0, s.z);
}

float3x3 RotationScale(float4 q, float3 s) {
    float3x3 R = q2m(q);
    float3x3 S = Scale(s);
    return mul(R, S);
}

float3x3 CholeskyFromQS(float4 q, float3 sigma)
{
    return Triangularize3x3_L(RotationScale(q, sigma));
}


// ============================================================================
// 3)  Inverse of a lower-triangular 3×3
//     (returns lower-triangular result as well)
// ============================================================================
float3x3 Invert3x3_L(float3x3 L)
{
    float3x3 V;            // will also be lower-triangular

    V[0][0] = 1.0 / L[0][0];

    V[1][0] = -L[1][0] * V[0][0] / L[1][1];
    V[1][1] = 1.0 / L[1][1];

    V[2][0] = (L[1][0]*L[2][1] - L[2][0]*L[1][1]) * V[0][0] / L[2][2];
    V[2][1] = -L[2][1] / L[2][2];
    V[2][2] = 1.0 / L[2][2];

    // zero upper part just in case
    V[0][1] = V[0][2] = V[1][2] = 0.0;
    return V;
}

#endif // LINALG_CGINC