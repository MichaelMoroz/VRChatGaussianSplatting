#if UNITY_EDITOR
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Custom inspector for GaussianSplatObject. The component stores the imported splat data as arrays of
    // multi-hundred-MB Texture2D references (positions/colors/rotations/scales/sh) plus chunk-metadata textures.
    // Unity's DEFAULT inspector draws an object-field THUMBNAIL for each, which decodes every referenced texture
    // on selection - pulling gigabytes into memory and freezing the editor when these prefab assets are selected
    // or moved in the Project window. This editor draws only user display metadata + a header-only footprint
    // summary (no pixel decode) and lists the source textures read-only (name/dims/size, no thumbnails), so selecting the
    // asset loads nothing heavy.
    [CustomEditor(typeof(GaussianSplatObject))]
    [CanEditMultipleObjects]
    class GaussianSplatObjectEditor : UnityEditor.Editor
    {
        // Storage size from the texture header (dims/format/mips) - does NOT decode or upload pixel data.
        static readonly MethodInfo GetStorageMemorySizeLongMethod = typeof(UnityEditor.Editor).Assembly
            .GetType("UnityEditor.TextureUtil")
            ?.GetMethod("GetStorageMemorySizeLong", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Texture) }, null);

        bool _showSourceTextures;
        bool _showTextureSets;
        SerializedProperty _splatName;
        SerializedProperty _description;

        void OnEnable()
        {
            _splatName = serializedObject.FindProperty("splatName");
            _description = serializedObject.FindProperty("description");
        }

        public override void OnInspectorGUI()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(targets);
            }
            else
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target);
            }

            serializedObject.Update();
            DrawEditableMetadata();
            serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects)
            {
                EditorGUILayout.Space();
                DrawImportedDataSummary((GaussianSplatObject)target);
            }

            EditorGUILayout.Space();
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawUtilities(targets);
            }
            else
            {
                UdonSharpGUI.DrawUtilities(target);
            }
        }

        void DrawEditableMetadata()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GSEditorText.T("Display Metadata", "表示メタデータ"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_splatName, GSEditorText.C("Display Name", "表示名"));
            EditorGUILayout.PropertyField(_description, GSEditorText.C("Description", "説明"));
            EditorGUILayout.EndVertical();
        }

        void DrawImportedDataSummary(GaussianSplatObject lo)
        {
            GaussianSplatImporter.ImportMetadata metadata = TryParseMetadata(lo);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GSEditorText.T("Imported Data (read-only)", "インポート済みデータ（読み取り専用）"), EditorStyles.boldLabel);

            int posFiles = lo.positions != null ? lo.positions.Length : 0;
            int shFiles = lo.sh != null ? lo.sh.Length : 0;
            long storedSplats = SumInts(lo.fileSplatCounts);
            DrawInfoRow(GSEditorText.T("Import Mode", "インポートモード"), ImportModeLabel(lo, metadata));
            DrawInfoRow(GSEditorText.T("Renderable", "描画可能"), YesNo(lo.IsRenderable()));
            if (metadata != null)
            {
                DrawInfoRow(GSEditorText.T("Source", "ソース"), string.IsNullOrEmpty(metadata.sourcePath) ? "-" : metadata.sourcePath);
                DrawInfoRow(GSEditorText.T("Prefab", "Prefab"), string.IsNullOrEmpty(metadata.prefabPath) ? "-" : metadata.prefabPath);
            }
            DrawInfoRow(GSEditorText.T("Splat Count", "Splat 数"), lo.totalSplatCount.ToString("N0"));
            DrawInfoRow(GSEditorText.T("Stored Splat Texels", "保存済み Splat テクセル"), storedSplats > 0 ? storedSplats.ToString("N0") : "-");
            DrawInfoRow(GSEditorText.T("Texture Sets", "テクスチャセット"), posFiles.ToString("N0"));
            DrawInfoRow(GSEditorText.T("SH Texture Sets", "SH テクスチャセット"), shFiles.ToString("N0"));
            DrawInfoRow(GSEditorText.T("Chunk Count", "チャンク数"), lo.chunkCount.ToString("N0"));
            DrawInfoRow(GSEditorText.T("Chunk Size", "チャンクサイズ"), lo.chunkSize.ToString("N0"));
            DrawInfoRow(GSEditorText.T("Position Encoding", "位置エンコード"), lo.usePackedPositions ? "Packed RGBA32" : "RGBAFloat");
            DrawInfoRow(GSEditorText.T("Max SH Band", "最大 SH バンド"), "SH" + lo.GetMaxSHBand());
            DrawInfoRow(GSEditorText.T("SH Coefficients", "SH 係数"), ShCoeffSummary(lo));
            DrawInfoRow(GSEditorText.T("LOD Reused Splats", "LOD 再利用 Splat"), lo.GetLodReusePercent() + "%");
            DrawInfoRow(GSEditorText.T("Bounds Center", "境界中心"), FormatVector((lo.boundsMin + lo.boundsMax) * 0.5f));
            DrawInfoRow(GSEditorText.T("Bounds Size", "境界サイズ"), FormatVector(lo.boundsMax - lo.boundsMin));
            DrawInfoRow(GSEditorText.T("Bounds Min", "境界 Min"), FormatVector(lo.boundsMin));
            DrawInfoRow(GSEditorText.T("Bounds Max", "境界 Max"), FormatVector(lo.boundsMax));
            Texture2D pos0 = (lo.positions != null && lo.positions.Length > 0) ? lo.positions[0] : null;
            DrawInfoRow(GSEditorText.T("Position Resolution", "位置テクスチャ解像度"),
                pos0 != null ? $"{pos0.width} x {pos0.height}" : GSEditorText.T("Unknown", "不明"));
            if (lo.chunkBoundsMinTexture != null)
            {
                DrawInfoRow(GSEditorText.T("Chunk Metadata", "チャンクメタデータ"),
                    $"{lo.chunkBoundsMinTexture.width} x {lo.chunkBoundsMinTexture.height}, layout {FormatVector4(lo.chunkTextureLayout)}");
            }
            DrawInfoRow(GSEditorText.T("Estimated GPU Memory", "推定 GPU メモリ"), EditorUtility.FormatBytes(EstimateFootprint(lo)));

            EditorGUILayout.EndVertical();

            _showTextureSets = EditorGUILayout.Foldout(_showTextureSets, GSEditorText.T("Texture Sets", "テクスチャセット"), true);
            if (_showTextureSets)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawTextureSetRows(lo);
                EditorGUILayout.EndVertical();
            }

            // Read-only listing of the source textures (name/dims/size only - no object fields, no thumbnails,
            // so expanding this never decodes the textures).
            _showSourceTextures = EditorGUILayout.Foldout(_showSourceTextures, GSEditorText.T("Source Textures", "ソーステクスチャ"), true);
            if (_showSourceTextures)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawTextureArray(GSEditorText.T("Positions", "位置"), lo.positions);
                DrawTextureArray(GSEditorText.T("Colors", "色"), lo.colors);
                DrawTextureArray(GSEditorText.T("Rotations", "回転"), lo.rotations);
                DrawTextureArray(GSEditorText.T("Scales", "スケール"), lo.scales);
                DrawTextureArray(GSEditorText.T("SH", "SH"), lo.sh);
                DrawTextureRow(GSEditorText.T("Chunk Bounds Min", "チャンク境界 Min"), lo.chunkBoundsMinTexture);
                DrawTextureRow(GSEditorText.T("Chunk Bounds Max", "チャンク境界 Max"), lo.chunkBoundsMaxTexture);
                DrawTextureRow(GSEditorText.T("Chunk Range", "チャンクレンジ"), lo.chunkRangeTexture);
                EditorGUILayout.EndVertical();
            }
        }

        static GaussianSplatImporter.ImportMetadata TryParseMetadata(GaussianSplatObject lo)
        {
            try { return GaussianSplatImporter.ImportMetadata.FromJson(lo.importMetadataJson); }
            catch { return null; }
        }

        static string ImportModeLabel(GaussianSplatObject lo, GaussianSplatImporter.ImportMetadata metadata)
        {
            if (metadata != null && metadata.options.standalone)
            {
                return "Standalone";
            }
            return "LOD";
        }

        void DrawTextureSetRows(GaussianSplatObject lo)
        {
            int setCount = lo.positions != null ? lo.positions.Length : 0;
            if (setCount == 0)
            {
                EditorGUILayout.LabelField(GSEditorText.T("No texture sets.", "テクスチャセットがありません。"));
                return;
            }
            for (int i = 0; i < setCount; i++)
            {
                int splats = lo.fileSplatCounts != null && i < lo.fileSplatCounts.Length ? lo.fileSplatCounts[i] : 0;
                int shCoeff = lo.fileShCoeffCounts != null && i < lo.fileShCoeffCounts.Length ? lo.fileShCoeffCounts[i] : 0;
                Texture2D pos = lo.positions[i];
                string resolution = pos != null ? $"{pos.width}x{pos.height}" : "-";
                string value = $"{splats:N0} splats, {resolution}, {ShCoeffLabel(shCoeff)}, {EditorUtility.FormatBytes(TextureSetBytes(lo, i))}";
                DrawInfoRow("Set " + i, value);
            }
        }

        void DrawTextureArray(string label, Texture2D[] arr)
        {
            int n = arr != null ? arr.Length : 0;
            EditorGUILayout.LabelField(label, n + GSEditorText.T(" file(s)", " 個"));
            if (arr == null)
            {
                return;
            }
            EditorGUI.indentLevel++;
            for (int i = 0; i < arr.Length; i++)
            {
                DrawTextureRow($"[{i}]", arr[i]);
            }
            EditorGUI.indentLevel--;
        }

        void DrawTextureRow(string label, Texture t)
        {
            string value = t != null
                ? $"{t.name}  ({t.width}x{t.height} {(t as Texture2D)?.format})  {EditorUtility.FormatBytes(StorageBytes(t))}"
                : GSEditorText.T("(none)", "（なし）");
            DrawInfoRow(label, value);
        }

        static long TextureSetBytes(GaussianSplatObject lo, int index)
        {
            long total = 0;
            total += ArrayStorageBytes(lo.positions, index);
            total += ArrayStorageBytes(lo.colors, index);
            total += ArrayStorageBytes(lo.rotations, index);
            total += ArrayStorageBytes(lo.scales, index);
            total += ArrayStorageBytes(lo.sh, index);
            return total;
        }

        static long ArrayStorageBytes(Texture2D[] arr, int index)
        {
            return arr != null && index >= 0 && index < arr.Length ? StorageBytes(arr[index]) : 0L;
        }

        static long EstimateFootprint(GaussianSplatObject lo)
        {
            long total = 0;
            total += SumArray(lo.positions) + SumArray(lo.colors) + SumArray(lo.rotations) + SumArray(lo.scales) + SumArray(lo.sh);
            total += StorageBytes(lo.chunkBoundsMinTexture) + StorageBytes(lo.chunkBoundsMaxTexture) + StorageBytes(lo.chunkRangeTexture);
            return total;
        }

        static long SumArray(Texture2D[] arr)
        {
            long sum = 0;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    sum += StorageBytes(arr[i]);
                }
            }
            return sum;
        }

        static long SumInts(int[] arr)
        {
            long sum = 0;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    sum += Mathf.Max(0, arr[i]);
                }
            }
            return sum;
        }

        static string ShCoeffSummary(GaussianSplatObject lo)
        {
            if (lo.fileShCoeffCounts == null || lo.fileShCoeffCounts.Length == 0)
            {
                return "SH0 (0 coeff)";
            }
            int min = int.MaxValue;
            int max = 0;
            for (int i = 0; i < lo.fileShCoeffCounts.Length; i++)
            {
                int coeff = Mathf.Max(0, lo.fileShCoeffCounts[i]);
                min = Mathf.Min(min, coeff);
                max = Mathf.Max(max, coeff);
            }
            if (min == int.MaxValue || max == 0)
            {
                return "SH0 (0 coeff)";
            }
            return min == max ? ShCoeffLabel(max) : ShCoeffLabel(min) + " - " + ShCoeffLabel(max);
        }

        static string ShCoeffLabel(int coeff)
        {
            int band = coeff >= 15 ? 3 : (coeff >= 8 ? 2 : (coeff >= 3 ? 1 : 0));
            return "SH" + band + " (" + Mathf.Max(0, coeff) + " coeff)";
        }

        static string YesNo(bool value)
        {
            return value ? GSEditorText.T("Yes", "はい") : GSEditorText.T("No", "いいえ");
        }

        static string FormatVector(Vector3 v)
        {
            return $"{v.x:0.###}, {v.y:0.###}, {v.z:0.###}";
        }

        static string FormatVector4(Vector4 v)
        {
            return $"{v.x:0.###}, {v.y:0.###}, {v.z:0.###}, {v.w:0.###}";
        }

        static long StorageBytes(Texture t)
        {
            if (t == null || GetStorageMemorySizeLongMethod == null)
            {
                return 0L;
            }
            try { return (long)GetStorageMemorySizeLongMethod.Invoke(null, new object[] { t }); }
            catch { return 0L; }
        }

        static void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.SelectableLabel(value, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
