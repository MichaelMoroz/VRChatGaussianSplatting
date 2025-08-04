// ------------------------------------------------------------------
// tiny helpers for (sign-)extending bit-fields
// ------------------------------------------------------------------
int  sx(uint bits, uint n)      { return int(bits << (32u - n)) >> (32 - int(n)); }
uint ux(int  v,   uint n)       { return uint(v) & ((1u << n) - 1u); }

// ------------------------------------------------------------------
// shared helpers you supplied
// ------------------------------------------------------------------
float scaleFromExponent(int e)         { return asfloat(uint(127 + e) << 23); }
int   getExponentFromScale(float s)    { return int((asuint(s) >> 23) & 0xFFu) - 127; }

// ===============================================================
// F3U1  (E5  /  S9×3)   — 32 bits
// layout:  mx[8:0] | my[8:0] | mz[4:0] | exp[4:0] | mz[8:5]
// ===============================================================
static const float M9 = 255.0;          // 2^8−1

uint packF3U1(float3 v)
{
    float maxv = max(max(abs(v.x), abs(v.y)), abs(v.z));

    int e = (maxv == 0.0) ? 0
            : clamp(getExponentFromScale(maxv) +
                    ((asuint(maxv) & 0x007FFFFFu) ? 1 : 0), -16, 15);

    float  scale = scaleFromExponent(-e);
    int3   q     = int3(round(clamp(v * scale, -1.0, 1.0) * M9));   // −255 … 255

    uint mx = ux(q.x, 9);
    uint my = ux(q.y, 9);
    uint mz = ux(q.z, 9);
    uint eb = ux(e,   5);

    return  mx
          | (my << 9)
          | ((mz & 0x1Fu) << 18)
          | (eb << 23)
          | ((mz >> 5) << 28);
}

float3 unpackF3U1(uint w)
{
    if (w == 0u) return 0.0;

    uint mxBits =  w        & 0x1FFu;
    uint myBits = (w >>  9) & 0x1FFu;
    uint mzBits = ((w >> 28) & 0xFu) << 5 | ((w >> 18) & 0x1Fu);
    uint ebBits = (w >> 23) & 0x1Fu;

    int   mx = sx(mxBits, 9);
    int   my = sx(myBits, 9);
    int   mz = sx(mzBits, 9);
    int   e  = sx(ebBits, 5);

    float scale = scaleFromExponent(e);
    return float3(mx, my, mz) / M9 * scale;
}

// ===============================================================
// F3U2  (E7  /  S19×3)  — 64 bits (uint2)
// layout:  mx[18:0] | exp[6:0] | my[5:0]  ||  my[18:6] | mz[18:0]
// ===============================================================
static const float M19 = 262143.0;      // 2^18−1

uint2 packF3U2(float3 v)
{
    float maxv = max(max(abs(v.x), abs(v.y)), abs(v.z));

    int e = (maxv == 0.0) ? 0
            : clamp(getExponentFromScale(maxv) +
                    ((asuint(maxv) & 0x007FFFFFu) ? 1 : 0), -64, 63);

    float  scale = scaleFromExponent(-e);
    int3   q     = int3(round(clamp(v * scale, -1.0, 1.0) * M19));  // −262 143 … 262 143

    uint mx = ux(q.x, 19);
    uint my = ux(q.y, 19);
    uint mz = ux(q.z, 19);
    uint eb = ux(e,   7);

    uint lo =  mx | (eb << 19) | ((my & 0x3Fu) << 26);
    uint hi = (my >> 6) | (mz << 13);

    return uint2(lo, hi);
}

float3 unpackF3U2(uint2 d)
{
    if (all(d == 0u)) return 0.0;

    uint lo = d.x, hi = d.y;

    uint mxBits =  lo & 0x7FFFFu;
    uint myBits = ((lo >> 26) & 0x3Fu) | ((hi & 0x1FFFu) << 6);
    uint mzBits =  (hi >> 13) & 0x7FFFFu;
    uint ebBits = (lo >> 19) & 0x7Fu;

    int  mx = sx(mxBits, 19);
    int  my = sx(myBits, 19);
    int  mz = sx(mzBits, 19);
    int  e  = sx(ebBits, 7);

    float scale = scaleFromExponent(e);
    return float3(mx, my, mz) / M19 * scale;
}

float FromLog(float lg, float minv, float maxv) {
    return exp2(lg * (maxv - minv) + minv) / lg;
}

