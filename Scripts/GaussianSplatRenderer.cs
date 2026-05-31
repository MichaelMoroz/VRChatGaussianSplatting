using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
#endif

namespace GaussianSplatting
{

public enum GaussianSplatRenderingMode
{
    SingleSplat = 0,
    CombineAllSplats = 1,
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 2;
    const int COMBINED_SOURCE_BATCH_SIZE = 8;
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;
    const int DEFAULT_COMBINED_SPLATS_PER_PASS = 3 * 256 * 1024;
    const int DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT = 1;
    const int SCREEN_CAMERA_ID = 0;
    const int PHOTO_CAMERA_ID = 1;
    const int NO_ACTIVE_SORT = -1;
    const float DEFAULT_ALPHA_CUTOFF = 0.03f;

    Vector3[] _completedCameraPos;
    Vector3[] _pendingCameraPos;
    Vector3[] _pendingCameraWorldPos;
    bool[] _hasCompletedSort;
    bool[] _hasPendingSort;
    int _activeSortCameraId = NO_ACTIVE_SORT;
    Vector3 _activeSortQuantizedPos = Vector3.positiveInfinity;
    RadixSort _radixSort;
    Material keyValueMat;
    MeshRenderer _sortedRenderer;

    GaussianSplatObject[] _sceneSplats = new GaussianSplatObject[0];
    int _currentSourceIndex = -1;
    bool _runtimeCacheValid;

    int _combinedActualSplatCount;

    [HideInInspector, SerializeField] GameObject[] cachedSceneSplatObjects;
    [SerializeField] GaussianSplatRenderingMode renderingMode = GaussianSplatRenderingMode.SingleSplat;
    [SerializeField] MeshRenderer combinedSortedRenderer;
    [SerializeField] Material combineDataMaterial;
    [SerializeField] RenderTexture combinedPositions;
    [SerializeField] RenderTexture combinedRotations;
    [SerializeField] RenderTexture combinedScales;
    [SerializeField] RenderTexture combinedColorsCamera;
    [SerializeField] RenderTexture combinedColorsScratch;

    [Header("Render Settings")]
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    [Tooltip("If true, the splat render order will be updated every frame. Useful for animated splats. If false, it will only update when the camera position changes.")]
    [SerializeField] bool alwaysUpdate;
    [Tooltip("Number of frames used to pipeline the 8 radix sort subpasses. 1 sorts fully in one frame; 8 runs one subpass per frame.")]
    [Range(1, 8)] [SerializeField] int sortPipelineFrames = 2;
    [Tooltip("Render texture array used to store sorted splat render order. Slice 0 is screen, slice 1 is photo.")]
    public RenderTexture splatRenderOrder;

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [UdonSynced, SerializeField] public bool overrideMaterialProperties;
    [UdonSynced, Range(0, 3)] [SerializeField] int requestedSHBand = 3;
    [UdonSynced, Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] float thinThreshold = 0.005f;
    [Range(0.0f, 3.0f)] [SerializeField] float antiAliasing = 1.0f;
    [Range(-20.0f, 10.0f)] [SerializeField] float log2MinScale = -15.0f;
    [Range(0.005f, 0.3f)] [SerializeField] public float alphaCutoff = DEFAULT_ALPHA_CUTOFF;
    [Range(0.0f, 100.0f)] [SerializeField] float scaleCutoff = 100.0f;
    [Range(0.0f, 5.0f)] [SerializeField] float exposure = 1.0f;
    [Range(0.0f, 5.0f)] [SerializeField] float opacity = 1.0f;
    [SerializeField] Vector3 oklchShift = Vector3.zero;
    [SerializeField] float gamma = 1.0f;
    [UdonSynced, SerializeField] bool useVrcLightVolumes;
    [Range(0.0f, 4.0f)] [SerializeField] float lightVolumeIntensity = 1.0f;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static bool _editorRefreshQueued = true;
#endif

    void ResetCameraPositions()
    {
        if (_completedCameraPos == null || _completedCameraPos.Length < MAX_CAMERA_COUNT)
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

    void ResetRuntimeCache()
    {
        _runtimeCacheValid = false;
        _sceneSplats = new GaussianSplatObject[0];
        _currentSourceIndex = -1;
        _combinedActualSplatCount = 0;
        _sortedRenderer = null;
        ResetCameraPositions();
    }

    static int ResolveActualSplatCount(Material material, Texture positionsTexture)
    {
        if (material == null || positionsTexture == null)
        {
            return 0;
        }
        int textureElementCount = positionsTexture.width * positionsTexture.height;
        int actualSplatCount = material.HasProperty("_ActualSplatCount") ? material.GetInt("_ActualSplatCount") : 0;
        return actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
    }

    static int ComputeTextureCoordShift(int width)
    {
        int shift = 0;
        width = Mathf.Max(1, width);
        while (width > 1)
        {
            width >>= 1;
            shift++;
        }
        return shift;
    }

    static Material ResolvePrimarySplatMaterial(Material[] materials)
    {
        if (materials == null)
        {
            return null;
        }
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material != null && material.HasProperty("_GS_Positions"))
            {
                return material;
            }
        }
        return null;
    }

    static bool TryGetSplatSource(GaussianSplatObject splat, out MeshRenderer renderer, out Material primaryMaterial, out Texture positions, out int count)
    {
        renderer = splat != null ? splat.GetSortedRenderer() : null;
        primaryMaterial = ResolvePrimarySplatMaterial(renderer != null ? renderer.sharedMaterials : null);
        positions = primaryMaterial != null ? primaryMaterial.GetTexture("_GS_Positions") : null;
        count = ResolveActualSplatCount(primaryMaterial, positions);
        return renderer != null && primaryMaterial != null && positions != null && count > 0;
    }

    static bool TryGetCombinedChunkBinding(Transform child, out MeshRenderer renderer, out int offset)
    {
        renderer = child != null ? child.GetComponent<MeshRenderer>() : null;
        Material primaryMaterial = ResolvePrimarySplatMaterial(renderer != null ? renderer.sharedMaterials : null);
        offset = primaryMaterial != null && primaryMaterial.HasProperty("_SplatOffset") ? primaryMaterial.GetInt("_SplatOffset") : 0;
        return renderer != null && primaryMaterial != null && primaryMaterial.HasProperty("_SplatCount");
    }

