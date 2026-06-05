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

            DrawSettingsGroup("Sorting Settings", DrawSortingSettings);
            EditorGUILayout.Space();
            DrawSettingsGroup("Material Settings", DrawMaterialSettings);

            serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                GaussianSplatCombinedHierarchyBuilder.EnsureChunkHierarchy(sceneRenderer);
            }

            EditorGUILayout.Space();
            DrawUdonSharpUtilities();
        }

        void DrawSortingSettings()
        {
            EditorGUILayout.PropertyField(_renderingMode, new GUIContent("Rendering Mode"));
            EditorGUILayout.PropertyField(_cameraPositionQuantization, new GUIContent("Camera Position Quantization"));
            EditorGUILayout.PropertyField(_alwaysUpdate, new GUIContent("Always Update"));
            EditorGUILayout.IntSlider(_sortPassesPerFrame, 1, RadixSort.TotalSortPasses, new GUIContent("Sort Passes Per Frame"));
            EditorGUILayout.PropertyField(_splatRenderOrder, new GUIContent("Splat Render Order"));
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                EditorGUILayout.LabelField("Rendered Splat Count", sceneRenderer.GetCurrentRenderedSplatCount().ToString());
            }
        }

        void DrawMaterialSettings()
        {
            EditorGUILayout.IntSlider(_requestedSHBand, 0, 3, new GUIContent("Requested SH Band"));
            EditorGUILayout.PropertyField(_useVrcLightVolumes, new GUIContent("Use VRC Light Volumes"));
            using (new EditorGUI.DisabledScope(!_useVrcLightVolumes.boolValue))
            {
                EditorGUILayout.Slider(_lightVolumeIntensity, 0.0f, 10.0f, new GUIContent("Light Volume Intensity"));
            }

            EditorGUILayout.Space();
            DrawQualityPresetButtons();
            EditorGUILayout.PropertyField(_overrideMaterialProperties, new GUIContent("Override Material Properties"));
            using (new EditorGUI.DisabledScope(!_overrideMaterialProperties.boolValue))
            {
                EditorGUILayout.Slider(_gaussianScale, 0.0f, 2.0f, new GUIContent("Gaussian Scale"));
                EditorGUILayout.Slider(_thinThreshold, 0.0f, 1.0f, new GUIContent("Thinness Threshold"));
                EditorGUILayout.Slider(_antiAliasing, 0.0f, 5.0f, new GUIContent("Anti Aliasing"));
                EditorGUILayout.Slider(_log2MinScale, -20.0f, 10.0f, new GUIContent("Log2 Minimum Scale"));
                DrawLogSlider(_alphaCutoff, new GUIContent("Alpha Cutoff"), 0.005f, 0.3f);
                DrawLogSlider(_alphaCull, new GUIContent("Alpha Cull"), 0.005f, 0.3f);
                EditorGUILayout.Slider(_lodCull, 0.0f, 0.1f, new GUIContent("LOD Cull"));
                EditorGUILayout.Slider(_scaleCutoff, 0.0f, 100.0f, new GUIContent("Scale Cutoff"));
                EditorGUILayout.Slider(_exposure, 0.0f, 5.0f, new GUIContent("Exposure"));
                EditorGUILayout.Slider(_opacity, 0.0f, 5.0f, new GUIContent("Opacity"));
                EditorGUILayout.PropertyField(_oklchShift, new GUIContent("OKLCH Color Shift"));
                DrawMinFloatField(_gamma, new GUIContent("Gamma"), 0.001f);
            }
        }

        void DrawQualityPresetButtons()
        {
            EditorGUILayout.LabelField("Quality Preset", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Very Low")) ApplyQualityPreset(0.15f, 0.15f);
            if (GUILayout.Button("Low")) ApplyQualityPreset(0.07f, 0.1f);
            if (GUILayout.Button("Medium")) ApplyQualityPreset(0.04f, 0.04f);
            if (GUILayout.Button("High")) ApplyQualityPreset(0.01f, 0.01f);
            EditorGUILayout.EndHorizontal();
        }

        void ApplyQualityPreset(float cull, float cutoff)
        {
            _overrideMaterialProperties.boolValue = true;
            _alphaCull.floatValue = cull;
            _alphaCutoff.floatValue = cutoff;
        }

        static void DrawLogSlider(SerializedProperty property, GUIContent label, float minValue, float maxValue)
        {
            float clampedValue = Mathf.Clamp(property.floatValue, minValue, maxValue);
            float logMin = Mathf.Log(minValue);
            float logMax = Mathf.Log(maxValue);

            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float normalizedValue = Mathf.InverseLerp(logMin, logMax, Mathf.Log(clampedValue));
            float nextNormalizedValue = EditorGUILayout.Slider(label, normalizedValue, 0.0f, 1.0f);
            if (EditorGUI.EndChangeCheck())
            {
                property.floatValue = Mathf.Exp(Mathf.Lerp(logMin, logMax, nextNormalizedValue));
            }
            EditorGUI.showMixedValue = false;
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
