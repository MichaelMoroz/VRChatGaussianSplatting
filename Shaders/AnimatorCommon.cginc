#include "BlitCommon.cginc"
#include "GSData.cginc"
#include "GSMath.cginc"

float3 move(float3 p)
{
    float3 c = p;
    int iters = 16;
    int i = 0;
    c += 0.025 * os2NoiseWithDerivatives_ImproveXY(c * 2.0).xyz;
    const float str = 0.02 * _SinTime.w; // strength of the noise, can be animated
    float freq = 1.0;
    for(; i<iters; i++ )
    {
        c += str * CurlNoise3D(c, freq) / freq;
        freq *= 1.2;
    }
    return c;
}

static const float kStepFactor = 1.0;
float4x4 EstimateAffineFromMove(GaussianData g)
{
    // world‑space σ along X,Y,Z  (needed only for step sizes)
    float3x3 Sigma = mul(transpose(g.RS), (g.RS)); // covariance matrix

    float3 p  = g.P;
    float3 f0 = move(p);

    // finite‑difference Jacobian in world basis
    float3 Jc0, Jc1, Jc2;
    [unroll]
    for (int i = 0; i < 3; ++i)
    {
        float  h = kStepFactor * sqrt(Sigma[i][i]);
        float3 delta =
            (i == 0) ? float3(h, 0, 0) :
            (i == 1) ? float3(0, h, 0) :
                    float3(0, 0, h);

        float3 fi   = move(p + delta);
        float3 diff = (fi - f0) / h;          // column i of Jacobian

        if (i == 0) Jc0 = diff;
        if (i == 1) Jc1 = diff;
        if (i == 2) Jc2 = diff;
    }
    float3x3 A = float3x3(Jc0, Jc1, Jc2);

    // correct translation so that A·p + t == f0
    float3 t = f0 - mul(p, A);

    // pack column‑major 4×4  ┌ A  t ┐
    float4x4 M;
    M[0] = float4(A[0], 0);
    M[1] = float4(A[1], 0);
    M[2] = float4(A[2], 0);
    M[3] = float4(t,   1);
    return transpose(M);
}

float3 ClampScale(float3 s, float maxaniso)
{
    // Clamp scale to avoid extreme anisotropy
    float volume = s.x * s.y * s.z;
    float maxs = max(s.x, max(s.y, s.z));
    float3 news = clamp(s, maxs / maxaniso, maxs);
    float newvolume = news.x * news.y * news.z;
    float ratio = pow(volume / newvolume, 1.0 / 3.0);
    return news * ratio; // scale down to match original volume
}

// --- one‑liner: sample‑→‑matrix‑→‑Gaussian ----------------------------------
GaussianData PropagateGaussianViaMove(GaussianData g, bool transformVolume = true)
{
    float4x4 M = EstimateAffineFromMove(g);
    GaussianData n = TransformGaussian(g, M, transformVolume);
    //n.s = ClampScale(n.s, 50.0);
    return n;
}

// -----------------------------------------------------------------------------
// 1)  Core mapper with explicit lattice dimensions
// -----------------------------------------------------------------------------
float4 HexIndexToCoord(uint index, uint3 dims)
{
    // Unpack dimensions
    uint Nx = dims.x, Ny = dims.y, Nz = dims.z;

    // Slice size of one Z layer
    uint slice = Nx * Ny;

    // 3-D integer grid coordinates
    uint iz   =  index / slice;
    uint rest =  index - iz * slice;
    uint iy   =  rest / Nx;
    uint ix   =  rest - iy * Nx;

    // Hexagonal spacing constants (unit edge length = 1)
    const float DX = 1.0;                     // x step
    const float DY = 0.8660254037844386;      // √3 / 2
    const float DZ = 0.8164965809277260;      // √6 / 3
    const float X_OFFSET = 0.5;               // half-step
    const float Y_OFFSET = 0.288675134594813; // √3 / 6

    // AB-layer offsets (even/odd rows → staggered columns; even/odd layers → staggered rows)
    float ox = X_OFFSET * float((iy ^ iz) & 1u);
    float oy = (iz & 1u) ? Y_OFFSET : 0.0;

    // Physical position before normalising
    float3 p = float3((float)ix + ox,
                      (float)iy * DY + oy,
                      (float)iz * DZ);

    // Bounding box extent (max value reached on each axis)
    float3 ext = float3((float)Nx - 1.0 + X_OFFSET,
                        (float)(Ny - 1) * DY + Y_OFFSET,
                        (float)(Nz - 1) * DZ);

    return float4(p - ext * 0.5, 1.0) / max(max(ext.x, ext.y), max(ext.z, 1e-6));
}

GaussianData GenerateUniformGrid(uint id, uint count, uint layerssqrt, float4 color, float randomness = 0.0) {
    uint3 gridsize = uint3(count / layerssqrt, count / layerssqrt, layerssqrt*layerssqrt);
    float4 gridpos = HexIndexToCoord(id, gridsize);

    GaussianData g;
    g.P = gridpos.xyz * 2.0 + randomness * (rand3(id) - 0.5) * gridpos.w;
    g.RS = Diag3x3(gridpos.w * 0.7);
    g.C = color;
    return g;
}

float3x3 ClampTransform(float3x3 target, float3x3 source, float maxdist) {
    float3x3 diff = target - source;
    float sourceL = FrobeniusNorm3x3(source);
    float dist = FrobeniusNorm3x3(diff);
    if (dist > maxdist * sourceL) {
        float scale = maxdist * sourceL / dist;
        return source + diff * scale; // clamp the distance
    } else {
        return target; // no clamping needed
    }
}

float3 AddLight(float3 pos, float3 lpos, float3 col, float falloff, float falloffexp = 0.0) {
    float lightDistance = length(pos - lpos);
    float lightFalloff = exp(-falloffexp*lightDistance) / (1.0 + falloff * lightDistance * lightDistance); // simple falloff
    return col * lightFalloff; // apply light effect
}

GaussianData GenerateGaussian(uint id) {
    GaussianData g = GenerateUniformGrid(id, _ActualSplatCountSqrt, 5, float4(0.25, 0.25, 0.25, 0.1), 0.0);

    float4 noise = os2NoiseWithDerivatives_ImproveXY(5.0*g.P);
    float3 col = float3(0.7, 0.8, 0.9);
    col = pow(col, 1.2);
   
    const float threshold = 0.95;
    uint seed = id + 123456789u; // unique seed for each Gaussian
    float star = smoothstep(threshold - 0.002, threshold, rand(seed));
    col += 50.0 * star * ((noise.w > 0.3) ? float3(0.1, 0.4, 1.0) : float3(1.0, 0.4, 0.1)); // blue or orange star color
    g.C.xyz = col; // density
    //g.C.w *= 4.0*abs(noise.w);


    float3x3 oldRS = g.RS;
    g = PropagateGaussianViaMove(g, true);
    g.RS = ClampTransform(g.RS, oldRS, 1.5); // clamp the scale to avoid extreme anisotropy
    g.C.xyz *= smoothstep(0.3, -0.3, g.P.z) * (0.5 * abs(noise.w) + 0.5);
    //g.RS = lerp(g.RS, Diag3x3(0.007), 0.5); // small scale
    g.RS = (star > 0.01) ?  g.RS*0.15 : g.RS * 5.0;
    g.C = (star > 0.01) ? float4(col, 0.15) : g.C;
    g.P = (star > 0.01) ? g.P * float3(5.0, 5.0, 2.0) : g.P * 5.0;
    return g;
}