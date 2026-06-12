// rotate vector v by quaternion q
float3 q_rotate(float3 v, float4 q) {
    float3 t  = 2.0f * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

float4 conj_q(float4 q) {
    return float4(-q.xyz, q.w);  // conjugate of quaternion
}

float3x3 outer_product(float3 a, float3 b) {
    return float3x3(a * b.x, a * b.y, a * b.z);
}

float3x3 unit(float a) {
    return float3x3(a, 0, 0, 0, a, 0, 0, 0, a);
}

#define DIV_EPSILON 1e-6
#define FINITE_LIMIT 1e8
#define SAFE_NDC_LIMIT 8.0
#define SAFE_AXIS_LIMIT 4.0
#define SAFE_ELLIPSE_SIZE_LIMIT 2.0

float safe_divide(float a, float b) {
    return (abs(b) > DIV_EPSILON) ? a / b : 0.0;
}

bool valid_float(float x)
{
    return x > -FINITE_LIMIT && x < FINITE_LIMIT;
}

bool valid_float2(float2 v)
{
    return valid_float(v.x) && valid_float(v.y);
}

bool valid_float3(float3 v)
{
    return valid_float(v.x) && valid_float(v.y) && valid_float(v.z);
}

float3x3 quat_to_mat(float4 q) {
    float3 a = float3(-1, 1, 1);
    float3 u = q.zyz * a * q.w, v = q.xyx * a.xxy * q.w;
    float3x3 m = float3x3(0, u.x, u.y, u.z, 0, v.x, v.y, v.z, 0) + unit(0.5) + outer_product(q.xyz, q.xyz) * (1.0 - unit(1.0));
    q *= q;
    m -= float3x3(q.y + q.z, 0, 0, 0, q.x + q.z, 0, 0, 0, q.x + q.y);
    return m * 2.0;
}

struct Ellipse {
    float2 center;
    float2 axis;
    float2 size;
};

bool valid_ellipse(Ellipse ellipse)
{
    return valid_float2(ellipse.center)
        && valid_float2(ellipse.axis)
        && valid_float2(ellipse.size)
        && abs(ellipse.center.x) < SAFE_NDC_LIMIT
        && abs(ellipse.center.y) < SAFE_NDC_LIMIT
        && abs(ellipse.axis.x) < SAFE_AXIS_LIMIT
        && abs(ellipse.axis.y) < SAFE_AXIS_LIMIT
        && ellipse.size.x > 0.0
        && ellipse.size.y > 0.0
        && ellipse.size.x < SAFE_ELLIPSE_SIZE_LIMIT
        && ellipse.size.y < SAFE_ELLIPSE_SIZE_LIMIT;
}

Ellipse GetProjectedEllipsoid(float3 pos, float3 scale, float4 rotation)
{
    Ellipse ellipse;
    ellipse.center = 0.0;
    ellipse.axis = float2(1.0, 0.0);
    ellipse.size = 0.0;

    float4 centerClip = UnityObjectToClipPos(float4(pos, 1.0));
    if (centerClip.w <= DIV_EPSILON) return ellipse;

    float2 centerNdc = centerClip.xy / centerClip.w;
    float3 axis0 = q_rotate(float3(scale.x, 0.0, 0.0), rotation);
    float3 axis1 = q_rotate(float3(0.0, scale.y, 0.0), rotation);
    float3 axis2 = q_rotate(float3(0.0, 0.0, scale.z), rotation);
    float4 axisClip0 = mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, float4(axis0, 0.0)));
    float4 axisClip1 = mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, float4(axis1, 0.0)));
    float4 axisClip2 = mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, float4(axis2, 0.0)));
    float3 axisDepth = float3(axisClip0.w, axisClip1.w, axisClip2.w);
    float depthSupportSq = dot(axisDepth, axisDepth);
    float depthSupport = sqrt(max(depthSupportSq, 0.0));

    // The projected silhouette becomes singular when the camera/near plane intersects
    // the ellipsoid. Reject these rare near-camera splats instead of letting c22
    // approach zero and amplify the screen covariance.
    float nearPlane = max(_ProjectionParams.y, DIV_EPSILON);
    if (centerClip.w - depthSupport <= nearPlane) return ellipse;

    float3 h0 = float3(axisClip0.x - centerNdc.x * axisClip0.w, axisClip0.y - centerNdc.y * axisClip0.w, axisClip0.w);
    float3 h1 = float3(axisClip1.x - centerNdc.x * axisClip1.w, axisClip1.y - centerNdc.y * axisClip1.w, axisClip1.w);
    float3 h2 = float3(axisClip2.x - centerNdc.x * axisClip2.w, axisClip2.y - centerNdc.y * axisClip2.w, axisClip2.w);

    float c00 = dot(float3(h0.x, h1.x, h2.x), float3(h0.x, h1.x, h2.x));
    float c01 = dot(float3(h0.x, h1.x, h2.x), float3(h0.y, h1.y, h2.y));
    float c11 = dot(float3(h0.y, h1.y, h2.y), float3(h0.y, h1.y, h2.y));
    float c02 = dot(float3(h0.x, h1.x, h2.x), float3(h0.z, h1.z, h2.z));
    float c12 = dot(float3(h0.y, h1.y, h2.y), float3(h0.z, h1.z, h2.z));
    float c22 = depthSupportSq - centerClip.w * centerClip.w;

    float c22Epsilon = max(DIV_EPSILON, centerClip.w * centerClip.w * 1e-5);
    if (c22 >= -c22Epsilon) return ellipse;

    float invC22 = 1.0 / c22;
    float invScale = -invC22;
    float2 localCenter = float2(c02, c12) * invC22;
    float covXX = (c00 - c02 * c02 * invC22) * invScale;
    float covXY = (c01 - c02 * c12 * invC22) * invScale;
    float covYY = (c11 - c12 * c12 * invC22) * invScale;
    if (!valid_float(covXX) || !valid_float(covXY) || !valid_float(covYY)) return ellipse;

    float trace = covXX + covYY;
    float diff = covXX - covYY;
    float discr = sqrt(max(diff * diff + 4.0 * covXY * covXY, 0.0));
    float lambdaMajor = 0.5 * (trace + discr);
    float lambdaMinor = 0.5 * (trace - discr);
    if (!valid_float(lambdaMajor) || !valid_float(lambdaMinor)) return ellipse;
    if (lambdaMajor <= 0.0 || lambdaMinor <= 0.0) return ellipse;

    float2 majorAxis = abs(covXY) + abs(lambdaMajor - covXX) > DIV_EPSILON
        ? normalize(float2(covXY, lambdaMajor - covXX))
        : float2(1.0, 0.0);

    ellipse.center = centerNdc + localCenter;
    ellipse.axis = majorAxis;
    ellipse.size = sqrt(float2(lambdaMajor, lambdaMinor));
    return ellipse;
}
