
using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.Udon;
using VRC.SDK3.Components;

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TurnOnToggle : UdonSharpBehaviour 
{   
    [Tooltip("The GameObject that will be enabled when this toggle is activated.")]
    public int enableObjectIndex = 0; // Index of the object to enable in the GaussianSplatRenderer's splatObjects array
    [Tooltip("The Gaussian Splat Renderer that will use the enabled object as the splat object.")]
    public GaussianSplatRenderer gaussianSplatRenderer;

    public void Start()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        GameObject targetObject = gaussianSplatRenderer.GetObjectByIndex(enableObjectIndex);
        if (targetObject != null)
        {
            this.InteractionText = targetObject.name;
        }
    }

    public void SelectObject()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        if (Networking.LocalPlayer != null)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            Networking.SetOwner(Networking.LocalPlayer, gaussianSplatRenderer.gameObject);
        }

        gaussianSplatRenderer.SetSplatObjectIndex(enableObjectIndex);
    }

    public override void Interact()
    {
        SelectObject();
    }

    public void OnTrigger()
    {
        SelectObject();
    }
}

}