float ToLog(float ex, float minv, float maxv) {
    return (log2(ex) - minv) / (ex * (maxv - minv));
}

float maxc(float3 v) {
    return max(max(v.x, v.y), v.z);
}

float maxc(float4 v) {
    return max(max(max(v.x, v.y), v.z), v.w);
}

float maxc(float3x3 v) {
    return max(max(maxc(v[0]), maxc(v[1])), maxc(v[2]));
}

float FromLogF1(float v, float minv, float maxv) {
    if(v == 0.0) return v;
    float scale = FromLog(v, minv, maxv);
    return v * scale;
}

float ToLogF1(float v, float minv, float maxv) {
    if(v == 0.0) return v;
    float scale = ToLog(v, minv, maxv);
    return v * scale;
}

float3 FromLogF3(float3 v, float minv, float maxv) {
    float max_v = maxc(abs(v));
    if(max_v == 0.0) return v;
    float scale = FromLog(max_v, minv, maxv);
    return v * scale;
}

float3 ToLogF3(float3 v, float minv, float maxv) {
    float max_v = maxc(abs(v));
    if(max_v == 0.0) return v;
    float scale = ToLog(max_v, minv, maxv);
    return v * scale;
}

float4 FromLogF4(float4 v, float minv, float maxv) {
    float max_v = maxc(abs(v));
    if(max_v == 0.0) return v;
    float scale = FromLog(max_v, minv, maxv);
    return v * scale;
}

float4 ToLogF4(float4 v, float minv, float maxv) {
    float max_v = maxc(abs(v));
    if(max_v == 0.0) return v;
    float scale = ToLog(max_v, minv, maxv);
    return v * scale;
}

float3x3 FromLogF3x3(float3x3 v, float minv, float maxv) {
    float max_v = maxc(abs(v));
    if(max_v == 0.0) return v;
    float scale = FromLog(max_v, minv, maxv);
    return v * scale;
}

float3x3 ToLogF3x3(float3x3 v, float minv, float maxv) {
    float max_v = maxc(abs(v));
    if(max_v == 0.0) return v;
    float scale = ToLog(max_v, minv, maxv);
    return v * scale;
}

inline uint Quantize(float v, float mn, float mx, uint bits)
{
    const float levels = float(1u << bits);
    return (uint)round(clamp((v - mn) / (mx - mn), 0.0, 1.0) * levels);
}

inline float Dequantize(uint q, float mn, float mx, uint bits)
{
    const float levels = float(1u << bits);
    return (float(q) / levels) * (mx - mn) + mn;
}

void WriteDataAt(inout uint4 info, inout int bit_offset, uint data, uint data_bits) {
    uint word_index = bit_offset >> 5;
    uint word_bit = bit_offset & 31;
    uint data_bits_start = 32 - data_bits;
    int data_bits_offset = data_bits_start - word_bit;

    if(data_bits_offset >= 0) //data fits into the first element
    {
        info[word_index] |= data << data_bits_offset;
    }
    else //data overflows into the next element
    {
        info[word_index] |= data >> (-data_bits_offset);
        info[word_index + 1] |= data << (data_bits_offset + 32);
    }
    bit_offset += data_bits;
}

uint ReadDataAt(uint4 info, inout int bit_offset, uint data_bits) {
    uint word_index = bit_offset >> 5;
    uint word_bit = bit_offset & 31;
    uint data_bits_start = 32 - data_bits;
    int data_bits_offset = data_bits_start - word_bit;

    uint data = 0;
    if(data_bits_offset >= 0) //can read all data from the first element
    {
        data = info[word_index] >> data_bits_offset;
    }
    else //need to read from two elements
    {
        data = info[word_index] << (-data_bits_offset);
        data |= info[word_index + 1] >> (data_bits_offset + 32);
    }
    bit_offset += data_bits;
    return data & ((1u << data_bits) - 1u);
}

#define MIN_POS_LOG2 -15
#define MAX_POS_LOG2  4
#define MIN_SCALE_LOG2 -15
#define MAX_SCALE_LOG2  -2
#define MIN_COLOR_LOG2 -7
#define MAX_COLOR_LOG2  7
#define MIN_DENSITY_LOG2 -10
#define MAX_DENSITY_LOG2 0