    Material[] GetRendererMaterialsForRead(MeshRenderer renderer)
    {
        if (renderer == null)
        {
            return new Material[0];
        }
        Material[] materials = renderer.sharedMaterials;
        return materials ?? new Material[0];
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

    void RefreshRuntimeCache()
    {
        GameObject[] roots = cachedSceneSplatObjects;
        if (roots == null || roots.Length == 0)
        {
            ResetRuntimeCache();
            _runtimeCacheValid = true;
            return;
        }
        MeshRenderer ignoredRenderer;
        Material ignoredMaterial;
        Texture ignoredPositions;
        int ignoredCount;
        int validCount = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            GaussianSplatObject splat = root != null ? root.GetComponent<GaussianSplatObject>() : null;
            if (TryGetSplatSource(splat, out ignoredRenderer, out ignoredMaterial, out ignoredPositions, out ignoredCount))
            {
                validCount++;
            }
        }
        _sceneSplats = new GaussianSplatObject[validCount];
        int writeIndex = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            GaussianSplatObject splat = root != null ? root.GetComponent<GaussianSplatObject>() : null;
            if (!TryGetSplatSource(splat, out ignoredRenderer, out ignoredMaterial, out ignoredPositions, out ignoredCount))
            {
                continue;
            }
            _sceneSplats[writeIndex] = splat;
            writeIndex++;
        }
        _currentSourceIndex = FindFirstActiveSourceIndex();
        _runtimeCacheValid = true;
    }

    int FindFirstActiveSourceIndex() { for (int i = 0; i < _sceneSplats.Length; i++) if (_sceneSplats[i] != null && _sceneSplats[i].gameObject.activeInHierarchy) return i; return -1; }
    bool IsSourceActive(int index) { return index >= 0 && index < _sceneSplats.Length && _sceneSplats[index] != null && _sceneSplats[index].gameObject.activeInHierarchy; }
    GaussianSplatObject GetCurrentSplat() { return IsSourceActive(_currentSourceIndex) ? _sceneSplats[_currentSourceIndex] : null; }

    int FindSourceIndex(GaussianSplatObject splat)
    {
        if (splat == null)
        {
            return -1;
        }
        for (int i = 0; i < _sceneSplats.Length; i++)
        {
            if (_sceneSplats[i] == splat)
            {
                return i;
            }
        }
        return -1;
    }

    void RegisterRuntimeSplatObject(GaussianSplatObject splat)
    {
        if (splat == null || !TryGetSplatSource(splat, out MeshRenderer renderer, out Material primaryMaterial, out Texture positions, out int count))
        {
            return;
        }
        GameObject root = splat.gameObject;
        int cachedCount = cachedSceneSplatObjects != null ? cachedSceneSplatObjects.Length : 0;
        bool hasCachedRoot = false;
        for (int i = 0; i < cachedCount; i++)
        {
            if (cachedSceneSplatObjects[i] == root)
            {
                hasCachedRoot = true;
                break;
            }
        }
        if (!hasCachedRoot)
        {
            GameObject[] roots = new GameObject[cachedCount + 1];
            for (int i = 0; i < cachedCount; i++)
            {
                roots[i] = cachedSceneSplatObjects[i];
            }
            roots[cachedCount] = root;
            cachedSceneSplatObjects = roots;
        }
        if (FindSourceIndex(splat) >= 0)
        {
            return;
        }
        GaussianSplatObject[] sceneSplats = new GaussianSplatObject[_sceneSplats.Length + 1];
        for (int i = 0; i < _sceneSplats.Length; i++)
        {
            sceneSplats[i] = _sceneSplats[i];
        }
        sceneSplats[_sceneSplats.Length] = splat;
        _sceneSplats = sceneSplats;
        if (_currentSourceIndex < 0)
        {
            _currentSourceIndex = _sceneSplats.Length - 1;
        }
    }

    bool EnsureCurrentSourceSelected()
    {
        if (IsCombinedRenderingMode())
        {
            return true;
        }
        if (IsSourceActive(_currentSourceIndex))
        {
            return true;
        }
        int nextIndex = FindFirstActiveSourceIndex();
        if (nextIndex != _currentSourceIndex)
        {
            _currentSourceIndex = nextIndex;
            ResetCameraPositions();
        }
        return nextIndex >= 0;
    }

    void SetCombinedRendererEnabled(bool enabled)
    {
        if (combinedSortedRenderer == null)
        {
            return;
        }
        if (combinedSortedRenderer.enabled != enabled)
        {
            combinedSortedRenderer.enabled = enabled;
        }
        if (combinedSortedRenderer.gameObject.activeSelf != enabled)
        {
            combinedSortedRenderer.gameObject.SetActive(enabled);
        }
    }

    void UpdateSourceVisibility()
    {
        int mode = (int)renderingMode;
        int visibleIndex = mode == (int)GaussianSplatRenderingMode.CombineAllSplats ? -1 : (EnsureCurrentSourceSelected() ? _currentSourceIndex : -1);
        for (int i = 0; i < _sceneSplats.Length; i++)
        {
            GaussianSplatObject splat = _sceneSplats[i];
            MeshRenderer renderer = splat != null ? splat.GetSortedRenderer() : null;
            bool enabled = i == visibleIndex;
            if (renderer != null && renderer.enabled != enabled)
            {
                splat.SetSortedRendererEnabled(enabled);
            }
        }
        if (visibleIndex >= 0)
        {
            _sceneSplats[visibleIndex].ShowSorted();
            SetCombinedRendererEnabled(false);
        }
    }

    void EnsureLocalOwnership() { if (Networking.LocalPlayer != null) Networking.SetOwner(Networking.LocalPlayer, gameObject); }
    void RequestSyncedStateUpdate() { if (Networking.LocalPlayer != null) RequestSerialization(); }
    public bool IsCombinedRenderingMode() { return renderingMode == GaussianSplatRenderingMode.CombineAllSplats; }
    bool ShouldAlwaysUpdate(bool useEditorOps) { return useEditorOps || IsCombinedRenderingMode() || alwaysUpdate; }

    void ApplyConfiguredMaterialSettings(Material material, int currentSHBand)
    {
        if (material == null)
        {
            return;
        }
        if (material.HasProperty("_SHBand")) material.SetFloat("_SHBand", Mathf.Clamp(currentSHBand, 0, 3));
        if (useVrcLightVolumes) material.EnableKeyword("_VRC_LIGHT_VOLUMES_ON");
        else material.DisableKeyword("_VRC_LIGHT_VOLUMES_ON");
        if (material.HasProperty("_LightVolumeIntensity")) material.SetFloat("_LightVolumeIntensity", lightVolumeIntensity);
        if (!overrideMaterialProperties)
        {
            return;
        }
        if (material.HasProperty("_GaussianMul")) material.SetFloat("_GaussianMul", gaussianScale);
        if (material.HasProperty("_ThinThreshold")) material.SetFloat("_ThinThreshold", thinThreshold);
        if (material.HasProperty("_AntiAliasing")) material.SetFloat("_AntiAliasing", antiAliasing);
        if (material.HasProperty("_Log2MinScale")) material.SetFloat("_Log2MinScale", log2MinScale);
        if (material.HasProperty("_AlphaCutoff")) material.SetFloat("_AlphaCutoff", alphaCutoff);
        if (material.HasProperty("_ScaleCutoff")) material.SetFloat("_ScaleCutoff", scaleCutoff);
        if (material.HasProperty("_Exposure")) material.SetFloat("_Exposure", exposure);
        if (material.HasProperty("_Opacity")) material.SetFloat("_Opacity", opacity);
        if (material.HasProperty("_OKLCHShift")) material.SetVector("_OKLCHShift", new Vector4(oklchShift.x, oklchShift.y, oklchShift.z, 0.0f));
        if (material.HasProperty("_Gamma")) material.SetFloat("_Gamma", Mathf.Max(0.001f, gamma));
    }

    void ApplyMaterialSettingsToSelectedObject()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return;
        }
#endif

        if (IsCombinedRenderingMode())
        {
            if (combinedSortedRenderer == null)
            {
                return;
            }
            Material[] combinedMaterials = GetRendererMaterialsForWrite(combinedSortedRenderer);
            for (int i = 0; i < combinedMaterials.Length; i++)
            {
                ApplyConfiguredMaterialSettings(combinedMaterials[i], 0);
            }
            Transform combinedRoot = combinedSortedRenderer.transform;
            for (int childIndex = 0; childIndex < combinedRoot.childCount; childIndex++)
            {
                if (!TryGetCombinedChunkBinding(combinedRoot.GetChild(childIndex), out MeshRenderer chunkRenderer, out int chunkOffset))
                {
                    continue;
                }
                Material[] chunkMaterials = GetRendererMaterialsForWrite(chunkRenderer);
                for (int materialIndex = 0; materialIndex < chunkMaterials.Length; materialIndex++)
                {
                    ApplyConfiguredMaterialSettings(chunkMaterials[materialIndex], 0);
                }
            }
            return;
        }
        if (!EnsureCurrentSourceSelected())
        {
            return;
        }
        GaussianSplatObject splat = GetCurrentSplat();
        Material[] materials = GetRendererMaterialsForWrite(splat != null ? splat.GetSortedRenderer() : null);
        int shBand = Mathf.Clamp(requestedSHBand, 0, splat != null ? splat.GetMaxSHBand() : 0);
        for (int i = 0; i < materials.Length; i++)
        {
            ApplyConfiguredMaterialSettings(materials[i], shBand);
        }
    }

    public int GetSelectedSplatMaxSHBand() { GaussianSplatObject splat = !EnsureInitialized() || IsCombinedRenderingMode() || !EnsureCurrentSourceSelected() ? null : GetCurrentSplat(); return splat != null ? splat.GetMaxSHBand() : 0; }
    public int GetCurrentSHBand() { return Mathf.Clamp(requestedSHBand, 0, GetSelectedSplatMaxSHBand()); }
    public void SetSHBand(int value) { EnsureLocalOwnership(); requestedSHBand = Mathf.Clamp(value, 0, 3); ApplyMaterialSettingsToSelectedObject(); RequestSyncedStateUpdate(); }
    public float GetCameraPositionQuantization() { return cameraPositionQuantization; }
    public void SetCameraPositionQuantization(float value) { cameraPositionQuantization = Mathf.Max(0.0f, value); ResetCameraPositions(); }
    public int GetSortPipelineFrames() { return sortPipelineFrames; }
    public void SetSortPipelineFrames(int value) { sortPipelineFrames = Mathf.Clamp(value, 1, 8); ResetCameraPositions(); }
    public bool GetAlwaysUpdate() { return IsCombinedRenderingMode() || alwaysUpdate; }
    public void SetAlwaysUpdate(bool value) { alwaysUpdate = value; ResetCameraPositions(); }
    public void ToggleAlwaysUpdate() { SetAlwaysUpdate(!alwaysUpdate); }
    public bool GetUseVrcLightVolumes() { return useVrcLightVolumes; }
    public void SetUseVrcLightVolumes(bool value) { EnsureLocalOwnership(); useVrcLightVolumes = value; ApplyMaterialSettingsToSelectedObject(); RequestSyncedStateUpdate(); }
    public void ToggleVrcLightVolumes() { SetUseVrcLightVolumes(!useVrcLightVolumes); }
    public float GetAntiAliasing() { return antiAliasing; }
    public void SetAntiAliasing(float value) { overrideMaterialProperties = true; antiAliasing = Mathf.Clamp(value, 0.0f, 3.0f); ApplyMaterialSettingsToSelectedObject(); }
    public float GetLightVolumeIntensity() { return lightVolumeIntensity; }
    public void SetLightVolumeIntensity(float value) { overrideMaterialProperties = true; lightVolumeIntensity = Mathf.Clamp(value, 0.0f, 4.0f); ApplyMaterialSettingsToSelectedObject(); }
    public void SetGaussianScale(float value) { EnsureLocalOwnership(); overrideMaterialProperties = true; gaussianScale = Mathf.Clamp(value, 0.0f, 2.0f); ApplyMaterialSettingsToSelectedObject(); RequestSyncedStateUpdate(); }
    public void SetAlphaCutoff(float value) { overrideMaterialProperties = true; alphaCutoff = Mathf.Clamp(value, 0.005f, 0.3f); ApplyMaterialSettingsToSelectedObject(); }

    public string GetCurrentSplatName()
    {
        if (!EnsureInitialized())
        {
            return "None";
        }
        if (IsCombinedRenderingMode())
        {
            return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject.name : "Combined";
        }
        GaussianSplatObject splat = EnsureCurrentSourceSelected() ? GetCurrentSplat() : null;
        return splat != null ? splat.name : "None";
    }

    public GameObject GetCurrentSplatObject()
    {
        if (!EnsureInitialized())
        {
            return null;
        }
        if (IsCombinedRenderingMode())
        {
            return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : null;
        }
        GaussianSplatObject splat = EnsureCurrentSourceSelected() ? GetCurrentSplat() : null;
        return splat != null ? splat.gameObject : null;
    }

    public void NotifySplatObjectEnabled(GaussianSplatObject activeSplatObject)
    {
        if (!EnsureInitialized() || activeSplatObject == null || !activeSplatObject.gameObject.activeInHierarchy)
        {
            return;
        }
        RegisterRuntimeSplatObject(activeSplatObject);
        if (IsCombinedRenderingMode())
        {
            ResetCameraPositions();
            return;
        }
        int sourceIndex = FindSourceIndex(activeSplatObject);
        if (sourceIndex < 0 || sourceIndex == _currentSourceIndex)
        {
            return;
        }
        _currentSourceIndex = sourceIndex;
        ResetCameraPositions();
        UpdateSourceVisibility();
        ApplyMaterialSettingsToSelectedObject();
    }

    void DisableMsaaInGame()
    {
        if (VRCCameraSettings.ScreenCamera != null)
        {
            VRCCameraSettings.ScreenCamera.AllowMSAA = false;
        }
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
            if (candidate != null && candidate.GetInstanceID() < primaryInstanceId)
            {
                primaryRenderer = candidate;
                primaryInstanceId = candidate.GetInstanceID();
            }
        }
        if (primaryRenderer == this)
        {
            return true;
        }
        Debug.LogError("Multiple GaussianSplatRenderer instances found. Only one renderer can be active in a scene.");
        return false;
