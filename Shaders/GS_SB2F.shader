Shader "VRChatGaussianSplatting/GaussianSplattingSimpleBackToFront"
{
    Properties
    {
        [HideInInspector] _GS_Positions ("Means", 2D) = "" {}
        [HideInInspector] _GS_Scales ("Scales", 2D) = "" {}
        [HideInInspector] _GS_Rotations ("Quats", 2D) = "" {}
        [HideInInspector] _GS_Colors ("Colors", 2D) = "" {}
        [HideInInspector] _GS_SH1 ("SH1", 2D) = "" {}
        [HideInInspector] _GS_SH2 ("SH2", 2D) = "" {}
        [HideInInspector] _GS_SH3 ("SH3", 2D) = "" {}
        [HideInInspector] _GS_SH4 ("SH4", 2D) = "" {}
        [HideInInspector] _GS_SH5 ("SH5", 2D) = "" {}
        [HideInInspector] _GS_SH6 ("SH6", 2D) = "" {}
        [HideInInspector] _GS_SH7 ("SH7", 2D) = "" {}
        [HideInInspector] _GS_SH8 ("SH8", 2D) = "" {}
        [HideInInspector] _GS_SH9 ("SH9", 2D) = "" {}
        [HideInInspector] _GS_SHA ("SHA", 2D) = "" {}
        [HideInInspector] _GS_SHB ("SHB", 2D) = "" {}
        [HideInInspector] _GS_SHC ("SHC", 2D) = "" {}
        [HideInInspector] _GS_SHD ("SHD", 2D) = "" {}
        [HideInInspector] _GS_SHE ("SHE", 2D) = "" {}
        [HideInInspector] _GS_SHF ("SHF", 2D) = "" {}
        [HideInInspector] _GS_SH1_Min ("SH1 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH2_Min ("SH2 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH3_Min ("SH3 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH4_Min ("SH4 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH5_Min ("SH5 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH6_Min ("SH6 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH7_Min ("SH7 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH8_Min ("SH8 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH9_Min ("SH9 Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SHA_Min ("SHA Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SHB_Min ("SHB Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SHC_Min ("SHC Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SHD_Min ("SHD Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SHE_Min ("SHE Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SHF_Min ("SHF Min", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GS_SH1_Range ("SH1 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH2_Range ("SH2 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH3_Range ("SH3 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH4_Range ("SH4 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH5_Range ("SH5 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH6_Range ("SH6 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH7_Range ("SH7 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH8_Range ("SH8 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SH9_Range ("SH9 Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SHA_Range ("SHA Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SHB_Range ("SHB Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SHC_Range ("SHC Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SHD_Range ("SHD Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SHE_Range ("SHE Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_SHF_Range ("SHF Range", Vector) = (1, 1, 1, 0)
        [HideInInspector] _GS_RenderOrder ("Rendering Orders", 2DArray) = "" {}
        [HideInInspector] _GS_RenderOrderMirror ("Rendering Order Mirror", 2D) = "" {}
        [HideInInspector] _MirrorCameraPos ("Mirror Camera Position", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SplatCount ("Splat Count", Int) = 0
        [HideInInspector] _ActualSplatCount ("Actual Splat Count", Int) = 0
        [HideInInspector] _SplatOffset ("Splat Offset", Int) = 0
        [HideInInspector] [Toggle] _PRECOMPUTED_SORTING ("Precomputed Sorting", Integer) = 0
        [HideInInspector] _GS_RenderOrderPrecomputed ("Precomputed Render Order", 2DArray) = "" {}

        _GaussianMul ("Gaussian Scale", Range(0, 2)) = 1.0
        [Enum(SH0,0,SH1,1,SH2,2,SH3,3)] _SHBand ("SH Band", Float) = 3
        _ThinThreshold ("Thinness Threshold", Range(0, 1)) = 0.005
        _AntiAliasing ("Antialiasing", Range(0, 5.0)) = 1.0
        _Log2MinScale ("Log2 of Minimum Scale", Range(-20, 10)) = -15.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.03
        _ScaleCutoff ("Scale Cutoff", Range(0, 100)) = 100.0
        _Exposure ("Exposure", Range(0, 5)) = 1.0
        _Opacity ("Opacity", Range(0, 5)) = 1.0
        _OKLCHShift ("OKLCH Color Shift", Vector) = (0, 0, 0, 0) // Shift for OKLCH color space
        _Gamma ("Gamma", Float) = 1.0 
        [Toggle] _VRC_LIGHT_VOLUMES ("Use VRC Light Volumes", Integer) = 0
        _LightVolumeIntensity ("Light Volume Intensity", Range(0, 10)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+500" }

        Pass
        {
            Blend One OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            CGPROGRAM
            #define _BACK_TO_FRONT
            #define _FAKE_SRGB
        	#include "GS.cginc"
            ENDCG
        }
    }
}