struct GaussianData {
    float3 P; // position
    float3x3 RS; // Cholesky factorization of covariance matrix, works same as rotation * scale
    float4 C; // density / color
};

uint4 PackGaussianData(GaussianData g)
{
    uint4 data = 0u;
    int bit_offset = 0;
    float3 Plog2 = ToLogF3(g.P, MIN_POS_LOG2, MAX_POS_LOG2);
    uint Px = Quantize(Plog2.x, -1.0, 1.0, 16);
    uint Py = Quantize(Plog2.y, -1.0, 1.0, 16);
    uint Pz = Quantize(Plog2.z, -1.0, 1.0, 16);
    WriteDataAt(data, bit_offset,  Px, 16);
    WriteDataAt(data, bit_offset, Py, 16);
    WriteDataAt(data, bit_offset, Pz, 16);

    float3x3 RSlog2 = ToLogF3x3(g.RS, MIN_SCALE_LOG2, MAX_SCALE_LOG2);
    uint RS00 = Quantize(RSlog2[0][0], 0.0, 1.0, 8); //diagonal terms are positive
    uint RS11 = Quantize(RSlog2[1][1], 0.0, 1.0, 8);
    uint RS22 = Quantize(RSlog2[2][2], 0.0, 1.0, 8);
    uint RS10 = Quantize(RSlog2[1][0], -1.0, 1.0, 8);
    uint RS20 = Quantize(RSlog2[2][0], -1.0, 1.0, 8);
    uint RS21 = Quantize(RSlog2[2][1], -1.0, 1.0, 8);
    WriteDataAt(data, bit_offset, RS00, 8);
    WriteDataAt(data, bit_offset, RS10, 8);
    WriteDataAt(data, bit_offset, RS20, 8);
    WriteDataAt(data, bit_offset, RS11, 8);
    WriteDataAt(data, bit_offset, RS21, 8);
    WriteDataAt(data, bit_offset, RS22, 8);
    
    float3 Clog2 = ToLogF3(g.C.xyz, MIN_COLOR_LOG2, MAX_COLOR_LOG2);
    uint C0 = Quantize(Clog2.x, 0.0, 1.0, 8); //density is positive
    uint C1 = Quantize(Clog2.y, 0.0, 1.0, 8);
    uint C2 = Quantize(Clog2.z, 0.0, 1.0, 8);
    WriteDataAt(data, bit_offset,  C0, 8);
    WriteDataAt(data, bit_offset, C1, 8);
    WriteDataAt(data, bit_offset, C2, 8);

    float Clog2w = ToLogF1(g.C.w, MIN_DENSITY_LOG2, MAX_DENSITY_LOG2);
    uint C3 = Quantize(Clog2w, 0.0, 1.0, 8);
    WriteDataAt(data, bit_offset, C3, 8);
    return data;
}

GaussianData UnpackGaussianData(uint4 data)
{
    GaussianData g;
    int bit_offset = 0;
    g.P.x = Dequantize(ReadDataAt(data, bit_offset, 16), -1.0, 1.0, 16);
    g.P.y = Dequantize(ReadDataAt(data, bit_offset, 16), -1.0, 1.0, 16);
    g.P.z = Dequantize(ReadDataAt(data, bit_offset, 16), -1.0, 1.0, 16);
    g.P = FromLogF3(g.P, MIN_POS_LOG2, MAX_POS_LOG2);

    float3x3 RS = 0.0;
    RS[0][0] = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    RS[1][0] = Dequantize(ReadDataAt(data, bit_offset, 8), -1.0, 1.0, 8);
    RS[2][0] = Dequantize(ReadDataAt(data, bit_offset, 8), -1.0, 1.0, 8);
    RS[1][1] = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    RS[2][1] = Dequantize(ReadDataAt(data, bit_offset, 8), -1.0, 1.0, 8);
    RS[2][2] = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    g.RS = FromLogF3x3(RS, MIN_SCALE_LOG2, MAX_SCALE_LOG2);

    g.C.x = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    g.C.y = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    g.C.z = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    g.C.xyz = FromLogF3(g.C.xyz, MIN_COLOR_LOG2, MAX_COLOR_LOG2);

    g.C.w = Dequantize(ReadDataAt(data, bit_offset, 8), 0.0, 1.0, 8);
    g.C.w = FromLogF1(g.C.w, MIN_DENSITY_LOG2, MAX_DENSITY_LOG2);
    return g;
}