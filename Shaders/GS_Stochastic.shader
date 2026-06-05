Shader "VRChatGaussianSplatting/GaussianSplattingStochastic"
{
    Properties
    {
        [HideInInspector] _GS_Positions ("Means", 2D) = "" {}
        [HideInInspector] _GS_Scales ("Scales", 2D) = "" {}
        [HideInInspector] _GS_Rotations ("Quats", 2D) = "" {}
        [HideInInspector] _GS_Colors ("Colors", 2D) = "" {}
        [HideInInspector] _GS_ColorsCamera ("Colors Camera", 2D) = "" {}
        [HideInInspector] _GS_SH ("SH", 2D) = "" {}
        [HideInInspector] _GS_SH_Min ("SH Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH_Range ("SH Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_RenderOrder ("Rendering Orders", 2DArray) = "" {}
        [HideInInspector] _GS_RenderOrderMirror ("Rendering Order Mirror", 2D) = "" {}
        [HideInInspector] _MirrorCameraPos ("Mirror Camera Position", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SplatCount ("Splat Count", Int) = 0
        [HideInInspector] _ActualSplatCount ("Actual Splat Count", Int) = 0
        [HideInInspector] _SplatOffset ("Splat Offset", Int) = 0
        [HideInInspector] _GS_CameraColorArray ("Colors From Camera Array", Float) = 0
        [HideInInspector] _GS_Positions_CoordMask ("Positions Coord Mask", Int) = 0
        [HideInInspector] _GS_Positions_CoordShift ("Positions Coord Shift", Int) = 0
        [HideInInspector] _GS_SH_CoeffCount ("SH Coeff Count", Int) = 0
        [HideInInspector] _GS_SH_CoeffStride ("SH Coeff Stride", Int) = 0
        [HideInInspector] _GS_SH_CoordMask ("SH Coord Mask", Int) = 0
        [HideInInspector] _GS_SH_CoordShift ("SH Coord Shift", Int) = 0
        [HideInInspector] [Toggle] _PRECOMPUTED_SORTING ("Precomputed Sorting", Integer) = 0
        [HideInInspector] _GS_RenderOrderPrecomputed ("Precomputed Render Order", 2DArray) = "" {}
        [HideInInspector] _GS_RenderOrderPrecomputed_CoordMask ("Precomputed Render Order Coord Mask", Int) = 0
        [HideInInspector] _GS_RenderOrderPrecomputed_CoordShift ("Precomputed Render Order Coord Shift", Int) = 0

        _GaussianMul ("Gaussian Scale", Range(0, 2)) = 1.0
        [Enum(SH0,0,SH1,1,SH2,2,SH3,3)] _SHBand ("SH Band", Float) = 3
        _ThinThreshold ("Thinness Threshold", Range(0, 1)) = 0.005
        _AntiAliasing ("Antialiasing", Range(0, 5.0)) = 1.0
        _Log2MinScale ("Log2 of Minimum Scale", Range(-20, 10)) = -15.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.03
        _ScaleCutoff ("Scale Cutoff", Range(0, 100)) = 100.0
        _Exposure ("Exposure", Range(0, 5)) = 1.0
        _Opacity ("Opacity", Range(0, 5)) = 1.0
        _OKLCHShift ("OKLCH Color Shift", Vector) = (0, 0, 0, 0)
        _Gamma ("Gamma", Float) = 1.0
        [Toggle] _VRC_LIGHT_VOLUMES ("Use VRC Light Volumes", Integer) = 0
        _LightVolumeIntensity ("Light Volume Intensity", Range(0, 10)) = 1.0
    }
    SubShader
    {
        Tags { "RenderQueue"="AlphaTest" "RenderType"="TransparentCutout" }
        Cull Off

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend Off
            ZWrite On
            CGPROGRAM
            #define _LEGACY_RANDOMIZED_ORDER
            #define _FAKE_SRGB
            #include "GS.cginc"
            ENDCG
        }
    }
}
