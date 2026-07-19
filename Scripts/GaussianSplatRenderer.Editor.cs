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
    const int DEBUG_ELLIPSOID_RENDER_QUEUE = 5000;
    const CameraEvent DEBUG_CHUNK_BOUNDS_CAMERA_EVENT = CameraEvent.BeforeForwardAlpha;
    static Shader _debugEllipsoidShader;
    static readonly Dictionary<int, CommandBuffer> _chunkBoundsCommandBuffers = new Dictionary<int, CommandBuffer>();
    // Production splat materials temporarily swapped out for the editor-only debug-ellipsoid shader while
    // "Debug Opaque Ellipsoids" is on, so they can be restored when it's turned off. Keyed per renderer.
    static readonly Dictionary<MeshRenderer, Material[]> _debugSwappedMaterials = new Dictionary<MeshRenderer, Material[]>();

    struct EditorCameraSortFrame
    {
        public int frame;
        public Vector3 position;
    }

    static readonly Dictionary<int, EditorCameraSortFrame> _editorCameraSortFrame = new Dictionary<int, EditorCameraSortFrame>();

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

    [InitializeOnLoadMethod]
    static void RegisterEditorHooks()
    {
        EditorApplication.hierarchyChanged -= QueueEditorRefresh;
        EditorApplication.hierarchyChanged += QueueEditorRefresh;
        EditorApplication.update -= ProcessEditorRefresh;
        EditorApplication.update += ProcessEditorRefresh;
        Camera.onPreCull -= OnEditorCameraPreCull;
        Camera.onPreCull += OnEditorCameraPreCull;
        Camera.onPostRender -= OnEditorCameraPostRender;
        Camera.onPostRender += OnEditorCameraPostRender;
    }

    static Material _chunkBoundsDebugMaterial;

    static Material GetChunkBoundsDebugMaterial()
    {
        if (_chunkBoundsDebugMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/VRChatGaussianSplatting/DebugChunkBounds");
            if (shader != null)
            {
                _chunkBoundsDebugMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }
        return _chunkBoundsDebugMaterial;
    }

    // Editor-only chunk bounding boxes (renderer "Debug Chunk Bounds" toggle). Drawn after opaque depth exists,
    // before transparent passes, so the boxes use scene depth instead of behaving as an overlay.
    static void OnEditorCameraPostRender(Camera camera)
    {
        RemoveChunkBoundsCommandBuffer(camera);
    }

    static void UpdateChunkBoundsCommandBuffer(Camera camera)
    {
        RemoveChunkBoundsCommandBuffer(camera);
        if (Application.isPlaying || camera == null)
        {
            return;
        }
        if (camera.cameraType != CameraType.SceneView && camera.cameraType != CameraType.Game)
        {
            return;
        }

        // Scope the overlay to the scene this camera actually renders. A Prefab-Stage SceneView camera has
        // camera.scene set to the prefab preview scene (that is how the stage isolates its rendering); the main
        // scene view / game view leave camera.scene invalid. Without this, a prefab camera would draw the main
        // scene's boxes (out in the void) or nothing.
        Scene cameraScene = camera.scene;
        bool prefabCamera = cameraScene.IsValid()
            && UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(cameraScene)
            && ShouldUseEditorScene(cameraScene);

        // The debug toggles live on a GaussianSplatRenderer. A prefab stage usually has none (the renderer is an
        // auto-created scene singleton, never saved into prefabs), so first look for a renderer in this camera's
        // scene, then for a prefab camera fall back to the toggle from any enabled renderer so the choice carries
        // into Prefab Mode. Match the original semantics: pick the first renderer that actually requests a debug draw.
        GaussianSplatRenderer[] renderers = FindSceneObjects<GaussianSplatRenderer>(default(Scene));
        bool drawBounds = false, drawCenterArea = false;
        bool haveTarget = false;
        Scene targetScene = default(Scene);
        for (int i = 0; i < renderers.Length; i++)
        {
            GaussianSplatRenderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || (!renderer.debugDrawChunkBounds && !renderer.debugDrawChunkCenterArea))
            {
                continue;
            }
            Scene rendererScene = renderer.gameObject.scene;
            bool rendererInPreview = UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(rendererScene);
            bool sameContext = prefabCamera ? (rendererScene == cameraScene) : !rendererInPreview;
            if (!sameContext)
            {
                continue;
            }
            drawBounds = renderer.debugDrawChunkBounds;
            drawCenterArea = renderer.debugDrawChunkCenterArea;
            targetScene = rendererScene;
            haveTarget = true;
            break;
        }
        if (prefabCamera && !haveTarget)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                GaussianSplatRenderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || (!renderer.debugDrawChunkBounds && !renderer.debugDrawChunkCenterArea))
                {
                    continue;
                }
                drawBounds = renderer.debugDrawChunkBounds;
                drawCenterArea = renderer.debugDrawChunkCenterArea;
                targetScene = cameraScene; // draw the prefab stage's own LOD/splat objects
                haveTarget = true;
                break;
            }
        }
        if (!haveTarget || (!drawBounds && !drawCenterArea))
        {
            return;
        }

        Material material = GetChunkBoundsDebugMaterial();
        if (material == null)
        {
            return;
        }
        CommandBuffer commandBuffer = new CommandBuffer { name = "Gaussian Splat Chunk Bounds" };
        bool hasCommands = AddChunkBoundsCommands(targetScene, material, commandBuffer, drawBounds, drawCenterArea);
        if (!hasCommands)
        {
            commandBuffer.Release();
            return;
        }
        camera.AddCommandBuffer(DEBUG_CHUNK_BOUNDS_CAMERA_EVENT, commandBuffer);
        _chunkBoundsCommandBuffers[camera.GetInstanceID()] = commandBuffer;
    }

    static void RemoveChunkBoundsCommandBuffer(Camera camera)
    {
        if (camera == null)
        {
            return;
        }
        int cameraId = camera.GetInstanceID();
        if (!_chunkBoundsCommandBuffers.TryGetValue(cameraId, out CommandBuffer commandBuffer))
        {
            return;
        }
        camera.RemoveCommandBuffer(DEBUG_CHUNK_BOUNDS_CAMERA_EVENT, commandBuffer);
        commandBuffer.Release();
        _chunkBoundsCommandBuffers.Remove(cameraId);
    }

    static bool AddChunkBoundsCommands(Scene scene, Material material, CommandBuffer commandBuffer, bool drawBounds, bool drawCenterArea)
    {
        bool hasCommands = false;
        MaterialPropertyBlock properties = new MaterialPropertyBlock();
        GaussianSplatObject[] lodObjects = FindSceneObjects<GaussianSplatObject>(default(Scene));
        for (int i = 0; i < lodObjects.Length; i++)
        {
            GaussianSplatObject lo = lodObjects[i];
            if (lo == null || lo.gameObject.scene != scene || !lo.gameObject.activeInHierarchy)
            {
                continue;
            }
            int chunkCount = lo.GetChunkCount();
            if (chunkCount <= 0)
            {
                continue;
            }
            if (drawBounds && lo.chunkBoundsMinTexture != null && lo.chunkBoundsMaxTexture != null)
            {
                properties.Clear();
                properties.SetTexture("_ChunkBoundsMin", lo.chunkBoundsMinTexture);
                properties.SetTexture("_ChunkBoundsMax", lo.chunkBoundsMaxTexture);
                properties.SetInt("_ChunkBoundsWidth", lo.chunkBoundsMinTexture.width);
                properties.SetInt("_ChunkBoundsMaxRow", 0);
                properties.SetInt("_CenterAreaMode", 0);
                properties.SetInt("_ChunkCount", chunkCount);
                properties.SetMatrix("_LocalToWorld", lo.transform.localToWorldMatrix);
                properties.SetColor("_Color", new Color(0.1f, 1.0f, 0.2f, 0.6f));
                properties.SetFloat("_ColorByIndex", 1.0f);
                commandBuffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Lines, 24, chunkCount, properties);
                hasCommands = true;
            }
            // The chunk range texture's 2nd block holds (center.xyz, area); draw the equal-area cube on the center.
            if (drawCenterArea && lo.chunkRangeTexture != null && lo.chunkRangeTexture.height >= 2)
            {
                properties.Clear();
                properties.SetTexture("_ChunkBoundsMin", lo.chunkRangeTexture);
                properties.SetTexture("_ChunkBoundsMax", lo.chunkRangeTexture);
                properties.SetInt("_ChunkBoundsWidth", lo.chunkRangeTexture.width);
                properties.SetInt("_ChunkBoundsMaxRow", lo.chunkRangeTexture.height / 2);
                properties.SetInt("_CenterAreaMode", 1);
                properties.SetInt("_ChunkCount", chunkCount);
                properties.SetMatrix("_LocalToWorld", lo.transform.localToWorldMatrix);
                properties.SetColor("_Color", new Color(1.0f, 0.5f, 0.05f, 0.8f)); // orange = center+area cube
                properties.SetFloat("_ColorByIndex", 0.0f);
                commandBuffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Lines, 24, chunkCount, properties);
                hasCommands = true;
            }
        }

        return hasCommands;
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
        if (Application.isPlaying || camera == null)
        {
            return;
        }
        UpdateChunkBoundsCommandBuffer(camera);
        if (camera.cameraType != CameraType.SceneView)
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
                renderer.SortCameraViews(cameraPosition, cameraForward, cameraPosition, false, true, camera.pixelHeight, camera.fieldOfView);
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
        for (int tier = 0; tier < COMBINED_BUCKET_TIER_COUNT; tier++)
        {
            if (TryGetTierTexture(splatRenderOrderByBucket, tier, out RenderTexture order)
                && TryGetTierTexture(splatRenderOrderPhotoByBucket, tier, out RenderTexture orderPhoto))
            {
                return true;
            }
        }
        return false;
    }

    bool RefreshCachedSceneSplatObjects()
    {
        GaussianSplatObject[] lodObjects = FindSceneObjects<GaussianSplatObject>(gameObject.scene);
        GameObject[] lodRoots = new GameObject[lodObjects.Length];
        bool changed = false;
        for (int i = 0; i < lodObjects.Length; i++)
        {
            GaussianSplatObject lodObject = lodObjects[i];
            lodRoots[i] = lodObject != null ? lodObject.gameObject : null;
            if (lodObject != null && lodObject.gaussianSplatRenderer != this)
            {
                lodObject.gaussianSplatRenderer = this;
                EditorUtility.SetDirty(lodObject);
                changed = true;
            }
        }
        if (!changed && cachedSceneLODObjects != null && cachedSceneLODObjects.Length == lodRoots.Length)
        {
            for (int i = 0; i < lodRoots.Length; i++)
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
        if (sceneCombiner != null && sceneCombiner.EnsureGeneratedHierarchyState(false))
        {
            EditorUtility.SetDirty(sceneCombiner);
        }
        ApplyEditorDebugRenderingMode();
        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    public bool PrepareEditorCameraRender(Camera camera)
    {
        if (camera == null)
        {
            return false;
        }
        if (!HasEditorSortRenderOrderTextures())
        {
            RefreshEditorResourcesAndVisibility();
            if (!HasEditorSortRenderOrderTextures())
            {
                return false;
            }
        }
        SortCameraViews(GetEditorCameraWorldPosition(camera), GetEditorCameraWorldForward(camera),
            GetEditorCameraWorldPosition(camera), false, true, camera.pixelHeight, camera.fieldOfView);
        return true;
    }

    public bool PrepareEditorColliderCameraRender(Camera camera, Matrix4x4 worldToBox, float boxHeight)
    {
        if (camera == null)
        {
            return false;
        }
        if (!HasEditorSortRenderOrderTextures())
        {
            RefreshEditorResourcesAndVisibility();
            if (!HasEditorSortRenderOrderTextures())
            {
                return false;
            }
        }
        if (!EnsureInitialized())
        {
            return false;
        }

        Vector3 cameraPosition = GetEditorCameraWorldPosition(camera);
        Vector3 cameraForward = GetEditorCameraWorldForward(camera);
        GaussianSplatCombiner sceneCombiner = GetCombiner();
        if (sceneCombiner == null)
        {
            return false;
        }

        sceneCombiner.SetLodSplatTargetScale(1.0f);
        sceneCombiner.SetLodDirectionalBias(GetCombinedLodDirectionalBias());
        sceneCombiner.SetLodShBand(GetCurrentSHBand());
        sceneCombiner.SetEditorDebugLodColors(false);

        Vector4 fullDetailLodParams = new Vector4(-GaussianSplatObject.MAX_LOD_ALPHA_LOG2, 0.0f, 0.0f, 0.0f);
        if (!UpdateCombinedTexturesForSort(sceneCombiner, cameraPosition, cameraPosition, cameraForward, cameraPosition,
            false, 0, fullDetailLodParams, false, true, true))
        {
            return false;
        }

        float previousPlanarSort = keyValueMat.HasProperty("_GS_ColliderPlanarSort") ? keyValueMat.GetFloat("_GS_ColliderPlanarSort") : 0.0f;
        Matrix4x4 objectToBox = _sortedRenderer != null ? worldToBox * _sortedRenderer.transform.localToWorldMatrix : worldToBox;
        keyValueMat.SetFloat("_GS_ColliderPlanarSort", 1.0f);
        keyValueMat.SetMatrix("_GS_ColliderObjectToBox", objectToBox);
        keyValueMat.SetFloat("_GS_ColliderBoxHeight", Mathf.Max(boxHeight, 1e-6f));
        try
        {
            SetSortCameraPos(cameraPosition);
            _radixSort.RunFullSortForEditor(splatRenderOrder, SCREEN_CAMERA_ID);
            _completedCameraPos[SCREEN_CAMERA_ID] = QuantizePosition(cameraPosition);
            _completedCameraWorldPos[SCREEN_CAMERA_ID] = cameraPosition;
            _hasCompletedSort[SCREEN_CAMERA_ID] = true;
            OnScreenSortPublished();
            return true;
        }
        finally
        {
            keyValueMat.SetFloat("_GS_ColliderPlanarSort", previousPlanarSort);
        }
    }

    void ApplyEditorDebugRenderingMode()
    {
        bool debug = debugRenderOpaqueEllipsoids;
        // The debug-ellipsoid swap applies to the combined renderer and its per-chunk child renderers.
        GaussianSplatCombiner sceneCombiner = GetCombiner();
        MeshRenderer combinedRenderer = sceneCombiner != null ? sceneCombiner.GetCombinedSortedRenderer() : null;
        if (combinedRenderer == null)
        {
            return;
        }

        ApplyDebugRenderingMode(combinedRenderer, debug);
        MeshRenderer[] chunkRenderers = combinedRenderer.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; chunkRenderers != null && i < chunkRenderers.Length; i++)
        {
            ApplyDebugRenderingMode(chunkRenderers[i], debug);
        }
    }

    public void ApplyEditorDebugRenderingModeNow()
    {
        ApplyEditorDebugRenderingMode();
    }

    // Editor-only "Debug Opaque Ellipsoids": swap each splat material to the editor-only debug-ellipsoid
    // replacement shader (and restore when off). The production shaders contain no debug pass, so this is
    // purely an editor preview and ships nothing. Idempotent + refresh-safe: a full refresh re-asserts the
    // production materials, after which this re-swaps them; turning debug off restores from the cache.
    static void ApplyDebugRenderingMode(MeshRenderer renderer, bool debug)
    {
        if (renderer == null)
        {
            return;
        }
        Material[] current = renderer.sharedMaterials;
        if (current == null || current.Length == 0)
        {
            return;
        }
        bool currentlyDebug = false;
        for (int i = 0; i < current.Length; i++)
        {
            if (IsDebugMaterial(current[i])) { currentlyDebug = true; break; }
        }

        if (debug)
        {
            if (currentlyDebug)
            {
                return;
            }
            Shader debugShader = GetDebugEllipsoidShader();
            if (debugShader == null)
            {
                return;
            }
            Material[] swapped = new Material[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                swapped[i] = CreateDebugMaterial(current[i], debugShader);
            }
            _debugSwappedMaterials[renderer] = current;
            renderer.sharedMaterials = swapped;
        }
        else
        {
            if (!currentlyDebug)
            {
                return;
            }
            if (_debugSwappedMaterials.TryGetValue(renderer, out Material[] original))
            {
                _debugSwappedMaterials.Remove(renderer);
                renderer.sharedMaterials = original;
            }
            for (int i = 0; i < current.Length; i++)
            {
                if (IsDebugMaterial(current[i]))
                {
                    Object.DestroyImmediate(current[i]);
                }
            }
        }
    }

    static Shader GetDebugEllipsoidShader()
    {
        if (_debugEllipsoidShader == null)
        {
            _debugEllipsoidShader = Shader.Find("Hidden/VRChatGaussianSplatting/DebugEllipsoid");
        }
        return _debugEllipsoidShader;
    }

    static bool IsDebugMaterial(Material material)
    {
        return material != null && material.shader != null && material.shader == GetDebugEllipsoidShader();
    }

    // Only splat materials (those exposing _GS_Positions) get the debug shader; non-splat materials
    // (AlphaDepthMask, sRGB convert) are passed through unchanged so they still bind correctly.
    static Material CreateDebugMaterial(Material source, Shader debugShader)
    {
        if (source == null || source.shader == null || !source.HasProperty("_GS_Positions"))
        {
            return source;
        }
        Material debugMaterial = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
        debugMaterial.shader = debugShader;
        debugMaterial.renderQueue = DEBUG_ELLIPSOID_RENDER_QUEUE;
        return debugMaterial;
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
        requestedSHBand = Mathf.Clamp(requestedSHBand, 0, 3);
        ResetCameraPositions();
        SceneView.RepaintAll();
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
        bool changed = false;
        Material computeKeyValues = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ComputeKeyValue.mat");
        if (computeKeyValues != null && radixSort.computeKeyValues != computeKeyValues)
        {
            radixSort.computeKeyValues = computeKeyValues;
            changed = true;
        }
        Material radixSortMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/RadixSort/Materials/Misha_RadixSort.mat");
        if (radixSortMaterial != null && radixSort.radixSort != radixSortMaterial)
        {
            radixSort.radixSort = radixSortMaterial;
            changed = true;
        }
        Material copyMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_CopyRenderOrder.mat");
        if (copyMaterial != null && radixSort.copySortedOrder != copyMaterial)
        {
            radixSort.copySortedOrder = copyMaterial;
            changed = true;
        }
        if (changed)
        {
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
        return GaussianSplatImporter.EnsureSortRenderTexture(ref targetTexture, folderPath, assetName, width, height, format, useMipMap, volumeDepth);
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

    static bool EnsureBucketArray(ref RenderTexture[] textures)
    {
        if (textures != null && textures.Length == COMBINED_BUCKET_TIER_COUNT)
        {
            return false;
        }
        RenderTexture[] resized = new RenderTexture[COMBINED_BUCKET_TIER_COUNT];
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

    // Every object is a computed-LOD object, so the whole active set is thinnable: total = min(thinnableSum,
    // budget), clamped to the combined cap. (internal for the test assembly.)
    internal static int ComputeCombinedTierCount(int thinnableSum, int lodBudget)
    {
        int thinnable = lodBudget > 0 ? Mathf.Min(thinnableSum, Mathf.Max(0, lodBudget)) : thinnableSum;
        return Mathf.Min(MAX_COMBINED_SPLAT_COUNT, Mathf.Max(0, thinnable));
    }


    void UpdateSortingResourceTextures()
    {
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort == null)
        {
            return;
        }
        int thinnableSum = 0;   // every computed-LOD object; the budget caps these
        for (int i = 0; cachedSceneLODObjects != null && i < cachedSceneLODObjects.Length; i++)
        {
            GaussianSplatObject lodObject = cachedSceneLODObjects[i] != null ? cachedSceneLODObjects[i].GetComponent<GaussianSplatObject>() : null;
            if (lodObject == null || !lodObject.IsRenderable())
            {
                continue;
            }
            thinnableSum += lodObject.GetMaxLOD0SplatCount();
        }
        // Max splats the scene can ever render = min(thinnableSum, highest-quality budget).
        int maxReachableTotal = ComputeCombinedTierCount(thinnableSum, GetCombinedLodSplatBudgetAtQuality(1.0f));
        if (maxReachableTotal <= 0)
        {
            return;
        }
        // Pool-backed sort textures: tier i == RT bucket i (256K/1M/4M/16M). Assign the shared pool sets into
        // every tier slot; the runtime picks the bucket by the live rendered count. The baseline (non-array)
        // fields default to the largest bucket the scene can reach (the runtime swap overrides them per frame).
        int safeCombinedCount = maxReachableTotal;
        bool resourcesChanged = false;
        resourcesChanged |= EnsureBucketArray(ref splatRenderOrderByBucket);
        resourcesChanged |= EnsureBucketArray(ref splatRenderOrderPhotoByBucket);
        resourcesChanged |= EnsureBucketArray(ref radixSort.keyValues0ByBucket);
        resourcesChanged |= EnsureBucketArray(ref radixSort.keyValues1ByBucket);
        resourcesChanged |= EnsureBucketArray(ref radixSort.histogramsByBucket);
        resourcesChanged |= EnsureBucketArray(ref radixSort.prefixSumsByBucket);
        for (int b = 0; b < COMBINED_BUCKET_TIER_COUNT; b++)
        {
            GaussianSplatRTPool.BucketSet set = GaussianSplatRTPool.LoadBucket(b);
            resourcesChanged |= AssignBucketTexture(ref radixSort.keyValues0ByBucket[b], set.keyValues0);
            resourcesChanged |= AssignBucketTexture(ref radixSort.keyValues1ByBucket[b], set.keyValues1);
            resourcesChanged |= AssignBucketTexture(ref radixSort.histogramsByBucket[b], set.histograms);
            resourcesChanged |= AssignBucketTexture(ref radixSort.prefixSumsByBucket[b], set.prefixSums);
            resourcesChanged |= AssignBucketTexture(ref splatRenderOrderByBucket[b], set.splatRenderOrder);
            resourcesChanged |= AssignBucketTexture(ref splatRenderOrderPhotoByBucket[b], set.splatRenderOrderPhoto);
        }
        if (resourcesChanged)
        {
            EditorUtility.SetDirty(radixSort);
            EditorUtility.SetDirty(this);
        }
        ApplyEditorRenderQueueOverride();
        combiner = GaussianSplatCombiner.EnsureSceneCombiner(this);
        if (combiner == null)
        {
            return;
        }
        combiner.UpdateResources(safeCombinedCount);
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
        // Override the combined renderer's material queues.
        GaussianSplatCombiner sceneCombiner = GetCombiner();
        MeshRenderer combinedRenderer = sceneCombiner != null ? sceneCombiner.GetCombinedSortedRenderer() : null;
        Material[] materials = combinedRenderer != null ? combinedRenderer.sharedMaterials : null;
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
#endif
