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

            DrawSettingsGroup(GSEditorText.T("References", "参照"), DrawReferenceFields);

            if (serializedObject.isEditingMultipleObjects)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "General info is shown for a single selected splat object.",
                    "一般情報は、単一の Splat オブジェクトを選択した場合に表示されます。"), MessageType.Info);
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
            EditorGUILayout.PropertyField(_gaussianSplatRenderer, GSEditorText.C("Gaussian Splat Renderer", "Gaussian Splat レンダラー"));
            EditorGUILayout.PropertyField(_sortedObject, GSEditorText.C("Sorted Object", "ソート済みオブジェクト"));
            EditorGUILayout.PropertyField(_sortedRenderer, GSEditorText.C("Sorted Renderer", "ソート済みレンダラー"));
        }

        static void DrawGeneralInfo(InspectorStats stats)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GSEditorText.T("General Info", "一般情報"), EditorStyles.boldLabel);

            if (stats.sortedRenderer == null)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "No sorted renderer was found for this splat object.",
                    "この Splat オブジェクトにソート済みレンダラーが見つかりません。"), MessageType.Info);
            }
            else
            {
                DrawInfoRow(GSEditorText.T("Sorted Renderer", "ソート済みレンダラー"), stats.sortedRenderer.name);
                DrawInfoRow(GSEditorText.T("Material Count", "マテリアル数"), stats.materialCount.ToString());
                DrawInfoRow(GSEditorText.T("Splat Materials", "Splat マテリアル"), stats.splatMaterialCount.ToString());
                DrawInfoRow(GSEditorText.T("Actual Splats", "実 Splat 数"), stats.actualSplatCount > 0 ? stats.actualSplatCount.ToString("N0") : GSEditorText.T("Unknown", "不明"));
                DrawInfoRow(GSEditorText.T("Max SH Band", "最大 SH バンド"), stats.maxShBand.ToString());
                DrawInfoRow(GSEditorText.T("Data Resolution", "データ解像度"), string.IsNullOrEmpty(stats.dataResolution) ? GSEditorText.T("Unknown", "不明") : stats.dataResolution);
                DrawInfoRow(GSEditorText.T("Precomputed Sorting", "事前計算ソート"), stats.usesPrecomputedSorting ? GSEditorText.T("Yes", "はい") : GSEditorText.T("No", "いいえ"));
                DrawInfoRow(GSEditorText.T("Unique Textures", "ユニークテクスチャ"), stats.textures.Count.ToString());
            }

            EditorGUILayout.EndVertical();
        }

        static void DrawTextureInfo(InspectorStats stats)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GSEditorText.T("Texture Footprint", "テクスチャ使用量"), EditorStyles.boldLabel);

            if (stats.textures.Count == 0)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "No textures were found on the shared materials for this splat object.",
                    "この Splat オブジェクトの共有マテリアルにテクスチャが見つかりません。"), MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            DrawInfoRow(GSEditorText.T("Estimated GPU Memory", "推定 GPU メモリ"), EditorUtility.FormatBytes(stats.totalGpuBytes));
            DrawInfoRow(GSEditorText.T("Runtime Memory", "ランタイムメモリ"), EditorUtility.FormatBytes(stats.totalRuntimeBytes));
            DrawInfoRow(GSEditorText.T("Asset Size", "アセットサイズ"), EditorUtility.FormatBytes(stats.totalAssetBytes));

            for (int i = 0; i < stats.textures.Count; ++i)
            {
                TextureStats texture = stats.textures[i];
                EditorGUILayout.Space(3f);
                EditorGUILayout.LabelField($"{texture.propertyName} ({texture.textureName})", EditorStyles.boldLabel);
                DrawInfoRow(GSEditorText.T("Dimensions", "寸法"), texture.dimensions);
                DrawInfoRow(GSEditorText.T("Format", "形式"), texture.format);
                DrawInfoRow(GSEditorText.T("Estimated GPU Memory", "推定 GPU メモリ"), EditorUtility.FormatBytes(texture.gpuBytes));
                DrawInfoRow(GSEditorText.T("Runtime Memory", "ランタイムメモリ"), EditorUtility.FormatBytes(texture.runtimeBytes));
                DrawInfoRow(GSEditorText.T("Asset Size", "アセットサイズ"), EditorUtility.FormatBytes(texture.assetBytes));
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

            return material.IsKeywordEnabled("_PRECOMPUTED_SORTING_ON");
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
