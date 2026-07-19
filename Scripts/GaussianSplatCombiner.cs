using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
#endif

namespace GaussianSplatting
{

/// <summary>
/// Owns the "combine all scene splats into one sorted render object" subsystem. The combined object
/// behaves like a single GaussianSplatObject (SH0) that the renderer drives through its sort/render
/// path. The renderer delegates all combine work to this component.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class GaussianSplatCombiner : UdonSharpBehaviour
{
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;

    [SerializeField] GaussianSplatRenderer gaussianSplatRenderer;
    [SerializeField] MeshRenderer combinedSortedRenderer;
    [SerializeField] RenderTextureFormat combinedPositionsFormat = RenderTextureFormat.ARGBFloat, combinedRotationsFormat = RenderTextureFormat.ARGB32, combinedScalesFormat = RenderTextureFormat.ARGBHalf, combinedColorsFormat = RenderTextureFormat.ARGB32, combinedColorsCameraFormat = RenderTextureFormat.ARGB32;
    [SerializeField, HideInInspector] bool combinedTextureFormatsInitialized = true;
    [SerializeField] int combinedStartRenderQueue = 4050;
    // Per-frame output holders are rebound from the serialized bucket arrays; never serialize these.
    [System.NonSerialized] RenderTexture combinedPositions, combinedRotations, combinedScales, combinedColors, combinedColorsCamera;
    [SerializeField, HideInInspector] RenderTexture[] combinedPositionsByBucket, combinedRotationsByBucket, combinedScalesByBucket, combinedColorsByBucket, combinedColorsCameraByBucket;
    [SerializeField] RenderTexture lodAlphaState;
    [SerializeField] RenderTexture lodAlphaStateScratch;
    // Ping-pong state swaps these holders only; the serialized refs above stay canonical.
    [System.NonSerialized] RenderTexture _lodAlphaFront;
    [System.NonSerialized] RenderTexture _lodAlphaBack;
    [SerializeField] int builtCombinedElementCount;

    // Content signature of the last unified fuse bake (object set + source textures + counts). The fuse
    // does heavy GPU readbacks, so the bake must run ONLY when its signature changes — never every refresh.
    // lodFusedSignature is the per-instance LAYOUT signature (object set + per-instance chunk metadata);
    // lodFusedSourceSignature hashes only the UNIQUE source set, which is all the heavy fused source
    // textures depend on (instances dedup). A duplicate add/remove changes layout but not source, so the
    // ~GB GPU source concat is skipped and only the small metadata textures rebuild.
    [SerializeField, HideInInspector] int lodFusedSignature;
    [SerializeField, HideInInspector] int lodFusedSourceSignature;
    // The pending-rebake queue (membership + target signature + debounce time) is transient editor-only state,
    // held in static maps in the editor partial; it must NOT be serialized or it dirties the scene on save.
    const int FUSED_TRANSFORM_ROWS = 9; // per-object transform-table rows (shared by the unified path)

    // The unified fused source set baked by GaussianSplatFuse.CreateFuseLODJob: every scene splat concatenated into
    // one source, rendered by a single scene-global selection (2D mip pyramid -> one alpha = scene budget)
    // + a single combine, reading a per-frame per-object param texture (camera-in-object-space) + a 9-row
    // transform texture. lodFusedObjects = [non-LOD then LOD] in objId order.
    [SerializeField] Material lodUnifiedSelectMaterial, lodUnifiedCombineMaterial;
    [SerializeField] Texture2D lodFusedPositions, lodFusedColors, lodFusedRotations, lodFusedScales;
    [SerializeField] Texture2D lodGlobalBounds, lodGlobalRange, lodFileBase;
    [SerializeField] GameObject[] lodFusedObjects;
    [SerializeField] int lodFusedObjectCount, lodTotalChunks, lodMetaWidth, lodSelectionSide, lodFusedCoordShift, lodFusedCoordMask;
    // Every fused object is a GaussianSplatObject (1..N levels); its chunks feed the unified selection.
    [SerializeField] Texture2D lodUnifiedSH, lodShParams;
    [SerializeField] int lodUnifiedShCoordShift, lodUnifiedShCoordMask;
    [SerializeField] int lodTotalSourceCount;   // total splats baked into the fused set (all objects, all levels)
    [SerializeField] int lodShDroppedObjects;   // objects whose SH overflowed the single fused SH texture (render DC-only)
    // Per fused object (objId order) debug stats for the inspector table.
    [SerializeField, HideInInspector] int[] lodObjSplatCount;
    [SerializeField, HideInInspector] int[] lodObjFileCount;
    [SerializeField, HideInInspector] int[] lodObjChunkCount;
    [SerializeField, HideInInspector] int[] lodObjShCoeff;
    [SerializeField, HideInInspector] bool[] lodObjShDropped;
    [SerializeField] RenderTexture lodUnifiedSelection; // POT-square, mip-chained (2D pyramid)
    [System.NonSerialized] Texture2D _lodParamTex;
    [System.NonSerialized] Color[] _lodParamPixels;
    [System.NonSerialized] Texture2D _lodUnifiedTransformTex;
    [System.NonSerialized] Color[] _lodUnifiedTransformPixels;
    const int LOD_PARAM_COLS = 5;

    const float LOD_ALPHA_ADAPT_RATE = 0.5f;

    [System.NonSerialized] int _combinedActualSplatCount;
    [System.NonSerialized] float _lodSplatTargetScale = 1.0f;
    [System.NonSerialized] float _lodDirectionalBias = 2.0f;
    [System.NonSerialized] int _lodShBand = 3;
    [System.NonSerialized] int[] _lodOutputCounts;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
    // Editor-only LOD debug preview flag. Must be static (not an instance field) so it stays out
    // of UdonSharp's proxy->Udon serialization surface: as an instance field UdonSharp's formatter
    // tried to serialize it despite [NonSerialized] and the Udon program (compiled without this
    // editor-only block) has no symbol for it, throwing "Field for System.Boolean does not exist"
    // on every play-mode enter. Static matches the adjacent editor-only readback dictionaries and
    // is fine here (one combiner per scene; set every sort before use).
    static bool _debugLodColors;
    static readonly Dictionary<GaussianSplatCombiner, int> _editorReadbackRenderedSplatCounts = new Dictionary<GaussianSplatCombiner, int>();
    static readonly Dictionary<GaussianSplatCombiner, int> _editorReadbackReservedSplatCounts = new Dictionary<GaussianSplatCombiner, int>();
    static readonly Dictionary<GaussianSplatCombiner, float> _editorReadbackAlphas = new Dictionary<GaussianSplatCombiner, float>();
#endif
    [System.NonSerialized] GaussianSplatObject[] _sceneLods = new GaussianSplatObject[0];

    public MeshRenderer GetCombinedSortedRenderer() { return combinedSortedRenderer; }
    public GameObject GetCombinedObject() { return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : null; }
    public bool UseBucketResources(int tier)
    {
        if (!TryGetTierTexture(combinedPositionsByBucket, tier, out RenderTexture positions)
            || !TryGetTierTexture(combinedRotationsByBucket, tier, out RenderTexture rotations)
            || !TryGetTierTexture(combinedScalesByBucket, tier, out RenderTexture scales)
            || !TryGetTierTexture(combinedColorsByBucket, tier, out RenderTexture colors)
            || !TryGetTierTexture(combinedColorsCameraByBucket, tier, out RenderTexture colorsCamera))
        {
            return false;
        }

        combinedPositions = positions;
        combinedRotations = rotations;
        combinedScales = scales;
        combinedColors = colors;
        combinedColorsCamera = colorsCamera;
        return true;
    }

    public bool BindDefaultBucketResources()
    {
        if (combinedPositions != null && combinedRotations != null && combinedScales != null && combinedColors != null && combinedColorsCamera != null)
        {
            return true;
        }
        int maxTier = combinedPositionsByBucket != null ? combinedPositionsByBucket.Length - 1 : -1;
        for (int tier = maxTier; tier >= 0; tier--)
        {
            if (UseBucketResources(tier))
            {
                return true;
            }
        }
        return false;
    }
    // Pure check (no mutation) used by the renderer to verify a tier is fully baked before committing a swap.
    public bool HasBucketResources(int tier)
    {
        return TryGetTierTexture(combinedPositionsByBucket, tier, out RenderTexture positions)
            && TryGetTierTexture(combinedRotationsByBucket, tier, out RenderTexture rotations)
            && TryGetTierTexture(combinedScalesByBucket, tier, out RenderTexture scales)
            && TryGetTierTexture(combinedColorsByBucket, tier, out RenderTexture colors)
            && TryGetTierTexture(combinedColorsCameraByBucket, tier, out RenderTexture colorsCamera);
    }
    [System.NonSerialized] int _activePassCount = -1;

    // Geometric pass ladder: cumulative capacity after pass k == 512K << k (512K,1M,2M,4M,8M,16M). Must match
    // GaussianSplatRTPool. Smallest pass prefix whose cumulative covers renderedCount.
    // (internal so the test assembly can verify it matches the editor pool's PassesToCover.)
    internal static int PassesToCover(int count)
    {
        if (count <= 0)
        {
            return 0;
        }
        for (int k = 0; k < 6; k++)
        {
            if (((512 * 1024) << k) >= count)
            {
                return k + 1;
            }
        }
        return 6;
    }

    // Enable the minimal prefix of combined pass chunks that covers renderedCount and disable the rest, so the
    // draw processes only as many splats as are actually selected. Only does work when the prefix length
    // changes (the per-chunk name lookup would otherwise allocate a string every frame).
    public void UpdateActivePassCount(int renderedCount)
    {
        if (combinedSortedRenderer == null)
        {
            return;
        }
        int passes = PassesToCover(renderedCount);
        if (passes == _activePassCount)
        {
            return;
        }
        _activePassCount = passes;
        Transform parent = combinedSortedRenderer.transform;
        for (int i = 0; i < 6; i++)
        {
            Transform chunk = parent.Find("CombinedChunk" + i);
            if (chunk == null)
            {
                continue;
            }
            bool shouldBeActive = i < passes;
            if (chunk.gameObject.activeSelf != shouldBeActive)
            {
                chunk.gameObject.SetActive(shouldBeActive);
            }
        }
    }

    static bool TryGetTierTexture(RenderTexture[] textures, int tier, out RenderTexture texture)
    {
        texture = textures != null && tier >= 0 && tier < textures.Length ? textures[tier] : null;
        return texture != null;
    }
    public bool ContainsFusedLODObject(GameObject splatObject)
    {
        if (splatObject == null || lodFusedObjects == null)
        {
            return false;
        }
        int count = Mathf.Min(lodFusedObjectCount, lodFusedObjects.Length);
        for (int i = 0; i < count; i++)
        {
            if (lodFusedObjects[i] == splatObject)
            {
                return true;
            }
        }
        return false;
    }

    public void SetLodSplatTargetScale(float value) { _lodSplatTargetScale = Mathf.Clamp(value, 0.01f, 1.0f); }
    public void SetLodDirectionalBias(float value) { _lodDirectionalBias = Mathf.Clamp(value, 1.0f, 16.0f); }
    public void SetLodShBand(int value) { _lodShBand = Mathf.Clamp(value, 0, 3); }
    public int GetFusedShDroppedObjectCount() { return lodShDroppedObjects; }
#if UNITY_EDITOR && !COMPILER_UDONSHARP
    public void SetEditorDebugLodColors(bool value) { _debugLodColors = value; }
#endif

    void ResetLODOutputCounts(int count)
    {
        count = Mathf.Max(0, count);
        if (_lodOutputCounts == null || _lodOutputCounts.Length < count)
        {
            _lodOutputCounts = new int[count];
        }
        for (int i = 0; i < count; i++)
        {
            _lodOutputCounts[i] = 0;
        }
    }

    static int ComputeLODTargetBudget(int hardBudget, float targetScale)
    {
        if (hardBudget <= 0)
        {
            return 0;
        }
        return Mathf.Clamp(Mathf.FloorToInt(hardBudget * Mathf.Clamp(targetScale, 0.01f, 1.0f)), 1, hardBudget);
    }

    static Vector4 LODComputedParams(GaussianSplatObject lodObject)
    {
        return new Vector4(
            1.0f,
            1.0f,
            lodObject != null ? lodObject.GetLodReusePercent() : 50.0f,
            0.0f);
    }

    GaussianSplatRenderer GetOwnerRenderer()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (gaussianSplatRenderer == null || gaussianSplatRenderer.gameObject == null || gaussianSplatRenderer.gameObject.scene != gameObject.scene)
        {
            gaussianSplatRenderer = GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);
        }
#else
        if (gaussianSplatRenderer == null)
        {
            GameObject rendererObject = GameObject.Find("GaussianSplatRenderer");
            if (rendererObject != null)
            {
                gaussianSplatRenderer = rendererObject.GetComponent<GaussianSplatRenderer>();
            }
        }
#endif
        return gaussianSplatRenderer;
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

