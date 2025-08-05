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
            #include "AnimatorCommon.cginc"
            

            float4 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint id = pixel.x + pixel.y * _ActualSplatCountSqrt;

                //SplatData splat = LoadPackedSplatData(id);
                GaussianData g = GenerateGaussian(id);
                return asfloat(PackGaussianData(g));
            }
            ENDCG
        }
    }
}