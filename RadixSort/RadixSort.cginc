#include "UnityCG.cginc"
#include "Utils.cginc"

struct v2f  { float4 pos : SV_POSITION; };

Texture2D<float2> _KeyValues;
Texture2D<float4> _Histograms;
Texture2D<float> _PrefixSums;
float4 _KeyValues_TexelSize;
float4 _Histograms_TexelSize;
float4 _PrefixSums_TexelSize;
int _ElementCount;
int _CurrentBit;
int _BitsPerStep;
int _GroupSize;
int _ImageSizeLog2X;
int _ImageSizeLog2Y;
int _ImageElementsLog2;
float2 _Scale;
float2 _HistogramScale;

v2f vert (appdata_img v) {
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex * float4(_Scale, 1.0, 1.0)); // use optimal quad size for sorting
    return o;
}

v2f vertHistogram (appdata_img v) {
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex * float4(_HistogramScale, 1.0, 1.0));
    return o;
}

// Digit j of a group's digit pack: 4 bits each, 6 per float channel as exact ints.
uint DigitAt(float4 pack, uint j) {
    uint channel = j / 6u;
    uint shift = (j - channel * 6u) * 4u;
    uint bits = channel == 0u ? (uint)round(pack.x) : (channel == 1u ? (uint)round(pack.y) : (uint)round(pack.z));
    return (bits >> shift) & 15u;
}
