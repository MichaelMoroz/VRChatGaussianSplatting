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

            // Record the 16 group digits themselves (4 bits each, 6 per float channel as
            // exact ints) with one pass over the keys; later passes decode counts and
            // in-group positions from the pack instead of re-reading the keys.
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
                uint3 acc = uint3(0u, 0u, 0u);

                [unroll(16)]
                for(uint j = 0u; j < groupElementCount; ++j) {
                    uint key = asuint(_KeyValues[IndexToUV(keyIndex + j)].y);
                    uint digit = (key >> _CurrentBit) & mask;
                    uint channel = j / 6u;
                    uint shift = (j - channel * 6u) * 4u;
                    if (channel == 0u) acc.x |= digit << shift;
                    else if (channel == 1u) acc.y |= digit << shift;
                    else acc.z |= digit << shift;
                }

                return float4((float)acc.x, (float)acc.y, (float)acc.z, 0.0);
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

            // Expand the digit-pack histograms into the scalar digit/group layout expected by mip prefix sums.
            float frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint morton = UVToIndex(pixel);

                uint elementsLog2 = _ImageElementsLog2;
                uint groupsLog2 = elementsLog2 - _GroupSize;
                uint digitIndex = morton >> groupsLog2;
                uint groupIndex = morton - (digitIndex << groupsLog2);
                uint groupCount = 1u << groupsLog2;

                if(digitIndex >= (1u << _BitsPerStep) || groupIndex >= groupCount) return 0.0;

                uint keyIndex = groupIndex << (uint)_GroupSize;
                uint elementCount = uint(_ElementCount);
                if(keyIndex >= elementCount) return 0.0;
                uint groupElementCount = min(1u << _GroupSize, elementCount - keyIndex);

                float4 pack = _Histograms[IndexToUV(groupIndex)];
                uint count = 0u;
                [unroll(16)]
                for(uint j = 0u; j < groupElementCount; ++j) {
                    count += uint(DigitAt(pack, j) == digitIndex);
                }
                return (float)count;
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

            // Binary search the prefix-sum mips for the key at the given sorted index, then
            // locate it inside its group from the digit pack: one histogram load and one
            // keyvalue load instead of scanning the group's keys.
            float2 frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint index = UVToIndex(pixel);
                uint elementCount = uint(_ElementCount);
                if(index >= elementCount) return float2(1e10, 1e10); // Return a large value if index is out of bounds

                uint _ImageSize = _KeyValues_TexelSize.z;
                uint prefixWidth = (_ImageSize << (_BitsPerStep >> 1)) >> (_GroupSize >> 1);
                uint groupsLog2 = (uint)_ImageElementsLog2 - _GroupSize;
                float count;
                int2 activePixel = ActiveTexelIndexToUV(_PrefixSums, prefixWidth, index, count);
                uint activeIndex = UVToIndex(activePixel);
                uint digitIndex = activeIndex >> groupsLog2;
                uint groupIndex = activeIndex - (digitIndex << groupsLog2);
                uint keyIndex = groupIndex << _GroupSize;
                uint groupElementCount = min(1u << _GroupSize, elementCount - keyIndex);

                // Slot of the (index - count)-th occurrence of digitIndex inside the group.
                float4 pack = _Histograms[IndexToUV(groupIndex)];
                uint occurrence = index - (uint)count;
                uint matches = 0u;
                uint slot = 0u;
                [unroll(16)]
                for(uint j = 0u; j < groupElementCount; ++j) {
                    bool match = DigitAt(pack, j) == digitIndex;
                    if(match && matches == occurrence) slot = j;
                    matches += uint(match);
                }

                return _KeyValues[IndexToUV(keyIndex + slot)];
            }
            ENDCG
        }

    }
}
