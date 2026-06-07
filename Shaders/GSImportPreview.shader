Shader "Hidden/VRChatGaussianSplatting/ImportPreviewSplat"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 corner : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float4 color : COLOR;
                float2 corner : TEXCOORD0;
                float3 objectPos : TEXCOORD1;
            };

            float _PointSize;
            float _CropEnabled;
            float3 _CropMin;
            float3 _CropMax;

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.position.xy += v.corner * _PointSize * 2.0 * o.position.w / _ScreenParams.xy;
                o.color = v.color;
                o.corner = v.corner;
                o.objectPos = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float gaussian = exp(-dot(i.corner, i.corner) * 3.0);
                float inside = step(_CropMin.x, i.objectPos.x) * step(i.objectPos.x, _CropMax.x)
                    * step(_CropMin.y, i.objectPos.y) * step(i.objectPos.y, _CropMax.y)
                    * step(_CropMin.z, i.objectPos.z) * step(i.objectPos.z, _CropMax.z);
                float excluded = _CropEnabled * (1.0 - inside);
                fixed4 color = lerp(i.color, fixed4(1.0, 0.12, 0.02, 0.8), excluded);
                color.a *= gaussian;
                clip(color.a - 0.01);
                return color;
            }
            ENDCG
        }
    }

    FallBack Off
}
