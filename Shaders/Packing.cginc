float FromLog(float lg, float minv, float maxv) {
    return exp2(lg * (maxv - minv) + minv) / lg;
}

float ToLog(float ex, float minv, float maxv) {
    return (log2(ex) - minv) / (ex * (maxv - minv));
}

float maxc(float3 v) {
    return max(max(v.x, v.y), v.z);
}

float maxc(float3x3 v) {
    return max(max(maxc(v[0]), maxc(v[1])), maxc(v[2]));
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
    return (uint)clamp(round((v - mn) / (mx - mn) * levels), 0.0f, levels - 1.0f);
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

void WriteQuantizedSigned(inout uint4 data, inout int bit_offset, float v, uint bits) {
    uint q = Quantize(v, -1.0, 1.0, bits);
    WriteDataAt(data, bit_offset, q, bits);
}

void WriteQuantizedUnsigned(inout uint4 data, inout int bit_offset, float v, uint bits) {
    uint q = Quantize(v, 0.0, 1.0, bits);
    WriteDataAt(data, bit_offset, q, bits);
}

float ReadQuantizedSigned(uint4 data, inout int bit_offset, uint bits) {
    uint q = ReadDataAt(data, bit_offset, bits);
    return Dequantize(q, -1.0, 1.0, bits);
}

float ReadQuantizedUnsigned(uint4 data, inout int bit_offset, uint bits) {
    uint q = ReadDataAt(data, bit_offset, bits);
    return Dequantize(q, 0.0, 1.0, bits);
}

struct GaussianData {
    float3 P; // position
    float3x3 RS; // Cholesky factorization of covariance matrix, works same as rotation * scale
    float4 C; // density / color
};

// Should sum to 128 bits
static const int X_POS_BITS = 21;
static const int Y_POS_BITS = 21;
static const int Z_POS_BITS = 21;
static const int XX_RS_BITS = 11;
static const int YY_RS_BITS = 11;
static const int ZZ_RS_BITS = 10;
static const int XY_RS_BITS = 11;
static const int XZ_RS_BITS = 11;
static const int YZ_RS_BITS = 11;

uint4 PackGaussianData(GaussianData g, int4 ScalesLOG2)
{   
    g.RS /= max(1e-8, length(g.P)); // Store scale relative to distance from origin to improve precision
    uint4 data = 0u;
    int bit_offset = 0;
    float3 Plog2 = ToLogF3(g.P, ScalesLOG2.x, ScalesLOG2.y);
    float3x3 RSlog2 = ToLogF3x3(g.RS, ScalesLOG2.z, ScalesLOG2.w);

    WriteQuantizedSigned(data, bit_offset, Plog2.x, X_POS_BITS);
    WriteQuantizedSigned(data, bit_offset, Plog2.y, Y_POS_BITS);
    WriteQuantizedSigned(data, bit_offset, Plog2.z, Z_POS_BITS);

    //diagonal terms are positive
    WriteQuantizedUnsigned(data, bit_offset, RSlog2[0][0], XX_RS_BITS);
    WriteQuantizedUnsigned(data, bit_offset, RSlog2[1][1], YY_RS_BITS);
    WriteQuantizedUnsigned(data, bit_offset, RSlog2[2][2], ZZ_RS_BITS);
    
    //off-diagonal terms are signed
    WriteQuantizedSigned(data, bit_offset, RSlog2[1][0], XY_RS_BITS);
    WriteQuantizedSigned(data, bit_offset, RSlog2[2][0], XZ_RS_BITS);
    WriteQuantizedSigned(data, bit_offset, RSlog2[2][1], YZ_RS_BITS);

    return data;
}

GaussianData UnpackGaussianData(uint4 data, int4 ScalesLOG2)
{
    GaussianData g;
    g.P = 0.0;
    g.RS = 0.0;
    g.C = 0.0;

    int bit_offset = 0;
    g.P.x = ReadQuantizedSigned(data, bit_offset, X_POS_BITS);
    g.P.y = ReadQuantizedSigned(data, bit_offset, Y_POS_BITS);
    g.P.z = ReadQuantizedSigned(data, bit_offset, Z_POS_BITS);
    g.RS[0][0] = ReadQuantizedUnsigned(data, bit_offset, XX_RS_BITS);
    g.RS[1][1] = ReadQuantizedUnsigned(data, bit_offset, YY_RS_BITS);
    g.RS[2][2] = ReadQuantizedUnsigned(data, bit_offset, ZZ_RS_BITS);
    g.RS[1][0] = ReadQuantizedSigned(data, bit_offset, XY_RS_BITS);
    g.RS[2][0] = ReadQuantizedSigned(data, bit_offset, XZ_RS_BITS);
    g.RS[2][1] = ReadQuantizedSigned(data, bit_offset, YZ_RS_BITS);

    g.P = FromLogF3(g.P, ScalesLOG2.x, ScalesLOG2.y);
    g.RS = FromLogF3x3(g.RS, ScalesLOG2.z, ScalesLOG2.w);
    g.RS *= max(1e-8, length(g.P)); // Restore scale relative to distance from origin
    return g;
}