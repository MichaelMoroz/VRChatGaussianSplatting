using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon;
using UnityEngine.UI;
using UnityEngine.EventSystems;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.Rendering;
using UdonSharpEditor;
#endif

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 2; // Screen camera + Photo camera
    const int SCREEN_CAMERA_ID = 0;
    const int PHOTO_CAMERA_ID = 1;
    const int NO_ACTIVE_SORT = -1;
    const float DEFAULT_ALPHA_CUTOFF = 0.03f;

    private Vector3[] _completedCameraPos;
    private Vector3[] _pendingCameraPos;
    private Vector3[] _pendingCameraWorldPos;
    private bool[] _hasCompletedSort;
    private bool[] _hasPendingSort;
    private int _activeSortCameraId = NO_ACTIVE_SORT;
    private Vector3 _activeSortQuantizedPos = Vector3.positiveInfinity;
    private RadixSort _radixSort;
    private MeshRenderer _sortedRenderer;
    private Material keyValueMat;
    private GameObject splatObject;

    [Header("Render Settings")]
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    [Tooltip("If true, the splat render order will be updated every frame. Useful for animated splats. If false, it will only update when the camera position changes.")]
    [SerializeField] bool alwaysUpdate = false;
    [Tooltip("Number of frames used to pipeline the 8 radix sort subpasses. 1 sorts fully in one frame; 8 runs one subpass per frame.")]
    [Range(1, 8)] [SerializeField] int sortPipelineFrames = 2;
    [Tooltip("Render texture array used to store sorted splat render order. Slice 0 is screen, slice 1 is photo.")]
    public RenderTexture splatRenderOrder;

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [UdonSynced, SerializeField] public bool overrideMaterialProperties = false;
    [UdonSynced, Range(0, 3)] [SerializeField] int requestedSHBand = 3;
    [UdonSynced, Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] float thinThreshold = 0.005f;
    [Range(0.0f, 3.0f)] [SerializeField] float antiAliasing = 1.0f;
    [Range(-20.0f, 10.0f)] [SerializeField] float log2MinScale = -15.0f;
    [Range(0.005f, 0.1f)] [SerializeField] public float alphaCutoff = DEFAULT_ALPHA_CUTOFF;
    [Range(0.0f, 100.0f)] [SerializeField] float scaleCutoff = 100.0f;
    [Range(0.0f, 5.0f)] [SerializeField] float exposure = 1.0f;
    [Range(0.0f, 5.0f)] [SerializeField] float opacity = 1.0f;
    [SerializeField] Vector3 oklchShift = Vector3.zero;
    [SerializeField] float gamma = 1.0f;
    [UdonSynced, SerializeField] bool useVrcLightVolumes = false;
    [Range(0.0f, 4.0f)] [SerializeField] float lightVolumeIntensity = 1.0f;

    // [Header("Optional Mirror")]
    // [Tooltip("Optional mirror GameObject. If set, the script will also sort splats for the mirror camera position.")]
    // public GameObject mirror;

    void ResetCameraPositions()
    {
        if (_completedCameraPos == null || _completedCameraPos.Length < MAX_CAMERA_COUNT
            || _pendingCameraPos == null || _pendingCameraPos.Length < MAX_CAMERA_COUNT
            || _pendingCameraWorldPos == null || _pendingCameraWorldPos.Length < MAX_CAMERA_COUNT
            || _hasCompletedSort == null || _hasCompletedSort.Length < MAX_CAMERA_COUNT
            || _hasPendingSort == null || _hasPendingSort.Length < MAX_CAMERA_COUNT)
        {
            return;
        }

        for (int i = 0; i < MAX_CAMERA_COUNT; i++)
        {
            _completedCameraPos[i] = Vector3.positiveInfinity;
            _pendingCameraPos[i] = Vector3.positiveInfinity;
            _pendingCameraWorldPos[i] = Vector3.positiveInfinity;
            _hasCompletedSort[i] = false;
            _hasPendingSort[i] = false;
        }

        _activeSortCameraId = NO_ACTIVE_SORT;
        _activeSortQuantizedPos = Vector3.positiveInfinity;
        if (_radixSort != null)
        {
            _radixSort.CancelSort();
        }
    }

    GameObject FindNamedChild(GameObject rootObject, string childName)
    {
        if (rootObject == null)
        {
            return null;
        }

        Transform child = rootObject.transform.Find(childName);
        if (child != null)
        {
            return child.gameObject;
        }

        return null;
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
            return;
        }

        GameObject sortedObject = FindNamedChild(rootObject, "Sorted");
        if (sortedObject != null)
        {
            sortedObject.SetActive(true);
        }

        Transform rootTransform = rootObject.transform;
        for (int i = 0; i < rootTransform.childCount; i++)
        {
            Transform child = rootTransform.GetChild(i);
            if (child == null || (sortedObject != null && child.gameObject == sortedObject))
            {
                continue;
            }

            if (child.GetComponent(typeof(Renderer)) != null || child.GetComponent(typeof(MeshFilter)) != null)
            {
                child.gameObject.SetActive(false);
            }
        }
    }

    void SetSplatMeshRendererEnabled(GameObject rootObject, bool enabled)
    {
        if (rootObject == null)
        {
            return;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            gaussianSplatObject.SetSortedRendererEnabled(enabled);
            return;
        }

        MeshRenderer renderer = GetSortedRenderer(rootObject);
        if (renderer != null)
        {
            renderer.enabled = enabled;
        }
    }

    void EnforceSingleSplatMeshRenderer(GameObject activeObject)
    {
#if !COMPILER_UDONSHARP
        GaussianSplatObject[] sceneObjects = FindSceneSplatObjects(true);
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject sceneObject = sceneObjects[i];
            if (sceneObject != null)
            {
                sceneObject.SetSortedRendererEnabled(sceneObject.gameObject == activeObject);
            }
        }
