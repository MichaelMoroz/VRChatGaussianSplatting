#ifndef RADIX_SORT_UTILS_CGINC
#define RADIX_SORT_UTILS_CGINC

uint InterleaveWithZero(uint word) {
    word = (word ^ (word << 8)) & 0x00ff00ff;
    word = (word ^ (word << 4)) & 0x0f0f0f0f;
    word = (word ^ (word << 2)) & 0x33333333;
    word = (word ^ (word << 1)) & 0x55555555;
    return word;
}

uint DeinterleaveWithZero(uint word) {
    word &= 0x55555555;
    word = (word | (word >> 1)) & 0x33333333;
    word = (word | (word >> 2)) & 0x0f0f0f0f;
    word = (word | (word >> 4)) & 0x00ff00ff;
    word = (word | (word >> 8)) & 0x0000ffff;
    return word;
}

uint2 IndexToUV(uint index) {
    return uint2(DeinterleaveWithZero(index), DeinterleaveWithZero(index >> 1));
}

uint UVToIndex(uint2 uv) {
    return InterleaveWithZero(uv.x) | (InterleaveWithZero(uv.y) << 1);
}

#define ASFLOAT_NO_DENORM(x) (asfloat(x + (1 << 30)))
#define ASUINT_NO_DENORM(x) (asuint(x) - (1 << 30))

float CountActiveTexels(Texture2D<float> _Texels, int3 uv, int2 offset) {
    return (float)(1 << (uv.z + uv.z)) * _Texels.Load(uv, offset);
}

float CountActiveTexels(Texture2D<float> _Texels, int3 uv) {
    return CountActiveTexels(_Texels, uv, int2(0, 0));
}

int2 ActiveTexelIndexToUV(Texture2D<float> _Texels, float height, float index, out float activePrevTexelSum) {
    float maxLod = round(log2(height));
    int3 uv = int3(0, 0, maxLod);
    float countTotal = CountActiveTexels(_Texels, uv);
    activePrevTexelSum = 0;
    if (index >= countTotal)
    {
        activePrevTexelSum = countTotal;
        return -1;
    }
    while (uv.z >= 1)
    {
        uv += int3(uv.xy, -1);
        float count00 = CountActiveTexels(_Texels, uv);
        float count01 = CountActiveTexels(_Texels, uv, int2(1, 0));
        float count10 = CountActiveTexels(_Texels, uv, int2(0, 1));
        bool in00 = index < (activePrevTexelSum + count00);
        bool in01 = index < (activePrevTexelSum + count00 + count01);
        bool in10 = index < (activePrevTexelSum + count00 + count01 + count10);
        if (in00)
        {
            uv.xy += int2(0, 0);
        }
        else if (in01)
        {
            uv.xy += int2(1, 0);
            activePrevTexelSum += count00;
        }
        else if (in10)
        {
            uv.xy += int2(0, 1);
            activePrevTexelSum += count00 + count01;
        }
        else
        {
            uv.xy += int2(1, 1);
            activePrevTexelSum += count00 + count01 + count10;
        }
    }
    return uv.xy;
}
#endif