#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;

namespace GaussianSplatting
{

// Editor-only resource management, scene wiring, and combined-hierarchy bookkeeping for
// GaussianSplatCombiner. Kept in a partial file so the runtime behaviour stays small; the whole
// file is excluded from Udon compilation via the preprocessor guard above.
public partial class GaussianSplatCombiner
{
    static bool EnsureGeneratedObjectEditable(GameObject generatedObject)
    {
        if (generatedObject == null || generatedObject.hideFlags == HideFlags.None)
        {
            return false;
        }
        generatedObject.hideFlags = HideFlags.None;
        EditorUtility.SetDirty(generatedObject);
        return true;
    }

    public bool EnsureGeneratedHierarchyState(bool disableRoot)
    {
        MeshRenderer meshRenderer = combinedSortedRenderer;
        GameObject root = meshRenderer != null ? meshRenderer.gameObject : null;
        if (root == null)
        {
            return false;
        }
        bool changed = EnsureGeneratedObjectEditable(root);
        Transform rootTransform = root.transform;
        if (rootTransform.parent != null)
        {
            Undo.SetTransformParent(rootTransform, null, "Reparent Combined Gaussian Splat Renderer");
            changed = true;
        }
        if (rootTransform.localPosition != Vector3.zero || rootTransform.localRotation != Quaternion.identity || rootTransform.localScale != Vector3.one)
        {
            Undo.RecordObject(rootTransform, "Reset Combined Gaussian Splat Renderer Transform");
            rootTransform.localPosition = Vector3.zero;
            rootTransform.localRotation = Quaternion.identity;
            rootTransform.localScale = Vector3.one;
            EditorUtility.SetDirty(rootTransform);
            changed = true;
        }
        if (disableRoot && root.activeSelf)
        {
            Undo.RecordObject(root, "Disable Combined Gaussian Splat Renderer");
            root.SetActive(false);
            EditorUtility.SetDirty(root);
            changed = true;
        }
        for (int i = 0; i < rootTransform.childCount; i++)
        {
            Transform child = rootTransform.GetChild(i);
            if (!child.name.StartsWith("CombinedChunk"))
            {
                continue;
            }
            changed |= EnsureGeneratedObjectEditable(child.gameObject);
            if (child.localPosition != Vector3.zero || child.localRotation != Quaternion.identity || child.localScale != Vector3.one)
            {
                Undo.RecordObject(child, "Reset Combined Gaussian Splat Chunk Transform");
                child.localPosition = Vector3.zero;
                child.localRotation = Quaternion.identity;
                child.localScale = Vector3.one;
                EditorUtility.SetDirty(child);
                changed = true;
            }
        }
        return changed;
    }

    void CopyPersistentStateFrom(GaussianSplatCombiner source)
    {
        if (source == null || source == this)
        {
            return;
        }

        source.EnsureCombinedTextureFormatsInitialized();

        gaussianSplatRenderer = source.gaussianSplatRenderer;
        combinedSortedRenderer = source.combinedSortedRenderer;
        combineDataMaterial = source.combineDataMaterial;
        lodChunkSelectMaterial = source.lodChunkSelectMaterial;
        lodCombineDataMaterial = source.lodCombineDataMaterial;
        combinedPositionsFormat = source.combinedPositionsFormat;
        combinedRotationsFormat = source.combinedRotationsFormat;
        combinedScalesFormat = source.combinedScalesFormat;
        combinedColorsFormat = source.combinedColorsFormat;
        combinedColorsCameraFormat = source.combinedColorsCameraFormat;
        combinedTextureFormatsInitialized = true;
        combinedPositions = source.combinedPositions;
        combinedRotations = source.combinedRotations;
        combinedScales = source.combinedScales;
        combinedColors = source.combinedColors;
        combinedColorsCamera = source.combinedColorsCamera;
        lodChunkSelection = source.lodChunkSelection;
        lodAlphaState = source.lodAlphaState;
        lodAlphaStateScratch = source.lodAlphaStateScratch;
        builtCombinedElementCount = source.builtCombinedElementCount;
    }

