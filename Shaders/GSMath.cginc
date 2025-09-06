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

float4x4 To4x4(float3x3 m) {
    return float4x4(m[0], 0, m[1], 0, m[2], 0, 0, 0, 0, 1);
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

GaussianData TransformGaussian(GaussianData g, float4x4 M, bool transformVolume = true)
{
    g.P = mul(M, float4(g.P, 1.0)).xyz; // transform position

    if(transformVolume)
    {
        float3x3 A = (float3x3)M; // affine transform matrix
        float volumeScale = abs(determinant(A));
        //g.C.w = clamp(g.C.w / max(0.001,volumeScale), 0.0, 1.0); // scale color and density by determinant of affine transform
        g.RS = Triangularize3x3_L(mul(A, g.RS)); // transform RS
    }
    return g;
}