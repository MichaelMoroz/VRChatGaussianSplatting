Shader "Misha/RadixSort"
{
    Properties {
        _KeyValues("Key Values", 2D) = "white" {}
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
            #pragma vertex   vert
            #pragma fragment frag
            //#pragma enable_d3d11_debug_symbols
            #include "RadixSort.cginc"

            // Count number of given digits in the key values.
            float frag (v2f i) : SV_Target {
                uint2 pixel = floor(i.pos.xy);
                uint morton = UVToIndex(pixel);

                uint elementsLog2 = _ImageElementsLog2;
                uint groupsLog2 = elementsLog2 - _GroupSize;
                uint digitIndex = morton >> groupsLog2;
                uint keyIndex = (morton - (digitIndex << groupsLog2)) << _GroupSize;

                if(keyIndex >= _ElementCount) return 0.0;

                uint count = 0;
                uint groupElements = 1 << _GroupSize;
                uint mask = ((1u << _BitsPerStep) - 1u);
                for(int i = 0; i < groupElements; i++) {
                    uint2 groupPixel = IndexToUV(keyIndex + i);
                    uint key = asuint(_KeyValues[groupPixel].y);
                    uint digit = (key >> _CurrentBit) & mask;
                    count += uint(digit == digitIndex);
                }

                return count;
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
                if(index >= _ElementCount) return float2(1e10, 1e10); // Return a large value if index is out of bounds

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
                float2 keyValue;
                uint groupElements = 1 << _GroupSize;
                uint mask = ((1u << _BitsPerStep) - 1u);
                for(int i = 0; i < groupElements; i++) {
                    uint2 groupPixel = IndexToUV(keyIndex + i);
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
