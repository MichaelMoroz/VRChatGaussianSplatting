#include "UnityCG.cginc"

struct v2f  { float4 pos : SV_POSITION; };

v2f vert (appdata_img v) {
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex); // use optimal quad size for sorting
    return o;
}