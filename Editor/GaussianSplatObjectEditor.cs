#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatObject))]
    [CanEditMultipleObjects]
    class GaussianSplatObjectEditor : UnityEditor.Editor
    {
        struct TextureStats
        {
            public string propertyName;
            public string textureName;
            public string dimensions;
            public string format;
            public long gpuBytes;
            public long runtimeBytes;
            public long assetBytes;
        }

        struct InspectorStats
        {
            public MeshRenderer sortedRenderer;
            public int materialCount;
            public int splatMaterialCount;
            public int actualSplatCount;
            public int maxShBand;
            public bool usesPrecomputedSorting;
            public string dataResolution;
            public long totalGpuBytes;
            public long totalRuntimeBytes;
            public long totalAssetBytes;
            public List<TextureStats> textures;
        }

        static readonly MethodInfo GetStorageMemorySizeLongMethod = typeof(UnityEditor.Editor).Assembly
            .GetType("UnityEditor.TextureUtil")
            ?.GetMethod("GetStorageMemorySizeLong", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Texture) }, null);

        SerializedProperty _gaussianSplatRenderer;
        SerializedProperty _sortedObject;
        SerializedProperty _sortedRenderer;

        void OnEnable()
        {
            _gaussianSplatRenderer = serializedObject.FindProperty("gaussianSplatRenderer");
            _sortedObject = serializedObject.FindProperty("sortedObject");
            _sortedRenderer = serializedObject.FindProperty("sortedRenderer");
        }

        public override void OnInspectorGUI()
        {
            DrawUdonSharpHeader();

            serializedObject.Update();

            DrawSettingsGroup("References", DrawReferenceFields);

            if (serializedObject.isEditingMultipleObjects)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("General info is shown for a single selected splat object.", MessageType.Info);
            }
            else
            {
                InspectorStats stats = GatherStats((GaussianSplatObject)target);

                EditorGUILayout.Space();
                DrawGeneralInfo(stats);

                EditorGUILayout.Space();
                DrawTextureInfo(stats);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawUdonSharpUtilities();
        }

        void DrawReferenceFields()
        {
            EditorGUILayout.PropertyField(_gaussianSplatRenderer, new GUIContent("Gaussian Splat Renderer"));
            EditorGUILayout.PropertyField(_sortedObject, new GUIContent("Sorted Object"));
            EditorGUILayout.PropertyField(_sortedRenderer, new GUIContent("Sorted Renderer"));
        }

        static void DrawGeneralInfo(InspectorStats stats)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("General Info", EditorStyles.boldLabel);

            if (stats.sortedRenderer == null)
            {
                EditorGUILayout.HelpBox("No sorted renderer was found for this splat object.", MessageType.Info);
            }
            else
            {
                DrawInfoRow("Sorted Renderer", stats.sortedRenderer.name);
                DrawInfoRow("Material Count", stats.materialCount.ToString());
                DrawInfoRow("Splat Materials", stats.splatMaterialCount.ToString());
                DrawInfoRow("Actual Splats", stats.actualSplatCount > 0 ? stats.actualSplatCount.ToString("N0") : "Unknown");
                DrawInfoRow("Max SH Band", stats.maxShBand.ToString());
                DrawInfoRow("Data Resolution", string.IsNullOrEmpty(stats.dataResolution) ? "Unknown" : stats.dataResolution);
                DrawInfoRow("Precomputed Sorting", stats.usesPrecomputedSorting ? "Yes" : "No");
                DrawInfoRow("Unique Textures", stats.textures.Count.ToString());
            }

            EditorGUILayout.EndVertical();
        }

        static void DrawTextureInfo(InspectorStats stats)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Texture Footprint", EditorStyles.boldLabel);

            if (stats.textures.Count == 0)
            {
                EditorGUILayout.HelpBox("No textures were found on the shared materials for this splat object.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            DrawInfoRow("Estimated GPU Memory", EditorUtility.FormatBytes(stats.totalGpuBytes));
            DrawInfoRow("Runtime Memory", EditorUtility.FormatBytes(stats.totalRuntimeBytes));
            DrawInfoRow("Asset Size", EditorUtility.FormatBytes(stats.totalAssetBytes));

            for (int i = 0; i < stats.textures.Count; ++i)
            {
                TextureStats texture = stats.textures[i];
                EditorGUILayout.Space(3f);
                EditorGUILayout.LabelField($"{texture.propertyName} ({texture.textureName})", EditorStyles.boldLabel);
                DrawInfoRow("Dimensions", texture.dimensions);
                DrawInfoRow("Format", texture.format);
                DrawInfoRow("Estimated GPU Memory", EditorUtility.FormatBytes(texture.gpuBytes));
                DrawInfoRow("Runtime Memory", EditorUtility.FormatBytes(texture.runtimeBytes));
                DrawInfoRow("Asset Size", EditorUtility.FormatBytes(texture.assetBytes));
            }

            EditorGUILayout.EndVertical();
        }

        static void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.SelectableLabel(value, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }

        static InspectorStats GatherStats(GaussianSplatObject splatObject)
        {
            InspectorStats stats = new InspectorStats
            {
                actualSplatCount = -1,
                maxShBand = 0,
                textures = new List<TextureStats>()
            };

            MeshRenderer sortedRenderer = ResolveSortedRenderer(splatObject);
            stats.sortedRenderer = sortedRenderer;
            if (sortedRenderer == null)
            {
                return stats;
            }

            Material[] materials = sortedRenderer.sharedMaterials;
            if (materials == null)
            {
                return stats;
            }

            Dictionary<int, TextureStats> texturesById = new Dictionary<int, TextureStats>();
            for (int materialIndex = 0; materialIndex < materials.Length; ++materialIndex)
            {
                Material material = materials[materialIndex];
                if (material == null)
                {
                    continue;
                }

                stats.materialCount++;
                if (IsSplatMaterial(material))
                {
                    stats.splatMaterialCount++;
                    stats.maxShBand = Mathf.Max(stats.maxShBand, InferMaxShBand(material));

                    if (stats.actualSplatCount < 0 && material.HasProperty("_ActualSplatCount"))
                    {
                        stats.actualSplatCount = material.GetInt("_ActualSplatCount");
                    }

                    if (string.IsNullOrEmpty(stats.dataResolution))
                    {
                        Texture positionTexture = material.GetTexture("_GS_Positions");
                        if (positionTexture != null)
                        {
                            stats.dataResolution = GetTextureDimensions(positionTexture);
                        }
                    }

                    stats.usesPrecomputedSorting |= HasPrecomputedSorting(material);
                }

                CollectTextures(material, texturesById, ref stats.totalGpuBytes, ref stats.totalRuntimeBytes, ref stats.totalAssetBytes);
            }

            foreach (TextureStats texture in texturesById.Values)
            {
                stats.textures.Add(texture);
            }
            stats.textures.Sort((left, right) => string.CompareOrdinal(left.propertyName, right.propertyName));

            return stats;
        }

        static MeshRenderer ResolveSortedRenderer(GaussianSplatObject splatObject)
        {
            if (splatObject == null)
            {
                return null;
            }

            if (splatObject.sortedRenderer != null)
            {
                return splatObject.sortedRenderer;
            }

            GameObject sortedObject = splatObject.sortedObject;
            if (sortedObject == null)
            {
                Transform sortedTransform = splatObject.transform.Find("Sorted");
                if (sortedTransform != null)
                {
                    sortedObject = sortedTransform.gameObject;
                }
            }

            if (sortedObject != null)
            {
                MeshRenderer childRenderer = sortedObject.GetComponent<MeshRenderer>();
                if (childRenderer != null)
                {
                    return childRenderer;
                }
            }

            return splatObject.GetComponent<MeshRenderer>();
        }

        static bool IsSplatMaterial(Material material)
        {
            return material != null && material.HasProperty("_GS_Positions");
        }

        static int InferMaxShBand(Material material)
        {
            if (material == null)
            {
                return 0;
            }

            if (!material.HasProperty("_GS_SH") || material.GetTexture("_GS_SH") == null || !material.HasProperty("_GS_SH_CoeffCount"))
            {
                return 0;
            }

            int coeffCount = material.GetInt("_GS_SH_CoeffCount");
            if (coeffCount >= 15)
            {
                return 3;
            }

            if (coeffCount >= 8)
            {
                return 2;
            }

            if (coeffCount >= 3)
            {
                return 1;
            }

            return 0;
        }

        static bool HasPrecomputedSorting(Material material)
        {
            if (material == null)
            {
                return false;
            }

            if (material.HasProperty("_GS_RenderOrderPrecomputed") && material.GetTexture("_GS_RenderOrderPrecomputed") != null)
            {
                return true;
            }

            return material.IsKeywordEnabled("_PRECOMPUTED_SORTING_ON") || material.IsKeywordEnabled("_PRECOMPUTED_SORTING");
        }

        static void CollectTextures(Material material, Dictionary<int, TextureStats> texturesById, ref long totalGpuBytes, ref long totalRuntimeBytes, ref long totalAssetBytes)
        {
            Shader shader = material.shader;
            if (shader == null)
            {
                return;
            }

            int propertyCount = ShaderUtil.GetPropertyCount(shader);
            for (int propertyIndex = 0; propertyIndex < propertyCount; ++propertyIndex)
            {
                if (ShaderUtil.GetPropertyType(shader, propertyIndex) != ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    continue;
                }

                string propertyName = ShaderUtil.GetPropertyName(shader, propertyIndex);
                Texture texture = material.GetTexture(propertyName);
                if (texture == null)
                {
                    continue;
                }

                int instanceId = texture.GetInstanceID();
                if (texturesById.ContainsKey(instanceId))
                {
                    continue;
                }

                long gpuBytes = GetEstimatedGpuBytes(texture);
                long runtimeBytes = Profiler.GetRuntimeMemorySizeLong(texture);
                long assetBytes = GetAssetSize(texture);
                totalGpuBytes += gpuBytes;
                totalRuntimeBytes += runtimeBytes;
                totalAssetBytes += assetBytes;

                texturesById.Add(instanceId, new TextureStats
                {
                    propertyName = propertyName,
                    textureName = texture.name,
                    dimensions = GetTextureDimensions(texture),
                    format = GetTextureFormat(texture),
                    gpuBytes = gpuBytes,
                    runtimeBytes = runtimeBytes,
                    assetBytes = assetBytes
                });
            }
        }

        static long GetEstimatedGpuBytes(Texture texture)
        {
            if (texture == null || GetStorageMemorySizeLongMethod == null)
            {
                return 0L;
            }

            try
            {
                return (long)GetStorageMemorySizeLongMethod.Invoke(null, new object[] { texture });
            }
            catch
            {
                return 0L;
            }
        }

        static long GetAssetSize(Object asset)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                return 0L;
            }

            string absolutePath = Path.GetFullPath(assetPath);
            if (!File.Exists(absolutePath))
            {
                return 0L;
            }

            return new FileInfo(absolutePath).Length;
        }

        static string GetTextureDimensions(Texture texture)
        {
            if (texture is Texture2DArray arrayTexture)
            {
                return $"{arrayTexture.width} x {arrayTexture.height} x {arrayTexture.depth}";
            }

            return $"{texture.width} x {texture.height}";
        }

        static string GetTextureFormat(Texture texture)
        {
            if (texture is Texture2D texture2D)
            {
                return texture2D.format.ToString();
            }

            if (texture is Texture2DArray textureArray)
            {
                return textureArray.format.ToString();
            }

            return texture.GetType().Name;
        }

        static void DrawSettingsGroup(string title, System.Action drawContents)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            drawContents();
            EditorGUILayout.EndVertical();
        }

        void DrawUdonSharpHeader()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(targets);
            }
            else
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target);
            }
        }

        void DrawUdonSharpUtilities()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawUtilities(targets);
            }
            else
            {
                UdonSharpGUI.DrawUtilities(target);
            }
        }
    }
}
#endif