#else
        SetSplatMeshRendererEnabled(activeObject, true);
#endif
    }

    GaussianSplatObject[] FindSceneSplatObjects(bool includeInactive)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        List<GaussianSplatObject> sceneObjects = new List<GaussianSplatObject>();
        GaussianSplatObject[] allObjects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
        for (int i = 0; i < allObjects.Length; i++)
        {
            GaussianSplatObject currentObject = allObjects[i];
            if (!IsSceneSplatObject(currentObject))
            {
                continue;
            }

            if (!includeInactive && !currentObject.gameObject.activeInHierarchy)
            {
                continue;
            }

            sceneObjects.Add(currentObject);
        }

        return sceneObjects.ToArray();
#else
#if COMPILER_UDONSHARP
        return new GaussianSplatObject[0];
#else
        return UnityEngine.Object.FindObjectsOfType<GaussianSplatObject>(includeInactive);
#endif
#endif
    }

    bool SetCurrentSplatObject(GameObject activeGameObject, bool applyMaterialSettings)
    {
        if (activeGameObject == null)
        {
            splatObject = null;
            _sortedRenderer = null;
            return false;
        }

        if (GetSortedRenderer(activeGameObject) == null)
        {
            return false;
        }

        if (splatObject == activeGameObject)
        {
            SetSplatMeshRendererEnabled(splatObject, true);
            EnforceSingleSplatMeshRenderer(splatObject);
            if (applyMaterialSettings)
            {
                ApplyMaterialSettingsToSelectedObject();
            }

            return true;
        }

        SetSplatMeshRendererEnabled(splatObject, false);
        splatObject = activeGameObject;
        ShowSorted(splatObject);
        EnforceSingleSplatMeshRenderer(splatObject);
        ResetCameraPositions();
        if (applyMaterialSettings)
        {
            ApplyMaterialSettingsToSelectedObject();
        }

        return true;
    }

    GameObject FindFirstActiveSplatObject()
    {
        GaussianSplatObject[] sceneObjects = FindSceneSplatObjects(false);
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject currentObject = sceneObjects[i];
            if (currentObject == null || !currentObject.gameObject.activeInHierarchy)
            {
                continue;
            }

            GameObject currentGameObject = currentObject.gameObject;
            ShowSorted(currentGameObject);
            if (GetSortedRenderer(currentGameObject) != null)
            {
                return currentGameObject;
            }
        }

        return null;
    }

    bool ApplyActiveSplatObject(bool applyMaterialSettings)
    {
#if COMPILER_UDONSHARP
        if (splatObject != null && splatObject.activeInHierarchy)
        {
            SetSplatMeshRendererEnabled(splatObject, true);
            return true;
        }
#endif

        GameObject activeSplatObject = FindFirstActiveSplatObject();
        if (activeSplatObject == null)
        {
            splatObject = null;
            _sortedRenderer = null;
            return false;
        }

        return SetCurrentSplatObject(activeSplatObject, applyMaterialSettings);
    }

    bool ApplyActiveSplatObject()
    {
        return ApplyActiveSplatObject(true);
    }

    void InitializeSplatObject()
    {
        ApplyActiveSplatObject();
    }

    public void NotifySplatObjectEnabled(GaussianSplatObject activeSplatObject)
    {
        if (activeSplatObject == null || !activeSplatObject.gameObject.activeInHierarchy)
        {
            return;
        }

        SetCurrentSplatObject(activeSplatObject.gameObject, true);
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

        GameObject sortedObject = FindNamedChild(rootObject, "Sorted");
        if (sortedObject != null)
        {
            MeshRenderer childRenderer = (MeshRenderer)sortedObject.GetComponent(typeof(MeshRenderer));
            if (childRenderer != null)
            {
                return childRenderer;
            }
        }

        return (MeshRenderer)rootObject.GetComponent(typeof(MeshRenderer));
    }

    void EnsureLocalOwnership()
    {
        if (Networking.LocalPlayer != null)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }
    }

    void RequestSyncedStateUpdate()
    {
        if (Networking.LocalPlayer != null)
        {
            RequestSerialization();
        }
    }

    public void SetGaussianScale(float value)
    {
        EnsureLocalOwnership();
        overrideMaterialProperties = true;
        gaussianScale = Mathf.Clamp(value, 0.0f, 2.0f);
        ApplyMaterialSettingsToSelectedObject();
        RequestSyncedStateUpdate();
    }

    public void SetAntiAliasing(float value)
    {
        overrideMaterialProperties = true;
        antiAliasing = Mathf.Clamp(value, 0.0f, 3.0f);
        ApplyMaterialSettingsToSelectedObject();
    }

    public void SetAlphaCutoff(float value)
    {
        overrideMaterialProperties = true;
        alphaCutoff = Mathf.Clamp(value, 0.005f, 0.1f);
        ApplyMaterialSettingsToSelectedObject();
    }

    Material[] GetRendererMaterialsForWrite(MeshRenderer renderer)
    {
        if (renderer == null)
        {
            return new Material[0];
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return renderer.sharedMaterials;
        }
#endif

        return renderer.materials;
    }

    void SetMaterialVrcLightVolumes(Material material, bool enabled)
    {
        if (material == null)
        {
            return;
        }

        if (enabled)
        {
            material.EnableKeyword("_VRC_LIGHT_VOLUMES_ON");
        }
        else
        {
            material.DisableKeyword("_VRC_LIGHT_VOLUMES_ON");
        }
    }

    void SetMaterialSHBand(Material material, int band)
    {
        if (material == null || !material.HasProperty("_SHBand"))
        {
            return;
        }

        int clampedBand = Mathf.Clamp(band, 0, 3);
        material.SetFloat("_SHBand", clampedBand);
    }

    void SetMaterialFloatIfPresent(Material material, string propertyName, float value)
    {
        if (material == null || !material.HasProperty(propertyName))
        {
            return;
        }

        material.SetFloat(propertyName, value);
    }

    void SetMaterialVectorIfPresent(Material material, string propertyName, Vector4 value)
    {
        if (material == null || !material.HasProperty(propertyName))
        {
            return;
        }

        material.SetVector(propertyName, value);
    }

    void ApplyConfiguredMaterialSettings(Material material, int currentSHBand)
    {
        if (material == null)
        {
            return;
        }

        SetMaterialSHBand(material, currentSHBand);
        SetMaterialVrcLightVolumes(material, useVrcLightVolumes);
        SetMaterialFloatIfPresent(material, "_LightVolumeIntensity", lightVolumeIntensity);

        if (!overrideMaterialProperties)
        {
            return;
        }

        SetMaterialFloatIfPresent(material, "_GaussianMul", gaussianScale);
        SetMaterialFloatIfPresent(material, "_ThinThreshold", thinThreshold);
        SetMaterialFloatIfPresent(material, "_AntiAliasing", antiAliasing);
        SetMaterialFloatIfPresent(material, "_Log2MinScale", log2MinScale);
        SetMaterialFloatIfPresent(material, "_AlphaCutoff", alphaCutoff);
        SetMaterialFloatIfPresent(material, "_ScaleCutoff", scaleCutoff);
        SetMaterialFloatIfPresent(material, "_Exposure", exposure);
        SetMaterialFloatIfPresent(material, "_Opacity", opacity);
        SetMaterialVectorIfPresent(material, "_OKLCHShift", new Vector4(oklchShift.x, oklchShift.y, oklchShift.z, 0.0f));
        SetMaterialFloatIfPresent(material, "_Gamma", Mathf.Max(0.001f, gamma));
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    public void ApplyConfiguredMaterialSettingsForEditor(Material material)
    {
        ApplyConfiguredMaterialSettings(material, GetCurrentSHBand());
    }
#endif

    int GetSplatObjectMaxSHBand(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return 0;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            return gaussianSplatObject.GetMaxSHBand();
        }

        MeshRenderer renderer = GetSortedRenderer(rootObject);
        if (renderer == null)
        {
            return 0;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
        {
            return 0;
        }

        int inferredMax = 0;
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
            {
                continue;
            }

            if (!material.HasProperty("_GS_SH") || material.GetTexture("_GS_SH") == null || !material.HasProperty("_GS_SH_CoeffCount"))
            {
                continue;
            }

            int coeffCount = material.GetInt("_GS_SH_CoeffCount");
            if (coeffCount >= 15)
            {
                inferredMax = Mathf.Max(inferredMax, 3);
                continue;
            }

            if (coeffCount >= 8)
            {
                inferredMax = Mathf.Max(inferredMax, 2);
                continue;
            }

            if (coeffCount >= 3)
            {
                inferredMax = Mathf.Max(inferredMax, 1);
            }
        }

        return inferredMax;
    }

    public int GetSelectedSplatMaxSHBand()
    {
        if ((splatObject == null || !splatObject.activeInHierarchy) && !ApplyActiveSplatObject())
        {
            return 0;
        }

        return GetSplatObjectMaxSHBand(splatObject);
    }

    public int GetCurrentSHBand()
    {
        return Mathf.Clamp(requestedSHBand, 0, GetSelectedSplatMaxSHBand());
    }

    public void SetSHBand(int value)
    {
        EnsureLocalOwnership();
        requestedSHBand = Mathf.Clamp(value, 0, 3);
        ApplyMaterialSettingsToSelectedObject();
        RequestSyncedStateUpdate();
    }

    void ApplyMaterialSettingsToSelectedObject()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return;
        }
