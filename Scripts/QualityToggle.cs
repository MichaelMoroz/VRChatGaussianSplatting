
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class QualityToggle : UdonSharpBehaviour 
{   
    [Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] public float alphaCutoff = 0.03f;
    [Tooltip("The Gaussian Splat Renderer that will use the enabled object as the splat object.")]
    public GaussianSplatRenderer gaussianSplatRenderer;

    public override void Interact()
    {
        if (gaussianSplatRenderer == null)
        {
#if !COMPILER_UDONSHARP
            gaussianSplatRenderer = Object.FindObjectOfType<GaussianSplatRenderer>();
#endif
        }

        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.overrideMaterialProperties = true;
        gaussianSplatRenderer.gaussianScale = gaussianScale;
        gaussianSplatRenderer.alphaCutoff = alphaCutoff;
    }
}

}
