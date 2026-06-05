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
        SerializedProperty _cameraPositionQuantization;
        SerializedProperty _alwaysUpdate;
        SerializedProperty _sortPassesPerFrame;
        SerializedProperty _splatRenderOrder;
        SerializedProperty _renderingMode;

        SerializedProperty _overrideMaterialProperties;
        SerializedProperty _overrideRenderQueue;
        SerializedProperty _startRenderQueue;
        SerializedProperty _requestedSHBand;
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
            _alwaysUpdate = serializedObject.FindProperty("alwaysUpdate");
            _sortPassesPerFrame = serializedObject.FindProperty("sortPassesPerFrame");
            _splatRenderOrder = serializedObject.FindProperty("splatRenderOrder");
            _renderingMode = serializedObject.FindProperty("renderingMode");

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
                }
                else
                {
                    GaussianSplatCombinedHierarchyBuilder.EnsureChunkHierarchy(sceneRenderer);
                }
            }

            EditorGUILayout.Space();
            DrawUdonSharpUtilities();
        }

        void DrawRenderingSettings()
        {
            DrawRenderingModeField();
            EditorGUILayout.HelpBox(GSEditorText.T(
                "Combined mode is slightly slower than single splat. Rendering multiple splats requires separately transforming splats into world space and writing them into a combined set of render textures.",
                "統合モードは単体 Splat より少し低速です。複数の Splat を描画するには、各 Splat を個別にワールド空間へ変換し、統合された RenderTexture セットへ書き込む必要があります。"), MessageType.Info);
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer warningRenderer && !warningRenderer.IsCombinedRenderingMode() && CountActiveSceneSplats(warningRenderer) > 1)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Multiple Gaussian splats are active, but Rendering Mode is Single Splat. Only one splat will be rendered. Enable Combined rendering to render multiple active splats.",
                    "複数の Gaussian Splat が有効ですが、表示モードは単体です。描画されるのは 1 つだけです。複数を描画するには統合表示を有効にしてください。"), MessageType.Warning);
            }
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                EditorGUILayout.LabelField(GSEditorText.T("Rendered Splat Count", "描画スプラット数"), sceneRenderer.GetCurrentRenderedSplatCount().ToString());
            }
        }

        void DrawRenderingModeField()
        {
            string[] modeLabels =
            {
                GSEditorText.T("Single Splat", "単体"),
                GSEditorText.T("Combined", "統合")
            };
            EditorGUI.showMixedValue = _renderingMode.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int nextMode = EditorGUILayout.Popup(GSEditorText.T("Rendering Mode", "表示モード"), Mathf.Clamp(_renderingMode.enumValueIndex, 0, modeLabels.Length - 1), modeLabels);
            if (EditorGUI.EndChangeCheck())
            {
                _renderingMode.enumValueIndex = nextMode;
            }
            EditorGUI.showMixedValue = false;
        }

        static int CountActiveSceneSplats(GaussianSplatRenderer renderer)
        {
            int count = 0;
            foreach (GaussianSplatObject splat in Resources.FindObjectsOfTypeAll<GaussianSplatObject>())
            {
                if (splat != null && splat.gameObject.scene == renderer.gameObject.scene && !EditorUtility.IsPersistent(splat) && splat.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }
            return count;
        }

        void DrawSortingSettings()
        {
            EditorGUILayout.PropertyField(_cameraPositionQuantization, GSEditorText.C("Camera Position Quantization", "カメラ位置量子化"));
            EditorGUILayout.PropertyField(_alwaysUpdate, GSEditorText.C("Always Update", "常に更新"));
            EditorGUILayout.IntSlider(_sortPassesPerFrame, 1, RadixSort.TotalSortPasses, GSEditorText.C("Sort Passes Per Frame", "フレーム毎のソートパス数"));
            EditorGUILayout.PropertyField(_splatRenderOrder, GSEditorText.C("Splat Render Order", "スプラット描画順"));
        }

        void DrawMaterialSettings()
        {
            EditorGUILayout.IntSlider(_requestedSHBand, 0, 3, GSEditorText.C("Requested SH Band", "要求 SH バンド"));
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

        void DrawQualityPresetButtons()
        {
            EditorGUILayout.LabelField(GSEditorText.T("Quality Preset", "品質プリセット"), EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(GSEditorText.T("Very Low", "最低"))) ApplyQualityPreset(0.15f, 0.15f);
            if (GUILayout.Button(GSEditorText.T("Low", "低"))) ApplyQualityPreset(0.07f, 0.1f);
            if (GUILayout.Button(GSEditorText.T("Medium", "中"))) ApplyQualityPreset(0.04f, 0.04f);
            if (GUILayout.Button(GSEditorText.T("High", "高"))) ApplyQualityPreset(0.01f, 0.01f);
            EditorGUILayout.EndHorizontal();
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
