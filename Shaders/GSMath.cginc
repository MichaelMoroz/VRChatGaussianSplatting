#include "ExFloat.cginc"
#include "SVD.cginc"
#include "Linalg.cginc"

#define DIV_EPSILON 1e-6
#define PI 3.14159265358979323846

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

float dotM(float4 a, float4 b)
{
    static const float4 s = float4(1.0, 1.0, 1.0, -1.0);
    return dot(a * s, b);
}

void EigenSym2x2(float a, float b, float c,
    out float2 eval,   // eval.x >= eval.y
    out float2x2 evec) // columns = eigenvectors
{
    float trace = a + c;
    float det   = a * c - b * b;
    float disc  = trace * trace * 0.25 - det;
    disc = (disc < 0.0) ? 0.0 : sqrt(disc);

    float lambda1 = 0.5 * trace + disc;   // larger
    float lambda2 = 0.5 * trace - disc;

    float2 v1 = float2(b, lambda1 - a);
    float2 v2 = float2(b, lambda2 - a);

    // near-isotropic fallback avoids divide-by-zero
    if (abs(b) < 1e-6 && abs(a - c) < 1e-6)
    {
    v1 = float2(1.0, 0.0);
    v2 = float2(0.0, 1.0);
    }

    v1 = normalize(v1);
    v2 = float2(-v1.y, v1.x);             // orthogonal

    eval = float2(lambda1, lambda2);
    evec = float2x2(v1, v2);              // columns
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
    float3x3 S_inv = Scale(float3(1.0 / s.x, 1.0 / s.y, 1.0 / s.z));
    float3x3 Pinv = mul(S_inv, Rt);
    return float4x4(Pinv[0], 0, Pinv[1], 0, Pinv[2], 0, 0, 0, 0, 1);
}

float4x4 InvGaussianTransform(Gaussian g) {
    float4x4 T_inv = Translation(-g.p);
    float4x4 RS_inv = RotationScaleInverse(g.q, g.s);
    return mul(RS_inv, T_inv);
}

float4x4 To4x4(float3x3 m) {
    return float4x4(m[0], 0, m[1], 0, m[2], 0, 0, 0, 0, 1);
}

float4x4 GaussianTransform(Gaussian g) {
    float4x4 T = Translation(g.p);
    //float4x4 RS = To4x4(RotationScale(g.q, g.s));
    float4x4 RS = To4x4(CholeskyFromQS(g.q, g.s)); // Cholesky factorization
    return mul(T, RS);
}

#define OUTLINE_SAMPLES 6   // 2 points per axis

Ellipse GetProjectedGaussian(GaussianData g)
{
    // transforms ------------------------------------------------------
    float4x4  S     = mul(Translation(g.P), To4x4(g.RS));     // unit gaussian ➜ world
    float4x4  MVP   = UNITY_MATRIX_MVP;
    float4x4  SMVP  = mul(MVP, S);              // gaussian ➜ clip
    float3    camWS = _WorldSpaceCameraPos;

    // six canonical directions in unit gaussian ------------------------
    static const float SCALE = sqrt(1.5);   //???
    static const float3 AXIS[OUTLINE_SAMPLES] = {
        float3( SCALE, 0, 0), float3(-SCALE, 0, 0),
        float3( 0, SCALE, 0), float3( 0,-SCALE, 0),
        float3( 0, 0, SCALE), float3( 0, 0,-SCALE)
    };

    float2 P[OUTLINE_SAMPLES];
    bool    invalid = false;

    [unroll] for (int i = 0; i < OUTLINE_SAMPLES; ++i)
    {
        float4 clip = mul(SMVP, float4(AXIS[i], 1.0));
        if (clip.w <= 0.0 || clip.z <= 0.0)     // behind near plane
            invalid = true;
        P[i] = clip.xy / clip.w;                // NDC xy
    }

    Ellipse ellipse;
    ellipse.center = 0.0;
    ellipse.axis   = float2(1, 0);
    ellipse.size   = 0.0;

    if (invalid)                // any sample invisible ⇒ discard
        return ellipse;

    // -----------------------------------------------------------------
    // 1) centroid
    float2 mu = 0.0;
    [loop] for (uint i = 0; i < OUTLINE_SAMPLES; ++i) mu += P[i];
    mu *= (1.0 / OUTLINE_SAMPLES);

    // 2) second moment  (population scaling: divide by N)
    float  m00 = 0.0, m01 = 0.0, m11 = 0.0;
    [loop]
    for (uint i = 0; i < OUTLINE_SAMPLES; ++i)
    {
        float2 d = P[i] - mu;
        m00 += d.x * d.x;
        m01 += d.x * d.y;
        m11 += d.y * d.y;
    }
    float s = 1.0 / OUTLINE_SAMPLES;
    m00 *= s; m01 *= s; m11 *= s;

    // 3) eigen-decomposition of the 2×2 covariance
    float2  eval;
    float2x2 evec;
    EigenSym2x2(m00, m01, m11, eval, evec);     // same helper as before

    float2 axes = sqrt(eval * 2.0);             // perimeter → semi-axes
    float  ang  = atan2(evec[1][0], evec[0][0]);

    if (axes.y > axes.x)            // enforce a ≥ b
    {
        float t = axes.x; axes.x = axes.y; axes.y = t;
        ang += 1.57079632679;       // +π/2
    }

    ellipse.center = mu; 
    ellipse.axis   = float2(sin(ang), cos(ang));
    ellipse.size   = axes.yx;

    return ellipse;
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

GaussianData TransformGaussian(GaussianData g, float4x4 M)
{
    float3x3 A = (float3x3)M; // affine transform matrix
    g.RS = Triangularize3x3_L(mul(A , g.RS)); // transform RS
    g.P = mul(M, float4(g.P, 1.0)).xyz; // transform position
    float volumeScale = abs(determinant(A));
    g.C.w = g.C.w / max(0.001,volumeScale); // scale color and density by determinant of affine transform
    return g;
    // float3x3 R = q2m(g.q);

    // // covariance = R * diag(s²) * Rᵀ
    // float3x3 S2 = float3x3(g.s.x * g.s.x, 0, 0,
    //                        0, g.s.y * g.s.y, 0,
    //                        0, 0, g.s.z * g.s.z);
    // float3x3 Sigma = mul(R, mul(S2, transpose(R)));

    // // split affine
    // float3x3 A = (float3x3)M;
    // float3   t = M[3].xyz;

    // // propagate mean
    // float3 pOut = mul(A, g.p) + t;

    // // propagate covariance
    // float3x3 SigmaP = mul(A, mul(Sigma, transpose(A)));

    // // eigen/SVD: SigmaP = U * diag(D) * Uᵀ
    // float3x3 U, V;
    // float3   D;
    // GetSVD3D(SigmaP, U, D, V);

    // // enforce right‑handed frame
    // if (determinant(U) < 0) U[0] = -U[0];

    // Gaussian result;
    // result.s = sqrt(D);      // new scales
    // result.q = m2q(U);       // new orientation
    // if (result.q.w < 0) result.q = -result.q;
    // result.p = pOut;         // new mean
    // result.a = g.a / max(0.001,abs(determinant(A))); // scale density by determinant of affine transform
    // return result;
}