#endif

        if (splatObject == null)
        {
            return;
        }

        MeshRenderer renderer = GetSortedRenderer(splatObject);
        if (renderer == null)
        {
            return;
        }

        Material[] splatMats = GetRendererMaterialsForWrite(renderer);
        int currentSHBand = GetCurrentSHBand();
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            ApplyConfiguredMaterialSettings(splatMat, currentSHBand);
        }
    }

    public float GetCameraPositionQuantization()
    {
        return cameraPositionQuantization;
    }

    public void SetCameraPositionQuantization(float value)
    {
        cameraPositionQuantization = Mathf.Max(0.0f, value);
        ResetCameraPositions();
    }

    public int GetSortPipelineFrames()
    {
        return sortPipelineFrames;
    }

    public void SetSortPipelineFrames(int value)
    {
        sortPipelineFrames = Mathf.Clamp(value, 1, 8);
        ResetCameraPositions();
    }

    public int GetSortingSteps()
    {
        return GetSortPipelineFrames();
    }

    public void SetSortingSteps(int value)
    {
        SetSortPipelineFrames(value);
    }

    public bool GetAlwaysUpdate()
    {
        return alwaysUpdate;
    }

    public void SetAlwaysUpdate(bool value)
    {
        alwaysUpdate = value;
        ResetCameraPositions();
    }

    public void ToggleAlwaysUpdate()
    {
        SetAlwaysUpdate(!alwaysUpdate);
    }

    public bool GetUseVrcLightVolumes()
    {
        return useVrcLightVolumes;
    }

    public void SetUseVrcLightVolumes(bool value)
    {
        EnsureLocalOwnership();
        useVrcLightVolumes = value;
        ApplyMaterialSettingsToSelectedObject();
        RequestSyncedStateUpdate();
    }

    public void ToggleVrcLightVolumes()
    {
        SetUseVrcLightVolumes(!useVrcLightVolumes);
    }

    public float GetAntiAliasing()
    {
        return antiAliasing;
    }

    public float GetLightVolumeIntensity()
    {
        return lightVolumeIntensity;
    }

    public void SetLightVolumeIntensity(float value)
    {
        overrideMaterialProperties = true;
        lightVolumeIntensity = Mathf.Clamp(value, 0.0f, 4.0f);
        ApplyMaterialSettingsToSelectedObject();
    }

    public string GetCurrentSplatName()
    {
        if ((splatObject == null || !splatObject.activeInHierarchy) && !ApplyActiveSplatObject(false))
        {
            return "None";
        }

        return splatObject.name;
    }

    public GameObject GetCurrentSplatObject()
    {
        if ((splatObject == null || !splatObject.activeInHierarchy) && !ApplyActiveSplatObject(false))
        {
            return null;
        }

        return splatObject;
    }

    void DisableMsaaInGame()
    {
        if (VRCCameraSettings.ScreenCamera != null)
        {
            VRCCameraSettings.ScreenCamera.AllowMSAA = false;
        }
        // Commented out to prevent the VRChat Photo Camera from crashing!
       // if (VRCCameraSettings.PhotoCamera != null)
       // {
       //     VRCCameraSettings.PhotoCamera.AllowMSAA = false;
       // }
    }

    bool IsPrimaryRendererInstance()
    {
#if COMPILER_UDONSHARP
        return true;
#else
        GaussianSplatRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<GaussianSplatRenderer>();
        if (renderers == null || renderers.Length <= 1)
        {
            return true;
        }

        GaussianSplatRenderer primaryRenderer = renderers[0];
        int primaryInstanceId = primaryRenderer.GetInstanceID();
        for (int i = 1; i < renderers.Length; i++)
        {
            GaussianSplatRenderer candidate = renderers[i];
            if (candidate == null)
            {
                continue;
            }

            int candidateInstanceId = candidate.GetInstanceID();
            if (candidateInstanceId < primaryInstanceId)
            {
                primaryRenderer = candidate;
                primaryInstanceId = candidateInstanceId;
            }
        }

        if (primaryRenderer != this)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Only one renderer can be active in a scene.");
            return false;
        }

        Debug.LogError("Multiple GaussianSplatRenderer instances found. This renderer will be used; disable or remove the duplicates.");
        return true;
