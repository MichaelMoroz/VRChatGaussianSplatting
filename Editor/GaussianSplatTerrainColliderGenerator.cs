#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Editor
{
    // Editor tool that bakes a collision mesh from the normal fused Gaussian splat renderer by
    // rendering a top-down collider camera with a depth accumulation shader.
    static class GaussianSplatTerrainColliderGenerator
    {
        const float HeightSentinel = float.NegativeInfinity;
        const float OpacityEpsilon = 1e-8f;
        const string ColliderChildName = "SplatHeightmapCollider";
        const string GridMaterialPath = "Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ColliderGrid.mat";
        const string ResolveShaderName = "Hidden/GaussianSplatting/HeightmapColliderResolve";

        [MenuItem("Gaussian Splatting/Generate Terrain Collider...")]
        static void Open()
        {
            ColliderWindow window = EditorWindow.GetWindow<ColliderWindow>();
            window.titleContent = new GUIContent("Terrain Collider");
            window.TryUseSelection();
            window.Show();
        }

        [MenuItem("CONTEXT/GaussianSplatObject/Generate terrain collider for gaussian splat")]
        static void OpenForContext(MenuCommand command)
        {
            ColliderWindow window = EditorWindow.GetWindow<ColliderWindow>();
            window.titleContent = new GUIContent("Terrain Collider");
            window.SetTarget(command.context as GaussianSplatObject);
            window.Show();
        }

        sealed class ColliderWindow : EditorWindow
        {
            const float MinBoxSize = 0.001f;
            static readonly Color BoxWireColor = new Color(0.15f, 0.85f, 1.0f, 0.95f);
            static readonly Color FaceHandleColor = new Color(1.0f, 0.92f, 0.12f, 1.0f);

            [SerializeField] GaussianSplatObject _target;
            [SerializeField] Vector3 _boxCenter;
            [SerializeField] Vector3 _boxEuler;
            [SerializeField] Vector3 _boxSize = new Vector3(10f, 6f, 10f);
            [SerializeField] bool _showSceneHandle;

            [SerializeField] int _outputResolution = 512;
            [SerializeField] int _supersample = 1;
            [SerializeField] float _opacityMultiplier = 4.0f;
            [SerializeField] float _alphaCullThreshold = 0.04f;
            [SerializeField] float _reductionPercentile = 0.5f;
            [SerializeField] int _medianFilterRadius = 4;
            [SerializeField] int _holeFillRadius = 3;

            void OnEnable()
            {
                SceneView.duringSceneGui += OnSceneGui;
                Undo.undoRedoPerformed += OnUndoRedo;
            }

            void OnDisable()
            {
                SceneView.duringSceneGui -= OnSceneGui;
                Undo.undoRedoPerformed -= OnUndoRedo;
            }

            public void SetTarget(GaussianSplatObject target)
            {
                if (target == null) return;
                _target = target;
                SeedBoxFromTarget();
            }

            public void TryUseSelection()
            {
                GaussianSplatObject sel = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<GaussianSplatObject>() : null;
                if (sel != null) SetTarget(sel);
            }

            void SeedBoxFromTarget()
            {
                if (_target == null) return;
                Bounds world = TransformBounds(_target.transform.localToWorldMatrix,
                    new Bounds((_target.boundsMin + _target.boundsMax) * 0.5f, _target.boundsMax - _target.boundsMin));
                if (world.size == Vector3.zero) return;
                _boxCenter = world.center;
                _boxSize = world.size;
                _boxEuler = Vector3.zero;
            }

            void OnGUI()
            {
                EditorGUILayout.LabelField("Splat Heightmap Collider", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                GaussianSplatObject newTarget = (GaussianSplatObject)EditorGUILayout.ObjectField("Source splat", _target, typeof(GaussianSplatObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    _target = newTarget;
                    if (_target != null) SeedBoxFromTarget();
                }
                if (GUILayout.Button("Use selected splat")) TryUseSelection();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Region (oriented box)", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                _boxCenter = EditorGUILayout.Vector3Field("Center", _boxCenter);
                _boxEuler = EditorGUILayout.Vector3Field("Rotation", _boxEuler);
                _boxSize = EditorGUILayout.Vector3Field("Size", _boxSize);
                _boxSize = Vector3.Max(_boxSize, new Vector3(MinBoxSize, MinBoxSize, MinBoxSize));
                _showSceneHandle = EditorGUILayout.Toggle("Show scene handle", _showSceneHandle);
                if (EditorGUI.EndChangeCheck()) MarkRegionChanged();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);
                _outputResolution = Mathf.Clamp(EditorGUILayout.IntField("Output resolution", _outputResolution), 8, 4096);
                _supersample = Mathf.Clamp(EditorGUILayout.IntField("Supersample", _supersample), 1, 8);
                _opacityMultiplier = Mathf.Clamp(EditorGUILayout.FloatField("Opacity multiplier", _opacityMultiplier), 0.001f, 1000.0f);
                _alphaCullThreshold = EditorGUILayout.Slider("Cull splats below alpha", _alphaCullThreshold, 0.0f, 1.0f);
                _reductionPercentile = EditorGUILayout.Slider("Reduction percentile", _reductionPercentile, 0.0f, 1.0f);
                _medianFilterRadius = Mathf.Clamp(EditorGUILayout.IntField("Median filter radius", _medianFilterRadius), 0, 8);
                _holeFillRadius = Mathf.Clamp(EditorGUILayout.IntField("Hole fill radius", _holeFillRadius), 0, 32);

                EditorGUILayout.Space();
                using (new EditorGUI.DisabledScope(_target == null))
                {
                    if (GUILayout.Button("Bake Collision Mesh", GUILayout.Height(30f)))
                    {
                        Generate(this);
                    }
                }
                if (_target == null) EditorGUILayout.HelpBox("Assign a source GaussianSplatObject.", MessageType.Info);
            }

            void OnSceneGui(SceneView view)
            {
                if (_target == null || !_showSceneHandle) return;
                Quaternion rot = Quaternion.Euler(_boxEuler);

                EditorGUI.BeginChangeCheck();
                Vector3 newCenter = Handles.PositionHandle(_boxCenter, rot);
                Quaternion newRot = Handles.RotationHandle(rot, _boxCenter);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Edit Splat Collider Region");
                    _boxCenter = newCenter;
                    _boxEuler = newRot.eulerAngles;
                    MarkRegionChanged();
                    rot = Quaternion.Euler(_boxEuler);
                }

                using (new Handles.DrawingScope(BoxWireColor, Matrix4x4.TRS(_boxCenter, rot, Vector3.one)))
                {
                    Handles.DrawWireCube(Vector3.zero, _boxSize);
                }

                DrawFaceHandle(0, -1, rot);
                DrawFaceHandle(0, 1, rot);
                DrawFaceHandle(1, -1, rot);
                DrawFaceHandle(1, 1, rot);
                DrawFaceHandle(2, -1, rot);
                DrawFaceHandle(2, 1, rot);
            }

            void DrawFaceHandle(int axis, int side, Quaternion rotation)
            {
                Vector3 axisVector = AxisVector(axis);
                Vector3 worldAxis = rotation * axisVector;
                float halfSize = Mathf.Max(_boxSize[axis] * 0.5f, MinBoxSize * 0.5f);
                Vector3 handlePosition = _boxCenter + worldAxis * (side * halfSize);
                float handleSize = HandleUtility.GetHandleSize(handlePosition) * 0.085f;

                using (new Handles.DrawingScope(FaceHandleColor))
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 newPosition = Handles.Slider(handlePosition, worldAxis * side, handleSize, Handles.DotHandleCap, 0.0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(this, "Edit Splat Collider Region");
                        MoveBoxSide(axis, side, newPosition, rotation);
                        MarkRegionChanged();
                    }
                }
            }

            void MoveBoxSide(int axis, int side, Vector3 newWorldPosition, Quaternion rotation)
            {
                Vector3 worldAxis = rotation * AxisVector(axis);
                float newCoord = Vector3.Dot(newWorldPosition - _boxCenter, worldAxis);
                float min = -_boxSize[axis] * 0.5f;
                float max = _boxSize[axis] * 0.5f;

                if (side > 0) max = Mathf.Max(newCoord, min + MinBoxSize);
                else min = Mathf.Min(newCoord, max - MinBoxSize);

                float centerDelta = (min + max) * 0.5f;
                Vector3 newSize = _boxSize;
                newSize[axis] = Mathf.Max(max - min, MinBoxSize);
                _boxCenter += worldAxis * centerDelta;
                _boxSize = Vector3.Max(newSize, Vector3.one * MinBoxSize);
            }

            static Vector3 AxisVector(int axis)
            {
                switch (axis)
                {
                    case 0: return Vector3.right;
                    case 1: return Vector3.up;
                    default: return Vector3.forward;
                }
            }

            void MarkRegionChanged()
            {
                _boxSize = Vector3.Max(_boxSize, Vector3.one * MinBoxSize);
                EditorUtility.SetDirty(this);
                Repaint();
                SceneView.RepaintAll();
            }

            void OnUndoRedo()
            {
                _boxSize = Vector3.Max(_boxSize, Vector3.one * MinBoxSize);
                Repaint();
                SceneView.RepaintAll();
            }

            internal GaussianSplatObject Target => _target;
            internal Matrix4x4 BoxToWorld => Matrix4x4.TRS(_boxCenter, Quaternion.Euler(_boxEuler), Vector3.one);
            internal Vector3 BoxSize => _boxSize;
            internal int Resolution => _outputResolution;
            internal int Supersample => _supersample;
            internal float OpacityMultiplier => _opacityMultiplier;
            internal float AlphaCullThreshold => _alphaCullThreshold;
            internal float ReductionPercentile => _reductionPercentile;
            internal int MedianFilterRadius => _medianFilterRadius;
            internal int HoleFillRadius => _holeFillRadius;
        }

        static void Generate(ColliderWindow window)
        {
            GaussianSplatObject target = window.Target;
            if (target == null) return;
            int res = window.Resolution;
            Vector3 size = window.BoxSize;
            Matrix4x4 boxToWorld = window.BoxToWorld;

            float[] heights = new float[res * res];
            for (int i = 0; i < heights.Length; i++) heights[i] = HeightSentinel;

            try
            {
                BakeHeightfield(target, res, window.Supersample,
                    window.OpacityMultiplier, window.AlphaCullThreshold, window.ReductionPercentile,
                    window.MedianFilterRadius, size, boxToWorld, heights);
                if (window.HoleFillRadius > 0)
                {
                    FillSmallHoles(heights, res, window.HoleFillRadius);
                }

                Mesh mesh = BuildMesh(heights, res, size);
                mesh.name = SanitizeName(target.GetDisplayName()) + "_collider";
                mesh = SaveMeshAsset(mesh, target, ExistingColliderMeshAssetPath(target));
                GameObject colliderObject = EnsureColliderObject(target, mesh, boxToWorld);
                Selection.activeGameObject = colliderObject;
                EditorGUIUtility.PingObject(colliderObject);
                Debug.Log($"Baked collision mesh '{mesh.name}' ({mesh.vertexCount} verts).", colliderObject);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static void BakeHeightfield(GaussianSplatObject target, int res, int supersample,
            float opacityMultiplier, float alphaCullThreshold, float reductionPercentile, int medianFilterRadius,
            Vector3 size, Matrix4x4 boxToWorld, float[] heights)
        {
            BakeHeightfieldGpu(target, res, supersample, opacityMultiplier, alphaCullThreshold,
                reductionPercentile, medianFilterRadius, size, boxToWorld, heights);
        }

        static Mesh BuildMesh(float[] heights, int res, Vector3 size)
        {
            Vector3[] verts = new Vector3[res * res];
            float halfX = size.x * 0.5f;
            float halfZ = size.z * 0.5f;
            float bottom = -size.y * 0.5f;
            for (int j = 0; j < res; j++)
            {
                float tz = res > 1 ? j / (float)(res - 1) : 0f;
                float z = -halfZ + tz * size.z;
                for (int i = 0; i < res; i++)
                {
                    float tx = res > 1 ? i / (float)(res - 1) : 0f;
                    float x = -halfX + tx * size.x;
                    float h = heights[i + j * res];
                    float y = IsHole(h) ? bottom : bottom + Mathf.Clamp(h, 0f, size.y);
                    verts[i + j * res] = new Vector3(x, y, z);
                }
            }

            List<int> tris = new List<int>(res * res * 6);
            for (int j = 0; j < res - 1; j++)
            {
                for (int i = 0; i < res - 1; i++)
                {
                    int a = i + j * res;
                    int b = (i + 1) + j * res;
                    int c = i + (j + 1) * res;
                    int d = (i + 1) + (j + 1) * res;
                    if (IsHole(heights[a]) || IsHole(heights[b]) || IsHole(heights[c]) || IsHole(heights[d])) continue;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            Mesh mesh = new Mesh();
            mesh.indexFormat = verts.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static void BakeHeightfieldGpu(GaussianSplatObject target, int res, int supersample,
            float opacityMultiplier, float alphaCullThreshold, float reductionPercentile, int medianFilterRadius,
            Vector3 size, Matrix4x4 boxToWorld, float[] heights)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                throw new System.InvalidOperationException("The selected GaussianSplatObject must be active in the scene.");
            }

            Shader resolveShader = Shader.Find(ResolveShaderName);
            if (resolveShader == null)
            {
                throw new System.InvalidOperationException("Terrain collider resolve shader is missing.");
            }

            supersample = Mathf.Clamp(supersample, 1, 8);
            medianFilterRadius = Mathf.Clamp(medianFilterRadius, 0, 8);
            int superRes = checked(res * supersample);

            RenderTexture accum = null;
            RenderTexture reduced = null;
            RenderTexture filtered = null;
            Material resolveMaterial = null;

            try
            {
                accum = CreateTemporaryRenderTexture(superRes, superRes, RenderTextureFormat.ARGBFloat, "GS Collider Weighted Depth");
                reduced = CreateTemporaryRenderTexture(res, res, RenderTextureFormat.ARGBFloat, "GS Collider Resolved Heights");
                if (medianFilterRadius > 0)
                {
                    filtered = CreateTemporaryRenderTexture(res, res, RenderTextureFormat.ARGBFloat, "GS Collider Filtered Heights");
                }

                // Raster+blend every LOD0 source splat inside the box, CPU-sorted front-to-back, directly from
                // the original packed textures (no fused set, no GPU sort, no chunk cap).
                EditorUtility.DisplayProgressBar("Splat Heightmap Collider", "Rasterizing source splats", 0.35f);
                ColliderSourceBake.RasterSourceIntoAccum(target, boxToWorld, size, opacityMultiplier, alphaCullThreshold,
                    accum, out _, out _);

                EditorUtility.DisplayProgressBar("Splat Heightmap Collider", "Resolving heightmap", 0.82f);
                resolveMaterial = new Material(resolveShader) { hideFlags = HideFlags.HideAndDontSave };
                resolveMaterial.SetTexture("_DepthTex", accum);
                resolveMaterial.SetInt("_OutputResolution", res);
                resolveMaterial.SetInt("_Supersample", supersample);
                resolveMaterial.SetFloat("_BoxHeight", size.y);
                resolveMaterial.SetFloat("_OpacityEpsilon", OpacityEpsilon);
                resolveMaterial.SetFloat("_ReductionPercentile", Mathf.Clamp01(reductionPercentile));
                Graphics.Blit(null, reduced, resolveMaterial, 0);

                RenderTexture finalHeights = reduced;
                if (medianFilterRadius > 0)
                {
                    EditorUtility.DisplayProgressBar("Splat Heightmap Collider", "Median filtering heightmap", 0.9f);
                    resolveMaterial.SetTexture("_InputHeightTex", reduced);
                    resolveMaterial.SetInt("_MedianRadius", medianFilterRadius);
                    Graphics.Blit(null, filtered, resolveMaterial, 1);
                    finalHeights = filtered;
                }

                Color[] pixels = ReadbackFloatTexture(finalHeights);
                int count = Mathf.Min(heights.Length, pixels.Length);
                for (int i = 0; i < count; i++)
                {
                    heights[i] = pixels[i].r;
                }
            }
            finally
            {
                if (resolveMaterial != null) UnityEngine.Object.DestroyImmediate(resolveMaterial);
                if (accum != null) RenderTexture.ReleaseTemporary(accum);
                if (reduced != null) RenderTexture.ReleaseTemporary(reduced);
                if (filtered != null) RenderTexture.ReleaseTemporary(filtered);
            }
        }

        static RenderTexture CreateTemporaryRenderTexture(int width, int height, RenderTextureFormat format, string name)
        {
            RenderTextureDescriptor desc = new RenderTextureDescriptor(width, height, format, 0)
            {
                msaaSamples = 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false
            };
            RenderTexture rt = RenderTexture.GetTemporary(desc);
            rt.name = name;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Point;
            return rt;
        }

        static Color[] ReadbackFloatTexture(Texture texture)
        {
            if (texture == null) return new Color[0];
            RenderTexture rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                readable.Apply(false, false);
                return readable.GetPixels();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        static void FillSmallHoles(float[] heights, int res, int radius)
        {
            bool[] visited = new bool[heights.Length];
            Queue<int> queue = new Queue<int>();
            List<int> component = new List<int>();
            int maxArea = Mathf.Max(1, radius * radius);
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int start = 0; start < heights.Length; start++)
            {
                if (visited[start] || !IsHole(heights[start])) continue;
                component.Clear();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    component.Add(cur);
                    int x = cur % res;
                    int y = cur / res;
                    for (int n = 0; n < 4; n++)
                    {
                        int nx = x + dx[n];
                        int ny = y + dy[n];
                        if (nx < 0 || nx >= res || ny < 0 || ny >= res) continue;
                        int ni = nx + ny * res;
                        if (visited[ni] || !IsHole(heights[ni])) continue;
                        visited[ni] = true;
                        queue.Enqueue(ni);
                    }
                }
                if (component.Count <= maxArea)
                {
                    FillHoleComponent(heights, res, component, radius);
                }
            }
        }

        static void FillHoleComponent(float[] heights, int res, List<int> component, int radius)
        {
            for (int c = 0; c < component.Count; c++)
            {
                int idx = component[c];
                int x = idx % res;
                int y = idx / res;
                float sum = 0.0f;
                int count = 0;
                for (int yy = Mathf.Max(0, y - radius); yy <= Mathf.Min(res - 1, y + radius); yy++)
                {
                    for (int xx = Mathf.Max(0, x - radius); xx <= Mathf.Min(res - 1, x + radius); xx++)
                    {
                        float h = heights[xx + yy * res];
                        if (IsHole(h)) continue;
                        sum += h;
                        count++;
                    }
                }
                if (count > 0) heights[idx] = sum / count;
            }
        }

        static bool IsHole(float height)
        {
            return height == HeightSentinel || height < -1e20f || float.IsNaN(height);
        }

        static GameObject EnsureColliderObject(GaussianSplatObject target, Mesh mesh, Matrix4x4 boxToWorld)
        {
            Transform existing = target.transform.Find(ColliderChildName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(ColliderChildName);
            if (existing == null)
            {
                Undo.RegisterCreatedObjectUndo(go, "Create Splat Heightmap Collider");
            }
            Undo.SetTransformParent(go.transform, target.transform, "Parent Splat Heightmap Collider");
            Vector3 position = boxToWorld.GetColumn(3);
            Quaternion rotation = Quaternion.LookRotation(boxToWorld.GetColumn(2), boxToWorld.GetColumn(1));
            Undo.RecordObject(go.transform, "Place Splat Heightmap Collider");
            go.transform.SetPositionAndRotation(position, rotation);
            SetWorldScale(go.transform, Vector3.one);

            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (filter == null) filter = Undo.AddComponent<MeshFilter>(go);
            Undo.RecordObject(filter, "Assign Splat Heightmap Collider Mesh");
            filter.sharedMesh = mesh;

            MeshCollider collider = go.GetComponent<MeshCollider>();
            if (collider == null) collider = Undo.AddComponent<MeshCollider>(go);
            Undo.RecordObject(collider, "Assign Splat Heightmap Collider Mesh");
            collider.sharedMesh = null;
            collider.sharedMesh = mesh;

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<MeshRenderer>(go);
                Undo.RecordObject(renderer, "Configure Splat Heightmap Collider Renderer");
                renderer.enabled = false;
            }
            Material gridMaterial = LoadColliderGridMaterial();
            if (gridMaterial != null)
            {
                Undo.RecordObject(renderer, "Assign Splat Heightmap Collider Material");
                renderer.sharedMaterial = gridMaterial;
            }
            else if (renderer.sharedMaterial == null)
            {
                Shader shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
                Material material = new Material(shader) { name = ColliderChildName + "Material" };
                Undo.RegisterCreatedObjectUndo(material, "Create Splat Heightmap Collider Material");
                Undo.RecordObject(renderer, "Assign Splat Heightmap Collider Material");
                renderer.sharedMaterial = material;
            }

            EditorUtility.SetDirty(go);
            return go;
        }

        static Material LoadColliderGridMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(GridMaterialPath);
            if (material == null) Debug.LogWarning($"Collider grid material not found at '{GridMaterialPath}'.");
            return material;
        }

        static void SetWorldScale(Transform transform, Vector3 worldScale)
        {
            Transform parent = transform.parent;
            if (parent == null)
            {
                transform.localScale = worldScale;
                return;
            }
            Vector3 parentScale = parent.lossyScale;
            transform.localScale = new Vector3(
                parentScale.x != 0.0f ? worldScale.x / parentScale.x : worldScale.x,
                parentScale.y != 0.0f ? worldScale.y / parentScale.y : worldScale.y,
                parentScale.z != 0.0f ? worldScale.z / parentScale.z : worldScale.z);
        }

        static string ExistingColliderMeshAssetPath(GaussianSplatObject source)
        {
            if (source == null) return null;
            Transform existing = source.transform.Find(ColliderChildName);
            MeshFilter filter = existing != null ? existing.GetComponent<MeshFilter>() : null;
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            string path = mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        static Mesh SaveMeshAsset(Mesh mesh, GaussianSplatObject source, string replacementPath)
        {
            string folder = "Assets";
            Texture positions = source.GetPositions(0);
            string srcPath = positions != null ? AssetDatabase.GetAssetPath(positions) : null;
            if (!string.IsNullOrEmpty(srcPath)) folder = Path.GetDirectoryName(srcPath)?.Replace('\\', '/') ?? "Assets";
            string path = !string.IsNullOrEmpty(replacementPath)
                ? replacementPath
                : AssetDatabase.GenerateUniqueAssetPath(folder + "/" + mesh.name + ".asset");
            if (!string.IsNullOrEmpty(replacementPath))
            {
                AssetDatabase.DeleteAsset(replacementPath);
            }
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Mesh>(path) ?? mesh;
        }

        static string SanitizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "GaussianSplat";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (System.Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }

        static Bounds TransformBounds(Matrix4x4 m, Bounds local)
        {
            Vector3 min = local.min;
            Vector3 max = local.max;
            Bounds b = new Bounds(m.MultiplyPoint3x4(min), Vector3.zero);
            for (int x = 0; x <= 1; x++)
                for (int y = 0; y <= 1; y++)
                    for (int z = 0; z <= 1; z++)
                        b.Encapsulate(m.MultiplyPoint3x4(new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z)));
            return b;
        }
    }
}
#endif
