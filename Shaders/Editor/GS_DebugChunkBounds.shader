// EDITOR-ONLY chunk bounding-box visualization. Lives in an Editor folder so it never ships in a build.
// Draws one wireframe box per chunk (12 edges) from a chunk-bounds texture pair, procedurally (no mesh):
//
//   Graphics.DrawProcedural(MeshTopology.Lines, vertexCount: 24, instanceCount: chunkCount)
//
// Bind, per LOD object (GaussianSplatLODObject.chunkBoundsMinTexture / chunkBoundsMaxTexture are RGBAFloat,
// 1 row, width = NextPOT(chunkCount), .xyz = local-space min/max):
//   mat.SetTexture("_ChunkBoundsMin", lo.chunkBoundsMinTexture);
//   mat.SetTexture("_ChunkBoundsMax", lo.chunkBoundsMaxTexture);
//   mat.SetInt("_ChunkBoundsWidth", lo.chunkBoundsMinTexture.width);
//   mat.SetInt("_ChunkCount", lo.GetChunkCount());
//   mat.SetMatrix("_LocalToWorld", lo.transform.localToWorldMatrix);  // chunk bounds are object-local
//   mat.SetColor("_Color", new Color(0,1,0,1)); mat.SetFloat("_ColorByIndex", 1);
// then DrawProcedural with that material (e.g. in a SceneView duringSceneGui handler or a CommandBuffer).
//
// Non-LOD splats store bounds as a single 2-row _GS_ChunkBounds texture (row0 = min, row1 = max); bind that
// to both _ChunkBoundsMin/_ChunkBoundsMax and set _ChunkBoundsMaxRow = 1 to read max from the second row.
Shader "Hidden/VRChatGaussianSplatting/DebugChunkBounds"
{
    Properties
    {
        [HideInInspector] _ChunkBoundsMin ("Chunk Bounds Min", 2D) = "black" {}
        [HideInInspector] _ChunkBoundsMax ("Chunk Bounds Max", 2D) = "black" {}
        _Color ("Color", Color) = (0, 1, 0, 1)
        _ColorByIndex ("Color By Chunk Index", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        ZWrite On
        ZTest LEqual
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D _ChunkBoundsMin;
            Texture2D _ChunkBoundsMax;
            int _ChunkBoundsWidth;
            int _ChunkCount;
            int _ChunkBoundsMaxRow;   // row offset for the max texture (0 = same as min, 1 = second row for 2-row _GS_ChunkBounds)
            int _CenterAreaMode;      // 0 = bbox (min/max); 1 = read (center.xyz, area) at _ChunkBoundsMaxRow and draw the equal-surface-area cube centered on the mass center
            float4x4 _LocalToWorld;
            float4 _Color;
            float _ColorByIndex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };

            // 12 box edges as pairs of corner indices (corner = 3 bits: x=bit0, y=bit1, z=bit2).
            static const uint kEdgeCorners[24] = {
                0,1, 1,3, 3,2, 2,0,   // bottom face (z-)
                4,5, 5,7, 7,6, 6,4,   // top face (z+)
                0,4, 1,5, 2,6, 3,7    // vertical edges
            };

            float3 HashColor(uint i)
            {
                // distinct-ish color per chunk index
                float3 c = frac(float3(i * 0.13731f, i * 0.27193f, i * 0.51237f) + 0.5f);
                return 0.25f + 0.75f * c;
            }

            int2 ChunkCoord(uint chunk)
            {
                uint w = (uint)max(1, _ChunkBoundsWidth);
                return int2((int)(chunk % w), (int)(chunk / w));
            }

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                v2f o;
                uint chunk = iid;
                if (chunk >= (uint)_ChunkCount)
                {
                    o.pos = float4(0, 0, 0, 0); // degenerate -> nothing drawn for padding instances
                    o.color = 0;
                    return o;
                }

                int2 minCoord = ChunkCoord(chunk);
                float3 mn, mx;
                if (_CenterAreaMode != 0)
                {
                    // (center.xyz, area) at the offset row -> cube of equal surface area centered on the mass center.
                    float4 ca = _ChunkBoundsMin.Load(int3(minCoord + int2(0, _ChunkBoundsMaxRow), 0));
                    float halfSide = 0.5 * sqrt(max(ca.w, 0.0) / 6.0);
                    mn = ca.xyz - halfSide;
                    mx = ca.xyz + halfSide;
                }
                else
                {
                    mn = _ChunkBoundsMin.Load(int3(minCoord, 0)).xyz;
                    mx = _ChunkBoundsMax.Load(int3(minCoord + int2(0, _ChunkBoundsMaxRow), 0)).xyz;
                }

                uint corner = kEdgeCorners[vid];
                float3 local = float3(
                    (corner & 1u) ? mx.x : mn.x,
                    (corner & 2u) ? mx.y : mn.y,
                    (corner & 4u) ? mx.z : mn.z);

                float3 world = mul(_LocalToWorld, float4(local, 1.0)).xyz;
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                float3 rgb = _ColorByIndex > 0.5 ? HashColor(chunk) : _Color.rgb;
                o.color = float4(rgb, _Color.a);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
    Fallback Off
}
