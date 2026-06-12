using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace GaussianSplatting
{

public enum GaussianSplatRenderingMode
{
    SingleSplat = 0,
    CombineAllSplats = 1,
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public partial class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 2;
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;
    const int SCREEN_CAMERA_ID = 0;
    const int PHOTO_CAMERA_ID = 1;
    const int DEFAULT_START_RENDER_QUEUE = 4050;
    const float DEFAULT_ALPHA_CUTOFF = 0.04f;
    const float DEFAULT_ALPHA_CULL = 0.04f;
    const int DEFAULT_COMBINED_LOD_SPLAT_BUDGET_PC = 3000000;
    const int DEFAULT_COMBINED_LOD_SPLAT_BUDGET_ANDROID = 300000;
    const float DEFAULT_COMBINED_LOD_TARGET_SCALE = 0.95f;
    const float DEFAULT_COMBINED_LOD_DIRECTIONAL_BIAS = 2.0f;

    [System.NonSerialized] Vector3[] _completedCameraPos;
    [System.NonSerialized] Vector3[] _completedCameraWorldPos;
    [System.NonSerialized] bool[] _hasCompletedSort;
    [System.NonSerialized] RadixSort _radixSort;
    [System.NonSerialized] Material keyValueMat;
    [System.NonSerialized] MeshRenderer _sortedRenderer;

    [System.NonSerialized] GaussianSplatObject[] _sceneSplats = new GaussianSplatObject[0];
    [System.NonSerialized] GaussianSplatLODObject[] _sceneLods = new GaussianSplatLODObject[0];
    [System.NonSerialized] int _currentSourceIndex = -1;
    [System.NonSerialized] bool _runtimeCacheValid;

    [HideInInspector, SerializeField] GameObject[] cachedSceneSplatObjects;
    [HideInInspector, SerializeField] GameObject[] cachedSceneLODObjects;
    [SerializeField] GaussianSplatRenderingMode renderingMode = GaussianSplatRenderingMode.SingleSplat;
    [SerializeField] GaussianSplatCombiner combiner;
    [Tooltip("Combined LOD splat cap for PC builds. 0 disables the cap.")]
    [SerializeField] int combinedLodSplatBudgetPC = DEFAULT_COMBINED_LOD_SPLAT_BUDGET_PC;
    [Tooltip("Combined LOD splat cap for Android builds. 0 disables the cap.")]
    [SerializeField] int combinedLodSplatBudgetAndroid = DEFAULT_COMBINED_LOD_SPLAT_BUDGET_ANDROID;
    [Tooltip("GPU LOD selection target as a fraction of the active LOD cap. Lower values leave headroom for alpha solver overshoot.")]
    [SerializeField] float combinedLodTargetScale = DEFAULT_COMBINED_LOD_TARGET_SCALE;
    [Tooltip("Directional LOD distance divisor. 1 disables the camera direction bias; 2 halves effective distance for chunks directly in front of the camera.")]
    [Range(1.0f, 16.0f)] [SerializeField] float combinedLodDirectionalBias = DEFAULT_COMBINED_LOD_DIRECTIONAL_BIAS;
    [HideInInspector, SerializeField] int combinedLodSettingsVersion;
    [Tooltip("Draw editor-only wire boxes for active LOD chunks, colored by the currently selected GPU LOD.")]
    [SerializeField] bool debugDrawLodGrid;

    [Header("Render Settings")]
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    [Tooltip("If true, the splat render order will be updated every frame. Useful for animated splats. If false, it will only update when the camera position changes.")]
    [SerializeField] bool alwaysUpdate;
    [Tooltip("2D render texture used to store sorted splat render order for the screen camera.")]
    public RenderTexture splatRenderOrder;
    [Tooltip("2D render texture used to store sorted splat render order for the photo camera.")]
    public RenderTexture splatRenderOrderPhoto;

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [UdonSynced, SerializeField] public bool overrideMaterialProperties;
    [Tooltip("When enabled, non-master players cannot change in-game UI controls marked as global.")]
    [SerializeField] bool blockNonMasterGlobalChanges;
    [SerializeField] bool overrideRenderQueue;
    [SerializeField] int startRenderQueue = DEFAULT_START_RENDER_QUEUE;
    [UdonSynced, Range(0, 3)] [SerializeField] int requestedSHBand = 3;
    [UdonSynced, Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] float thinThreshold = 0.005f;
    [Range(0.0f, 3.0f)] [SerializeField] float antiAliasing = 1.0f;
    [Range(-20.0f, 10.0f)] [SerializeField] float log2MinScale = -15.0f;
    [Range(0.005f, 0.3f)] [SerializeField] public float alphaCutoff = DEFAULT_ALPHA_CUTOFF;
    [Range(0.005f, 0.3f)] [SerializeField] public float alphaCull = DEFAULT_ALPHA_CULL;
    [Range(0.0f, 0.1f)] [SerializeField] public float lodCull = 0.0f;
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
            _completedCameraWorldPos[i] = Vector3.positiveInfinity;
            _hasCompletedSort[i] = false;
        }
    }

    void ResetRuntimeCache()
    {
        _runtimeCacheValid = false;
        _sceneSplats = new GaussianSplatObject[0];
        _sceneLods = new GaussianSplatLODObject[0];
        _currentSourceIndex = -1;
        _sortedRenderer = null;
        ResetCameraPositions();
    }

    GaussianSplatCombiner ResolveCombiner()
    {
        if (combiner != null && combiner.gameObject != null && combiner.gameObject != gameObject)
        {
            return combiner;
        }

        // The combiner lives on the serialized "CombinedSorted" object. At runtime we
        // rely on the serialized reference only; calling GameObject.Find here would be
        // unsafe (e.g. during OnDisable it trips Unity's go.IsActive() assertion).
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        GameObject combinedObject = GameObject.Find("CombinedSorted");
        if (combinedObject != null)
        {
            GaussianSplatCombiner combinedObjectCombiner = combinedObject.GetComponent<GaussianSplatCombiner>();
            if (combinedObjectCombiner != null)
            {
                combiner = combinedObjectCombiner;
                return combiner;
            }
        }
#endif

        return combiner;
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

    static bool TryGetLODSource(GaussianSplatLODObject lodObject)
    {
        return lodObject != null && lodObject.IsRenderable();
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
        GameObject[] lodRoots = cachedSceneLODObjects;
        bool hasSplats = roots != null && roots.Length > 0;
        bool hasLods = lodRoots != null && lodRoots.Length > 0;
        if (!hasSplats && !hasLods)
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
        for (int i = 0; roots != null && i < roots.Length; i++)
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
        for (int i = 0; roots != null && i < roots.Length; i++)
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

        int validLodCount = 0;
        for (int i = 0; lodRoots != null && i < lodRoots.Length; i++)
        {
            GameObject root = lodRoots[i];
            GaussianSplatLODObject lodObject = root != null ? root.GetComponent<GaussianSplatLODObject>() : null;
            if (TryGetLODSource(lodObject))
            {
                validLodCount++;
            }
        }
        _sceneLods = new GaussianSplatLODObject[validLodCount];
        writeIndex = 0;
        for (int i = 0; lodRoots != null && i < lodRoots.Length; i++)
        {
            GameObject root = lodRoots[i];
            GaussianSplatLODObject lodObject = root != null ? root.GetComponent<GaussianSplatLODObject>() : null;
            if (!TryGetLODSource(lodObject))
            {
                continue;
            }
            _sceneLods[writeIndex] = lodObject;
            writeIndex++;
        }
        _currentSourceIndex = FindFirstActiveSourceIndex();
        _runtimeCacheValid = true;
    }

    int FindFirstActiveSourceIndex() { for (int i = 0; i < _sceneSplats.Length; i++) if (_sceneSplats[i] != null && _sceneSplats[i].gameObject.activeInHierarchy) return i; return -1; }
    bool IsSourceActive(int index) { return index >= 0 && index < _sceneSplats.Length && _sceneSplats[index] != null && _sceneSplats[index].gameObject.activeInHierarchy; }
    bool IsLODObjectActive(int index) { return index >= 0 && index < _sceneLods.Length && _sceneLods[index] != null && _sceneLods[index].gameObject.activeInHierarchy && _sceneLods[index].IsRenderable(); }
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

    int FindLODObjectIndex(GaussianSplatLODObject lodObject)
    {
        if (lodObject == null)
        {
            return -1;
        }
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            if (_sceneLods[i] == lodObject)
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

    void RegisterRuntimeLODObject(GaussianSplatLODObject lodObject)
    {
        if (!TryGetLODSource(lodObject))
        {
            return;
        }
        GameObject root = lodObject.gameObject;
        int cachedCount = cachedSceneLODObjects != null ? cachedSceneLODObjects.Length : 0;
        bool hasCachedRoot = false;
        for (int i = 0; i < cachedCount; i++)
        {
            if (cachedSceneLODObjects[i] == root)
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
                roots[i] = cachedSceneLODObjects[i];
            }
            roots[cachedCount] = root;
            cachedSceneLODObjects = roots;
        }
        if (FindLODObjectIndex(lodObject) >= 0)
        {
            return;
        }
        GaussianSplatLODObject[] sceneLods = new GaussianSplatLODObject[_sceneLods.Length + 1];
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            sceneLods[i] = _sceneLods[i];
        }
        sceneLods[_sceneLods.Length] = lodObject;
        _sceneLods = sceneLods;
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
            GaussianSplatCombiner combined = ResolveCombiner();
            if (combined != null) combined.SetRendererEnabled(false);
        }
    }

    void EnsureLocalOwnership() { if (Networking.LocalPlayer != null) Networking.SetOwner(Networking.LocalPlayer, gameObject); }
    void RequestSyncedStateUpdate() { if (Networking.LocalPlayer != null) RequestSerialization(); }
    bool LocalPlayerIsMasterOrEditor() { return Networking.LocalPlayer == null || Networking.LocalPlayer.isMaster; }
    bool CanModifyGlobalState() { return !blockNonMasterGlobalChanges || LocalPlayerIsMasterOrEditor(); }
    public bool CanLocalPlayerModifyGlobalState() { return CanModifyGlobalState(); }
    public bool IsCombinedRenderingMode() { return renderingMode == GaussianSplatRenderingMode.CombineAllSplats; }
    bool ShouldAlwaysUpdate(bool useEditorOps) { return useEditorOps || IsCombinedRenderingMode() || alwaysUpdate; }

    public int GetEffectiveCombinedLodSplatBudget()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return Mathf.Max(0, combinedLodSplatBudgetAndroid);
