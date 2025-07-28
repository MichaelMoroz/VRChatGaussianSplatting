#include "ExFloat.cginc"
#include "SVD.cginc"

#define DIV_EPSILON 1e-6

float safe_divide(float a, float b) {
    return (abs(b) > DIV_EPSILON) ? a / b : 0.0;
}

float safe_sqrt(float a) {
    return (a > 0.0) ? sqrt(a) : 0.0;  // return 0 for negative inputs
}

struct Ellipse {
    float2 center;
    float2 axis;
    float2 size;
};

Ellipse extractEllipse(float a, float b, float c, float d, float e, float f) {
    float delta = c * c - 4.0 * a * b;
    float h = safe_divide(2.0 * b * d - c * e, delta);
    float k = safe_divide(2.0 * a * e - c * d, delta);

    float Fp = a * h * h + b * k * k + c * h * k + d * h + e * k + f;

    float diff_ba = b - a;
    float sum_ba  = b + a;
    float J = sqrt(diff_ba * diff_ba + c * c);

    float lambda1 = (sum_ba + J) * 0.5;
    float lambda2 = (sum_ba - J) * 0.5;

    float r = safe_divide(diff_ba, c);
    float ca = safe_divide(0.5 * sign(c), sqrt(1.0 + r * r));
    float ch = sqrt(0.5 + ca) * sqrt(0.5);
    float sh = sqrt(0.5 - ca) * sqrt(0.5) * sign(diff_ba);
    float cos_theta = ch - sh;
    float sin_theta = ch + sh;

    float a1 = safe_sqrt(-safe_divide(Fp, lambda1));
    float a2 = safe_sqrt(-safe_divide(Fp, lambda2));

    Ellipse ellipse;
    ellipse.center = float2(h, k);
    ellipse.axis   = float2(cos_theta, sin_theta);
    ellipse.size   = float2(a1, a2);
    return ellipse;
}

float4x4 CreateClipToViewMatrix()
{
    float4x4 flipZ = float4x4(1, 0, 0, 0,
                              0, 1, 0, 0,
                              0, 0, -1, 1,
                              0, 0, 0, 1);
    float4x4 scaleZ = float4x4(1, 0, 0, 0,
                               0, 1, 0, 0,
                               0, 0, 2, -1,
                               0, 0, 0, 1);
    float4x4 invP = unity_CameraInvProjection;
    float4x4 flipY = float4x4(1, 0, 0, 0,
                              0, _ProjectionParams.x, 0, 0,
                              0, 0, 1, 0,
                              0, 0, 0, 1);

    float4x4 result = mul(scaleZ, flipZ);
    result = mul(invP, result);
    result = mul(flipY, result);
    result._24 *= _ProjectionParams.x;
    result._42 *= -1;
    return result;
}

float4x4 Translation(float3 t) {
    return float4x4(1, 0, 0, t.x,
                    0, 1, 0, t.y,
                    0, 0, 1, t.z,
                    0, 0, 0, 1);
}

float4x4 RotationScaleInverse(float4 q, float3 s) {
    float3x3 R = q2m(q);
    float3x3 Rt = transpose(R);
    float3x3 Pinv = float3x3(Rt[0] / s.x, Rt[1] / s.y, Rt[2] / s.z);
    return float4x4(Pinv[0], 0, Pinv[1], 0, Pinv[2], 0, 0, 0, 0, 1);
}

float4x4 InvGaussianTransform(Gaussian g) {
    float4x4 T_inv = Translation(-g.p);
    float4x4 R_inv = RotationScaleInverse(g.q, g.s);
    return mul(R_inv, T_inv);
}

float4x4 RotationScale(float4 q, float3 s) {
    float3x3 R = q2m(q);
    return float4x4(R[0] * s.x, 0, R[1] * s.y, 0, R[2] * s.z, 0, 0, 0, 0, 1);
}

float4x4 GaussianTransform(Gaussian g) {
    float4x4 T = Translation(g.p);
    float4x4 R = RotationScale(g.q, g.s);
    return mul(T, R);
}

float dotM(float4 a, float4 b)
{
    static const float4 s = float4(1.0, 1.0, 1.0, -1.0);
    return dot(a * s, b);
}