#endif
    }

    bool EnsureRenderTextureCreated(RenderTexture renderTexture)
    {
        if (renderTexture == null || renderTexture.IsCreated())
        {
            return renderTexture != null;
        }
        renderTexture.Create();
        return renderTexture.IsCreated();
    }

    bool EnsureInitialized()
    {
        if (!IsPrimaryRendererInstance())
        {
            enabled = false;
            return false;
        }
        if (_radixSort == null)
        {
            _radixSort = GetComponent<RadixSort>();
        }
        if (_radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return false;
        }
        if (keyValueMat == null)
        {
            keyValueMat = _radixSort.computeKeyValues;
        }
        if (splatRenderOrder == null)
        {
            Debug.LogError("Splat Render Order texture is not assigned. Please assign a RenderTexture.");
            return false;
        }
        if (!EnsureRenderTextureCreated(splatRenderOrder)
            || !EnsureRenderTextureCreated(_radixSort.keyValues0)
            || !EnsureRenderTextureCreated(_radixSort.keyValues1)
            || !EnsureRenderTextureCreated(_radixSort.prefixSums)
            || (combinedPositions != null && !EnsureRenderTextureCreated(combinedPositions))
            || (combinedRotations != null && !EnsureRenderTextureCreated(combinedRotations))
            || (combinedScales != null && !EnsureRenderTextureCreated(combinedScales))
            || (combinedColorsCamera != null && !EnsureRenderTextureCreated(combinedColorsCamera))
            || (combinedColorsScratch != null && !EnsureRenderTextureCreated(combinedColorsScratch)))
        {
            Debug.LogError("Gaussian splat render textures could not be created at runtime.");
            return false;
        }
        if (_completedCameraPos == null || _completedCameraPos.Length < MAX_CAMERA_COUNT)
        {
            _completedCameraPos = new Vector3[MAX_CAMERA_COUNT];
            _pendingCameraPos = new Vector3[MAX_CAMERA_COUNT];
            _pendingCameraWorldPos = new Vector3[MAX_CAMERA_COUNT];
            _hasCompletedSort = new bool[MAX_CAMERA_COUNT];
            _hasPendingSort = new bool[MAX_CAMERA_COUNT];
            ResetCameraPositions();
        }
        if (!_runtimeCacheValid)
        {
            RefreshRuntimeCache();
            ApplyMaterialSettingsToSelectedObject();
        }
        return true;
    }

    Vector3 QuantizePosition(Vector3 position)
    {
        if (cameraPositionQuantization <= 0.0f)
        {
            return position;
        }
        return new Vector3(
            Mathf.Round(position.x / cameraPositionQuantization) * cameraPositionQuantization,
            Mathf.Round(position.y / cameraPositionQuantization) * cameraPositionQuantization,
            Mathf.Round(position.z / cameraPositionQuantization) * cameraPositionQuantization);
    }

    void SetRenderOrderOnMaterials(Material[] materials, int actualCount)
    {
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
            {
                continue;
            }
            if (material.HasProperty("_GS_RenderOrder")) material.SetTexture("_GS_RenderOrder", splatRenderOrder);
            if (material.HasProperty("_ActualSplatCount")) material.SetInt("_ActualSplatCount", actualCount);
        }
    }

    bool BindKeyValuePositions(Material sourceMaterial, Texture positions, int actualCount)
    {
        if (keyValueMat == null || sourceMaterial == null || positions == null)
        {
            return false;
        }
        _radixSort.elementCount = actualCount;
        keyValueMat.SetTexture("_GS_Positions", positions);
        keyValueMat.SetInt("_GS_Positions_CoordMask", sourceMaterial.GetInt("_GS_Positions_CoordMask"));
        keyValueMat.SetInt("_GS_Positions_CoordShift", sourceMaterial.GetInt("_GS_Positions_CoordShift"));
        return true;
    }

    bool UpdateSingleBinding()
    {
        if (!EnsureCurrentSourceSelected())
        {
            return false;
        }
        GaussianSplatObject splat = GetCurrentSplat();
        if (!TryGetSplatSource(splat, out _sortedRenderer, out Material primaryMaterial, out Texture positions, out int count) || !BindKeyValuePositions(primaryMaterial, positions, count))
        {
            return false;
        }
        SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(_sortedRenderer), count);
        return true;
    }

    bool UpdateCombinedBinding()
    {
        if (combinedSortedRenderer == null || combinedPositions == null || _combinedActualSplatCount <= 0)
        {
            SetCombinedRendererEnabled(false);
            return false;
        }
        _sortedRenderer = null;
        Transform combinedRoot = combinedSortedRenderer.transform;
        SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(combinedSortedRenderer), _combinedActualSplatCount);
        for (int i = 0; i < combinedRoot.childCount; i++)
        {
            if (!TryGetCombinedChunkBinding(combinedRoot.GetChild(i), out MeshRenderer chunkRenderer, out int offset))
            {
                continue;
            }
            bool shouldRender = _combinedActualSplatCount > offset;
            if (chunkRenderer.gameObject.activeSelf != shouldRender)
            {
                chunkRenderer.gameObject.SetActive(shouldRender);
            }
            if (chunkRenderer.enabled != shouldRender)
            {
                chunkRenderer.enabled = shouldRender;
            }
            SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(chunkRenderer), _combinedActualSplatCount);
            if (shouldRender && _sortedRenderer == null)
            {
                _sortedRenderer = chunkRenderer;
            }
        }
        Material primaryMaterial = ResolvePrimarySplatMaterial(GetRendererMaterialsForRead(_sortedRenderer));
        if (_sortedRenderer == null || primaryMaterial == null || !BindKeyValuePositions(primaryMaterial, combinedPositions, _combinedActualSplatCount))
        {
            SetCombinedRendererEnabled(false);
            return false;
        }
        return true;
    }

    bool UpdateSortBinding()
    {
        if (keyValueMat == null)
        {
            keyValueMat = _radixSort != null ? _radixSort.computeKeyValues : null;
        }
        if (keyValueMat == null)
        {
            Debug.LogError("ComputeKeyValues material is not assigned on the RadixSort component.");
            return false;
        }
        return IsCombinedRenderingMode() ? UpdateCombinedBinding() : UpdateSingleBinding();
    }

    void Blit(Texture source, RenderTexture target, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Graphics.Blit(source, target);
            return;
        }
