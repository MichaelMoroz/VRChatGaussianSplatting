// World-space multiscale grid that only reveals within a short distance of the surface.
// Drawn opaque: fragments off the grid lines (or beyond the reveal distance) are discarded,
// so the collider stays nearly transparent and only the thin lines occlude.
Shader "VRChatGaussianSplatting/ColliderProximityGrid"
{
    Properties
    {
        _MajorColor ("Major Line Color", Color) = (0.35, 0.95, 1.0, 1)
        _MinorColor ("Minor Line Color", Color) = (0.12, 0.45, 0.6, 1)
        _RevealDistance ("Reveal Distance (m)", Float) = 2.0
        _MajorCell ("Major Cell (m)", Float) = 1.0
        _MinorCell ("Minor Cell (m)", Float) = 0.1
        _MajorWidth ("Major Line Width (m)", Float) = 0.012
        _MinorWidth ("Minor Line Width (m)", Float) = 0.004
        [HideInInspector] _ColliderPreviewEnabled ("Collider Preview Enabled", Float) = 0
        [HideInInspector] _ColliderPreviewHeightmap ("Collider Preview Heightmap", 2D) = "black" {}
        [HideInInspector] _ColliderPreviewBoxSize ("Collider Preview Box Size", Vector) = (1, 1, 1, 0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" "IgnoreProjector"="True" }
        Cull Off
        ZWrite On
        ZTest LEqual
        Offset -1, -1 // pull the lines slightly toward the camera so they sit on top of the splats

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float previewValid : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _MajorColor, _MinorColor;
            float _RevealDistance, _MajorCell, _MinorCell, _MajorWidth, _MinorWidth;
            float _ColliderPreviewEnabled;
            sampler2D _ColliderPreviewHeightmap;
            float4 _ColliderPreviewHeightmap_TexelSize;
            float4 _ColliderPreviewBoxSize;

            bool validHeight(float height)
            {
                return height > -1e20 && height < 1e20 && height == height;
            }

            float samplePreviewHeight(float2 uv, float fallback)
            {
                float h = tex2Dlod(_ColliderPreviewHeightmap, float4(saturate(uv), 0.0, 0.0)).r;
                return validHeight(h) ? h : fallback;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 localVertex = v.vertex;
                float3 localNormal = v.normal;
                o.previewValid = 1.0;

                if (_ColliderPreviewEnabled > 0.5)
                {
                    float height = tex2Dlod(_ColliderPreviewHeightmap, float4(v.uv, 0.0, 0.0)).r;
                    bool valid = validHeight(height);
                    o.previewValid = valid ? 1.0 : 0.0;
                    float boxHeight = max(_ColliderPreviewBoxSize.y, 1e-5);
                    float clampedHeight = valid ? clamp(height, 0.0, boxHeight) : 0.0;
                    localVertex.y = -boxHeight * 0.5 + clampedHeight;

                    float2 texel = max(_ColliderPreviewHeightmap_TexelSize.xy, float2(1e-5, 1e-5));
                    float hL = samplePreviewHeight(v.uv - float2(texel.x, 0.0), clampedHeight);
                    float hR = samplePreviewHeight(v.uv + float2(texel.x, 0.0), clampedHeight);
                    float hD = samplePreviewHeight(v.uv - float2(0.0, texel.y), clampedHeight);
                    float hU = samplePreviewHeight(v.uv + float2(0.0, texel.y), clampedHeight);
                    float3 tx = float3(max(_ColliderPreviewBoxSize.x * texel.x * 2.0, 1e-5), hR - hL, 0.0);
                    float3 tz = float3(0.0, hU - hD, max(_ColliderPreviewBoxSize.z * texel.y * 2.0, 1e-5));
                    localNormal = normalize(cross(tz, tx));
                }

                o.pos = UnityObjectToClipPos(localVertex);
                o.worldPos = mul(unity_ObjectToWorld, localVertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(localNormal);
                o.uv = v.uv;
                return o;
            }

            // Center (head) eye position: in VR _WorldSpaceCameraPos is per-eye, which makes the reveal radius
            // differ slightly between eyes. Average the two stereo eye positions so both eyes mask identically.
            float3 CenterEyeWorldPos()
            {
            #if defined(USING_STEREO_MATRICES)
                return (unity_StereoWorldSpaceCameraPos[0].xyz + unity_StereoWorldSpaceCameraPos[1].xyz) * 0.5;
            #else
                return _WorldSpaceCameraPos;
            #endif
            }

            // Line coverage (0..1) on one world plane, constant world-space line width, derivative anti-aliased.
            float planeGrid(float2 uv, float cell, float halfWidth)
            {
                float2 dist = abs(frac(uv / cell + 0.5) - 0.5) * cell; // world distance to nearest line per axis
                float2 aa = fwidth(uv) + 1e-5;
                float2 lines = 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, dist);
                return max(lines.x, lines.y);
            }

            // Triplanar world grid: blend the three axis-plane grids by the surface normal.
            float worldGrid(float3 p, float3 n, float cell, float width)
            {
                float halfWidth = width * 0.5;
                float3 w = abs(normalize(n));
                w /= max(w.x + w.y + w.z, 1e-5);
                return planeGrid(p.yz, cell, halfWidth) * w.x
                     + planeGrid(p.xz, cell, halfWidth) * w.y
                     + planeGrid(p.xy, cell, halfWidth) * w.z;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                if (_ColliderPreviewEnabled > 0.5)
                {
                    if (i.previewValid < 0.5) discard;
                    float height = tex2D(_ColliderPreviewHeightmap, i.uv).r;
                    if (!validHeight(height)) discard;
                }

                float fade = 1.0;
                if (_RevealDistance < 1e8)
                {
                    float dist = distance(i.worldPos, CenterEyeWorldPos());
                    if (dist > _RevealDistance) discard;
                    fade = 1.0 - smoothstep(_RevealDistance * 0.7, _RevealDistance, dist);
                }

                float major = worldGrid(i.worldPos, i.worldNormal, _MajorCell, _MajorWidth);
                float minor = worldGrid(i.worldPos, i.worldNormal, _MinorCell, _MinorWidth);
                float grid = max(major, minor);
                if (grid * fade < 0.12) discard;

                fixed3 col = lerp(_MinorColor.rgb, _MajorColor.rgb, saturate(major));
                return fixed4(col * fade, 1.0);
            }
            ENDCG
        }
    }
}
