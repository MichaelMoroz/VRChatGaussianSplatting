#ifndef QUATERNION_CGINC
#define QUATERNION_CGINC

float4 quaternion(float3 axis, float angle) {
    return float4(axis * sin(angle * 0.5), cos(angle * 0.5));
}

float4 qmul(float4 a, float4 b) {
    return float4(a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz), a.w * b.w - dot(a.xyz, b.xyz));
}

float3x3 unit(float a) {
    return float3x3(a, 0, 0, 0, a, 0, 0, 0, a);
}

float3 qrot(float3 x, float4 q)
{
    return x + 2.0 * cross(cross(x, q.xyz) + q.w * x, q.xyz);
}

float4 conj_q(float4 q)
{
    return float4(-q.xyz, q.w);
}

float3x3 outerProduct(float3 a, float3 b)
{
    return float3x3(a * b.x, a * b.y, a * b.z);
}

float3x3 q2m(float4 q) {
    float3 a = float3(-1, 1, 1);
    float3 u = q.zyz * a * q.w, v = q.xyx * a.xxy * q.w;
    float3x3 m = float3x3(0, u.x, u.y, u.z, 0, v.x, v.y, v.z, 0) + unit(0.5) + outerProduct(q.xyz, q.xyz) * (1.0 - unit(1.0));
    q *= q;
    m -= float3x3(q.y + q.z, 0, 0, 0, q.x + q.z, 0, 0, 0, q.x + q.y);
    return m * 2.0;
}

float4 m2q(float3x3 m) {
    float4 q;
    q.w = sqrt(max(0.0, 1.0 + m[0][0] + m[1][1] + m[2][2])) / 2.0;
    q.x = sqrt(max(0.0, 1.0 + m[0][0] - m[1][1] - m[2][2])) / 2.0;
    q.y = sqrt(max(0.0, 1.0 - m[0][0] + m[1][1] - m[2][2])) / 2.0;
    q.z = sqrt(max(0.0, 1.0 - m[0][0] - m[1][1] + m[2][2])) / 2.0;

    q.x = abs(q.x) * sign(m[2][1] - m[1][2]);
    q.y = abs(q.y) * sign(m[0][2] - m[2][0]);
    q.z = abs(q.z) * sign(m[1][0] - m[0][1]);
    
    return q;
}

float3 normalize_safe(float3 v) {
    float l2 = dot(v, v);
    if (l2 < 1e-6) return float3(0, 0, 1); // Avoid division by zero
    return v / sqrt(l2);
}

float3 quaternionAxis(float4 q)
{
    return normalize_safe(q.xyz); 
}

float quaternionAngle(float4 q)
{
    float l = length(q.xyz);
    if (l < 1e-6) return 0.0; // Avoid division by zero
    return atan2(l, q.w) * 2.0;
}

float3 quaternionToAxisAngle(float4 q)
{
    return quaternionAxis(q) * quaternionAngle(q);
}

float4 axisAngleToQuaternion(float3 aa)
{
    float angle = length(aa);
    float3 axis = normalize_safe(aa);
    return float4(axis * sin(angle * 0.5), cos(angle * 0.5));
}

#endif // QUATERNION_CGINC