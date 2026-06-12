Shader "Misha/RadixSort"
{
    Properties {
        _KeyValues("Key Values", 2D) = "white" {}
        _Histograms("Histograms", 2D) = "white" {}
        _PrefixSums("Prefix Sums", 2D) = "white" {}
        _ElementCount("Element Count", Int) = 512
        _CurrentBit("Current Bit", Int) = 0
        _BitsPerStep("Bits Per Step", Int) = 2
        _GroupSize("Group Size", Int) = 2
        _ImageSizeLog2("Image Size Log2", Int) = 9
    }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass {
            ZTest Always
            Cull Off
            ZWrite Off

            CGPROGRAM
            #pragma vertex   vertHistogram
            #pragma fragment frag
            //#pragma enable_d3d11_debug_symbols
            #include "RadixSort.cginc"

            float PackHistogramCounts(uint4 counts)
            {
                return (float)(counts.x | (counts.y << 5u) | (counts.z << 10u) | (counts.w << 15u));
            }

            // Build all 16 digit counts for a 16-key group with one pass over the keys.
            float4 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint groupIndex = UVToIndex(pixel);
                uint elementCount = uint(_ElementCount);
                uint groupElements = 1u << _GroupSize;
                uint groupsLog2 = (uint)_ImageElementsLog2 - (uint)_GroupSize;
                uint groupCount = 1u << groupsLog2;

                if(groupIndex >= groupCount) return 0.0;

                uint keyIndex = groupIndex << (uint)_GroupSize;
                if(keyIndex >= elementCount) return 0.0;

                uint groupElementCount = min(groupElements, elementCount - keyIndex);
                uint mask = ((1u << _BitsPerStep) - 1u);
                uint4 counts0 = 0u;
                uint4 counts1 = 0u;
                uint4 counts2 = 0u;
                uint4 counts3 = 0u;

                [unroll(16)]
                for(uint i = 0u; i < groupElementCount; ++i) {
                    uint groupKeyIndex = keyIndex + i;
                    uint2 groupPixel = IndexToUV(groupKeyIndex);
                    uint key = asuint(_KeyValues[groupPixel].y);
                    uint digit = (key >> _CurrentBit) & mask;
                    counts0 += uint4(digit == 0u, digit == 1u, digit == 2u, digit == 3u);
                    counts1 += uint4(digit == 4u, digit == 5u, digit == 6u, digit == 7u);
                    counts2 += uint4(digit == 8u, digit == 9u, digit == 10u, digit == 11u);
                    counts3 += uint4(digit == 12u, digit == 13u, digit == 14u, digit == 15u);
                }

                return float4(
                    PackHistogramCounts(counts0),
                    PackHistogramCounts(counts1),
                    PackHistogramCounts(counts2),
                    PackHistogramCounts(counts3));
            }
            ENDCG
        }

        Pass {
            ZTest Always
            Cull Off
            ZWrite Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            //#pragma enable_d3d11_debug_symbols
            #include "RadixSort.cginc"

            uint UnpackHistogramCount(float4 packedHistogram, uint digitIndex)
            {
                uint packedGroup = digitIndex >> 2u;
                uint packed = 0u;
                if (packedGroup == 0u)
                {
                    packed = (uint)round(packedHistogram.x);
                }
                else if (packedGroup == 1u)
                {
                    packed = (uint)round(packedHistogram.y);
                }
                else if (packedGroup == 2u)
                {
                    packed = (uint)round(packedHistogram.z);
                }
                else
                {
                    packed = (uint)round(packedHistogram.w);
                }
                return (packed >> ((digitIndex & 3u) * 5u)) & 31u;
            }

            // Expand the packed group histograms into the scalar digit/group layout expected by mip prefix sums.
            float frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint morton = UVToIndex(pixel);

                uint elementsLog2 = _ImageElementsLog2;
                uint groupsLog2 = elementsLog2 - _GroupSize;
                uint digitIndex = morton >> groupsLog2;
                uint groupIndex = morton - (digitIndex << groupsLog2);
                uint groupCount = 1u << groupsLog2;

                if(digitIndex >= (1u << _BitsPerStep) || groupIndex >= groupCount) return 0.0;

                uint2 histogramPixel = IndexToUV(groupIndex);
                return (float)UnpackHistogramCount(_Histograms[histogramPixel], digitIndex);
            }
            ENDCG
        }

        // The Graphics API computes the mipmaps of the digit counts (averages), only works up to 2^24 elements due to float precision.
        // Check https://github.com/d4rkc0d3r/CompactSparseTextureDemo for more info

        Pass {
            ZTest Always
            Cull Off
            ZWrite Off

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            //#pragma enable_d3d11_debug_symbols
            #include "RadixSort.cginc"
            
            // Do binary search for the key value at the given sorted index.
            float2 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint index = UVToIndex(pixel);
                uint elementCount = uint(_ElementCount);
                if(index >= elementCount) return float2(1e10, 1e10); // Return a large value if index is out of bounds

                // Do binary search for the key value in the prefix sum by summing/going over the mips 
                uint _ImageSize = _KeyValues_TexelSize.z;
                uint prefixWidth = (_ImageSize << (_BitsPerStep >> 1)) >> (_GroupSize >> 1);
                uint elementsLog2 = _ImageElementsLog2;
                uint groupsLog2 = elementsLog2 - _GroupSize;
                uint count;
                int2 activePixel = ActiveTexelIndexToUV(_PrefixSums, prefixWidth, index, count);
                uint activeIndex = UVToIndex(activePixel);
                uint digitIndex = activeIndex >> groupsLog2;
                uint keyIndex = (activeIndex - (digitIndex << groupsLog2)) << _GroupSize;

                // Find the final key value in the group
                float2 keyValue = float2(1e10, 1e10);
                uint groupElements = 1u << _GroupSize;
                uint groupElementCount = min(groupElements, elementCount - keyIndex);
                uint mask = ((1u << _BitsPerStep) - 1u);
                [unroll(16)]
                for(uint i = 0u; i < groupElementCount; ++i) {
                    uint groupIndex = keyIndex + i;
                    uint2 groupPixel = IndexToUV(groupIndex);
                    keyValue = _KeyValues[groupPixel];
                    uint key = asuint(keyValue.y);
                    uint digit = (key >> _CurrentBit) & mask;
                    count += uint(digit == digitIndex);
                    if(count > index) break;
                }

                return keyValue;
            }
            ENDCG
        }

    }
}
