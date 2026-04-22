Shader "VRChatGaussianSplatting/ComputeKeyValue" {
    Properties {
        [HideInInspector] _GS_Positions ("Means", 2D) = "" {}
        [HideInInspector] _CameraPos ("Camera Position", Vector) = (0, 0, 0, 0)
        [HideInInspector] _ElementCount ("Element Count", Int) = 0
        _CameraPosQuantization ("Camera Position Quantization", Range(0, 0.1)) = 0.01
    }
    SubShader {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Pass {
            ZTest Always
            Cull Off
            ZWrite Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "../RadixSort/RadixSort.cginc"
            #include "GSData.cginc"

            float3 _CameraPos;

            uint float_to_ordered_uint(float value)
            {
                uint bits = asuint(value);
                return (bits & 0x80000000u) != 0u ? ~bits : (bits | 0x80000000u);
            }

            uint ComputeD(uint id) {
                SplatData splat = LoadSplatData(id);
                float dist = length(_CameraPos - splat.mean);
                if(isnan(dist) || isinf(dist)) {
                    return 0xFFFFFFFFu;
                }
                return float_to_ordered_uint(dist); // Front to back sorting
            }

            float2 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint index = UVToIndex(pixel);
                if (index >= _ElementCount) discard;
                return float2(ASFLOAT_NO_DENORM(index), asfloat(ComputeD(index)));
            }
            ENDCG
        }
    }
    Fallback Off
}
