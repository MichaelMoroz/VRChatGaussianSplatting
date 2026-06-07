Shader "VRChatGaussianSplatting/ToSRGB"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+499" }
        
        GrabPass
        {
            "_LinearBackground"
        }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #include "FullscreenCommon.cginc"
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_LinearBackground); 
            float4 frag(v2f i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 col = GS_SAMPLE_GRABPASS_TEXTURE(_LinearBackground, i.uv); 
                return fixed4(LinearToGammaSpace(col.rgb), 0.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
