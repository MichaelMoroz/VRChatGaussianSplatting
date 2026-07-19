using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 2;
    // Number of pre-baked combined-resource sets the renderer can switch between at runtime by LOD budget,
    // without reallocating. VRAM COST: each tier holds its own combined position/rotation/scale/color/
    // colorsCamera textures + radix sort buffers + render-order RTs, so total VRAM is roughly the sum over
    // tiers. Lower tiers are sized to their (smaller) element count, so it is NOT a flat 4x of the top tier,
    // but it is still several times a single set — the deliberate tradeoff for realloc-free quality switching.
    public const int COMBINED_BUCKET_TIER_COUNT = 4;
    public const int COMBINED_BUCKET_HIGH_TIER = COMBINED_BUCKET_TIER_COUNT - 1;
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;
    const int SCREEN_CAMERA_ID = 0;
    const int PHOTO_CAMERA_ID = 1;
    const int DEFAULT_START_RENDER_QUEUE = 4050;
    const float DEFAULT_ALPHA_CUTOFF = 0.04f;
    const float DEFAULT_ALPHA_CULL = 0.04f;
    const int DEFAULT_COMBINED_LOD_SPLAT_BUDGET_PC = 1 << 22;      // 4,194,304
    const int DEFAULT_COMBINED_LOD_SPLAT_BUDGET_ANDROID = 1 << 18; // 262,144
    const int MIN_COMBINED_LOD_SPLAT_BUDGET = 1 << 16; // 64K floor for the runtime LOD-cap slider + quality presets
    const float DEFAULT_COMBINED_LOD_TARGET_SCALE = 0.95f;
    const float DEFAULT_COMBINED_LOD_DIRECTIONAL_BIAS = 2.0f;
    const float LOD_DENSITY_ALPHA_NUMERATOR = 4.0f * 1024.0f * 12.0f;
    const int STARTUP_RENDER_SUPPRESSION_FRAMES = 8;

    [System.NonSerialized] Vector3[] _completedCameraPos;
    [System.NonSerialized] Vector3[] _completedCameraWorldPos;
    [System.NonSerialized] bool[] _hasCompletedSort;
    [System.NonSerialized] RadixSort _radixSort;
    [System.NonSerialized] Material keyValueMat;
    [System.NonSerialized] MeshRenderer _sortedRenderer;
    [System.NonSerialized] int _activeCombinedBucketTier = -1;
    [System.NonSerialized] bool _startupRenderSuppressionInitialized;
    [System.NonSerialized] int _startupRenderSuppressionFramesRemaining;

    // Runtime RT-pool bucket selection. The bound bucket only ever grows within a session (a touched bucket
    // can't be freed in Udon, so binding down would allocate a smaller surface on top of the bigger one), and
    // upgrades are debounced so a transient spike (e.g. an object-switch overlap frame) never commits a bigger
    // bucket. While demand exceeds the committed bucket the combine is skipped for that frame.
    const float BUCKET_UPGRADE_DEBOUNCE_SECONDS = 0.2f; // framerate-independent so it holds in VR and on Quest
    [System.NonSerialized] int _committedBucket = -1;
    [System.NonSerialized] int _pendingBucket = -1;
    [System.NonSerialized] float _pendingSinceTime = 0.0f;
    [System.NonSerialized] bool _bucketOverCapacity;
    [System.NonSerialized] int _currentRequired;

    // Pool RT bucket capacities: 256K, 1M, 4M, 16M == 1 << (18 + 2*i). Must match GaussianSplatRTPool.
    // (internal so the test assembly can verify these match the editor pool's values.)
    internal static int BucketCapacity(int bucket) { return 1 << (18 + 2 * Mathf.Clamp(bucket, 0, COMBINED_BUCKET_HIGH_TIER)); }
    internal static int BucketIndexForCount(int count)
    {
        for (int b = 0; b < COMBINED_BUCKET_TIER_COUNT; b++)
        {
            if (count <= BucketCapacity(b)) return b;
        }
        return COMBINED_BUCKET_HIGH_TIER;
    }

    [System.NonSerialized] GaussianSplatObject[] _sceneLods = new GaussianSplatObject[0];
    [System.NonSerialized] bool _runtimeCacheValid;

    [HideInInspector, SerializeField] GameObject[] cachedSceneLODObjects;
    [SerializeField] GaussianSplatCombiner combiner;
    [Tooltip("Combined LOD splat cap for PC builds. 0 disables the cap.")]
    [SerializeField] int combinedLodSplatBudgetPC = DEFAULT_COMBINED_LOD_SPLAT_BUDGET_PC;
    [Tooltip("Combined LOD splat cap for Android builds. 0 disables the cap.")]
    [SerializeField] int combinedLodSplatBudgetAndroid = DEFAULT_COMBINED_LOD_SPLAT_BUDGET_ANDROID;
    [SerializeField] int requestedCombinedLodSplatBudget;
    [Tooltip("GPU LOD selection target as a fraction of the active LOD cap. Lower values leave headroom for alpha solver overshoot.")]
    [SerializeField] float combinedLodTargetScale = DEFAULT_COMBINED_LOD_TARGET_SCALE;
    [Tooltip("Directional LOD distance divisor. 1 disables the camera direction bias; 2 halves effective distance for chunks directly in front of the camera.")]
    [Range(1.0f, 16.0f)] [SerializeField] float combinedLodDirectionalBias = DEFAULT_COMBINED_LOD_DIRECTIONAL_BIAS;
    [Tooltip("Max splats per projected screen pixel, estimated as a lower bound on the LOD solver's log-alpha. 0 disables the screen-density alpha clamp.")]
    [SerializeField] float lodMaxSplatsPerPixel = 0.5f;
    [Tooltip("Quality preset applied once at world start. 'Keep Inspector Settings' (-1) uses the Startup LOD Capacity fraction below instead of applying a preset.")]
    [SerializeField] int startupQualityPreset = -1;
    [Tooltip("Startup LOD capacity as a fraction of THIS platform's cap (scales per-platform). Used when Startup Quality is 'Keep Inspector Settings'. 1 = full cap.")]
    [Range(0.0f, 1.0f)] [SerializeField] float startupLodCapacity = 1.0f;
    [HideInInspector, SerializeField] int combinedLodSettingsVersion;
    [Tooltip("Editor-only preview that recolors combined LOD splats by the currently selected GPU LOD.")]
    [SerializeField] bool debugDrawLodGrid;
    [Tooltip("Editor-only preview mode that renders splats as opaque ray-traced ellipsoids and writes ellipsoid surface depth.")]
    [SerializeField] bool debugRenderOpaqueEllipsoids;
    [Tooltip("Editor-only preview that draws a wireframe bounding box per chunk (LOD and non-LOD splats).")]
    [SerializeField] bool debugDrawChunkBounds;
    [Tooltip("Editor-only preview that draws, per LOD chunk, a cube centered on the splat center-of-mass with surface area equal to the stored covariance area.")]
    [SerializeField] bool debugDrawChunkCenterArea;

    [Header("Render Settings")]
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    // Per-frame render-order holders are rebound from the serialized bucket arrays; never serialize these.
    [System.NonSerialized] public RenderTexture splatRenderOrder;
    [System.NonSerialized] public RenderTexture splatRenderOrderPhoto;
    [HideInInspector, SerializeField] RenderTexture[] splatRenderOrderByBucket;
    [HideInInspector, SerializeField] RenderTexture[] splatRenderOrderPhotoByBucket;

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [SerializeField] public bool overrideMaterialProperties;
    [SerializeField] bool overrideRenderQueue;
    [SerializeField] int startRenderQueue = DEFAULT_START_RENDER_QUEUE;
    [Range(0, 3)] [SerializeField] int requestedSHBand = 3;
    [Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
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
    [SerializeField] bool useVrcLightVolumes;
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

    void RestartStartupRenderSuppressionWindow()
    {
        _startupRenderSuppressionInitialized = true;
        _startupRenderSuppressionFramesRemaining = STARTUP_RENDER_SUPPRESSION_FRAMES;
    }

    void EnsureStartupRenderSuppressionWindowInitialized()
    {
        if (!_startupRenderSuppressionInitialized)
        {
            RestartStartupRenderSuppressionWindow();
        }
    }

    bool SuppressStartupRendering()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return false;
        }
