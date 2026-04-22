// rotate vector v by quaternion q
float3 q_rotate(float3 v, float4 q) {
    float3 t  = 2.0f * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

float4 conj_q(float4 q) {
    return float4(-q.xyz, q.w);  // conjugate of quaternion
}

float3 unit_space_to_model(float3 p, float3 pos, float4 rot, float3 rad) {
    return q_rotate(p * rad, rot) + pos;  // rotate and scale position
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
#define SAFE_NORM_POINT_LIMIT 4.0
#define SAFE_RENORM_LIMIT 16.0

float safe_divide(float a, float b) {
    return (abs(b) > DIV_EPSILON) ? a / b : 0.0;
}

float safe_sqrt(float a) {
    return (a > 0.0) ? sqrt(a) : 0.0;  // return 0 for negative inputs
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

Ellipse extractEllipse(float a, float b, float c, float d, float e, float f) {
    float delta = c * c - 4.0 * a * b;
    float h = safe_divide(2.0 * b * d - c * e, delta);
    float k = safe_divide(2.0 * a * e - c * d, delta);

    float Fp = a * h * h + b * k * k + c * h * k + d * h + e * k + f;

    float diff_ba = b - a;
    float sum_ba  = b + a;
    float J = sqrt(diff_ba * diff_ba + c * c);

    float lambda1 = (sum_ba + J) * 0.5;
    float lambda2 = (sum_ba - J) * 0.5;

    float r = safe_divide(diff_ba, c);
    float ca = safe_divide(0.5 * sign(c), sqrt(1.0 + r * r));
    float ch = sqrt(0.5 + ca) * sqrt(0.5);
    float sh = sqrt(0.5 - ca) * sqrt(0.5) * sign(diff_ba);
    float cos_theta = ch - sh;
    float sin_theta = ch + sh;

    float a1 = safe_sqrt(-safe_divide(Fp, lambda1));
    float a2 = safe_sqrt(-safe_divide(Fp, lambda2));

    Ellipse ellipse;
    ellipse.center = float2(h, k);
    ellipse.axis   = float2(cos_theta, sin_theta);
    ellipse.size   = float2(a1, a2);
    return ellipse;
}

float2 project_object_to_ndc(float3 p)
{
    float4 clip = UnityObjectToClipPos(float4(p, 1.0));
    float clipW = max(clip.w, DIV_EPSILON);
    return clamp(clip.xy / clipW, -SAFE_NDC_LIMIT, SAFE_NDC_LIMIT);
}

void stable_tangent_basis(float3 n, out float3 u, out float3 v)
{
    if (n.z < -0.999999)
    {
        u = float3(0.0, -1.0, 0.0);
        v = float3(-1.0, 0.0, 0.0);
        return;
    }

    float a = 1.0 / (1.0 + n.z);
    u = normalize(float3(1.0 - n.x * n.x * a, -n.x * n.y * a, -n.x));
    v = cross(n, u);
}

Ellipse fit_outline_ellipse_5(float2 points[5], float2 centerNdc)
{
    Ellipse ellipse;
    ellipse.center = 0.0;
    ellipse.axis = float2(1.0, 0.0);
    ellipse.size = 0.0;

    float2 p0 = points[0];
    float2 p1 = points[1];
    float2 p2 = points[2];
    float2 p3 = points[3];
    float2 p4 = points[4];
    float2 outlineMin = min(min(min(min(p0, p1), p2), p3), min(p4, centerNdc));
    float2 outlineMax = max(max(max(max(p0, p1), p2), p3), max(p4, centerNdc));
    float2 bboxCenter = 0.5 * (outlineMin + outlineMax);
    float2 bboxHalfExtent = max(0.5 * (outlineMax - outlineMin), float2(DIV_EPSILON, DIV_EPSILON));
    float2 n0 = (p0 - bboxCenter) / bboxHalfExtent;
    float2 n1 = (p1 - bboxCenter) / bboxHalfExtent;
    float2 n2 = (p2 - bboxCenter) / bboxHalfExtent;
    float2 n3 = (p3 - bboxCenter) / bboxHalfExtent;
    float2 n4 = (p4 - bboxCenter) / bboxHalfExtent;

    float sx = n0.x - n1.x;
    float sy = n1.y - n0.y;
    if (abs(sx) <= DIV_EPSILON || abs(sy) <= DIV_EPSILON) return ellipse;

    float invSx = 1.0 / sx;
    float invSy = 1.0 / sy;
    float offsetX = n1.x;
    float offsetY = n0.y;
    float u2 = (n2.x - offsetX) * invSx;
    float v2 = (n2.y - offsetY) * invSy;
    float u3 = (n3.x - offsetX) * invSx;
    float v3 = (n3.y - offsetY) * invSy;
    float u4 = (n4.x - offsetX) * invSx;
    float v4 = (n4.y - offsetY) * invSy;
    float m00 = u2 * u2 - u2;
    float m01 = 2.0 * u2 * v2;
    float m02 = v2 * v2 - v2;
    float r0 = u2 + v2 - 1.0;
    float m10 = u3 * u3 - u3;
    float m11 = 2.0 * u3 * v3;
    float m12 = v3 * v3 - v3;
    float r1 = u3 + v3 - 1.0;
    float m20 = u4 * u4 - u4;
    float m21 = 2.0 * u4 * v4;
    float m22 = v4 * v4 - v4;
    float r2 = u4 + v4 - 1.0;

    if (abs(m00) <= DIV_EPSILON) return ellipse;
    float invM00 = 1.0 / m00;
    m01 *= invM00;
    m02 *= invM00;
    r0 *= invM00;
    m11 -= m10 * m01;
    m12 -= m10 * m02;
    r1 -= m10 * r0;
    m21 -= m20 * m01;
    m22 -= m20 * m02;
    r2 -= m20 * r0;
    if (abs(m11) <= DIV_EPSILON) return ellipse;
    float invM11 = 1.0 / m11;
    m12 *= invM11;
    r1 *= invM11;
    m22 -= m21 * m12;
    r2 -= m21 * r1;
    if (abs(m22) <= DIV_EPSILON) return ellipse;
    r2 /= m22;

    float conicC = r2;
    float conicB = r1 - m12 * conicC;
    float conicA = r0 - m01 * conicB - m02 * conicC;
    float conicD = -(conicA + 1.0);
    float conicE = -(conicC + 1.0);
    float invSx2 = invSx * invSx;
    float invSy2 = invSy * invSy;
    float invSxSy = invSx * invSy;
    float coeffA = conicA * invSx2;
    float coeffB = conicB * invSxSy;
    float coeffC = conicC * invSy2;
    float coeffD = -2.0 * coeffA * offsetX - 2.0 * coeffB * offsetY + conicD * invSx;
    float coeffE = -2.0 * coeffB * offsetX - 2.0 * coeffC * offsetY + conicE * invSy;
    float coeffF = coeffA * offsetX * offsetX + 2.0 * coeffB * offsetX * offsetY + coeffC * offsetY * offsetY - conicD * invSx * offsetX - conicE * invSy * offsetY + 1.0;
    float invF = -safe_divide(1.0, coeffF);
    if (invF == 0.0) return ellipse;

    float ellipseA = coeffA * invF;
    float ellipseB = coeffC * invF;
    float ellipseC = 2.0 * coeffB * invF;
    float ellipseD = coeffD * invF;
    float ellipseE = coeffE * invF;
    float invHx = safe_divide(1.0, bboxHalfExtent.x);
    float invHy = safe_divide(1.0, bboxHalfExtent.y);
    float invHx2 = invHx * invHx;
    float invHy2 = invHy * invHy;
    float invHxHy = invHx * invHy;
    float bboxX = bboxCenter.x;
    float bboxY = bboxCenter.y;
    float finalA = ellipseA * invHx2;
    float finalB = ellipseB * invHy2;
    float finalC = ellipseC * invHxHy;
    float finalD = ellipseD * invHx - 2.0 * ellipseA * bboxX * invHx2 - ellipseC * bboxY * invHxHy;
    float finalE = ellipseE * invHy - 2.0 * ellipseB * bboxY * invHy2 - ellipseC * bboxX * invHxHy;
    float finalF = ellipseA * bboxX * bboxX * invHx2 + ellipseB * bboxY * bboxY * invHy2 + ellipseC * bboxX * bboxY * invHxHy - ellipseD * bboxX * invHx - ellipseE * bboxY * invHy - 1.0;
    if (abs(finalF) <= DIV_EPSILON) return ellipse;

    float invConicF = -safe_divide(1.0, finalF);
    if (invConicF == 0.0) return ellipse;
    finalA *= invConicF;
    finalB *= invConicF;
    finalC *= invConicF;
    finalD *= invConicF;
    finalE *= invConicF;

    return extractEllipse(finalA, finalB, finalC, finalD, finalE, -1.0);
}

void GetProjectedEllipsoidOutline(float3 pos, float3 scale, float4 rotation, out float2 points[5], out float2 centerNdc)
{
    centerNdc = project_object_to_ndc(pos);
    float3 cameraObjectPos = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
    float3 invScale = 1.0 / max(scale, float3(DIV_EPSILON, DIV_EPSILON, DIV_EPSILON));
    float3 viewOriginLocal = q_rotate(cameraObjectPos - pos, conj_q(rotation)) * invScale;
    float viewDistanceRaw = length(viewOriginLocal);
    float3 viewDirLocal = viewDistanceRaw > DIV_EPSILON ? viewOriginLocal / viewDistanceRaw : float3(0.0, 0.0, 1.0);
    float viewDistance = max(viewDistanceRaw, 1.0 + DIV_EPSILON);
    float3 tangentCircleCenter = viewDirLocal / viewDistance;
    float tangentCircleRadius = sqrt(max(1.0 - 1.0 / (viewDistance * viewDistance), 0.0));
    float3 tangentBasisU;
    float3 tangentBasisV;
    stable_tangent_basis(viewDirLocal, tangentBasisU, tangentBasisV);

    [unroll] for (uint i = 0; i < 5; i++)
    {
        float theta = 6.2831853 * float(i) / 5.0;
        float3 localPoint = tangentCircleCenter + tangentCircleRadius * (cos(theta) * tangentBasisU + sin(theta) * tangentBasisV);
        points[i] = project_object_to_ndc(unit_space_to_model(localPoint, pos, rotation, scale));
    }
}

Ellipse GetProjectedEllipsoid(float3 pos, float3 scale, float4 rotation)
{
    float2 centerNdc;
    float2 points[5];
    GetProjectedEllipsoidOutline(pos, scale, rotation, points, centerNdc);
    return fit_outline_ellipse_5(points, centerNdc);
}
