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
/// behaves like a single GaussianSplatObject (SH0) so the renderer can drive it through the same
/// single-splat sort/render path. The renderer delegates all combine work to this component.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class GaussianSplatCombiner : UdonSharpBehaviour
{
    const int COMBINED_SOURCE_BATCH_SIZE_DESKTOP = 8;
    const int COMBINED_SOURCE_BATCH_SIZE_GLES = 1;
    const int LOD_SOURCE_BATCH_SIZE = 2;
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;
    const int DEFAULT_COMBINED_SPLATS_PER_PASS = 3 * 256 * 1024;
    const int DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT = 1;

    [SerializeField] GaussianSplatRenderer gaussianSplatRenderer;
    [SerializeField] MeshRenderer combinedSortedRenderer;
    [SerializeField] Material combineDataMaterial;
    [SerializeField] Material lodChunkSelectMaterial;
    [SerializeField] Material lodCombineDataMaterial;
    [SerializeField] RenderTextureFormat combinedPositionsFormat = RenderTextureFormat.ARGBFloat, combinedRotationsFormat = RenderTextureFormat.ARGB32, combinedScalesFormat = RenderTextureFormat.ARGBHalf, combinedColorsFormat = RenderTextureFormat.ARGB32, combinedColorsCameraFormat = RenderTextureFormat.ARGB32;
    [SerializeField, HideInInspector] bool combinedTextureFormatsInitialized = true;
    [SerializeField] int combinedStartRenderQueue = 4050;
    [SerializeField] RenderTexture combinedPositions, combinedRotations, combinedScales, combinedColors, combinedColorsCamera;
    [SerializeField] RenderTexture lodChunkSelection;
    [SerializeField] RenderTexture lodAlphaState;
    [SerializeField] RenderTexture lodAlphaStateScratch;
    [SerializeField] int builtCombinedElementCount;

    const float LOD_ALPHA_ADAPT_RATE = 0.5f;

    [System.NonSerialized] int _combinedActualSplatCount;
    [System.NonSerialized] float _lodSplatTargetScale = 1.0f;
    [System.NonSerialized] float _lodDirectionalBias = 2.0f;
    [System.NonSerialized] int[] _lodOutputCounts;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static readonly Dictionary<GaussianSplatCombiner, int> _editorReadbackRenderedSplatCounts = new Dictionary<GaussianSplatCombiner, int>();
    static readonly Dictionary<GaussianSplatCombiner, int> _editorReadbackReservedSplatCounts = new Dictionary<GaussianSplatCombiner, int>();
    static readonly Dictionary<GaussianSplatCombiner, float> _editorReadbackAlphas = new Dictionary<GaussianSplatCombiner, float>();
    static readonly Dictionary<GaussianSplatLODObject, Color[]> _editorLodChunkStates = new Dictionary<GaussianSplatLODObject, Color[]>();
#endif
    [System.NonSerialized] GaussianSplatObject[] _sceneSplats = new GaussianSplatObject[0];
    [System.NonSerialized] GaussianSplatLODObject[] _sceneLods = new GaussianSplatLODObject[0];

    public MeshRenderer GetCombinedSortedRenderer() { return combinedSortedRenderer; }
    public GameObject GetCombinedObject() { return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : null; }
    public string GetCombinedObjectName() { return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject.name : "Combined"; }
    public void SetLodSplatTargetScale(float value) { _lodSplatTargetScale = Mathf.Clamp(value, 0.01f, 1.0f); }
    public void SetLodDirectionalBias(float value) { _lodDirectionalBias = Mathf.Clamp(value, 1.0f, 16.0f); }

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

    bool EnsureLODMaterials()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (lodChunkSelectMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/GaussianSplatting/LODChunkSelect");
            if (shader != null)
            {
                lodChunkSelectMaterial = new Material(shader);
                lodChunkSelectMaterial.name = "LODChunkSelect_Runtime";
            }
        }
        if (lodCombineDataMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/GaussianSplatting/LODCombineData");
            if (shader != null)
            {
                lodCombineDataMaterial = new Material(shader);
                lodCombineDataMaterial.name = "LODCombineData_Runtime";
            }
        }
#endif
        return lodChunkSelectMaterial != null && lodCombineDataMaterial != null;
    }

    bool EnsureLODChunkTextures(GaussianSplatLODObject lodObject)
    {
        int chunkCount = lodObject != null ? lodObject.GetChunkCount() : 0;
        if (chunkCount <= 0)
        {
            return false;
        }
        int selectionWidth = Mathf.NextPowerOfTwo(Mathf.Max(1, chunkCount));
        bool selectionReady = lodChunkSelection != null
            && lodChunkSelection.width >= selectionWidth
            && lodChunkSelection.height == 1
            && lodChunkSelection.format == RenderTextureFormat.ARGBFloat
            && EnsureRenderTextureCreated(lodChunkSelection, "LOD chunk selection");
        bool alphaReady = lodAlphaState != null
            && lodAlphaState.width == 1
            && lodAlphaState.height == 1
            && lodAlphaState.format == RenderTextureFormat.ARGBFloat
            && EnsureRenderTextureCreated(lodAlphaState, "LOD alpha state")
            && lodAlphaStateScratch != null
            && lodAlphaStateScratch.width == 1
            && lodAlphaStateScratch.height == 1
            && lodAlphaStateScratch.format == RenderTextureFormat.ARGBFloat
            && EnsureRenderTextureCreated(lodAlphaStateScratch, "LOD alpha state scratch");
        if (selectionReady && alphaReady)
        {
            return true;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (lodChunkSelection != null && lodChunkSelection.IsCreated())
        {
            lodChunkSelection.Release();
        }
        lodChunkSelection = new RenderTexture(selectionWidth, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        lodChunkSelection.name = "LODChunkSelection_Runtime";
        lodChunkSelection.wrapMode = TextureWrapMode.Clamp;
        lodChunkSelection.filterMode = FilterMode.Point;
        lodChunkSelection.useMipMap = true;
        lodChunkSelection.autoGenerateMips = false;

        if (lodAlphaState != null && lodAlphaState.IsCreated())
        {
            lodAlphaState.Release();
        }
        lodAlphaState = CreateAlphaStateRT("LODAlphaState_Runtime");
        if (lodAlphaStateScratch != null && lodAlphaStateScratch.IsCreated())
        {
            lodAlphaStateScratch.Release();
        }
        lodAlphaStateScratch = CreateAlphaStateRT("LODAlphaStateScratch_Runtime");
        return EnsureRenderTextureCreated(lodChunkSelection, "LOD chunk selection")
            && EnsureRenderTextureCreated(lodAlphaState, "LOD alpha state")
            && EnsureRenderTextureCreated(lodAlphaStateScratch, "LOD alpha state scratch");
#else
        Debug.LogError("LOD chunk RenderTextures are missing or have the wrong size. Refresh the GaussianSplatRenderer resources in the editor.");
        return false;
#endif
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    RenderTexture CreateAlphaStateRT(string name)
    {
        RenderTexture texture = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        texture.name = name;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Point;
        texture.useMipMap = false;
        texture.autoGenerateMips = false;
        return texture;
    }
#endif

    bool IsSourceActive(int index)
    {
        return index >= 0 && index < _sceneSplats.Length && _sceneSplats[index] != null && _sceneSplats[index].gameObject.activeInHierarchy;
    }

    bool IsLodObjectActive(int index)
    {
        return index >= 0 && index < _sceneLods.Length && _sceneLods[index] != null && _sceneLods[index].gameObject.activeInHierarchy && _sceneLods[index].IsRenderable();
    }

    bool IsLodObjectGPUReady(GaussianSplatLODObject lodObject)
    {
        return lodObject != null
            && lodObject.IsRenderable()
            && lodObject.chunkBoundsMinTexture != null
            && lodObject.chunkBoundsMaxTexture != null
            && lodObject.chunkRangeTexture != null;
    }

    int GetCombinedSourceBatchSize()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        UnityEngine.Rendering.GraphicsDeviceType graphicsDevice = SystemInfo.graphicsDeviceType;
        return graphicsDevice == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 || graphicsDevice == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3
            ? COMBINED_SOURCE_BATCH_SIZE_GLES
            : COMBINED_SOURCE_BATCH_SIZE_DESKTOP;
#elif UNITY_ANDROID
        return COMBINED_SOURCE_BATCH_SIZE_GLES;
#else
        return COMBINED_SOURCE_BATCH_SIZE_DESKTOP;
#endif
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

    public bool EnsureResourcesCreated()
    {
        return (combinedPositions == null || EnsureRenderTextureCreated(combinedPositions, "Combined positions"))
            && (combinedRotations == null || EnsureRenderTextureCreated(combinedRotations, "Combined rotations"))
            && (combinedScales == null || EnsureRenderTextureCreated(combinedScales, "Combined scales"))
            && (combinedColors == null || EnsureRenderTextureCreated(combinedColors, "Combined colors"))
            && (combinedColorsCamera == null || EnsureRenderTextureCreated(combinedColorsCamera, "Combined camera colors"))
            && (CountActiveGPULODObjects() == 0 || EnsureLODMaterials());
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
            combineDataMaterial.SetVector("_GS_SourceTransformRotation" + suffix, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            combineDataMaterial.SetVector("_GS_SourceTransformScale" + suffix, new Vector4(1.0f, 1.0f, 1.0f, 0.0f));
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
        if (sourceRenderer != null)
        {
            Transform sourceTransform = sourceRenderer.transform;
            Quaternion sourceRotation = sourceTransform.rotation;
            Vector3 sourceScale = sourceTransform.lossyScale;
            combineDataMaterial.SetMatrix("_GS_SourceLocalToWorld" + suffix, sourceTransform.localToWorldMatrix);
            combineDataMaterial.SetMatrix("_GS_SourceWorldToLocal" + suffix, sourceTransform.worldToLocalMatrix);
            // The shader-side qrot convention uses the conjugated Unity quaternion.
            combineDataMaterial.SetVector("_GS_SourceTransformRotation" + suffix, new Vector4(-sourceRotation.x, -sourceRotation.y, -sourceRotation.z, sourceRotation.w));
            combineDataMaterial.SetVector("_GS_SourceTransformScale" + suffix, new Vector4(sourceScale.x, sourceScale.y, sourceScale.z, 0.0f));
        }
        else
        {
            combineDataMaterial.SetMatrix("_GS_SourceLocalToWorld" + suffix, Matrix4x4.identity);
            combineDataMaterial.SetMatrix("_GS_SourceWorldToLocal" + suffix, Matrix4x4.identity);
            combineDataMaterial.SetVector("_GS_SourceTransformRotation" + suffix, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            combineDataMaterial.SetVector("_GS_SourceTransformScale" + suffix, new Vector4(1.0f, 1.0f, 1.0f, 0.0f));
        }
    }

    int CountActiveNormalSplatSources(out int splatCount)
    {
        splatCount = 0;
        int sourceCount = 0;
        for (int i = 0; i < _sceneSplats.Length; i++)
        {
            if (!IsSourceActive(i))
            {
                continue;
            }
            if (!TryGetSplatSource(_sceneSplats[i], out MeshRenderer renderer, out Material material, out Texture positions, out int count))
            {
                continue;
            }
            splatCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, splatCount + count);
            sourceCount++;
        }
        return sourceCount;
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

    int LODSelectionMaxMip()
    {
        int width = Mathf.Max(1, lodChunkSelection != null ? lodChunkSelection.width : 1);
        return Mathf.Max(0, ComputeTextureCoordShift(width));
    }

    Vector4 LODChunkLayout(GaussianSplatLODObject lodObject)
    {
        int width = Mathf.Max(1, lodChunkSelection != null ? lodChunkSelection.width : Mathf.NextPowerOfTwo(Mathf.Max(1, lodObject.GetChunkCount())));
        return new Vector4(width, 1.0f / width, lodObject.GetChunkCount(), LODSelectionMaxMip());
    }

    void BindLODSelection(Material material, GaussianSplatLODObject lodObject, Vector3 cameraPosWorld, Vector3 cameraForwardWorld, int outputBudget, bool adaptAlpha)
    {
        Transform sourceTransform = lodObject.transform;
        int width = Mathf.Max(1, lodChunkSelection != null ? lodChunkSelection.width : Mathf.NextPowerOfTwo(Mathf.Max(1, lodObject.GetChunkCount())));
        material.SetTexture("_LODChunkBoundsMin", lodObject.chunkBoundsMinTexture);
        material.SetTexture("_LODChunkBoundsMax", lodObject.chunkBoundsMaxTexture);
        material.SetTexture("_LODChunkSelection", lodChunkSelection);
        material.SetTexture("_LODAlphaState", lodAlphaState);
        material.SetVector("_LODChunkLayout", LODChunkLayout(lodObject));
        material.SetVector("_LODSelectionParams", new Vector4(GaussianSplatLODObject.MAX_LOD_ALPHA_LOG2, width, LODSelectionMaxMip(), adaptAlpha ? LOD_ALPHA_ADAPT_RATE : 0.0f));
        material.SetVector("_LODDistanceParams", new Vector4(
            Mathf.Max(0.0f, lodObject.lodZeroOffset),
            Mathf.Max(0.001f, lodObject.lodSplatRadius),
            Mathf.Max(0.001f, lodObject.smallestChunkSize),
            Mathf.Max(1.0f, _lodDirectionalBias)));
        material.SetVector("_LODBudgetParams", new Vector4(outputBudget, 0.0f, 0.0f, 0.0f));
        material.SetVector("_LODCameraPosObject", sourceTransform.InverseTransformPoint(cameraPosWorld));
        material.SetVector("_LODCameraForwardObject", sourceTransform.InverseTransformDirection(cameraForwardWorld.normalized));
    }

    void ClearLODSourceSlot(Material material, int slot)
    {
        string suffix = slot.ToString();
        material.SetTexture("_LODSourcePositions" + suffix, null);
        material.SetTexture("_LODSourceColors" + suffix, null);
        material.SetTexture("_LODSourceRotations" + suffix, null);
        material.SetTexture("_LODSourceScales" + suffix, null);
        material.SetTexture("_LODSourceSH" + suffix, null);
        material.SetVector("_LODSourceLayout" + suffix, Vector4.zero);
        material.SetVector("_LODSourceShLayout" + suffix, Vector4.zero);
        material.SetVector("_LODSourceShMin" + suffix, Vector4.zero);
        material.SetVector("_LODSourceShRange" + suffix, Vector4.one);
    }

    bool BindLODSourceBatch(Material material, GaussianSplatLODObject lodObject, int firstTextureSet, int batchCount)
    {
        bool anyValid = false;
        if (lodObject.usePackedPositions)
        {
            material.EnableKeyword("_LOD_PACKED_POSITIONS_ON");
        }
        else
        {
            material.DisableKeyword("_LOD_PACKED_POSITIONS_ON");
        }
        for (int slot = 0; slot < LOD_SOURCE_BATCH_SIZE; slot++)
        {
            int textureSetIndex = firstTextureSet + slot;
            if (slot >= batchCount)
            {
                ClearLODSourceSlot(material, slot);
                continue;
            }

            Texture positions = lodObject.GetPositions(textureSetIndex);
            Texture colors = lodObject.GetColors(textureSetIndex);
            Texture rotations = lodObject.GetRotations(textureSetIndex);
            Texture scales = lodObject.GetScales(textureSetIndex);
            if (positions == null || colors == null || rotations == null || scales == null)
            {
                ClearLODSourceSlot(material, slot);
                continue;
            }

            string suffix = slot.ToString();
            int positionBlocksPerRow = Mathf.Max(1, positions.width >> 2);
            Texture shTexture = lodObject.GetSH(textureSetIndex);
            int shBlocksPerRow = Mathf.Max(1, shTexture != null ? shTexture.width >> 2 : 1);
            material.SetTexture("_LODSourcePositions" + suffix, positions);
            material.SetTexture("_LODSourceColors" + suffix, colors);
            material.SetTexture("_LODSourceRotations" + suffix, rotations);
            material.SetTexture("_LODSourceScales" + suffix, scales);
            material.SetTexture("_LODSourceSH" + suffix, shTexture);
            material.SetVector("_LODSourceLayout" + suffix, new Vector4(
                positionBlocksPerRow - 1,
                ComputeTextureCoordShift(positionBlocksPerRow),
                1.0f,
                0.0f));
            material.SetVector("_LODSourceShLayout" + suffix, new Vector4(
                lodObject.GetFileSHCoeffCount(textureSetIndex),
                lodObject.GetFileSHCoeffStride(textureSetIndex),
                shTexture != null ? shBlocksPerRow - 1 : 0,
                shTexture != null ? ComputeTextureCoordShift(shBlocksPerRow) : 0));
            material.SetVector("_LODSourceShMin" + suffix, lodObject.GetFileSHMin(textureSetIndex));
            material.SetVector("_LODSourceShRange" + suffix, lodObject.GetFileSHRange(textureSetIndex));
            anyValid = true;
        }
        return anyValid;
    }

    void BindLODTransform(Material material, GaussianSplatLODObject lodObject, Vector3 cameraPosWorld, int outputStart, int outputCount, int firstTextureSet, int textureSetBatchCount, int combinedCoordShift)
    {
        Transform sourceTransform = lodObject.transform;
        Quaternion sourceRotation = sourceTransform.rotation;
        Vector3 sourceScale = sourceTransform.lossyScale;
        material.SetTexture("_LODChunkSelection", lodChunkSelection);
        material.SetTexture("_LODChunkBoundsMin", lodObject.chunkBoundsMinTexture);
        material.SetTexture("_LODChunkBoundsMax", lodObject.chunkBoundsMaxTexture);
        material.SetTexture("_LODChunkRange", lodObject.chunkRangeTexture);
        material.SetVector("_LODChunkLayout", LODChunkLayout(lodObject));
        material.SetVector("_LODOutputParams", new Vector4(outputStart, outputCount, firstTextureSet, combinedCoordShift));
        material.SetVector("_LODSourceBatchParams", new Vector4(firstTextureSet, textureSetBatchCount, 0.0f, 0.0f));
        material.SetMatrix("_LODLocalToWorld", sourceTransform.localToWorldMatrix);
        material.SetMatrix("_LODWorldToLocal", sourceTransform.worldToLocalMatrix);
        material.SetVector("_LODTransformRotation", new Vector4(-sourceRotation.x, -sourceRotation.y, -sourceRotation.z, sourceRotation.w));
        material.SetVector("_LODTransformScale", new Vector4(sourceScale.x, sourceScale.y, sourceScale.z, 0.0f));
        material.SetVector("_CameraPosWorld", cameraPosWorld);
    }

    bool RunLODChunkSelection(GaussianSplatLODObject lodObject, Vector3 cameraPosWorld, Vector3 cameraForwardWorld, int outputBudget, bool adaptAlpha, bool useEditorOps)
    {
        if (outputBudget <= 0 || !EnsureLODMaterials() || !EnsureLODChunkTextures(lodObject))
        {
            return false;
        }
        BindLODSelection(lodChunkSelectMaterial, lodObject, cameraPosWorld, cameraForwardWorld, outputBudget, adaptAlpha);
        Blit(lodChunkSelection, lodChunkSelectMaterial, 0, useEditorOps);
        lodChunkSelection.GenerateMips();
        if (adaptAlpha)
        {
            lodChunkSelectMaterial.SetTexture("_LODChunkSelection", lodChunkSelection);
            lodChunkSelectMaterial.SetTexture("_LODAlphaState", lodAlphaState);
            Blit(lodAlphaStateScratch, lodChunkSelectMaterial, 1, useEditorOps);
            SwapLODAlphaStateBuffers();
            BindLODSelection(lodChunkSelectMaterial, lodObject, cameraPosWorld, cameraForwardWorld, outputBudget, false);
            Blit(lodChunkSelection, lodChunkSelectMaterial, 0, useEditorOps);
            lodChunkSelection.GenerateMips();
        }
        return true;
    }

    public void SwapLODAlphaStateBuffers()
    {
        RenderTexture swap = lodAlphaState;
        lodAlphaState = lodAlphaStateScratch;
        lodAlphaStateScratch = swap;
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    int ReadbackLODActualSplatCount(GaussianSplatLODObject lodObject, out float alpha)
    {
        alpha = 0.0f;
        int chunkCount = lodObject != null ? lodObject.GetChunkCount() : 0;
        if (chunkCount <= 0 || lodChunkSelection == null)
        {
            return 0;
        }

        RenderTexture previous = RenderTexture.active;
        Texture2D readback = null;
        Texture2D alphaReadback = null;
        int total = 0;
        Color[] chunkStates = new Color[chunkCount];
        try
        {
            RenderTexture.active = lodChunkSelection;
            readback = new Texture2D(lodChunkSelection.width, lodChunkSelection.height, TextureFormat.RGBAFloat, false, true);
            readback.ReadPixels(new Rect(0, 0, lodChunkSelection.width, lodChunkSelection.height), 0, 0, false);
            readback.Apply(false, false);
            Color[] pixels = readback.GetPixels();
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                if (chunkIndex >= 0 && chunkIndex < pixels.Length)
                {
                    Color pixel = pixels[chunkIndex];
                    chunkStates[chunkIndex] = pixel;
                    int count = Mathf.Max(0, Mathf.RoundToInt(pixel.r));
                    total += count;
                }
            }

            if (lodAlphaState != null)
            {
                RenderTexture.active = lodAlphaState;
                alphaReadback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                alphaReadback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                alphaReadback.Apply(false, false);
                alpha = alphaReadback.GetPixel(0, 0).r;
            }
        }
        finally
        {
            RenderTexture.active = previous;
            if (readback != null)
            {
                DestroyImmediate(readback);
            }
            if (alphaReadback != null)
            {
                DestroyImmediate(alphaReadback);
            }
        }
        _editorLodChunkStates[lodObject] = chunkStates;
        return total;
    }
#endif

    bool RunLODCombineObject(GaussianSplatLODObject lodObject, Vector3 screenCameraPos, int outputStart, int outputBudget, int combinedCoordShift, bool useEditorOps)
    {
        if (outputBudget <= 0 || lodCombineDataMaterial == null || lodChunkSelection == null)
        {
            return false;
        }

        int fileCount = lodObject.GetFileCount();
        for (int fileIndex = 0; fileIndex < fileCount; fileIndex += LOD_SOURCE_BATCH_SIZE)
        {
            int batchCount = Mathf.Min(LOD_SOURCE_BATCH_SIZE, fileCount - fileIndex);
            if (!BindLODSourceBatch(lodCombineDataMaterial, lodObject, fileIndex, batchCount))
            {
                continue;
            }
            BindLODTransform(lodCombineDataMaterial, lodObject, screenCameraPos, outputStart, outputBudget, fileIndex, batchCount, combinedCoordShift);
            Blit(combinedPositions, lodCombineDataMaterial, 0, useEditorOps);
            Blit(combinedRotations, lodCombineDataMaterial, 1, useEditorOps);
            Blit(combinedScales, lodCombineDataMaterial, 2, useEditorOps);
            Blit(combinedColors, lodCombineDataMaterial, 3, useEditorOps);
        }
        return true;
    }

    bool RunLODPhotoColorObject(GaussianSplatLODObject lodObject, Vector3 photoCameraPos, int outputStart, int outputBudget, int combinedCoordShift)
    {
        if (outputBudget <= 0 || lodCombineDataMaterial == null || lodChunkSelection == null)
        {
            return false;
        }

        int fileCount = lodObject.GetFileCount();
        for (int fileIndex = 0; fileIndex < fileCount; fileIndex += LOD_SOURCE_BATCH_SIZE)
        {
            int batchCount = Mathf.Min(LOD_SOURCE_BATCH_SIZE, fileCount - fileIndex);
            if (!BindLODSourceBatch(lodCombineDataMaterial, lodObject, fileIndex, batchCount))
            {
                continue;
            }
            BindLODTransform(lodCombineDataMaterial, lodObject, photoCameraPos, outputStart, outputBudget, fileIndex, batchCount, combinedCoordShift);
            Blit(combinedColorsCamera, lodCombineDataMaterial, 3, false);
        }
        return true;
    }

    bool BindCombinedBatch(ref int sourceCursor, ref int combinedOffset, int positionCapacity, int colorCapacity)
    {
        MeshRenderer ignoredRenderer;
        Material ignoredMaterial;
        Texture ignoredPositions;
        int boundCount = 0;
        int batchSize = GetCombinedSourceBatchSize();
        for (int slot = 0; slot < batchSize; slot++)
        {
            while (sourceCursor < _sceneSplats.Length && !IsSourceActive(sourceCursor))
            {
                sourceCursor++;
            }
            if (sourceCursor < _sceneSplats.Length)
            {
                if (!TryGetSplatSource(_sceneSplats[sourceCursor], out ignoredRenderer, out ignoredMaterial, out ignoredPositions, out int sourceCount))
                {
                    sourceCursor++;
                    slot--;
                    continue;
                }
                if (combinedOffset + sourceCount > positionCapacity || combinedOffset + sourceCount > colorCapacity)
                {
                    _combinedActualSplatCount = 0;
                    SetRendererEnabled(false);
#if !UNITY_EDITOR || COMPILER_UDONSHARP
                    Debug.LogError("Combined Gaussian splat resources are too small for the active scene splats. Refresh the renderer resources in the editor.");
#endif
                    return false;
                }
                SetCombinedSourceSlot(slot, sourceCursor, combinedOffset);
                combinedOffset += sourceCount;
                sourceCursor++;
                boundCount++;
                continue;
            }

            SetCombinedSourceSlot(slot, -1, 0);
        }
        return boundCount > 0;
    }

    public bool UpdateTextures(GaussianSplatObject[] sceneSplats, Vector3 screenCameraPos, Vector3 photoCameraPos, bool useEditorOps)
    {
        return UpdateTexturesWithPhotoFlag(sceneSplats, null, screenCameraPos, screenCameraPos, photoCameraPos, false, 0, true, useEditorOps);
    }

    public bool UpdateTextures(GaussianSplatObject[] sceneSplats, GaussianSplatLODObject[] sceneLods, Vector3 screenCameraPos, Vector3 lodCameraPos, Vector3 photoCameraPos, int lodSplatBudget, bool adaptLodSelection, bool useEditorOps)
    {
        return UpdateTexturesWithPhotoFlag(sceneSplats, sceneLods, screenCameraPos, lodCameraPos, Vector3.forward, photoCameraPos, true, lodSplatBudget, adaptLodSelection, useEditorOps);
    }

    public bool UpdateTexturesWithPhotoFlag(GaussianSplatObject[] sceneSplats, GaussianSplatLODObject[] sceneLods, Vector3 screenCameraPos, Vector3 lodCameraPos, Vector3 photoCameraPos, bool updatePhotoCameraColors, int lodSplatBudget, bool adaptLodSelection, bool useEditorOps)
    {
        return UpdateTexturesWithPhotoFlag(sceneSplats, sceneLods, screenCameraPos, lodCameraPos, Vector3.forward, photoCameraPos, updatePhotoCameraColors, lodSplatBudget, adaptLodSelection, useEditorOps);
    }

    public bool UpdateTexturesWithPhotoFlag(GaussianSplatObject[] sceneSplats, GaussianSplatLODObject[] sceneLods, Vector3 screenCameraPos, Vector3 lodCameraPos, Vector3 lodCameraForward, Vector3 photoCameraPos, bool updatePhotoCameraColors, int lodSplatBudget, bool adaptLodSelection, bool useEditorOps)
    {
        _sceneSplats = sceneSplats != null ? sceneSplats : new GaussianSplatObject[0];
        _sceneLods = sceneLods != null ? sceneLods : new GaussianSplatLODObject[0];
        ResetLODOutputCounts(_sceneLods.Length);
        float lodTargetScale = lodSplatBudget > 0 ? Mathf.Clamp(_lodSplatTargetScale, 0.01f, 1.0f) : 1.0f;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        SetEditorReadback(0, 0, 0.0f);
        for (int i = 0; i < _sceneLods.Length; i++)
        {
            if (_sceneLods[i] != null)
            {
                _editorLodChunkStates.Remove(_sceneLods[i]);
            }
        }
#endif
        if (combinedSortedRenderer == null || combinedPositions == null || combinedRotations == null || combinedScales == null || combinedColors == null || combinedColorsCamera == null || combineDataMaterial == null)
        {
#if !UNITY_EDITOR || COMPILER_UDONSHARP
            Debug.LogError("Combined rendering mode is missing generated resources. Refresh the GaussianSplatRenderer in the editor.");
#endif
            return false;
        }
        int normalSplatCount;
        int activeSourceCount = CountActiveNormalSplatSources(out normalSplatCount);
        int activeLodObjectCount = CountActiveGPULODObjects();
        activeSourceCount += activeLodObjectCount;
        if (activeSourceCount == 0)
        {
            _combinedActualSplatCount = 0;
            SetRendererEnabled(false);
            return false;
        }
        int combinedBlocksPerRow = Mathf.Max(1, combinedPositions.width >> 2);
        int combinedCoordShift = ComputeTextureCoordShift(combinedBlocksPerRow);
        combineDataMaterial.SetInt("_CombinedCoordShift", combinedCoordShift);
        int positionCapacity = combinedPositions.width * combinedPositions.height;
        int colorCapacity = combinedColors.width * combinedColors.height;
        int combinedCapacity = Mathf.Min(positionCapacity, colorCapacity);
        Blit(Texture2D.blackTexture, combinedPositions, useEditorOps);
        Blit(Texture2D.blackTexture, combinedRotations, useEditorOps);
        Blit(Texture2D.blackTexture, combinedScales, useEditorOps);
        Blit(Texture2D.blackTexture, combinedColors, useEditorOps);
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
            Blit(combinedColors, combineDataMaterial, 3, useEditorOps);
            if (combinedOffset == batchStartOffset)
            {
                break;
            }
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        int editorReadbackCount = combinedOffset;
        float editorReadbackAlpha = 0.0f;
#endif
        int lodRemainingBudget = lodSplatBudget > 0 ? Mathf.Max(0, lodSplatBudget - normalSplatCount) : 0;
        for (int lodIndex = 0; lodIndex < _sceneLods.Length; lodIndex++)
        {
            GaussianSplatLODObject lodObject = _sceneLods[lodIndex];
            if (!IsLodObjectActive(lodIndex) || !IsLodObjectGPUReady(lodObject))
            {
                continue;
            }

            int remainingCapacity = combinedCapacity - combinedOffset;
            if (remainingCapacity <= 0)
            {
                break;
            }

            int objectHardBudget = lodSplatBudget > 0 ? lodRemainingBudget : lodObject.GetMaxLOD0SplatCount();
            objectHardBudget = Mathf.Min(Mathf.Max(0, objectHardBudget), remainingCapacity);
            int objectSelectionTarget = lodSplatBudget > 0 ? ComputeLODTargetBudget(objectHardBudget, lodTargetScale) : objectHardBudget;
            if (objectHardBudget <= 0 || objectSelectionTarget <= 0)
            {
                continue;
            }

            if (RunLODChunkSelection(lodObject, lodCameraPos, lodCameraForward, objectSelectionTarget, adaptLodSelection, useEditorOps))
            {
                int lodOutputCount = objectHardBudget;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
                if (useEditorOps)
                {
                    int lodActualCount = ReadbackLODActualSplatCount(lodObject, out float lodAlpha);
                    lodOutputCount = Mathf.Min(lodOutputCount, Mathf.Max(0, lodActualCount));
                    editorReadbackCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, editorReadbackCount + lodOutputCount);
                    if (lodActualCount > 0 && editorReadbackAlpha <= 0.0f)
                    {
                        editorReadbackAlpha = lodAlpha;
                    }
                }
                else
                {
                    editorReadbackCount = Mathf.Min(MAX_COMBINED_SPLAT_COUNT, editorReadbackCount + lodOutputCount);
                }
#endif
                if (lodOutputCount <= 0)
                {
                    continue;
                }
                _lodOutputCounts[lodIndex] = lodOutputCount;
                RunLODCombineObject(lodObject, lodCameraPos, combinedOffset, lodOutputCount, combinedCoordShift, useEditorOps);
                combinedOffset += lodOutputCount;
                if (lodSplatBudget > 0)
                {
                    lodRemainingBudget = Mathf.Max(0, lodRemainingBudget - lodOutputCount);
                }
            }
        }

        if (combinedOffset <= 0)
        {
            _combinedActualSplatCount = 0;
            SetRendererEnabled(false);
            return false;
        }
        _combinedActualSplatCount = combinedOffset;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        SetEditorReadback(useEditorOps ? editorReadbackCount : combinedOffset, combinedOffset, useEditorOps ? editorReadbackAlpha : 0.0f);
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

        Blit(Texture2D.blackTexture, combinedColorsCamera, false);
        sourceCursor = 0;
        combinedOffset = 0;
        int photoNormalEndOffset = 0;
        while (true)
        {
            combineDataMaterial.SetVector("_CameraPosWorld", photoCameraPos);
            int photoBatchStartOffset = combinedOffset;
            bool hasPhotoBatch = BindCombinedBatch(ref sourceCursor, ref combinedOffset, positionCapacity, colorCapacity);
            if (!hasPhotoBatch)
            {
                break;
            }
            Blit(combinedColorsCamera, combineDataMaterial, 3, false);
            if (combinedOffset == photoBatchStartOffset)
            {
                break;
            }
        }
        photoNormalEndOffset = combinedOffset;
        int lodPhotoOffset = photoNormalEndOffset;
        for (int lodIndex = 0; lodIndex < _sceneLods.Length; lodIndex++)
        {
            GaussianSplatLODObject lodObject = _sceneLods[lodIndex];
            if (!IsLodObjectActive(lodIndex) || !IsLodObjectGPUReady(lodObject))
            {
                continue;
            }
            int remainingCapacity = combinedCapacity - lodPhotoOffset;
            if (remainingCapacity <= 0)
            {
                break;
            }
            int lodOutputCount = _lodOutputCounts != null && lodIndex < _lodOutputCounts.Length ? _lodOutputCounts[lodIndex] : 0;
            lodOutputCount = Mathf.Min(Mathf.Max(0, lodOutputCount), remainingCapacity);
            if (lodOutputCount <= 0)
            {
                continue;
            }
            RunLODPhotoColorObject(lodObject, photoCameraPos, lodPhotoOffset, lodOutputCount, combinedCoordShift);
            lodPhotoOffset += lodOutputCount;
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
        primaryMaterial = ResolvePrimarySplatMaterial(GetRendererMaterialsForRead(sortedRenderer));
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

    public float GetEditorReadbackAlpha()
    {
        return _editorReadbackAlphas.TryGetValue(this, out float value) ? value : 0.0f;
    }

    public Color[] GetEditorLODChunkStates(GaussianSplatLODObject lodObject)
    {
        return lodObject != null && _editorLodChunkStates.TryGetValue(lodObject, out Color[] states) ? states : null;
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