#endif
        EnsureStartupRenderSuppressionWindowInitialized();
        return _startupRenderSuppressionFramesRemaining > 0;
    }

    void ConsumeStartupRenderSuppressionFrame()
    {
        if (_startupRenderSuppressionFramesRemaining > 0)
        {
            _startupRenderSuppressionFramesRemaining--;
        }
    }

    void InvalidateCombinedSort()
    {
        ResetCameraPositions();
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            QueueEditorRefresh();
            UnityEditor.SceneView.RepaintAll();
        }
#endif
    }

    void ResetRuntimeCache()
    {
        _runtimeCacheValid = false;
        _sceneLods = new GaussianSplatObject[0];
        _sortedRenderer = null;
        ResetCameraPositions();
        RestartStartupRenderSuppressionWindow();
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

    static bool TryGetLODSource(GaussianSplatObject lodObject)
    {
        return lodObject != null && lodObject.IsRenderable();
    }

    bool IsFusedLODSource(GaussianSplatObject lodObject)
    {
        if (lodObject == null)
        {
            return false;
        }
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null && sceneCombiner.ContainsFusedLODObject(lodObject.gameObject);
    }

    bool CanUseLODSource(GaussianSplatObject lodObject)
    {
        return TryGetLODSource(lodObject) || IsFusedLODSource(lodObject);
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
        GameObject[] lodRoots = cachedSceneLODObjects;
        if (lodRoots == null || lodRoots.Length == 0)
        {
            ResetRuntimeCache();
            _runtimeCacheValid = true;
            return;
        }
        int validLodCount = 0;
        for (int i = 0; i < lodRoots.Length; i++)
        {
            GameObject root = lodRoots[i];
            GaussianSplatObject lodObject = root != null ? root.GetComponent<GaussianSplatObject>() : null;
            if (CanUseLODSource(lodObject))
            {
                validLodCount++;
            }
        }
        _sceneLods = new GaussianSplatObject[validLodCount];
        int writeIndex = 0;
        for (int i = 0; i < lodRoots.Length; i++)
        {
            GameObject root = lodRoots[i];
            GaussianSplatObject lodObject = root != null ? root.GetComponent<GaussianSplatObject>() : null;
            if (!CanUseLODSource(lodObject))
            {
                continue;
            }
            _sceneLods[writeIndex] = lodObject;
            writeIndex++;
        }
        _runtimeCacheValid = true;
    }

    bool IsLODObjectActive(int index) { return index >= 0 && index < _sceneLods.Length && _sceneLods[index] != null && _sceneLods[index].gameObject.activeInHierarchy && CanUseLODSource(_sceneLods[index]); }

    int FindLODObjectIndex(GaussianSplatObject lodObject)
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

    // Append root to a cached GameObject[] only if it is not already present, returning the
    // (possibly grown) array. Shared by the splat/LOD register paths, which otherwise duplicated
    // this resize loop. The typed _sceneSplats/_sceneLods arrays cannot share this without
    // generics (Udon-blocked), so those appends stay in their respective methods.
    static GameObject[] AppendCachedRootUnique(GameObject[] roots, GameObject root)
    {
        int count = roots != null ? roots.Length : 0;
        for (int i = 0; i < count; i++)
        {
            if (roots[i] == root)
            {
                return roots;
            }
        }
        GameObject[] grown = new GameObject[count + 1];
        for (int i = 0; i < count; i++)
        {
            grown[i] = roots[i];
        }
        grown[count] = root;
        return grown;
    }

    void RegisterRuntimeLODObject(GaussianSplatObject lodObject)
    {
        if (!CanUseLODSource(lodObject))
        {
            return;
        }
        GameObject root = lodObject.gameObject;
        cachedSceneLODObjects = AppendCachedRootUnique(cachedSceneLODObjects, root);
        if (FindLODObjectIndex(lodObject) >= 0)
        {
            return;
        }
        GaussianSplatObject[] sceneLods = new GaussianSplatObject[_sceneLods.Length + 1];
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            sceneLods[i] = _sceneLods[i];
        }
        sceneLods[_sceneLods.Length] = lodObject;
        _sceneLods = sceneLods;
    }


    void UpdateSourceVisibility()
    {
        // Let combine/sort run during startup warmup, but keep the renderer invisible until the order RTs
        // have had a few successful frames to populate.
        GaussianSplatCombiner combined = ResolveCombiner();
        if (combined != null) combined.SetRendererEnabled(!SuppressStartupRendering());
    }

    // The pool bucket (== quality-tier slot) to bind this frame. Selected by the live rendered count, grown
    // only (never shrunk) and only after the elevated demand persists past the debounce window; a transient
    // spike never commits a bigger bucket. Also flags whether demand currently exceeds the committed bucket
    // (the combine is skipped that frame so it never overflows).
    int GetCombinedBucketTier()
    {
        int required = GetCombinedRenderedSplatCount();
        _currentRequired = required;
        int needed = BucketIndexForCount(required);
        if (_committedBucket < 0)
        {
            // Not yet initialized (scene cache may still be empty on the first frames). Until there is real
            // demand, return -1 so the caller keeps the baked baseline bucket and does NOT swap to / allocate
            // bucket 0 (that would touch a bucket that may never be used). Commit immediately on first demand
            // so there are no black startup frames.
            if (required <= 0)
            {
                _bucketOverCapacity = true;
                return -1;
            }
            _committedBucket = needed;
        }
        if (needed > _committedBucket)
        {
            if (needed != _pendingBucket)
            {
                _pendingBucket = needed;
                _pendingSinceTime = Time.time;
            }
            if (Time.time - _pendingSinceTime >= BUCKET_UPGRADE_DEBOUNCE_SECONDS)
            {
                _committedBucket = needed;
                _pendingBucket = -1;
            }
        }
        else
        {
            _pendingBucket = -1;
        }
        _bucketOverCapacity = required > BucketCapacity(_committedBucket);
        return _committedBucket;
    }

    void SelectBucketResources()
    {
        // All-or-nothing: only switch to a tier if EVERY component (render order + photo order + radix sort
        // buffers + combiner textures) has that tier baked. Swapping them independently could mix resources
        // from different tiers (render order indexing wrong-sized sort/combined textures -> garbage). If the
        // desired tier isn't fully baked, fall back to the highest fully-baked tier; if none are baked (scenes
        // predating the quality-tier bake), don't swap at all and keep the baseline (non-tier) resources.
        if (!_runtimeCacheValid)
        {
            // The scene LOD cache (_sceneLods) is populated later this frame; selecting now would read a stale
            // count. Keep the baked baseline bucket + current pass state until the cache is ready.
            return;
        }
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        int desiredBucket = GetCombinedBucketTier();
        if (sceneCombiner != null)
        {
            // Enable only the pass chunks covering the live rendered count (draw fewer splats when fewer active).
            sceneCombiner.UpdateActivePassCount(_currentRequired);
        }
        if (desiredBucket < 0)
        {
            return; // not ready yet: keep the baked baseline bucket, don't swap/allocate
        }
        int tier = ResolveAvailableBucketTier(desiredBucket, sceneCombiner);
        if (tier < 0)
        {
            return;
        }
        TryGetTierTexture(splatRenderOrderByBucket, tier, out RenderTexture order);
        splatRenderOrder = order;
        TryGetTierTexture(splatRenderOrderPhotoByBucket, tier, out RenderTexture orderPhoto);
        splatRenderOrderPhoto = orderPhoto;
        if (_radixSort != null)
        {
            _radixSort.UseBucketResources(tier);
        }
        if (sceneCombiner != null)
        {
            sceneCombiner.UseBucketResources(tier);
        }
        if (_activeCombinedBucketTier != tier)
        {
            _activeCombinedBucketTier = tier;
            ResetCameraPositions();
            RestartStartupRenderSuppressionWindow();
        }
    }

    // The desired tier if it is fully baked across all components, else the highest fully-baked tier, else -1
    // (no tier set baked -> caller keeps the baseline, non-tier resources).
    int ResolveAvailableBucketTier(int desired, GaussianSplatCombiner sceneCombiner)
    {
        if (IsBucketTierFullyAvailable(desired, sceneCombiner))
        {
            return desired;
        }
        for (int tier = COMBINED_BUCKET_HIGH_TIER; tier >= 0; tier--)
        {
            if (IsBucketTierFullyAvailable(tier, sceneCombiner))
            {
                return tier;
            }
        }
        return -1;
    }

    bool IsBucketTierFullyAvailable(int tier, GaussianSplatCombiner sceneCombiner)
    {
        return TryGetTierTexture(splatRenderOrderByBucket, tier, out RenderTexture order)
            && TryGetTierTexture(splatRenderOrderPhotoByBucket, tier, out RenderTexture orderPhoto)
            && (_radixSort == null || _radixSort.HasBucketResources(tier))
            && (sceneCombiner == null || sceneCombiner.HasBucketResources(tier));
    }

    static bool TryGetTierTexture(RenderTexture[] textures, int tier, out RenderTexture texture)
    {
        texture = textures != null && tier >= 0 && tier < textures.Length ? textures[tier] : null;
        return texture != null;
    }

    bool BindDefaultSortResources()
    {
        if (splatRenderOrder != null && splatRenderOrderPhoto != null)
        {
            return true;
        }
        int maxTier = splatRenderOrderByBucket != null ? splatRenderOrderByBucket.Length - 1 : -1;
        for (int tier = maxTier; tier >= 0; tier--)
        {
            if (TryGetTierTexture(splatRenderOrderByBucket, tier, out RenderTexture order)
                && TryGetTierTexture(splatRenderOrderPhotoByBucket, tier, out RenderTexture orderPhoto))
            {
                splatRenderOrder = order;
                splatRenderOrderPhoto = orderPhoto;
                return true;
            }
        }
        return false;
    }

    public int GetEffectiveCombinedLodSplatBudget()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        // Edit-mode preview renders with the SELECTED startup setting so the editor matches the world's
        // startup state: a preset's quality fraction, or the relative Startup LOD Capacity for "Keep
        // Inspector Settings". (Play-in-editor falls through to the live requested/cap path below.)
        if (!Application.isPlaying)
        {
            return GetCombinedLodSplatBudgetAtQuality(StartupLodCapacityFraction());
        }
