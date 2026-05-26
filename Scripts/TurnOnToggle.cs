
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
    [Tooltip("The Gaussian Splat Object that will be enabled when this toggle is activated.")]
    public GameObject targetObject;
    [Tooltip("The automatically discovered Gaussian Splat Object index that will be enabled when this toggle is activated.")]
    public int enableObjectIndex = 0;

    GameObject GetTargetObject()
    {
        if (targetObject != null)
        {
            return targetObject;
        }

#if COMPILER_UDONSHARP
        return null;
#else
        GaussianSplatObject[] sceneSplatObjects = Object.FindObjectsOfType<GaussianSplatObject>(true);
        if (enableObjectIndex < 0 || enableObjectIndex >= sceneSplatObjects.Length || sceneSplatObjects[enableObjectIndex] == null)
        {
            return null;
        }

        return sceneSplatObjects[enableObjectIndex].gameObject;
#endif
    }

    void SelectOnlyTargetObject(GameObject selectedObject)
    {
        if (selectedObject == null)
        {
            return;
        }

#if !COMPILER_UDONSHARP
        GaussianSplatObject[] sceneSplatObjects = Object.FindObjectsOfType<GaussianSplatObject>(true);
        for (int i = 0; i < sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject splatObject = sceneSplatObjects[i];
            if (splatObject != null)
            {
                splatObject.gameObject.SetActive(false);
            }
        }

        selectedObject.SetActive(true);
        GaussianSplatObject selectedSplatObject = selectedObject.GetComponent<GaussianSplatObject>();
        if (selectedSplatObject != null)
        {
            selectedSplatObject.NotifyRendererEnabled();
        }
#else
        selectedObject.SetActive(true);
        GaussianSplatObject selectedSplatObject = selectedObject.GetComponent<GaussianSplatObject>();
        if (selectedSplatObject != null)
        {
            selectedSplatObject.NotifyRendererEnabled();
        }
#endif
    }

    public void Start()
    {
        GameObject targetObject = GetTargetObject();
        if (targetObject != null)
        {
            this.InteractionText = targetObject.name;
        }
    }

    public void SelectObject()
    {
        if (Networking.LocalPlayer != null)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        GameObject targetObject = GetTargetObject();
        SelectOnlyTargetObject(targetObject);
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
