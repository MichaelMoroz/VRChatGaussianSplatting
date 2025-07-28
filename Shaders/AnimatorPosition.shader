Shader "VRChatGaussianSplatting/AnimatorPosition"
{
    Properties {
       _GS_PackedPositions ("Packed Positions", 2D) = "" {}
       _GS_PackedColors ("Packed Colors", 2D) = "" {}
       [HideInInspector] _ActualSplatCount ("Actual Splat Count", Int) = 0
       [HideInInspector] _ActualSplatCountSqrt ("Actual Splat Count Sqrt", Int) = 0
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
        
            float3 move(float3 p)
            {
                float3 c = p;
                int iters = 10;
                float pow1 = 2.0 + _SinTime.w;
                float pow2 = 3.0 + _CosTime.w;
                int i = 0;
                const float str = 0.05;
                const float frq = 2.5;
                float3x3 rotscale = RotationScale(normalize(float4(1,2,3,4)), float3(3.0,1.0,1));
                for(; i<iters; i++ )
                {
                    c += str * sin(frq*mul(rotscale,c) + 0.3 * c * c.zyx) * 0.05 * pow1;
                    c += str * cos(frq*float3(1.0,2.0,1.75)*c - 0.3 * c * c.yxz) * 0.05 * pow2;
                }
                return c;
            }
            
            static const float kStepFactor =1.0;
            float4x4 EstimateAffineFromMove(Gaussian g)
            {
                // world‑space σ along X,Y,Z  (needed only for step sizes)
                float3x3 R  = q2m(g.q);
                float3x3 S2 = float3x3(g.s.x*g.s.x,0,0,
                                    0,g.s.y*g.s.y,0,
                                    0,0,g.s.z*g.s.z);
                float3x3 Sigma = mul(R, mul(S2, transpose(R)));

                float3  p  = g.p;
                float3  f0 = move(p);

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
                float3x3 A = transpose(float3x3(Jc0, Jc1, Jc2));

                // correct translation so that A·p + t == f0
                float3 t = f0 - mul(A, p);

                // pack column‑major 4×4  ┌ A  t ┐
                float4x4 M;
                M[0] = float4(A[0], 0);
                M[1] = float4(A[1], 0);
                M[2] = float4(A[2], 0);
                M[3] = float4(t,   1);
                return M;
            }

            // --- one‑liner: sample‑→‑matrix‑→‑Gaussian ----------------------------------
            Gaussian PropagateGaussianViaMove(Gaussian g)
            {
                float4x4 M = EstimateAffineFromMove(g);
                float4x4 test = RotationScale(normalize(float4(1,2,3,4)), float3(3.0,1.0,1));
                return TransformGaussian(g, M);
            }

            float4 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint id = pixel.x + pixel.y * _ActualSplatCountSqrt;

                SplatData splat = LoadPackedSplatData(id);
                Gaussian g = splat.g;

                g = PropagateGaussianViaMove(g);

                return asfloat(PackGaussian(g));
            }
            ENDCG
        }
    }
}