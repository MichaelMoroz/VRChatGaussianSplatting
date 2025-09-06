Shader "VRChatGaussianSplatting/AlphaDepthMask"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+500" }
        
        GrabPass {}
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            ColorMask 0
            Stencil {
                Ref 9
                Comp Always
                Pass Replace
            }
            CGPROGRAM
            #include "FullscreenCommon.cginc"
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_GrabTexture);
            float4 frag(v2f i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 col = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_GrabTexture, i.uv.xy); 
                if(col.a < 0.99) discard;
                return 0.0;
            }
            ENDCG
        }
    }

    FallBack Off
}
