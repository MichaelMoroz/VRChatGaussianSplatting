#ifndef __GS_GAUSSIAN_TRANSFORM_CGINC__
#define __GS_GAUSSIAN_TRANSFORM_CGINC__

#include "Quaternion.cginc"
#include "SVD.cginc"

struct GaussianTransformData
{
    float3 position;
    float4 rotation;
    float3 scale;
};

GaussianTransformData GSTransformGaussian(GaussianTransformData gaussian, float4x4 localToWorld)
{
    float3x3 rotationMatrix = q2m(gaussian.rotation);

    float3x3 scaleSq = float3x3(
        gaussian.scale.x * gaussian.scale.x, 0, 0,
        0, gaussian.scale.y * gaussian.scale.y, 0,
        0, 0, gaussian.scale.z * gaussian.scale.z);

    float3x3 covariance = mul(rotationMatrix, mul(scaleSq, transpose(rotationMatrix)));

    float3 transformedPosition = mul(localToWorld, float4(gaussian.position, 1.0)).xyz;
    float3x3 linearTransform = (float3x3)localToWorld;
    float3x3 transformedCovariance = mul(linearTransform, mul(covariance, transpose(linearTransform)));

    float3x3 u;
    float3x3 v;
    float3 d;
    GetSVD3D(transformedCovariance, u, d, v);

    if (determinant(u) < 0)
    {
        u[0] = -u[0];
    }

    GaussianTransformData result;
    result.position = transformedPosition;
    result.scale = sqrt(max(d, float3(0.0, 0.0, 0.0)));
    result.rotation = m2q(u);
    if (result.rotation.w < 0)
    {
        result.rotation = -result.rotation;
    }

    return result;
}

#endif