#else
        return Mathf.Max(0, combinedLodSplatBudgetPC);
#endif
    }

    public int GetCombinedLodSplatBudgetPC() { return Mathf.Max(0, combinedLodSplatBudgetPC); }
    public int GetCombinedLodSplatBudgetAndroid() { return Mathf.Max(0, combinedLodSplatBudgetAndroid); }
    public bool HasActiveLODObjects()
    {
        if (!EnsureInitialized())
        {
            return false;
        }
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            if (IsLODObjectActive(i))
            {
                return true;
            }
        }
        return false;
    }

    public void SetCombinedLodSplatBudgetPC(int value)
    {
        int clampedValue = Mathf.Max(0, value);
        if (combinedLodSplatBudgetPC == clampedValue)
        {
            return;
        }
        combinedLodSplatBudgetPC = clampedValue;
        ResetCameraPositions();
    }

    public void SetCombinedLodSplatBudgetAndroid(int value)
    {
        int clampedValue = Mathf.Max(0, value);
        if (combinedLodSplatBudgetAndroid == clampedValue)
        {
            return;
        }
        combinedLodSplatBudgetAndroid = clampedValue;
        ResetCameraPositions();
    }

    public void SetEffectiveCombinedLodSplatBudget(int value)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SetCombinedLodSplatBudgetAndroid(value);
