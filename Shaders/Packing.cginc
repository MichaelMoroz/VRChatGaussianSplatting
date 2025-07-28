float scaleFromExponent(int e)
{
    return asfloat(uint(127 + e) << 23);
}

int getExponentFromScale(float scale)
{
    return int((asuint(scale) >> 23) & 0xFFu) - 127;
}

//----------------------------------------------
// E5 / S9×3 → uint
//----------------------------------------------
uint packF3U1(float3 v)          // 32-bit
{
    if (all(v == 0.0)) return 0.0;
    float maxv  = max(max(abs(v.x), abs(v.y)), abs(v.z));
    int   e     = clamp(getExponentFromScale(maxv) + 1, -15, 15);
    float scale = scaleFromExponent(-e);

    uint3 sv = uint3(round(clamp(v*scale, -1.0, 1.0) * 255.0) + 255.0);
    return uint(e + 15)          |
          (sv.x << 5)            |
          (sv.y << 14)           |
          (sv.z << 23);
}

float3 unpackF3U1(uint data)     // ← uint
{
    if (data == 0) return 0.0;
    int   e  = int(data & 0x1Fu) - 15;
    uint3 sv = uint3((data >> 5) & 0x1FFu,
                     (data >> 14) & 0x1FFu,
                      data >> 23);
    float scale = scaleFromExponent(e);
    return (float3(sv) / 255.0 - 1.0) * scale;
}

//----------------------------------------------------
// E7 / S19×3 → uint2
//----------------------------------------------------
static const float M        = 262143.0;  // 2^18 − 1
static const uint  MANT_BIAS = 1u << 18; // 2^18

uint2 packF3U2(float3 v)                // → uint2
{
    if (all(v == 0.0)) return 0.0;

    float maxv  = max(max(abs(v.x), abs(v.y)), abs(v.z));
    int   e     = clamp(getExponentFromScale(maxv) + 1, -63, 63);
    float scale = scaleFromExponent(-e);

    uint3 m  = uint3(round(clamp(v * scale, -1.0, 1.0) * M) + MANT_BIAS);
    uint eb = uint(e + 63);           // 7-bit biased exponent

    uint lo =  eb | (m.x << 7) | ((m.y & 0x3Fu) << 26);
    uint hi = (m.y >> 6) | (m.z << 13);

    return uint2(lo, hi);
}

float3 unpackF3U2(uint2 data)
{
    if (data.x == 0 && data.y == 0) return 0.0;
    uint lo = data.x, hi = data.y;

    int  e  = int(lo & 0x7Fu) - 63;
    uint mx = (lo >> 7)  & 0x7FFFFu;
    uint my = ((hi & 0x1FFFu) << 6) | ((lo >> 26) & 0x3Fu);
    uint mz = (hi >> 13) & 0x7FFFFu;

    float scale = scaleFromExponent(e);
    return (float3(mx, my, mz) - 262144.0) / M * scale;
}