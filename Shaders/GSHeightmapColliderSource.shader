// Collider depth pass that rasterizes the ORIGINAL packed source splats directly (no fused set, no GPU
// radix sort). Draw rank -> source texel index comes from a CPU-sorted uint order texture; the packed
// position is decoded with per-chunk bounds located by an in-shader binary search over the chunk table.
Shader "Hidden/GaussianSplatting/HeightmapColliderDepthSource"
{
    Properties
    {
        [HideInInspector] _GS_Positions ("Means", 2D) = "" {}
        [HideInInspector] _GS_Scales ("Scales", 2D) = "" {}
        [HideInInspector] _GS_Rotations ("Quats", 2D) = "" {}
        [HideInInspector] _GS_Colors ("Colors", 2D) = "" {}
        [HideInInspector] _GS_ColorsCamera ("Colors Camera", 2D) = "" {}
        [HideInInspector] _GS_SH ("SH", 2D) = "" {}
        [HideInInspector] _GS_ChunkBounds ("Chunk Bounds", 2D) = "" {}
        [HideInInspector] _GS_ChunkSize ("Chunk Size", Int) = 0
        [HideInInspector] _SplatCount ("Splat Count", Int) = 0
        [HideInInspector] _ActualSplatCount ("Actual Splat Count", Int) = 0
        [HideInInspector] _SplatOffset ("Splat Offset", Int) = 0
        [HideInInspector] _GS_CameraColorArray ("Colors From Camera Array", Float) = 0
        [HideInInspector] _GS_Positions_CoordMask ("Positions Coord Mask", Int) = 0
        [HideInInspector] _GS_Positions_CoordShift ("Positions Coord Shift", Int) = 0
        [HideInInspector] _GS_SH_CoeffCount ("SH Coeff Count", Int) = 0

        _GaussianMul ("Gaussian Scale", Range(0, 2)) = 1.0
        _ThinThreshold ("Thinness Threshold", Range(0, 1)) = 0.005
        _AntiAliasing ("Antialiasing", Range(0, 5.0)) = 1.0
        _Log2MinScale ("Log2 of Minimum Scale", Range(-20, 10)) = -15.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.04
        _AlphaCull ("Alpha Cull", Range(0, 1)) = 0.04
        _LODCull ("LOD Cull", Range(0, 0.1)) = 0.0
        _ScaleCutoff ("Scale Cutoff", Range(0, 100)) = 100.0
        _Opacity ("Opacity", Range(0, 5)) = 1.0
        [HideInInspector] _GS_ColliderBoxHeight ("Collider Box Height", Float) = 1.0
        [HideInInspector] _GS_ColliderScreenParams ("Collider Screen Params", Vector) = (1, 1, 2, 2)
        [HideInInspector] _GS_ColliderOpacityLogMultiplier ("Collider Opacity Log Multiplier", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" }

        Pass
        {
            Blend OneMinusDstAlpha One
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #define GS_COLLIDER_DEPTH_WEIGHT
            #define GS_COLLIDER_SOURCE_LOAD
            #include "GS.cginc"
            ENDCG
        }
    }
}
