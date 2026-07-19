Shader "Hidden/GaussianSplatting/LODCombine"
{
    // Combines all scene splats into the world-space sort/render RTs in one pass set. Every output splat is
    // resolved by the 2D selection descent (there is no fixed identity region). Each splat is read from the
    // single fused source set, dequantized via per-chunk bounds, view-dependent SH applied, then transformed
    // by its object's matrix from the per-object transform data-texture.
    Properties
    {
        [HideInInspector] _SHBand ("SH Band", Float) = 3
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "GaussianTransform.cginc"
        #include "GSLODSelect.cginc"

        Texture2D _LODFusedPositions, _LODFusedColors, _LODFusedRotations, _LODFusedScales;
        Texture2D<float4> _LODFusedTransforms; // (objects x 9): 0-2 L2W, 3-5 W2L, 6 quat, 7 scale+active, 8 decode(log2MinScale,opacity,..)
        int _LODFusedCoordShift;   // log2(blocksPerRow) of the fused LOD source texture
        int _LODFusedCoordMask;    // blocksPerRow - 1
        float4 _LODCombineOutputParams; // x = unused (0), y = combined coord shift, z/w unused
        float3 _LODCameraPosWorld;

        // View-dependent SH. _LODShParams (objects x 4 cols): col0 (coeffCount, n, shBaseHi, shBaseLo),
        // col1 (shMin.xyz, shBand), col2 (shRange.xyz, 0), col3 (objSrcBaseHi, objSrcBaseLo, 0, 0).
        Texture2D _LODFusedSH;
        Texture2D<float4> _LODShParams;
        int _LODFusedShCoordShift, _LODFusedShCoordMask;
        float _SHBand;
        int _LODDebugColors; // when nonzero, tint splats by their effective LOD level

        float3 GSLODDebugColor(float lodLevel)
        {
            float level = fmod(max(0.0, floor(lodLevel + 0.5)), 8.0);
            if (level < 0.5) return float3(0.0, 0.85, 1.0);
            if (level < 1.5) return float3(0.1, 1.0, 0.35);
            if (level < 2.5) return float3(1.0, 0.9, 0.1);
            if (level < 3.5) return float3(1.0, 0.45, 0.05);
            if (level < 4.5) return float3(1.0, 0.1, 0.1);
            if (level < 5.5) return float3(0.8, 0.2, 1.0);
            if (level < 6.5) return float3(0.25, 0.45, 1.0);
            return float3(1.0, 1.0, 1.0);
        }

        uint CombinedOutputIndex(uint2 pixel)
        {
            uint bx = pixel.x >> 2, by = pixel.y >> 2;
            uint bi = bx | (by << (uint)_LODCombineOutputParams.y);
            return (bi << 4) | ((pixel.y & 3u) << 2) | (pixel.x & 3u);
        }

        uint2 LODFusedCoord(uint index)
        {
            uint bi = index >> 4;
            uint bx = bi & (uint)_LODFusedCoordMask;
            uint by = bi >> (uint)_LODFusedCoordShift;
            return uint2((bx << 2) | (index & 3u), (by << 2) | ((index >> 2) & 3u));
        }

        uint2 LODFusedShCoord(uint index)
        {
            uint bi = index >> 4;
            uint bx = bi & (uint)_LODFusedShCoordMask;
            uint by = bi >> (uint)_LODFusedShCoordShift;
            return uint2((bx << 2) | (index & 3u), (by << 2) | ((index >> 2) & 3u));
        }

        float3 GSDecodeFusedSH(float4 shLayout, float4 shMin, float4 shRange, uint id, int coeffIndex)
        {
            int coeffCount = (int)shLayout.x;
            if (coeffIndex < 0 || coeffIndex >= coeffCount) return 0.0;
            uint linearIndex = (uint)coeffIndex * (uint)shLayout.y + id;
            return shMin.xyz + _LODFusedSH[LODFusedShCoord(linearIndex)].rgb * shRange.xyz;
        }
        #define GS_DECODE_SH(id, coeffIndex) GSDecodeFusedSH(shLayout, shMin, shRange, id, coeffIndex)
        #define GS_DECODE_SH_PARAMETERS float4 shLayout, float4 shMin, float4 shRange,
        #include "GaussianSplatShEval.cginc"
        #undef GS_DECODE_SH_PARAMETERS
        #undef GS_DECODE_SH

        // Apply view-dependent SH for object objId to baseColor.rgb. localWithinObject = splat index within
        // the object's source. W2L = object's world->local matrix (rows 3-5 of the transform table).
        float3 LODApplySH(uint objId, uint localWithinObject, float3 baseColor, float3 objPos)
        {
            float4 p0 = _LODShParams[uint2(0, objId)];
            int coeffCount = (int)p0.x;
            if (coeffCount <= 0) return baseColor;
            float4 p1 = _LODShParams[uint2(1, objId)];
            float4 p2 = _LODShParams[uint2(2, objId)];
            float4 shLayout = float4(p0.x, p0.y, 0, 0);
            float4 shMin = float4(p1.xyz, 0);
            float4 shRange = float4(p2.xyz, 0);
            float shBand = min(p1.w, _SHBand);
            uint id = (uint)p0.z * 4096u + (uint)p0.w + localWithinObject;
            float4x4 w2l = float4x4(_LODFusedTransforms[uint2(objId, 3)], _LODFusedTransforms[uint2(objId, 4)], _LODFusedTransforms[uint2(objId, 5)], float4(0, 0, 0, 1));
            float3 cameraPosObject = mul(w2l, float4(_LODCameraPosWorld, 1.0)).xyz;
            return GSEvaluateDecodedSHColor(shLayout, shMin, shRange, baseColor, objPos, cameraPosObject, shBand, id);
        }

        float3 LODDequant(uint2 coord, uint2 mc)
        {
            uint4 b = (uint4)round(saturate(_LODFusedPositions[coord]) * 255.0);
            uint qx = b.r | ((b.a & 3u) << 8);
            uint qy = b.g | (((b.a >> 2) & 3u) << 8);
            uint qz = b.b | (((b.a >> 4) & 3u) << 8);
            float3 mn = _LODChunkBounds[mc].xyz;
            float3 mx = _LODChunkBounds[mc + uint2(0u, GSLODMetaHeight())].xyz;
            return lerp(mn, mx, float3(qx, qy, qz) * (1.0 / 1023.0));
        }

        float4x4 LODMatrix(uint k)
        {
            return float4x4(_LODFusedTransforms[uint2(k, 0)], _LODFusedTransforms[uint2(k, 1)], _LODFusedTransforms[uint2(k, 2)], float4(0, 0, 0, 1));
        }

        bool LODLoad(uint2 pixel, out float3 wp, out float4 wq, out float3 ws, out float4 col, bool wantColor)
        {
            wp = 0; wq = float4(0, 0, 0, 1); ws = 0; col = 0;
            uint ci = CombinedOutputIndex(pixel);

            uint objId, localWithinObject;
            uint2 coord;
            float3 objPos;

            // Scene-global 2D mip-descent over every object's chunks.
            uint lodLocal = ci;
            uint chunkIndex, gsi; float lodLevel;
            if (!GSLODResolveChunkUnified(lodLocal, chunkIndex, objId, gsi, lodLevel)) return false;

            coord = LODFusedCoord(gsi);
            uint2 mc = GSLODUnifiedMetaCoord(chunkIndex);
            float4 scaleActive = _LODFusedTransforms[uint2(objId, 7)];
            if (scaleActive.w < 0.5) return false; // inactive object
            float4 decode = _LODFusedTransforms[uint2(objId, 8)];
            uint objSrcBase = (uint)round(_LODShParams[uint2(3, objId)].x) * 4096u + (uint)round(_LODShParams[uint2(3, objId)].y);
            localWithinObject = gsi - objSrcBase;

            GaussianTransformData g;
            g.position = LODDequant(coord, mc);
            g.rotation = normalize(lerp(-1.0, 1.0, _LODFusedRotations[coord]));
            g.scale = max(exp2(decode.x), _LODFusedScales[coord].xyz);
            objPos = g.position;
            g = GSTransformGaussian(g, LODMatrix(objId), _LODFusedTransforms[uint2(objId, 6)], scaleActive.xyz);
            wp = g.position; wq = g.rotation; ws = g.scale;
            if (wantColor)
            {
                col = _LODFusedColors[coord]; col.a *= decode.y;
                col.rgb = LODApplySH(objId, localWithinObject, col.rgb, objPos);
                if (_LODDebugColors != 0) col.rgb = saturate(lerp(col.rgb, GSLODDebugColor(lodLevel), 0.25));
            }
            return true;
        }

        float4 fragPositions(v2f_img i) : SV_Target { float3 wp; float4 wq; float3 ws; float4 c; if (!LODLoad(uint2(i.pos.xy), wp, wq, ws, c, false)) discard; return float4(wp, 1.0); }
        float4 fragRotations(v2f_img i) : SV_Target { float3 wp; float4 wq; float3 ws; float4 c; if (!LODLoad(uint2(i.pos.xy), wp, wq, ws, c, false)) discard; return wq * 0.5 + 0.5; }
        float4 fragScales(v2f_img i)    : SV_Target { float3 wp; float4 wq; float3 ws; float4 c; if (!LODLoad(uint2(i.pos.xy), wp, wq, ws, c, false)) discard; return float4(ws, 1.0); }
        float4 fragColors(v2f_img i)    : SV_Target { float3 wp; float4 wq; float3 ws; float4 c; if (!LODLoad(uint2(i.pos.xy), wp, wq, ws, c, true)) discard; return c; }
        ENDCG

        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma shader_feature_local _LOD_PACKED_POSITIONS_ON
            #pragma vertex vert_img
            #pragma fragment fragPositions
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragRotations
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragScales
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragColors
            ENDCG }
    }
    Fallback Off
}
