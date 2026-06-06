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

            Texture2D<float2> _KeyValues;

            float frag(v2f_img input) : SV_Target
            {
                return _KeyValues.Load(int3((int2)floor(input.pos.xy), 0)).r;
            }
            ENDCG
        }
    }
    Fallback Off
}
