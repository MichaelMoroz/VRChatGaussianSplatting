Shader "VRChatGaussianSplatting/Animator"
{
    Properties {
        _GS_PackedPositions ("Packed Positions", 2D) = "" {}
        _GS_PackedColors ("Packed Colors", 2D) = "" {}
        [HideInInspector] _ActualSplatCount ("Actual Splat Count", Int) = 0
        [HideInInspector] _ActualSplatCountSqrt ("Actual Splat Count Sqrt", Int) = 0

        _SplatScalesLOG2 ("Splat Scales (log2)", Vector) = ( -15, 15, -15, 4 )
        _AnimationFrame ("Animation Frame", Int) = 0
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass {
            ZTest Always
            Cull Off
            ZWrite Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma enable_d3d11_debug_symbols

            #include "BlitCommon.cginc"
            #include "GSData.cginc"
            #include "GSMath.cginc"

            int _AnimationFrame;

            float3 move(float3 p)
            {
                float3 c = p;
                int iters = 1;
                int i = 0;
                const float str = 0.001;
                c += str * os2NoiseWithDerivatives_ImproveXY(c * 1.0).xyz;
                c += str * float3(0,10,0);
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

            static const float EPS = 1e-8;

            float3x3 Diag(float3 d) { return float3x3(d.x,0,0, 0,d.y,0, 0,0,d.z); }

            void JacobiPair(inout float3x3 A, inout float3x3 V, int p, int q)
            {
                float apq = A[p][q]; if (abs(apq) < 1e-12) return;
                float app = A[p][p], aqq = A[q][q];
                float tau = (aqq - app) / (2.0 * apq);
                float t = (tau >= 0.0) ? 1.0 / (tau + sqrt(1.0 + tau*tau))
                                    : 1.0 / (tau - sqrt(1.0 + tau*tau));
                float c = rsqrt(1.0 + t*t), s = t * c;

                // A = G^T A G, zero A[p][q]
                for (int k=0;k<3;k++) if (k!=p && k!=q) {
                    float aik = A[p][k], aqk = A[q][k];
                    float tik = c*aik - s*aqk, tqk = s*aik + c*aqk;
                    A[p][k]=A[k][p]=tik; A[q][k]=A[k][q]=tqk;
                }
                float app2 = c*c*app - 2.0*c*s*apq + s*s*aqq;
                float aqq2 = s*s*app + 2.0*c*s*apq + c*c*aqq;
                A[p][p]=app2; A[q][q]=aqq2; A[p][q]=A[q][p]=0.0;

                // accumulate eigenvectors
                [unroll] for (int k=0;k<3;k++){
                    float vkp = V[k][p], vkq = V[k][q];
                    V[k][p]= c*vkp - s*vkq;
                    V[k][q]= s*vkp + c*vkq;
                }
            }

            float3x3 SymmEigen3x3(float3x3 A, out float3 eval)
            {
                float3x3 V = float3x3(1,0,0, 0,1,0, 0,0,1);
                [unroll] for (int it=0; it<5; ++it) { // few sweeps suffice for SPD
                    JacobiPair(A,V,0,1);
                    JacobiPair(A,V,0,2);
                    JacobiPair(A,V,1,2);
                }
                eval = float3(A[0][0], A[1][1], A[2][2]);
                return V; // columns are eigenvectors
            }

            float3x3 Cholesky3x3(float3x3 A) // returns lower-triangular
            {
                float l00 = sqrt(max(A[0][0], EPS));
                float l10 = A[1][0]/l00;
                float l20 = A[2][0]/l00;

                float t11 = A[1][1] - l10*l10;
                float l11 = sqrt(max(t11, EPS));
                float l21 = (A[2][1] - l20*l10)/l11;

                float t22 = A[2][2] - l20*l20 - l21*l21;
                float l22 = sqrt(max(t22, EPS));

                return float3x3(
                    l00, 0,   0,
                    l10, l11, 0,
                    l20, l21, l22
                );
            }

            // Clamp anisotropy of a lower-triangular L, preserve volume (det(L))
            float3x3 ClampAnisotropy_L(float3x3 L, float maxAniso)
            {
                maxAniso = max(maxAniso, 1.0);
                float3x3 C = mul(L, transpose(L));                  // SPD shape
                float3 lam;                                         // eigenvalues of C
                float3x3 U = SymmEigen3x3(C, lam);                  // C = U diag(lam) U^T

                float3 s = sqrt(max(lam, float3(EPS,EPS,EPS)));     // principal radii
                float3 lgs = log(s);                                // log-radii
                float mu = (lgs.x + lgs.y + lgs.z) * (1.0/3.0);
                float3 x = lgs - mu;                                // zero-mean logs

                float halfLogK = 0.5 * log(maxAniso);
                x = clamp(x, -halfLogK.xxx, halfLogK.xxx);          // bound pairwise gaps
                x -= ((x.x + x.y + x.z) * (1.0/3.0));               // re-center → volume preserved

                float3 sClamped = exp(x + mu);
                float3 lamClamped = sClamped * sClamped;

                float3x3 Ccl = mul(U, mul(Diag(lamClamped), transpose(U)));
                return Cholesky3x3(Ccl);                            // lower-triangular again
            }

            float3 AddLight(float3 pos, float3 lpos, float3 col, float falloff, float falloffexp = 0.0) {
                float lightDistance = length(pos - lpos);
                float lightFalloff = exp(-falloffexp*lightDistance) / (1.0 + falloff * lightDistance * lightDistance); // simple falloff
                return col * lightFalloff; // apply light effect
            }

            GaussianData GenerateGaussian(uint id) {
                GaussianData g = GenerateUniformGrid(id, _ActualSplatCountSqrt, 5, float4(0.25, 0.25, 0.25, 0.2), 0.0);
                g.P *= float3(1.0, 0.01, 1.0);
                float4 noise = os2NoiseWithDerivatives_ImproveXY(5.0*g.P);
                float3 col = float3(0.7, 0.8, 0.9);
                col = pow(col, 1.2);
            
                const float threshold = 0.93;
                uint seed = id + 123456789u; // unique seed for each Gaussian
                float star = smoothstep(threshold - 0.002, threshold, rand(seed));
                col += 100.0 * star * ((noise.w > 0.3) ? float3(0.1, 0.4, 1.0) : float3(1.0, 0.4, 0.1)); // blue or orange star color
                g.C.xyz = col; // density
                //g.C.w *= 4.0*abs(noise.w);


                float3x3 oldRS = g.RS;
                g.C.xyz *= smoothstep(0.3, -0.3, g.P.z) * (0.5 * abs(noise.w) + 0.5);
                //g.RS = lerp(g.RS, Diag3x3(0.007), 0.5); // small scale
                g.RS = (star > 0.01) ?  g.RS*0.15 : g.RS * 10.0;
                g.C = (star > 0.01) ? float4(col, 0.15) : g.C;
                g.P *= 5.0;//(star > 0.01) ? g.P * float3(5.0, 5.0, 2.0) : g.P * 5.0;
                return g;
            }

            GaussianData MoveGaussian(GaussianData g) {
                g = PropagateGaussianViaMove(g, true);
                g.RS = ClampAnisotropy_L(g.RS, 3.0);
                return g;
            }

            struct Output { float4 c0:SV_Target0; float4 c1:SV_Target1; };

            Output frag (v2f i) {
                uint2 pixel = floor(i.pos.xy);
                uint id = pixel.x + pixel.y * _ActualSplatCountSqrt;

                GaussianData g = LoadPackedSplatData(id);
                uint seed = id + 987654321u + _AnimationFrame * 1234567u; // unique seed for each Gaussian
                if(rand(seed) < 0.001) {
                   g = GenerateGaussian(id);
                }
                g = MoveGaussian(g);
                g.C.w *= 0.999;
                Output o;
                o.c0 = asfloat(PackGaussianData(g, _SplatScalesLOG2));
                o.c1 = g.C;
                return o;
            }
            ENDCG
        }
    }
}