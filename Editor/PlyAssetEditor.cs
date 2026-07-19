#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(DefaultAsset), true)]
    [CanEditMultipleObjects]
    class PlyAssetEditor : UnityEditor.Editor
    {
        struct PlyInspectorInfo
        {
            public bool fileExists;
            public long fileSizeBytes;
            public int splatCount;
            public int vertexStride;
            public int attributeCount;
            public string missingRequiredAttributes;
            public SHBand availableShBand;
            public List<string> attributeLines;
            public string error;

            public bool IsGaussianSplat => string.IsNullOrEmpty(error) && string.IsNullOrEmpty(missingRequiredAttributes);
        }

        static readonly Type DefaultAssetInspectorType = Type.GetType("UnityEditor.DefaultAssetInspector, UnityEditor");
        static readonly string[] RequiredFloatAttributes =
        {
            "x",
            "y",
            "z",
            "f_dc_0",
            "f_dc_1",
            "f_dc_2",
            "opacity",
            "scale_0",
            "scale_1",
            "scale_2",
            "rot_0",
            "rot_1",
            "rot_2",
            "rot_3"
        };

        static bool _showAttributeList;

        UnityEditor.Editor _defaultInspector;
        string _outputSourceAssetPath;
        string _prefabOutputPath;

        void OnEnable()
        {
            if (DefaultAssetInspectorType != null)
            {
                _defaultInspector = CreateEditor(targets, DefaultAssetInspectorType);
            }
        }

        void OnDisable()
        {
            if (_defaultInspector != null)
            {
                DestroyImmediate(_defaultInspector);
                _defaultInspector = null;
            }
        }

        public override void OnInspectorGUI()
        {
            if (!TryGetSelectedPlyAssetPath(out string assetPath, out string absolutePath))
            {
                if (_defaultInspector != null)
                {
                    _defaultInspector.OnInspectorGUI();
                }

                return;
            }

            GUI.enabled = true;
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            GUI.backgroundColor = Color.white;

            EnsureDefaultOutputPath(assetPath);

            PlyInspectorInfo info = GatherInfo(absolutePath);

            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GSEditorText.T("Gaussian Splat PLY", "Gaussian Splat PLY"), EditorStyles.boldLabel);

            DrawInfoRow(GSEditorText.T("Asset Path", "アセットパス"), assetPath);
            if (info.fileExists)
            {
                DrawInfoRow(GSEditorText.T("File Size", "ファイルサイズ"), EditorUtility.FormatBytes(info.fileSizeBytes));
            }

            if (!string.IsNullOrEmpty(info.error))
            {
                EditorGUILayout.HelpBox(info.error, MessageType.Error);
            }
            else
            {
                DrawInfoRow(GSEditorText.T("Splats", "Splat 数"), info.splatCount.ToString("N0"));
                DrawInfoRow(GSEditorText.T("Vertex Stride", "頂点ストライド"), info.vertexStride + " bytes");
                DrawInfoRow(GSEditorText.T("Header Attributes", "ヘッダー属性"), info.attributeCount.ToString());
                DrawInfoRow(GSEditorText.T("Gaussian Splat", "Gaussian Splat"), info.IsGaussianSplat ? GSEditorText.T("Yes", "はい") : GSEditorText.T("No", "いいえ"));
                DrawInfoRow(GSEditorText.T("Available SH Band", "利用可能な SH バンド"), info.availableShBand.ToString());

                if (!string.IsNullOrEmpty(info.missingRequiredAttributes))
                {
                    EditorGUILayout.HelpBox(GSEditorText.T("Missing required float attributes: ", "必須 float 属性が不足しています: ") + info.missingRequiredAttributes, MessageType.Warning);
                }

                _showAttributeList = EditorGUILayout.Foldout(_showAttributeList, GSEditorText.T("Header Attributes", "ヘッダー属性"), true);
                if (_showAttributeList && info.attributeLines != null)
                {
                    for (int i = 0; i < info.attributeLines.Count; i++)
                    {
                        EditorGUILayout.LabelField(info.attributeLines[i], EditorStyles.miniLabel);
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(GSEditorText.T("Import", "インポート"), EditorStyles.boldLabel);
            _prefabOutputPath = EditorGUILayout.TextField(GSEditorText.T("Prefab Output", "Prefab 出力先"), _prefabOutputPath);

            string normalizedPrefabPath = NormalizePrefabAssetPath(_prefabOutputPath);
            if (!IsValidPrefabAssetPath(normalizedPrefabPath))
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Prefab Output must be a path under Assets/ and end with .prefab.",
                    "Prefab 出力先は Assets/ 配下で、.prefab で終わるパスにしてください。"), MessageType.Error);
            }
            else if (normalizedPrefabPath != _prefabOutputPath)
            {
                EditorGUILayout.HelpBox(GSEditorText.T("The output path will be normalized to ", "出力パスは次のように正規化されます: ") + normalizedPrefabPath, MessageType.Info);
            }

            EditorGUILayout.HelpBox(GSEditorText.T(
                "Import With Default Settings uses the same defaults as the package import wizard. Use Open Import Wizard for advanced settings.",
                "デフォルト設定でインポートは、パッケージのインポートウィザードと同じ既定値を使用します。詳細設定にはインポートウィザードを開いてください。"), MessageType.Info);

            using (new EditorGUI.DisabledScope(!info.IsGaussianSplat || !IsValidPrefabAssetPath(normalizedPrefabPath)))
            {
                if (GUILayout.Button(GSEditorText.T("Import With Default Settings", "デフォルト設定でインポート")))
                {
                    ImportSelectedPly(absolutePath, normalizedPrefabPath);
                }
            }

            if (GUILayout.Button(GSEditorText.T("Open Import Wizard", "インポートウィザードを開く")))
            {
                GaussianSplatting.Editor.Importers.GaussianSplatImportWizard.OpenWithSource(absolutePath);
            }

            EditorGUILayout.EndVertical();
        }

        bool TryGetSelectedPlyAssetPath(out string assetPath, out string absolutePath)
        {
            assetPath = null;
            absolutePath = null;

            if (targets == null || targets.Length != 1 || target == null)
            {
                return false;
            }

            assetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".ply", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            absolutePath = Path.GetFullPath(assetPath);
            return true;
        }

        void EnsureDefaultOutputPath(string assetPath)
        {
            if (_outputSourceAssetPath == assetPath && !string.IsNullOrEmpty(_prefabOutputPath))
            {
                return;
            }

            _outputSourceAssetPath = assetPath;
            _prefabOutputPath = NormalizePrefabAssetPath(Path.ChangeExtension(assetPath, ".prefab"));
        }

        static PlyInspectorInfo GatherInfo(string absolutePath)
        {
            PlyInspectorInfo info = new PlyInspectorInfo
            {
                fileExists = File.Exists(absolutePath),
                attributeLines = new List<string>()
            };

            if (!info.fileExists)
            {
                info.error = "PLY file does not exist on disk.";
                return info;
            }

            info.fileSizeBytes = new FileInfo(absolutePath).Length;

            try
            {
                PLYFileReader.ReadFileHeader(absolutePath, out info.splatCount, out info.vertexStride, out List<(string, PLYFileReader.ElementType)> attributes);
                info.attributeCount = attributes.Count;
                info.missingRequiredAttributes = GetMissingRequiredAttributes(attributes);
                info.availableShBand = InferAvailableShBand(attributes);
                info.attributeLines = attributes
                    .Select(attribute => attribute.Item2 + " " + attribute.Item1)
                    .ToList();
            }
            catch (Exception exception)
            {
                info.error = exception.Message;
            }

            return info;
        }

        static string GetMissingRequiredAttributes(List<(string, PLYFileReader.ElementType)> attributes)
        {
            List<string> missing = RequiredFloatAttributes
                .Where(requiredAttribute => !HasFloatAttribute(attributes, requiredAttribute))
                .ToList();

            if (missing.Count == 0)
            {
                return null;
            }

            return string.Join(", ", missing);
        }

        static bool HasFloatAttribute(List<(string, PLYFileReader.ElementType)> attributes, string attributeName)
        {
            return attributes.Any(attribute => attribute.Item1 == attributeName && attribute.Item2 == PLYFileReader.ElementType.Float);
        }

        static SHBand InferAvailableShBand(List<(string, PLYFileReader.ElementType)> attributes)
        {
            int coefficientTriplets = 0;
            for (int coefficient = 0; coefficient < 15; coefficient++)
            {
                bool hasTriplet = HasFloatAttribute(attributes, "f_rest_" + coefficient)
                    && HasFloatAttribute(attributes, "f_rest_" + (coefficient + 15))
                    && HasFloatAttribute(attributes, "f_rest_" + (coefficient + 30));

                if (!hasTriplet)
                {
                    continue;
                }

                coefficientTriplets++;
            }

            if (coefficientTriplets >= 15)
            {
                return SHBand.SH3;
            }

            if (coefficientTriplets >= 8)
            {
                return SHBand.SH2;
            }

            if (coefficientTriplets >= 3)
            {
                return SHBand.SH1;
            }

            return SHBand.SH0;
        }

        static string NormalizePrefabAssetPath(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
            {
                return string.Empty;
            }

            string normalizedPath = prefabPath.Replace('\\', '/');
            if (!normalizedPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = Path.ChangeExtension(normalizedPath, ".prefab").Replace('\\', '/');
            }

            return normalizedPath;
        }

        static bool IsValidPrefabAssetPath(string prefabPath)
        {
            return !string.IsNullOrEmpty(prefabPath)
                && prefabPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                && prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        static void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.SelectableLabel(value, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }

        static void ImportSelectedPly(string absolutePlyPath, string prefabAssetPath)
        {
            try
            {
                EditorUtility.DisplayProgressBar(
                    GSEditorText.T("PLY Import", "PLY インポート"),
                    GSEditorText.T("Importing ", "インポート中: ") + Path.GetFileName(absolutePlyPath),
                    0.0f);
                GaussianSplatting.Editor.Importers.GaussianSplatImportWizard.ImportWithDefaults(absolutePlyPath, prefabAssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                UnityEngine.Object importedAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabAssetPath);
                if (importedAsset != null)
                {
                    Selection.activeObject = importedAsset;
                    EditorGUIUtility.PingObject(importedAsset);
                }
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog(GSEditorText.T("PLY Import Failed", "PLY インポート失敗"), exception.Message, "OK");
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
#endif