#endif
        VRCGraphics.Blit(source, target);
    }

    void Blit(RenderTexture target, Material material, int pass, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Graphics.Blit(null, target, material, pass);
            return;
        }
#endif
        VRCGraphics.Blit(null, target, material, pass);
    }

    void CopyToCameraSlice(RenderTexture source, int slice, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Graphics.CopyTexture(source, 0, 0, combinedColorsCamera, slice, 0);
            return;
        }
#endif
        VRCGraphics.Blit(source, combinedColorsCamera, 0, slice);
    }

    void SetCombinedSourceSlot(int slot, int sourceIndex, int sourceOffset)
    {
        string suffix = slot.ToString();
        if (sourceIndex < 0)
        {
            combineDataMaterial.SetTexture("_GS_SourcePositions" + suffix, null);
            combineDataMaterial.SetTexture("_GS_SourceColors" + suffix, null);
            combineDataMaterial.SetTexture("_GS_SourceRotations" + suffix, null);
            combineDataMaterial.SetTexture("_GS_SourceScales" + suffix, null);
            combineDataMaterial.SetTexture("_GS_SourceSH" + suffix, null);
            combineDataMaterial.SetVector("_GS_SourceLayout" + suffix, Vector4.zero);
            combineDataMaterial.SetVector("_GS_SourceShLayout" + suffix, Vector4.zero);
            combineDataMaterial.SetVector("_GS_SourceDecode" + suffix, Vector4.zero);
            combineDataMaterial.SetVector("_GS_SourceShMin" + suffix, Vector4.zero);
            combineDataMaterial.SetVector("_GS_SourceShRange" + suffix, Vector4.one);
            combineDataMaterial.SetMatrix("_GS_SourceLocalToWorld" + suffix, Matrix4x4.identity);
            combineDataMaterial.SetMatrix("_GS_SourceWorldToLocal" + suffix, Matrix4x4.identity);
            return;
        }
        if (!TryGetSplatSource(_sceneSplats[sourceIndex], out MeshRenderer sourceRenderer, out Material sourceMaterial, out Texture positions, out int sourceCount))
        {
            SetCombinedSourceSlot(slot, -1, 0);
            return;
        }
        combineDataMaterial.SetTexture("_GS_SourcePositions" + suffix, positions);
        combineDataMaterial.SetTexture("_GS_SourceColors" + suffix, sourceMaterial.GetTexture("_GS_Colors"));
        combineDataMaterial.SetTexture("_GS_SourceRotations" + suffix, sourceMaterial.GetTexture("_GS_Rotations"));
        combineDataMaterial.SetTexture("_GS_SourceScales" + suffix, sourceMaterial.GetTexture("_GS_Scales"));
        combineDataMaterial.SetTexture("_GS_SourceSH" + suffix, sourceMaterial.GetTexture("_GS_SH"));
        combineDataMaterial.SetVector("_GS_SourceLayout" + suffix, new Vector4(
            sourceMaterial.HasProperty("_GS_Positions_CoordMask") ? sourceMaterial.GetInt("_GS_Positions_CoordMask") : 0,
            sourceMaterial.HasProperty("_GS_Positions_CoordShift") ? sourceMaterial.GetInt("_GS_Positions_CoordShift") : 0,
            sourceOffset,
            sourceCount));
        combineDataMaterial.SetVector("_GS_SourceShLayout" + suffix, new Vector4(
            sourceMaterial.HasProperty("_GS_SH_CoeffCount") ? sourceMaterial.GetInt("_GS_SH_CoeffCount") : 0,
            sourceMaterial.HasProperty("_GS_SH_CoeffStride") ? sourceMaterial.GetInt("_GS_SH_CoeffStride") : 0,
            sourceMaterial.HasProperty("_GS_SH_CoordMask") ? sourceMaterial.GetInt("_GS_SH_CoordMask") : 0,
            sourceMaterial.HasProperty("_GS_SH_CoordShift") ? sourceMaterial.GetInt("_GS_SH_CoordShift") : 0));
        combineDataMaterial.SetVector("_GS_SourceDecode" + suffix, new Vector4(
            sourceMaterial.HasProperty("_Log2MinScale") ? sourceMaterial.GetFloat("_Log2MinScale") : -15.0f,
            sourceMaterial.HasProperty("_Opacity") ? sourceMaterial.GetFloat("_Opacity") : 1.0f,
            sourceMaterial.HasProperty("_SHBand") ? sourceMaterial.GetFloat("_SHBand") : 0.0f,
            0.0f));
        combineDataMaterial.SetVector("_GS_SourceShMin" + suffix, sourceMaterial.HasProperty("_GS_SH_Min") ? sourceMaterial.GetVector("_GS_SH_Min") : Vector4.zero);
        combineDataMaterial.SetVector("_GS_SourceShRange" + suffix, sourceMaterial.HasProperty("_GS_SH_Range") ? sourceMaterial.GetVector("_GS_SH_Range") : Vector4.one);
        combineDataMaterial.SetMatrix("_GS_SourceLocalToWorld" + suffix, sourceRenderer != null ? sourceRenderer.transform.localToWorldMatrix : Matrix4x4.identity);
        combineDataMaterial.SetMatrix("_GS_SourceWorldToLocal" + suffix, sourceRenderer != null ? sourceRenderer.transform.worldToLocalMatrix : Matrix4x4.identity);
    }

    bool BindCombinedBatch(ref int sourceCursor, ref int combinedOffset, int positionCapacity, int colorCapacity)
    {
        MeshRenderer ignoredRenderer;
        Material ignoredMaterial;
        Texture ignoredPositions;
        int boundCount = 0;
        for (int slot = 0; slot < COMBINED_SOURCE_BATCH_SIZE; slot++)
        {
            while (sourceCursor < _sceneSplats.Length && !IsSourceActive(sourceCursor))
            {
                sourceCursor++;
            }
            if (sourceCursor >= _sceneSplats.Length)
            {
                SetCombinedSourceSlot(slot, -1, 0);
                continue;
            }
            if (!TryGetSplatSource(_sceneSplats[sourceCursor], out ignoredRenderer, out ignoredMaterial, out ignoredPositions, out int sourceCount))
            {
                sourceCursor++;
                slot--;
                continue;
            }
            if (combinedOffset + sourceCount > positionCapacity || combinedOffset + sourceCount > colorCapacity)
            {
                _combinedActualSplatCount = 0;
                SetCombinedRendererEnabled(false);
#if !UNITY_EDITOR || COMPILER_UDONSHARP
                Debug.LogError("Combined Gaussian splat resources are too small for the active scene splats. Refresh the renderer resources in the editor.");
#endif
                return false;
            }
            SetCombinedSourceSlot(slot, sourceCursor, combinedOffset);
            combinedOffset += sourceCount;
            sourceCursor++;
            boundCount++;
        }
        return boundCount > 0;
    }

    bool UpdateCombinedTextures(Vector3 screenCameraPos, Vector3 photoCameraPos, bool useEditorOps)
    {
        if (combinedSortedRenderer == null || combinedPositions == null || combinedRotations == null || combinedScales == null || combinedColorsCamera == null || combinedColorsScratch == null || combineDataMaterial == null)
        {
#if !UNITY_EDITOR || COMPILER_UDONSHARP
            Debug.LogError("Combined rendering mode is missing generated resources. Refresh the GaussianSplatRenderer in the editor.");
#endif
            return false;
        }
        int activeSourceCount = 0;
        for (int i = 0; i < _sceneSplats.Length; i++)
        {
            if (IsSourceActive(i))
            {
                activeSourceCount++;
            }
        }
        if (activeSourceCount == 0)
        {
            _combinedActualSplatCount = 0;
            SetCombinedRendererEnabled(false);
            return false;
        }
        int combinedBlocksPerRow = Mathf.Max(1, combinedPositions.width >> 2);
        combineDataMaterial.SetInt("_CombinedCoordShift", ComputeTextureCoordShift(combinedBlocksPerRow));
        int positionCapacity = combinedPositions.width * combinedPositions.height;
        int colorCapacity = combinedColorsScratch.width * combinedColorsScratch.height;
        Blit(Texture2D.blackTexture, combinedPositions, useEditorOps);
        Blit(Texture2D.blackTexture, combinedRotations, useEditorOps);
        Blit(Texture2D.blackTexture, combinedScales, useEditorOps);
        Blit(Texture2D.blackTexture, combinedColorsScratch, useEditorOps);
        int sourceCursor = 0;
        int combinedOffset = 0;
        while (true)
        {
            combineDataMaterial.SetVector("_CameraPosWorld", Vector3.zero);
            int batchStartOffset = combinedOffset;
            bool hasBatch = BindCombinedBatch(ref sourceCursor, ref combinedOffset, positionCapacity, colorCapacity);
            if (!hasBatch)
            {
                break;
            }
            Blit(combinedPositions, combineDataMaterial, 0, useEditorOps);
            Blit(combinedRotations, combineDataMaterial, 1, useEditorOps);
            Blit(combinedScales, combineDataMaterial, 2, useEditorOps);
            combineDataMaterial.SetVector("_CameraPosWorld", screenCameraPos);
            Blit(combinedColorsScratch, combineDataMaterial, 3, useEditorOps);
            if (combinedOffset == batchStartOffset)
            {
                break;
            }
        }
        if (combinedOffset <= 0)
        {
            _combinedActualSplatCount = 0;
            SetCombinedRendererEnabled(false);
            return false;
        }
        _combinedActualSplatCount = combinedOffset;
        CopyToCameraSlice(combinedColorsScratch, SCREEN_CAMERA_ID, useEditorOps);

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            CopyToCameraSlice(combinedColorsScratch, PHOTO_CAMERA_ID, true);
            SetCombinedRendererEnabled(true);
            return true;
        }
