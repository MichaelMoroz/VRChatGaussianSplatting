Shader "Hidden/GaussianSplatting/FuseConcat"
{
    // Phase 3: GPU fuse concatenation. Gathers one source "file" into its region of a fused texture so
    // the editor bake avoids the per-source CPU readback + the multi-million-splat re-pack loop. For each
    // fused output texel: globalIndex = block-swizzle(pixel); if it falls in this file's range
    // [base, base+count) the source splat (base offset subtracted) is fetched at its block-swizzled
    // source coord and written; otherwise discard (so disjoint files accumulate into one RT). The
    // block-swizzle matches PlySplatImporter.ComputePackedTextureIndex / the combine shader's fused coord, so
    // a GPU-concatenated texture is bit-identical to the old CPU path. Pass-through sampling (RGBA32 byte
    // round-trip and RGB9e5 float) is exact.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            Texture2D _SrcTex;
            // x=fusedBaseHi, y=fusedBaseLo, z=countHi, w=countLo; base=hi*4096+lo, count=hi*4096+lo.
            // Both base and count are split hi/lo because either can exceed the float32 exact-integer
            // limit (2^24): a single fused object can have >16.7M splats. See vrcgs-float-texture-index-precision.
            float4 _ConcatParams;
            float4 _SrcBaseParam;          // x=srcBaseHi, y=srcBaseLo; source-texel offset added before swizzle (for interleaved gathers)
            int _SrcShift; int _SrcMask;   // source: log2(blocksPerRow), blocksPerRow-1
            int _DstShift; int _DstMask;   // fused dst: log2(blocksPerRow), blocksPerRow-1

            uint DstIndex(uint2 p)
            {
                uint bx = p.x >> 2, by = p.y >> 2;
                uint bi = bx | (by << (uint)_DstShift);
                return (bi << 4) | ((p.y & 3u) << 2) | (p.x & 3u);
            }
            uint2 SrcCoord(uint idx)
            {
                uint bi = idx >> 4;
                uint bx = bi & (uint)_SrcMask;
                uint by = bi >> (uint)_SrcShift;
                return uint2((bx << 2) | (idx & 3u), (by << 2) | ((idx >> 2) & 3u));
            }

            float4 frag(v2f_img i) : SV_Target
            {
                uint g = DstIndex(uint2(i.pos.xy));
                uint base = (uint)_ConcatParams.x * 4096u + (uint)_ConcatParams.y;
                uint count = (uint)_ConcatParams.z * 4096u + (uint)_ConcatParams.w;
                uint srcBase = (uint)_SrcBaseParam.x * 4096u + (uint)_SrcBaseParam.y;
                if (g < base || g >= base + count) discard;
                return _SrcTex[SrcCoord(g - base + srcBase)];
            }
            ENDCG
        }
    }
    Fallback Off
}
