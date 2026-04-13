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
    const int MAX_CAMERA_COUNT = 3; // Screen camera + Photo camera + Mirror camera
    private Vector3[] _prevCameraPos;
    private RadixSort _radixSort;
    private MeshRenderer _sortedRenderer;
    private Material keyValueMat;
    private GameObject splatObject;

    [Header("Gaussian Splat Object")]
    [UdonSynced, Tooltip("The index of the currently rendered splat object in the splatObjects array.")]
    public int splatObjectIndex = 0; // Index of the current splat object in the splatObjects array
    [Tooltip("The GameObjects that contain the Gaussian Splat roots.")]
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
    [UdonSynced, SerializeField] public bool overrideMaterialProperties = false;
    [UdonSynced, Range(0, 3)] [SerializeField] int requestedSHBand = 3;
    [UdonSynced, Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 3.0f)] [SerializeField] float antiAliasing = 1.0f;
    [Range(0.005f, 0.1f)] [SerializeField] public float alphaCutoff = 0.03f;
    [UdonSynced, SerializeField] bool useVrcLightVolumes = false;
    [Range(0.0f, 4.0f)] [SerializeField] float lightVolumeIntensity = 1.0f;

    // [Header("Optional Mirror")]
    // [Tooltip("Optional mirror GameObject. If set, the script will also sort splats for the mirror camera position.")]
    // public GameObject mirror;

    void ResetCameraPositions()
    {
        if (_prevCameraPos == null)
        {
            return;
        }

        for (int i = 0; i < MAX_CAMERA_COUNT; i++)
        {
            _prevCameraPos[i] = Vector3.positiveInfinity; // Reset to a value that will always trigger an update
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

    void DeactivateSplatObjects()
    {
        if (splatObjects == null)
        {
            return;
        }

        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject splatObj = splatObjects[i];
            if (splatObj != null)
            {
                ShowSorted(splatObj);
                splatObj.SetActive(false);
            }
        }
    }

    bool HasSplatObjects()
    {
        return splatObjects != null && splatObjects.Length > 0;
    }

    bool IsValidSplatObjectIndex(int index)
    {
        return HasSplatObjects() && index >= 0 && index < splatObjects.Length;
    }

    bool ApplySelectedSplatObject()
    {
        if (!HasSplatObjects())
        {
            return false;
        }

        if (!IsValidSplatObjectIndex(splatObjectIndex))
        {
            Debug.LogError($"Invalid splat object index: {splatObjectIndex}. Must be between 0 and {splatObjects.Length - 1}.");
            return false;
        }

        GameObject selectedSplatObject = splatObjects[splatObjectIndex];
        if (selectedSplatObject == null)
        {
            Debug.LogError($"Splat object at index {splatObjectIndex} is null. Please ensure the splatObjects array is populated correctly.");
            return false;
        }

        if (splatObject == selectedSplatObject)
        {
            return true;
        }

        DeactivateSplatObjects();
        splatObject = selectedSplatObject;
        splatObject.SetActive(true);
        ShowSorted(splatObject);
        return true;
    }

    void InitializeSplatObject()
    {
        if (!HasSplatObjects())
        {
            return;
        }

        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject splatObj = splatObjects[i];
            if (splatObj == null)
            {
                continue;
            }

            ShowSorted(splatObj);
            splatObj.SetActive(false);
        }

        if (!IsValidSplatObjectIndex(splatObjectIndex))
        {
            Debug.LogError($"Invalid splat object index: {splatObjectIndex}. Must be between 0 and {splatObjects.Length - 1}.");
            return;
        }

        splatObject = null;
        if (!ApplySelectedSplatObject())
        {
            return;
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

    public void SetSplatObjectIndex(int index)
    {
        if (splatObjectIndex == index && splatObject != null)
        {
            return;
        }

        if (!HasSplatObjects())
        {
            Debug.LogError("No splat objects have been assigned to the GaussianSplatRenderer.");
            return;
        }

        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return;
        }

        ResetCameraPositions();
        splatObjectIndex = index;
        if (!ApplySelectedSplatObject())
        {
            return;
        }

        requestedSHBand = GetSelectedSplatMaxSHBand();
        ApplyMaterialSettingsToSelectedObject();
    }

    public GameObject GetObjectByIndex(int index)
    {
        if (!HasSplatObjects())
        {
            Debug.LogError("No splat objects have been assigned to the GaussianSplatRenderer.");
            return null;
        }

        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return null;
        }
        return splatObjects[index];
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

    public void SelectSplatObject(int index)
    {
        EnsureLocalOwnership();
        SetSplatObjectIndex(index);
        RequestSyncedStateUpdate();
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

    void SetMaterialVrcLightVolumes(Material material, bool enabled)
    {
        if (material == null || !material.HasProperty("_VRC_LIGHT_VOLUMES"))
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

            if (material.HasProperty("_GS_SH9") && material.GetTexture("_GS_SH9") != null)
            {
                inferredMax = Mathf.Max(inferredMax, 3);
                continue;
            }

            if (material.HasProperty("_GS_SH4") && material.GetTexture("_GS_SH4") != null)
            {
                inferredMax = Mathf.Max(inferredMax, 2);
                continue;
            }

            if (material.HasProperty("_GS_SH1") && material.GetTexture("_GS_SH1") != null)
            {
                inferredMax = Mathf.Max(inferredMax, 1);
            }
        }

        return inferredMax;
    }

    public int GetSelectedSplatMaxSHBand()
    {
        if (!ApplySelectedSplatObject())
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
        if (!ApplySelectedSplatObject())
        {
            return;
        }

        MeshRenderer renderer = GetSortedRenderer(splatObject);
        if (renderer == null)
        {
            return;
        }

        Material[] splatMats = renderer.materials;
        int currentSHBand = GetCurrentSHBand();
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            SetMaterialSHBand(splatMat, currentSHBand);
            SetMaterialVrcLightVolumes(splatMat, useVrcLightVolumes);
            if (splatMat.HasProperty("_LightVolumeIntensity"))
            {
                splatMat.SetFloat("_LightVolumeIntensity", lightVolumeIntensity);
            }
            if (overrideMaterialProperties)
            {
                if (splatMat.HasProperty("_GaussianMul"))
                {
                    splatMat.SetFloat("_GaussianMul", gaussianScale);
                }

                if (splatMat.HasProperty("_AntiAliasing"))
                {
                    splatMat.SetFloat("_AntiAliasing", antiAliasing);
                }

                if (splatMat.HasProperty("_AlphaCutoff"))
                {
                    splatMat.SetFloat("_AlphaCutoff", alphaCutoff);
                }
            }
        }
    }

    public float GetMinSortDistance()
    {
        return minSortDistance;
    }

    public void SetMinSortDistance(float value)
    {
        minSortDistance = Mathf.Clamp(value, 0.0f, maxSortDistance);
        ResetCameraPositions();
    }

    public float GetMaxSortDistance()
    {
        return maxSortDistance;
    }

    public void SetMaxSortDistance(float value)
    {
        maxSortDistance = Mathf.Max(value, minSortDistance);
        ResetCameraPositions();
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

    public int GetSortingSteps()
    {
        return sortingSteps;
    }

    public void SetSortingSteps(int value)
    {
        sortingSteps = Mathf.Clamp(value, 2, 8);
        ResetCameraPositions();
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
        if (!ApplySelectedSplatObject())
        {
            return "None";
        }

        return splatObject.name;
    }

    void DisableMsaaInGame()
    {
        if (VRCCameraSettings.ScreenCamera != null)
        {
            VRCCameraSettings.ScreenCamera.AllowMSAA = false;
        }

        if (VRCCameraSettings.PhotoCamera != null)
        {
            VRCCameraSettings.PhotoCamera.AllowMSAA = false;
        }
    }

    void Start()
    {
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
        if (!ApplySelectedSplatObject())
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
        int currentSHBand = GetCurrentSHBand();
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
            splatMat.SetVector("_MinMaxSortDistance", minMaxSortDistance);
            SetMaterialSHBand(splatMat, currentSHBand);
            SetMaterialVrcLightVolumes(splatMat, useVrcLightVolumes);
            if (splatMat.HasProperty("_LightVolumeIntensity"))
            {
                splatMat.SetFloat("_LightVolumeIntensity", lightVolumeIntensity);
            }
            if (overrideMaterialProperties)
            {
                if (splatMat.HasProperty("_GaussianMul"))
                {
                    splatMat.SetFloat("_GaussianMul", gaussianScale);
                }

                if (splatMat.HasProperty("_AntiAliasing"))
                {
                    splatMat.SetFloat("_AntiAliasing", antiAliasing);
                }

                if (splatMat.HasProperty("_AlphaCutoff"))
                {
                    splatMat.SetFloat("_AlphaCutoff", alphaCutoff);
                }
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
        if (!ApplySelectedSplatObject())
        {
            return;
        }

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
        DisableMsaaInGame();

        if (!ApplySelectedSplatObject())
        {
            return;
        }

        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        SortCameras(screenCamPos);
    }

    public override void OnDeserialization()
    {
        ResetCameraPositions();
        if (!ApplySelectedSplatObject())
        {
            return;
        }

        ApplyMaterialSettingsToSelectedObject();
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static Type _cachedVrChatUiShapeType;

    static Type FindTypeInLoadedAssemblies(string fullTypeName, string shortTypeName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type resolvedType = assemblies[i].GetType(fullTypeName);
            if (resolvedType != null)
            {
                return resolvedType;
            }
        }

        for (int i = 0; i < assemblies.Length; i++)
        {
            Type[] types;
            try
            {
                types = assemblies[i].GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }

            if (types == null)
            {
                continue;
            }

            for (int j = 0; j < types.Length; j++)
            {
                Type candidateType = types[j];
                if (candidateType != null && candidateType.Name == shortTypeName)
                {
                    return candidateType;
                }
            }
        }

        return null;
    }

    static Type GetVrChatUiShapeType()
    {
        if (_cachedVrChatUiShapeType != null)
        {
            return _cachedVrChatUiShapeType;
        }

        _cachedVrChatUiShapeType = FindTypeInLoadedAssemblies("VRC.SDK3.Components.VRCUiShape", "VRCUiShape");
        if (_cachedVrChatUiShapeType == null)
        {
            _cachedVrChatUiShapeType = FindTypeInLoadedAssemblies("VRC.SDKBase.VRC_UiShape", "VRC_UiShape");
        }

        return _cachedVrChatUiShapeType;
    }

    static void TryAddVrChatUiShape(GameObject targetObject)
    {
        if (targetObject == null)
        {
            return;
        }

        Type vrChatUiShapeType = GetVrChatUiShapeType();
        if (vrChatUiShapeType == null)
        {
            return;
        }

        if (targetObject.GetComponent(vrChatUiShapeType) == null)
        {
            targetObject.AddComponent(vrChatUiShapeType);
        }
    }

    static Font GetBuiltinUiFont()
    {
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    static T AddGeneratedUdonSharpComponent<T>(GameObject targetObject, string undoLabel) where T : UdonSharpBehaviour
    {
        Undo.RegisterCompleteObjectUndo(targetObject, undoLabel);
        return targetObject.AddUdonSharpComponent<T>();
    }

    static UdonBehaviour GetBackingUdonBehaviour(UdonSharpBehaviour proxyBehaviour)
    {
        if (proxyBehaviour == null)
        {
            return null;
        }

        return UdonSharpEditorUtility.GetBackingUdonBehaviour(proxyBehaviour);
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

    string GetSortResourceFolderPath()
    {
        string scenePath = gameObject.scene.path;
        if (!string.IsNullOrEmpty(scenePath))
        {
            string sceneFolder = Path.GetDirectoryName(scenePath);
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);
            if (!string.IsNullOrEmpty(sceneFolder) && !string.IsNullOrEmpty(sceneName))
            {
                return (sceneFolder.Replace('\\', '/') + "/" + sceneName + "_GaussianSplatSortResources");
            }
        }

        return "Assets/VRChatGaussianSplatting/GeneratedSortingResources";
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
        renderTexture.autoGenerateMips = useMipMap;
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

    void EnsureSortRenderTexture(ref RenderTexture targetTexture, string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        if (targetTexture == null)
        {
            targetTexture = CreateSortRenderTextureAsset(folderPath, assetName, width, height, format, useMipMap, volumeDepth);
            return;
        }

        Undo.RecordObject(targetTexture, "Resize Gaussian Splat Sort Texture");
        targetTexture.Release();
        targetTexture.width = width;
        targetTexture.height = height;
        targetTexture.format = format;
        targetTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        targetTexture.volumeDepth = volumeDepth;
        targetTexture.useMipMap = useMipMap;
        targetTexture.autoGenerateMips = useMipMap;
        targetTexture.wrapMode = TextureWrapMode.Clamp;
        targetTexture.filterMode = FilterMode.Point;
        targetTexture.enableRandomWrite = false;
        targetTexture.anisoLevel = 0;
        targetTexture.antiAliasing = 1;
        targetTexture.Create();
        EditorUtility.SetDirty(targetTexture);
    }

    static void AddUdonSharpButtonEvent(Button button, UdonSharpBehaviour targetBehaviour, string eventName)
    {
        if (button == null || targetBehaviour == null || string.IsNullOrEmpty(eventName))
        {
            return;
        }

        UdonBehaviour backingBehaviour = GetBackingUdonBehaviour(targetBehaviour);
        if (backingBehaviour == null)
        {
            return;
        }

        UnityEventTools.AddStringPersistentListener(button.onClick, backingBehaviour.SendCustomEvent, eventName);
        EditorUtility.SetDirty(backingBehaviour);
    }

    static Material CreateOpaqueBackgroundMaterial()
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            return null;
        }

        Material material = new Material(shader);
        material.name = "Gaussian Splat UI Background";
        material.color = new Color(0.08f, 0.08f, 0.1f, 1.0f);
        return material;
    }

    static GameObject CreateOpaqueBackgroundPlate(Transform parent, Vector2 sizeDelta)
    {
        GameObject backgroundObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(backgroundObject, "Create Gaussian Splat UI Background");
        backgroundObject.name = "Background";
        backgroundObject.transform.SetParent(parent, false);
        backgroundObject.transform.localPosition = new Vector3(0.0f, 0.0f, 6.0f);
        backgroundObject.transform.localRotation = Quaternion.identity;
        backgroundObject.transform.localScale = new Vector3(sizeDelta.x + 24.0f, sizeDelta.y + 24.0f, 1.0f);

        Collider backgroundCollider = backgroundObject.GetComponent<Collider>();
        if (backgroundCollider != null)
        {
            backgroundCollider.enabled = false;
        }

        MeshRenderer backgroundRenderer = backgroundObject.GetComponent<MeshRenderer>();
        if (backgroundRenderer != null)
        {
            Material backgroundMaterial = CreateOpaqueBackgroundMaterial();
            if (backgroundMaterial != null)
            {
                backgroundRenderer.sharedMaterial = backgroundMaterial;
            }
        }

        return backgroundObject;
    }

    static RectTransform CreateRectTransform(string objectName, Transform parent, Vector2 sizeDelta)
    {
        GameObject childObject = new GameObject(objectName, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(childObject, "Create Gaussian Splat UI Element");
        childObject.transform.SetParent(parent, false);
        RectTransform rectTransform = childObject.GetComponent<RectTransform>();
        rectTransform.sizeDelta = sizeDelta;
        return rectTransform;
    }

    static Text CreateTextElement(string objectName, Transform parent, string textValue, int fontSize, TextAnchor alignment, Color textColor)
    {
        int lineCount = 1;
        for (int i = 0; i < textValue.Length; i++)
        {
            if (textValue[i] == '\n')
            {
                lineCount++;
            }
        }

        float preferredHeight = (lineCount * (fontSize + 6.0f)) + 12.0f;
        RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, preferredHeight));
        Text text = rectTransform.gameObject.AddComponent<Text>();
        text.font = GetBuiltinUiFont();
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = textColor;
        text.text = textValue;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = preferredHeight;
        layoutElement.minHeight = preferredHeight;
        return text;
    }

    static void SetPreferredWidth(GameObject targetObject, float width, float flexibleWidth)
    {
        LayoutElement layoutElement = targetObject.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = targetObject.AddComponent<LayoutElement>();
        }

        if (width > 0.0f)
        {
            layoutElement.minWidth = width;
            layoutElement.preferredWidth = width;
        }

        layoutElement.flexibleWidth = flexibleWidth;
    }

    static void SetPreferredHeight(GameObject targetObject, float height, float flexibleHeight)
    {
        LayoutElement layoutElement = targetObject.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = targetObject.AddComponent<LayoutElement>();
        }

        if (height > 0.0f)
        {
            layoutElement.minHeight = height;
            layoutElement.preferredHeight = height;
        }

        layoutElement.flexibleHeight = flexibleHeight;
    }

    static GameObject CreateVerticalGroup(string objectName, Transform parent, RectOffset padding, float spacing, TextAnchor childAlignment)
    {
        RectTransform rectTransform = CreateRectTransform(objectName, parent, Vector2.zero);
        VerticalLayoutGroup layoutGroup = rectTransform.gameObject.AddComponent<VerticalLayoutGroup>();
        layoutGroup.padding = padding;
        layoutGroup.spacing = spacing;
        layoutGroup.childControlWidth = true;
        layoutGroup.childControlHeight = true;
        layoutGroup.childForceExpandWidth = true;
        layoutGroup.childForceExpandHeight = false;
        layoutGroup.childAlignment = childAlignment;

        ContentSizeFitter fitter = rectTransform.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return rectTransform.gameObject;
    }

    static GameObject CreateHorizontalGroup(string objectName, Transform parent, float spacing, bool forceExpandWidth)
    {
        RectTransform rectTransform = CreateRectTransform(objectName, parent, Vector2.zero);
        HorizontalLayoutGroup layoutGroup = rectTransform.gameObject.AddComponent<HorizontalLayoutGroup>();
        layoutGroup.spacing = spacing;
        layoutGroup.childControlWidth = true;
        layoutGroup.childControlHeight = true;
        layoutGroup.childForceExpandWidth = forceExpandWidth;
        layoutGroup.childForceExpandHeight = false;
        layoutGroup.childAlignment = TextAnchor.UpperLeft;

        ContentSizeFitter fitter = rectTransform.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
        layoutElement.flexibleWidth = 1.0f;
        return rectTransform.gameObject;
    }

    static Button CreateButtonElement(string objectName, Transform parent, string buttonLabel, Color backgroundColor, float preferredWidth = 0.0f, float flexibleWidth = 1.0f)
    {
        RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(preferredWidth, 38.0f));
        Image image = rectTransform.gameObject.AddComponent<Image>();
        image.color = backgroundColor;

        Button button = rectTransform.gameObject.AddComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = backgroundColor;
        colors.highlightedColor = backgroundColor * 1.1f;
        colors.pressedColor = backgroundColor * 0.85f;
        colors.selectedColor = backgroundColor;
        colors.disabledColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0.4f);
        button.colors = colors;

        LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = 38.0f;
        layoutElement.minHeight = 38.0f;
        if (preferredWidth > 0.0f)
        {
            layoutElement.preferredWidth = preferredWidth;
            layoutElement.minWidth = preferredWidth;
        }
        layoutElement.flexibleWidth = flexibleWidth;

        Text label = CreateTextElement("Label", rectTransform, buttonLabel, 16, TextAnchor.MiddleCenter, Color.white);
        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(8.0f, 4.0f);
        labelRect.offsetMax = new Vector2(-8.0f, -4.0f);

        TryAddVrChatUiShape(rectTransform.gameObject);
        return button;
    }

    static Slider CreateSliderElement(string objectName, Transform parent, float minValue, float maxValue, bool wholeNumbers)
    {
        RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, 34.0f));
        Image background = rectTransform.gameObject.AddComponent<Image>();
        background.color = new Color(0.16f, 0.16f, 0.18f, 1.0f);

        Slider slider = rectTransform.gameObject.AddComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = minValue;
        slider.maxValue = maxValue;
        slider.wholeNumbers = wholeNumbers;

        LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = 34.0f;
        layoutElement.minHeight = 34.0f;
        layoutElement.flexibleWidth = 1.0f;

        RectTransform fillArea = CreateRectTransform("Fill Area", rectTransform, Vector2.zero);
        fillArea.anchorMin = new Vector2(0.0f, 0.0f);
        fillArea.anchorMax = new Vector2(1.0f, 1.0f);
        fillArea.offsetMin = new Vector2(12.0f, 10.0f);
        fillArea.offsetMax = new Vector2(-12.0f, -10.0f);

        RectTransform fill = CreateRectTransform("Fill", fillArea, Vector2.zero);
        fill.anchorMin = new Vector2(0.0f, 0.0f);
        fill.anchorMax = new Vector2(1.0f, 1.0f);
        fill.offsetMin = Vector2.zero;
        fill.offsetMax = Vector2.zero;
        Image fillImage = fill.gameObject.AddComponent<Image>();
        fillImage.color = new Color(0.18f, 0.4f, 0.24f, 1.0f);

        RectTransform handleSlideArea = CreateRectTransform("Handle Slide Area", rectTransform, Vector2.zero);
        handleSlideArea.anchorMin = Vector2.zero;
        handleSlideArea.anchorMax = Vector2.one;
        handleSlideArea.offsetMin = new Vector2(12.0f, 10.0f);
        handleSlideArea.offsetMax = new Vector2(-12.0f, -10.0f);

        RectTransform handle = CreateRectTransform("Handle", handleSlideArea, new Vector2(8.0f, 12.0f));
        handle.anchorMin = new Vector2(0.0f, 0.5f);
        handle.anchorMax = new Vector2(0.0f, 0.5f);
        handle.pivot = new Vector2(0.5f, 0.5f);
        Image handleImage = handle.gameObject.AddComponent<Image>();
        handleImage.color = new Color(0.86f, 0.86f, 0.9f, 1.0f);

        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = handleImage;

        TryAddVrChatUiShape(rectTransform.gameObject);
        return slider;
    }

    static void EnsureEventSystemExists()
    {
        EventSystem[] eventSystems = Resources.FindObjectsOfTypeAll<EventSystem>();
        for (int i = 0; i < eventSystems.Length; i++)
        {
            EventSystem existingEventSystem = eventSystems[i];
            if (existingEventSystem == null)
            {
                continue;
            }

            GameObject existingEventSystemObject = existingEventSystem.gameObject;
            if (existingEventSystemObject == null || EditorUtility.IsPersistent(existingEventSystemObject))
            {
                continue;
            }

            return;
        }

        GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        Undo.RegisterCreatedObjectUndo(eventSystemObject, "Create EventSystem");
    }

    [ContextMenu("Generate UI")]
    void GenerateUI()
    {
        EnsureEventSystemExists();

        Transform existingUi = transform.Find("Gaussian Splat UI");
        if (existingUi != null)
        {
            Undo.DestroyObjectImmediate(existingUi.gameObject);
        }

        GameObject canvasObject = new GameObject("Gaussian Splat UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Undo.RegisterCreatedObjectUndo(canvasObject, "Create Gaussian Splat UI");
        canvasObject.transform.SetParent(transform, false);
        canvasObject.transform.localPosition = new Vector3(0.0f, 1.2f, 1.5f);
        canvasObject.transform.localRotation = Quaternion.identity;
        canvasObject.transform.localScale = Vector3.one * 0.0025f;

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
        scaler.dynamicPixelsPerUnit = 10.0f;

        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(1120.0f, 980.0f);

        TryAddVrChatUiShape(canvasObject);

        CreateOpaqueBackgroundPlate(canvasObject.transform, canvasRect.sizeDelta);

        GameObject panelObject = CreateVerticalGroup("Panel", canvasObject.transform, new RectOffset(12, 12, 10, 10), 10.0f, TextAnchor.UpperLeft);
        RectTransform panelRect = panelObject.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(1120.0f, 0.0f);

        GaussianSplatRendererUI generatedUi = AddGeneratedUdonSharpComponent<GaussianSplatRendererUI>(canvasObject, "Add Gaussian Splat Renderer UI");
        generatedUi.gaussianSplatRenderer = this;

        GameObject bodyRow = CreateHorizontalGroup("Body Row", panelObject.transform, 18.0f, false);
        SetPreferredHeight(bodyRow, 900.0f, 0.0f);

        GameObject settingsColumn = CreateVerticalGroup("Settings Column", bodyRow.transform, new RectOffset(0, 0, 0, 0), 12.0f, TextAnchor.UpperLeft);
        SetPreferredWidth(settingsColumn, 520.0f, 0.0f);

        GameObject splatColumn = CreateVerticalGroup("Splat Column", bodyRow.transform, new RectOffset(0, 0, 0, 0), 10.0f, TextAnchor.UpperLeft);
        SetPreferredWidth(splatColumn, 560.0f, 1.0f);

        CreateTextElement("Title", settingsColumn.transform, "VRChatGaussianSplatting", 22, TextAnchor.MiddleLeft, Color.white);
        CreateTextElement("Subtitle", settingsColumn.transform, "Github: https://github.com/MichaelMoroz/VRChatGaussianSplatting\nDeveloped by misha_m", 12, TextAnchor.MiddleLeft, new Color(0.82f, 0.82f, 0.82f, 1.0f));
        generatedUi.currentSplatText = CreateTextElement("Current Splat", settingsColumn.transform, "Current Splat (global): None", 16, TextAnchor.MiddleLeft, new Color(0.9f, 0.9f, 0.9f, 1.0f));

        CreateTextElement("Sorting Section", settingsColumn.transform, "Sorting Settings", 18, TextAnchor.MiddleLeft, Color.white);

        GameObject minSortDistanceRow = CreateHorizontalGroup("Min Sort Distance Row", settingsColumn.transform, 8.0f, false);
        Text minSortDistanceLabel = CreateTextElement("Min Sort Distance Label", minSortDistanceRow.transform, "Min Sort Dist", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(minSortDistanceLabel.gameObject, 210.0f, 1.0f);
        Button minSortDistanceDownButton = CreateButtonElement("Min Sort Distance Down", minSortDistanceRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.minSortDistanceText = CreateTextElement("Min Sort Distance Value", minSortDistanceRow.transform, "0", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.minSortDistanceText.gameObject, 72.0f, 0.0f);
        Button minSortDistanceUpButton = CreateButtonElement("Min Sort Distance Up", minSortDistanceRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(minSortDistanceDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseMinSortDistance));
        AddUdonSharpButtonEvent(minSortDistanceUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseMinSortDistance));

        GameObject maxSortDistanceRow = CreateHorizontalGroup("Max Sort Distance Row", settingsColumn.transform, 8.0f, false);
        Text maxSortDistanceLabel = CreateTextElement("Max Sort Distance Label", maxSortDistanceRow.transform, "Max Sort Dist", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(maxSortDistanceLabel.gameObject, 210.0f, 1.0f);
        Button maxSortDistanceDownButton = CreateButtonElement("Max Sort Distance Down", maxSortDistanceRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.maxSortDistanceText = CreateTextElement("Max Sort Distance Value", maxSortDistanceRow.transform, "150", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.maxSortDistanceText.gameObject, 72.0f, 0.0f);
        Button maxSortDistanceUpButton = CreateButtonElement("Max Sort Distance Up", maxSortDistanceRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(maxSortDistanceDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseMaxSortDistance));
        AddUdonSharpButtonEvent(maxSortDistanceUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseMaxSortDistance));

        GameObject cameraQuantizationRow = CreateHorizontalGroup("Camera Quantization Row", settingsColumn.transform, 8.0f, false);
        Text cameraQuantizationLabel = CreateTextElement("Camera Quantization Label", cameraQuantizationRow.transform, "Camera Quant", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(cameraQuantizationLabel.gameObject, 210.0f, 1.0f);
        Button cameraQuantizationDownButton = CreateButtonElement("Camera Quantization Down", cameraQuantizationRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.cameraQuantizationText = CreateTextElement("Camera Quantization Value", cameraQuantizationRow.transform, "0.1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.cameraQuantizationText.gameObject, 72.0f, 0.0f);
        Button cameraQuantizationUpButton = CreateButtonElement("Camera Quantization Up", cameraQuantizationRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(cameraQuantizationDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseCameraQuantization));
        AddUdonSharpButtonEvent(cameraQuantizationUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseCameraQuantization));

        GameObject sortingStepsRow = CreateHorizontalGroup("Sorting Steps Row", settingsColumn.transform, 8.0f, false);
        Text sortingStepsLabel = CreateTextElement("Sorting Steps Label", sortingStepsRow.transform, "Sorting Steps", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(sortingStepsLabel.gameObject, 210.0f, 1.0f);
        Button sortingStepsDownButton = CreateButtonElement("Sorting Steps Down", sortingStepsRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.sortingStepsText = CreateTextElement("Sorting Steps Value", sortingStepsRow.transform, "4", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.sortingStepsText.gameObject, 72.0f, 0.0f);
        Button sortingStepsUpButton = CreateButtonElement("Sorting Steps Up", sortingStepsRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(sortingStepsDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseSortingSteps));
        AddUdonSharpButtonEvent(sortingStepsUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseSortingSteps));

        GameObject alwaysUpdateRow = CreateHorizontalGroup("Sort Every Frame Row", settingsColumn.transform, 8.0f, false);
        Text alwaysUpdateLabel = CreateTextElement("Sort Every Frame Label", alwaysUpdateRow.transform, "Sort every frame", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(alwaysUpdateLabel.gameObject, 210.0f, 1.0f);
        Button alwaysUpdateButton = CreateButtonElement("Sort Every Frame Button", alwaysUpdateRow.transform, "Off", new Color(0.3f, 0.16f, 0.14f, 1.0f), 72.0f, 0.0f);
        generatedUi.alwaysUpdateButton = alwaysUpdateButton;
        AddUdonSharpButtonEvent(alwaysUpdateButton, generatedUi, nameof(GaussianSplatRendererUI.ToggleAlwaysUpdate));

        CreateTextElement("Settings Section", settingsColumn.transform, "Material Settings", 18, TextAnchor.MiddleLeft, Color.white);

        GameObject shBandRow = CreateHorizontalGroup("SH Band Row", settingsColumn.transform, 8.0f, false);
        Text shBandLabel = CreateTextElement("SH Band Label", shBandRow.transform, "SH Band (global)", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(shBandLabel.gameObject, 210.0f, 0.0f);
        generatedUi.shBandSlider = CreateSliderElement("SH Band Slider", shBandRow.transform, 0.0f, 3.0f, true);
        generatedUi.shBandText = CreateTextElement("SH Band Value", shBandRow.transform, "3", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.shBandText.gameObject, 72.0f, 0.0f);

        GameObject vrcLightVolumesRow = CreateHorizontalGroup("VRC Light Volumes Row", settingsColumn.transform, 8.0f, false);
        Text vrcLightVolumesLabel = CreateTextElement("VRC Light Volumes Label", vrcLightVolumesRow.transform, "VRC Light Volumes (global)", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(vrcLightVolumesLabel.gameObject, 210.0f, 1.0f);
        Button vrcLightVolumesButton = CreateButtonElement("VRC Light Volumes Button", vrcLightVolumesRow.transform, "Off", new Color(0.3f, 0.16f, 0.14f, 1.0f), 72.0f, 0.0f);
        generatedUi.vrcLightVolumesButton = vrcLightVolumesButton;
        AddUdonSharpButtonEvent(vrcLightVolumesButton, generatedUi, nameof(GaussianSplatRendererUI.ToggleVrcLightVolumes));

        GameObject lightVolumeIntensityRow = CreateHorizontalGroup("Light Volume Intensity Row", settingsColumn.transform, 8.0f, false);
        Text lightVolumeIntensityLabel = CreateTextElement("Light Volume Intensity Label", lightVolumeIntensityRow.transform, "Light Volume Intensity", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(lightVolumeIntensityLabel.gameObject, 210.0f, 0.0f);
        generatedUi.lightVolumeIntensitySlider = CreateSliderElement("Light Volume Intensity Slider", lightVolumeIntensityRow.transform, 0.0f, 4.0f, false);
        generatedUi.lightVolumeIntensityText = CreateTextElement("Light Volume Intensity Value", lightVolumeIntensityRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.lightVolumeIntensityText.gameObject, 72.0f, 0.0f);

        GameObject antiAliasingRow = CreateHorizontalGroup("AntiAliasing Row", settingsColumn.transform, 8.0f, false);
        Text antiAliasingLabel = CreateTextElement("AntiAliasing Label", antiAliasingRow.transform, "Antialiasing", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(antiAliasingLabel.gameObject, 210.0f, 0.0f);
        generatedUi.antiAliasingSlider = CreateSliderElement("AntiAliasing Slider", antiAliasingRow.transform, 0.0f, 3.0f, false);
        generatedUi.antiAliasingText = CreateTextElement("AntiAliasing Value", antiAliasingRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.antiAliasingText.gameObject, 72.0f, 0.0f);

        GameObject gaussianScaleRow = CreateHorizontalGroup("Gaussian Scale Row", settingsColumn.transform, 8.0f, false);
        Text gaussianScaleLabel = CreateTextElement("Gaussian Scale Label", gaussianScaleRow.transform, "Gaussian Scale (global)", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(gaussianScaleLabel.gameObject, 210.0f, 1.0f);
        Button gaussianScaleDownButton = CreateButtonElement("Gaussian Scale Down", gaussianScaleRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.gaussianScaleText = CreateTextElement("Gaussian Scale Value", gaussianScaleRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.gaussianScaleText.gameObject, 72.0f, 0.0f);
        Button gaussianScaleUpButton = CreateButtonElement("Gaussian Scale Up", gaussianScaleRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(gaussianScaleDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseGaussianScale));
        AddUdonSharpButtonEvent(gaussianScaleUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseGaussianScale));

        GameObject alphaCutoffRow = CreateHorizontalGroup("Alpha Cutoff Row", settingsColumn.transform, 8.0f, false);
        Text alphaCutoffLabel = CreateTextElement("Alpha Cutoff Label", alphaCutoffRow.transform, "Alpha Cutoff\n(lower = better quality)", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(alphaCutoffLabel.gameObject, 210.0f, 0.0f);
        generatedUi.alphaCutoffSlider = CreateSliderElement("Alpha Cutoff Slider", alphaCutoffRow.transform, 0.005f, 0.1f, false);
        generatedUi.alphaCutoffText = CreateTextElement("Alpha Cutoff Value", alphaCutoffRow.transform, "0.03", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.alphaCutoffText.gameObject, 72.0f, 0.0f);

        const float splatListPanelHeight = 840.0f;
        const float splatListPanelSpacing = 8.0f;
        const float splatListPanelPadding = 8.0f;
        const float splatScrollButtonHeight = 38.0f;
        const float splatSlotButtonHeight = 42.0f;

        CreateTextElement("Splat Section", splatColumn.transform, "Splat Selection (global)", 18, TextAnchor.MiddleLeft, Color.white);
        GameObject splatListPanel = CreateVerticalGroup("Splat List Panel", splatColumn.transform, new RectOffset(8, 8, 8, 8), 8.0f, TextAnchor.UpperLeft);
        Image splatListPanelImage = splatListPanel.AddComponent<Image>();
        splatListPanelImage.color = new Color(0.09f, 0.09f, 0.11f, 1.0f);
        SetPreferredHeight(splatListPanel, splatListPanelHeight, 0.0f);

        GameObject splatScrollRow = CreateHorizontalGroup("Splat Scroll Controls", splatListPanel.transform, 8.0f, false);
        Button scrollUpButton = CreateButtonElement("Splat Scroll Up", splatScrollRow.transform, "Up", new Color(0.15f, 0.24f, 0.36f, 1.0f), 96.0f, 0.0f);
        Button scrollDownButton = CreateButtonElement("Splat Scroll Down", splatScrollRow.transform, "Down", new Color(0.15f, 0.24f, 0.36f, 1.0f), 96.0f, 0.0f);
        generatedUi.splatScrollUpButton = scrollUpButton;
        generatedUi.splatScrollDownButton = scrollDownButton;
        AddUdonSharpButtonEvent(scrollUpButton, generatedUi, nameof(GaussianSplatRendererUI.ScrollSplatListUp));
        AddUdonSharpButtonEvent(scrollDownButton, generatedUi, nameof(GaussianSplatRendererUI.ScrollSplatListDown));

        GameObject splatButtonContainer = CreateVerticalGroup("Splat Button Container", splatListPanel.transform, new RectOffset(0, 0, 0, 0), 8.0f, TextAnchor.UpperLeft);

        List<Button> splatButtons = new List<Button>();
        List<int> splatButtonIndices = new List<int>();
        List<string> splatButtonLabels = new List<string>();

        string[] slotSelectEventNames = new string[]
        {
            nameof(GaussianSplatRendererUI.SelectSplatSlot0),
            nameof(GaussianSplatRendererUI.SelectSplatSlot1),
            nameof(GaussianSplatRendererUI.SelectSplatSlot2),
            nameof(GaussianSplatRendererUI.SelectSplatSlot3),
            nameof(GaussianSplatRendererUI.SelectSplatSlot4),
            nameof(GaussianSplatRendererUI.SelectSplatSlot5),
            nameof(GaussianSplatRendererUI.SelectSplatSlot6),
            nameof(GaussianSplatRendererUI.SelectSplatSlot7),
            nameof(GaussianSplatRendererUI.SelectSplatSlot8),
            nameof(GaussianSplatRendererUI.SelectSplatSlot9),
            nameof(GaussianSplatRendererUI.SelectSplatSlot10),
            nameof(GaussianSplatRendererUI.SelectSplatSlot11),
            nameof(GaussianSplatRendererUI.SelectSplatSlot12),
            nameof(GaussianSplatRendererUI.SelectSplatSlot13),
            nameof(GaussianSplatRendererUI.SelectSplatSlot14),
            nameof(GaussianSplatRendererUI.SelectSplatSlot15),
        };

        float availableSplatButtonHeight = splatListPanelHeight - (splatListPanelPadding * 2.0f) - splatScrollButtonHeight - splatListPanelSpacing;
        int visibleSplatButtonCount = Mathf.Max(1, Mathf.FloorToInt((availableSplatButtonHeight + splatListPanelSpacing) / (splatSlotButtonHeight + splatListPanelSpacing)));
        visibleSplatButtonCount = Mathf.Min(visibleSplatButtonCount, slotSelectEventNames.Length);

        for (int slotIndex = 0; slotIndex < visibleSplatButtonCount; slotIndex++)
        {
            Button slotButton = CreateButtonElement("Splat Slot " + slotIndex, splatButtonContainer.transform, "", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            SetPreferredHeight(slotButton.gameObject, splatSlotButtonHeight, 0.0f);
            splatButtons.Add(slotButton);
            AddUdonSharpButtonEvent(slotButton, generatedUi, slotSelectEventNames[slotIndex]);
        }


        if (!HasSplatObjects())
        {
            generatedUi.splatButtonIndices = new int[0];
            generatedUi.splatButtonLabels = new string[0];
        }
        else
        {
            for (int i = 0; i < splatObjects.Length; i++)
            {
                GameObject listedSplatObject = splatObjects[i];
                if (listedSplatObject == null)
                {
                    continue;
                }

                splatButtonIndices.Add(i);
                splatButtonLabels.Add(listedSplatObject.name);
            }

            generatedUi.splatButtonIndices = splatButtonIndices.ToArray();
            generatedUi.splatButtonLabels = splatButtonLabels.ToArray();
        }

        generatedUi.splatButtons = splatButtons.ToArray();

        generatedUi.RefreshUI();
        EditorUtility.SetDirty(canvasObject);
        EditorUtility.SetDirty(this);
        EditorUtility.SetDirty(generatedUi);
        UdonBehaviour generatedUiBacking = GetBackingUdonBehaviour(generatedUi);
        if (generatedUiBacking != null)
        {
            EditorUtility.SetDirty(generatedUiBacking);
        }
        Selection.activeGameObject = canvasObject;
    }

    [ContextMenu("Update Sorting Resource Textures")]
    void UpdateSortingResourceTextures()
    {
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return;
        }

        if (!HasSplatObjects())
        {
            Debug.LogError("No splat objects have been assigned to the GaussianSplatRenderer.");
            return;
        }

        int largestElementCount = 0;
        string largestSplatName = null;
        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject currentSplatObject = splatObjects[i];
            if (currentSplatObject == null)
            {
                continue;
            }

            Texture positionsTexture = GetPositionsTexture(currentSplatObject);
            if (positionsTexture == null)
            {
                continue;
            }

            int elementCount = positionsTexture.width * positionsTexture.height;
            if (elementCount > largestElementCount)
            {
                largestElementCount = elementCount;
                largestSplatName = currentSplatObject.name;
            }
        }

        if (largestElementCount <= 0)
        {
            Debug.LogError("No valid _GS_Positions textures were found on the assigned splat objects.");
            return;
        }

        ComputeRequiredSortTextureSize(largestElementCount, out int requiredWidth, out int requiredHeight);

        string resourceFolderPath = GetSortResourceFolderPath();
        string assetPrefix = SanitizeAssetName(name);

        Undo.RecordObject(this, "Update Gaussian Splat Sorting Resources");
        Undo.RecordObject(radixSort, "Update Gaussian Splat Sorting Resources");

        EnsureSortRenderTexture(ref radixSort.keyValues0, resourceFolderPath, assetPrefix + "_KeyValues0", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        EnsureSortRenderTexture(ref radixSort.keyValues1, resourceFolderPath, assetPrefix + "_KeyValues1", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        EnsureSortRenderTexture(ref radixSort.prefixSums, resourceFolderPath, assetPrefix + "_PrefixSums", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, true, 1);
        EnsureSortRenderTexture(ref splatRenderOrder, resourceFolderPath, assetPrefix + "_SplatRenderOrder", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 2);

        EditorUtility.SetDirty(radixSort);
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssets();

        Debug.Log($"Updated sorting textures to {requiredWidth}x{requiredHeight} for largest splat '{largestSplatName}' ({largestElementCount} padded elements).");
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
