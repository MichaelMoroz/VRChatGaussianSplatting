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

        #if defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
            #define GS_SOURCE_SLOT_LIST(APPLY) \
                APPLY(0)
        #else
            #define GS_SOURCE_SLOT_LIST(APPLY) \
                APPLY(0) \
                APPLY(1) \
                APPLY(2) \
                APPLY(3) \
                APPLY(4) \
                APPLY(5) \
                APPLY(6) \
                APPLY(7)
        #endif

        #define GS_DECLARE_SOURCE_TEXTURES(slot) Texture2D _GS_SourcePositions##slot, _GS_SourceScales##slot, _GS_SourceRotations##slot, _GS_SourceColors##slot, _GS_SourceSH##slot;
        GS_SOURCE_SLOT_LIST(GS_DECLARE_SOURCE_TEXTURES)
        #undef GS_DECLARE_SOURCE_TEXTURES

        #define GS_DECLARE_SOURCE_VECTORS(slot) float4 _GS_SourceLayout##slot, _GS_SourceShLayout##slot, _GS_SourceDecode##slot, _GS_SourceShMin##slot, _GS_SourceShRange##slot, _GS_SourceTransformRotation##slot, _GS_SourceTransformScale##slot;
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
            uint absoluteSourceIndex = sourceIndex + (uint)sourceDecode.w;
            uint2 coord = GetBlockCoord(absoluteSourceIndex, (uint)sourceLayout.x, (uint)sourceLayout.y);

            SplatData source;
            source.mean = positionsTexture[coord].xyz;
            source.scale = max(exp2(sourceDecode.x), scalesTexture[coord].xyz);
            source.quat = normalize(lerp(-1.0, 1.0, rotationsTexture[coord]));
            source.color = colorsTexture[coord];
            source.color.a *= sourceDecode.y;
            source.id = absoluteSourceIndex;
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

        bool GSTrySlotGaussian(uint combinedIndex,
            Texture2D positions, Texture2D scales, Texture2D rotations,
            float4 layout, float4 decode,
            float4 transformRotation, float4 transformScale, float4x4 localToWorld,
            out GaussianTransformData gaussian)
        {
            gaussian.position = 0.0;
            gaussian.rotation = float4(0.0, 0.0, 0.0, 1.0);
            gaussian.scale = 0.0;
            uint sourceIndex;
            if (!GSResolveSourceIndex(combinedIndex, layout, sourceIndex)) return false;

            uint absoluteSourceIndex = sourceIndex + (uint)decode.w;
            uint2 coord = GetBlockCoord(absoluteSourceIndex, (uint)layout.x, (uint)layout.y);
            gaussian.position = positions[coord].xyz;
            gaussian.scale = max(exp2(decode.x), scales[coord].xyz);
            gaussian.rotation = normalize(lerp(-1.0, 1.0, rotations[coord]));
            gaussian = GSTransformGaussian(gaussian, localToWorld, transformRotation, transformScale.xyz);
            return true;
        }

        bool GSTrySlotPosition(uint combinedIndex,
            Texture2D positions,
            float4 layout, float4 decode, float4x4 localToWorld,
            out float3 position)
        {
            position = 0.0;
            uint sourceIndex;
            if (!GSResolveSourceIndex(combinedIndex, layout, sourceIndex)) return false;

            uint absoluteSourceIndex = sourceIndex + (uint)decode.w;
            uint2 coord = GetBlockCoord(absoluteSourceIndex, (uint)layout.x, (uint)layout.y);
            position = mul(localToWorld, float4(positions[coord].xyz, 1.0)).xyz;
            return true;
        }

        bool GSTrySlotColor(uint combinedIndex,
            Texture2D positions, Texture2D colors, Texture2D sh,
            float4 layout, float4 shLayout, float4 decode, float4 shMin, float4 shRange,
            float4x4 worldToLocal,
            out float4 color)
        {
            color = 0.0;
            uint sourceIndex;
            if (!GSResolveSourceIndex(combinedIndex, layout, sourceIndex)) return false;

            uint absoluteSourceIndex = sourceIndex + (uint)decode.w;
            uint2 coord = GetBlockCoord(absoluteSourceIndex, (uint)layout.x, (uint)layout.y);
            float3 positionObject = positions[coord].xyz;
            float4 sourceColor = colors[coord];
            sourceColor.a *= decode.y;
            float3 cameraPosObject = mul(worldToLocal, float4(_CameraPosWorld, 1.0)).xyz;
            color = float4(GSEvaluateSourceSHColor(sh, shMin, shRange, shLayout, decode.z, absoluteSourceIndex, sourceColor.rgb, positionObject, cameraPosObject), sourceColor.a);
            return true;
        }

        #define GS_TRY_GAUSSIAN_SLOT(slot) if (GSTrySlotGaussian(combinedIndex, _GS_SourcePositions##slot, _GS_SourceScales##slot, _GS_SourceRotations##slot, _GS_SourceLayout##slot, _GS_SourceDecode##slot, _GS_SourceTransformRotation##slot, _GS_SourceTransformScale##slot, _GS_SourceLocalToWorld##slot, gaussian)) return true;

        bool GSTryGetCombinedGaussian(v2f_img input, out GaussianTransformData gaussian)
        {
            uint combinedIndex = GSCombineOutputIndex(uint2(input.pos.xy));
            gaussian.position = 0.0;
            gaussian.rotation = float4(0.0, 0.0, 0.0, 1.0);
            gaussian.scale = 0.0;
            GS_SOURCE_SLOT_LIST(GS_TRY_GAUSSIAN_SLOT)
            return false;
        }
        #undef GS_TRY_GAUSSIAN_SLOT

        #define GS_TRY_POSITION_SLOT(slot) if (GSTrySlotPosition(combinedIndex, _GS_SourcePositions##slot, _GS_SourceLayout##slot, _GS_SourceDecode##slot, _GS_SourceLocalToWorld##slot, position)) return true;

        bool GSTryGetCombinedPosition(v2f_img input, out float3 position)
        {
            uint combinedIndex = GSCombineOutputIndex(uint2(input.pos.xy));
            position = 0.0;
            GS_SOURCE_SLOT_LIST(GS_TRY_POSITION_SLOT)
            return false;
        }
        #undef GS_TRY_POSITION_SLOT

        #define GS_TRY_COLOR_SLOT(slot) if (GSTrySlotColor(combinedIndex, _GS_SourcePositions##slot, _GS_SourceColors##slot, _GS_SourceSH##slot, _GS_SourceLayout##slot, _GS_SourceShLayout##slot, _GS_SourceDecode##slot, _GS_SourceShMin##slot, _GS_SourceShRange##slot, _GS_SourceWorldToLocal##slot, color)) return true;

        bool GSTryGetCombinedColor(v2f_img input, out float4 color)
        {
            uint combinedIndex = GSCombineOutputIndex(uint2(input.pos.xy));
            color = 0.0;
            GS_SOURCE_SLOT_LIST(GS_TRY_COLOR_SLOT)
            return false;
        }
        #undef GS_TRY_COLOR_SLOT

        float4 fragPositions(v2f_img input) : SV_Target
        {
            float3 position;
            if (!GSTryGetCombinedPosition(input, position)) discard;

            return float4(position, 1.0);
        }

        float4 fragRotations(v2f_img input) : SV_Target
        {
            GaussianTransformData gaussian;
            if (!GSTryGetCombinedGaussian(input, gaussian)) discard;

            return gaussian.rotation * 0.5 + 0.5;
        }

        float4 fragScales(v2f_img input) : SV_Target
        {
            GaussianTransformData gaussian;
            if (!GSTryGetCombinedGaussian(input, gaussian)) discard;

            return float4(gaussian.scale, 1.0);
        }

        float4 fragColors(v2f_img input) : SV_Target
        {
            float4 color;
            if (!GSTryGetCombinedColor(input, color)) discard;

            return color;
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragPositions
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragRotations
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragScales
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragColors
            ENDCG
        }
    }
    Fallback Off
}