#else
        SetCombinedLodSplatBudgetPC(value);
#endif
    }

    public int GetCombinedLodSplatBudgetSliderMax()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return Mathf.Max(DEFAULT_COMBINED_LOD_SPLAT_BUDGET_ANDROID * 2, combinedLodSplatBudgetAndroid);
#else
        return Mathf.Max(DEFAULT_COMBINED_LOD_SPLAT_BUDGET_PC * 2, combinedLodSplatBudgetPC);
#endif
    }

    public float GetEffectiveCombinedLodTargetScale()
    {
        return combinedLodTargetScale > 0.0f ? Mathf.Clamp01(combinedLodTargetScale) : DEFAULT_COMBINED_LOD_TARGET_SCALE;
    }

    public float GetCombinedLodDirectionalBias()
    {
        return Mathf.Clamp(combinedLodDirectionalBias > 0.0f ? combinedLodDirectionalBias : DEFAULT_COMBINED_LOD_DIRECTIONAL_BIAS, 1.0f, 16.0f);
    }

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
        if (material.HasProperty("_AlphaCull")) material.SetFloat("_AlphaCull", alphaCull);
        if (material.HasProperty("_LODCull")) material.SetFloat("_LODCull", lodCull);
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
            GaussianSplatCombiner combined = ResolveCombiner();
            if (combined != null) combined.ApplyMaterialSettings();
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

    public void ApplyConfiguredMaterialSettingsForCombined(Material material) { ApplyConfiguredMaterialSettings(material, 0); }
    public GaussianSplatCombiner GetCombiner() { return ResolveCombiner(); }
    public void SetCombiner(GaussianSplatCombiner value) { combiner = value; }

    public int GetSelectedSplatMaxSHBand()
    {
        if (IsCombinedRenderingMode())
        {
            return 0;
        }
        GaussianSplatObject splat = !EnsureInitialized() || !EnsureCurrentSourceSelected() ? null : GetCurrentSplat();
        return splat != null ? splat.GetMaxSHBand() : 0;
    }
    public int GetCurrentSHBand() { return Mathf.Clamp(requestedSHBand, 0, GetSelectedSplatMaxSHBand()); }
    public void SetSHBand(int value) { if (!CanModifyGlobalState()) return; EnsureLocalOwnership(); requestedSHBand = Mathf.Clamp(value, 0, 3); ApplyMaterialSettingsToSelectedObject(); RequestSyncedStateUpdate(); }
    public float GetCameraPositionQuantization() { return cameraPositionQuantization; }
    public void SetCameraPositionQuantization(float value) { cameraPositionQuantization = Mathf.Max(0.0f, value); ResetCameraPositions(); }
    public bool GetAlwaysUpdate() { return IsCombinedRenderingMode() || alwaysUpdate; }
    public void SetAlwaysUpdate(bool value) { alwaysUpdate = value; ResetCameraPositions(); }
    public void ToggleAlwaysUpdate() { SetAlwaysUpdate(!alwaysUpdate); }
    public bool GetUseVrcLightVolumes() { return useVrcLightVolumes; }
    public void SetUseVrcLightVolumes(bool value) { if (!CanModifyGlobalState()) return; EnsureLocalOwnership(); useVrcLightVolumes = value; ApplyMaterialSettingsToSelectedObject(); RequestSyncedStateUpdate(); }
    public void ToggleVrcLightVolumes() { SetUseVrcLightVolumes(!useVrcLightVolumes); }
    public float GetAntiAliasing() { return antiAliasing; }
    public void SetAntiAliasing(float value) { overrideMaterialProperties = true; antiAliasing = Mathf.Clamp(value, 0.0f, 3.0f); ApplyMaterialSettingsToSelectedObject(); }
    public float GetLightVolumeIntensity() { return lightVolumeIntensity; }
    public void SetLightVolumeIntensity(float value) { overrideMaterialProperties = true; lightVolumeIntensity = Mathf.Clamp(value, 0.0f, 4.0f); ApplyMaterialSettingsToSelectedObject(); }
    public void SetGaussianScale(float value) { if (!CanModifyGlobalState()) return; EnsureLocalOwnership(); overrideMaterialProperties = true; gaussianScale = Mathf.Clamp(value, 0.0f, 2.0f); ApplyMaterialSettingsToSelectedObject(); RequestSyncedStateUpdate(); }
    public void SetAlphaCutoff(float value) { overrideMaterialProperties = true; alphaCutoff = Mathf.Clamp(value, 0.005f, 0.3f); ApplyMaterialSettingsToSelectedObject(); }
    public float GetAlphaCull() { return alphaCull; }
    public void SetAlphaCull(float value) { overrideMaterialProperties = true; alphaCull = Mathf.Clamp(value, 0.005f, 0.3f); ApplyMaterialSettingsToSelectedObject(); }
    public float GetLODCull() { return lodCull; }
    public void SetLODCull(float value) { overrideMaterialProperties = true; lodCull = Mathf.Clamp(value, 0.0f, 0.1f); ApplyMaterialSettingsToSelectedObject(); }

    bool QualityPresetMatches(float cull, float cutoff) { return Mathf.Abs(alphaCull - cull) < 0.001f && Mathf.Abs(alphaCutoff - cutoff) < 0.001f; }
    public int GetQualityPresetIndex()
    {
        if (QualityPresetMatches(0.15f, 0.15f)) return 0;
        if (QualityPresetMatches(0.07f, 0.1f)) return 1;
        if (QualityPresetMatches(0.04f, 0.04f)) return 2;
        if (QualityPresetMatches(0.01f, 0.01f)) return 3;
        return -1;
    }
    void ApplyQualityPreset(float cull, float cutoff) { overrideMaterialProperties = true; alphaCull = cull; alphaCutoff = cutoff; ApplyMaterialSettingsToSelectedObject(); }
    public void SetQualityVeryLow() { ApplyQualityPreset(0.15f, 0.15f); }
    public void SetQualityLow() { ApplyQualityPreset(0.07f, 0.1f); }
    public void SetQualityMedium() { ApplyQualityPreset(0.04f, 0.04f); }
    public void SetQualityHigh() { ApplyQualityPreset(0.01f, 0.01f); }

    int GetCombinedRenderedSplatCount()
    {
        int totalCount = 0;
        for (int i = 0; i < _sceneSplats.Length; i++)
        {
            if (!IsSourceActive(i))
            {
                continue;
            }
            if (!TryGetSplatSource(_sceneSplats[i], out MeshRenderer renderer, out Material primaryMaterial, out Texture positions, out int count))
            {
                continue;
            }
            totalCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, totalCount + count);
        }
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            if (!IsLODObjectActive(i))
            {
                continue;
            }
            int effectiveBudget = GetEffectiveCombinedLodSplatBudget();
            int lodBound = effectiveBudget > 0 ? Mathf.Max(0, effectiveBudget - totalCount) : _sceneLods[i].GetMaxLOD0SplatCount();
            totalCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, totalCount + lodBound);
            if (effectiveBudget > 0)
            {
                break;
            }
        }
        return totalCount;
    }

    public int GetCurrentRenderedSplatCount()
    {
        if (!EnsureInitialized())
        {
            return 0;
        }
        if (IsCombinedRenderingMode())
        {
            return GetCombinedRenderedSplatCount();
        }
        GaussianSplatObject splat = EnsureCurrentSourceSelected() ? GetCurrentSplat() : null;
        return TryGetSplatSource(splat, out MeshRenderer renderer, out Material primaryMaterial, out Texture positions, out int count) ? count : 0;
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    public int GetEditorReadbackRenderedSplatCount()
    {
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null ? sceneCombiner.GetEditorReadbackRenderedSplatCount() : 0;
    }

    public int GetEditorReadbackReservedSplatCount()
    {
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null ? sceneCombiner.GetEditorReadbackReservedSplatCount() : 0;
    }

    public float GetEditorReadbackAlpha()
    {
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null ? sceneCombiner.GetEditorReadbackAlpha() : 0.0f;
    }
#endif

    public string GetCurrentSplatName()
    {
        if (!EnsureInitialized())
        {
            return "None";
        }
        if (IsCombinedRenderingMode())
        {
            GaussianSplatCombiner combined = ResolveCombiner();
            return combined != null ? combined.GetCombinedObjectName() : "Combined";
        }
        GaussianSplatObject splat = EnsureCurrentSourceSelected() ? GetCurrentSplat() : null;
        return splat != null ? splat.GetDisplayName() : "None";
    }

    public GameObject GetCurrentSplatObject()
    {
        if (!EnsureInitialized())
        {
            return null;
        }
        if (IsCombinedRenderingMode())
        {
            GaussianSplatCombiner combined = ResolveCombiner();
            return combined != null ? combined.GetCombinedObject() : null;
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
            UpdateSourceVisibility();
            return;
        }
        EnsureCurrentSourceSelected();
        UpdateSourceVisibility();
        ApplyMaterialSettingsToSelectedObject();
    }

    public void NotifyLODObjectEnabled(GaussianSplatLODObject lodObject)
    {
        if (!EnsureInitialized() || lodObject == null || !lodObject.gameObject.activeInHierarchy)
        {
            return;
        }
        RegisterRuntimeLODObject(lodObject);
        ResetCameraPositions();
        if (IsCombinedRenderingMode())
        {
            UpdateSourceVisibility();
        }
    }

    public void SelectSplatObject(GaussianSplatObject selectedSplatObject)
    {
        if (!CanModifyGlobalState() || !EnsureInitialized() || selectedSplatObject == null || !selectedSplatObject.gameObject.activeInHierarchy)
        {
            return;
        }
        RegisterRuntimeSplatObject(selectedSplatObject);
        if (IsCombinedRenderingMode())
        {
            ResetCameraPositions();
            UpdateSourceVisibility();
            return;
        }
        int sourceIndex = FindSourceIndex(selectedSplatObject);
        if (sourceIndex < 0)
        {
            return;
        }
        if (_currentSourceIndex != sourceIndex)
        {
            _currentSourceIndex = sourceIndex;
            ResetCameraPositions();
        }
        UpdateSourceVisibility();
        ApplyMaterialSettingsToSelectedObject();
    }

    public void NotifySplatObjectDisabled(GaussianSplatObject disabledSplatObject)
    {
        if (!EnsureInitialized() || disabledSplatObject == null)
        {
            return;
        }
        if (IsCombinedRenderingMode())
        {
            ResetCameraPositions();
            UpdateSourceVisibility();
            return;
        }
        int sourceIndex = FindSourceIndex(disabledSplatObject);
        if (sourceIndex != _currentSourceIndex)
        {
            UpdateSourceVisibility();
            return;
        }
        // The active source was disabled; re-select the next available splat.
        _currentSourceIndex = FindFirstActiveSourceIndex();
        ResetCameraPositions();
        UpdateSourceVisibility();
        ApplyMaterialSettingsToSelectedObject();
    }

    public void NotifyLODObjectDisabled(GaussianSplatLODObject lodObject)
    {
        if (!EnsureInitialized() || lodObject == null)
        {
            return;
        }
        ResetCameraPositions();
        if (IsCombinedRenderingMode())
        {
            UpdateSourceVisibility();
        }
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

    bool EnsureRenderTextureCreated(RenderTexture renderTexture, string label)
    {
        if (renderTexture == null)
        {
            Debug.LogError(label + " RenderTexture reference is missing.");
            return false;
        }
        if (renderTexture.IsCreated())
        {
            return true;
        }
        renderTexture.Create();
        if (renderTexture.IsCreated())
        {
            return true;
        }
        Debug.LogError(label + " RenderTexture could not be created at runtime: " + renderTexture.name + " (" + renderTexture.width + "x" + renderTexture.height + ", " + renderTexture.format + ", " + renderTexture.dimension + ")");
        return false;
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
        GaussianSplatCombiner combined = ResolveCombiner();
        if (keyValueMat == null)
        {
            keyValueMat = _radixSort.computeKeyValues;
        }
        if (splatRenderOrder == null || splatRenderOrderPhoto == null)
        {
            Debug.LogError("Splat Render Order textures are not assigned. Please assign RenderTextures.");
            return false;
        }
        if (!EnsureRenderTextureCreated(splatRenderOrder, "Splat render order")
            || !EnsureRenderTextureCreated(splatRenderOrderPhoto, "Splat render order photo")
            || !EnsureRenderTextureCreated(_radixSort.keyValues0, "RadixSort keyValues0")
            || !EnsureRenderTextureCreated(_radixSort.keyValues1, "RadixSort keyValues1")
            || !EnsureRenderTextureCreated(_radixSort.histograms, "RadixSort histograms")
            || !EnsureRenderTextureCreated(_radixSort.prefixSums, "RadixSort prefixSums")
            || (combined != null && !combined.EnsureResourcesCreated()))
        {
            Debug.LogError("Gaussian splat render textures could not be created at runtime.");
            return false;
        }
        if (_completedCameraPos == null || _completedCameraPos.Length < MAX_CAMERA_COUNT || _completedCameraWorldPos == null || _completedCameraWorldPos.Length < MAX_CAMERA_COUNT)
        {
            _completedCameraPos = new Vector3[MAX_CAMERA_COUNT];
            _completedCameraWorldPos = new Vector3[MAX_CAMERA_COUNT];
            _hasCompletedSort = new bool[MAX_CAMERA_COUNT];
            ResetCameraPositions();
        }
        if (!_runtimeCacheValid)
        {
            RefreshRuntimeCache();
            UpdateSourceVisibility();
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
            if (material.HasProperty("_GS_RenderOrderPhoto")) material.SetTexture("_GS_RenderOrderPhoto", splatRenderOrderPhoto);
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
        _sortedRenderer = null;
        GaussianSplatCombiner combined = ResolveCombiner();
        if (combined == null || !combined.BindRenderOrder(splatRenderOrder, splatRenderOrderPhoto, out _sortedRenderer, out Material primaryMaterial, out Texture positions, out int count))
        {
            return false;
        }
        return BindKeyValuePositions(primaryMaterial, positions, count);
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

    void OnScreenSortPublished()
    {
        if (IsCombinedRenderingMode())
        {
            GaussianSplatCombiner combined = ResolveCombiner();
            if (combined != null) combined.SetRendererEnabled(true);
            return;
        }
        GaussianSplatObject splat = GetCurrentSplat();
        if (splat != null)
        {
            splat.ShowSorted();
        }
    }

    void SetSortCameraPos(Vector3 worldCameraPos)
    {
        keyValueMat.SetVector("_CameraPos", _sortedRenderer.transform.InverseTransformPoint(worldCameraPos));
    }

    bool UpdateCombinedTexturesForSort(GaussianSplatCombiner combined, Vector3 screenCamPos, Vector3 lodCameraPos, Vector3 lodCameraForward, Vector3 photoCamPos, bool updatePhotoCameraColors, bool adaptLodSelection, bool useEditorOps)
    {
        return combined != null
            && combined.UpdateTexturesWithPhotoFlag(_sceneSplats, _sceneLods, screenCamPos, lodCameraPos, lodCameraForward, photoCamPos, updatePhotoCameraColors, GetEffectiveCombinedLodSplatBudget(), adaptLodSelection, useEditorOps)
            && UpdateSortBinding();
    }

    void SortCameraViews(Vector3 screenCamPos, Vector3 screenCamForward, Vector3 photoCamPos, bool sortPhotoCamera, bool useEditorOps)
    {
        if (!EnsureInitialized())
        {
            return;
        }
        if (!IsCombinedRenderingMode() && !EnsureCurrentSourceSelected())
        {
            return;
        }
        GaussianSplatCombiner combined = IsCombinedRenderingMode() ? ResolveCombiner() : null;
        if (IsCombinedRenderingMode() && combined != null)
        {
            combined.SetLodSplatTargetScale(GetEffectiveCombinedLodTargetScale());
            combined.SetLodDirectionalBias(GetCombinedLodDirectionalBias());
        }
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            if (IsCombinedRenderingMode() && (combined == null || !UpdateCombinedTexturesForSort(combined, screenCamPos, screenCamPos, screenCamForward, photoCamPos, sortPhotoCamera, true, true)))
            {
                return;
            }
            if (!IsCombinedRenderingMode() && !UpdateSortBinding())
            {
                return;
            }
            // Editor previews need a blocking full sort for the visible SceneView camera.
            SetSortCameraPos(screenCamPos);
            _radixSort.RunFullSortForEditor(splatRenderOrder, SCREEN_CAMERA_ID);
            _completedCameraPos[SCREEN_CAMERA_ID] = QuantizePosition(screenCamPos);
            _completedCameraWorldPos[SCREEN_CAMERA_ID] = screenCamPos;
            _hasCompletedSort[SCREEN_CAMERA_ID] = true;
            if (sortPhotoCamera)
            {
                SetSortCameraPos(photoCamPos);
                _radixSort.RunFullSortForEditor(splatRenderOrderPhoto, PHOTO_CAMERA_ID);
                _completedCameraPos[PHOTO_CAMERA_ID] = QuantizePosition(photoCamPos);
                _completedCameraWorldPos[PHOTO_CAMERA_ID] = photoCamPos;
                _hasCompletedSort[PHOTO_CAMERA_ID] = true;
            }
            OnScreenSortPublished();
            return;
        }
#endif

        if (IsCombinedRenderingMode())
        {
            if (combined == null || !UpdateCombinedTexturesForSort(combined, screenCamPos, screenCamPos, screenCamForward, photoCamPos, sortPhotoCamera, true, false))
            {
                return;
            }
            SetSortCameraPos(screenCamPos);
            _radixSort.RunFullSort(splatRenderOrder, SCREEN_CAMERA_ID);
            _completedCameraPos[SCREEN_CAMERA_ID] = QuantizePosition(screenCamPos);
            _completedCameraWorldPos[SCREEN_CAMERA_ID] = screenCamPos;
            _hasCompletedSort[SCREEN_CAMERA_ID] = true;
            if (sortPhotoCamera)
            {
                SetSortCameraPos(photoCamPos);
                _radixSort.RunFullSort(splatRenderOrderPhoto, PHOTO_CAMERA_ID);
                _completedCameraPos[PHOTO_CAMERA_ID] = QuantizePosition(photoCamPos);
                _completedCameraWorldPos[PHOTO_CAMERA_ID] = photoCamPos;
                _hasCompletedSort[PHOTO_CAMERA_ID] = true;
            }
            OnScreenSortPublished();
            return;
        }

        if (!UpdateSortBinding())
        {
            return;
        }

        SetSortCameraPos(screenCamPos);
        _radixSort.RunFullSort(splatRenderOrder, SCREEN_CAMERA_ID);
        _completedCameraPos[SCREEN_CAMERA_ID] = QuantizePosition(screenCamPos);
        _completedCameraWorldPos[SCREEN_CAMERA_ID] = screenCamPos;
        _hasCompletedSort[SCREEN_CAMERA_ID] = true;

        if (sortPhotoCamera)
        {
            SetSortCameraPos(photoCamPos);
            _radixSort.RunFullSort(splatRenderOrderPhoto, PHOTO_CAMERA_ID);
            _completedCameraPos[PHOTO_CAMERA_ID] = QuantizePosition(photoCamPos);
            _completedCameraWorldPos[PHOTO_CAMERA_ID] = photoCamPos;
            _hasCompletedSort[PHOTO_CAMERA_ID] = true;
        }
        OnScreenSortPublished();
    }

    void Update()
    {
        DisableMsaaInGame();
        if (!EnsureInitialized() || VRCCameraSettings.ScreenCamera == null)
        {
            return;
        }
        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        Vector3 screenCamForward = VRCCameraSettings.ScreenCamera.Rotation * Vector3.forward;
        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        bool sortPhotoCamera = photoCam != null && photoCam.Active;
        SortCameraViews(screenCamPos, screenCamForward, sortPhotoCamera ? photoCam.Position : screenCamPos, sortPhotoCamera, false);
    }

    public override void OnDeserialization()
    {
        if (!EnsureInitialized())
        {
            return;
        }
        ApplyMaterialSettingsToSelectedObject();
    }
}

}
