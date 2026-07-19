#if UNITY_EDITOR
using GaussianSplatting;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        const long VRC_WORLD_SIZE_WARNING_BYTES = 1200L * 1024L * 1024L;
        const float SPLAT_DATA_DOWNLOAD_COMPRESSION_ESTIMATE = 0.85f;

        SerializedProperty _cameraPositionQuantization;
        SerializedProperty _combinedLodSplatBudgetPC;
        SerializedProperty _combinedLodSplatBudgetAndroid;
        SerializedProperty _combinedLodTargetScale;
        SerializedProperty _combinedLodDirectionalBias;
        SerializedProperty _lodMaxSplatsPerPixel;
        SerializedProperty _startupQualityPreset;
        SerializedProperty _startupLodCapacity;
        SerializedProperty _debugDrawLodGrid;
        SerializedProperty _debugRenderOpaqueEllipsoids;
        SerializedProperty _debugDrawChunkBounds;
        SerializedProperty _debugDrawChunkCenterArea;

        SerializedProperty _overrideMaterialProperties;
        SerializedProperty _overrideRenderQueue;
        SerializedProperty _startRenderQueue;
        SerializedProperty _requestedSHBand;
        bool _showFusedObjectTable;
        SerializedProperty _gaussianScale;
        SerializedProperty _thinThreshold;
        SerializedProperty _antiAliasing;
        SerializedProperty _log2MinScale;
        SerializedProperty _alphaCutoff;
        SerializedProperty _alphaCull;
        SerializedProperty _lodCull;
        SerializedProperty _scaleCutoff;
        SerializedProperty _exposure;
        SerializedProperty _opacity;
        SerializedProperty _oklchShift;
        SerializedProperty _gamma;
        SerializedProperty _useVrcLightVolumes;
        SerializedProperty _lightVolumeIntensity;

        void OnEnable()
        {
            _cameraPositionQuantization = serializedObject.FindProperty("cameraPositionQuantization");
            _combinedLodSplatBudgetPC = serializedObject.FindProperty("combinedLodSplatBudgetPC");
            _combinedLodSplatBudgetAndroid = serializedObject.FindProperty("combinedLodSplatBudgetAndroid");
            _combinedLodTargetScale = serializedObject.FindProperty("combinedLodTargetScale");
            _combinedLodDirectionalBias = serializedObject.FindProperty("combinedLodDirectionalBias");
            _lodMaxSplatsPerPixel = serializedObject.FindProperty("lodMaxSplatsPerPixel");
            _startupQualityPreset = serializedObject.FindProperty("startupQualityPreset");
            _startupLodCapacity = serializedObject.FindProperty("startupLodCapacity");
            _debugDrawLodGrid = serializedObject.FindProperty("debugDrawLodGrid");
            _debugRenderOpaqueEllipsoids = serializedObject.FindProperty("debugRenderOpaqueEllipsoids");
            _debugDrawChunkBounds = serializedObject.FindProperty("debugDrawChunkBounds");
            _debugDrawChunkCenterArea = serializedObject.FindProperty("debugDrawChunkCenterArea");

            _overrideMaterialProperties = serializedObject.FindProperty("overrideMaterialProperties");
            _overrideRenderQueue = serializedObject.FindProperty("overrideRenderQueue");
            _startRenderQueue = serializedObject.FindProperty("startRenderQueue");
            _requestedSHBand = serializedObject.FindProperty("requestedSHBand");
            _gaussianScale = serializedObject.FindProperty("gaussianScale");
            _thinThreshold = serializedObject.FindProperty("thinThreshold");
            _antiAliasing = serializedObject.FindProperty("antiAliasing");
            _log2MinScale = serializedObject.FindProperty("log2MinScale");
            _alphaCutoff = serializedObject.FindProperty("alphaCutoff");
            _alphaCull = serializedObject.FindProperty("alphaCull");
            _lodCull = serializedObject.FindProperty("lodCull");
            _scaleCutoff = serializedObject.FindProperty("scaleCutoff");
            _exposure = serializedObject.FindProperty("exposure");
            _opacity = serializedObject.FindProperty("opacity");
            _oklchShift = serializedObject.FindProperty("oklchShift");
            _gamma = serializedObject.FindProperty("gamma");
            _useVrcLightVolumes = serializedObject.FindProperty("useVrcLightVolumes");
            _lightVolumeIntensity = serializedObject.FindProperty("lightVolumeIntensity");
        }

        public override void OnInspectorGUI()
        {
            DrawUdonSharpHeader();

            serializedObject.Update();

            DrawSettingsGroup(GSEditorText.T("Rendering Settings", "表示設定"), DrawRenderingSettings);
            EditorGUILayout.Space();
            DrawSettingsGroup(GSEditorText.T("Sorting Settings", "ソート設定"), DrawSortingSettings);
            EditorGUILayout.Space();
            DrawSettingsGroup(GSEditorText.T("Material Settings", "マテリアル設定"), DrawMaterialSettings);

            bool changed = serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                if (changed)
                {
                    sceneRenderer.RefreshEditorResourcesAndVisibility();
                    sceneRenderer.ApplyEditorDebugRenderingModeNow();
                }
                else
                {
                    GaussianSplatCombinedHierarchyBuilder.EnsureChunkHierarchy(sceneRenderer);
                    sceneRenderer.ApplyEditorDebugRenderingModeNow();
                }
            }

            EditorGUILayout.Space();
            DrawUdonSharpUtilities();
        }

        void DrawRenderingSettings()
        {
            bool hasActiveLodObjects = !serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRendererWithLod && CountActiveSceneLODObjects(sceneRendererWithLod) > 0;
            if (hasActiveLodObjects)
            {
                DrawCombinedLodBudgetField();
                EditorGUILayout.PropertyField(_debugDrawLodGrid, GSEditorText.C("Debug LOD", "LOD をデバッグ表示"));
            }
            EditorGUILayout.PropertyField(_debugRenderOpaqueEllipsoids, GSEditorText.C("Debug Opaque Ellipsoids", "不透明楕円体をデバッグ表示"));
            EditorGUILayout.PropertyField(_debugDrawChunkBounds, GSEditorText.C("Debug Chunk Bounds", "チャンク境界をデバッグ表示"));
            EditorGUILayout.PropertyField(_debugDrawChunkCenterArea, GSEditorText.C("Debug Chunk Center+Area", "チャンク重心+面積をデバッグ表示"));
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                EditorGUILayout.LabelField(GSEditorText.T("Rendered Splat Count", "描画スプラット数"), sceneRenderer.GetCurrentRenderedSplatCount().ToString());
                EditorGUILayout.LabelField(GSEditorText.T("Baked Splat Count", "ベイク済みスプラット数"), sceneRenderer.GetTotalBakedSplatCount().ToString("N0"));
                long splatDataBytes = sceneRenderer.GetBakedSplatDataBytes();
                if (splatDataBytes > 0)
                {
                    // Build/download is an LZ4 asset bundle; the high-entropy RGBA32 splat textures compress to
                    // ~0.85x (measured on a 4.5M-splat build: 81.7 MB uncompressed source -> ~70 MB in the .vrcw).
                    long compressedBytes = (long)(splatDataBytes * SPLAT_DATA_DOWNLOAD_COMPRESSION_ESTIMATE);
                    EditorGUILayout.LabelField(GSEditorText.T("Splat Data (download est.)", "スプラットデータ (DL推定)"),
                        EditorUtility.FormatBytes(compressedBytes) + " (~" + EditorUtility.FormatBytes(splatDataBytes) + " raw)");
                    if (compressedBytes > VRC_WORLD_SIZE_WARNING_BYTES)
                    {
                        EditorGUILayout.HelpBox(
                            "Estimated splat data alone exceeds the 1.2 GB VRChat world size limit. Reduce baked splat data before uploading.",
                            MessageType.Warning);
                    }
                }
                int readbackCount = sceneRenderer.GetEditorReadbackRenderedSplatCount();
                int reservedCount = sceneRenderer.GetEditorReadbackReservedSplatCount();
                if (reservedCount > 0)
                {
                    EditorGUILayout.LabelField(GSEditorText.T("Editor Readback Splat Count", "エディタ読み戻しスプラット数"), readbackCount + " / " + reservedCount);
                    EditorGUILayout.LabelField(GSEditorText.T("Editor Readback Log2 Alpha", "エディタ読み戻し Log2 アルファ"), sceneRenderer.GetEditorReadbackAlpha().ToString("0.###"));
                }
            }
        }

        void DrawCombinedLodBudgetField()
        {
            if (_combinedLodSplatBudgetPC != null)
            {
                EditorGUI.showMixedValue = _combinedLodSplatBudgetPC.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextPcBudget = EditorGUILayout.IntField(GSEditorText.T("Combined LOD PC Cap", "統合 LOD PC 上限"), _combinedLodSplatBudgetPC.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodSplatBudgetPC.intValue = Mathf.Max(0, nextPcBudget);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_combinedLodSplatBudgetAndroid != null)
            {
                EditorGUI.showMixedValue = _combinedLodSplatBudgetAndroid.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextAndroidBudget = EditorGUILayout.IntField(GSEditorText.T("Combined LOD Android Cap", "統合 LOD Android 上限"), _combinedLodSplatBudgetAndroid.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodSplatBudgetAndroid.intValue = Mathf.Max(0, nextAndroidBudget);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_combinedLodTargetScale != null)
            {
                EditorGUI.showMixedValue = _combinedLodTargetScale.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextTargetScale = EditorGUILayout.Slider(GSEditorText.T("Combined LOD Target Scale", "統合 LOD 目標倍率"), _combinedLodTargetScale.floatValue > 0.0f ? _combinedLodTargetScale.floatValue : 0.95f, 0.1f, 1.0f);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodTargetScale.floatValue = Mathf.Clamp(nextTargetScale, 0.1f, 1.0f);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_combinedLodDirectionalBias != null)
            {
                EditorGUI.showMixedValue = _combinedLodDirectionalBias.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextDirectionalBias = EditorGUILayout.Slider(GSEditorText.T("Combined LOD Directional Bias", "統合 LOD 方向バイアス"), _combinedLodDirectionalBias.floatValue > 0.0f ? _combinedLodDirectionalBias.floatValue : 2.0f, 1.0f, 16.0f);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodDirectionalBias.floatValue = Mathf.Clamp(nextDirectionalBias, 1.0f, 16.0f);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_lodMaxSplatsPerPixel != null)
            {
                EditorGUI.showMixedValue = _lodMaxSplatsPerPixel.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextMaxPerPixel = EditorGUILayout.Slider(GSEditorText.T("LOD Max Splats / Pixel", "LOD 最大スプラット/ピクセル"), Mathf.Max(0.0f, _lodMaxSplatsPerPixel.floatValue), 0.0f, 4.0f);
                if (EditorGUI.EndChangeCheck())
                {
                    _lodMaxSplatsPerPixel.floatValue = Mathf.Max(0.0f, nextMaxPerPixel);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_startupQualityPreset != null)
            {
                // Index 0 = "Keep Inspector Settings" maps to the stored value -1; indices 1..4 map to 0..3.
                string[] startupLabels = { GSEditorText.T("Keep Inspector Settings", "インスペクター設定を維持"), GSEditorText.T("Very Low", "最低"), GSEditorText.T("Low", "低"), GSEditorText.T("Medium", "中"), GSEditorText.T("High", "高") };
                EditorGUI.showMixedValue = _startupQualityPreset.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextStartup = EditorGUILayout.Popup(GSEditorText.T("Startup Quality", "起動時の品質"), Mathf.Clamp(_startupQualityPreset.intValue + 1, 0, startupLabels.Length - 1), startupLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    _startupQualityPreset.intValue = nextStartup - 1;
                }
                EditorGUI.showMixedValue = false;
            }
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                if (_startupLodCapacity != null)
                {
                    // Startup LOD capacity as a fraction of THIS platform's cap (scales per-platform). Used when
                    // Startup Quality = "Keep Inspector Settings". Shows the resolved count for the editor's
                    // platform (PC cap) for reference.
                    EditorGUI.showMixedValue = _startupLodCapacity.hasMultipleDifferentValues;
                    EditorGUI.BeginChangeCheck();
                    float nextCapacity = EditorGUILayout.Slider(GSEditorText.T("Startup LOD Capacity", "起動時 LOD 容量"), Mathf.Clamp01(_startupLodCapacity.floatValue), 0.0f, 1.0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _startupLodCapacity.floatValue = Mathf.Clamp01(nextCapacity);
                    }
                    EditorGUI.showMixedValue = false;
                    int sliderMin = sceneRenderer.GetCombinedLodSplatBudgetSliderMin();
                    int sliderMax = sceneRenderer.GetCombinedLodSplatBudgetSliderMax();
                    int resolved = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(_startupLodCapacity.floatValue) * sliderMax), sliderMin, sliderMax);
                    EditorGUILayout.LabelField(" ", GSEditorText.T("= " + resolved.ToString("N0") + " splats (PC cap)", "= " + resolved.ToString("N0") + " スプラット (PC 上限)"));
                }
                int effectiveBudget = sceneRenderer.GetEffectiveCombinedLodSplatBudget();
                int targetBudget = effectiveBudget > 0 ? Mathf.FloorToInt(effectiveBudget * sceneRenderer.GetEffectiveCombinedLodTargetScale()) : 0;
                string effective = effectiveBudget == 0 ? GSEditorText.T("No cap", "上限なし") : effectiveBudget.ToString();
                string target = effectiveBudget == 0 ? GSEditorText.T("No cap", "上限なし") : targetBudget.ToString();
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Editor previews use the PC cap. Android builds use the Android cap. 0 disables the cap. Effective cap: " + effective + ". LOD selection target: " + target + ".",
                    "エディタプレビューでは PC 上限を使用します。Android ビルドでは Android 上限を使用します。0 は上限なしです。現在の上限: " + effective + "。LOD 選択目標: " + target + "。"), MessageType.None);
            }
        }

        static int CountActiveSceneLODObjects(GaussianSplatRenderer renderer)
        {
            int count = 0;
            foreach (GaussianSplatObject lodObject in Resources.FindObjectsOfTypeAll<GaussianSplatObject>())
            {
                if (lodObject != null && lodObject.gameObject.scene == renderer.gameObject.scene && !EditorUtility.IsPersistent(lodObject) && lodObject.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }
            return count;
        }

        void DrawSortingSettings()
        {
            EditorGUILayout.PropertyField(_cameraPositionQuantization, GSEditorText.C("Camera Position Quantization", "カメラ位置量子化"));
        }

        void DrawMaterialSettings()
        {
            EditorGUILayout.IntSlider(_requestedSHBand, 0, 3, GSEditorText.C("Requested SH Band", "要求 SH バンド"));
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer shRenderer)
            {
                GaussianSplatCombiner shCombiner = shRenderer.GetCombiner();
                int droppedSh = shCombiner != null ? shCombiner.GetFusedShDroppedObjectCount() : 0;
                if (droppedSh > 0)
                {
                    EditorGUILayout.HelpBox(GSEditorText.T(
                        $"Spherical harmonics were dropped for {droppedSh} object(s): the scene's total SH exceeds the single fused SH texture cap (16384² texels). Those splats render without view-dependent color regardless of this setting. Lower the SH band on some splats, reduce splat counts, or split the scene.",
                        $"{droppedSh} 個のオブジェクトの球面調和が破棄されました: シーン全体の SH が単一の統合 SH テクスチャ上限 (16384² テクセル) を超えています。該当スプラットはこの設定に関わらず視点依存色なしで描画されます。一部スプラットの SH バンドを下げる、スプラット数を減らす、またはシーンを分割してください。"),
                        MessageType.Warning);
                }
                DrawFusedObjectTable(shCombiner);
            }
            EditorGUILayout.PropertyField(_useVrcLightVolumes, GSEditorText.C("Use VRC Light Volumes", "VRC Light Volumes を使用"));
            using (new EditorGUI.DisabledScope(!_useVrcLightVolumes.boolValue))
            {
                EditorGUILayout.Slider(_lightVolumeIntensity, 0.0f, 10.0f, GSEditorText.C("Light Volume Intensity", "ライトボリューム強度"));
            }

            EditorGUILayout.Space();
            DrawQualityPresetButtons();
            EditorGUILayout.PropertyField(_overrideRenderQueue, GSEditorText.C("Override Render Queue", "レンダーキューを上書き"));
            using (new EditorGUI.DisabledScope(!_overrideRenderQueue.boolValue))
            {
                EditorGUILayout.IntSlider(_startRenderQueue, 2000, 5000, GSEditorText.C("Start Render Queue", "開始レンダーキュー"));
            }
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_overrideMaterialProperties, GSEditorText.C("Override Material Properties", "マテリアル設定を上書き"));
            using (new EditorGUI.DisabledScope(!_overrideMaterialProperties.boolValue))
            {
                EditorGUILayout.Slider(_gaussianScale, 0.0f, 2.0f, GSEditorText.C("Gaussian Scale", "ガウススケール"));
                EditorGUILayout.Slider(_thinThreshold, 0.0f, 1.0f, GSEditorText.C("Thinness Threshold", "薄さしきい値"));
                EditorGUILayout.Slider(_antiAliasing, 0.0f, 5.0f, GSEditorText.C("Anti Aliasing", "アンチエイリアス"));
                EditorGUILayout.Slider(_log2MinScale, -20.0f, 10.0f, GSEditorText.C("Log2 Minimum Scale", "Log2 最小スケール"));
                EditorGUILayout.Slider(_alphaCutoff, 0.0f, 1.0f, GSEditorText.C("Alpha Cutoff", "アルファカットオフ"));
                EditorGUILayout.Slider(_alphaCull, 0.0f, 1.0f, GSEditorText.C("Alpha Cull", "アルファカリング"));
                EditorGUILayout.Slider(_lodCull, 0.0f, 0.1f, GSEditorText.C("LOD Cull", "LOD カリング"));
                EditorGUILayout.Slider(_scaleCutoff, 0.0f, 100.0f, GSEditorText.C("Scale Cutoff", "スケールカットオフ"));
                EditorGUILayout.Slider(_exposure, 0.0f, 5.0f, GSEditorText.C("Exposure", "露出"));
                EditorGUILayout.Slider(_opacity, 0.0f, 5.0f, GSEditorText.C("Opacity", "不透明度"));
                EditorGUILayout.PropertyField(_oklchShift, GSEditorText.C("OKLCH Color Shift", "OKLCH 色シフト"));
                DrawMinFloatField(_gamma, GSEditorText.C("Gamma", "ガンマ"), 0.001f);
            }
        }

        static readonly GUILayoutOption[] _colNum = { GUILayout.Width(64) };
        static readonly GUILayoutOption[] _colShTex = { GUILayout.Width(110) };
        static readonly GUILayoutOption[] _colFlag = { GUILayout.Width(72) };

        static void DrawFusedTableRow(string c0, string active, string splats, string files, string chunks, string band, string shTex, string flag, GUIStyle style)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(c0, style, GUILayout.MinWidth(120));
                EditorGUILayout.LabelField(active, style, _colFlag);
                EditorGUILayout.LabelField(splats, style, _colNum);
                EditorGUILayout.LabelField(files, style, _colNum);
                EditorGUILayout.LabelField(chunks, style, _colNum);
                EditorGUILayout.LabelField(band, style, _colNum);
                EditorGUILayout.LabelField(shTex, style, _colShTex);
                EditorGUILayout.LabelField(flag, style, _colFlag);
            }
        }

        // Debug table of every fused object's params (splats / files / chunks / SH), including which objects had
        // their SH dropped because the scene's total SH exceeds the single fused SH texture (GaussianSplatFuse cap).
        void DrawFusedObjectTable(GaussianSplatCombiner combiner)
        {
            if (combiner == null)
            {
                return;
            }
            var rows = combiner.GetFusedObjectDebugRows();
            if (rows == null || rows.Count == 0)
            {
                return;
            }
            _showFusedObjectTable = EditorGUILayout.Foldout(_showFusedObjectTable, GSEditorText.T("Fused Objects (debug)", "統合オブジェクト (デバッグ)"), true);
            if (!_showFusedObjectTable)
            {
                return;
            }
            long cap = GaussianSplatFuse.MaxFusedShTexels;
            long shUsed = 0, shRequested = 0, splatTotal = 0;
            foreach (var r in rows)
            {
                shRequested += r.shTexels;
                if (!r.shDropped) shUsed += r.shTexels;
                splatTotal += r.splats;
            }
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(GSEditorText.T("Objects / Splats", "オブジェクト / スプラット"), $"{rows.Count}  /  {splatTotal:N0}");
                EditorGUILayout.LabelField(GSEditorText.T("Fused SH used / cap", "統合 SH 使用 / 上限"), $"{shUsed:N0} / {cap:N0}  ({(cap > 0 ? 100.0 * shUsed / cap : 0):F1}%)");
                if (shRequested != shUsed)
                {
                    EditorGUILayout.LabelField(GSEditorText.T("SH requested (incl. dropped)", "SH 要求 (破棄含む)"), $"{shRequested:N0}  ({(cap > 0 ? 100.0 * shRequested / cap : 0):F1}%)");
                }
                EditorGUILayout.Space(2);
                var head = EditorStyles.miniBoldLabel;
                var cell = EditorStyles.miniLabel;
                DrawFusedTableRow("Object", "Active", "Splats", "Files", "Chunks", "Band", "SH texels", "Dropped", head);
                foreach (var r in rows)
                {
                    int band = r.shCoeff >= 15 ? 3 : (r.shCoeff >= 8 ? 2 : (r.shCoeff >= 3 ? 1 : 0));
                    string shTex = r.shCoeff > 0 ? $"{r.shTexels:N0} ({(cap > 0 ? 100.0 * r.shTexels / cap : 0):F1}%)" : "-";
                    string flag = r.shDropped ? "DROPPED" : (r.shCoeff > 0 ? "" : "no SH");
                    DrawFusedTableRow(r.name, r.active ? "yes" : "no", $"{r.splats:N0}", r.files.ToString(), r.chunks.ToString(), band.ToString(), shTex, flag, cell);
                }
            }
        }

        void DrawQualityPresetButtons()
        {
            EditorGUILayout.LabelField(GSEditorText.T("Quality Preset", "品質プリセット"), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(GSEditorText.T("Very Low", "最低"))) ApplyQualityPreset(0.15f, 0.15f);
                if (GUILayout.Button(GSEditorText.T("Low", "低"))) ApplyQualityPreset(0.07f, 0.1f);
                if (GUILayout.Button(GSEditorText.T("Medium", "中"))) ApplyQualityPreset(0.04f, 0.04f);
                if (GUILayout.Button(GSEditorText.T("High", "高"))) ApplyQualityPreset(0.01f, 0.01f);
            }
        }

        void ApplyQualityPreset(float cull, float cutoff)
        {
            _overrideMaterialProperties.boolValue = true;
            _alphaCull.floatValue = cull;
            _alphaCutoff.floatValue = cutoff;
        }

        static void DrawMinFloatField(SerializedProperty property, GUIContent label, float minValue)
        {
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float nextValue = EditorGUILayout.FloatField(label, property.floatValue);
            if (EditorGUI.EndChangeCheck())
            {
                property.floatValue = Mathf.Max(minValue, nextValue);
            }
            EditorGUI.showMixedValue = false;
        }

        static void DrawSettingsGroup(string title, System.Action drawContents)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                drawContents();
            }
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
