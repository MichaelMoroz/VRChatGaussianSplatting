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
        
        [SerializeField] public Material mainMaterial;

        [Header("Static Render Resources")]
        [SerializeField] public Texture2D positionData;
        [SerializeField] public Texture2D colorData;

        [Header("Dynamic Render Resources")]
        [SerializeField] public Material animator;
        [SerializeField] public RenderTexture positionBuffer0;
        [SerializeField] public RenderTexture positionBuffer1;
        [SerializeField] public RenderTexture colorBuffer0;
        [SerializeField] public RenderTexture colorBuffer1;

        [Header("Multiple Dynamic Splats")]
        [SerializeField] public GaussianSplatObject[] splatObjects;

        public MRTBlit blitter;
        private int _AnimationFrame = 0;

        void Update() {
            if(animator) {
                int sideLength = positionBuffer0.width;
                animator.SetInt("_ActualSplatCountSqrt", sideLength);
                animator.SetInt("_ActualSplatCount", sideLength * sideLength);
                animator.SetInt("_AnimationFrame", _AnimationFrame);
                _AnimationFrame++;
            }
        
            if(renderMode == RenderMode.ProceduralSplat) {
                RenderTexture[] outputs = new RenderTexture[2] { positionBuffer0, colorBuffer0 };
                blitter.Blit(animator, outputs);
            }
            if(renderMode == RenderMode.ProceduralStatefulSplat) {
                RenderTexture[] outputs = new RenderTexture[2] { positionBuffer0, colorBuffer0 };
                animator.SetTexture("_GS_PackedPositions", positionBuffer1);
                animator.SetTexture("_GS_PackedColors", colorBuffer1);
                blitter.Blit(animator, outputs);
                // swap buffers
                var temp = positionBuffer0;
                positionBuffer0 = positionBuffer1;
                positionBuffer1 = temp;
                temp = colorBuffer0;
                colorBuffer0 = colorBuffer1;
                colorBuffer1 = temp;
                mainMaterial.SetTexture("_GS_PackedPositions", positionBuffer0);
                mainMaterial.SetTexture("_GS_PackedColors", colorBuffer0);
            }
        }

        void Start() {
            if(renderMode == RenderMode.ProceduralSplat || renderMode == RenderMode.ProceduralStatefulSplat) {
                if(!animator || !positionBuffer0 || !colorBuffer0 || !blitter) {
                    Debug.LogError("GaussianSplatObject: Missing resources for ProceduralSplat mode.");
                    return;
                }
            }
        }

        public Texture[] GetSplatData() {
            if(renderMode == RenderMode.ProceduralStatefulSplat || renderMode == RenderMode.ProceduralSplat) {
                return new Texture[2] { positionBuffer0, colorBuffer0 };
            } else {
                return new Texture[2] { positionData, colorData };
            }
        }
    }
    
}
