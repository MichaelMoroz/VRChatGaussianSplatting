using UnityEngine;
using System.Collections.Generic;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon;


namespace GaussianSplatting
{
    public enum RenderMode
    {
        SingleStaticSplat,
        MultipleDynamicSplats,
        ProceduralSplat,
        ProceduralStatefulSplat
    }

    [RequireComponent(typeof(MeshRenderer))]
    public class GaussianSplatObject : UdonSharpBehaviour
    {
        [Header("Render Mode")]
        [Tooltip("The render mode for the Gaussian Splat renderer. Static mode uses a single splat object, MultiSplat uses multiple splat objects, and Procedural generates splats procedurally.")]
        [SerializeField] public RenderMode renderMode = RenderMode.SingleStaticSplat;

        [Header("Static Render Resources")]
        [SerializeField] public Texture2D positionData;
        [SerializeField] public Texture2D colorData;

        [Header("Dynamic Render Resources")]
        [SerializeField] public Material positionAnimator;
        [SerializeField] public Material colorAnimator;
        [SerializeField] public RenderTexture positionBuffer0;
        [SerializeField] public RenderTexture positionBuffer1;
        [SerializeField] public RenderTexture colorBuffer0;
        [SerializeField] public RenderTexture colorBuffer1;

        [Header("Multiple Dynamic Splats")]
        [SerializeField] public GameObject[] splatObjects;
        
        void Update() {
            if(renderMode == RenderMode.ProceduralSplat) {
                int sideLength = positionBuffer0.width;
                positionAnimator.SetInt("_ActualSplatCountSqrt", sideLength);
                positionAnimator.SetInt("_ActualSplatCount", sideLength * sideLength);
                colorAnimator.SetInt("_ActualSplatCountSqrt", sideLength);
                colorAnimator.SetInt("_ActualSplatCount", sideLength * sideLength);

                VRCGraphics.Blit(null, positionBuffer0, positionAnimator);
                VRCGraphics.Blit(null, colorBuffer0, colorAnimator);
            }
        }
    }
    
}
