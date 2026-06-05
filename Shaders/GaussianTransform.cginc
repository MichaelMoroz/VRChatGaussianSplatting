#ifndef __GS_GAUSSIAN_TRANSFORM_CGINC__
#define __GS_GAUSSIAN_TRANSFORM_CGINC__

#include "Quaternion.cginc"

struct GaussianTransformData
{
    float3 position;
    float4 rotation;
    float3 scale;
};

void GSJacobiRotate01(inout float3x3 covariance, inout float3x3 eigenvectors)
{
    float apq = covariance[0][1];
    if (abs(apq) <= 1e-8)
    {
        return;
    }

    float tau = (covariance[1][1] - covariance[0][0]) / (2.0 * apq);
    float t = sign(tau) / (abs(tau) + sqrt(1.0 + tau * tau));
    float c = rsqrt(1.0 + t * t);
    float s = t * c;

    float a00 = covariance[0][0];
    float a11 = covariance[1][1];
    float a02 = covariance[0][2];
    float a12 = covariance[1][2];

    covariance[0][0] = a00 - t * apq;
    covariance[1][1] = a11 + t * apq;
    covariance[0][1] = 0.0;
    covariance[1][0] = 0.0;
    covariance[0][2] = c * a02 - s * a12;
    covariance[2][0] = covariance[0][2];
    covariance[1][2] = s * a02 + c * a12;
    covariance[2][1] = covariance[1][2];

    float v00 = eigenvectors[0][0];
    float v10 = eigenvectors[1][0];
    float v20 = eigenvectors[2][0];
    float v01 = eigenvectors[0][1];
    float v11 = eigenvectors[1][1];
    float v21 = eigenvectors[2][1];
    eigenvectors[0][0] = c * v00 - s * v01;
    eigenvectors[1][0] = c * v10 - s * v11;
    eigenvectors[2][0] = c * v20 - s * v21;
    eigenvectors[0][1] = s * v00 + c * v01;
    eigenvectors[1][1] = s * v10 + c * v11;
    eigenvectors[2][1] = s * v20 + c * v21;
}

void GSJacobiRotate02(inout float3x3 covariance, inout float3x3 eigenvectors)
{
    float apq = covariance[0][2];
    if (abs(apq) <= 1e-8)
    {
        return;
    }

    float tau = (covariance[2][2] - covariance[0][0]) / (2.0 * apq);
    float t = sign(tau) / (abs(tau) + sqrt(1.0 + tau * tau));
    float c = rsqrt(1.0 + t * t);
    float s = t * c;

    float a00 = covariance[0][0];
    float a22 = covariance[2][2];
    float a01 = covariance[0][1];
    float a12 = covariance[1][2];

    covariance[0][0] = a00 - t * apq;
    covariance[2][2] = a22 + t * apq;
    covariance[0][2] = 0.0;
    covariance[2][0] = 0.0;
    covariance[0][1] = c * a01 - s * a12;
    covariance[1][0] = covariance[0][1];
    covariance[1][2] = s * a01 + c * a12;
    covariance[2][1] = covariance[1][2];

    float v00 = eigenvectors[0][0];
    float v10 = eigenvectors[1][0];
    float v20 = eigenvectors[2][0];
    float v02 = eigenvectors[0][2];
    float v12 = eigenvectors[1][2];
    float v22 = eigenvectors[2][2];
    eigenvectors[0][0] = c * v00 - s * v02;
    eigenvectors[1][0] = c * v10 - s * v12;
    eigenvectors[2][0] = c * v20 - s * v22;
    eigenvectors[0][2] = s * v00 + c * v02;
    eigenvectors[1][2] = s * v10 + c * v12;
    eigenvectors[2][2] = s * v20 + c * v22;
}

void GSJacobiRotate12(inout float3x3 covariance, inout float3x3 eigenvectors)
{
    float apq = covariance[1][2];
    if (abs(apq) <= 1e-8)
    {
        return;
    }

    float tau = (covariance[2][2] - covariance[1][1]) / (2.0 * apq);
    float t = sign(tau) / (abs(tau) + sqrt(1.0 + tau * tau));
    float c = rsqrt(1.0 + t * t);
    float s = t * c;

    float a11 = covariance[1][1];
    float a22 = covariance[2][2];
    float a01 = covariance[0][1];
    float a02 = covariance[0][2];

    covariance[1][1] = a11 - t * apq;
    covariance[2][2] = a22 + t * apq;
    covariance[1][2] = 0.0;
    covariance[2][1] = 0.0;
    covariance[0][1] = c * a01 - s * a02;
    covariance[1][0] = covariance[0][1];
    covariance[0][2] = s * a01 + c * a02;
    covariance[2][0] = covariance[0][2];

    float v01 = eigenvectors[0][1];
    float v11 = eigenvectors[1][1];
    float v21 = eigenvectors[2][1];
    float v02 = eigenvectors[0][2];
    float v12 = eigenvectors[1][2];
    float v22 = eigenvectors[2][2];
    eigenvectors[0][1] = c * v01 - s * v02;
    eigenvectors[1][1] = c * v11 - s * v12;
    eigenvectors[2][1] = c * v21 - s * v22;
    eigenvectors[0][2] = s * v01 + c * v02;
    eigenvectors[1][2] = s * v11 + c * v12;
    eigenvectors[2][2] = s * v21 + c * v22;
}

