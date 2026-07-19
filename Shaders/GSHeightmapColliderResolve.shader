Shader "Hidden/GaussianSplatting/HeightmapColliderResolve"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #pragma target 3.5
        #include "UnityCG.cginc"

        #define HEIGHT_SENTINEL -3.402823466e+38
        #define SMALL_VALUE 1e-8
        #define MAX_SUPERSAMPLE 8
        #define MAX_REDUCTION_VALUES 64
        #define MAX_MEDIAN_RADIUS 8
        #define MAX_MEDIAN_VALUES 289

        Texture2D _DepthTex;
        Texture2D _InputHeightTex;
        SamplerState sampler_PointClamp;

        int _OutputResolution;
        int _Supersample;
        int _MedianRadius;
        float _BoxHeight;
        float _OpacityEpsilon;
        float _ReductionPercentile;

        bool ValidHeight(float height)
        {
            return height > -1e20 && height < 1e20 && height == height;
        }

        void InsertSorted(inout float values[MAX_REDUCTION_VALUES], inout uint count, float value)
        {
            uint index = count;
            count++;
            [loop]
            while (index > 0u && values[index - 1u] > value)
            {
                values[index] = values[index - 1u];
                index--;
            }
            values[index] = value;
        }

        void InsertSortedMedian(inout float values[MAX_MEDIAN_VALUES], inout uint count, float value)
        {
            uint index = count;
            count++;
            [loop]
            while (index > 0u && values[index - 1u] > value)
            {
                values[index] = values[index - 1u];
                index--;
            }
            values[index] = value;
        }

        float ResolveHeight(float4 accum)
        {
            float opacity = saturate(accum.a);
            if (opacity <= max(_OpacityEpsilon, SMALL_VALUE))
            {
                return HEIGHT_SENTINEL;
            }
            return _BoxHeight * (1.0 - saturate(accum.r / max(opacity, SMALL_VALUE)));
        }

        float4 fragResolve(v2f_img i) : SV_Target
        {
            uint2 pixel = (uint2)floor(i.pos.xy);
            uint ss = min(max((uint)_Supersample, 1u), (uint)MAX_SUPERSAMPLE);
            uint2 basePixel = pixel * ss;
            float values[MAX_REDUCTION_VALUES];
            uint validCount = 0u;
            float maxOpacity = 0.0;

            [loop]
            for (uint y = 0u; y < (uint)MAX_SUPERSAMPLE; y++)
            {
                if (y >= ss) break;
                [loop]
                for (uint x = 0u; x < (uint)MAX_SUPERSAMPLE; x++)
                {
                    if (x >= ss) break;
                    float4 accum = _DepthTex.Load(int3(basePixel + uint2(x, y), 0));
                    maxOpacity = max(maxOpacity, saturate(accum.a));
                    float height = ResolveHeight(accum);
                    if (ValidHeight(height) && validCount < (uint)MAX_REDUCTION_VALUES)
                    {
                        InsertSorted(values, validCount, height);
                    }
                }
            }

            if (validCount == 0u)
            {
                return float4(HEIGHT_SENTINEL, 0.0, 0.0, maxOpacity);
            }

            uint rank = (uint)round(saturate(_ReductionPercentile) * (float)(validCount - 1u));
            return float4(values[min(rank, validCount - 1u)], 0.0, 0.0, maxOpacity);
        }

        float4 fragMedian(v2f_img i) : SV_Target
        {
            int2 pixel = (int2)floor(i.pos.xy);
            float center = _InputHeightTex.Load(int3(pixel, 0)).r;
            if (!ValidHeight(center))
            {
                return float4(HEIGHT_SENTINEL, 0.0, 0.0, 0.0);
            }

            int radius = min(max(_MedianRadius, 0), MAX_MEDIAN_RADIUS);
            float values[MAX_MEDIAN_VALUES];
            uint validCount = 0u;

            [loop]
            for (int dy = -MAX_MEDIAN_RADIUS; dy <= MAX_MEDIAN_RADIUS; dy++)
            {
                if (dy < -radius || dy > radius) continue;
                int sy = pixel.y + dy;
                if (sy < 0 || sy >= _OutputResolution) continue;

                [loop]
                for (int dx = -MAX_MEDIAN_RADIUS; dx <= MAX_MEDIAN_RADIUS; dx++)
                {
                    if (dx < -radius || dx > radius) continue;
                    int sx = pixel.x + dx;
                    if (sx < 0 || sx >= _OutputResolution) continue;

                    float height = _InputHeightTex.Load(int3(sx, sy, 0)).r;
                    if (ValidHeight(height) && validCount < (uint)MAX_MEDIAN_VALUES)
                    {
                        InsertSortedMedian(values, validCount, height);
                    }
                }
            }

            return float4(validCount > 0u ? values[validCount >> 1] : center, 0.0, 0.0, 1.0);
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragResolve
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragMedian
            ENDCG
        }
    }
}