#endif
        if (requestedCombinedLodSplatBudget > 0)
        {
            return ClampCombinedLodSplatBudgetToSliderRange(requestedCombinedLodSplatBudget);
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        return Mathf.Max(0, combinedLodSplatBudgetAndroid);
#else
        return Mathf.Max(0, combinedLodSplatBudgetPC);
#endif
    }

    // The startup setting's LOD-capacity fraction: a preset's quality level, or the manual relative
    // Startup LOD Capacity when "Keep Inspector Settings" (-1) is chosen.
    float StartupLodCapacityFraction()
    {
        if (startupQualityPreset == 0) return 0.25f;  // Very Low = 1/4 cap
        if (startupQualityPreset == 1) return 0.5f;   // Low = 2/4 cap
        if (startupQualityPreset == 2) return 0.75f;  // Medium = 3/4 cap
        if (startupQualityPreset == 3) return 1.0f;   // High = 4/4 cap
        return Mathf.Clamp01(startupLodCapacity);
    }

    public int GetCombinedLodSplatBudgetPC() { return Mathf.Max(0, combinedLodSplatBudgetPC); }
    public int GetCombinedLodSplatBudgetAndroid() { return Mathf.Max(0, combinedLodSplatBudgetAndroid); }
    public int GetCombinedLodSplatBudgetSliderMin()
    {
        // Fixed 64K floor (not min(Android, PC)), clamped to the platform cap so min never exceeds max.
        return Mathf.Min(MIN_COMBINED_LOD_SPLAT_BUDGET, GetCombinedLodSplatBudgetSliderMax());
    }

    public int GetCombinedLodSplatBudgetSliderMax()
    {
        // The ceiling is THIS platform's cap, not max(Android, PC): the slider's upper bound and
        // ClampCombinedLodSplatBudgetToSliderRange both read this, so using the larger cap let an Android
        // build's runtime slider exceed its own cap (up to the PC cap). Matches GetEffectiveCombinedLodSplatBudget.
#if UNITY_ANDROID && !UNITY_EDITOR
        return GetCombinedLodSplatBudgetAndroid();
#else
        return GetCombinedLodSplatBudgetPC();
#endif
    }

    public int ClampCombinedLodSplatBudgetToSliderRange(int value)
    {
        int min = GetCombinedLodSplatBudgetSliderMin();
        int max = GetCombinedLodSplatBudgetSliderMax();
        if (max <= min)
        {
            return min;
        }
        return Mathf.Clamp(value, min, max);
    }

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
        if (requestedCombinedLodSplatBudget > 0)
        {
            requestedCombinedLodSplatBudget = ClampCombinedLodSplatBudgetToSliderRange(requestedCombinedLodSplatBudget);
        }
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
        if (requestedCombinedLodSplatBudget > 0)
        {
            requestedCombinedLodSplatBudget = ClampCombinedLodSplatBudgetToSliderRange(requestedCombinedLodSplatBudget);
        }
        ResetCameraPositions();
    }

    public void SetEffectiveCombinedLodSplatBudget(int value)
    {
        int clampedValue = ClampCombinedLodSplatBudgetToSliderRange(value);
        if (requestedCombinedLodSplatBudget == clampedValue)
        {
            return;
        }
        requestedCombinedLodSplatBudget = clampedValue;
        ResetCameraPositions();
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

        // Material settings are applied to the combined object.
        GaussianSplatCombiner combined = ResolveCombiner();
        if (combined != null) combined.ApplyMaterialSettings();
    }

    public void ApplyConfiguredMaterialSettingsForCombined(Material material) { ApplyConfiguredMaterialSettings(material, GetCurrentSHBand()); }
    public GaussianSplatCombiner GetCombiner() { return ResolveCombiner(); }
    public void SetCombiner(GaussianSplatCombiner value) { combiner = value; }

    public int GetSelectedSplatMaxSHBand()
    {
        // Combine renders every object; the SH-band selector reflects the max band across the active set.
        int maxBand = 0;
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            GaussianSplatObject splat = _sceneLods[i];
            if (splat != null && splat.gameObject.activeInHierarchy)
            {
                int b = splat.GetMaxSHBand();
                if (b > maxBand) maxBand = b;
            }
        }
        return maxBand;
    }
    public int GetCurrentSHBand() { return Mathf.Clamp(requestedSHBand, 0, GetSelectedSplatMaxSHBand()); }
    public void SetSHBand(int value)
    {
        int clampedValue = Mathf.Clamp(value, 0, 3);
        if (requestedSHBand == clampedValue)
        {
            return;
        }
        requestedSHBand = clampedValue;
        InvalidateCombinedSort();
        ApplyMaterialSettingsToSelectedObject();
    }
    public float GetCameraPositionQuantization() { return cameraPositionQuantization; }
    public void SetCameraPositionQuantization(float value) { cameraPositionQuantization = Mathf.Max(0.0f, value); ResetCameraPositions(); }
    public bool GetUseVrcLightVolumes() { return useVrcLightVolumes; }
    public void SetUseVrcLightVolumes(bool value) { useVrcLightVolumes = value; ApplyMaterialSettingsToSelectedObject(); }
    public void ToggleVrcLightVolumes() { SetUseVrcLightVolumes(!useVrcLightVolumes); }
    public float GetAntiAliasing() { return antiAliasing; }
    public void SetAntiAliasing(float value) { overrideMaterialProperties = true; antiAliasing = Mathf.Clamp(value, 0.0f, 3.0f); ApplyMaterialSettingsToSelectedObject(); }
    public float GetLightVolumeIntensity() { return lightVolumeIntensity; }
    public void SetLightVolumeIntensity(float value) { overrideMaterialProperties = true; lightVolumeIntensity = Mathf.Clamp(value, 0.0f, 4.0f); ApplyMaterialSettingsToSelectedObject(); }
    public void SetGaussianScale(float value) { overrideMaterialProperties = true; gaussianScale = Mathf.Clamp(value, 0.0f, 2.0f); ApplyMaterialSettingsToSelectedObject(); }
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
    int GetCombinedLodSplatBudgetAtQuality(float quality)
    {
        // A quality/capacity fraction maps to that exact fraction of THIS platform's cap (the presets use
        // 1/4, 2/4, 3/4, 4/4 of the cap), floored at the 64K slider minimum. Scales per-platform.
        int max = GetCombinedLodSplatBudgetSliderMax();
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(quality) * max), GetCombinedLodSplatBudgetSliderMin(), max);
    }

    void ApplyQualityPreset(float cull, float cutoff, float lodQuality)
    {
        overrideMaterialProperties = true;
        alphaCull = cull;
        alphaCutoff = cutoff;
        requestedCombinedLodSplatBudget = GetCombinedLodSplatBudgetAtQuality(lodQuality);
        ResetCameraPositions();
        ApplyMaterialSettingsToSelectedObject();
    }

    public void SetQualityVeryLow() { ApplyQualityPreset(0.15f, 0.15f, 0.25f); }  // 1/4 cap
    public void SetQualityLow() { ApplyQualityPreset(0.07f, 0.1f, 0.5f); }        // 2/4 cap
    public void SetQualityMedium() { ApplyQualityPreset(0.04f, 0.04f, 0.75f); }   // 3/4 cap
    public void SetQualityHigh() { ApplyQualityPreset(0.01f, 0.01f, 1.0f); }      // 4/4 cap

    void Start()
    {
        // Apply the startup setting once when the world loads. (Start only runs in play mode, so the editor
        // preview is unaffected.) A preset applies its quality; 'Keep Inspector Settings' (-1) resolves the
        // relative Startup LOD Capacity against this platform's cap so it scales per-platform.
        if (startupQualityPreset == 0) SetQualityVeryLow();
        else if (startupQualityPreset == 1) SetQualityLow();
        else if (startupQualityPreset == 2) SetQualityMedium();
        else if (startupQualityPreset == 3) SetQualityHigh();
        else
        {
            requestedCombinedLodSplatBudget = GetCombinedLodSplatBudgetAtQuality(startupLodCapacity);
            ResetCameraPositions();
        }
    }

    int GetCombinedRenderedSplatCount()
    {
        // Every object is a computed-LOD object, so the whole active set is thinnable: total =
        // min(thinnableSum, budget), clamped to the combined cap.
        int thinnableSum = 0;
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            if (!IsLODObjectActive(i))
            {
                continue;
            }
            thinnableSum = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, thinnableSum + _sceneLods[i].GetMaxLOD0SplatCount());
        }
        int effectiveBudget = GetEffectiveCombinedLodSplatBudget();
        int thinnable = effectiveBudget > 0 ? Mathf.Min(thinnableSum, Mathf.Max(0, effectiveBudget)) : thinnableSum;
        return Mathf.Min(MAX_COMBINED_SPLAT_COUNT, thinnable);
    }

    public int GetCurrentRenderedSplatCount()
    {
        return EnsureInitialized() ? GetCombinedRenderedSplatCount() : 0;
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

    public int GetTotalBakedSplatCount()
    {
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null ? sceneCombiner.GetTotalBakedSplatCount() : 0;
    }

    public long GetBakedSplatDataBytes()
    {
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null ? sceneCombiner.GetBakedSplatDataBytes() : 0;
    }

    public float GetEditorReadbackAlpha()
    {
        GaussianSplatCombiner sceneCombiner = ResolveCombiner();
        return sceneCombiner != null ? sceneCombiner.GetEditorReadbackAlpha() : 0.0f;
    }
#endif

    public void NotifyLODObjectEnabled(GaussianSplatObject lodObject)
    {
        if (!EnsureInitialized() || lodObject == null || !lodObject.gameObject.activeInHierarchy)
        {
            return;
        }
        RegisterRuntimeLODObject(lodObject);
        ResetCameraPositions();
        UpdateSourceVisibility();
    }

    public void NotifyLODObjectDisabled(GaussianSplatObject lodObject)
    {
        if (!EnsureInitialized() || lodObject == null)
        {
            return;
        }
        ResetCameraPositions();
        UpdateSourceVisibility();
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
        EnsureStartupRenderSuppressionWindowInitialized();
        SelectBucketResources();
        BindDefaultSortResources();
        _radixSort.BindDefaultBucketResources();
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
        return UpdateCombinedBinding();
    }

    void OnScreenSortPublished()
    {
        GaussianSplatCombiner combined = ResolveCombiner();
        if (combined == null)
        {
            return;
        }
        if (SuppressStartupRendering())
        {
            combined.SetRendererEnabled(false);
            ConsumeStartupRenderSuppressionFrame();
            return;
        }
        combined.SetRendererEnabled(true);
    }

    void SetSortCameraPos(Vector3 worldCameraPos)
    {
        keyValueMat.SetVector("_CameraPos", _sortedRenderer.transform.InverseTransformPoint(worldCameraPos));
    }

    // Screen-derived LOD alpha floor: x = min log2(alpha), y = focal length in pixels, z = max splats per
    // projected pixel. Runtime uses VRChat's main screen camera; editor preview passes the SceneView camera.
    Vector4 GetLodScreenParams(float editorPixelHeight, float editorFovYDeg)
    {
        float maxPerPixel = Mathf.Max(0.0f, lodMaxSplatsPerPixel);
        if (maxPerPixel <= 0.0f)
        {
            return new Vector4(-GaussianSplatObject.MAX_LOD_ALPHA_LOG2, 0.0f, 0.0f, 0.0f);
        }
        float pixelHeight = 1080.0f;
        float fovYDeg = 60.0f;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        // Editor preview cannot safely query VRCCameraSettings.ScreenCamera. Use the SceneView camera supplied
        // by the editor preCull path; fall back to the game view if this is called from another editor path.
        if (editorPixelHeight > 0.0f) pixelHeight = editorPixelHeight;
        else if (Screen.height > 0) pixelHeight = Screen.height;
        if (editorFovYDeg > 0.0f) fovYDeg = editorFovYDeg;
#else
        VRCCameraSettings screenCamera = VRCCameraSettings.ScreenCamera;
        if (screenCamera != null)
        {
            if (screenCamera.PixelHeight > 0) pixelHeight = screenCamera.PixelHeight;
            if (screenCamera.FieldOfView > 0.0f) fovYDeg = screenCamera.FieldOfView;
        }
#endif
        float tanHalf = Mathf.Tan(0.5f * fovYDeg * Mathf.Deg2Rad);
        float focalPx = tanHalf > 1e-4f ? 0.5f * pixelHeight / tanHalf : 0.0f;
        if (focalPx <= 0.0f)
        {
            return new Vector4(-GaussianSplatObject.MAX_LOD_ALPHA_LOG2, 0.0f, maxPerPixel, 0.0f);
        }
        float alphaSq = LOD_DENSITY_ALPHA_NUMERATOR / Mathf.Max(1e-6f, maxPerPixel * focalPx * focalPx);
        float minLogAlpha = 0.5f * Mathf.Log(Mathf.Max(alphaSq, 1e-20f), 2.0f);
        return new Vector4(minLogAlpha, focalPx, maxPerPixel, 0.0f);
    }

    bool UpdateCombinedTexturesForSort(GaussianSplatCombiner combined, Vector3 screenCamPos, Vector3 lodCameraPos, Vector3 lodCameraForward, Vector3 photoCamPos, bool updatePhotoCameraColors, int lodSplatBudget, Vector4 lodScreenParams, bool adaptLodSelection, bool forceMinLodAlpha, bool useEditorOps)
    {
        if (combined == null)
        {
            return false;
        }
        if (_bucketOverCapacity)
        {
            // Demand exceeds the committed bucket (the debounce window before an upgrade commits): skip the
            // combine so it never overflows the bound textures; keep last frame's combined buffer and just
            // rebind it for the sort.
            return UpdateSortBinding();
        }
        return combined.UpdateTextures(_sceneLods, screenCamPos, lodCameraPos, lodCameraForward, photoCamPos, updatePhotoCameraColors, lodSplatBudget, lodScreenParams, adaptLodSelection, forceMinLodAlpha, useEditorOps)
            && UpdateSortBinding();
    }

    void SortCameraViews(Vector3 screenCamPos, Vector3 screenCamForward, Vector3 photoCamPos, bool sortPhotoCamera, bool useEditorOps, float editorPixelHeight, float editorFovYDeg)
    {
        if (!EnsureInitialized())
        {
            return;
        }
        GaussianSplatCombiner combined = ResolveCombiner();
        if (combined != null)
        {
            combined.SetLodSplatTargetScale(GetEffectiveCombinedLodTargetScale());
            combined.SetLodDirectionalBias(GetCombinedLodDirectionalBias());
            combined.SetLodShBand(GetCurrentSHBand());
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            combined.SetEditorDebugLodColors(useEditorOps && debugDrawLodGrid);
#endif
        }
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            if (combined == null || !UpdateCombinedTexturesForSort(combined, screenCamPos, screenCamPos, screenCamForward, photoCamPos, sortPhotoCamera, GetEffectiveCombinedLodSplatBudget(), GetLodScreenParams(editorPixelHeight, editorFovYDeg), true, false, true))
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

        if (combined == null || !UpdateCombinedTexturesForSort(combined, screenCamPos, screenCamPos, screenCamForward, photoCamPos, sortPhotoCamera, GetEffectiveCombinedLodSplatBudget(), GetLodScreenParams(editorPixelHeight, editorFovYDeg), true, false, false))
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
        SortCameraViews(screenCamPos, screenCamForward, sortPhotoCamera ? photoCam.Position : screenCamPos, sortPhotoCamera, false, 0.0f, 0.0f);
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
