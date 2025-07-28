/*
    https://github.com/vanish87/UnitySVDComputeShader/blob/master/UnitySVDComputeShader/Assets/Math.cginc
    UnitySVDComputeShader by vanish87
*/

#ifndef __SVDHLSL__
#define __SVDHLSL__

#define EPSILON 1.19209e-07

float2x2 G2(float c, float s)
{
    return float2x2(c, s, -s, c);
}

float2 GetGivensConventionalCS(float a, float b)
{
    float d = a * a + b * b;
    float c = 1;
    float s = 0;
    if (abs(d) > 0)
    {
        float t = rsqrt(d);
        c = a * t;
        s = -b * t;
    }

    return float2(c, s);
}

float2 GetGivensUnConventionalCS(float a, float b)
{

    float d = a * a + b * b;
    float c = 0;
    float s = 1;
    if (abs(d) > 0)
    {
        float t = rsqrt(d);
        s = a * t;
        c = b * t;
    }

    return float2(c, s);
}

float3x3 G3_12(float c, float s, bool use_conventional = true)
{
    float2 cs = use_conventional ? GetGivensConventionalCS(c, s) : GetGivensUnConventionalCS(c, s);
    c = cs.x;
    s = cs.y;
    return float3x3(c, s, 0,
        -s, c, 0,
        0, 0, 1);
}

float3x3 G3_12_Direct(float c, float s)
{
    return float3x3(c, s, 0,
        -s, c, 0,
        0, 0, 1);
}


float3x3 G3_23(float c, float s, bool use_conventional = true)
{
    float2 cs = use_conventional ? GetGivensConventionalCS(c, s) : GetGivensUnConventionalCS(c, s);
    c = cs.x;
    s = cs.y;
    return float3x3(1, 0, 0,
        0, c, s,
        0, -s, c);
}
float3x3 G3_23_Direct(float c, float s)
{
    return float3x3(1, 0, 0,
        0, c, s,
        0, -s, c);
}


float3x3 G3_13(float c, float s, bool use_conventional = true)
{
    float2 cs = use_conventional ? GetGivensConventionalCS(c, s) : GetGivensUnConventionalCS(c, s);
    c = cs.x;
    s = cs.y;
    return float3x3(c, 0, s,
        0, 1, 0,
        -s, 0, c);
}

float3x3 G3_13_Direct(float c, float s)
{
    return float3x3(c, 0, s,
        0, 1, 0,
        -s, 0, c);
}

void GetPolarDecomposition2D(in float2x2 A, out float2x2 R, out float2x2 S)
{
    R = float2x2(0, 0, 0, 0);
    S = float2x2(0, 0, 0, 0);

    float x = A[0][0] + A[1][1];
    float y = A[1][0] - A[0][1];

    float d = sqrt(x * x + y * y);

    float c = 1;
    float s = 0;

    R = G2(c, s);

    if (abs(d) > EPSILON)
    {
        d = 1.0f / d;
        R = G2(x * d, -y * d);
    }

    S = mul(transpose(R), A);
}


void GetSVD2D(in float2x2 A, out float2x2 U, out float2 D, out float2x2 V)
{
    U = float2x2(0, 0, 0, 0);
    D = float2(0, 0);
    V = float2x2(0, 0, 0, 0);

    float2x2 R = float2x2(0, 0, 0, 0);
    float2x2 S = float2x2(0, 0, 0, 0);

    GetPolarDecomposition2D(A, R, S);

    float c = 1;
    float s = 0;

    if (abs(S[0][1]) < EPSILON)
    {
        D[0] = S[0][0];
        D[1] = S[1][1];
    }
    else
    {
        float taw = 0.5f * (S[0][0] - S[1][1]);
        float w = sqrt(taw * taw + S[0][1] * S[0][1]);
        float t = taw > 0 ? S[0][1] / (taw + w) : S[0][1] / (taw - w);

        c = rsqrt(t * t + 1);
        s = -t * c;

        D[0] = c * c * S[0][0] - 2 * c * s * S[0][1] + s * s * S[1][1];
        D[1] = s * s * S[0][0] + 2 * c * s * S[0][1] + c * c * S[1][1];

    }

    if (D[0] < D[1])
    {
        float temp = D[0];
        D[0] = D[1];
        D[1] = temp;

        V = G2(-s, c);
    }
    else
    {
        V = G2(c, s);
    }

    U = mul(R, V);
}

