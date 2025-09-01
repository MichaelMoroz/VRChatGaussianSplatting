#ifndef UTILITIES_CGINC
#define UTILITIES_CGINC

#include "Random.cginc"
#include "Packing.cginc"
#include "Quaternion.cginc"
#include "Linalg.cginc"

float3 rgb_to_oklab(float3 c) 
{
    float l = 0.4121656120f * c.r + 0.5362752080f * c.g + 0.0514575653f * c.b;
    float m = 0.2118591070f * c.r + 0.6807189584f * c.g + 0.1074065790f * c.b;
    float s = 0.0883097947f * c.r + 0.2818474174f * c.g + 0.6302613616f * c.b;

    float l_ = pow(max(l, 0.0), 1./3.);
    float m_ = pow(max(m, 0.0), 1./3.);
    float s_ = pow(max(s, 0.0), 1./3.);

    float3 labResult;
    labResult.x = 0.2104542553f*l_ + 0.7936177850f*m_ - 0.0040720468f*s_;
    labResult.y = 1.9779984951f*l_ - 2.4285922050f*m_ + 0.4505937099f*s_;
    labResult.z = 0.0259040371f*l_ + 0.7827717662f*m_ - 0.8086757660f*s_;
    return labResult;
}

float3 oklab_to_rgb(float3 c) 
{
    //c.yz *= c.x;
    float l_ = c.x + 0.3963377774f * c.y + 0.2158037573f * c.z;
    float m_ = c.x - 0.1055613458f * c.y - 0.0638541728f * c.z;
    float s_ = c.x - 0.0894841775f * c.y - 1.2914855480f * c.z;

    float l = l_*l_*l_;
    float m = m_*m_*m_;
    float s = s_*s_*s_;

    float3 rgbResult;
    rgbResult.r = + 4.0767245293f*l - 3.3072168827f*m + 0.2307590544f*s;
    rgbResult.g = - 1.2681437731f*l + 2.6093323231f*m - 0.3411344290f*s;
    rgbResult.b = - 0.0041119885f*l - 0.7034763098f*m + 1.7068625689f*s;
    return rgbResult;
}

#define TAU 6.28318530718 // 2 * PI

float3 oklch2oklab(float3 lch) {
    return float3(lch.x, lch.y * cos(lch.z * TAU), lch.y * sin(lch.z * TAU));
}

float3 oklab2oklch(float3 lab) {
    float h = (lab.y != 0.0) ? atan2(lab.z, lab.y) : 0.0; // atan2 handles the case when lab.y is zero
    float c = sqrt(lab.y * lab.y + lab.z * lab.z);
    return float3(lab.x, c, h / TAU);
}

float3 shift_color_oklch(float3 rgb, float3 shift) {
    float3 oklch = oklab2oklch(rgb_to_oklab(rgb));
    oklch += shift;
    oklch.y = max(0.0, oklch.y); 
    float3 oklab = oklch2oklab(oklch);
    rgb = max(oklab_to_rgb(oklab), 0.0); 
    return rgb;
}

#endif