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
    const int COMBINED_SOURCE_BATCH_SIZE = 8;
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;
    const int DEFAULT_COMBINED_SPLATS_PER_PASS = 3 * 256 * 1024;
    const int DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT = 1;

    [SerializeField] GaussianSplatRenderer gaussianSplatRenderer;
    [SerializeField] MeshRenderer combinedSortedRenderer;
    [SerializeField] Material combineDataMaterial;
    [SerializeField] RenderTextureFormat combinedPositionsFormat = RenderTextureFormat.ARGBFloat, combinedRotationsFormat = RenderTextureFormat.ARGBHalf, combinedScalesFormat = RenderTextureFormat.ARGBHalf, combinedColorsFormat = RenderTextureFormat.ARGB32, combinedColorsCameraFormat = RenderTextureFormat.ARGB32;
    [SerializeField, HideInInspector] bool combinedTextureFormatsInitialized = true;
    [SerializeField] int combinedStartRenderQueue = 4050;
    [SerializeField] RenderTexture combinedPositions, combinedRotations, combinedScales, combinedColors, combinedColorsCamera;
    [SerializeField] int builtCombinedElementCount;

    int _combinedActualSplatCount;
    GaussianSplatObject[] _sceneSplats = new GaussianSplatObject[0];

    public MeshRenderer GetCombinedSortedRenderer() { return combinedSortedRenderer; }
    public GameObject GetCombinedObject() { return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : null; }
    public string GetCombinedObjectName() { return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject.name : "Combined"; }

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

    bool IsSourceActive(int index)
    {
        return index >= 0 && index < _sceneSplats.Length && _sceneSplats[index] != null && _sceneSplats[index].gameObject.activeInHierarchy;
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

    void SetRenderOrderOnMaterials(Material[] materials, int actualCount, RenderTexture splatRenderOrder)
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

    public bool EnsureResourcesCreated()
    {
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
        }
        return boundCount > 0;
    }

    public bool UpdateTextures(GaussianSplatObject[] sceneSplats, Vector3 screenCameraPos, Vector3 photoCameraPos, bool useEditorOps)
    {
        _sceneSplats = sceneSplats != null ? sceneSplats : new GaussianSplatObject[0];
        if (combinedSortedRenderer == null || combinedPositions == null || combinedRotations == null || combinedScales == null || combinedColors == null || combinedColorsCamera == null || combineDataMaterial == null)
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
            SetRendererEnabled(false);
            return false;
        }
        int combinedBlocksPerRow = Mathf.Max(1, combinedPositions.width >> 2);
        combineDataMaterial.SetInt("_CombinedCoordShift", ComputeTextureCoordShift(combinedBlocksPerRow));
        int positionCapacity = combinedPositions.width * combinedPositions.height;
        int colorCapacity = combinedColors.width * combinedColors.height;
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
        if (combinedOffset <= 0)
        {
            _combinedActualSplatCount = 0;
            SetRendererEnabled(false);
            return false;
        }
        _combinedActualSplatCount = combinedOffset;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            // No photo camera in the editor: mirror the screen colors into the camera texture.
            Blit(combinedColors, combinedColorsCamera, true);
            return true;
        }
#endif

        Blit(Texture2D.blackTexture, combinedColorsCamera, false);
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
            Blit(combinedColorsCamera, combineDataMaterial, 3, false);
            if (combinedOffset == photoBatchStartOffset)
            {
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// Applies the render-order texture + actual splat count to the combined parent + chunk materials,
    /// toggles chunk visibility, and resolves the primary sort renderer/material/positions for the
    /// renderer to bind sort keys against. Returns false (and disables the combined object) when the
    /// combined resources are not ready.
    /// </summary>
    public bool BindRenderOrder(RenderTexture splatRenderOrder, out MeshRenderer sortedRenderer, out Material primaryMaterial, out Texture positions, out int count)
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
        SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(combinedSortedRenderer), _combinedActualSplatCount, splatRenderOrder);
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
            SetRenderOrderOnMaterials(GetRendererMaterialsForWrite(chunkRenderer), _combinedActualSplatCount, splatRenderOrder);
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