void GSSortEigenSystem(inout float3 eigenvalues, inout float3x3 eigenvectors)
{
    if (eigenvalues.x < eigenvalues.y)
    {
        float tmp = eigenvalues.x;
        eigenvalues.x = eigenvalues.y;
        eigenvalues.y = tmp;
        float3 column = float3(eigenvectors[0][0], eigenvectors[1][0], eigenvectors[2][0]);
        eigenvectors[0][0] = eigenvectors[0][1];
        eigenvectors[1][0] = eigenvectors[1][1];
        eigenvectors[2][0] = eigenvectors[2][1];
        eigenvectors[0][1] = column.x;
        eigenvectors[1][1] = column.y;
        eigenvectors[2][1] = column.z;
    }
    if (eigenvalues.x < eigenvalues.z)
    {
        float tmp = eigenvalues.x;
        eigenvalues.x = eigenvalues.z;
        eigenvalues.z = tmp;
        float3 column = float3(eigenvectors[0][0], eigenvectors[1][0], eigenvectors[2][0]);
        eigenvectors[0][0] = eigenvectors[0][2];
        eigenvectors[1][0] = eigenvectors[1][2];
        eigenvectors[2][0] = eigenvectors[2][2];
        eigenvectors[0][2] = column.x;
        eigenvectors[1][2] = column.y;
        eigenvectors[2][2] = column.z;
    }
    if (eigenvalues.y < eigenvalues.z)
    {
        float tmp = eigenvalues.y;
        eigenvalues.y = eigenvalues.z;
        eigenvalues.z = tmp;
        float3 column = float3(eigenvectors[0][1], eigenvectors[1][1], eigenvectors[2][1]);
        eigenvectors[0][1] = eigenvectors[0][2];
        eigenvectors[1][1] = eigenvectors[1][2];
        eigenvectors[2][1] = eigenvectors[2][2];
        eigenvectors[0][2] = column.x;
        eigenvectors[1][2] = column.y;
        eigenvectors[2][2] = column.z;
    }
}

void GSDiagonalizeSymmetricCovariance(float3x3 covariance, out float3x3 eigenvectors, out float3 eigenvalues)
{
    eigenvectors = float3x3(
        1.0, 0.0, 0.0,
        0.0, 1.0, 0.0,
        0.0, 0.0, 1.0);

    [unroll] for (int sweep = 0; sweep < 6; sweep++)
    {
        GSJacobiRotate01(covariance, eigenvectors);
        GSJacobiRotate02(covariance, eigenvectors);
        GSJacobiRotate12(covariance, eigenvectors);
    }

    eigenvalues = float3(covariance[0][0], covariance[1][1], covariance[2][2]);
    GSSortEigenSystem(eigenvalues, eigenvectors);
    if (determinant(eigenvectors) < 0.0)
    {
        eigenvectors[0][0] = -eigenvectors[0][0];
        eigenvectors[1][0] = -eigenvectors[1][0];
        eigenvectors[2][0] = -eigenvectors[2][0];
    }
}

GaussianTransformData GSTransformGaussian(GaussianTransformData gaussian, float4x4 localToWorld, float4 transformRotation, float3 transformScale)
{
    float3x3 sourceRotationMatrix = q2m(gaussian.rotation);
    float3x3 sourceScaleSq = float3x3(
        gaussian.scale.x * gaussian.scale.x, 0, 0,
        0, gaussian.scale.y * gaussian.scale.y, 0,
        0, 0, gaussian.scale.z * gaussian.scale.z);
    float3x3 sourceCovariance = mul(sourceRotationMatrix, mul(sourceScaleSq, transpose(sourceRotationMatrix)));

    float4 transformRotationMatrixQuat = qconj(transformRotation);
    float3x3 transformRotationMatrix = q2m(transformRotationMatrixQuat);
    float3x3 transformScaleMatrix = float3x3(
        transformScale.x, 0, 0,
        0, transformScale.y, 0,
        0, 0, transformScale.z);
    float3x3 linearTransform = mul(transformRotationMatrix, transformScaleMatrix);
    float3x3 transformedCovariance = mul(linearTransform, mul(sourceCovariance, transpose(linearTransform)));

    float3x3 eigenvectors;
    float3 eigenvalues;
    GSDiagonalizeSymmetricCovariance(transformedCovariance, eigenvectors, eigenvalues);

    GaussianTransformData result;
    result.position = mul(localToWorld, float4(gaussian.position, 1.0)).xyz;
    result.scale = sqrt(max(eigenvalues, float3(0.0, 0.0, 0.0)));
    result.rotation = m2q(eigenvectors);
    if (result.rotation.w < 0)
    {
        result.rotation = -result.rotation;
    }

    return result;
}

#endif