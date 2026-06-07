#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"

struct appdata
{
    uint id : SV_VertexID;
#if defined(UNITY_VERTEX_INPUT_INSTANCE_ID)
    UNITY_VERTEX_INPUT_INSTANCE_ID
#endif
};

struct v2f
{
    float4 pos : SV_POSITION;
    float4 uv : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
};

v2f vert (appdata v) {
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2f, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    if(v.id == 0) {
        o.pos = float4(-1, -1, 1, 1);
    } else if(v.id == 1) {
        o.pos = float4(3, -1, 1, 1);
    } else {
        o.pos = float4(-1, 3, 1, 1);
    } 
    o.uv = ComputeGrabScreenPos(o.pos);
    return o;
}

#if (defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)) && !SHADER_TARGET_SURFACE_ANALYSIS
    #define GS_SAMPLE_GRABPASS_TEXTURE(tex, t) UNITY_SAMPLE_SCREENSPACE_TEXTURE(tex, (t).xy / (t).w)
#else
    #define GS_SAMPLE_GRABPASS_TEXTURE(tex, t) tex2Dproj(tex, UNITY_PROJ_COORD(t))
#endif