void CodeZerochasing(inout float3x3 U, inout float3x3 A, inout float3x3 V)
{
    float3x3 G = G3_12(A[0][0], A[1][0]);
    A = mul(transpose(G), A);
    U = mul(U, G);
    //checked
        
    float c = A[0][1];
    float s = A[0][2];
    if (abs(A[1][0]) > EPSILON)
    {
        c = A[0][0] * A[0][1] + A[1][0] * A[1][1];
        s = A[0][0] * A[0][2] + A[1][0] * A[1][2];
    }

    G = G3_23(c, s);
    A = mul(A, G);
    V = mul(V, G);
    //checked;
    
    G = G3_23(A[1][1], A[2][1]);
    A = mul(transpose(G), A);
    U = mul(U, G);
    //checked
}

void Zerochasing(inout float3x3 U, inout float3x3 A, inout float3x3 V)
{
    float3x3 G = G3_23(A[0][1], A[0][2]);
    A = mul(A, G);
    U = mul(transpose(G), U);

    G = G3_23(A[0][1], A[0][2]);
    A = mul(transpose(G), A);
    V = mul(transpose(G), V);

    G = G3_23(A[1][1], A[2][1]);
    A = mul(transpose(G), A);
    U = mul(U, G);
}

void Bidiagonalize(inout float3x3 U, inout float3x3 A, inout float3x3 V)
{
    float3x3 G = G3_23(A[1][0], A[2][0]);
    A = mul(transpose(G), A);
    U = mul(U, G);
    //checked
    
    CodeZerochasing(U, A, V);
}

float FrobeniusNorm(float3x3 input)
{
    float ret = 0;
    for (int i = 0; i < 3; ++i)
    {
        for (int j = 0; j < 3; ++j)
        {
            ret += input[i][j] * input[i][j];
        }
    }

    return sqrt(ret);
}

void FlipSign(int index, inout float3x3 mat, inout float3 sigma)
{
    mat[0][index] = -mat[0][index];
    mat[1][index] = -mat[1][index];
    mat[2][index] = -mat[2][index];
    sigma[index] = -sigma[index];
}

void FlipSignColumn(inout float3x3 mat, int col)
{
    mat[0][col] = -mat[0][col];
    mat[1][col] = -mat[1][col];
    mat[2][col] = -mat[2][col];
}

inline void Swap(inout float a, inout float b)
{
    float temp = a;
    a = b;
    b = temp;
}


inline void Swap(inout float3 a, inout float3 b)
{
    float3 temp = a;
    a = b;
    b = temp;
}

inline void SwapColumn(inout float3x3 a, int col_a, int col_b)
{
    float3 temp = float3(a[0][col_a], a[1][col_a], a[2][col_a]);
    a[0][col_a] = a[0][col_b];
    a[1][col_a] = a[1][col_b];
    a[2][col_a] = a[2][col_b];

    a[0][col_b] = temp[0];
    a[1][col_b] = temp[1];
    a[2][col_b] = temp[2];
}

void SortWithTopLeftSub(inout float3x3 U, inout float3 sigma, inout float3x3 V)
{
    if (abs(sigma[1]) >= abs(sigma[2]))
    {
        if (sigma[1] < 0)
        {
            FlipSign(1, U, sigma);
            FlipSign(2, U, sigma);
        }
        return;
    }
    if (sigma[2] < 0)
    {
        FlipSign(1, U, sigma);
        FlipSign(2, U, sigma);
    }
    Swap(sigma[1], sigma[2]);
    SwapColumn(U, 1, 2);
    SwapColumn(V, 1, 2);

    if (sigma[1] > sigma[0])
    {
        Swap(sigma[0], sigma[1]);
        SwapColumn(U, 0, 1);
        SwapColumn(V, 0, 1);
    }
    else
    {
        FlipSignColumn(U, 2);
        FlipSignColumn(V, 2);
    }
}

