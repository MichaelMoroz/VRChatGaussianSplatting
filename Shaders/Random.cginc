uint pcg(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint seed) {
    seed = pcg(seed);
    return seed / float(0xFFFFFFFFu);
}

float2 rand2(inout uint seed) {
    return float2(rand(seed), rand(seed));
}

float3 rand3(inout uint seed) {
    return float3(rand(seed), rand(seed), rand(seed));
}

// ───────────────────────────────────────────────────────────────────────────
// Permutation-polynomial hash (Stefan Gustavson)
float4 permute(float4 t)
{
    return t * (t * 34.0 + 133.0);
}

// Rhombic-dodecahedral gradient set
float3 grad(float hash)
{
    float3 cube = fmod(floor(hash / float3(1.0, 2.0, 4.0)), 2.0) * 2.0 - 1.0;

    float3 cuboct = cube;
    int idx = (int)floor(hash / 16.0);         // 0,1,2 choose axis
    if (idx == 0)      cuboct.x = 0.0;
    else if (idx == 1) cuboct.y = 0.0;
    else               cuboct.z = 0.0;

    float type = fmod(floor(hash / 8.0), 2.0);
    float3 rhomb = lerp(cube, cuboct + cross(cube, cuboct), type);

    float3 g = cuboct * 1.22474487139 + rhomb;
    g *= (1.0 - 0.042942436724648037 * type) * 3.5946317686139184;

    return g;
}

// One half of the BCC lattice — returns <∂x,∂y,∂z,value>
float4 os2NoiseWithDerivativesPart(float3 X)
{
    float3 b  = floor(X);
    float4 i4 = float4(X - b, 2.5);

    float3 v1 = b + floor(dot(i4, float4(0.25, 0.25, 0.25, 0.25)));
    float3 v2 = b + float3(1, 0, 0) + float3(-1, 1, 1)
              * floor(dot(i4, float4(-0.25, 0.25, 0.25, 0.35)));
    float3 v3 = b + float3(0, 1, 0) + float3(1, -1, 1)
              * floor(dot(i4, float4(0.25, -0.25, 0.25, 0.35)));
    float3 v4 = b + float3(0, 0, 1) + float3(1, 1, -1)
              * floor(dot(i4, float4(0.25, 0.25, -0.25, 0.35)));

    float4 hashes = permute(fmod(float4(v1.x, v2.x, v3.x, v4.x), 289.0));
    hashes = permute(fmod(hashes + float4(v1.y, v2.y, v3.y, v4.y), 289.0));
    hashes = fmod(permute(fmod(hashes + float4(v1.z, v2.z, v3.z, v4.z), 289.0)), 48.0);

    float3 d1 = X - v1, d2 = X - v2, d3 = X - v3, d4 = X - v4;

    float4 a     = max(0.75 - float4(dot(d1,d1), dot(d2,d2), dot(d3,d3), dot(d4,d4)), 0.0);
    float4 aa    = a * a;
    float4 aaaa  = aa * aa;

    float3 g1 = grad(hashes.x), g2 = grad(hashes.y),
           g3 = grad(hashes.z), g4 = grad(hashes.w);

    float4 extrap = float4(dot(d1,g1), dot(d2,g2), dot(d3,g3), dot(d4,g4));
    float4 k      = aa * a * extrap;

    float3 deriv = -8.0 * (d1 * k.x + d2 * k.y + d3 * k.z + d4 * k.w)
                   + (g1 * aaaa.x + g2 * aaaa.y + g3 * aaaa.z + g4 * aaaa.w);

    return float4(deriv, dot(aaaa, extrap));
}

// Fallback orientation (good for hiding straight grid slices)
float4 os2NoiseWithDerivatives_Fallback(float3 X)
{
    const float s = 2.0 / 3.0;
    X = dot(X, float3(s, s, s)) - X;

    float4 r = os2NoiseWithDerivativesPart(X) +
               os2NoiseWithDerivativesPart(X + 144.5);

    float t = dot(r.xyz, float3(s, s, s));
    return float4(float3(t, t, t) - r.xyz, r.w);
}

// XY-improved orientation (useful for terrain/time slices)
static const float3x3 ORTHONORMAL_MAP = float3x3(
     0.788675134594813, -0.211324865405187, -0.577350269189626,
    -0.211324865405187,  0.788675134594813, -0.577350269189626,
     0.577350269189626,  0.577350269189626,  0.577350269189626);

float4 os2NoiseWithDerivatives_ImproveXY(float3 X)
{
    X = mul(ORTHONORMAL_MAP, X);

    float4 r = os2NoiseWithDerivativesPart(X) +
               os2NoiseWithDerivativesPart(X + 144.5);

    float3 grad = mul(r.xyz, ORTHONORMAL_MAP);
    return float4(grad, r.w);
}

float3 CurlNoise3D(float3 p, float frequency)
{
    float4 n1 = os2NoiseWithDerivativesPart(p * frequency);
    float4 n2 = os2NoiseWithDerivativesPart(p * frequency + 37.0);   // any large, odd offset
    float4 n3 = os2NoiseWithDerivativesPart(p * frequency + 73.0);

    // A = (n1.w, n2.w, n3.w);   grad A = (n1.xyz, n2.xyz, n3.xyz)
    float3 dA_dx = n1.xyz;
    float3 dA_dy = n2.xyz;
    float3 dA_dz = n3.xyz;

    // ∇×A
    return float3(
        dA_dy.z - dA_dz.y,
        dA_dz.x - dA_dx.z,
        dA_dx.y - dA_dy.x);
}