    static bool TryGetCombinedChunkBinding(Transform child, out MeshRenderer renderer, out int offset)
    {
        renderer = child != null ? child.GetComponent<MeshRenderer>() : null;
        Material primaryMaterial = GaussianSplatSource.ResolvePrimarySplatMaterial(renderer != null ? renderer.sharedMaterials : null);
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
        Debug.LogError(label + " RenderTexture could not be created at runtime: " + renderTexture.name + " (" + renderTexture.width + "x" + renderTexture.height + ", " + renderTexture.format + ")");
        return false;
    }

    bool IsLodObjectActive(int index)
    {
        return index >= 0 && index < _sceneLods.Length
            && _sceneLods[index] != null
            && _sceneLods[index].gameObject.activeInHierarchy
            && (_sceneLods[index].IsRenderable() || ContainsFusedLODObject(_sceneLods[index].gameObject));
    }

    bool IsLodObjectGPUReady(GaussianSplatObject lodObject)
    {
        if (lodObject == null)
        {
            return false;
        }
        if (ContainsFusedLODObject(lodObject.gameObject))
        {
            return true;
        }
        return lodObject.IsRenderable()
            && lodObject.chunkBoundsMinTexture != null
            && lodObject.chunkBoundsMaxTexture != null
            && lodObject.chunkRangeTexture != null;
    }

