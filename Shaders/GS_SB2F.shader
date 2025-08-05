Shader "VRChatGaussianSplatting/GaussianSplattingSimpleBackToFront"
{
    Properties
    {
        _GS_PackedPositions ("Packed Positions", 2D) = "" {}
        _GS_PackedColors ("Packed Colors", 2D) = "" {}
        [HideInInspector] _GS_RenderOrder ("Rendering Orders", 2DArray) = "" {}
        [HideInInspector] _GS_RenderOrderMirror ("Rendering Order Mirror", 2D) = "" {}
        [HideInInspector] _MirrorCameraPos ("Mirror Camera Position", Vector) = (0, 0, 0, 0)
        [HideInInspector] _HACK_UNIFORM ("hack (must be 1.0)", Float) = 1.0 // HACK to avoid compiler optimizing out double precision
        [HideInInspector] _MinMaxSortDistance ("Min Max Distance", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SplatCount ("Splat Count", Int) = 0
        [HideInInspector] _ActualSplatCount ("Actual Splat Count", Int) = 0
        [HideInInspector] _ActualSplatCountSqrt ("Actual Splat Count Sqrt", Int) = 0
        [HideInInspector] _SplatOffset ("Splat Offset", Int) = 0
        [HideInInspector] [Toggle] _PRECOMPUTED_SORTING ("Precomputed Sorting", Integer) = 0
        [HideInInspector] _GS_RenderOrderPrecomputed ("Precomputed Render Order", 2DArray) = "" {}

        _QuadScale ("Quad Scale", Range(0, 2)) = 1.1
        _GaussianMul ("Gaussian Scale", Range(0, 2)) = 1.0
        _ThinThreshold ("Thinness Threshold", Range(0, 1)) = 0.005
        _AntiAliasing ("Antialiasing", Range(0, 5.0)) = 1.0
        _Log2MinScale ("Log2 of Minimum Scale", Range(-20, 10)) = -15.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.03
        _ScaleCutoff ("Scale Cutoff", Range(0, 100)) = 100.0
        _Exposure ("Exposure", Range(0, 5)) = 1.0
        _Opacity ("Opacity", Range(0, 5)) = 1.0
        _OKLCHShift ("OKLCH Color Shift", Vector) = (0, 0, 0, 0) // Shift for OKLCH color space
        _Gamma ("Gamma", Float) = 1.0 
        [Toggle] _FAKE_SRGB ("Fake sRGB", Integer) = 0
        [Toggle] _VRC_LIGHT_VOLUMES ("VRC Light Volumes support", Integer) = 0
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