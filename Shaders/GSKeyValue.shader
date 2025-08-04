Shader "VRChatGaussianSplatting/ComputeKeyValue" {
    Properties {
        [HideInInspector] _GS_PackedPositions ("Packed Positions", 2D) = "" {}
        [HideInInspector] _CameraPos ("Camera Position", Vector) = (0, 0, 0, 0)
        [HideInInspector] _MinMaxSortDistance ("Min Max Sort Distance", Vector) = (0, 0, 0, 0)
        [HideInInspector] _ElementCount ("Element Count", Int) = 0
        [HideInInspector] _KeyScale ("Key Scale", Float) = 1.0
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

            float4x4 _SplatToWorld;
            float3 _CameraPos;
            float _KeyScale;

            uint float2fixed(float v) {
                if(isnan(v) || isinf(v)) {
                    return 0xFFFFFFFF; // Return max value for NaN or Inf
                }
                return round(clamp(v, 0.0, 1.0) * _KeyScale);
            }

            uint ComputeD(uint id) {
                GaussianData splat = LoadPackedSplatData(id);
                float3 splat_pos = mul(_SplatToWorld, float4(splat.P, 1.0)).xyz;
                float dist = length(_CameraPos - splat_pos);
                float dist_norm = (dist - _MinMaxSortDistance.x) / (_MinMaxSortDistance.y - _MinMaxSortDistance.x);
                return float2fixed(sqrt(dist_norm)); //Front to back sorting
                //return float2fixed(1.0 - sqrt(dist_norm)); //Back to front sorting
            }

            float2 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint index = UVToIndex(pixel);
                if (index >= _ElementCount) discard;
                return float2(index, ComputeD(index));
            }
            ENDCG
        }
    }
    Fallback Off
}