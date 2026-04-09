using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
using System.Collections.Generic;
#endif

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 3; // Screen camera + Photo camera + Mirror camera
    private Vector3[] _prevCameraPos;
    private RadixSort _radixSort;
    private MeshRenderer _sortedRenderer;
    private Material keyValueMat;
    private GameObject splatObject;

    [Header("Gaussian Splat Object")]
    [UdonSynced, Tooltip("The index of the currently rendered splat object in the splatObjects array.")]
    public int splatObjectIndex = 0; // Index of the current splat object in the splatObjects array
    [Tooltip("The GameObject that contains the Gaussian Splat root. Non-presorted imports should contain sorted and stochastic children.")]
    public GameObject[] splatObjects;

    [Header("Render Settings")]
    [Tooltip("Minimum distance for sorting splats. Splat positions closer than this will not be sorted. The smaller the minmax range the more accurate the sorting")]
    [SerializeField] float minSortDistance = 0.0f;
    [Tooltip("Maximum distance for sorting splats. Splat positions further than this will not be sorted. The smaller the minmax range the more accurate the sorting")]
    [SerializeField] float maxSortDistance = 150.0f;
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    [Tooltip("If true, the splat render order will be updated every frame. Useful for animated splats. If false, it will only update when the camera position changes.")]
    [SerializeField] bool alwaysUpdate = false;
    [Tooltip("Number of sorting steps for the radix sort. The more steps the more bits of the distance can be sorted, so the render order is more accurate. The fewer steps the faster the sorting, so it is a tradeoff between performance and accuracy. Default is 16 bits, which is 4 sorting steps.")]
    [Range(2, 8)] [SerializeField] int sortingSteps = 4;
    [Tooltip("Render texture used to store the sorted splat render order. This should be a RenderTexture with the same dimensions as the sorting textures used in the radix sort.")]
    public RenderTexture splatRenderOrder;

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [SerializeField] public bool overrideMaterialProperties = false;
    [Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] public float alphaCutoff = 0.03f;

    // [Header("Optional Mirror")]
    // [Tooltip("Optional mirror GameObject. If set, the script will also sort splats for the mirror camera position.")]
    // public GameObject mirror;

    void ResetCameraPositions()
    {
        for (int i = 0; i < MAX_CAMERA_COUNT; i++)
        {
            _prevCameraPos[i] = Vector3.positiveInfinity; // Reset to a value that will always trigger an update
        }
    }

    void ShowStochastic(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            gaussianSplatObject.ShowStochastic();
        }
    }

    void ShowSorted(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            gaussianSplatObject.ShowSorted();
        }
    }

    void DeactivateSplatObjects()
    {
        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject splatObj = splatObjects[i];
            if (splatObj != null)
            {
                ShowStochastic(splatObj);
                splatObj.SetActive(false);
            }
        }
    }

    MeshRenderer GetSortedRenderer(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return null;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            MeshRenderer renderer = gaussianSplatObject.GetSortedRenderer();
            if (renderer != null)
            {
                return renderer;
            }
        }

        return (MeshRenderer)rootObject.GetComponent(typeof(MeshRenderer));
    }

    public void SetSplatObjectIndex(int index)
    {
        if (splatObjectIndex == index && splatObject != null)
        {
            return;
        }
        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return;
        }

        ResetCameraPositions();
        DeactivateSplatObjects();

        splatObjectIndex = index;
        splatObject = splatObjects[splatObjectIndex];
        if (splatObject == null)
        {
            Debug.LogError($"Splat object at index {splatObjectIndex} is null. Please ensure the splatObjects array is populated correctly.");
            return;
        }
        splatObject.SetActive(true);
        ShowStochastic(splatObject);
    }

    public GameObject GetObjectByIndex(int index)
    {
        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return null;
        }
        return splatObjects[index];
    }

    void Start()
    {
        VRCCameraSettings.ScreenCamera.AllowMSAA = false; // MSAA is too slow for Gaussian Splatting, disable it
        _radixSort = (RadixSort)GetComponent<RadixSort>();
        if (_radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return;
        }
        if (splatRenderOrder == null)
        {
            Debug.LogError("Splat Render Order texture is not assigned. Please assign a RenderTexture.");
            return;
        }

        _prevCameraPos = new Vector3[MAX_CAMERA_COUNT];
        ResetCameraPositions();
    }

    Vector3 QuantizePosition(Vector3 position)
    {
        if (cameraPositionQuantization <= 0)
            return position;

        return new Vector3(
            Mathf.Round(position.x / cameraPositionQuantization) * cameraPositionQuantization,
            Mathf.Round(position.y / cameraPositionQuantization) * cameraPositionQuantization,
            Mathf.Round(position.z / cameraPositionQuantization) * cameraPositionQuantization
        );
    }

    bool UpdateMaterials()
    {
        SetSplatObjectIndex(splatObjectIndex);

        if (splatObject == null)
        {
            return false;
        }

        _sortedRenderer = GetSortedRenderer(splatObject);
        if (_sortedRenderer == null)
        {
            Debug.LogError($"No sorted MeshRenderer found on {splatObject.name}.");
            return false;
        }

        Material[] splatMats = _sortedRenderer.materials;
        Vector4 minMaxSortDistance = new Vector4(minSortDistance, maxSortDistance, 0, 0);
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
            splatMat.SetVector("_MinMaxSortDistance", minMaxSortDistance);
            if (overrideMaterialProperties)
            {
                splatMat.SetFloat("_GaussianMul", gaussianScale);
                splatMat.SetFloat("_AlphaCutoff", alphaCutoff);
            }
        }

        Texture positions = null;
        if (splatMats.Length > 1)
        {
            positions = splatMats[1].GetTexture("_GS_Positions");
        }
        else
        {
            positions = splatMats[0].GetTexture("_GS_Positions");
        }

        _radixSort.elementCount = positions.width * positions.height;
        _radixSort.maxKeyBits = sortingSteps * 4; // Each sorting step sorts 4 bits, so total bits = steps * 4
        keyValueMat = _radixSort.computeKeyValues;
        keyValueMat.SetTexture("_GS_Positions", positions);
        keyValueMat.SetVector("_MinMaxSortDistance", minMaxSortDistance);
        keyValueMat.SetFloat("_KeyScale", (float)((1 << (sortingSteps * 4)) - 1));
        keyValueMat.SetMatrix("_SplatToWorld", _sortedRenderer.transform.localToWorldMatrix);
        return true;
    }

    bool SortCamera(Vector3 cameraPos, int cameraID, bool forceUpdate = false)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        if (quantizedPos == _prevCameraPos[cameraID] && !alwaysUpdate && !forceUpdate)
        {
            return false;
        }

        _prevCameraPos[cameraID] = quantizedPos;
        keyValueMat.SetVector("_CameraPos", cameraPos);
        _radixSort.Sort();
        VRCGraphics.Blit(_radixSort.keyValues0, splatRenderOrder, 0, cameraID);
        return true;
    }

    public void SortCameras(Vector3 screenCamPos)
    {
        if (!UpdateMaterials())
        {
            return;
        }

        if (SortCamera(screenCamPos, 0))
        {
            ShowSorted(splatObject);
        }

        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        if (photoCam != null && photoCam.Active) SortCamera(photoCam.Position, 1);

        // if (mirror != null && mirror.activeInHierarchy) //Mirror order is currently broken in VRChat
        // {
        //     Vector3 mirrorZ = mirror.transform.forward;
        //     float zDist = Vector3.Dot(mirrorZ, mirror.transform.position - screenCamPos);
        //     if (zDist > 0)
        //     {
        //         Vector3 mirrorCamPos = screenCamPos + 2 * zDist * mirrorZ;
        //         _sortedRenderer.material.SetVector("_MirrorCameraPos", mirrorCamPos);
        //         SortCamera(mirrorCamPos, 2, true);
        //     }
        // }
    }

    void Update()
    {
        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        SortCameras(screenCamPos);
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    // Generate turn on toggles for all splat objects in the inspector
    [ContextMenu("Generate Gaussian Splat Toggles for the renderer")]
    void GenerateSplatToggles()
    {
        GameObject toggleParent = new GameObject("Splat Toggles");
        int toggleIndex = 0;

        var renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.InstanceID);

        if(renderers.Length > 1)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Please ensure only one instance is present in the scene.");
            return;
        }

        foreach (var renderer in renderers)
        {
            if (renderer.splatObjects == null || renderer.splatObjects.Length == 0)
            {
                Debug.LogWarning($"GaussianSplatRenderer on {renderer.gameObject.name} has no splat objects assigned.");
                continue;
            }

            for (int i = 0; i < renderer.splatObjects.Length; i++)
            {
                GameObject splatObj = renderer.splatObjects[i];
                if (splatObj != null)
                {
                    GameObject toggleObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    toggleObject.name = splatObj.name + " Toggle";
                    toggleObject.transform.SetParent(toggleParent.transform);
                    toggleObject.transform.localPosition = new Vector3(toggleIndex * 0.2f, 0.5f, 0); // Arrange toggles horizontally
                    toggleObject.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f); // Adjust size for visibility

                    TurnOnToggle toggle = toggleObject.AddComponent<TurnOnToggle>();
                    toggle.enableObjectIndex = i;
                    toggle.gaussianSplatRenderer = renderer;

                    Renderer toggleRenderer = toggleObject.GetComponent<Renderer>();
                    toggleRenderer.sharedMaterial = new Material(Shader.Find("Standard"));
                    toggleRenderer.sharedMaterial.color = Color.gray;

                    Collider collider = toggleObject.GetComponent<Collider>();
                    collider.isTrigger = true; // Make the toggle a trigger to allow interaction

                    UnityEditorInternal.ComponentUtility.MoveComponentUp(toggle);
                    toggleIndex++;
                }
            }
        }
    }

    List<GaussianSplatObject> GetAllObjectsOnlyInScene()
    {
        List<GaussianSplatObject> objectsInScene = new List<GaussianSplatObject>();

        foreach (GaussianSplatObject go in Resources.FindObjectsOfTypeAll(typeof(GaussianSplatObject)) as GaussianSplatObject[])
        {
            if (!EditorUtility.IsPersistent(go.transform.root.gameObject) && !(go.hideFlags == HideFlags.NotEditable || go.hideFlags == HideFlags.HideAndDontSave))
                objectsInScene.Add(go);
        }

        return objectsInScene;
    }

    [ContextMenu("Collect Gaussian Splat Objects for the renderer")]
    void CollectSplatObjects()
    {
        GaussianSplatRenderer[] renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.InstanceID);

        if (renderers.Length > 1)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Please ensure only one instance is present in the scene.");
            return;
        }

        foreach (var renderer in renderers)
        {
            List<GaussianSplatObject> objectsInScene = GetAllObjectsOnlyInScene();
            renderer.splatObjects = new GameObject[objectsInScene.Count];

            for (int i = 0; i < objectsInScene.Count; i++)
            {
                GaussianSplatObject go = objectsInScene[i];
                if (go != null)
                {
                    renderer.splatObjects[i] = go.gameObject;
                }
                else
                {
                    Debug.LogWarning($"Gaussian Splat Object at index {i} is null. Please ensure all objects are valid.");
                }
            }

            Debug.Log($"Collected {renderer.splatObjects.Length} Gaussian Splat Objects for the renderer.");
        }
    }
#endif
}

}