    void SwapLODAlphaStateBuffers()
    {
        RenderTexture tmp = _lodAlphaFront;
        _lodAlphaFront = _lodAlphaBack;
        _lodAlphaBack = tmp;
    }

    bool BindLODAlphaStateBuffers()
    {
        bool frontIsCanonical = _lodAlphaFront == lodAlphaState || _lodAlphaFront == lodAlphaStateScratch;
        bool backIsCanonical = _lodAlphaBack == lodAlphaState || _lodAlphaBack == lodAlphaStateScratch;
        bool hasPair = _lodAlphaFront != null && _lodAlphaBack != null && _lodAlphaFront != _lodAlphaBack && frontIsCanonical && backIsCanonical;
        if (!hasPair)
        {
            _lodAlphaFront = lodAlphaState;
            _lodAlphaBack = lodAlphaStateScratch;
        }
        return _lodAlphaFront != null && _lodAlphaBack != null && _lodAlphaFront != _lodAlphaBack;
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

    void SetRenderOrderOnMaterials(Material[] materials, int actualCount, RenderTexture splatRenderOrder, RenderTexture splatRenderOrderPhoto)
    {
        int positionBlocksPerRow = Mathf.Max(1, combinedPositions != null ? combinedPositions.width >> 2 : 1);
        int positionCoordMask = positionBlocksPerRow - 1;
        int positionCoordShift = ComputeTextureCoordShift(positionBlocksPerRow);
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
            if (material.HasProperty("_GS_Positions")) material.SetTexture("_GS_Positions", combinedPositions);
            if (material.HasProperty("_GS_Rotations")) material.SetTexture("_GS_Rotations", combinedRotations);
            if (material.HasProperty("_GS_Scales")) material.SetTexture("_GS_Scales", combinedScales);
            if (material.HasProperty("_GS_Colors")) material.SetTexture("_GS_Colors", combinedColors);
            if (material.HasProperty("_GS_ColorsCamera")) material.SetTexture("_GS_ColorsCamera", combinedColorsCamera);
            if (material.HasProperty("_GS_Positions_CoordMask")) material.SetInt("_GS_Positions_CoordMask", positionCoordMask);
            if (material.HasProperty("_GS_Positions_CoordShift")) material.SetInt("_GS_Positions_CoordShift", positionCoordShift);
        }
    }

