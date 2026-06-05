#ifndef __GS_QUATERNION_CGINC__
#define __GS_QUATERNION_CGINC__

float3x3 q_unit(float a)
{
    return float3x3(a, 0, 0, 0, a, 0, 0, 0, a);
}

float4 qconj(float4 q)
{
    return float4(-q.xyz, q.w);
}

float3x3 outerProduct(float3 a, float3 b)
{
    return float3x3(a * b.x, a * b.y, a * b.z);
}

float3x3 q2m(float4 q)
{
    float3 a = float3(-1, 1, 1);
    float3 u = q.zyz * a * q.w;
    float3 v = q.xyx * a.xxy * q.w;
    float3x3 m = float3x3(0, u.x, u.y, u.z, 0, v.x, v.y, v.z, 0) + q_unit(0.5) + outerProduct(q.xyz, q.xyz) * (1.0 - q_unit(1.0));
    q *= q;
    m -= float3x3(q.y + q.z, 0, 0, 0, q.x + q.z, 0, 0, 0, q.x + q.y);
    return m * 2.0;
}

float4 m2q(float3x3 m)
{
    float4 q;
    q.w = sqrt(max(0.0, 1.0 + m[0][0] + m[1][1] + m[2][2])) * 0.5;
    q.x = sqrt(max(0.0, 1.0 + m[0][0] - m[1][1] - m[2][2])) * 0.5;
    q.y = sqrt(max(0.0, 1.0 - m[0][0] + m[1][1] - m[2][2])) * 0.5;
    q.z = sqrt(max(0.0, 1.0 - m[0][0] - m[1][1] + m[2][2])) * 0.5;

    q.x = abs(q.x) * sign(m[2][1] - m[1][2]);
    q.y = abs(q.y) * sign(m[0][2] - m[2][0]);
    q.z = abs(q.z) * sign(m[1][0] - m[0][1]);
    return q;
}

#endif