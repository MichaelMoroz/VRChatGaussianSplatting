Shader "VRChatGaussianSplatting/GaussianSplatting"
{
    Properties
    {
        [HideInInspector] _GS_Positions ("Means", 2D) = "" {}
        [HideInInspector] _GS_Scales ("Scales", 2D) = "" {}
        [HideInInspector] _GS_Rotations ("Quats", 2D) = "" {}
        [HideInInspector] _GS_Colors ("Colors", 2D) = "" {}
        [HideInInspector] _GS_RenderOrder ("Rendering Orders", 2DArray) = "" {}
        [HideInInspector] _GS_RenderOrderMirror ("Rendering Order Mirror", 2D) = "" {}
        [HideInInspector] _MirrorCameraPos ("Mirror Camera Position", Vector) = (0, 0, 0, 0)
        [HideInInspector] _HACK_UNIFORM ("hack", Float) = 1.0 // HACK to avoid compiler optimizing out double precision
        [HideInInspector] _MinMaxSortDistance ("Min Max Distance", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SplatCount ("Splat Count", Int) = 0
        [HideInInspector] _SplatOffset ("Splat Offset", Int) = 0
        [Toggle] _PRECOMPUTED_SORTING ("Precomputed Sorting", Integer) = 0
        [HideInInspector] _GS_RenderOrderPrecomputed ("Precomputed Render Order", 2DArray) = "" {}

        _QuadScale ("Quad Scale", Range(0, 2)) = 1.1
        _GaussianMul ("Gaussian Scale", Range(0, 2)) = 1.0
        _ThinThreshold ("Thinness Threshold", Range(0, 1)) = 0.005
        _AntiAliasing ("Antialiasing", Range(0, 5.0)) = 1.0
        _Log2MinScale ("Log2 of Minimum Scale", Range(-20, 10)) = -12.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.03
        _ScaleCutoff ("Scale Cutoff", Range(0, 100)) = 100.0
        _Exposure ("Exposure", Range(0, 5)) = 1.0
        _Opacity ("Opacity", Range(0, 5)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+500" }

        Pass
        {
            //Blend One OneMinusSrcAlpha //Back to front blending
            Blend OneMinusDstAlpha One //Front to back blending
            Cull Off
            ZWrite Off

            Stencil {
                Ref 1
                Comp NotEqual  // skip pixels where first pass wrote 1
            }
            CGPROGRAM
        	#include "GS.cginc"
            ENDCG
        }
    }
}