    void SetDefaultCombinedTextureFormats()
    {
        combinedPositionsFormat = RenderTextureFormat.ARGBFloat;
        combinedRotationsFormat = RenderTextureFormat.ARGB32;
        combinedScalesFormat = RenderTextureFormat.ARGBHalf;
        combinedColorsFormat = RenderTextureFormat.ARGB32;
        combinedColorsCameraFormat = RenderTextureFormat.ARGB32;
        combinedTextureFormatsInitialized = true;
    }

    bool EnsureCombinedTextureFormatsInitialized()
    {
        if (combinedTextureFormatsInitialized)
        {
            return false;
        }
        SetDefaultCombinedTextureFormats();
        return true;
    }

    void OnValidate()
    {
        if (EditorUtility.IsPersistent(this) || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
        {
            return;
        }
        if (EnsureCombinedTextureFormatsInitialized())
        {
            EditorUtility.SetDirty(this);
        }
    }

    static GameObject FindOrCreateCombinedObject(Scene scene)
    {
        GameObject combinedObject = null;
        if (scene.IsValid())
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] != null && roots[i].name == "CombinedSorted")
                {
                    combinedObject = roots[i];
                    break;
                }
            }
        }

        if (combinedObject == null)
        {
            combinedObject = new GameObject("CombinedSorted");
            combinedObject.hideFlags = HideFlags.None;
            Undo.RegisterCreatedObjectUndo(combinedObject, "Create Combined Gaussian Splat Renderer");
            if (scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(combinedObject, scene);
            }
        }
        else
        {
            EnsureGeneratedObjectEditable(combinedObject);
        }

        if (combinedObject.GetComponent<MeshFilter>() == null)
        {
            Undo.AddComponent<MeshFilter>(combinedObject);
        }
        if (combinedObject.GetComponent<MeshRenderer>() == null)
        {
            Undo.AddComponent<MeshRenderer>(combinedObject);
        }

        return combinedObject;
    }

    static bool HasValidBackingProgram(GaussianSplatCombiner combiner)
    {
        if (combiner == null)
        {
            return false;
        }

        var backingBehaviour = UdonSharpEditorUtility.GetBackingUdonBehaviour(combiner);
        return backingBehaviour != null && backingBehaviour.programSource != null;
    }

    static GaussianSplatCombiner CleanupCombinedBehaviours(GameObject combinedObject)
    {
        if (combinedObject == null)
        {
            return null;
        }

        GaussianSplatCombiner[] combiners = combinedObject.GetComponents<GaussianSplatCombiner>();
        GaussianSplatCombiner preferredCombiner = null;
        for (int i = 0; i < combiners.Length; i++)
        {
            if (HasValidBackingProgram(combiners[i]))
            {
                preferredCombiner = combiners[i];
                break;
            }
        }
        if (preferredCombiner == null && combiners.Length > 0 && HasValidBackingProgram(combiners[combiners.Length - 1]))
        {
            preferredCombiner = combiners[combiners.Length - 1];
        }
        for (int i = 0; i < combiners.Length; i++)
        {
            if (combiners[i] == null)
            {
                continue;
            }
            if (combiners[i] != preferredCombiner)
            {
                Undo.DestroyObjectImmediate(combiners[i]);
            }
        }

        VRC.Udon.UdonBehaviour[] udonBehaviours = combinedObject.GetComponents<VRC.Udon.UdonBehaviour>();
        for (int i = 0; i < udonBehaviours.Length; i++)
        {
            if (udonBehaviours[i] == null)
            {
                continue;
            }

            UdonSharp.UdonSharpBehaviour proxyBehaviour = UdonSharpEditorUtility.GetProxyBehaviour(udonBehaviours[i]);
            if (udonBehaviours[i].programSource == null || !(proxyBehaviour is GaussianSplatCombiner))
            {
                Undo.DestroyObjectImmediate(udonBehaviours[i]);
            }
        }

        return preferredCombiner != null && HasValidBackingProgram(preferredCombiner) ? preferredCombiner : null;
    }

    static void RefreshCombinerProgramAssetLookup()
    {
        AssetDatabase.ImportAsset("Assets/VRChatGaussianSplatting/Scripts/GaussianSplatCombiner.asset", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        typeof(UdonSharp.UdonSharpProgramAsset).GetMethod("ClearProgramAssetCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
        typeof(UdonSharpEditorUtility).GetMethod("ResetCaches", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
    }

    public static GaussianSplatCombiner EnsureSceneCombiner(GaussianSplatRenderer owner)
    {
        if (owner == null)
        {
            return null;
        }

        GameObject combinedObject = FindOrCreateCombinedObject(owner.gameObject.scene);
        MeshRenderer meshRenderer = combinedObject != null ? combinedObject.GetComponent<MeshRenderer>() : null;
        if (combinedObject == null || meshRenderer == null)
        {
            return null;
        }

        GaussianSplatCombiner sceneCombiner = CleanupCombinedBehaviours(combinedObject);
        if (sceneCombiner == null)
        {
            RefreshCombinerProgramAssetLookup();
            if (UdonSharpEditorUtility.GetUdonSharpProgramAsset(typeof(GaussianSplatCombiner)) == null)
            {
                return null;
            }
            sceneCombiner = combinedObject.AddUdonSharpComponent<GaussianSplatCombiner>();
            sceneCombiner = CleanupCombinedBehaviours(combinedObject) ?? sceneCombiner;
        }

        GaussianSplatCombiner staleCombiner = owner.gameObject.GetComponent<GaussianSplatCombiner>();
        if (staleCombiner != null && staleCombiner != sceneCombiner)
        {
            Undo.RecordObject(sceneCombiner, "Migrate Combined Gaussian Splat Combiner");
            sceneCombiner.CopyPersistentStateFrom(staleCombiner);
            EditorUtility.SetDirty(sceneCombiner);
        }

        if (sceneCombiner.gaussianSplatRenderer != owner)
        {
            Undo.RecordObject(sceneCombiner, "Assign Combined Gaussian Splat Owner");
            sceneCombiner.gaussianSplatRenderer = owner;
            EditorUtility.SetDirty(sceneCombiner);
        }

        if (sceneCombiner.combinedSortedRenderer != meshRenderer)
        {
            Undo.RecordObject(sceneCombiner, "Assign Combined Gaussian Splat Renderer Root");
            sceneCombiner.combinedSortedRenderer = meshRenderer;
            EditorUtility.SetDirty(sceneCombiner);
        }

        if (sceneCombiner.EnsureCombinedTextureFormatsInitialized())
        {
            EditorUtility.SetDirty(sceneCombiner);
        }

        if (owner.GetCombiner() != sceneCombiner)
        {
            Undo.RecordObject(owner, "Assign Combined Gaussian Splat Combiner");
            owner.SetCombiner(sceneCombiner);
            EditorUtility.SetDirty(owner);
        }

        if (staleCombiner != null && staleCombiner != sceneCombiner)
        {
            Undo.DestroyObjectImmediate(staleCombiner);
        }

        return sceneCombiner;
    }

    GaussianSplatRenderer ResolveOwner()
    {
        return GetOwnerRenderer();
    }

    bool OwnerIsCombinedMode()
    {
        GaussianSplatRenderer owner = ResolveOwner();
        return owner != null && owner.IsCombinedRenderingMode();
    }

    bool EnsureOwnerChunkHierarchy(GaussianSplatRenderer owner)
    {
        if (owner == null)
        {
            return false;
        }

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
        if (builderType == null)
        {
            return false;
        }

        var ensureChunkHierarchy = builderType.GetMethod("EnsureChunkHierarchy", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (ensureChunkHierarchy == null)
        {
            return false;
        }

        object result = ensureChunkHierarchy.Invoke(null, new object[] { owner });
        return result is bool hierarchyChanged && hierarchyChanged;
    }

    static bool UsesCombinedHierarchyShader(Material material, string shaderName)
    {
        return material != null && material.shader != null && material.shader.name == shaderName;
    }

    bool CombinedMaterialQueuesMatch(PlySplatImporter.PassInfo[] passInfos, bool useSrgb)
    {
        if (combinedSortedRenderer == null)
        {
            return false;
        }

        int expectedRenderQueue = GetEffectiveStartRenderQueue();
        Material[] parentMaterials = combinedSortedRenderer.sharedMaterials;

        if (useSrgb)
        {
            if (parentMaterials == null || parentMaterials.Length < 2)
            {
                return false;
            }
            if (!UsesCombinedHierarchyShader(parentMaterials[0], "VRChatGaussianSplatting/ToSRGB") || parentMaterials[0].renderQueue != expectedRenderQueue++)
            {
                return false;
            }
        }

        for (int passIndex = 0; passIndex < passInfos.Length; passIndex++)
        {
            Transform chunkTransform = combinedSortedRenderer.transform.Find("CombinedChunk" + passIndex);
            if (chunkTransform == null)
            {
                return false;
            }

            MeshRenderer chunkRenderer = chunkTransform.GetComponent<MeshRenderer>();
            if (chunkRenderer == null)
            {
                return false;
            }
            MeshFilter chunkFilter = chunkTransform.GetComponent<MeshFilter>();
            if (!ChunkMeshMatchesSplatCount(chunkFilter != null ? chunkFilter.sharedMesh : null, passInfos[passIndex].SplatCount, passInfos[passIndex].HasAlphaMask))
            {
                return false;
            }

            Material[] chunkMaterials = chunkRenderer.sharedMaterials;
            int chunkMaterialIndex = 0;
            if (passInfos[passIndex].HasAlphaMask)
            {
                if (chunkMaterials == null || chunkMaterials.Length < 2)
                {
                    return false;
                }
                if (!UsesCombinedHierarchyShader(chunkMaterials[0], "VRChatGaussianSplatting/AlphaDepthMask") || chunkMaterials[0].renderQueue != expectedRenderQueue++)
                {
                    return false;
                }
                chunkMaterialIndex = 1;
            }

            if (chunkMaterials == null || chunkMaterials.Length <= chunkMaterialIndex)
            {
                return false;
            }

            Material splatMaterial = chunkMaterials[chunkMaterialIndex];
            if (splatMaterial == null || !splatMaterial.HasProperty("_SplatCount") || splatMaterial.renderQueue != expectedRenderQueue++)
            {
                return false;
            }
        }

        if (useSrgb)
        {
            Material toLinear = parentMaterials[parentMaterials.Length - 1];
            if (!UsesCombinedHierarchyShader(toLinear, "VRChatGaussianSplatting/ToLinear") || toLinear.renderQueue != expectedRenderQueue)
            {
                return false;
            }
        }

        return true;
    }

    static bool ChunkMeshMatchesSplatCount(Mesh mesh, int splatCount, bool hasAlphaMask)
    {
        int splatSubMesh = hasAlphaMask ? 1 : 0;
        return mesh != null
            && mesh.subMeshCount > splatSubMesh
            && mesh.GetTopology(splatSubMesh) == MeshTopology.Points
            && mesh.GetIndexCount(splatSubMesh) == (uint)((splatCount + 31) / 32);
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
        if (!GaussianSplatRenderer.MaterialArraysMatch(meshRenderer.sharedMaterials, parentMaterials.ToArray()))
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
            if (!GaussianSplatRenderer.MaterialArraysMatch(chunkRenderer.sharedMaterials, chunkMaterials))
            {
                return false;
            }
            MeshFilter chunkFilter = chunkTransform.GetComponent<MeshFilter>();
            if (!ChunkMeshMatchesSplatCount(chunkFilter != null ? chunkFilter.sharedMesh : null, Mathf.Max(0, splatMaterial.GetInt("_SplatCount")), alphaMask != null))
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

    bool EnsureCombinedRendererRoot(Material[] combinedMaterials)
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
            combinedObject.hideFlags = HideFlags.None;
            Undo.RegisterCreatedObjectUndo(combinedObject, "Create Combined Gaussian Splat Renderer");
            SceneManager.MoveGameObjectToScene(combinedObject, gameObject.scene);
            changed = true;
        }
        else if (EnsureGeneratedObjectEditable(combinedObject))
        {
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
        if (!CombinedHierarchyMatches(meshRenderer, combinedMaterials) && !GaussianSplatRenderer.MaterialArraysMatch(meshRenderer.sharedMaterials, combinedMaterials))
        {
            Undo.RecordObject(meshRenderer, "Update Combined Gaussian Splat Materials");
            meshRenderer.sharedMaterials = combinedMaterials;
            EditorUtility.SetDirty(meshRenderer);
            changed = true;
        }
        if (meshRenderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off ||
            meshRenderer.receiveShadows ||
            meshRenderer.lightProbeUsage != UnityEngine.Rendering.LightProbeUsage.Off ||
            meshRenderer.reflectionProbeUsage != UnityEngine.Rendering.ReflectionProbeUsage.Off ||
            meshRenderer.motionVectorGenerationMode != MotionVectorGenerationMode.ForceNoMotion ||
            meshRenderer.allowOcclusionWhenDynamic)
        {
            Undo.RecordObject(meshRenderer, "Update Combined Gaussian Splat Renderer Settings");
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            meshRenderer.allowOcclusionWhenDynamic = false;
            EditorUtility.SetDirty(meshRenderer);
            changed = true;
        }
        return changed;
    }

    int GetEffectiveStartRenderQueue()
    {
        GaussianSplatRenderer owner = ResolveOwner();
        return owner != null && owner.TryGetRenderQueueOverride(out int renderQueue) ? renderQueue : combinedStartRenderQueue;
    }

    int GetSceneMaxLODChunkCount()
    {
        int maxChunks = 1;
        GaussianSplatLODObject[] lodObjects = UnityEngine.Object.FindObjectsOfType<GaussianSplatLODObject>(true);
        for (int i = 0; i < lodObjects.Length; i++)
        {
            GaussianSplatLODObject lodObject = lodObjects[i];
            if (lodObject != null && lodObject.gameObject.scene == gameObject.scene)
            {
                maxChunks = Mathf.Max(maxChunks, lodObject.GetChunkCount());
            }
        }
        return maxChunks;
    }

    public void UpdateResources(int combinedElementCount)
    {
        if (combinedElementCount <= 0)
        {
            return;
        }
        if (EnsureCombinedTextureFormatsInitialized())
        {
            EditorUtility.SetDirty(this);
        }
        GaussianSplatRenderer owner = ResolveOwner();
        string ownerName = owner != null ? owner.name : name;
        PlySplatImporter.TextureLayout combinedLayout = PlySplatImporter.ChoosePotTextureLayout(combinedElementCount);
        int combinedWidth = combinedLayout.Width;
        int combinedHeight = combinedLayout.Height;
        string combinedFolderPath = PlySplatImporter.GetSceneTempResourceFolderPath(gameObject.scene, "RTs") + "/Combined";
        string assetPrefix = PlySplatImporter.SanitizeAssetName(ownerName);
        RenderTexture previousCombinedPositions = combinedPositions;
        RenderTexture previousCombinedRotations = combinedRotations;
        RenderTexture previousCombinedScales = combinedScales;
        RenderTexture previousCombinedColors = combinedColors;
        RenderTexture previousCombinedColorsCamera = combinedColorsCamera;
        RenderTexture previousLodChunkSelection = lodChunkSelection;
        RenderTexture previousLodAlphaState = lodAlphaState;
        RenderTexture previousLodAlphaStateScratch = lodAlphaStateScratch;
        Material previousCombineDataMaterial = combineDataMaterial;
        MeshRenderer previousCombinedSortedRenderer = combinedSortedRenderer;
        bool resourcesChanged = false;
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref combinedPositions, combinedFolderPath, assetPrefix + "_CombinedPositions", combinedWidth, combinedHeight, combinedPositionsFormat, false, 1);
        // Quaternions are baked into the combined texture here; the format is user-configurable
        // because precision and memory tradeoffs depend on the scene.
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref combinedRotations, combinedFolderPath, assetPrefix + "_CombinedRotations", combinedWidth, combinedHeight, combinedRotationsFormat, false, 1);
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref combinedScales, combinedFolderPath, assetPrefix + "_CombinedScales", combinedWidth, combinedHeight, combinedScalesFormat, false, 1);
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref combinedColors, combinedFolderPath, assetPrefix + "_CombinedColors", combinedWidth, combinedHeight, combinedColorsFormat, false, 1);
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref combinedColorsCamera, combinedFolderPath, assetPrefix + "_CombinedColorsCamera", combinedWidth, combinedHeight, combinedColorsCameraFormat, false, 1);
        int lodSelectionWidth = Mathf.NextPowerOfTwo(Mathf.Max(1, GetSceneMaxLODChunkCount()));
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref lodChunkSelection, combinedFolderPath, assetPrefix + "_LODChunkSelection", lodSelectionWidth, 1, RenderTextureFormat.ARGBFloat, true, 1);
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref lodAlphaState, combinedFolderPath, assetPrefix + "_LODAlphaState", 1, 1, RenderTextureFormat.ARGBFloat, false, 1);
        resourcesChanged |= PlySplatImporter.EnsureSortRenderTexture(ref lodAlphaStateScratch, combinedFolderPath, assetPrefix + "_LODAlphaStateScratch", 1, 1, RenderTextureFormat.ARGBFloat, false, 1);
        bool useSrgb = true;
        PlySplatImporter.PassInfo[] passInfos = PlySplatImporter.CreatePassLayout(combinedElementCount, Mathf.Min(DEFAULT_COMBINED_SPLATS_PER_PASS, combinedElementCount), DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT, useSrgb);
        bool ownerCombinedMode = OwnerIsCombinedMode();
        bool hierarchyStateChanged = EnsureGeneratedHierarchyState(false);
        bool chunkHierarchyChanged = EnsureOwnerChunkHierarchy(owner);
        bool sortedRendererSatisfied = combinedSortedRenderer != null && (ownerCombinedMode || !combinedSortedRenderer.gameObject.activeSelf);
        if (!resourcesChanged
            && builtCombinedElementCount == combinedElementCount
            && combineDataMaterial != null
            && lodChunkSelectMaterial != null
            && lodCombineDataMaterial != null
            && sortedRendererSatisfied
            && CombinedMaterialQueuesMatch(passInfos, useSrgb))
        {
            if (hierarchyStateChanged || chunkHierarchyChanged)
            {
                EditorUtility.SetDirty(this);
            }
            return;
        }
        bool rendererVisibilityChanged = false;
        if (combinedSortedRenderer != null && combinedSortedRenderer.gameObject.activeSelf)
        {
            Undo.RecordObject(combinedSortedRenderer.gameObject, "Disable Combined Gaussian Splat Renderer While Refreshing");
            combinedSortedRenderer.gameObject.SetActive(false);
            EditorUtility.SetDirty(combinedSortedRenderer.gameObject);
            rendererVisibilityChanged = true;
        }
        Shader combineShader = Shader.Find("Hidden/GaussianSplatting/CombineData");
        if (combineShader == null)
        {
            return;
        }
        Material combineMaterial = new Material(combineShader);
        combineMaterial.name = assetPrefix + "_CombineData";
        combineDataMaterial = PlySplatImporter.CreateOrReplaceAsset(combineMaterial, combinedFolderPath + "/" + assetPrefix + "_CombineData.mat");
        Shader lodChunkSelectShader = Shader.Find("Hidden/GaussianSplatting/LODChunkSelect");
        Shader lodCombineShader = Shader.Find("Hidden/GaussianSplatting/LODCombineData");
        if (lodChunkSelectShader != null)
        {
            Material chunkSelectMaterial = new Material(lodChunkSelectShader);
            chunkSelectMaterial.name = assetPrefix + "_LODChunkSelect";
            lodChunkSelectMaterial = PlySplatImporter.CreateOrReplaceAsset(chunkSelectMaterial, combinedFolderPath + "/" + assetPrefix + "_LODChunkSelect.mat");
        }
        if (lodCombineShader != null)
        {
            Material lodCombineMaterial = new Material(lodCombineShader);
            lodCombineMaterial.name = assetPrefix + "_LODCombineData";
            lodCombineDataMaterial = PlySplatImporter.CreateOrReplaceAsset(lodCombineMaterial, combinedFolderPath + "/" + assetPrefix + "_LODCombineData.mat");
        }
        List<Material> generatedMaterials = new List<Material>();
        List<int> generatedRenderQueues = new List<int>();
        int renderQueue = GetEffectiveStartRenderQueue();
        if (useSrgb)
        {
            Material toSrgb = PlySplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/ToSRGB", assetPrefix + "_CombinedToSRGB");
            if (toSrgb != null)
            {
                toSrgb.renderQueue = renderQueue++;
                generatedMaterials.Add(toSrgb);
                generatedRenderQueues.Add(toSrgb.renderQueue);
            }
        }
        Material mainMaterial = null;
        for (int passIndex = 0; passIndex < passInfos.Length; passIndex++)
        {
            PlySplatImporter.PassInfo passInfo = passInfos[passIndex];
            string materialName = assetPrefix + (passInfo.PassIndex > 0 ? "_CombinedPass" + passInfo.PassIndex : "_CombinedMain") + "_Splat";
            Material splatMaterial = passInfo.PassIndex == 0
                ? PlySplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/GaussianSplatting", materialName)
                : (mainMaterial != null ? new Material(mainMaterial) : PlySplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/GaussianSplatting", materialName));
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
                combinedColors,
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
            if (owner != null)
            {
                owner.ApplyConfiguredMaterialSettingsForCombined(splatMaterial);
            }
            if (passInfo.HasAlphaMask)
            {
                Material alphaMask = PlySplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/AlphaDepthMask", materialName + "_AlphaDepthMask");
                if (alphaMask != null)
                {
                    alphaMask.renderQueue = renderQueue++;
                    generatedMaterials.Add(alphaMask);
                    generatedRenderQueues.Add(alphaMask.renderQueue);
                }
            }
            splatMaterial.renderQueue = renderQueue++;
            generatedMaterials.Add(splatMaterial);
            generatedRenderQueues.Add(splatMaterial.renderQueue);
        }
        if (useSrgb)
        {
            Material toLinear = PlySplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/ToLinear", assetPrefix + "_CombinedToLinear");
            if (toLinear != null)
            {
                toLinear.renderQueue = renderQueue++;
                generatedMaterials.Add(toLinear);
                generatedRenderQueues.Add(toLinear.renderQueue);
            }
        }
        string materialsFolderPath = combinedFolderPath + "/Materials";
        PlySplatImporter.EnsureFolderExists(materialsFolderPath);
        for (int i = 0; i < generatedMaterials.Count; i++)
        {
            Material savedMaterial = PlySplatImporter.CreateOrReplaceAsset(generatedMaterials[i], materialsFolderPath + "/" + generatedMaterials[i].name + ".mat");
            if (savedMaterial != null && savedMaterial.renderQueue != generatedRenderQueues[i])
            {
                savedMaterial.renderQueue = generatedRenderQueues[i];
                EditorUtility.SetDirty(savedMaterial);
            }
            generatedMaterials[i] = savedMaterial;
        }
        Material[] combinedMaterials = generatedMaterials.ToArray();
        bool rendererRootChanged = EnsureCombinedRendererRoot(combinedMaterials);
        chunkHierarchyChanged |= EnsureOwnerChunkHierarchy(owner);
        if (combinedPositions != previousCombinedPositions ||
            combinedRotations != previousCombinedRotations ||
            combinedScales != previousCombinedScales ||
            combinedColors != previousCombinedColors ||
            combinedColorsCamera != previousCombinedColorsCamera ||
            lodChunkSelection != previousLodChunkSelection ||
            lodAlphaState != previousLodAlphaState ||
            lodAlphaStateScratch != previousLodAlphaStateScratch ||
            combineDataMaterial != previousCombineDataMaterial ||
            combinedSortedRenderer != previousCombinedSortedRenderer ||
            builtCombinedElementCount != combinedElementCount ||
            rendererRootChanged ||
            chunkHierarchyChanged ||
            rendererVisibilityChanged ||
            hierarchyStateChanged)
        {
            builtCombinedElementCount = combinedElementCount;
            EditorUtility.SetDirty(this);
        }
    }
}

}
#endif
