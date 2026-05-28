Shader "Hidden/GaussianSplatting/CombineData"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "GaussianTransform.cginc"
        #include "GaussianSplatShared.cginc"

        #define GS_SOURCE_SLOT_LIST(APPLY) \
            APPLY(0) \
            APPLY(1) \
            APPLY(2) \
            APPLY(3) \
            APPLY(4) \
            APPLY(5) \
            APPLY(6) \
            APPLY(7)

        #define GS_DECLARE_SOURCE_TEXTURES(slot) Texture2D _GS_SourcePositions##slot, _GS_SourceScales##slot, _GS_SourceRotations##slot, _GS_SourceColors##slot, _GS_SourceSH##slot;
        GS_SOURCE_SLOT_LIST(GS_DECLARE_SOURCE_TEXTURES)
        #undef GS_DECLARE_SOURCE_TEXTURES

        #define GS_DECLARE_SOURCE_VECTORS(slot) float4 _GS_SourceLayout##slot, _GS_SourceShLayout##slot, _GS_SourceDecode##slot, _GS_SourceShMin##slot, _GS_SourceShRange##slot;
        GS_SOURCE_SLOT_LIST(GS_DECLARE_SOURCE_VECTORS)
        #undef GS_DECLARE_SOURCE_VECTORS

        #define GS_DECLARE_SOURCE_MATRICES(slot) float4x4 _GS_SourceLocalToWorld##slot, _GS_SourceWorldToLocal##slot;
        GS_SOURCE_SLOT_LIST(GS_DECLARE_SOURCE_MATRICES)
        #undef GS_DECLARE_SOURCE_MATRICES

        int _CombinedCoordShift;
        float3 _CameraPosWorld;

        uint GSCombineOutputIndex(uint2 pixel)
        {
            uint blockX = pixel.x >> 2;
            uint blockY = pixel.y >> 2;
            uint blockIndex = blockX | (blockY << uint(_CombinedCoordShift));
            return (blockIndex << 4) | ((pixel.y & 3u) << 2) | (pixel.x & 3u);
        }

        bool GSResolveSourceIndex(uint combinedIndex, float4 sourceLayout, out uint sourceIndex)
        {
            uint combinedOffset = (uint)sourceLayout.z;
            uint sourceCount = (uint)sourceLayout.w;
            if (sourceCount == 0u || combinedIndex < combinedOffset || combinedIndex >= combinedOffset + sourceCount)
            {
                sourceIndex = 0u;
                return false;
            }

            sourceIndex = combinedIndex - combinedOffset;
            return true;
        }

        SplatData GSLoadSourceSplat(Texture2D positionsTexture, Texture2D scalesTexture, Texture2D rotationsTexture, Texture2D colorsTexture, float4 sourceLayout, float4 sourceDecode, uint sourceIndex)
        {
            uint2 coord = GetBlockCoord(sourceIndex, (uint)sourceLayout.x, (uint)sourceLayout.y);

            SplatData source;
            source.mean = positionsTexture[coord].xyz;
            source.scale = max(exp2(sourceDecode.x), scalesTexture[coord].xyz);
            source.quat = normalize(lerp(-1.0, 1.0, rotationsTexture[coord]));
            source.color = colorsTexture[coord];
            source.color.a *= sourceDecode.y;
            source.id = sourceIndex;
            source.valid = true;
            return source;
        }

        float3 GSDecodeSourceSH(Texture2D shTexture, float4 shMin, float4 shRange, float4 shLayout, uint id, int coeffIndex)
        {
            int coeffCount = (int)shLayout.x;
            if (coeffIndex < 0 || coeffIndex >= coeffCount)
            {
                return 0.0;
            }

            uint linearIndex = (uint)coeffIndex * (uint)shLayout.y + id;
            uint2 coord = GetBlockCoord(linearIndex, (uint)shLayout.z, (uint)shLayout.w);
            return shMin.xyz + shTexture[coord].rgb * shRange.xyz;
        }

        #define GS_DECODE_SH(id, coeffIndex) GSDecodeSourceSH(shTexture, shMin, shRange, shLayout, id, coeffIndex)
        #define GS_DECODE_SH_PARAMETERS Texture2D shTexture, float4 shMin, float4 shRange, float4 shLayout,
        #include "GaussianSplatShEval.cginc"
        #undef GS_DECODE_SH_PARAMETERS
        #undef GS_DECODE_SH

        float3 GSEvaluateSourceSHColor(Texture2D shTexture, float4 shMin, float4 shRange, float4 shLayout, float shBand, uint id, float3 sh0Color, float3 positionObject, float3 cameraPosObject)
        {
            return GSEvaluateDecodedSHColor(shTexture, shMin, shRange, shLayout, sh0Color, positionObject, cameraPosObject, shBand, id);
        }

        bool GSTryGetCombinedSource(v2f_img input, out GaussianTransformData gaussian, out float4 color)
        {
            uint sourceIndex;
            uint combinedIndex = GSCombineOutputIndex(uint2(input.pos.xy));
            #define GS_TRY_SOURCE_SLOT(slot) \
                if (GSResolveSourceIndex(combinedIndex, _GS_SourceLayout##slot, sourceIndex)) \
                { \
                    SplatData source = GSLoadSourceSplat(_GS_SourcePositions##slot, _GS_SourceScales##slot, _GS_SourceRotations##slot, _GS_SourceColors##slot, _GS_SourceLayout##slot, _GS_SourceDecode##slot, sourceIndex); \
                    gaussian.position = source.mean; \
                    gaussian.rotation = source.quat; \
                    gaussian.scale = source.scale; \
                    gaussian = GSTransformGaussian(gaussian, _GS_SourceLocalToWorld##slot); \
                    color = float4(GSEvaluateSourceSHColor(_GS_SourceSH##slot, _GS_SourceShMin##slot, _GS_SourceShRange##slot, _GS_SourceShLayout##slot, _GS_SourceDecode##slot.z, source.id, source.color.rgb, source.mean, mul(_GS_SourceWorldToLocal##slot, float4(_CameraPosWorld, 1.0)).xyz), source.color.a); \
                    return true; \
                }
            GS_SOURCE_SLOT_LIST(GS_TRY_SOURCE_SLOT)
            #undef GS_TRY_SOURCE_SLOT
            gaussian.position = 0.0;
            gaussian.rotation = float4(0.0, 0.0, 0.0, 1.0);
            gaussian.scale = 0.0;
            color = 0.0;
            return false;
        }

        float4 fragPositions(v2f_img input) : SV_Target
        {
            GaussianTransformData gaussian;
            float4 color;
            if (!GSTryGetCombinedSource(input, gaussian, color)) discard;

            return float4(gaussian.position, 1.0);
        }

        float4 fragRotations(v2f_img input) : SV_Target
        {
            GaussianTransformData gaussian;
            float4 color;
            if (!GSTryGetCombinedSource(input, gaussian, color)) discard;

            return gaussian.rotation * 0.5 + 0.5;
        }

        float4 fragScales(v2f_img input) : SV_Target
        {
            GaussianTransformData gaussian;
            float4 color;
            if (!GSTryGetCombinedSource(input, gaussian, color)) discard;

            return float4(gaussian.scale, 1.0);
        }

        float4 fragColors(v2f_img input) : SV_Target
        {
            GaussianTransformData gaussian;
            float4 color;
            if (!GSTryGetCombinedSource(input, gaussian, color)) discard;

            return color;
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment fragPositions
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment fragRotations
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment fragScales
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert_img
            #pragma fragment fragColors
            ENDCG
        }
    }
    Fallback Off
}