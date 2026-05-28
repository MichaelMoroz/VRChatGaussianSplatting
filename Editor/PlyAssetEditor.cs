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
            EditorGUILayout.LabelField("Gaussian Splat PLY", EditorStyles.boldLabel);

            DrawInfoRow("Asset Path", assetPath);
            if (info.fileExists)
            {
                DrawInfoRow("File Size", EditorUtility.FormatBytes(info.fileSizeBytes));
            }

            if (!string.IsNullOrEmpty(info.error))
            {
                EditorGUILayout.HelpBox(info.error, MessageType.Error);
            }
            else
            {
                DrawInfoRow("Splats", info.splatCount.ToString("N0"));
                DrawInfoRow("Vertex Stride", info.vertexStride + " bytes");
                DrawInfoRow("Header Attributes", info.attributeCount.ToString());
                DrawInfoRow("Gaussian Splat", info.IsGaussianSplat ? "Yes" : "No");
                DrawInfoRow("Available SH Band", info.availableShBand.ToString());

                if (!string.IsNullOrEmpty(info.missingRequiredAttributes))
                {
                    EditorGUILayout.HelpBox("Missing required float attributes: " + info.missingRequiredAttributes, MessageType.Warning);
                }

                _showAttributeList = EditorGUILayout.Foldout(_showAttributeList, "Header Attributes", true);
                if (_showAttributeList && info.attributeLines != null)
                {
                    for (int i = 0; i < info.attributeLines.Count; i++)
                    {
                        EditorGUILayout.LabelField(info.attributeLines[i], EditorStyles.miniLabel);
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Import", EditorStyles.boldLabel);
            _prefabOutputPath = EditorGUILayout.TextField("Prefab Output", _prefabOutputPath);

            string normalizedPrefabPath = NormalizePrefabAssetPath(_prefabOutputPath);
            if (!IsValidPrefabAssetPath(normalizedPrefabPath))
            {
                EditorGUILayout.HelpBox("Prefab Output must be a path under Assets/ and end with .prefab.", MessageType.Error);
            }
            else if (normalizedPrefabPath != _prefabOutputPath)
            {
                EditorGUILayout.HelpBox("The output path will be normalized to " + normalizedPrefabPath, MessageType.Info);
            }

            EditorGUILayout.HelpBox("Import With Default Settings uses the same defaults as the package import wizard. Use Open Import Wizard for advanced settings.", MessageType.Info);

            using (new EditorGUI.DisabledScope(!info.IsGaussianSplat || !IsValidPrefabAssetPath(normalizedPrefabPath)))
            {
                if (GUILayout.Button("Import With Default Settings"))
                {
                    ImportSelectedPly(absolutePath, normalizedPrefabPath);
                }
            }

            if (GUILayout.Button("Open Import Wizard"))
            {
                GaussianSplatting.Editor.Importers.PlyImportWizard.OpenWithPly(absolutePath);
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
                EditorUtility.DisplayProgressBar("PLY Import", "Importing " + Path.GetFileName(absolutePlyPath), 0.0f);
                GaussianSplatting.Editor.Importers.PlyImportWizard.ImportWithDefaults(absolutePlyPath, prefabAssetPath);
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
                EditorUtility.DisplayDialog("PLY Import Failed", exception.Message, "OK");
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