#endif
    }

    void Start()
    {
        if (!IsPrimaryRendererInstance())
        {
            enabled = false;
            return;
        }

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

        _completedCameraPos = new Vector3[MAX_CAMERA_COUNT];
        _pendingCameraPos = new Vector3[MAX_CAMERA_COUNT];
        _pendingCameraWorldPos = new Vector3[MAX_CAMERA_COUNT];
        _hasCompletedSort = new bool[MAX_CAMERA_COUNT];
        _hasPendingSort = new bool[MAX_CAMERA_COUNT];
        ResetCameraPositions();
        InitializeSplatObject();
        DisableMsaaInGame();
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
        if (!ApplyActiveSplatObject())
        {
            return false;
        }

        _sortedRenderer = GetSortedRenderer(splatObject);
        if (_sortedRenderer == null)
        {
            Debug.LogError($"No sorted MeshRenderer found on {splatObject.name}.");
            return false;
        }

        Material[] splatMats = GetRendererMaterialsForWrite(_sortedRenderer);
        int currentSHBand = GetCurrentSHBand();
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
            ApplyConfiguredMaterialSettings(splatMat, currentSHBand);
        }

        Texture positions = null;
        Material positionsMaterial = null;
        if (splatMats.Length > 1)
        {
            positionsMaterial = splatMats[1];
        }
        else
        {
            positionsMaterial = splatMats[0];
        }

        if (positionsMaterial != null)
        {
            positions = positionsMaterial.GetTexture("_GS_Positions");
        }

        if (positions == null)
        {
            Debug.LogError($"No _GS_Positions texture found on {splatObject.name}.");
            return false;
        }

        int textureElementCount = positions.width * positions.height;
        int actualSplatCount = positionsMaterial != null && positionsMaterial.HasProperty("_ActualSplatCount")
            ? positionsMaterial.GetInt("_ActualSplatCount")
            : 0;

        if (_radixSort == null)
        {
            _radixSort = (RadixSort)GetComponent<RadixSort>();
        }

        if (_radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return false;
        }

        _radixSort.elementCount = actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
        keyValueMat = _radixSort.computeKeyValues;
        if (keyValueMat == null)
        {
            Debug.LogError("ComputeKeyValues material is not assigned on the RadixSort component.");
            return false;
        }

        keyValueMat.SetTexture("_GS_Positions", positions);
        keyValueMat.SetInt("_GS_Positions_CoordMask", positionsMaterial.GetInt("_GS_Positions_CoordMask"));
        keyValueMat.SetInt("_GS_Positions_CoordShift", positionsMaterial.GetInt("_GS_Positions_CoordShift"));
        return true;
    }

    Vector3 WorldToSplatObjectPosition(Vector3 worldPosition)
    {
        return _sortedRenderer.transform.InverseTransformPoint(worldPosition);
    }

    int GetSortSubpassBudget()
    {
        return Mathf.CeilToInt((float)RadixSort.TotalSortPasses / Mathf.Clamp(sortPipelineFrames, 1, RadixSort.TotalSortPasses));
    }

    void RequestCameraSort(Vector3 cameraPos, int cameraID, bool forceUpdate)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        if (!forceUpdate && !alwaysUpdate && _hasCompletedSort[cameraID] && quantizedPos == _completedCameraPos[cameraID])
        {
            return;
        }

        _pendingCameraPos[cameraID] = quantizedPos;
        _pendingCameraWorldPos[cameraID] = cameraPos;
        _hasPendingSort[cameraID] = true;
    }

    bool TryStartPendingSort(int cameraID)
    {
        if (!_hasPendingSort[cameraID])
        {
            return false;
        }

        if (!alwaysUpdate && _hasCompletedSort[cameraID] && _pendingCameraPos[cameraID] == _completedCameraPos[cameraID])
        {
            _hasPendingSort[cameraID] = false;
            return false;
        }

        keyValueMat.SetVector("_CameraPos", WorldToSplatObjectPosition(_pendingCameraWorldPos[cameraID]));
        _radixSort.BeginSort();
        _activeSortCameraId = cameraID;
        _activeSortQuantizedPos = _pendingCameraPos[cameraID];
        _hasPendingSort[cameraID] = false;
        return true;
    }

    void StartNextPendingSort()
    {
        if (_activeSortCameraId != NO_ACTIVE_SORT)
        {
            return;
        }

        if (TryStartPendingSort(SCREEN_CAMERA_ID))
        {
            return;
        }

        TryStartPendingSort(PHOTO_CAMERA_ID);
    }

    void PublishActiveSort()
    {
        if (_activeSortCameraId == NO_ACTIVE_SORT)
        {
            return;
        }

        _radixSort.CopySortedOrder(splatRenderOrder, _activeSortCameraId);
        _completedCameraPos[_activeSortCameraId] = _activeSortQuantizedPos;
        _hasCompletedSort[_activeSortCameraId] = true;

        if (_activeSortCameraId == SCREEN_CAMERA_ID)
        {
            ShowSorted(splatObject);
        }

        _activeSortCameraId = NO_ACTIVE_SORT;
        _activeSortQuantizedPos = Vector3.positiveInfinity;
    }

    void ProcessSortPipeline()
    {
        StartNextPendingSort();
        if (_activeSortCameraId == NO_ACTIVE_SORT)
        {
            return;
        }

        _radixSort.StepSort(GetSortSubpassBudget());
        if (_radixSort.IsSortComplete())
        {
            PublishActiveSort();
            StartNextPendingSort();
        }
    }

    void RunBlockingSort(Vector3 cameraPos, int cameraID)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        keyValueMat.SetVector("_CameraPos", WorldToSplatObjectPosition(cameraPos));
        _radixSort.BeginSort();
        _activeSortCameraId = cameraID;
        _activeSortQuantizedPos = quantizedPos;
        _radixSort.StepSort(RadixSort.TotalSortPasses);
        PublishActiveSort();
    }

    public void SortCameras(Vector3 screenCamPos)
    {
        if (!ApplyActiveSplatObject())
        {
            return;
        }

        if (!UpdateMaterials())
        {
            return;
        }

        if (!_hasCompletedSort[SCREEN_CAMERA_ID])
        {
            RunBlockingSort(screenCamPos, SCREEN_CAMERA_ID);
        }
        else
        {
            RequestCameraSort(screenCamPos, SCREEN_CAMERA_ID, false);
        }

        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        if (photoCam != null && photoCam.Active)
        {
            if (!_hasCompletedSort[PHOTO_CAMERA_ID])
            {
                RunBlockingSort(photoCam.Position, PHOTO_CAMERA_ID);
            }
            else
            {
                RequestCameraSort(photoCam.Position, PHOTO_CAMERA_ID, false);
            }
        }

        ProcessSortPipeline();

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
        DisableMsaaInGame();

        if (!ApplyActiveSplatObject())
        {
            return;
        }

        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        SortCameras(screenCamPos);
    }

    public override void OnDeserialization()
    {
        ResetCameraPositions();
        if (!ApplyActiveSplatObject())
        {
            return;
        }

        ApplyMaterialSettingsToSelectedObject();
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static bool IsSceneSplatObject(GaussianSplatObject splatObject)
    {
        if (splatObject == null)
        {
            return false;
        }

        GameObject rootObject = splatObject.transform.root != null ? splatObject.transform.root.gameObject : splatObject.gameObject;
        if (rootObject == null || EditorUtility.IsPersistent(rootObject))
        {
            return false;
        }

        if ((splatObject.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
        {
            return false;
        }

        return true;
    }

    [InitializeOnLoadMethod]
    static void RegisterSceneAutomation()
    {
        EditorApplication.hierarchyChanged -= OnEditorHierarchyChanged;
        EditorApplication.hierarchyChanged += OnEditorHierarchyChanged;
    }

    static void OnEditorHierarchyChanged()
    {
        GaussianSplatObject[] allObjects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
        for (int i = 0; i < allObjects.Length; i++)
        {
            if (IsSceneSplatObject(allObjects[i]))
            {
                EnsureSceneRendererExists();
                GaussianSplatRendererUI.RequestEditorRefresh();
                return;
            }
        }

        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    static GaussianSplatRenderer[] FindSceneRenderers()
    {
        List<GaussianSplatRenderer> sceneRenderers = new List<GaussianSplatRenderer>();
        GaussianSplatRenderer[] allRenderers = Resources.FindObjectsOfTypeAll<GaussianSplatRenderer>();
        for (int i = 0; i < allRenderers.Length; i++)
        {
            GaussianSplatRenderer renderer = allRenderers[i];
            if (renderer == null)
            {
                continue;
            }

            GameObject rootObject = renderer.transform.root != null ? renderer.transform.root.gameObject : renderer.gameObject;
            if (rootObject == null || EditorUtility.IsPersistent(rootObject))
            {
                continue;
            }

            if ((renderer.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
            {
                continue;
            }

            sceneRenderers.Add(renderer);
        }

        return sceneRenderers.ToArray();
    }

    static GaussianSplatRenderer GetPrimarySceneRenderer()
    {
        GaussianSplatRenderer[] renderers = FindSceneRenderers();
        if (renderers.Length == 0)
        {
            return null;
        }

        GaussianSplatRenderer primaryRenderer = renderers[0];
        int primaryInstanceId = primaryRenderer.GetInstanceID();
        for (int i = 1; i < renderers.Length; i++)
        {
            GaussianSplatRenderer candidate = renderers[i];
            if (candidate == null)
            {
                continue;
            }

            int candidateInstanceId = candidate.GetInstanceID();
            if (candidateInstanceId < primaryInstanceId)
            {
                primaryRenderer = candidate;
                primaryInstanceId = candidateInstanceId;
            }
        }

        return primaryRenderer;
    }

    static Material LoadPackageMaterial(string assetPath)
    {
        return AssetDatabase.LoadAssetAtPath<Material>(assetPath);
    }

    [MenuItem("GameObject/Gaussian Splatting/Gaussian Splat Renderer", false, 10)]
    static void CreateGaussianSplatRenderer(MenuCommand menuCommand)
    {
        GaussianSplatRenderer renderer = EnsureSceneRendererExists();
        if (renderer != null)
        {
            Selection.activeGameObject = renderer.gameObject;
        }
    }

    public static GaussianSplatRenderer FindExistingSceneRenderer()
    {
        return GetPrimarySceneRenderer();
    }

    public static GaussianSplatRenderer EnsureSceneRendererExists()
    {
        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer();
        if (primaryRenderer == null)
        {
            GameObject rendererObject = new GameObject("GaussianSplatRenderer");
            Undo.RegisterCreatedObjectUndo(rendererObject, "Create Gaussian Splat Renderer");

            primaryRenderer = AddGeneratedUdonSharpComponent<GaussianSplatRenderer>(rendererObject, "Add Gaussian Splat Renderer");
            RadixSort radixSort = AddGeneratedUdonSharpComponent<RadixSort>(rendererObject, "Add Radix Sort");
            radixSort.computeKeyValues = LoadPackageMaterial("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ComputeKeyValue.mat");
            radixSort.radixSort = LoadPackageMaterial("Assets/VRChatGaussianSplatting/RadixSort/Materials/Misha_RadixSort.mat");
            EditorUtility.SetDirty(rendererObject);
            EditorUtility.SetDirty(primaryRenderer);
            EditorUtility.SetDirty(radixSort);
        }

        GaussianSplatRenderer[] renderers = FindSceneRenderers();
        if (renderers.Length > 1)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Keep one renderer in the scene; Gaussian splats now share a single renderer.");
            for (int i = 0; i < renderers.Length; i++)
            {
                GaussianSplatRenderer renderer = renderers[i];
                if (renderer != null && renderer != primaryRenderer)
                {
                    renderer.enabled = false;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        if (!primaryRenderer.enabled)
        {
            primaryRenderer.enabled = true;
            EditorUtility.SetDirty(primaryRenderer);
        }

        primaryRenderer.UpdateSortingResourceTextures();
        return primaryRenderer;
    }

    void OnValidate()
    {
        if (EditorUtility.IsPersistent(this))
        {
            return;
        }

        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer();
        if (primaryRenderer != null && primaryRenderer != this)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Only one GaussianSplatRenderer is supported per scene.");
            enabled = false;
            EditorUtility.SetDirty(this);
            return;
        }

        UpdateSortingResourceTextures();
    }

    static T AddGeneratedUdonSharpComponent<T>(GameObject targetObject, string undoLabel) where T : UdonSharpBehaviour
    {
        Undo.RegisterCompleteObjectUndo(targetObject, undoLabel);
        return targetObject.AddUdonSharpComponent<T>();
    }

    static string SanitizeAssetName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "GaussianSplatRenderer";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] sanitizedChars = value.ToCharArray();
        for (int i = 0; i < sanitizedChars.Length; i++)
        {
            if (Array.IndexOf(invalidChars, sanitizedChars[i]) >= 0)
            {
                sanitizedChars[i] = '_';
            }
        }

        return new string(sanitizedChars);
    }

    static void EnsureFolderExists(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        string normalizedPath = folderPath.Replace('\\', '/');
        string[] parts = normalizedPath.Split('/');
        if (parts.Length == 0)
        {
            return;
        }

        string currentPath = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string nextPath = currentPath + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(nextPath))
            {
                AssetDatabase.CreateFolder(currentPath, parts[i]);
            }

            currentPath = nextPath;
        }
    }

    static string GetSceneSortResourceFolderName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return "GS_UnsavedScene";
        }

        string sanitizedSceneName = SanitizeAssetName(sceneName);
        if (string.IsNullOrEmpty(sanitizedSceneName))
        {
            return "GS_UnsavedScene";
        }

        return "GS_" + sanitizedSceneName;
    }

    string GetSortResourceFolderPath()
    {
        string sceneName = string.Empty;
        if (gameObject != null)
        {
            sceneName = gameObject.scene.name;
            if (string.IsNullOrEmpty(sceneName) && !string.IsNullOrEmpty(gameObject.scene.path))
            {
                sceneName = Path.GetFileNameWithoutExtension(gameObject.scene.path);
            }
        }

        return "Assets/Temp/" + GetSceneSortResourceFolderName(sceneName) + "/RTs";
    }

    Texture GetPositionsTexture(GameObject rootObject)
    {
        MeshRenderer renderer = GetSortedRenderer(rootObject);
        if (renderer == null)
        {
            return null;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0)
        {
            return null;
        }

        Material positionsMaterial = materials.Length > 1 && materials[1] != null ? materials[1] : materials[0];
        if (positionsMaterial == null || !positionsMaterial.HasProperty("_GS_Positions"))
        {
            return null;
        }

        return positionsMaterial.GetTexture("_GS_Positions");
    }

    int GetSortElementCount(GameObject rootObject, out Texture positionsTexture)
    {
        positionsTexture = null;

        MeshRenderer renderer = GetSortedRenderer(rootObject);
        if (renderer == null)
        {
            return 0;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0)
        {
            return 0;
        }

        Material positionsMaterial = materials.Length > 1 && materials[1] != null ? materials[1] : materials[0];
        if (positionsMaterial == null || !positionsMaterial.HasProperty("_GS_Positions"))
        {
            return 0;
        }

        positionsTexture = positionsMaterial.GetTexture("_GS_Positions");
        if (positionsTexture == null)
        {
            return 0;
        }

        int textureElementCount = positionsTexture.width * positionsTexture.height;
        int actualSplatCount = positionsMaterial.HasProperty("_ActualSplatCount") ? positionsMaterial.GetInt("_ActualSplatCount") : 0;
        return actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
    }

    static void ComputeRequiredSortTextureSize(int elementCount, out int width, out int height)
    {
        int optimalPot = Mathf.NextPowerOfTwo(Mathf.Max(1, elementCount));
        int optimalPotLog2 = Mathf.CeilToInt(Mathf.Log(optimalPot, 2));
        int imageSizeLog2Y = optimalPotLog2 / 2;
        int imageSizeLog2X = imageSizeLog2Y + (optimalPotLog2 % 2);
        width = 1 << imageSizeLog2X;
        height = 1 << imageSizeLog2Y;
    }

    RenderTexture CreateSortRenderTextureAsset(string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        EnsureFolderExists(folderPath);

        RenderTexture renderTexture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
        renderTexture.name = assetName;
        renderTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        renderTexture.volumeDepth = volumeDepth;
        renderTexture.useMipMap = useMipMap;
        renderTexture.autoGenerateMips = false;
        renderTexture.wrapMode = TextureWrapMode.Clamp;
        renderTexture.filterMode = FilterMode.Point;
        renderTexture.enableRandomWrite = false;
        renderTexture.anisoLevel = 0;
        renderTexture.antiAliasing = 1;
        renderTexture.Create();

        string assetPath = AssetDatabase.GenerateUniqueAssetPath(folderPath + "/" + assetName + ".renderTexture");
        AssetDatabase.CreateAsset(renderTexture, assetPath);
        return renderTexture;
    }

    bool EnsureSortRenderTexture(ref RenderTexture targetTexture, string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        string normalizedFolderPath = folderPath.Replace('\\', '/');
        string expectedAssetPath = normalizedFolderPath + "/" + assetName + ".renderTexture";
        string currentAssetPath = targetTexture == null ? string.Empty : AssetDatabase.GetAssetPath(targetTexture).Replace('\\', '/');
        bool needsPackageResource = targetTexture == null || string.IsNullOrEmpty(currentAssetPath) || !currentAssetPath.StartsWith(normalizedFolderPath + "/");

        if (needsPackageResource)
        {
            EnsureFolderExists(normalizedFolderPath);
            RenderTexture existingTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(expectedAssetPath);
            targetTexture = existingTexture != null
                ? existingTexture
                : CreateSortRenderTextureAsset(normalizedFolderPath, assetName, width, height, format, useMipMap, volumeDepth);
        }

        bool needsResize = targetTexture.width != width
            || targetTexture.height != height
            || targetTexture.format != format
            || targetTexture.dimension != (volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D)
            || targetTexture.volumeDepth != volumeDepth
            || targetTexture.useMipMap != useMipMap
            || targetTexture.autoGenerateMips
            || targetTexture.wrapMode != TextureWrapMode.Clamp
            || targetTexture.filterMode != FilterMode.Point
            || targetTexture.enableRandomWrite
            || targetTexture.anisoLevel != 0
            || targetTexture.antiAliasing != 1;
        if (!needsPackageResource && !needsResize)
        {
            return false;
        }

        Undo.RecordObject(targetTexture, "Resize Gaussian Splat Sort Texture");
        targetTexture.Release();
        targetTexture.width = width;
        targetTexture.height = height;
        targetTexture.format = format;
        targetTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        targetTexture.volumeDepth = volumeDepth;
        targetTexture.useMipMap = useMipMap;
        targetTexture.autoGenerateMips = false;
        targetTexture.wrapMode = TextureWrapMode.Clamp;
        targetTexture.filterMode = FilterMode.Point;
        targetTexture.enableRandomWrite = false;
        targetTexture.anisoLevel = 0;
        targetTexture.antiAliasing = 1;
        targetTexture.Create();
        EditorUtility.SetDirty(targetTexture);
        return true;
    }

    void UpdateSortingResourceTextures()
    {
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return;
        }

        int largestElementCount = 0;
        string largestSplatName = null;
        GaussianSplatObject[] sceneSplatObjects = FindSceneSplatObjects(true);
        for (int i = 0; i < sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject currentSplatObject = sceneSplatObjects[i];
            if (currentSplatObject == null)
            {
                continue;
            }

            int elementCount = GetSortElementCount(currentSplatObject.gameObject, out Texture positionsTexture);
            if (positionsTexture == null)
            {
                continue;
            }

            if (elementCount > largestElementCount)
            {
                largestElementCount = elementCount;
                largestSplatName = currentSplatObject.gameObject.name;
            }
        }

        if (largestElementCount <= 0)
        {
            Debug.LogWarning("No valid _GS_Positions textures were found on the scene Gaussian Splat Objects.");
            return;
        }

        ComputeRequiredSortTextureSize(largestElementCount, out int requiredWidth, out int requiredHeight);

        string resourceFolderPath = GetSortResourceFolderPath();
        string assetPrefix = SanitizeAssetName(name);

        Undo.RecordObject(this, "Update Gaussian Splat Sorting Resources");
        Undo.RecordObject(radixSort, "Update Gaussian Splat Sorting Resources");

        bool resourcesChanged = false;
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues0, resourceFolderPath, assetPrefix + "_KeyValues0", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues1, resourceFolderPath, assetPrefix + "_KeyValues1", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.prefixSums, resourceFolderPath, assetPrefix + "_PrefixSums", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, true, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref splatRenderOrder, resourceFolderPath, assetPrefix + "_SplatRenderOrder", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 2);

        EditorUtility.SetDirty(radixSort);
        EditorUtility.SetDirty(this);

        if (resourcesChanged)
        {
            Debug.Log($"Updated sorting textures to {requiredWidth}x{requiredHeight} for largest splat '{largestSplatName}' ({largestElementCount} splats).");
        }
    }

#endif
}

}