#endif

        Blit(Texture2D.blackTexture, combinedColorsScratch, false);
        sourceCursor = 0;
        combinedOffset = 0;
        while (true)
        {
            combineDataMaterial.SetVector("_CameraPosWorld", photoCameraPos);
            int photoBatchStartOffset = combinedOffset;
            bool hasPhotoBatch = BindCombinedBatch(ref sourceCursor, ref combinedOffset, positionCapacity, colorCapacity);
            if (!hasPhotoBatch)
            {
                break;
            }
            Blit(combinedColorsScratch, combineDataMaterial, 3, false);
            if (combinedOffset == photoBatchStartOffset)
            {
                break;
            }
        }
        CopyToCameraSlice(combinedColorsScratch, PHOTO_CAMERA_ID, false);
        SetCombinedRendererEnabled(true);
        return true;
    }

    void BeginSort(bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            _radixSort.BeginSortForEditor();
            return;
        }
#endif
        _radixSort.BeginSort();
    }

    void StepSort(int maxSubpasses, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            _radixSort.StepSortForEditor(maxSubpasses);
            return;
        }
#endif
        _radixSort.StepSort(maxSubpasses);
    }

    void CopySortedOrder(int slice, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            _radixSort.CopySortedOrderForEditor(splatRenderOrder, slice);
            return;
        }
