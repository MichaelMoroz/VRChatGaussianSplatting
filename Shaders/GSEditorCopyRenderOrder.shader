Shader "Hidden/GaussianSplatting/CopyRenderOrder"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D _MainTex;

            float frag(v2f_img input) : SV_Target
            {
                return tex2D(_MainTex, input.uv).r;
            }
            ENDCG
        }
    }
    Fallback Off
}