    public bool EnsureResourcesCreated()
    {
        BindDefaultBucketResources();
        return (combinedPositions == null || EnsureRenderTextureCreated(combinedPositions, "Combined positions"))
            && (combinedRotations == null || EnsureRenderTextureCreated(combinedRotations, "Combined rotations"))
            && (combinedScales == null || EnsureRenderTextureCreated(combinedScales, "Combined scales"))
            && (combinedColors == null || EnsureRenderTextureCreated(combinedColors, "Combined colors"))
            && (combinedColorsCamera == null || EnsureRenderTextureCreated(combinedColorsCamera, "Combined camera colors"));
    }

    public void SetRendererEnabled(bool enabled)
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

    int CountActiveGPULODObjects()
    {
        int count = 0;
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            if (IsLodObjectActive(i) && IsLodObjectGPUReady(_sceneLods[i]))
            {
                count++;
            }
        }
        return count;
    }

    int CountActiveGPULODMaxSplatCount()
    {
        int count = 0;
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            GaussianSplatObject lodObject = _sceneLods[i];
            if (IsLodObjectActive(i) && IsLodObjectGPUReady(lodObject))
            {
                count = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, count + lodObject.GetMaxLOD0SplatCount());
            }
        }
        return count;
    }

    // Runs the scene-global selection (2D mip pyramid -> single alpha = scene budget) then the combine over
    // the whole fused set, writing the selection-compacted output [0, selected): every object's chunks flow
    // through the same selection pass. selectionTarget drives the alpha adapt. Returns false if nothing baked.
    bool UpdateFusedLOD(Vector3 screenCameraPos, Vector3 lodCameraPos, Vector3 lodCameraForward, int lodRegionStart, int combinedCoordShift, int selectionTarget, Vector4 lodScreenParams, bool adaptLodSelection, bool forceMinLodAlpha, bool useEditorOps)
    {
        if (lodFusedObjectCount <= 0 || lodUnifiedSelectMaterial == null || lodUnifiedCombineMaterial == null
            || lodFusedPositions == null || lodUnifiedSelection == null || lodFusedObjects == null
            || !BindLODAlphaStateBuffers())
        {
            return false;
        }
        // The unified combine writes the selection-compacted output [0, selected): every object's chunks
        // flow through the same selection pass. Computed-LOD objects descend the pyramid by distance/budget;
        // single-level (Normal) objects always emit their full LOD0 count.
        int n = lodFusedObjectCount;
        if (_lodParamTex == null || _lodParamTex.width != LOD_PARAM_COLS || _lodParamTex.height != n)
        {
            _lodParamTex = new Texture2D(LOD_PARAM_COLS, n, TextureFormat.RGBAFloat, false, true);
            _lodParamTex.filterMode = FilterMode.Point; _lodParamTex.wrapMode = TextureWrapMode.Clamp;
            _lodParamPixels = new Color[LOD_PARAM_COLS * n];
        }
        if (_lodUnifiedTransformTex == null || _lodUnifiedTransformTex.width != n || _lodUnifiedTransformTex.height != FUSED_TRANSFORM_ROWS)
        {
            _lodUnifiedTransformTex = new Texture2D(n, FUSED_TRANSFORM_ROWS, TextureFormat.RGBAFloat, false, true);
            _lodUnifiedTransformTex.filterMode = FilterMode.Point; _lodUnifiedTransformTex.wrapMode = TextureWrapMode.Clamp;
            _lodUnifiedTransformPixels = new Color[n * FUSED_TRANSFORM_ROWS];
        }
        for (int k = 0; k < n; k++)
        {
            GameObject go = k < lodFusedObjects.Length ? lodFusedObjects[k] : null;
            // Every fused object is a GaussianSplatObject (1..N levels); resolve its transform + LOD params.
            float log2min = -15.0f, opacity = 1.0f, shband = 0.0f;
            GaussianSplatObject lo = go != null ? go.GetComponent<GaussianSplatObject>() : null;
            bool active = lo != null && go.activeInHierarchy;
            Transform tr = lo != null ? lo.transform : null;
            // Selection params (per-object): camera-in-object-space + distance/computed params.
            Vector3 camObj = active && lo != null ? tr.InverseTransformPoint(lodCameraPos) : Vector3.zero;
            Vector3 fwdObj = active && lo != null ? tr.InverseTransformDirection(lodCameraForward.normalized) : Vector3.forward;
            _lodParamPixels[k * LOD_PARAM_COLS + 0] = new Color(camObj.x, camObj.y, camObj.z, 0.0f);
            _lodParamPixels[k * LOD_PARAM_COLS + 1] = new Color(fwdObj.x, fwdObj.y, fwdObj.z, 0.0f);
            float zeroOffset = lo != null ? lo.lodZeroOffset : 0.0f;
            float radius = lo != null ? lo.lodSplatRadius : 1.0f;
            float smallest = lo != null ? lo.smallestChunkSize : 1.0f;
            _lodParamPixels[k * LOD_PARAM_COLS + 2] = new Color(zeroOffset, radius, smallest, _lodDirectionalBias);
            float computed = 1.0f;
            float reuse = lo != null ? lo.GetLodReusePercent() : 50.0f;
            _lodParamPixels[k * LOD_PARAM_COLS + 3] = new Color(computed, 1.0f, reuse, active ? 1.0f : 0.0f);
            // World lossyScale, so the selection can convert local chunk bounds + local camera distance into
            // world-space size/distance (chunk bbox is object-local; the metric must be scale-agnostic).
            Vector3 pscl = active && tr != null ? tr.lossyScale : Vector3.one;
            _lodParamPixels[k * LOD_PARAM_COLS + 4] = new Color(pscl.x, pscl.y, pscl.z, 0.0f);
            Matrix4x4 l2w = active ? tr.localToWorldMatrix : Matrix4x4.identity;
            Matrix4x4 w2l = active ? tr.worldToLocalMatrix : Matrix4x4.identity;
            _lodUnifiedTransformPixels[0 * n + k] = new Color(l2w.m00, l2w.m01, l2w.m02, l2w.m03);
            _lodUnifiedTransformPixels[1 * n + k] = new Color(l2w.m10, l2w.m11, l2w.m12, l2w.m13);
            _lodUnifiedTransformPixels[2 * n + k] = new Color(l2w.m20, l2w.m21, l2w.m22, l2w.m23);
            _lodUnifiedTransformPixels[3 * n + k] = new Color(w2l.m00, w2l.m01, w2l.m02, w2l.m03);
            _lodUnifiedTransformPixels[4 * n + k] = new Color(w2l.m10, w2l.m11, w2l.m12, w2l.m13);
            _lodUnifiedTransformPixels[5 * n + k] = new Color(w2l.m20, w2l.m21, w2l.m22, w2l.m23);
            Quaternion q = active ? tr.rotation : Quaternion.identity;
            _lodUnifiedTransformPixels[6 * n + k] = new Color(-q.x, -q.y, -q.z, q.w);
            Vector3 ls = active ? tr.lossyScale : Vector3.one;
            _lodUnifiedTransformPixels[7 * n + k] = new Color(ls.x, ls.y, ls.z, active ? 1.0f : 0.0f);
            _lodUnifiedTransformPixels[8 * n + k] = new Color(log2min, opacity, shband, 0.0f);
        }
        _lodParamTex.SetPixels(_lodParamPixels); _lodParamTex.Apply(false, false);
        _lodUnifiedTransformTex.SetPixels(_lodUnifiedTransformPixels); _lodUnifiedTransformTex.Apply(false, false);

        int side = lodSelectionSide;
        float maxMip = Mathf.RoundToInt(Mathf.Log(Mathf.Max(1, side), 2.0f));
        // .y carries log2(metaWidth) (metaWidth is a power of two) so shaders decode chunk->texel with
        // shifts/masks instead of % / ÷.
        int lodMetaShift = Mathf.RoundToInt(Mathf.Log(Mathf.Max(1, lodMetaWidth), 2f));
        Vector4 unifiedLayout = new Vector4(side, lodMetaShift, lodTotalChunks, maxMip);
        int metaWidth = Mathf.Max(1, lodMetaWidth);
        int metaHeight = (Mathf.Max(1, lodTotalChunks) + metaWidth - 1) / metaWidth;
        Vector4 rangeStatsParams = new Vector4(lodGlobalRange != null && lodGlobalRange.height >= metaHeight * 2 ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f);

        lodUnifiedSelectMaterial.SetTexture("_LODChunkBounds", lodGlobalBounds);
        lodUnifiedSelectMaterial.SetTexture("_LODChunkRange", lodGlobalRange);
        lodUnifiedSelectMaterial.SetTexture("_LODObjectParams", _lodParamTex);
        lodUnifiedSelectMaterial.SetTexture("_LODAlphaState", _lodAlphaFront);
        lodUnifiedSelectMaterial.SetVector("_LODUnifiedLayout", unifiedLayout);
        lodUnifiedSelectMaterial.SetVector("_LODRangeStatsParams", rangeStatsParams);
        lodUnifiedSelectMaterial.SetVector("_LODSelectionParams", new Vector4(GaussianSplatObject.MAX_LOD_ALPHA_LOG2, lodScreenParams.x, maxMip, adaptLodSelection ? LOD_ALPHA_ADAPT_RATE : 0.0f));
        lodUnifiedSelectMaterial.SetVector("_LODBudgetParams", new Vector4(selectionTarget, forceMinLodAlpha ? 1.0f : 0.0f, 0.0f, 0.0f));
        Blit(lodUnifiedSelection, lodUnifiedSelectMaterial, 0, useEditorOps);
        lodUnifiedSelection.GenerateMips();
        if (adaptLodSelection)
        {
            lodUnifiedSelectMaterial.SetTexture("_LODChunkSelection", lodUnifiedSelection);
            lodUnifiedSelectMaterial.SetTexture("_LODAlphaState", _lodAlphaFront);
            Blit(_lodAlphaBack, lodUnifiedSelectMaterial, 1, useEditorOps);
            SwapLODAlphaStateBuffers();
            lodUnifiedSelectMaterial.SetTexture("_LODAlphaState", _lodAlphaFront);
            Blit(lodUnifiedSelection, lodUnifiedSelectMaterial, 0, useEditorOps);
            lodUnifiedSelection.GenerateMips();
        }

        lodUnifiedCombineMaterial.SetTexture("_LODChunkSelection", lodUnifiedSelection);
        lodUnifiedCombineMaterial.SetTexture("_LODChunkBounds", lodGlobalBounds);
        lodUnifiedCombineMaterial.SetTexture("_LODChunkRange", lodGlobalRange);
        lodUnifiedCombineMaterial.SetTexture("_LODFileBase", lodFileBase);
        lodUnifiedCombineMaterial.SetTexture("_LODObjectParams", _lodParamTex);
        lodUnifiedCombineMaterial.SetTexture("_LODFusedPositions", lodFusedPositions);
        lodUnifiedCombineMaterial.SetTexture("_LODFusedColors", lodFusedColors);
        lodUnifiedCombineMaterial.SetTexture("_LODFusedRotations", lodFusedRotations);
        lodUnifiedCombineMaterial.SetTexture("_LODFusedScales", lodFusedScales);
        lodUnifiedCombineMaterial.SetTexture("_LODFusedTransforms", _lodUnifiedTransformTex);
        lodUnifiedCombineMaterial.SetVector("_LODUnifiedLayout", unifiedLayout);
        lodUnifiedCombineMaterial.SetInt("_LODFusedCoordShift", lodFusedCoordShift);
        lodUnifiedCombineMaterial.SetInt("_LODFusedCoordMask", lodFusedCoordMask);
        // Fused SH set (view-dependent; _LODCameraPosWorld is the actual view camera). Every object's chunks
        // are filled by the selection descent - there is no separate identity region.
        lodUnifiedCombineMaterial.SetTexture("_LODShParams", lodShParams != null ? lodShParams : Texture2D.blackTexture);
        lodUnifiedCombineMaterial.SetTexture("_LODFusedSH", lodUnifiedSH != null ? lodUnifiedSH : Texture2D.blackTexture);
        lodUnifiedCombineMaterial.SetInt("_LODFusedShCoordShift", lodUnifiedShCoordShift);
        lodUnifiedCombineMaterial.SetInt("_LODFusedShCoordMask", lodUnifiedShCoordMask);
        lodUnifiedCombineMaterial.SetFloat("_SHBand", _lodShBand);
        lodUnifiedCombineMaterial.SetVector("_LODCameraPosWorld", screenCameraPos);
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        lodUnifiedCombineMaterial.SetInt("_LODDebugColors", _debugLodColors ? 1 : 0);
#else
        lodUnifiedCombineMaterial.SetInt("_LODDebugColors", 0);
#endif
        lodUnifiedCombineMaterial.SetVector("_LODCombineOutputParams", new Vector4(lodRegionStart, combinedCoordShift, 0.0f, 0.0f));
        Blit(combinedPositions, lodUnifiedCombineMaterial, 0, useEditorOps);
        Blit(combinedRotations, lodUnifiedCombineMaterial, 1, useEditorOps);
        Blit(combinedScales, lodUnifiedCombineMaterial, 2, useEditorOps);
        Blit(combinedColors, lodUnifiedCombineMaterial, 3, useEditorOps);
        return true;
    }

    public bool UpdateTextures(GaussianSplatObject[] sceneLods, Vector3 screenCameraPos, Vector3 lodCameraPos, Vector3 lodCameraForward, Vector3 photoCameraPos, bool updatePhotoCameraColors, int lodSplatBudget, Vector4 lodScreenParams, bool adaptLodSelection, bool forceMinLodAlpha, bool useEditorOps)
    {
        _sceneLods = sceneLods != null ? sceneLods : new GaussianSplatObject[0];
        ResetLODOutputCounts(_sceneLods.Length);
        float lodTargetScale = lodSplatBudget > 0 ? Mathf.Clamp(_lodSplatTargetScale, 0.01f, 1.0f) : 1.0f;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        SetEditorReadback(0, 0, 0.0f);
#endif
        if (combinedSortedRenderer == null || combinedPositions == null || combinedRotations == null || combinedScales == null || combinedColors == null || combinedColorsCamera == null)
        {
            BindDefaultBucketResources();
        }
        if (combinedSortedRenderer == null || combinedPositions == null || combinedRotations == null || combinedScales == null || combinedColors == null || combinedColorsCamera == null)
        {
#if !UNITY_EDITOR || COMPILER_UDONSHARP
            Debug.LogError("Gaussian splat renderer is missing generated resources. Refresh the GaussianSplatRenderer in the editor.");
#endif
            return false;
        }
        int activeSourceCount = CountActiveGPULODObjects();
        if (activeSourceCount == 0)
        {
            _combinedActualSplatCount = 0;
            SetRendererEnabled(false);
            return false;
        }
        int combinedBlocksPerRow = Mathf.Max(1, combinedPositions.width >> 2);
        int combinedCoordShift = ComputeTextureCoordShift(combinedBlocksPerRow);
        int positionCapacity = combinedPositions.width * combinedPositions.height;
        int colorCapacity = combinedColors.width * combinedColors.height;
        int combinedCapacity = Mathf.Min(positionCapacity, colorCapacity);
        Blit(Texture2D.blackTexture, combinedPositions, useEditorOps);
        Blit(Texture2D.blackTexture, combinedRotations, useEditorOps);
        Blit(Texture2D.blackTexture, combinedScales, useEditorOps);
        Blit(Texture2D.blackTexture, combinedColors, useEditorOps);
        int combinedOffset = 0;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        int editorReadbackCount = 0;
        float editorReadbackAlpha = 0.0f;
#endif
        // ONE PATH: the unified combine (GSLODCombine) renders ALL combined content via the selection
        // descent in a single pass set. Unpacked (un-migrated / stale) content is not in the fused set and
        // simply does not render. The output has no fixed identity region (every object is selection-compacted).
        {
            int bakedNonLodCount = 0;
            if (bakedNonLodCount > combinedCapacity)
            {
                _combinedActualSplatCount = 0;
                SetRendererEnabled(false);
#if !UNITY_EDITOR || COMPILER_UDONSHARP
                Debug.LogError("Combined Gaussian splat resources are too small for the baked non-LOD fused region. Refresh the renderer resources in the editor.");
#endif
                return false;
            }
            int remainingCapacity = Mathf.Max(0, combinedCapacity - bakedNonLodCount);
            int activeLodMaxSplatCount = CountActiveGPULODMaxSplatCount();
            // Every object is a computed-LOD object: the whole active set is thinnable, so the budget caps the
            // full scene-wide count. The runtime perf throttle (lodTargetScale) scales the selection target.
            int sceneHardBudget;
            int sceneSelectionTarget;
            if (lodSplatBudget > 0)
            {
                int thinnableCap = Mathf.Min(activeLodMaxSplatCount, Mathf.Max(0, lodSplatBudget));
                sceneHardBudget = Mathf.Min(remainingCapacity, thinnableCap);
                int thinnableTarget = ComputeLODTargetBudget(thinnableCap, lodTargetScale);
                sceneSelectionTarget = Mathf.Min(remainingCapacity, thinnableTarget);
            }
            else
            {
                sceneHardBudget = Mathf.Min(remainingCapacity, activeLodMaxSplatCount);
                sceneSelectionTarget = sceneHardBudget;
            }
            if (!UpdateFusedLOD(screenCameraPos, lodCameraPos, lodCameraForward, bakedNonLodCount, combinedCoordShift, sceneSelectionTarget, lodScreenParams, adaptLodSelection, forceMinLodAlpha, useEditorOps))
            {
                _combinedActualSplatCount = 0;
                SetRendererEnabled(false);
                return false;
            }
            combinedOffset = bakedNonLodCount + sceneHardBudget;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                int sel = ReadbackUnifiedLODSelected(out float ua, true);
                editorReadbackCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, bakedNonLodCount + Mathf.Max(0, sel));
                editorReadbackAlpha = ua;
            }
            else
            {
                editorReadbackCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, bakedNonLodCount + sceneSelectionTarget);
            }
