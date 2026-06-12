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