#endif
        _radixSort.CopySortedOrder(splatRenderOrder, slice);
    }

    bool TryStartPendingSort(int cameraId, bool useEditorOps)
    {
        if (!_hasPendingSort[cameraId])
        {
            return false;
        }
        if (!ShouldAlwaysUpdate(useEditorOps) && _hasCompletedSort[cameraId] && _pendingCameraPos[cameraId] == _completedCameraPos[cameraId])
        {
            _hasPendingSort[cameraId] = false;
            return false;
        }
        keyValueMat.SetVector("_CameraPos", _sortedRenderer.transform.InverseTransformPoint(_pendingCameraWorldPos[cameraId]));
        BeginSort(useEditorOps);
        _activeSortCameraId = cameraId;
        _activeSortQuantizedPos = _pendingCameraPos[cameraId];
        _hasPendingSort[cameraId] = false;
        return true;
    }

    void PublishActiveSort(bool useEditorOps)
    {
        if (_activeSortCameraId == NO_ACTIVE_SORT)
        {
            return;
        }
        CopySortedOrder(_activeSortCameraId, useEditorOps);
        _completedCameraPos[_activeSortCameraId] = _activeSortQuantizedPos;
        _hasCompletedSort[_activeSortCameraId] = true;
        if (_activeSortCameraId == SCREEN_CAMERA_ID)
        {
            if (IsCombinedRenderingMode())
            {
                SetCombinedRendererEnabled(true);
            }
            else
            {
                GaussianSplatObject splat = GetCurrentSplat();
                if (splat != null)
                {
                    splat.ShowSorted();
                }
            }
        }
        _activeSortCameraId = NO_ACTIVE_SORT;
        _activeSortQuantizedPos = Vector3.positiveInfinity;
    }

    void RunBlockingSort(Vector3 cameraPos, int cameraId, bool useEditorOps)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        keyValueMat.SetVector("_CameraPos", _sortedRenderer.transform.InverseTransformPoint(cameraPos));
        BeginSort(useEditorOps);
        _activeSortCameraId = cameraId;
        _activeSortQuantizedPos = quantizedPos;
        StepSort(RadixSort.TotalSortPasses, useEditorOps);
        PublishActiveSort(useEditorOps);
    }

    void SortCameraViews(Vector3 screenCamPos, Vector3 photoCamPos, bool sortPhotoCamera, bool useEditorOps)
    {
        if (!EnsureInitialized())
        {
            return;
        }
        UpdateSourceVisibility();
        if (!IsCombinedRenderingMode() && !EnsureCurrentSourceSelected())
        {
            return;
        }
        if (IsCombinedRenderingMode() && !UpdateCombinedTextures(screenCamPos, photoCamPos, useEditorOps))
        {
            return;
        }
        if (!UpdateSortBinding())
        {
            return;
        }
        if (!_hasCompletedSort[SCREEN_CAMERA_ID])
        {
            RunBlockingSort(screenCamPos, SCREEN_CAMERA_ID, useEditorOps);
        }
        else
        {
            Vector3 quantizedScreenPos = QuantizePosition(screenCamPos);
            if (ShouldAlwaysUpdate(useEditorOps) || quantizedScreenPos != _completedCameraPos[SCREEN_CAMERA_ID])
            {
                _pendingCameraPos[SCREEN_CAMERA_ID] = quantizedScreenPos;
                _pendingCameraWorldPos[SCREEN_CAMERA_ID] = screenCamPos;
                _hasPendingSort[SCREEN_CAMERA_ID] = true;
            }
        }
        if (sortPhotoCamera)
        {
            Vector3 quantizedPhotoPos = QuantizePosition(photoCamPos);
            if (!_hasCompletedSort[PHOTO_CAMERA_ID] || ShouldAlwaysUpdate(useEditorOps) || quantizedPhotoPos != _completedCameraPos[PHOTO_CAMERA_ID])
            {
                _hasPendingSort[PHOTO_CAMERA_ID] = false;
                RunBlockingSort(photoCamPos, PHOTO_CAMERA_ID, useEditorOps);
            }
        }
        if (_activeSortCameraId == NO_ACTIVE_SORT && !TryStartPendingSort(SCREEN_CAMERA_ID, useEditorOps))
        {
            TryStartPendingSort(PHOTO_CAMERA_ID, useEditorOps);
        }
        if (_activeSortCameraId != NO_ACTIVE_SORT)
        {
            StepSort(Mathf.CeilToInt((float)RadixSort.TotalSortPasses / Mathf.Clamp(sortPipelineFrames, 1, RadixSort.TotalSortPasses)), useEditorOps);
            if (_radixSort.IsSortComplete())
            {
                PublishActiveSort(useEditorOps);
                if (_activeSortCameraId == NO_ACTIVE_SORT && !TryStartPendingSort(SCREEN_CAMERA_ID, useEditorOps))
                {
                    TryStartPendingSort(PHOTO_CAMERA_ID, useEditorOps);
                }
            }
        }
    }

    void Update()
    {
        DisableMsaaInGame();
        if (!EnsureInitialized() || VRCCameraSettings.ScreenCamera == null)
        {
            return;
        }
        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        bool sortPhotoCamera = photoCam != null && photoCam.Active;
        SortCameraViews(screenCamPos, sortPhotoCamera ? photoCam.Position : screenCamPos, sortPhotoCamera, false);
    }

    public override void OnDeserialization()
    {
        if (!EnsureInitialized())
        {
            return;
        }
        ResetCameraPositions();
        ApplyMaterialSettingsToSelectedObject();
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static bool ShouldUseEditorScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return false;
        }
        if (!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene))
        {
            return true;
        }
        UnityEditor.SceneManagement.PrefabStage prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
        return prefabStage != null && prefabStage.scene == scene;
    }

    static bool IsSceneObject(Component component, Scene scene)
    {
        if (component == null || (component.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
        {
            return false;
        }
        GameObject root = component.transform.root != null ? component.transform.root.gameObject : component.gameObject;
        return root != null && !EditorUtility.IsPersistent(root) && ShouldUseEditorScene(root.scene) && (!scene.IsValid() || root.scene == scene);
    }

    static T[] FindSceneObjects<T>(Scene scene) where T : Component
    {
        T[] sceneObjects = Resources.FindObjectsOfTypeAll<T>();
        List<T> filteredObjects = new List<T>();
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            if (IsSceneObject(sceneObjects[i], scene))
            {
                filteredObjects.Add(sceneObjects[i]);
            }
        }
        return filteredObjects.ToArray();
    }

    static void QueueEditorRefresh()
    {
        _editorRefreshQueued = true;
        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    static GaussianSplatRenderer GetPrimarySceneRenderer(Scene scene)
    {
        GaussianSplatRenderer[] renderers = FindSceneObjects<GaussianSplatRenderer>(scene);
        if (renderers.Length == 0)
        {
            return null;
        }
        GaussianSplatRenderer primary = renderers[0];
        int primaryInstanceId = primary.GetInstanceID();
        for (int i = 1; i < renderers.Length; i++)
        {
            GaussianSplatRenderer candidate = renderers[i];
            if (candidate != null && candidate.GetInstanceID() < primaryInstanceId)
            {
                primary = candidate;
                primaryInstanceId = candidate.GetInstanceID();
            }
        }
        return primary;
    }

    static void ApplyEditorVisibility(Scene scene, bool combinedMode)
    {
        GaussianSplatObject[] splats = FindSceneObjects<GaussianSplatObject>(scene);
        int visibleIndex = -1;
        if (!combinedMode)
        {
            for (int i = 0; i < splats.Length; i++)
            {
                GaussianSplatObject splat = splats[i];
                if (splat != null && splat.gameObject.activeInHierarchy)
                {
                    visibleIndex = i;
                    splat.ShowSorted();
                    break;
                }
            }
        }
        for (int i = 0; i < splats.Length; i++)
        {
            GaussianSplatObject splat = splats[i];
            MeshRenderer renderer = splat != null ? splat.GetSortedRenderer() : null;
            bool enabled = i == visibleIndex;
            if (renderer != null && renderer.enabled != enabled)
            {
                splat.SetSortedRendererEnabled(enabled);
            }
        }
    }

    [InitializeOnLoadMethod]
    static void RegisterEditorHooks()
    {
        EditorApplication.hierarchyChanged -= QueueEditorRefresh;
        EditorApplication.hierarchyChanged += QueueEditorRefresh;
        EditorApplication.update -= ProcessEditorRefresh;
        EditorApplication.update += ProcessEditorRefresh;
        Camera.onPreCull -= OnEditorCameraPreCull;
        Camera.onPreCull += OnEditorCameraPreCull;
    }

    static void ProcessEditorRefresh()
    {
        if (Application.isPlaying || !_editorRefreshQueued)
        {
            return;
        }
        _editorRefreshQueued = false;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!ShouldUseEditorScene(scene))
            {
                continue;
            }
            GaussianSplatRenderer renderer = GetPrimarySceneRenderer(scene);
            if (renderer == null)
            {
                ApplyEditorVisibility(scene, false);
                continue;
            }
            if (renderer.RefreshCachedSceneSplatObjects())
            {
                EditorUtility.SetDirty(renderer);
            }
            renderer.UpdateSortingResourceTextures();
            ApplyEditorVisibility(scene, renderer.IsCombinedRenderingMode());
        }
    }

    static void OnEditorCameraPreCull(Camera camera)
    {
        if (Application.isPlaying || camera == null || camera.cameraType != CameraType.SceneView)
        {
            return;
        }
        GaussianSplatRenderer[] renderers = FindSceneObjects<GaussianSplatRenderer>(default(Scene));
        for (int i = 0; i < renderers.Length; i++)
        {
            GaussianSplatRenderer renderer = renderers[i];
            if (renderer != null && renderer.enabled)
            {
                renderer.SortCameraViews(camera.transform.position, camera.transform.position, false, true);
            }
        }
    }

    bool RefreshCachedSceneSplatObjects()
    {
        GaussianSplatObject[] splats = FindSceneObjects<GaussianSplatObject>(gameObject.scene);
        GameObject[] roots = new GameObject[splats.Length];
        bool changed = false;
        for (int i = 0; i < splats.Length; i++)
        {
            GaussianSplatObject splat = splats[i];
            roots[i] = splat != null ? splat.gameObject : null;
            if (splat != null && splat.gaussianSplatRenderer != this)
            {
                splat.gaussianSplatRenderer = this;
                EditorUtility.SetDirty(splat);
                changed = true;
            }
        }
        if (!changed && cachedSceneSplatObjects != null && cachedSceneSplatObjects.Length == roots.Length)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                if (cachedSceneSplatObjects[i] != roots[i])
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
            {
                return false;
            }
        }
        cachedSceneSplatObjects = roots;
        ResetRuntimeCache();
        return true;
    }

    [MenuItem("GameObject/Gaussian Splatting/Gaussian Splat Renderer", false, 10)]
    static void CreateGaussianSplatRenderer(MenuCommand menuCommand)
    {
        GaussianSplatRenderer renderer = EnsureSceneRendererExists(default(Scene));
        if (renderer != null)
        {
            Selection.activeGameObject = renderer.gameObject;
        }
    }

    public static GaussianSplatRenderer FindExistingSceneRenderer(Scene scene)
    {
        return GetPrimarySceneRenderer(scene);
    }

    public static GaussianSplatRenderer EnsureSceneRendererExists(Scene scene)
    {
        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer(scene);
        if (primaryRenderer == null)
        {
            GameObject rendererObject = new GameObject("GaussianSplatRenderer");
            rendererObject.hideFlags = HideFlags.NotEditable;
            Undo.RegisterCreatedObjectUndo(rendererObject, "Create Gaussian Splat Renderer");
            if (scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(rendererObject, scene);
            }
            primaryRenderer = rendererObject.AddUdonSharpComponent<GaussianSplatRenderer>();
            RadixSort radixSort = rendererObject.AddUdonSharpComponent<RadixSort>();
            radixSort.computeKeyValues = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ComputeKeyValue.mat");
            radixSort.radixSort = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/RadixSort/Materials/Misha_RadixSort.mat");
            EditorUtility.SetDirty(primaryRenderer);
            EditorUtility.SetDirty(radixSort);
        }
        GaussianSplatRenderer[] renderers = FindSceneObjects<GaussianSplatRenderer>(scene);
        for (int i = 0; i < renderers.Length; i++)
        {
            GaussianSplatRenderer renderer = renderers[i];
            if (renderer != null && renderer != primaryRenderer && renderer.enabled)
            {
                renderer.enabled = false;
                EditorUtility.SetDirty(renderer);
            }
        }
        if (primaryRenderer.RefreshCachedSceneSplatObjects())
        {
            EditorUtility.SetDirty(primaryRenderer);
        }
        primaryRenderer.UpdateSortingResourceTextures();
        ApplyEditorVisibility(primaryRenderer.gameObject.scene, primaryRenderer.IsCombinedRenderingMode());
        return primaryRenderer;
    }

    void OnValidate()
    {
        if (EditorUtility.IsPersistent(this))
        {
            return;
        }
        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer(gameObject.scene);
        if (primaryRenderer != null && primaryRenderer != this)
        {
            if (enabled)
            {
                enabled = false;
                EditorUtility.SetDirty(this);
            }
            GaussianSplatRendererUI.RequestEditorRefresh();
            return;
        }
        bool changed = RefreshCachedSceneSplatObjects();
        if (changed)
        {
            EditorUtility.SetDirty(this);
        }
        UpdateSortingResourceTextures();
        ApplyEditorVisibility(gameObject.scene, IsCombinedRenderingMode());
        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    RenderTexture CreateSortRenderTextureAsset(string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        PlySplatImporter.EnsureFolderExists(folderPath);
        RenderTexture renderTexture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
        renderTexture.name = assetName;
        renderTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        renderTexture.volumeDepth = volumeDepth;
        renderTexture.useMipMap = useMipMap;
        renderTexture.autoGenerateMips = false;
        renderTexture.wrapMode = TextureWrapMode.Clamp;
        renderTexture.filterMode = FilterMode.Point;
        renderTexture.anisoLevel = 0;
        renderTexture.antiAliasing = 1;
        renderTexture.Create();
        AssetDatabase.CreateAsset(renderTexture, folderPath + "/" + assetName + ".renderTexture");
        return renderTexture;
    }

    bool EnsureSortRenderTexture(ref RenderTexture targetTexture, string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        string assetPath = folderPath + "/" + assetName + ".renderTexture";
        if (targetTexture == null)
        {
            PlySplatImporter.EnsureFolderExists(folderPath);
            targetTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(assetPath);
            if (targetTexture == null)
            {
                targetTexture = CreateSortRenderTextureAsset(folderPath, assetName, width, height, format, useMipMap, volumeDepth);
                return true;
            }
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
            || targetTexture.anisoLevel != 0
            || targetTexture.antiAliasing != 1;
        if (!needsResize)
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
        targetTexture.anisoLevel = 0;
        targetTexture.antiAliasing = 1;
        targetTexture.Create();
        EditorUtility.SetDirty(targetTexture);
        return true;
    }

    public static bool MaterialArraysMatch(Material[] left, Material[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        return true;
    }

    static bool UsesCombinedHierarchyShader(Material material, string shaderName)
    {
        return material != null && material.shader != null && material.shader.name == shaderName;
    }

    bool CombinedHierarchyMatches(MeshRenderer meshRenderer, Material[] combinedMaterials)
    {
        if (meshRenderer == null || combinedMaterials == null || combinedMaterials.Length == 0)
        {
            return false;
        }
        List<Material> parentMaterials = new List<Material>();
        int cursor = 0;
        if (UsesCombinedHierarchyShader(combinedMaterials[0], "VRChatGaussianSplatting/ToSRGB"))
        {
            parentMaterials.Add(combinedMaterials[cursor]);
            cursor++;
        }
        int end = combinedMaterials.Length;
        if (end > cursor && UsesCombinedHierarchyShader(combinedMaterials[end - 1], "VRChatGaussianSplatting/ToLinear"))
        {
            end--;
            parentMaterials.Add(combinedMaterials[end]);
        }
        if (!MaterialArraysMatch(meshRenderer.sharedMaterials, parentMaterials.ToArray()))
        {
            return false;
        }
        int expectedChunkCount = 0;
        while (cursor < end)
        {
            Material alphaMask = null;
            Material splatMaterial = combinedMaterials[cursor];
            if (UsesCombinedHierarchyShader(splatMaterial, "VRChatGaussianSplatting/AlphaDepthMask"))
            {
                alphaMask = splatMaterial;
                cursor++;
                if (cursor >= end)
                {
                    return false;
                }
                splatMaterial = combinedMaterials[cursor];
            }
            cursor++;
            if (splatMaterial == null || !splatMaterial.HasProperty("_SplatCount"))
            {
                continue;
            }
            Transform chunkTransform = meshRenderer.transform.Find("CombinedChunk" + expectedChunkCount);
            if (chunkTransform == null)
            {
                return false;
            }
            MeshRenderer chunkRenderer = chunkTransform.GetComponent<MeshRenderer>();
            if (chunkRenderer == null)
            {
                return false;
            }
            Material[] chunkMaterials = alphaMask != null ? new[] { alphaMask, splatMaterial } : new[] { splatMaterial };
            if (!MaterialArraysMatch(chunkRenderer.sharedMaterials, chunkMaterials))
            {
                return false;
            }
            expectedChunkCount++;
        }
        int actualChunkCount = 0;
        for (int childIndex = 0; childIndex < meshRenderer.transform.childCount; childIndex++)
        {
            if (meshRenderer.transform.GetChild(childIndex).name.StartsWith("CombinedChunk"))
            {
                actualChunkCount++;
            }
        }
        return actualChunkCount == expectedChunkCount;
    }

    bool EnsureCombinedRendererRoot(Material[] combinedMaterials, MeshRenderer templateRenderer)
    {
        GameObject combinedObject = combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : null;
        if (combinedObject == null && gameObject.scene.IsValid())
        {
            GameObject[] roots = gameObject.scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] != null && roots[i].name == "CombinedSorted")
                {
                    combinedObject = roots[i];
                    break;
                }
            }
        }
        bool changed = false;
        if (combinedObject == null)
        {
            combinedObject = new GameObject("CombinedSorted");
            combinedObject.hideFlags = HideFlags.NotEditable;
            Undo.RegisterCreatedObjectUndo(combinedObject, "Create Combined Gaussian Splat Renderer");
            SceneManager.MoveGameObjectToScene(combinedObject, gameObject.scene);
            changed = true;
        }
        Transform transformToReset = combinedObject.transform;
        if (transformToReset.parent != null)
        {
            Undo.SetTransformParent(transformToReset, null, "Reparent Combined Gaussian Splat Renderer");
            changed = true;
        }
        if (transformToReset.localPosition != Vector3.zero || transformToReset.localRotation != Quaternion.identity || transformToReset.localScale != Vector3.one)
        {
            Undo.RecordObject(transformToReset, "Reset Combined Gaussian Splat Renderer Transform");
            transformToReset.localPosition = Vector3.zero;
            transformToReset.localRotation = Quaternion.identity;
            transformToReset.localScale = Vector3.one;
            EditorUtility.SetDirty(transformToReset);
            changed = true;
        }
        MeshFilter meshFilter = combinedObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = Undo.AddComponent<MeshFilter>(combinedObject);
            changed = true;
        }
        MeshRenderer meshRenderer = combinedObject.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
        {
            meshRenderer = Undo.AddComponent<MeshRenderer>(combinedObject);
            changed = true;
        }
        combinedSortedRenderer = meshRenderer;
        if (!CombinedHierarchyMatches(meshRenderer, combinedMaterials) && !MaterialArraysMatch(meshRenderer.sharedMaterials, combinedMaterials))
        {
            Undo.RecordObject(meshRenderer, "Update Combined Gaussian Splat Materials");
            meshRenderer.sharedMaterials = combinedMaterials;
            EditorUtility.SetDirty(meshRenderer);
            changed = true;
        }
        if (templateRenderer != null)
        {
            if (meshRenderer.shadowCastingMode != templateRenderer.shadowCastingMode ||
                meshRenderer.receiveShadows != templateRenderer.receiveShadows ||
                meshRenderer.lightProbeUsage != templateRenderer.lightProbeUsage ||
                meshRenderer.reflectionProbeUsage != templateRenderer.reflectionProbeUsage ||
                meshRenderer.motionVectorGenerationMode != templateRenderer.motionVectorGenerationMode ||
                meshRenderer.allowOcclusionWhenDynamic != templateRenderer.allowOcclusionWhenDynamic)
            {
                Undo.RecordObject(meshRenderer, "Update Combined Gaussian Splat Renderer Settings");
                meshRenderer.shadowCastingMode = templateRenderer.shadowCastingMode;
                meshRenderer.receiveShadows = templateRenderer.receiveShadows;
                meshRenderer.lightProbeUsage = templateRenderer.lightProbeUsage;
                meshRenderer.reflectionProbeUsage = templateRenderer.reflectionProbeUsage;
                meshRenderer.motionVectorGenerationMode = templateRenderer.motionVectorGenerationMode;
                meshRenderer.allowOcclusionWhenDynamic = templateRenderer.allowOcclusionWhenDynamic;
                EditorUtility.SetDirty(meshRenderer);
                changed = true;
            }
        }
        if (combinedObject.activeSelf != IsCombinedRenderingMode())
        {
            Undo.RecordObject(combinedObject, "Toggle Combined Gaussian Splat Renderer");
            combinedObject.SetActive(IsCombinedRenderingMode());
            EditorUtility.SetDirty(combinedObject);
            changed = true;
        }
        return changed;
    }

    void UpdateCombinedResources(int combinedElementCount, MeshRenderer templateRenderer, Material primaryTemplate, Material alphaMaskTemplate, Material toSrgbTemplate, Material toLinearTemplate)
    {
        if (combinedElementCount <= 0 || primaryTemplate == null)
        {
            return;
        }
        PlySplatImporter.TextureLayout combinedLayout = PlySplatImporter.ChoosePotTextureLayout(combinedElementCount);
        int combinedWidth = combinedLayout.Width;
        int combinedHeight = combinedLayout.Height;
        string combinedFolderPath = PlySplatImporter.GetSceneTempResourceFolderPath(gameObject.scene, "RTs") + "/Combined";
        string assetPrefix = PlySplatImporter.SanitizeAssetName(name);
        RenderTexture previousCombinedPositions = combinedPositions;
        RenderTexture previousCombinedRotations = combinedRotations;
        RenderTexture previousCombinedScales = combinedScales;
        RenderTexture previousCombinedColorsCamera = combinedColorsCamera;
        RenderTexture previousCombinedColorsScratch = combinedColorsScratch;
        Material previousCombineDataMaterial = combineDataMaterial;
        MeshRenderer previousCombinedSortedRenderer = combinedSortedRenderer;
        EnsureSortRenderTexture(ref combinedPositions, combinedFolderPath, assetPrefix + "_CombinedPositions", combinedWidth, combinedHeight, RenderTextureFormat.ARGBFloat, false, 1);
        EnsureSortRenderTexture(ref combinedRotations, combinedFolderPath, assetPrefix + "_CombinedRotations", combinedWidth, combinedHeight, RenderTextureFormat.ARGB32, false, 1);
        EnsureSortRenderTexture(ref combinedScales, combinedFolderPath, assetPrefix + "_CombinedScales", combinedWidth, combinedHeight, RenderTextureFormat.ARGBHalf, false, 1);
        EnsureSortRenderTexture(ref combinedColorsCamera, combinedFolderPath, assetPrefix + "_CombinedColorsCamera", combinedWidth, combinedHeight, RenderTextureFormat.ARGB32, false, MAX_CAMERA_COUNT);
        EnsureSortRenderTexture(ref combinedColorsScratch, combinedFolderPath, assetPrefix + "_CombinedColorsScratch", combinedWidth, combinedHeight, RenderTextureFormat.ARGB32, false, 1);
        Shader combineShader = Shader.Find("Hidden/GaussianSplatting/CombineData");
        if (combineShader == null)
        {
            return;
        }
        Material combineMaterial = new Material(combineShader);
        combineMaterial.name = assetPrefix + "_CombineData";
        combineDataMaterial = PlySplatImporter.CreateOrReplaceAsset(combineMaterial, combinedFolderPath + "/" + assetPrefix + "_CombineData.mat");
        bool useSrgb = toSrgbTemplate != null || toLinearTemplate != null;
        PlySplatImporter.PassInfo[] passInfos = PlySplatImporter.CreatePassLayout(combinedElementCount, Mathf.Min(DEFAULT_COMBINED_SPLATS_PER_PASS, combinedElementCount), DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT, useSrgb);
        List<Material> generatedMaterials = new List<Material>();
        int renderQueue = 3500;
        if (useSrgb)
        {
            Material toSrgb = PlySplatImporter.CreateMaterialFromTemplate(toSrgbTemplate, "VRChatGaussianSplatting/ToSRGB", assetPrefix + "_CombinedToSRGB");
            if (toSrgb != null)
            {
                toSrgb.renderQueue = renderQueue++;
                generatedMaterials.Add(toSrgb);
            }
        }
        Material mainMaterial = null;
        for (int passIndex = 0; passIndex < passInfos.Length; passIndex++)
        {
            PlySplatImporter.PassInfo passInfo = passInfos[passIndex];
            string materialName = assetPrefix + (passInfo.PassIndex > 0 ? "_CombinedPass" + passInfo.PassIndex : "_CombinedMain") + "_Splat";
            Material splatMaterial = passInfo.PassIndex == 0
                ? PlySplatImporter.CreateMaterialFromTemplate(primaryTemplate, "VRChatGaussianSplatting/GaussianSplatting", materialName)
                : (mainMaterial != null ? new Material(mainMaterial) : PlySplatImporter.CreateMaterialFromTemplate(primaryTemplate, "VRChatGaussianSplatting/GaussianSplatting", materialName));
            if (splatMaterial == null)
            {
                continue;
            }
            splatMaterial.name = materialName;
            if (passInfo.PassIndex == 0)
            {
                mainMaterial = splatMaterial;
            }
            PlySplatImporter.ConfigureSplatMaterial(
                splatMaterial,
                combinedPositions,
                null,
                combinedRotations,
                combinedScales,
                null,
                0,
                combinedElementCount,
                Vector4.zero,
                Vector4.one,
                combinedElementCount,
                0.0f,
                combinedColorsCamera,
                true,
                null,
                passInfo.SplatCount,
                passInfo.SplatOffset);
            ApplyConfiguredMaterialSettings(splatMaterial, 0);
            if (passInfo.HasAlphaMask)
            {
                Material alphaMask = PlySplatImporter.CreateMaterialFromTemplate(alphaMaskTemplate, "VRChatGaussianSplatting/AlphaDepthMask", materialName + "_AlphaDepthMask");
                if (alphaMask != null)
                {
                    alphaMask.renderQueue = renderQueue++;
                    generatedMaterials.Add(alphaMask);
                }
            }
            splatMaterial.renderQueue = renderQueue++;
            generatedMaterials.Add(splatMaterial);
        }
        if (useSrgb)
        {
            Material toLinear = PlySplatImporter.CreateMaterialFromTemplate(toLinearTemplate, "VRChatGaussianSplatting/ToLinear", assetPrefix + "_CombinedToLinear");
            if (toLinear != null)
            {
                toLinear.renderQueue = renderQueue++;
                generatedMaterials.Add(toLinear);
            }
        }
        string materialsFolderPath = combinedFolderPath + "/Materials";
        PlySplatImporter.EnsureFolderExists(materialsFolderPath);
        for (int i = 0; i < generatedMaterials.Count; i++)
        {
            generatedMaterials[i] = PlySplatImporter.CreateOrReplaceAsset(generatedMaterials[i], materialsFolderPath + "/" + generatedMaterials[i].name + ".mat");
        }
        Material[] combinedMaterials = generatedMaterials.ToArray();
        bool rendererRootChanged = EnsureCombinedRendererRoot(combinedMaterials, templateRenderer);
        if (rendererRootChanged)
        {
            Type builderType = null;
            System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                builderType = assemblies[assemblyIndex].GetType("GaussianSplatting.Editor.GaussianSplatCombinedHierarchyBuilder");
                if (builderType != null)
                {
                    break;
                }
            }
            if (builderType != null)
            {
                var ensureChunkHierarchy = builderType.GetMethod("EnsureChunkHierarchy", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (ensureChunkHierarchy != null)
                {
                    ensureChunkHierarchy.Invoke(null, new object[] { this });
                }
            }
        }
        if (combinedPositions != previousCombinedPositions ||
            combinedRotations != previousCombinedRotations ||
            combinedScales != previousCombinedScales ||
            combinedColorsCamera != previousCombinedColorsCamera ||
            combinedColorsScratch != previousCombinedColorsScratch ||
            combineDataMaterial != previousCombineDataMaterial ||
            combinedSortedRenderer != previousCombinedSortedRenderer ||
            rendererRootChanged)
        {
            EditorUtility.SetDirty(this);
        }
    }

    void UpdateSortingResourceTextures()
    {
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort == null)
        {
            return;
        }
        int largestCount = 0;
        int combinedCount = 0;
        MeshRenderer templateRenderer = null;
        Material primaryTemplate = null;
        Material alphaMaskTemplate = null;
        Material toSrgbTemplate = null;
        Material toLinearTemplate = null;
        for (int i = 0; cachedSceneSplatObjects != null && i < cachedSceneSplatObjects.Length; i++)
        {
            GaussianSplatObject splat = cachedSceneSplatObjects[i] != null ? cachedSceneSplatObjects[i].GetComponent<GaussianSplatObject>() : null;
            if (!TryGetSplatSource(splat, out MeshRenderer renderer, out Material primaryMaterial, out Texture positions, out int count))
            {
                continue;
            }
            combinedCount += count;
            if (count > largestCount)
            {
                largestCount = count;
            }
            if (primaryTemplate == null)
            {
                templateRenderer = renderer;
                primaryTemplate = primaryMaterial;
                Material[] materials = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material material = materials[materialIndex];
                    if (material == null || material.shader == null)
                    {
                        continue;
                    }
                    string shaderName = material.shader.name;
                    if (alphaMaskTemplate == null && shaderName == "VRChatGaussianSplatting/AlphaDepthMask") alphaMaskTemplate = material;
                    else if (toSrgbTemplate == null && shaderName == "VRChatGaussianSplatting/ToSRGB") toSrgbTemplate = material;
                    else if (toLinearTemplate == null && shaderName == "VRChatGaussianSplatting/ToLinear") toLinearTemplate = material;
                }
            }
        }
        if (largestCount <= 0)
        {
            return;
        }
        int safeCombinedCount = Mathf.Min(combinedCount, MAX_COMBINED_SPLAT_COUNT);
        int requiredElementCount = IsCombinedRenderingMode() ? Mathf.Max(largestCount, safeCombinedCount) : largestCount;
        int optimalPot = Mathf.NextPowerOfTwo(Mathf.Max(1, requiredElementCount));
        int optimalPotLog2 = Mathf.CeilToInt(Mathf.Log(optimalPot, 2));
        int requiredHeight = 1 << (optimalPotLog2 / 2);
        int requiredWidth = 1 << (optimalPotLog2 / 2 + optimalPotLog2 % 2);
        string resourceFolderPath = PlySplatImporter.GetSceneTempResourceFolderPath(gameObject.scene, "RTs");
        string assetPrefix = PlySplatImporter.SanitizeAssetName(name);
        bool resourcesChanged = false;
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues0, resourceFolderPath, assetPrefix + "_KeyValues0", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues1, resourceFolderPath, assetPrefix + "_KeyValues1", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.prefixSums, resourceFolderPath, assetPrefix + "_PrefixSums", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, true, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref splatRenderOrder, resourceFolderPath, assetPrefix + "_SplatRenderOrder", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 2);
        if (resourcesChanged)
        {
            EditorUtility.SetDirty(radixSort);
            EditorUtility.SetDirty(this);
        }
        if (IsCombinedRenderingMode())
        {
            UpdateCombinedResources(safeCombinedCount, templateRenderer, primaryTemplate, alphaMaskTemplate, toSrgbTemplate, toLinearTemplate);
        }
        else if (combinedSortedRenderer != null && combinedSortedRenderer.gameObject.activeSelf)
        {
            Undo.RecordObject(combinedSortedRenderer.gameObject, "Toggle Combined Gaussian Splat Renderer");
            combinedSortedRenderer.gameObject.SetActive(false);
            EditorUtility.SetDirty(combinedSortedRenderer.gameObject);
        }
        ResetRuntimeCache();
    }
#endif
}

}
