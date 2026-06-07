Shader "VRChatGaussianSplatting/ToLinear"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+600" }
        
        GrabPass
        {
            "_SRGBBackground"
        }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #include "FullscreenCommon.cginc"
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_SRGBBackground); 
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_LinearBackground); 
            float4 frag(v2f i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 colPostSplat = GS_SAMPLE_GRABPASS_TEXTURE(_SRGBBackground, i.uv); 

                //Fix for front to back splat rendering
                float4 colPreSplat = GS_SAMPLE_GRABPASS_TEXTURE(_LinearBackground, i.uv);
                colPostSplat.rgb -= LinearToGammaSpace(colPreSplat.rgb) * colPostSplat.a; 

                colPostSplat.rgb = GammaToLinearSpace(colPostSplat.rgb);
                return colPostSplat;
            }
            ENDCG
        }
    }

    FallBack Off
}
