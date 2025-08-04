#include "BlitCommon.cginc"
#include "GSData.cginc"
#include "GSMath.cginc"

float3 sbf(float3 c, float3 w, float s){
    //float x = sin(pi*c.x*w.x) * cos(pi*c.y*w.y) * cos(pi*c.z*w.z);
    //float y = sin(pi*c.y*w.y) * cos(pi*c.z*w.z) * cos(pi*c.x*w.x);
    //float z = sin(pi*c.z*w.z) * cos(pi*c.x*w.x) * cos(pi*c.y*w.y);
    float3 k = sin(PI*c*w) * cos(PI*c.yzx*w.yzx) * cos(PI*c.zxy*w.zxy);
    k = lerp(k, k* cross(normalize(w), normalize(float3(2,4,1))), s);
    return k;
}

float3 move(float3 p)
{
    float3 c = p;
    int iters = 8;
    int i = 0;
    const float str = 0.005 * _SinTime.w; // strength of the noise, can be animated
    const float freq = 3.0;
    for(; i<iters; i++ )
    {
        c += str * CurlNoise3D(c, freq);
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
GaussianData PropagateGaussianViaMove(GaussianData g)
{
    float4x4 M = EstimateAffineFromMove(g);
    GaussianData n = TransformGaussian(g, M);
    //n.s = ClampScale(n.s, 50.0);
    return n;
}

// -----------------------------------------------------------------------------
// 1)  Core mapper with explicit lattice dimensions
// -----------------------------------------------------------------------------
float3 HexIndexToCoord(uint index, uint3 dims)
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

    // Map to [0,1]³ – guard against degenerate extents
    return p / max(max(ext.x, ext.y), max(ext.z, 1e-6.xxx));
}

// -----------------------------------------------------------------------------
// 2)  Convenience overload – infers an almost-cubic lattice from total point count
// -----------------------------------------------------------------------------
float3 HexIndexToCoord(uint index, uint totalPoints)
{
    // Cube root → side length rounded up
    uint k = (uint)ceil(pow((float)(totalPoints + 1u), 1.0 / 3.0));

    // Number of full Z layers actually needed
    uint Nz = (totalPoints + k * k - 1u) / (k * k);

    return HexIndexToCoord(index, uint3(k, k, Nz));
}

GaussianData GenerateGaussian(uint id) {
    GaussianData g;
    uint seed = id;
    
    uint zsqrt = 6;
    uint3 gridsize = uint3(_ActualSplatCountSqrt / zsqrt, _ActualSplatCountSqrt / zsqrt, zsqrt*zsqrt);
    float3 gridpos = HexIndexToCoord(id, gridsize);
    g.P = gridpos * 2.0 - 1.0; // random position in [-1,1]^3
   
    float4 noise = os2NoiseWithDerivatives_ImproveXY(2.0*g.P);
    float3 col = abs(noise.w)*1.5*float3(0.7, 0.8, 0.9); // random position in [-1,1]^3
    col = pow(col, 1.2);
   
    const float threshold = 0.95;

    float star = smoothstep(threshold - 0.002, threshold, rand(seed));
    col += 120.0 * star * ((noise.w > 0.3) ? float3(0.1, 0.4, 1.0) : float3(1.0, 0.4, 0.1)); // blue or orange star color
    float scale = (star > 0.01) ? 0.0005 : 0.007;
    g.RS = Diag3x3(scale); // small scale
    g.C = float4(col, 0.07); // density
    return g;
}