void SortWithBotRightSub(inout float3x3 U, inout float3 sigma, inout float3x3 V)
{
    if (abs(sigma[0]) >= abs(sigma[1]))
    {
        if (sigma[0] < 0)
        {
            FlipSign(0, U, sigma);
            FlipSign(2, U, sigma);
        }
        return;
    }
    Swap(sigma[0], sigma[1]);
    SwapColumn(U, 0, 1);
    SwapColumn(V, 0, 1);

    if (abs(sigma[1]) < abs(sigma[2]))
    {
        Swap(sigma[1], sigma[2]);;
        SwapColumn(U, 1, 2);
        SwapColumn(V, 1, 2);
    }
    else
    {
        FlipSignColumn(U, 2);
        FlipSignColumn(V, 2);
    }

    if (sigma[1] < 0)
    {
        FlipSign(1, U, sigma);
        FlipSign(2, U, sigma);
    }
}

void SolveReducedTopLeft(inout float3x3 B, inout float3x3 U, inout float3 sigma, inout float3x3 V)
{
    float s3 = B[2][2];
    //float2x2 u = G2(1, 0);
    //float2x2 v = G2(1, 0);

    float2x2 top_left = float2x2(B[0][0], B[0][1], B[1][0], B[1][1]);
    
    float2x2 A2 = top_left;
    float2x2 U2 = float2x2(0, 0, 0, 0);
    float2 D2 = float2(0, 0);
    float2x2 V2 = float2x2(0, 0, 0, 0);
    GetSVD2D(A2, U2, D2, V2);

    float3x3 u3 = G3_12_Direct(U2[0][0], U2[0][1]);
    float3x3 v3 = G3_12_Direct(V2[0][0], V2[0][1]);
        
    U = mul(U, u3);
    V = mul(V, v3);

    sigma = float3(D2, s3);
}


void SolveReducedBotRight(inout float3x3 B, inout float3x3 U, inout float3 sigma, inout float3x3 V)
{
    float s1 = B[0][0];
    //float2x2 u = G2(1, 0);
    //float2x2 v = G2(1, 0);

    float2x2 bot_right = float2x2(B[1][1], B[1][2], B[2][1], B[2][2]);
    
    float2x2 A2 = bot_right;
    float2x2 U2 = float2x2(0, 0, 0, 0);
    float2 D2 = float2(0, 0);
    float2x2 V2 = float2x2(0, 0, 0, 0);
    GetSVD2D(A2, U2, D2, V2);

    float3x3 u3 = G3_23_Direct(U2[0][0], U2[0][1]);
    float3x3 v3 = G3_23_Direct(V2[0][0], V2[0][1]);
        
    U = mul(U, u3);
    V = mul(V, v3);
    sigma = float3(s1, D2);
}

void PostProcess(float3x3 B, inout float3x3 U, inout float3x3 V, float3 alpha, float2 beta, inout float3 sigma, float tao)
{
    if (abs(beta[1]) <= tao)
    {
        SolveReducedTopLeft(B, U, sigma, V);
        //checked
        SortWithTopLeftSub(U, sigma, V);
        //checked
    }
    else if (abs(beta[0]) <= tao)
    {
        SolveReducedBotRight(B, U, sigma, V);
        SortWithBotRightSub(U, sigma, V);
        //checked once
    }
    else if (abs(alpha[1]) <= tao)
    {
        //UnConventional G here
        float3x3 G = G3_23(B[1][2], B[2][2], false);
        B = mul(transpose(G), B);
        U = mul(U, G);

        SolveReducedTopLeft(B, U, sigma, V);
        SortWithTopLeftSub(U, sigma, V);
    }
    else if (abs(alpha[2]) <= tao)
    {
        float3x3 G = G3_23(B[1][1], B[1][2]);
        B = mul(B, G);
        V = mul(V, G);

        G = G3_13(B[0][0], B[0][2]);
        B = mul(B, G);
        V = mul(V, G);

        //checked
        SolveReducedTopLeft(B, U, sigma, V);
        //checked
        SortWithTopLeftSub(U, sigma, V);
        //checked
    }
    else if (abs(alpha[0]) <= tao)
    {
        //UnConventional G here
        float3x3 G = G3_12(B[0][1], B[1][1], false);
        B = mul(transpose(G), B);
        U = mul(U, G);

        //UnConventional G here
        G = G3_13(B[0][2], B[2][2], false);
        B = mul(transpose(G), B);
        U = mul(U, G);

        SolveReducedBotRight(B, U, sigma, V);
        SortWithBotRightSub(U, sigma, V);
    }
}


