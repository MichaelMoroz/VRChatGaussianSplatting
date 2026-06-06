#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UdonSharpEditor;

namespace GaussianSplatting
{

// Editor-only scene management, hierarchy bookkeeping, and sorting-resource generation for
// GaussianSplatRenderer. Kept in a partial file so the runtime behaviour stays small; the whole
// file is excluded from Udon compilation via the preprocessor guard above.
public static class GSEditorText
{
    static readonly System.Reflection.PropertyInfo EditorLanguageProperty = typeof(UnityEditor.Editor).Assembly
        .GetType("UnityEditor.LocalizationDatabase")
        ?.GetProperty("currentEditorLanguage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    public static string T(string english, string japanese)
    {
        return GetEditorLanguage() == SystemLanguage.Japanese ? japanese : english;
    }

    public static GUIContent C(string english, string japanese)
    {
        return new GUIContent(T(english, japanese));
    }

    static SystemLanguage GetEditorLanguage()
    {
        try
        {
            object language = EditorLanguageProperty != null ? EditorLanguageProperty.GetValue(null) : null;
            if (language is SystemLanguage systemLanguage)
            {
                return systemLanguage;
            }
        }
        catch
        {
        }
        return Application.systemLanguage;
    }
}

public partial class GaussianSplatRenderer
{
    static readonly HashSet<int> _singleModeMultiSplatWarnedScenes = new HashSet<int>();

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
        if (component == null)
        {
            return false;
        }
        GameObject root = component.transform.root != null ? component.transform.root.gameObject : component.gameObject;
        bool allowHiddenRenderer = component is GaussianSplatRenderer && root != null;
        if (!allowHiddenRenderer && (component.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
        {
            return false;
        }
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
        int activeCount = 0;
        if (!combinedMode)
        {
            for (int i = 0; i < splats.Length; i++)
            {
                GaussianSplatObject splat = splats[i];
                if (splat != null && splat.gameObject.activeInHierarchy)
                {
                    if (visibleIndex < 0)
                    {
                        visibleIndex = i;
                        splat.ShowSorted();
                    }
                    activeCount++;
                }
            }
        }
        if (!combinedMode && activeCount > 1)
        {
            if (_singleModeMultiSplatWarnedScenes.Add(scene.handle))
            {
                Debug.LogWarning(GSEditorText.T(
                    $"Multiple Gaussian splats are active in {scene.path}, but Rendering Mode is Single Splat. Only one splat will be rendered. Enable Combined rendering to render multiple active splats.",
                    $"{scene.path} で複数の Gaussian Splat が有効ですが、表示モードは単体です。描画されるのは 1 つだけです。複数を描画するには統合表示を有効にしてください。"));
            }
        }
        else
        {
            _singleModeMultiSplatWarnedScenes.Remove(scene.handle);
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
            if (!ShouldUseEditorScene(scene) || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene))
            {
                continue;
            }
            GaussianSplatRenderer renderer = GetPrimarySceneRenderer(scene);
            if (renderer == null)
            {
                if (FindSceneObjects<GaussianSplatObject>(scene).Length == 0)
                {
                    ApplyEditorVisibility(scene, false);
                    continue;
                }
                renderer = EnsureSceneRendererExists(scene);
            }
            renderer.RefreshEditorResourcesAndVisibility();
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

    public static GaussianSplatRenderer FindExistingSceneRenderer(Scene scene)
    {
        return GetPrimarySceneRenderer(scene);
    }

    public static GaussianSplatRenderer EnsureSceneRendererExists(Scene scene)
    {
        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer(scene);
        if (primaryRenderer == null)
        {
            bool isPreviewScene = UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene);
            GameObject rendererObject = new GameObject("GaussianSplatRenderer");
            rendererObject.hideFlags = isPreviewScene ? HideFlags.HideAndDontSave : HideFlags.None;
            if (!isPreviewScene)
            {
                Undo.RegisterCreatedObjectUndo(rendererObject, "Create Gaussian Splat Renderer");
            }
            if (scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(rendererObject, scene);
            }
            primaryRenderer = rendererObject.AddUdonSharpComponent<GaussianSplatRenderer>();
            RadixSort radixSort = rendererObject.AddUdonSharpComponent<RadixSort>();
            radixSort.computeKeyValues = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ComputeKeyValue.mat");
            radixSort.radixSort = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/RadixSort/Materials/Misha_RadixSort.mat");
            radixSort.copySortedOrder = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_CopyRenderOrder.mat");
            radixSort.SetPipelinedPassesPerFrame(primaryRenderer.sortPassesPerFrame);
            EditorUtility.SetDirty(primaryRenderer);
            EditorUtility.SetDirty(radixSort);
        }
        else if (!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(primaryRenderer.gameObject.scene) && (primaryRenderer.gameObject.hideFlags != HideFlags.None || primaryRenderer.hideFlags != HideFlags.None))
        {
            primaryRenderer.gameObject.hideFlags = HideFlags.None;
            primaryRenderer.hideFlags = HideFlags.None;
            EditorUtility.SetDirty(primaryRenderer.gameObject);
            EditorUtility.SetDirty(primaryRenderer);
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
        primaryRenderer.RefreshEditorResourcesAndVisibility();
        return primaryRenderer;
    }

    public void RefreshEditorResourcesAndVisibility()
    {
        if (EditorUtility.IsPersistent(this) || !ShouldUseEditorScene(gameObject.scene))
        {
            return;
        }
        if (RefreshCachedSceneSplatObjects())
        {
            EditorUtility.SetDirty(this);
        }
        EnsureRadixSortMaterials();
        UpdateSortingResourceTextures();
        GaussianSplatCombiner sceneCombiner = GetCombiner();
        if (sceneCombiner != null && sceneCombiner.EnsureGeneratedHierarchyState(!IsCombinedRenderingMode()))
        {
            EditorUtility.SetDirty(sceneCombiner);
        }
        if (IsCombinedRenderingMode())
        {
            Vector3 cameraPosition = GetEditorSortCameraPosition();
            SortCameraViews(cameraPosition, cameraPosition, false, true);
        }
        ApplyEditorVisibility(gameObject.scene, IsCombinedRenderingMode());
        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    static Vector3 GetEditorSortCameraPosition()
    {
        SceneView sceneView = SceneView.lastActiveSceneView;
        Camera camera = sceneView != null ? sceneView.camera : null;
        return camera != null ? camera.transform.position : Vector3.zero;
    }

    void OnValidate()
    {
        if (EditorUtility.IsPersistent(this) || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
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
            QueueEditorRefresh();
            return;
        }
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort != null && radixSort.SetPipelinedPassesPerFrame(sortPassesPerFrame))
        {
            EditorUtility.SetDirty(radixSort);
        }
        startRenderQueue = Mathf.Clamp(startRenderQueue, 2000, 5000);
        QueueEditorRefresh();
    }

    void EnsureRadixSortMaterials()
    {
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort == null)
        {
            return;
        }
        Material copyMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_CopyRenderOrder.mat");
        if (copyMaterial != null && radixSort.copySortedOrder != copyMaterial)
        {
            radixSort.copySortedOrder = copyMaterial;
            EditorUtility.SetDirty(radixSort);
        }
    }

    bool EnsureSortRenderTexture(ref RenderTexture targetTexture, string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
        {
            UnityEngine.Rendering.TextureDimension dimension = volumeDepth > 1 ? UnityEngine.Rendering.TextureDimension.Tex2DArray : UnityEngine.Rendering.TextureDimension.Tex2D;
            bool matches = targetTexture != null && targetTexture.width == width && targetTexture.height == height && targetTexture.format == format && targetTexture.volumeDepth == volumeDepth && targetTexture.useMipMap == useMipMap && targetTexture.dimension == dimension && !targetTexture.autoGenerateMips;
            if (matches)
            {
                if (!targetTexture.IsCreated()) targetTexture.Create();
                return false;
            }
            if (targetTexture != null) Object.DestroyImmediate(targetTexture);
            targetTexture = new RenderTexture(width, height, 0, format);
            targetTexture.name = assetName;
            targetTexture.hideFlags = HideFlags.HideAndDontSave;
            targetTexture.useMipMap = useMipMap;
            targetTexture.autoGenerateMips = false;
            targetTexture.dimension = dimension;
            targetTexture.volumeDepth = volumeDepth;
            targetTexture.Create();
            return true;
        }
        return PlySplatImporter.EnsureSortRenderTexture(ref targetTexture, folderPath, assetName, width, height, format, useMipMap, volumeDepth);
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
        resourcesChanged |= EnsureSortRenderTexture(ref splatRenderOrder, resourceFolderPath, assetPrefix + "_SplatRenderOrderScreen", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref splatRenderOrderPhoto, resourceFolderPath, assetPrefix + "_SplatRenderOrderPhoto", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 1);
        if (resourcesChanged)
        {
            EditorUtility.SetDirty(radixSort);
            EditorUtility.SetDirty(this);
        }
        ApplyEditorRenderQueueOverride();
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene) && !IsCombinedRenderingMode())
        {
            ResetRuntimeCache();
            return;
        }
        combiner = GaussianSplatCombiner.EnsureSceneCombiner(this);
        if (combiner == null)
        {
            return;
        }
        combiner.UpdateResources(safeCombinedCount, templateRenderer, primaryTemplate, alphaMaskTemplate, toSrgbTemplate, toLinearTemplate);
        if (!IsCombinedRenderingMode())
        {
            MeshRenderer combinedSortedRenderer = combiner.GetCombinedSortedRenderer();
            if (combinedSortedRenderer != null && combinedSortedRenderer.gameObject.activeSelf)
            {
                Undo.RecordObject(combinedSortedRenderer.gameObject, "Toggle Combined Gaussian Splat Renderer");
                combinedSortedRenderer.gameObject.SetActive(false);
                EditorUtility.SetDirty(combinedSortedRenderer.gameObject);
            }
        }
        ResetRuntimeCache();
    }

    public bool TryGetRenderQueueOverride(out int renderQueue)
    {
        renderQueue = Mathf.Clamp(startRenderQueue, 2000, 5000);
        return overrideRenderQueue;
    }

    void ApplyEditorRenderQueueOverride()
    {
        if (!overrideRenderQueue)
        {
            return;
        }
        int renderQueue = Mathf.Clamp(startRenderQueue, 2000, 5000);
        for (int i = 0; cachedSceneSplatObjects != null && i < cachedSceneSplatObjects.Length; i++)
        {
            GaussianSplatObject splat = cachedSceneSplatObjects[i] != null ? cachedSceneSplatObjects[i].GetComponent<GaussianSplatObject>() : null;
            MeshRenderer renderer = splat != null ? splat.GetSortedRenderer() : null;
            Material[] materials = renderer != null ? renderer.sharedMaterials : null;
            for (int materialIndex = 0; materials != null && materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null || material.renderQueue == renderQueue + materialIndex)
                {
                    continue;
                }
                material.renderQueue = renderQueue + materialIndex;
                EditorUtility.SetDirty(material);
            }
        }
    }
}

}
#endif