#endif
        }

        if (combinedOffset <= 0)
        {
            _combinedActualSplatCount = 0;
            SetRendererEnabled(false);
            return false;
        }
        int actualCombinedCount = combinedOffset;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            actualCombinedCount = Mathf.Min(combinedOffset, Mathf.Max(0, editorReadbackCount));
        }
#endif
        _combinedActualSplatCount = actualCombinedCount;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        SetEditorReadback(useEditorOps ? actualCombinedCount : combinedOffset, combinedOffset, useEditorOps ? editorReadbackAlpha : 0.0f);
#endif

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            return true;
        }
#endif

        if (!updatePhotoCameraColors)
        {
            return true;
        }

        // Photo/mirror camera colors: re-run ONLY the unified combine's color pass with the photo camera
        // (view-dependent SH differs per camera). Every other binding persists from this frame's combine,
        // so the whole [0, selected) output is recolored in one blit into combinedColorsCamera.
        Blit(Texture2D.blackTexture, combinedColorsCamera, false);
        if (lodFusedObjectCount > 0 && lodUnifiedCombineMaterial != null)
        {
            lodUnifiedCombineMaterial.SetVector("_LODCameraPosWorld", photoCameraPos);
            Blit(combinedColorsCamera, lodUnifiedCombineMaterial, 3, false);
        }
        return true;
    }

    /// <summary>
    /// Applies the render-order texture + actual splat count to the combined parent + chunk materials,
    /// toggles chunk visibility, and resolves the primary sort renderer/material/positions for the
    /// renderer to bind sort keys against. Returns false (and disables the combined object) when the
    /// combined resources are not ready.
    /// </summary>
    public bool BindRenderOrder(RenderTexture splatRenderOrder, RenderTexture splatRenderOrderPhoto, out MeshRenderer sortedRenderer, out Material primaryMaterial, out Texture positions, out int count)
    {
        BindDefaultBucketResources();
        sortedRenderer = null;
        primaryMaterial = null;
        positions = combinedPositions;
        count = _combinedActualSplatCount;
        if (combinedSortedRenderer == null || combinedPositions == null || _combinedActualSplatCount <= 0)
        {
            SetRendererEnabled(false);
            return false;
        }
        Transform combinedRoot = combinedSortedRenderer.transform;
        SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(combinedSortedRenderer), _combinedActualSplatCount, splatRenderOrder, splatRenderOrderPhoto);
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
            SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(chunkRenderer), _combinedActualSplatCount, splatRenderOrder, splatRenderOrderPhoto);
            if (shouldRender && sortedRenderer == null)
            {
                sortedRenderer = chunkRenderer;
            }
        }
        primaryMaterial = GaussianSplatSource.ResolvePrimarySplatMaterial(GetRendererMaterialsForRead(sortedRenderer));
        if (sortedRenderer == null || primaryMaterial == null)
        {
            SetRendererEnabled(false);
            return false;
        }
        return true;
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    void SetEditorReadback(int renderedSplatCount, int reservedSplatCount, float alpha)
    {
        _editorReadbackRenderedSplatCounts[this] = renderedSplatCount;
        _editorReadbackReservedSplatCounts[this] = reservedSplatCount;
        _editorReadbackAlphas[this] = alpha;
    }

    public int GetEditorReadbackRenderedSplatCount()
    {
        return _editorReadbackRenderedSplatCounts.TryGetValue(this, out int value) ? value : 0;
    }

    public int GetEditorReadbackReservedSplatCount()
    {
        return _editorReadbackReservedSplatCounts.TryGetValue(this, out int value) ? value : 0;
    }

    // Total splats baked into the fused set across every combined object (full non-LOD + all LOD levels),
    // independent of the live per-frame LOD selection.
    public int GetTotalBakedSplatCount()
    {
        // One splat per texel in the fused source (LODFusedCoord is a bijection), so the count is just the
        // texture size. lodFusedPositions is a serialized reference, so this is available without a bake or
        // reload having run this session.
        return lodFusedPositions != null ? lodFusedPositions.width * lodFusedPositions.height : 0;
    }

    // Uncompressed byte size of the fused source textures (positions/colors/rotations/scales/SH) - the splat
    // data the build ships. Summed from the serialized texture refs so it is available without a bake/reload.
    public long GetBakedSplatDataBytes()
    {
        return FusedTextureBytes(lodFusedPositions) + FusedTextureBytes(lodFusedColors)
            + FusedTextureBytes(lodFusedRotations) + FusedTextureBytes(lodFusedScales)
            + FusedTextureBytes(lodUnifiedSH);
    }

    static long FusedTextureBytes(Texture2D tex)
    {
        // Shipped texel size (== the asset's m_CompleteImageSize). ComputeMipmapSize is correct for
        // block-compressed formats too: the SH texture is BC7, so width*height*GetBlockSize would count
        // the 16-byte 4x4 block as per-pixel (16x overcount). Profiler would also count the editor CPU copy.
        if (tex == null)
        {
            return 0;
        }
        return (long)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(tex.width, tex.height, tex.graphicsFormat);
    }

    public float GetEditorReadbackAlpha()
    {
        return _editorReadbackAlphas.TryGetValue(this, out float value) ? value : 0.0f;
    }

#endif

    public void ApplyMaterialSettings()
    {
        GaussianSplatRenderer owner = GetOwnerRenderer();
        if (owner == null || combinedSortedRenderer == null)
        {
            return;
        }
        Material[] combinedMaterials = GetRendererMaterialsForWrite(combinedSortedRenderer);
        for (int i = 0; i < combinedMaterials.Length; i++)
        {
            owner.ApplyConfiguredMaterialSettingsForCombined(combinedMaterials[i]);
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
                owner.ApplyConfiguredMaterialSettingsForCombined(chunkMaterials[materialIndex]);
            }
        }
    }

}

}
