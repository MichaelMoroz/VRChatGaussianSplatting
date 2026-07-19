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
        combinedPositionsFormat = source.combinedPositionsFormat;
        combinedRotationsFormat = source.combinedRotationsFormat;
        combinedScalesFormat = source.combinedScalesFormat;
        combinedColorsFormat = source.combinedColorsFormat;
        combinedColorsCameraFormat = source.combinedColorsCameraFormat;
        combinedTextureFormatsInitialized = true;
        combinedPositionsByBucket = source.combinedPositionsByBucket;
        combinedRotationsByBucket = source.combinedRotationsByBucket;
        combinedScalesByBucket = source.combinedScalesByBucket;
        combinedColorsByBucket = source.combinedColorsByBucket;
        combinedColorsCameraByBucket = source.combinedColorsCameraByBucket;
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

    bool HasOwner()
    {
        return ResolveOwner() != null;
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

    bool CombinedMaterialQueuesMatch(GaussianSplatImporter.PassInfo[] passInfos, bool useSrgb)
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
        GaussianSplatObject[] lodObjects = UnityEngine.Object.FindObjectsOfType<GaussianSplatObject>(true);
        for (int i = 0; i < lodObjects.Length; i++)
        {
            GaussianSplatObject lodObject = lodObjects[i];
            if (lodObject != null && lodObject.gameObject.scene == gameObject.scene)
            {
                maxChunks = Mathf.Max(maxChunks, lodObject.GetChunkCount());
            }
        }
        return maxChunks;
    }

    // Deterministic hash of an asset's GUID (stable across domain reloads, unlike GetInstanceID).
    // SH band number from stored coeff count (3 -> SH1, 8 -> SH2, 15 -> SH3), matching the importer.
    static float SHBandFromCoeffCount(int coeffCount)
    {
        if (coeffCount >= 15) return 3.0f;
        if (coeffCount >= 8) return 2.0f;
        if (coeffCount >= 3) return 1.0f;
        return 0.0f;
    }

    static int StableAssetHash(Texture2D t)
    {
        if (t == null) return 0;
        string g = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t));
        if (string.IsNullOrEmpty(g)) g = t.name;
        int h = 17;
        for (int i = 0; i < g.Length; i++) { unchecked { h = h * 31 + g[i]; } }
        return h;
    }

    static int StableTextureContentHash(Texture2D t)
    {
        if (t == null) return 0;
        int h = StableAssetHash(t);
        unchecked
        {
            h = h * 31 + t.width;
            h = h * 31 + t.height;
        }
        string path = AssetDatabase.GetAssetPath(t);
        if (!string.IsNullOrEmpty(path))
        {
            string dep = AssetDatabase.GetAssetDependencyHash(path).ToString();
            for (int i = 0; i < dep.Length; i++) { unchecked { h = h * 31 + dep[i]; } }
        }
        return h;
    }

    // The fuse bake's "is this content already baked?" signature is persisted to EditorPrefs (keyed by
    // scene GUID + owner prefix) rather than ONLY the scene-serialized field. The scene-serialized
    // signature reverts to its last-saved value on domain reload if the scene was not saved, so a heavy
    // ~GB rebake would fire on every editor open of an unsaved scene. EditorPrefs survives reload AND an
    // unsaved scene, so combined with the reload-from-disk fast path the bake runs only on real change.
    string FuseSigKey(string kind, string prefix)
    {
        string sceneGuid = AssetDatabase.AssetPathToGUID(gameObject.scene.path);
        if (string.IsNullOrEmpty(sceneGuid)) sceneGuid = gameObject.scene.name;
        return "GSFuseSig_" + kind + "_" + sceneGuid + "_" + prefix;
    }
    int LoadFuseSig(string kind, string prefix) { return EditorPrefs.GetInt(FuseSigKey(kind, prefix), int.MinValue); }
    void SaveFuseSig(string kind, string prefix, int sig) { EditorPrefs.SetInt(FuseSigKey(kind, prefix), sig); }


    bool EnsureUnifiedLODMaterials(string folder, string prefix)
    {
        bool changed = false;
        string selectPath = folder + "/" + prefix + "_LODSelect.mat";
        Material selectMaterial = AssetDatabase.LoadAssetAtPath<Material>(selectPath);
        if (selectMaterial == null)
        {
            Shader s = Shader.Find("Hidden/GaussianSplatting/LODChunkSelect");
            if (s != null)
            {
                selectMaterial = new Material(s) { name = prefix + "_LODSelect" };
                selectMaterial = GaussianSplatImporter.CreateOrReplaceAsset(selectMaterial, selectPath);
            }
        }
        if (lodUnifiedSelectMaterial != selectMaterial)
        {
            lodUnifiedSelectMaterial = selectMaterial;
            changed = true;
        }

        string combinePath = folder + "/" + prefix + "_LODCombine.mat";
        Material combineMaterial = AssetDatabase.LoadAssetAtPath<Material>(combinePath);
        if (combineMaterial == null)
        {
            Shader s = Shader.Find("Hidden/GaussianSplatting/LODCombine");
            if (s != null)
            {
                combineMaterial = new Material(s) { name = prefix + "_LODCombine" };
                combineMaterial = GaussianSplatImporter.CreateOrReplaceAsset(combineMaterial, combinePath);
            }
        }
        if (lodUnifiedCombineMaterial != combineMaterial)
        {
            lodUnifiedCombineMaterial = combineMaterial;
            changed = true;
        }
        if (changed)
        {
            EditorUtility.SetDirty(this);
        }
        return changed;
    }

    static bool TextureAssetExists(string path)
    {
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null;
    }

    static bool HasAppendedChunkRangeStats(Texture2D range, int chunkCount)
    {
        if (range == null)
        {
            return false;
        }
        int width = Mathf.Max(1, range.width);
        int metaHeight = (Mathf.Max(1, chunkCount) + width - 1) / width;
        return range.height >= metaHeight * 2;
    }

    static bool FusedLODAssetsExist(string folder, string prefix, int totalChunks, bool requireRangeStats)
    {
        Texture2D range = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODGlobalRange.asset");
        bool required = TextureAssetExists(folder + "/" + prefix + "_LODFusedPositions.asset")
            && TextureAssetExists(folder + "/" + prefix + "_LODFusedColors.asset")
            && TextureAssetExists(folder + "/" + prefix + "_LODFusedRotations.asset")
            && TextureAssetExists(folder + "/" + prefix + "_LODFusedScales.asset")
            && TextureAssetExists(folder + "/" + prefix + "_LODGlobalBounds.asset")
            && range != null
            && TextureAssetExists(folder + "/" + prefix + "_LODFileBase.asset");
        if (!required)
        {
            return false;
        }
        return !requireRangeStats || HasAppendedChunkRangeStats(range, totalChunks);
    }

    // Reload an already-current unified LOD fused set from disk instead of re-baking. objs must be in the
    // same reload-stable order as the bake (transform-table row k <-> objs[k]); the caller guarantees this.
    bool TryReloadFusedLODFromDisk(string folder, string prefix, List<GameObject> objs, int totalChunks, bool requireRangeStats)
    {
        Texture2D pos = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedPositions.asset");
        Texture2D col = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedColors.asset");
        Texture2D rot = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedRotations.asset");
        Texture2D scl = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedScales.asset");
        Texture2D bounds = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODGlobalBounds.asset");
        Texture2D rng = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODGlobalRange.asset");
        Texture2D fbase = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFileBase.asset");
        if (pos == null || col == null || rot == null || scl == null || bounds == null || rng == null || fbase == null)
        {
            return false;
        }
        if (requireRangeStats && !HasAppendedChunkRangeStats(rng, totalChunks))
        {
            return false;
        }
        lodFusedPositions = pos; lodFusedColors = col; lodFusedRotations = rot; lodFusedScales = scl;
        lodGlobalBounds = bounds; lodGlobalRange = rng; lodFileBase = fbase;
        lodUnifiedSH = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedSH.asset");
        lodShParams = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODShParams.asset");
        int shBpr = Mathf.Max(1, lodUnifiedSH != null ? lodUnifiedSH.width >> 2 : 1);
        lodUnifiedShCoordShift = ComputeTextureCoordShift(shBpr);
        lodUnifiedShCoordMask = shBpr - 1;
        // Total baked into the (deduped) fused source = every file's splat count (fileBase row z channel).
        int totalSource = 0;
        if (fbase.isReadable)
        {
            Color[] fb = fbase.GetPixels();
            for (int i = 0; i < fb.Length; i++) totalSource += Mathf.RoundToInt(fb[i].b);
        }
        lodTotalSourceCount = totalSource;
        lodFusedObjects = objs.ToArray();
        lodFusedObjectCount = objs.Count;
        lodTotalChunks = totalChunks;
        lodMetaWidth = bounds.width;
        lodSelectionSide = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1, totalChunks))));
        int bpr = Mathf.Max(1, pos.width >> 2);
        lodFusedCoordShift = ComputeTextureCoordShift(bpr);
        lodFusedCoordMask = bpr - 1;
        EnsureUnifiedLODMaterials(folder, prefix);
        return lodUnifiedCombineMaterial != null;
    }

    // Editor debug splat counter: selected total (sum of the selection texture mip0) + current log2 alpha.
    // The readback is a synchronous ReadPixels (GPU->CPU flush); the scene view fires a burst of sorts on
    // focus regain, so it is throttled (the counter is cosmetic; reuse the last value between readbacks).
    // The counter is cosmetic, so reuse the last value between readbacks (a few per second is plenty).
    static double _lastUnifiedReadbackTime = -1.0;
    static int _lastUnifiedReadbackTotal;
    static float _lastUnifiedReadbackAlpha;
    const double UNIFIED_READBACK_MIN_INTERVAL = 0.2;

    int ReadbackUnifiedLODSelected(out float alpha, bool force = false)
    {
        double now = EditorApplication.timeSinceStartup;
        if (!force && _lastUnifiedReadbackTime >= 0.0 && (now - _lastUnifiedReadbackTime) < UNIFIED_READBACK_MIN_INTERVAL)
        {
            alpha = _lastUnifiedReadbackAlpha;
            return _lastUnifiedReadbackTotal;
        }
        _lastUnifiedReadbackTime = now;

        alpha = 0.0f;
        if (lodUnifiedSelection == null) return 0;
        RenderTexture prev = RenderTexture.active;
        int total = 0;
        Texture2D rd = new Texture2D(lodUnifiedSelection.width, lodUnifiedSelection.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            RenderTexture.active = lodUnifiedSelection;
            rd.ReadPixels(new Rect(0, 0, lodUnifiedSelection.width, lodUnifiedSelection.height), 0, 0, false);
            rd.Apply(false, false);
            Color[] px = rd.GetPixels();
            double sum = 0.0;
            for (int i = 0; i < px.Length; i++) { sum += px[i].r; }
            total = (int)System.Math.Round(sum);
        }
        finally
        {
            RenderTexture.active = prev;
            UnityEngine.Object.DestroyImmediate(rd);
        }
        if (lodAlphaState != null)
        {
            Texture2D a = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = lodAlphaState;
                a.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                a.Apply(false, false);
                alpha = a.GetPixel(0, 0).r;
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(a);
            }
        }
        _lastUnifiedReadbackTotal = total;
        _lastUnifiedReadbackAlpha = alpha;
        return total;
    }

    static bool FusedLODObjectRefsMatch(GameObject[] current, List<GameObject> expected)
    {
        int expectedCount = expected != null ? expected.Count : 0;
        if (current == null || current.Length != expectedCount)
        {
            return false;
        }
        for (int i = 0; i < expectedCount; i++)
        {
            if (current[i] != expected[i])
            {
                return false;
            }
        }
        return true;
    }

    bool EnsureFusedLODObjectRefs(List<GameObject> expected)
    {
        int count = expected != null ? expected.Count : 0;
        if (FusedLODObjectRefsMatch(lodFusedObjects, expected))
        {
            if (lodFusedObjectCount != count)
            {
                lodFusedObjectCount = count;
                EditorUtility.SetDirty(this);
                return true;
            }
            return false;
        }
        lodFusedObjects = count > 0 ? expected.ToArray() : new GameObject[0];
        lodFusedObjectCount = count;
        EditorUtility.SetDirty(this);
        return true;
    }

    const double FUSED_BAKE_DEBOUNCE_SECONDS = 2.0;
    static readonly List<GaussianSplatCombiner> _queuedFusedBakes = new List<GaussianSplatCombiner>();
    static readonly Dictionary<GaussianSplatCombiner, double> _queuedFusedBakeTimes = new Dictionary<GaussianSplatCombiner, double>();
    // Canonical "rebake wanted" set + its target signature. Editor-only and static so queueing never touches the
    // serialized surface (a serialized queue flag re-dirtied the scene on save). Survives the ProcessQueuedFusedBakes
    // dequeue from _queuedFusedBakes (which happens before the bake runs); cleared only on commit/clear.
    static readonly Dictionary<GaussianSplatCombiner, int> _queuedFusedSignatures = new Dictionary<GaussianSplatCombiner, int>();
    static bool _processingQueuedFusedBake;
    static GaussianSplatCombiner _activeFusedBakeCombiner;
    static GaussianSplatFuse.FuseLODJob _activeFusedBakeJob;
    static FusedBakeCommit _activeFusedBakeCommit;

    sealed class FusedBakeCommit
    {
        public int signature;
        public int sourceSignature;
        public List<GameObject> combinedObjects;
        public string folder;
        public string prefix;
    }

    [InitializeOnLoadMethod]
    static void RegisterFusedBakeQueue()
    {
        EditorApplication.update -= ProcessQueuedFusedBakes;
        EditorApplication.update += ProcessQueuedFusedBakes;
    }

    static void ProcessQueuedFusedBakes()
    {
        if (_processingQueuedFusedBake || Application.isPlaying || EditorApplication.isCompiling)
        {
            return;
        }

        if (_activeFusedBakeJob != null)
        {
            _processingQueuedFusedBake = true;
            try
            {
                EditorUtility.DisplayProgressBar("Gaussian Splat Fusion", $"Fusing splat textures… ({_activeFusedBakeJob.StageName})", _activeFusedBakeJob.Progress);
                if (_activeFusedBakeJob.Step())
                {
                    if (!_activeFusedBakeJob.Failed && _activeFusedBakeCombiner != null)
                    {
                        GaussianSplatFuse.FuseLODResult r = _activeFusedBakeJob.Result;
                        _activeFusedBakeCombiner.CommitFusedLODResult(r, _activeFusedBakeCommit);
                        Debug.Log($"[GaussianSplatFuse] Fused splat textures updated: {r.totalSourceCount:N0} splats, {r.totalChunkCount:N0} chunks.", _activeFusedBakeCombiner);
                    }
                    _activeFusedBakeJob = null;
                    _activeFusedBakeCombiner = null;
                    _activeFusedBakeCommit = null;
                    EditorUtility.ClearProgressBar();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, _activeFusedBakeCombiner);
                _activeFusedBakeJob = null;
                _activeFusedBakeCombiner = null;
                _activeFusedBakeCommit = null;
                EditorUtility.ClearProgressBar();
            }
            finally
            {
                _processingQueuedFusedBake = false;
            }
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        for (int i = _queuedFusedBakes.Count - 1; i >= 0; i--)
        {
            GaussianSplatCombiner combiner = _queuedFusedBakes[i];
            if (combiner == null || !combiner.gameObject.scene.IsValid())
            {
                _queuedFusedBakes.RemoveAt(i);
                if (combiner != null) _queuedFusedBakeTimes.Remove(combiner);
                continue;
            }
            if (_queuedFusedBakeTimes.TryGetValue(combiner, out double dueTime) && now < dueTime)
            {
                continue;
            }

            _queuedFusedBakes.RemoveAt(i);
            _queuedFusedBakeTimes.Remove(combiner);
            _processingQueuedFusedBake = true;
            try
            {
                combiner.RunQueuedFusedLODBake();
            }
            catch (Exception e)
            {
                Debug.LogException(e, combiner);
            }
            finally
            {
                _processingQueuedFusedBake = false;
            }
            return;
        }
    }

    // One row of the inspector's per-fused-object debug table.
    public struct FusedObjectDebugRow
    {
        public string name;
        public bool active;
        public int splats;
        public int files;
        public int chunks;
        public int shCoeff;   // SH coefficients the source carries (0 = none)
        public long shTexels; // SH texels this object asks of the fused SH texture
        public bool shDropped;
    }

    public List<FusedObjectDebugRow> GetFusedObjectDebugRows()
    {
        var rows = new List<FusedObjectDebugRow>();
        int n = lodObjSplatCount != null ? lodObjSplatCount.Length : 0;
        for (int i = 0; i < n; i++)
        {
            GameObject go = lodFusedObjects != null && i < lodFusedObjects.Length ? lodFusedObjects[i] : null;
            int coeff = lodObjShCoeff != null && i < lodObjShCoeff.Length ? lodObjShCoeff[i] : 0;
            int splats = lodObjSplatCount[i];
            rows.Add(new FusedObjectDebugRow
            {
                name = go != null ? go.name : ("object " + i),
                active = go != null && go.activeInHierarchy,
                splats = splats,
                files = lodObjFileCount != null && i < lodObjFileCount.Length ? lodObjFileCount[i] : 0,
                chunks = lodObjChunkCount != null && i < lodObjChunkCount.Length ? lodObjChunkCount[i] : 0,
                shCoeff = coeff,
                shTexels = (long)coeff * splats,
                shDropped = lodObjShDropped != null && i < lodObjShDropped.Length && lodObjShDropped[i],
            });
        }
        return rows;
    }

    void QueueFusedLODBake(int signature)
    {
        bool wasQueued = _queuedFusedSignatures.TryGetValue(this, out int queuedSig);
        if (wasQueued && queuedSig == signature && _queuedFusedBakes.Contains(this))
        {
            return;
        }
        _queuedFusedSignatures[this] = signature;
        if (!_queuedFusedBakes.Contains(this))
        {
            _queuedFusedBakes.Add(this);
        }
        _queuedFusedBakeTimes[this] = EditorApplication.timeSinceStartup + FUSED_BAKE_DEBOUNCE_SECONDS;
        // Warn once when the fused set first goes stale (not on every edit) so it's clear a rebake is pending.
        if (!wasQueued)
        {
            Debug.LogWarning($"[GaussianSplatFuse] Fused splat textures are out of date in scene '{gameObject.scene.name}'; rebaking ~{FUSED_BAKE_DEBOUNCE_SECONDS:0}s after edits settle.", this);
        }
    }

    void ClearQueuedFusedLODBake()
    {
        _queuedFusedSignatures.Remove(this);
        _queuedFusedBakes.Remove(this);
        _queuedFusedBakeTimes.Remove(this);
    }

    void RunQueuedFusedLODBake()
    {
        if (!_queuedFusedSignatures.ContainsKey(this))
        {
            return;
        }
        GaussianSplatRenderer owner = ResolveOwner();
        string ownerName = owner != null ? owner.name : name;
        BuildFusedLOD(ownerName, true);
    }

    void StartFusedLODJob(List<GaussianSplatFuse.FuseLODSource> sources, string folder, string prefix, GaussianSplatFuse.FuseLODResult reuseFrom, int signature, int sourceSignature, List<GameObject> combinedObjects)
    {
        if (_activeFusedBakeJob != null)
        {
            QueueFusedLODBake(signature);
            return;
        }
        GaussianSplatImporter.EnsureFolderExists(folder);
        _activeFusedBakeCombiner = this;
        _activeFusedBakeCommit = new FusedBakeCommit
        {
            signature = signature,
            sourceSignature = sourceSignature,
            combinedObjects = combinedObjects,
            folder = folder,
            prefix = prefix
        };
        _activeFusedBakeJob = GaussianSplatFuse.CreateFuseLODJob(sources, folder, prefix, reuseFrom);
        if (_activeFusedBakeJob == null || _activeFusedBakeJob.Result == null)
        {
            _activeFusedBakeJob = null;
            _activeFusedBakeCombiner = null;
            _activeFusedBakeCommit = null;
            ClearQueuedFusedLODBake();
            return;
        }
        GaussianSplatFuse.FuseLODResult r = _activeFusedBakeJob.Result;
        Debug.Log($"[GaussianSplatFuse] Baking fused splat textures: {r.totalSourceCount:N0} splats, {r.totalChunkCount:N0} chunks, {r.objectCount} object(s).", this);
    }

    void CommitFusedLODResult(GaussianSplatFuse.FuseLODResult res, FusedBakeCommit commit)
    {
        if (res == null || commit == null)
        {
            ClearQueuedFusedLODBake();
            return;
        }
        lodFusedPositions = res.fusedPositions;
        lodFusedColors = res.fusedColors;
        lodFusedRotations = res.fusedRotations;
        lodFusedScales = res.fusedScales;
        lodGlobalBounds = res.globalBounds;
        lodGlobalRange = res.globalRange;
        lodFileBase = res.fileBaseTable;
        lodUnifiedSH = res.fusedSH;
        lodShParams = res.shParams;
        lodUnifiedShCoordShift = res.fusedShCoordShift;
        lodUnifiedShCoordMask = res.fusedShCoordMask;
        lodTotalSourceCount = res.totalSourceCount;
        lodShDroppedObjects = res.shDroppedObjects;
        lodObjSplatCount = res.objSplatCount;
        lodObjFileCount = res.objFileCount;
        lodObjChunkCount = res.objChunkCount;
        lodObjShCoeff = res.objShCoeff;
        lodObjShDropped = res.objShDropped;
        lodFusedObjects = commit.combinedObjects != null ? commit.combinedObjects.ToArray() : new GameObject[0];
        lodFusedObjectCount = lodFusedObjects.Length;
        lodTotalChunks = res.totalChunkCount;
        lodMetaWidth = res.metaWidth;
        lodFusedSignature = commit.signature;
        lodFusedSourceSignature = commit.sourceSignature;
        lodSelectionSide = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1, res.totalChunkCount))));
        int bpr = Mathf.Max(1, res.fusedPositions != null ? res.fusedPositions.width >> 2 : 1);
        lodFusedCoordShift = ComputeTextureCoordShift(bpr);
        lodFusedCoordMask = bpr - 1;
        EnsureUnifiedLODMaterials(commit.folder, commit.prefix);
        SaveFuseSig("LOD", commit.prefix, commit.signature);
        SaveFuseSig("LODsrc", commit.prefix, commit.sourceSignature);
        ClearQueuedFusedLODBake();
        EditorUtility.SetDirty(this);
        GaussianSplatRenderer.RequestEditorRefresh();
    }

    // Bakes all packed scene splats (non-LOD then LOD) into the unified fused set + chunk metadata via
    // GaussianSplatFuse.CreateFuseLODJob, records the ordered object list for the runtime transform writer, and
    // ensures the select/combine materials. The POT-square selection RT is created in UpdateResources.
    void BuildFusedLOD(string ownerName, bool allowHeavyBake)
    {
        // Gather packed, renderable LOD objects, then sort by a RELOAD-STABLE key (first position
        // texture's GUID hash) so the fused concatenation order is reproducible across domain reloads.
        // The per-object transform-table row k maps to objs[k] at runtime; an InstanceID sort is NOT
        // stable across reloads and would break the reload-from-disk fast path.
        int sig = 20; // cheap content signature: object set + source textures + chunk/splat counts.
                      // Bump the seed on fused-metadata FORMAT changes so old bakes regenerate once instead of
                      // being reused via the fast path.

        // Source signature: hashes ONLY what the heavy fused source textures depend on - each UNIQUE source once
        // (instances dedup). Stable when a duplicate is added/removed, so the ~GB GPU source concat is skipped and
        // only the metadata rebuilds. (sig, per placement, remains the LAYOUT signature gating the fast-path reload.)
        int sourceSig = 17;
        var sourceSigSeen = new HashSet<Texture2D>();

        var valid = new List<GaussianSplatObject>();
        GaussianSplatObject[] all = UnityEngine.Object.FindObjectsOfType<GaussianSplatObject>(true);
        for (int i = 0; i < all.Length; i++)
        {
            GaussianSplatObject lo = all[i];
            if (lo == null || lo.gameObject.scene != gameObject.scene || !lo.IsRenderable() || !lo.usePackedPositions)
            {
                continue; // only packed, renderable LOD objects in this scene participate
            }
            valid.Add(lo);
        }
        valid.Sort((a, b) =>
        {
            int ha = StableAssetHash((a.positions != null && a.positions.Length > 0) ? a.positions[0] : null);
            int hb = StableAssetHash((b.positions != null && b.positions.Length > 0) ? b.positions[0] : null);
            if (ha != hb) return ha.CompareTo(hb);
            return string.CompareOrdinal(a.name, b.name);
        });

        var srcs = new List<GaussianSplatFuse.FuseLODSource>();
        var objs = new List<GameObject>();
        int totalChunks = 0;
        bool requireRangeStats = false;
        for (int i = 0; i < valid.Count; i++)
        {
            GaussianSplatObject lo = valid[i];
            int loShCoeff = (lo.sh != null && lo.sh.Length > 0) ? Mathf.Max(0, lo.GetFileSHCoeffCount(0)) : 0;
            requireRangeStats |= HasAppendedChunkRangeStats(lo.chunkRangeTexture, lo.GetChunkCount());
            srcs.Add(new GaussianSplatFuse.FuseLODSource
            {
                positions = lo.positions, colors = lo.colors, rotations = lo.rotations, scales = lo.scales,
                fileSplatCounts = lo.fileSplatCounts,
                chunkBoundsMin = lo.chunkBoundsMinTexture, chunkBoundsMax = lo.chunkBoundsMaxTexture, chunkRange = lo.chunkRangeTexture,
                chunkCount = lo.GetChunkCount(), chunkSize = lo.chunkSize, packed = true,
                sh = lo.sh, shCoeffCount = loShCoeff, shMin = lo.GetFileSHMin(0), shRange = lo.GetFileSHRange(0),
                shBand = SHBandFromCoeffCount(loShCoeff)
            });
            objs.Add(lo.gameObject);
            totalChunks += Mathf.Max(0, lo.GetChunkCount());
            // Content-hash based (asset dependency hash): detects a source/SH reimport, yet stays stable across
            // domain reloads (the dependency hash only changes on an actual reimport). Commutative across objects.
            Texture2D firstTex = (lo.positions != null && lo.positions.Length > 0) ? lo.positions[0] : null;
            Texture2D shTex = lo.GetSH(0) as Texture2D;
            unchecked { sig += StableTextureContentHash(firstTex) * 31 + StableTextureContentHash(lo.chunkRangeTexture) * 17 + StableTextureContentHash(shTex) * 23 + lo.GetFileCount() * 7 + lo.GetChunkCount() * 13 + lo.totalSplatCount + loShCoeff * 101; }
            // Per UNIQUE source only (dedup by positions[0], matching FuseLOD's sharedSources key).
            if (firstTex == null || sourceSigSeen.Add(firstTex))
            {
                unchecked { sourceSig += StableTextureContentHash(firstTex) * 31 + StableTextureContentHash(shTex) * 23 + lo.totalSplatCount + loShCoeff * 101 + lo.GetFileCount() * 7; }
            }
        }
        if (srcs.Count == 0)
        {
            ClearQueuedFusedLODBake();
            EnsureFusedLODObjectRefs(null);
            lodTotalChunks = 0;
            lodSelectionSide = 0;
            lodTotalSourceCount = 0;
            lodFusedSignature = sig;
            lodFusedSourceSignature = sourceSig;
            return;
        }

        // Combined object list matches the bake's objId order.
        var combinedObjs = new List<GameObject>(objs);
        EnsureFusedLODObjectRefs(combinedObjs);

        string folder = GaussianSplatImporter.GetSceneTempResourceFolderPath(gameObject.scene, "RTs") + "/FusedLOD";
        string prefix = GaussianSplatImporter.SanitizeAssetName(ownerName);
        EnsureUnifiedLODMaterials(folder, prefix);

        // Fast path A: already current in memory (steady-state refresh / saved scene after reload).
        if (sig == lodFusedSignature && lodFusedPositions != null
            && FusedLODObjectRefsMatch(lodFusedObjects, combinedObjs) && lodUnifiedCombineMaterial != null
            && (lodSelectionSide > 0 || totalChunks == 0)
            && FusedLODAssetsExist(folder, prefix, totalChunks, requireRangeStats))
        {
            ClearQueuedFusedLODBake();
            return;
        }

        // Fast path B: the on-disk baked assets are already current for this content (EditorPrefs signature
        // survives reload + unsaved scene). Reload them instead of re-running the heavy ~GB fuse + rewrite.
        if (sig == LoadFuseSig("LOD", prefix) && TryReloadFusedLODFromDisk(folder, prefix, combinedObjs, totalChunks, requireRangeStats))
        {
            lodFusedSignature = sig;
            lodFusedSourceSignature = sourceSig;
            ClearQueuedFusedLODBake();
            return;
        }

        if (!allowHeavyBake)
        {
            QueueFusedLODBake(sig);
            return;
        }

        GaussianSplatImporter.EnsureFolderExists(folder);

        // Source-reuse path: only the layout changed (a duplicate added/removed, transforms, chunk metadata) but
        // the UNIQUE source set is identical, so the heavy GPU source concat output is byte-identical. Reuse the
        // cached fused source textures (in-memory if the source sig still matches, else reloaded from disk) and
        // let FuseLOD rebuild only the small per-instance metadata - skipping the ~GB gather + readback.
        GaussianSplatFuse.FuseLODResult reuseFrom = null;
        bool sourceCached = sourceSig == lodFusedSourceSignature && lodFusedPositions != null
            && lodFusedColors != null && lodFusedRotations != null && lodFusedScales != null;
        if (!sourceCached && sourceSig == LoadFuseSig("LODsrc", prefix))
        {
            Texture2D pos = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedPositions.asset");
            Texture2D col = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedColors.asset");
            Texture2D rot = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedRotations.asset");
            Texture2D scl = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedScales.asset");
            if (pos != null && col != null && rot != null && scl != null)
            {
                lodFusedPositions = pos; lodFusedColors = col; lodFusedRotations = rot; lodFusedScales = scl;
                lodUnifiedSH = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + prefix + "_LODFusedSH.asset");
                sourceCached = true;
            }
        }
        if (sourceCached)
        {
            reuseFrom = new GaussianSplatFuse.FuseLODResult
            {
                fusedPositions = lodFusedPositions, fusedColors = lodFusedColors,
                fusedRotations = lodFusedRotations, fusedScales = lodFusedScales, fusedSH = lodUnifiedSH,
            };
        }

        StartFusedLODJob(srcs, folder, prefix, reuseFrom, sig, sourceSig, combinedObjs);
    }

    static bool EnsureBucketArray(ref RenderTexture[] textures)
    {
        if (textures != null && textures.Length == GaussianSplatRenderer.COMBINED_BUCKET_TIER_COUNT)
        {
            return false;
        }
        RenderTexture[] resized = new RenderTexture[GaussianSplatRenderer.COMBINED_BUCKET_TIER_COUNT];
        for (int i = 0; textures != null && i < Mathf.Min(textures.Length, resized.Length); i++)
        {
            resized[i] = textures[i];
        }
        textures = resized;
        return true;
    }

    static bool AssignBucketTexture(ref RenderTexture target, RenderTexture source)
    {
        if (target == source)
        {
            return false;
        }
        target = source;
        return true;
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
        BuildFusedLOD(ownerName, false); // queue heavy fused bake automatically; do not block editor refresh
        string combinedFolderPath = GaussianSplatImporter.GetSceneTempResourceFolderPath(gameObject.scene, "RTs") + "/Combined";
        string assetPrefix = GaussianSplatImporter.SanitizeAssetName(ownerName);
        MeshRenderer previousCombinedSortedRenderer = combinedSortedRenderer;
        bool resourcesChanged = false;
        // Pool-backed combined textures: tier i == RT bucket i (256K/1M/4M/16M). Assign the shared pool sets
        // into every tier slot; the baseline fields default to the largest bucket the scene can reach (the
        // runtime swap overrides them per frame). Per-scene combined RTs are no longer allocated.
        int maxBucket = GaussianSplatRTPool.BucketIndexForCount(combinedElementCount);
        if (maxBucket < 0) maxBucket = GaussianSplatRenderer.COMBINED_BUCKET_TIER_COUNT - 1;
        resourcesChanged |= EnsureBucketArray(ref combinedPositionsByBucket);
        resourcesChanged |= EnsureBucketArray(ref combinedRotationsByBucket);
        resourcesChanged |= EnsureBucketArray(ref combinedScalesByBucket);
        resourcesChanged |= EnsureBucketArray(ref combinedColorsByBucket);
        resourcesChanged |= EnsureBucketArray(ref combinedColorsCameraByBucket);
        for (int b = 0; b < GaussianSplatRenderer.COMBINED_BUCKET_TIER_COUNT; b++)
        {
            GaussianSplatRTPool.BucketSet set = GaussianSplatRTPool.LoadBucket(b);
            resourcesChanged |= AssignBucketTexture(ref combinedPositionsByBucket[b], set.combinedPositions);
            resourcesChanged |= AssignBucketTexture(ref combinedRotationsByBucket[b], set.combinedRotations);
            resourcesChanged |= AssignBucketTexture(ref combinedScalesByBucket[b], set.combinedScales);
            resourcesChanged |= AssignBucketTexture(ref combinedColorsByBucket[b], set.combinedColors);
            resourcesChanged |= AssignBucketTexture(ref combinedColorsCameraByBucket[b], set.combinedColorsCamera);
        }
        GaussianSplatRTPool.BucketSet baseSet = GaussianSplatRTPool.LoadBucket(maxBucket);
        resourcesChanged |= GaussianSplatImporter.EnsureSortRenderTexture(ref lodAlphaState, combinedFolderPath, assetPrefix + "_LODAlphaState", 1, 1, RenderTextureFormat.ARGBFloat, false, 1);
        resourcesChanged |= GaussianSplatImporter.EnsureSortRenderTexture(ref lodAlphaStateScratch, combinedFolderPath, assetPrefix + "_LODAlphaStateScratch", 1, 1, RenderTextureFormat.ARGBFloat, false, 1);
        // Unified LOD selection: POT-square (mip-chained) 2D pyramid over all baked LOD chunks.
        if (lodSelectionSide > 0)
        {
            resourcesChanged |= GaussianSplatImporter.EnsureSortRenderTexture(ref lodUnifiedSelection, combinedFolderPath, assetPrefix + "_LODUnifiedSelection", lodSelectionSide, lodSelectionSide, RenderTextureFormat.ARGBFloat, true, 1);
        }
        bool useSrgb = true;
        // Geometric pass ladder (512K, 512K, 1M, 2M, 4M, 8M) covering the max reachable count, with shared
        // per-pass meshes; the runtime enables the minimal prefix that covers the live rendered count.
        GaussianSplatImporter.PassInfo[] passInfos = GaussianSplatRTPool.CreateGeometricPassLayout(combinedElementCount);
        bool hasOwner = HasOwner();
        bool hierarchyStateChanged = EnsureGeneratedHierarchyState(false);
        bool chunkHierarchyChanged = EnsureOwnerChunkHierarchy(owner);
        bool sortedRendererSatisfied = combinedSortedRenderer != null && (hasOwner || !combinedSortedRenderer.gameObject.activeSelf);
        if (!resourcesChanged
            && builtCombinedElementCount == combinedElementCount
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
        // Unified combine/select materials are created in BuildFusedLOD; no legacy CombineData/LODChunkSelect/
        // LODCombineData materials needed anymore.
        List<Material> generatedMaterials = new List<Material>();
        List<int> generatedRenderQueues = new List<int>();
        int renderQueue = GetEffectiveStartRenderQueue();
        if (useSrgb)
        {
            Material toSrgb = GaussianSplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/ToSRGB", assetPrefix + "_CombinedToSRGB");
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
            GaussianSplatImporter.PassInfo passInfo = passInfos[passIndex];
            string materialName = assetPrefix + (passInfo.PassIndex > 0 ? "_CombinedPass" + passInfo.PassIndex : "_CombinedMain") + "_Splat";
            Material splatMaterial = passInfo.PassIndex == 0
                ? GaussianSplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/GaussianSplatting", materialName)
                : (mainMaterial != null ? new Material(mainMaterial) : GaussianSplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/GaussianSplatting", materialName));
            if (splatMaterial == null)
            {
                continue;
            }
            splatMaterial.name = materialName;
            if (passInfo.PassIndex == 0)
            {
                mainMaterial = splatMaterial;
            }
            GaussianSplatImporter.ConfigureSplatMaterial(
                splatMaterial,
                baseSet.combinedPositions,
                baseSet.combinedColors,
                baseSet.combinedRotations,
                baseSet.combinedScales,
                null,
                0,
                combinedElementCount,
                Vector4.zero,
                Vector4.one,
                combinedElementCount,
                0.0f,
                baseSet.combinedColorsCamera,
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
                Material alphaMask = GaussianSplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/AlphaDepthMask", materialName + "_AlphaDepthMask");
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
            Material toLinear = GaussianSplatImporter.CreateMaterialFromTemplate(null, "VRChatGaussianSplatting/ToLinear", assetPrefix + "_CombinedToLinear");
            if (toLinear != null)
            {
                toLinear.renderQueue = renderQueue++;
                generatedMaterials.Add(toLinear);
                generatedRenderQueues.Add(toLinear.renderQueue);
            }
        }
        string materialsFolderPath = combinedFolderPath + "/Materials";
        GaussianSplatImporter.EnsureFolderExists(materialsFolderPath);
        for (int i = 0; i < generatedMaterials.Count; i++)
        {
            Material savedMaterial = GaussianSplatImporter.CreateOrReplaceAsset(generatedMaterials[i], materialsFolderPath + "/" + generatedMaterials[i].name + ".mat");
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
        if (resourcesChanged ||
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