Ellipse GetProjectedEllipsoid(Gaussian g) {
    float4x4 S_inv   = InvGaussianTransform(g);
    float4x4 P_inv   = CreateClipToViewMatrix(); // inverse(UNITY_MATRIX_P)
    float4x4 MV_inv  = transpose(UNITY_MATRIX_IT_MV);
    float4x4 inv = mul(S_inv, mul(MV_inv, P_inv));

    float4x4 Q0 = float4x4(
        1,0,0,0,
        0,1,0,0,
        0,0,1,0,
        0,0,0,-1
    );

    double_4x4 Q_df = dmat_mul(to_dmat(transpose(inv)), to_dmat(mul(Q0, inv)));

    // 1) extract the 1×1 scalar A and the 3-vector B from Q_df
    float2 A_df = Q_df.m[2][2];
    float2 B0   = Q_df.m[0][2];
    float2 B1   = Q_df.m[1][2];
    float2 B2   = Q_df.m[3][2];

    // 2) extract the 3×3 submatrix C
    float2 C_df[3][3];
    C_df[0][0] = Q_df.m[0][0];  C_df[0][1] = Q_df.m[0][1];  C_df[0][2] = Q_df.m[0][3];
    C_df[1][0] = Q_df.m[1][0];  C_df[1][1] = Q_df.m[1][1];  C_df[1][2] = Q_df.m[1][3];
    C_df[2][0] = Q_df.m[3][0];  C_df[2][1] = Q_df.m[3][1];  C_df[2][2] = Q_df.m[3][3];

    // 3) compute outer = B ⊗ B, and scalarC = A * C, then C2 = outer - scalarC
    float2 outer_df[3][3];
    float2 scalarC_df[3][3];
    float2 C2_df[3][3];

    [unroll]
    for (uint i = 0; i < 3; ++i) {
        // pick the i-th component of B
        float2 Bi = (i == 0 ? B0 : (i == 1 ? B1 : B2));

        // outer product row i
        outer_df[i][0] = df64_mul(Bi, B0);
        outer_df[i][1] = df64_mul(Bi, B1);
        outer_df[i][2] = df64_mul(Bi, B2);

        // A * C row i
        scalarC_df[i][0] = df64_mul(A_df, C_df[i][0]);
        scalarC_df[i][1] = df64_mul(A_df, C_df[i][1]);
        scalarC_df[i][2] = df64_mul(A_df, C_df[i][2]);

        // difference row i
        C2_df[i][0] = df64_sub(outer_df[i][0], scalarC_df[i][0]);
        C2_df[i][1] = df64_sub(outer_df[i][1], scalarC_df[i][1]);
        C2_df[i][2] = df64_sub(outer_df[i][2], scalarC_df[i][2]);
    }

    // 4) pull off the six scalar coefficients (hi-parts) and call extractEllipse

    // normalize the coefficients to avoid numerical issues
    float _max = 1.0 / max(C2_df[0][0].x, C2_df[1][1].x); 
    float _a = C2_df[0][0].x * _max;
    float _b = C2_df[0][1].x * 2.0 * _max;
    float _c = C2_df[1][1].x * _max;
    float _d = C2_df[0][2].x * 2.0 * _max;
    float _e = C2_df[1][2].x * 2.0 * _max;
    float _f = C2_df[2][2].x * _max;
    
    return extractEllipse(_a, _c, _b, _d, _e, _f);
} 

Gaussian TransformGaussian(Gaussian g, float4x4 M)
{
    float3x3 R = q2m(g.q);

    // covariance = R * diag(s²) * Rᵀ
    float3x3 S2 = float3x3(g.s.x * g.s.x, 0, 0,
                           0, g.s.y * g.s.y, 0,
                           0, 0, g.s.z * g.s.z);
    float3x3 Sigma = mul(R, mul(S2, transpose(R)));

    // split affine
    float3x3 A = (float3x3)M;
    float3   t = M[3].xyz;

    // propagate mean
    float3 pOut = mul(A, g.p) + t;

    // propagate covariance
    float3x3 SigmaP = mul(A, mul(Sigma, transpose(A)));

    // eigen/SVD: SigmaP = U * diag(D) * Uᵀ
    float3x3 U, V;
    float3   D;
    GetSVD3D(SigmaP, U, D, V);

    // enforce right‑handed frame
    if (determinant(U) < 0) U[0] = -U[0];

    Gaussian result;
    result.s = sqrt(D);      // new scales
    result.q = m2q(U);       // new orientation
    if (result.q.w < 0) result.q = -result.q;
    result.p = pOut;         // new mean
    return result;
}