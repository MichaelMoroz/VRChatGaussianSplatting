#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
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
    const int COMBINED_LOD_SETTINGS_VERSION_TARGET_SCALE_095 = 1;

    static readonly HashSet<int> _singleModeMultiSplatWarnedScenes = new HashSet<int>();
    static readonly HashSet<int> _singleModeLodWarnedScenes = new HashSet<int>();
    class EditorLodGridCache
    {
        public int minTextureId;
        public int maxTextureId;
        public int chunkCount;
        public Bounds[] chunkBounds;
    }
    class EditorLodGridRenderState
    {
        public GameObject gameObject;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;
        public Mesh mesh;
        public Material material;
    }

    static readonly Dictionary<GaussianSplatLODObject, EditorLodGridCache> _editorLodGridCaches = new Dictionary<GaussianSplatLODObject, EditorLodGridCache>();
    static readonly Dictionary<GaussianSplatRenderer, EditorLodGridRenderState> _editorLodGridRenderStates = new Dictionary<GaussianSplatRenderer, EditorLodGridRenderState>();
    struct EditorCameraSortFrame
    {
        public int frame;
        public Vector3 position;
    }

    static readonly Dictionary<int, EditorCameraSortFrame> _editorCameraSortFrame = new Dictionary<int, EditorCameraSortFrame>();
    static readonly Color[] _editorLodGridColors =
    {
        new Color(0.0f, 0.85f, 1.0f, 0.85f),
        new Color(0.1f, 1.0f, 0.35f, 0.85f),
        new Color(1.0f, 0.9f, 0.1f, 0.85f),
        new Color(1.0f, 0.45f, 0.05f, 0.85f),
        new Color(1.0f, 0.1f, 0.1f, 0.85f),
        new Color(0.8f, 0.2f, 1.0f, 0.85f),
        new Color(0.25f, 0.45f, 1.0f, 0.85f),
        new Color(1.0f, 1.0f, 1.0f, 0.85f),
    };

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
        filteredObjects.Sort(CompareSceneObjects);
        return filteredObjects.ToArray();
    }

    static string GetHierarchySortKey(Transform transform)
    {
        string key = string.Empty;
        while (transform != null)
        {
            key = transform.GetSiblingIndex().ToString("D6") + "/" + key;
            transform = transform.parent;
        }
        return key;
    }

    static int CompareSceneObjects(Component left, Component right)
    {
        if (left == right) return 0;
        if (left == null) return 1;
        if (right == null) return -1;
        int hierarchyCompare = string.CompareOrdinal(GetHierarchySortKey(left.transform), GetHierarchySortKey(right.transform));
        return hierarchyCompare != 0 ? hierarchyCompare : left.GetInstanceID().CompareTo(right.GetInstanceID());
    }

    static void QueueEditorRefresh()
    {
        _editorRefreshQueued = true;
        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    internal static void RequestEditorRefresh()
    {
        QueueEditorRefresh();
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

    static bool RemoveDuplicateSceneRenderers(Scene scene, GaussianSplatRenderer primaryRenderer)
    {
        if (primaryRenderer == null || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene))
        {
            return false;
        }

        bool removedAny = false;
        GaussianSplatRenderer[] renderers = FindSceneObjects<GaussianSplatRenderer>(scene);
        for (int i = 0; i < renderers.Length; i++)
        {
            GaussianSplatRenderer renderer = renderers[i];
            if (renderer == null || renderer == primaryRenderer || EditorUtility.IsPersistent(renderer))
            {
                continue;
            }

            GameObject rendererObject = renderer.gameObject;
            Component[] components = rendererObject.GetComponents<Component>();
            bool generatedRendererObject = rendererObject.name.StartsWith("GaussianSplatRenderer")
                && rendererObject.transform.childCount == 0
                && components.Length <= 3;
            if (generatedRendererObject)
            {
                Undo.DestroyObjectImmediate(rendererObject);
            }
            else
            {
                Undo.DestroyObjectImmediate(renderer);
            }
            removedAny = true;
        }
        return removedAny;
    }

    static void ApplyEditorVisibility(Scene scene, bool combinedMode)
    {
        GaussianSplatObject[] splats = FindSceneObjects<GaussianSplatObject>(scene);
        GaussianSplatLODObject[] lodObjects = FindSceneObjects<GaussianSplatLODObject>(scene);
        int visibleIndex = -1;
        int activeCount = 0;
        int activeLodCount = 0;
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
            for (int i = 0; i < lodObjects.Length; i++)
            {
                GaussianSplatLODObject lodObject = lodObjects[i];
                if (lodObject != null && lodObject.gameObject.activeInHierarchy && lodObject.IsRenderable())
                {
                    activeLodCount++;
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
        if (!combinedMode && activeLodCount > 0)
        {
            if (_singleModeLodWarnedScenes.Add(scene.handle))
            {
                Debug.LogWarning(GSEditorText.T(
                    $"Gaussian Splat LOD objects are active in {scene.path}, but Rendering Mode is Single Splat. LOD splats will not be rendered. Enable Combined rendering to render LOD splats.",
                    $"{scene.path} で Gaussian Splat LOD オブジェクトが有効ですが、表示モードは単体です。LOD スプラットは描画されません。LOD スプラットを描画するには統合表示を有効にしてください。"));
            }
        }
        else
        {
            _singleModeLodWarnedScenes.Remove(scene.handle);
        }
        for (int i = 0; i < splats.Length; i++)
        {
            GaussianSplatObject splat = splats[i];
            MeshRenderer renderer = splat != null ? splat.GetSortedRenderer() : null;
            bool enabled = !combinedMode && i == visibleIndex;
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
                if (FindSceneObjects<GaussianSplatObject>(scene).Length == 0 && FindSceneObjects<GaussianSplatLODObject>(scene).Length == 0)
                {
                    ApplyEditorVisibility(scene, false);
                    continue;
                }
                renderer = EnsureSceneRendererExists(scene);
            }
            else if (RemoveDuplicateSceneRenderers(scene, renderer))
            {
                EditorUtility.SetDirty(renderer);
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
        Vector3 cameraPosition = GetEditorCameraWorldPosition(camera);
        Vector3 cameraForward = GetEditorCameraWorldForward(camera);
        int cameraId = camera.GetInstanceID();
        int frame = Time.frameCount;
        if (_editorCameraSortFrame.TryGetValue(cameraId, out EditorCameraSortFrame lastSort)
            && lastSort.frame == frame
            && (lastSort.position - cameraPosition).sqrMagnitude < 0.000001f)
        {
            return;
        }
        _editorCameraSortFrame[cameraId] = new EditorCameraSortFrame { frame = frame, position = cameraPosition };
        GaussianSplatRenderer[] renderers = FindSceneObjects<GaussianSplatRenderer>(default(Scene));
        for (int i = 0; i < renderers.Length; i++)
        {
            GaussianSplatRenderer renderer = renderers[i];
            if (renderer != null && renderer.enabled)
            {
                if (!renderer.HasEditorSortRenderOrderTextures())
                {
                    renderer.RefreshEditorResourcesAndVisibility();
                    if (!renderer.HasEditorSortRenderOrderTextures())
                    {
                        continue;
                    }
                }
                renderer.SortCameraViews(cameraPosition, cameraForward, cameraPosition, false, true);
            }
        }
    }

    static Vector3 GetEditorCameraWorldPosition(Camera camera)
    {
        Matrix4x4 cameraToWorld = camera.cameraToWorldMatrix;
        Vector3 matrixPosition = new Vector3(cameraToWorld.m03, cameraToWorld.m13, cameraToWorld.m23);
        return IsFinite(matrixPosition) ? matrixPosition : camera.transform.position;
    }

    static Vector3 GetEditorCameraWorldForward(Camera camera)
    {
        Vector3 transformForward = camera.transform.forward;
        if (IsFinite(transformForward) && transformForward.sqrMagnitude > 0.000001f)
        {
            return transformForward.normalized;
        }

        Matrix4x4 cameraToWorld = camera.cameraToWorldMatrix;
        Vector3 matrixForward = new Vector3(-cameraToWorld.m02, -cameraToWorld.m12, -cameraToWorld.m22);
        return IsFinite(matrixForward) && matrixForward.sqrMagnitude > 0.000001f ? matrixForward.normalized : camera.transform.forward;
    }

    static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    bool HasEditorSortRenderOrderTextures()
    {
        return splatRenderOrder != null && splatRenderOrderPhoto != null;
    }

    void OnDrawGizmos()
    {
        if (EditorUtility.IsPersistent(this) || !ShouldUseEditorScene(gameObject.scene))
        {
            return;
        }

        if (!debugDrawLodGrid)
        {
            SetEditorLodGridRendererEnabled(false);
            return;
        }

        GaussianSplatCombiner sceneCombiner = GetCombiner();
        if (sceneCombiner == null)
        {
            SetEditorLodGridRendererEnabled(false);
            return;
        }

        EditorLodGridRenderState renderState = EnsureEditorLodGridRenderState();
        if (renderState == null || renderState.mesh == null || renderState.meshRenderer == null)
        {
            return;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<Color> colors = new List<Color>();
        List<int> indices = new List<int>();
        GaussianSplatLODObject[] lodObjects = FindSceneObjects<GaussianSplatLODObject>(gameObject.scene);
        for (int i = 0; i < lodObjects.Length; i++)
        {
            AppendEditorLodGrid(sceneCombiner, lodObjects[i], vertices, colors, indices);
        }

        if (vertices.Count == 0)
        {
            renderState.mesh.Clear();
            SetEditorLodGridRendererEnabled(false);
            return;
        }

        renderState.mesh.Clear();
        renderState.mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        renderState.mesh.SetVertices(vertices);
        renderState.mesh.SetColors(colors);
        renderState.mesh.SetIndices(indices, MeshTopology.Lines, 0, true);
        renderState.mesh.RecalculateBounds();
        int renderQueue = (TryGetRenderQueueOverride(out int overrideQueue) ? overrideQueue : DEFAULT_START_RENDER_QUEUE) - 1;
        renderState.material.renderQueue = Mathf.Clamp(renderQueue, 2000, 5000);
        renderState.meshRenderer.sharedMaterial = renderState.material;
        renderState.meshRenderer.enabled = true;
        if (!renderState.gameObject.activeSelf)
        {
            renderState.gameObject.SetActive(true);
        }
    }

    void AppendEditorLodGrid(GaussianSplatCombiner sceneCombiner, GaussianSplatLODObject lodObject, List<Vector3> vertices, List<Color> colors, List<int> indices)
    {
        if (lodObject == null || !lodObject.gameObject.activeInHierarchy || !lodObject.IsRenderable())
        {
            return;
        }

        Color[] chunkStates = sceneCombiner.GetEditorLODChunkStates(lodObject);
        if (chunkStates == null || chunkStates.Length == 0)
        {
            return;
        }

        EditorLodGridCache cache = GetEditorLodGridCache(lodObject);
        if (cache == null || cache.chunkBounds == null)
        {
            return;
        }

        int chunkCount = Mathf.Min(cache.chunkBounds.Length, chunkStates.Length);
        Matrix4x4 localToWorld = lodObject.transform.localToWorldMatrix;
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            Color state = chunkStates[chunkIndex];
            int selectedCount = Mathf.RoundToInt(state.r);
            float fraction = lodObject.chunkSize > 0 ? Mathf.Clamp01((float)selectedCount / lodObject.chunkSize) : 0.0f;
            int colorIndex = Mathf.Clamp(Mathf.RoundToInt((1.0f - fraction) * (_editorLodGridColors.Length - 1)), 0, _editorLodGridColors.Length - 1);
            Color color = selectedCount > 0
                ? _editorLodGridColors[colorIndex]
                : new Color(0.35f, 0.35f, 0.35f, 0.45f);

            Bounds bounds = cache.chunkBounds[chunkIndex];
            AppendWireBounds(localToWorld, bounds, color, vertices, colors, indices);
        }
    }

    static void AppendWireBounds(Matrix4x4 localToWorld, Bounds bounds, Color color, List<Vector3> vertices, List<Color> colors, List<int> indices)
    {
        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;
        Vector3[] corners =
        {
            center + new Vector3(-extents.x, -extents.y, -extents.z),
            center + new Vector3( extents.x, -extents.y, -extents.z),
            center + new Vector3( extents.x, -extents.y,  extents.z),
            center + new Vector3(-extents.x, -extents.y,  extents.z),
            center + new Vector3(-extents.x,  extents.y, -extents.z),
            center + new Vector3( extents.x,  extents.y, -extents.z),
            center + new Vector3( extents.x,  extents.y,  extents.z),
            center + new Vector3(-extents.x,  extents.y,  extents.z),
        };
        int[] edgePairs =
        {
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        };
        int start = vertices.Count;
        for (int i = 0; i < corners.Length; i++)
        {
            vertices.Add(localToWorld.MultiplyPoint3x4(corners[i]));
            colors.Add(color);
        }
        for (int i = 0; i < edgePairs.Length; i++)
        {
            indices.Add(start + edgePairs[i]);
        }
    }

    EditorLodGridRenderState EnsureEditorLodGridRenderState()
    {
        if (_editorLodGridRenderStates.TryGetValue(this, out EditorLodGridRenderState state)
            && state != null
            && state.gameObject != null
            && state.meshFilter != null
            && state.meshRenderer != null
            && state.mesh != null
            && state.material != null)
        {
            return state;
        }

        Shader shader = Shader.Find("Hidden/GaussianSplatting/LODDebugGrid");
        if (shader == null)
        {
            SetEditorLodGridRendererEnabled(false);
            return null;
        }

        GameObject gridObject = new GameObject("GaussianSplatLODGridDebug");
        gridObject.hideFlags = HideFlags.HideAndDontSave;
        gridObject.transform.position = Vector3.zero;
        gridObject.transform.rotation = Quaternion.identity;
        gridObject.transform.localScale = Vector3.one;
        SceneManager.MoveGameObjectToScene(gridObject, gameObject.scene);

        Mesh mesh = new Mesh { name = "GaussianSplatLODGridDebugMesh", hideFlags = HideFlags.HideAndDontSave };
        Material material = new Material(shader)
        {
            name = "GaussianSplatLODGridDebugMaterial",
            hideFlags = HideFlags.HideAndDontSave
        };
        MeshFilter meshFilter = gridObject.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = gridObject.AddComponent<MeshRenderer>();
        meshFilter.sharedMesh = mesh;
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        meshRenderer.allowOcclusionWhenDynamic = false;

        state = new EditorLodGridRenderState
        {
            gameObject = gridObject,
            meshFilter = meshFilter,
            meshRenderer = meshRenderer,
            mesh = mesh,
            material = material
        };
        _editorLodGridRenderStates[this] = state;
        return state;
    }

    void SetEditorLodGridRendererEnabled(bool enabled)
    {
        if (!_editorLodGridRenderStates.TryGetValue(this, out EditorLodGridRenderState state) || state == null || state.gameObject == null)
        {
            return;
        }
        if (state.meshRenderer != null)
        {
            state.meshRenderer.enabled = enabled;
        }
        if (state.gameObject.activeSelf != enabled)
        {
            state.gameObject.SetActive(enabled);
        }
    }

    static EditorLodGridCache GetEditorLodGridCache(GaussianSplatLODObject lodObject)
    {
        int chunkCount = lodObject != null ? lodObject.GetChunkCount() : 0;
        Texture2D minTexture = lodObject != null ? lodObject.chunkBoundsMinTexture : null;
        Texture2D maxTexture = lodObject != null ? lodObject.chunkBoundsMaxTexture : null;
        if (chunkCount <= 0 || minTexture == null || maxTexture == null)
        {
            return null;
        }

        int minTextureId = minTexture.GetInstanceID();
        int maxTextureId = maxTexture.GetInstanceID();
        if (_editorLodGridCaches.TryGetValue(lodObject, out EditorLodGridCache cache)
            && cache != null
            && cache.minTextureId == minTextureId
            && cache.maxTextureId == maxTextureId
            && cache.chunkCount == chunkCount
            && cache.chunkBounds != null)
        {
            return cache;
        }

        Color[] minPixels = ReadEditorTexturePixels(minTexture);
        Color[] maxPixels = ReadEditorTexturePixels(maxTexture);
        if (minPixels == null || maxPixels == null)
        {
            return null;
        }

        cache = new EditorLodGridCache
        {
            minTextureId = minTextureId,
            maxTextureId = maxTextureId,
            chunkCount = chunkCount,
            chunkBounds = new Bounds[chunkCount]
        };

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            Color minPixel = chunkIndex < minPixels.Length ? minPixels[chunkIndex] : Color.clear;
            Color maxPixel = chunkIndex < maxPixels.Length ? maxPixels[chunkIndex] : Color.clear;
            Vector3 min = new Vector3(minPixel.r, minPixel.g, minPixel.b);
            Vector3 max = new Vector3(maxPixel.r, maxPixel.g, maxPixel.b);
            Vector3 size = max - min;
            cache.chunkBounds[chunkIndex] = new Bounds((min + max) * 0.5f, new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)));
        }

        _editorLodGridCaches[lodObject] = cache;
        return cache;
    }

    static Color[] ReadEditorTexturePixels(Texture texture)
    {
        if (texture == null || texture.width <= 0 || texture.height <= 0)
        {
            return null;
        }

        RenderTexture previous = RenderTexture.active;
        RenderTexture temp = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        Texture2D readback = null;
        try
        {
            Graphics.Blit(texture, temp);
            RenderTexture.active = temp;
            readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            readback.Apply(false, false);
            return readback.GetPixels();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temp);
            if (readback != null)
            {
                DestroyImmediate(readback);
            }
        }
    }

    static Color SamplePackedEditorPixel(Color[] pixels, int textureWidth, int index)
    {
        if (pixels == null || pixels.Length == 0 || textureWidth <= 0)
        {
            return Color.clear;
        }

        int blocksPerRow = Mathf.Max(1, textureWidth >> 2);
        int blockIndex = index >> 4;
        int blockX = blockIndex & (blocksPerRow - 1);
        int blockY = blockIndex >> ComputeEditorTextureCoordShift(blocksPerRow);
        int inBlock = index & 15;
        int x = blockX * 4 + (inBlock & 3);
        int y = blockY * 4 + (inBlock >> 2);
        int pixelIndex = y * textureWidth + x;
        return pixelIndex >= 0 && pixelIndex < pixels.Length ? pixels[pixelIndex] : Color.clear;
    }

    static int ComputeEditorTextureCoordShift(int width)
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

    bool RefreshCachedSceneSplatObjects()
    {
        GaussianSplatObject[] splats = FindSceneObjects<GaussianSplatObject>(gameObject.scene);
        GaussianSplatLODObject[] lodObjects = FindSceneObjects<GaussianSplatLODObject>(gameObject.scene);
        GameObject[] roots = new GameObject[splats.Length];
        GameObject[] lodRoots = new GameObject[lodObjects.Length];
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
        for (int i = 0; i < lodObjects.Length; i++)
        {
            GaussianSplatLODObject lodObject = lodObjects[i];
            lodRoots[i] = lodObject != null ? lodObject.gameObject : null;
            if (lodObject != null && lodObject.gaussianSplatRenderer != this)
            {
                lodObject.gaussianSplatRenderer = this;
                EditorUtility.SetDirty(lodObject);
                changed = true;
            }
        }
        if (!changed && cachedSceneSplatObjects != null && cachedSceneSplatObjects.Length == roots.Length && cachedSceneLODObjects != null && cachedSceneLODObjects.Length == lodRoots.Length)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                if (cachedSceneSplatObjects[i] != roots[i])
                {
                    changed = true;
                    break;
                }
            }
            for (int i = 0; !changed && i < lodRoots.Length; i++)
            {
                if (cachedSceneLODObjects[i] != lodRoots[i])
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
        cachedSceneLODObjects = lodRoots;
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
        RemoveDuplicateSceneRenderers(scene, primaryRenderer);
        primaryRenderer.RefreshEditorResourcesAndVisibility();
        return primaryRenderer;
    }

    public void RefreshEditorResourcesAndVisibility()
    {
        if (EditorUtility.IsPersistent(this) || !ShouldUseEditorScene(gameObject.scene))
        {
            return;
        }
        if (MigrateEditorSerializedDefaults())
        {
            EditorUtility.SetDirty(this);
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
        ApplyEditorVisibility(gameObject.scene, IsCombinedRenderingMode());
        GaussianSplatRendererUI.RequestEditorRefresh();
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
            Scene scene = gameObject.scene;
            EditorApplication.delayCall += () =>
            {
                if (primaryRenderer != null)
                {
                    RemoveDuplicateSceneRenderers(scene, primaryRenderer);
                }
            };
            QueueEditorRefresh();
            return;
        }
        startRenderQueue = Mathf.Clamp(startRenderQueue, 2000, 5000);
        combinedLodSplatBudgetPC = Mathf.Max(0, combinedLodSplatBudgetPC);
        combinedLodSplatBudgetAndroid = Mathf.Max(0, combinedLodSplatBudgetAndroid);
        MigrateEditorSerializedDefaults();
        combinedLodTargetScale = combinedLodTargetScale > 0.0f ? Mathf.Clamp(combinedLodTargetScale, 0.1f, 1.0f) : DEFAULT_COMBINED_LOD_TARGET_SCALE;
        combinedLodDirectionalBias = combinedLodDirectionalBias > 0.0f ? Mathf.Clamp(combinedLodDirectionalBias, 1.0f, 16.0f) : DEFAULT_COMBINED_LOD_DIRECTIONAL_BIAS;
        QueueEditorRefresh();
    }

    bool MigrateEditorSerializedDefaults()
    {
        if (combinedLodSettingsVersion >= COMBINED_LOD_SETTINGS_VERSION_TARGET_SCALE_095)
        {
            return false;
        }

        if (Mathf.Abs(combinedLodTargetScale - 0.8f) <= 0.0001f)
        {
            combinedLodTargetScale = DEFAULT_COMBINED_LOD_TARGET_SCALE;
        }
        combinedLodSettingsVersion = COMBINED_LOD_SETTINGS_VERSION_TARGET_SCALE_095;
        return true;
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
        int lodMaxCount = 0;
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
        }
        for (int i = 0; cachedSceneLODObjects != null && i < cachedSceneLODObjects.Length; i++)
        {
            GaussianSplatLODObject lodObject = cachedSceneLODObjects[i] != null ? cachedSceneLODObjects[i].GetComponent<GaussianSplatLODObject>() : null;
            if (lodObject == null || !lodObject.IsRenderable())
            {
                continue;
            }
            int lodCount = lodObject.GetMaxLOD0SplatCount();
            lodMaxCount += lodCount;
        }
        int effectiveLodBudget = GetEffectiveCombinedLodSplatBudget();
        int combinedLodCount = effectiveLodBudget > 0 ? Mathf.Min(lodMaxCount, effectiveLodBudget) : lodMaxCount;
        combinedCount += combinedLodCount;
        if (largestCount <= 0 && (!IsCombinedRenderingMode() || combinedCount <= 0))
        {
            return;
        }
        int safeCombinedCount = Mathf.Min(combinedCount, MAX_COMBINED_SPLAT_COUNT);
        int requiredElementCount = IsCombinedRenderingMode() ? Mathf.Max(largestCount, safeCombinedCount) : largestCount;
        int optimalPot = Mathf.NextPowerOfTwo(Mathf.Max(1, requiredElementCount));
        int optimalPotLog2 = Mathf.CeilToInt(Mathf.Log(optimalPot, 2));
        int requiredHeight = 1 << (optimalPotLog2 / 2);
        int requiredWidth = 1 << (optimalPotLog2 / 2 + optimalPotLog2 % 2);
        int histogramPotLog2 = Mathf.Max(0, optimalPotLog2 - RadixSort.BitsPerPass);
        int requiredHistogramHeight = 1 << (histogramPotLog2 / 2);
        int requiredHistogramWidth = 1 << (histogramPotLog2 / 2 + histogramPotLog2 % 2);
        string resourceFolderPath = PlySplatImporter.GetSceneTempResourceFolderPath(gameObject.scene, "RTs");
        string assetPrefix = PlySplatImporter.SanitizeAssetName(name);
        bool resourcesChanged = false;
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues0, resourceFolderPath, assetPrefix + "_KeyValues0", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues1, resourceFolderPath, assetPrefix + "_KeyValues1", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.histograms, resourceFolderPath, assetPrefix + "_Histograms", requiredHistogramWidth, requiredHistogramHeight, RenderTextureFormat.ARGBFloat, false, 1);
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
        combiner.UpdateResources(safeCombinedCount);
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