void GetSVD3D(in float3x3 A, out float3x3 U, out float3 D, out float3x3 V)
{
    U = float3x3(1, 0, 0,
                 0, 1, 0,
                 0, 0, 1);
    D = float3(0, 0, 0);
    V = float3x3(1, 0, 0,
                 0, 1, 0,
                 0, 0, 1);

    float3x3 B = A;

    Bidiagonalize(U, B, V);
    //chekced

    float3 alpha = float3(B[0][0], B[1][1], B[2][2]);
    float2 beta = float2(B[0][1], B[1][2]);
    float2 gamma = float2(alpha[0] * beta[0], alpha[1] * beta[1]);

    float tol = 128 * EPSILON;
    float tao = tol * max(0.5 * FrobeniusNorm(B), 1.0);
    
    int count = 0;

    [unroll(5)]
    while (count < 5 && abs(beta[1]) > tao && abs(beta[0]) > tao &&
        abs(alpha[0]) > tao && abs(alpha[1]) > tao && abs(alpha[2]) > tao)
    {
        float a1 = alpha[1] * alpha[1] + beta[0] * beta[0];
        float a2 = alpha[2] * alpha[2] + beta[1] * beta[1];
        float b1 = gamma[1];


        float d = (a1 - a2) * 0.5;
        float mu = (b1 * b1) / (abs(d) + sqrt(d * d + b1 * b1));
        //copy sign from d to mu
        float d_sign = sign(d);
        mu = d_sign > 0 ? mu : -mu;


        //code not in the paper
        mu = a2 - mu;
        //----------------

        float3x3 G = G3_12((alpha[0] * alpha[0]) - mu, gamma[0]);
        B = mul(B, G);
        V = mul(V, G);

        CodeZerochasing(U, B, V);

        alpha = float3(B[0][0], B[1][1], B[2][2]);
        beta = float2(B[0][1], B[1][2]);
        gamma = float2(alpha[0] * beta[0], alpha[1] * beta[1]);

        count++;

    }
    
    PostProcess(B, U, V, alpha, beta, D, tao);
}



inline float GetBSplineHelper(const float value)
{
    float val = abs(value);
    float ret = 0;
    if (val < 1)
    {
        ret = (0.5f * val * val * val) - (val * val) + (2 / 3.0f);
    }
    else if (val < 2)
    {
        ret = (-1 / 6.0f) * (val * val * val) + (val * val) - (2 * val) + (4 / 3.0f);
    }
    else
    {
        return 0;
    }

    //if (ret < 1e-4f) 
    //  return 0;

    return ret;
}

inline float GetBSplineDerivativeHelper(float value)
{
    float val = abs(value);
    if (val < 1)
    {
        return (1.5f * val * value) - 2 * value;
    }
    else if (value < 2)
    {
        return (-0.5f) * (val * value) + 2 * value - 2 * value / val;
    }
    else
    {
        return 0;
    }
}

float3x3 Math_OutProduct(float3 lhs, float3 rhs)
{
    return float3x3(lhs[0] * rhs[0], lhs[0] * rhs[1], lhs[0] * rhs[2],
                    lhs[1] * rhs[0], lhs[1] * rhs[1], lhs[1] * rhs[2],
                    lhs[2] * rhs[0], lhs[2] * rhs[1], lhs[2] * rhs[2]);
}


float3 Math_GetBSpline(float3 value)
{
    return float3(GetBSplineHelper(value.x), GetBSplineHelper(value.y), GetBSplineHelper(value.z));
}


float3 Math_GetBSplineDerivative(float3 value)
{
    return float3(GetBSplineDerivativeHelper(value.x), GetBSplineDerivativeHelper(value.y), GetBSplineDerivativeHelper(value.z));
}

#endif