#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatUiBuilder
    {
        const float DefaultAlphaCutoff = 0.04f;
        const float DefaultAlphaCull = 0.04f;
        const float DefaultLODSplatCapSlider = 0.89164376f;
        const string UiFontAssetPath = "Assets/VRChatGaussianSplatting/Resources/Fonts/NotoSansJP-VF.ttf";
        const string UiTextMeshProFontAssetPath = "Assets/VRChatGaussianSplatting/Resources/Fonts/NotoSansJP-VF TMP.asset";
        const string UiMaterialFolderPath = "Assets/VRChatGaussianSplatting/Resources/Materials";
        const string SupersampledUiMaterialAssetPath = UiMaterialFolderPath + "/GaussianSplatUISupersampled.mat";
        const string VrChatSupersampledUiShaderName = "VRChat/Mobile/Worlds/Supersampled UI";
        const string TmpTextShaderName = "TextMeshPro/Mobile/Distance Field SSD";
        const int UiTextMeshProPointSize = 64;
        const int UiTextMeshProAtlasPadding = 8;
        const int UiTextMeshProAtlasSize = 2048;
        const UnityEngine.TextCore.LowLevel.GlyphRenderMode UiTextMeshProGlyphRenderMode = UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED;
        const float UiTextMeshProBoldStyle = 1.5f;

        static Type _cachedVrChatUiShapeType;
        static Material _cachedSupersampledUiMaterial;
        static TMP_FontAsset _cachedUiTextMeshProFont;
        static bool _autoRefreshQueued = true;

        [InitializeOnLoadMethod]
        static void RegisterAutoRefresh()
        {
            EditorApplication.hierarchyChanged -= QueueAutoRefresh;
            EditorApplication.hierarchyChanged += QueueAutoRefresh;
            EditorApplication.update -= ProcessAutoRefresh;
            EditorApplication.update += ProcessAutoRefresh;
        }

        static void QueueAutoRefresh() { _autoRefreshQueued = true; }

        static void ProcessAutoRefresh()
        {
            if (Application.isPlaying || !_autoRefreshQueued) return;
            _autoRefreshQueued = false;
            GaussianSplatRenderer[] renderers = Resources.FindObjectsOfTypeAll<GaussianSplatRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                GaussianSplatRenderer renderer = renderers[i];
                if (renderer == null || renderer != GaussianSplatRenderer.FindExistingSceneRenderer(renderer.gameObject.scene) || EditorUtility.IsPersistent(renderer) || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(renderer.gameObject.scene) || renderer.transform.Find("Gaussian Splat UI") != null) continue;
                if (SceneHasSplatContent(renderer.gameObject.scene))
                {
                    Generate(renderer, false);
                }
            }
        }

        static bool IsAutoUiSource(Component component, UnityEngine.SceneManagement.Scene scene)
        {
            return component != null
                && !EditorUtility.IsPersistent(component)
                && component.gameObject.scene == scene
                && component.gameObject.activeInHierarchy;
        }

        static bool SceneHasSplatContent(UnityEngine.SceneManagement.Scene scene)
        {
            GaussianSplatObject[] splats = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
            for (int i = 0; i < splats.Length; i++)
            {
                if (IsAutoUiSource(splats[i], scene))
                {
                    return true;
                }
            }
            return false;
        }

        static bool SceneHasActiveLODContent(UnityEngine.SceneManagement.Scene scene)
        {
            GaussianSplatObject[] lodObjects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
            for (int i = 0; i < lodObjects.Length; i++)
            {
                GaussianSplatObject lodObject = lodObjects[i];
                if (IsAutoUiSource(lodObject, scene) && lodObject.IsRenderable())
                {
                    return true;
                }
            }
            return false;
        }

        static string GetUiTextCharacterSet()
        {
            HashSet<char> characters = new HashSet<char>();
            for (int code = 32; code <= 126; code++)
            {
                characters.Add((char)code);
            }

            const string localizedCharacters = "表示モード統合単体現在のスプラットなし再ソートするカメラ移動量をフレームに分散毎マテリアル設定バンド共有光源強度アンチエイリアスアルファカットオフ低いほど高品質言語上へ下開発有効中日本語オンオフ示ライトボリュームガウスケールカリング減少距離最";
            for (int i = 0; i < localizedCharacters.Length; i++)
            {
                characters.Add(localizedCharacters[i]);
            }

            char[] result = new char[characters.Count];
            characters.CopyTo(result);
            Array.Sort(result);
            return new string(result);
        }

        static bool EnsureUiTextCharacters(TMP_FontAsset fontAsset)
        {
            if (fontAsset == null)
            {
                return false;
            }

            int previousCharacterCount = fontAsset.characterTable != null ? fontAsset.characterTable.Count : 0;
            string missingCharacters;
            fontAsset.TryAddCharacters(GetUiTextCharacterSet(), out missingCharacters);
            bool changed = (fontAsset.characterTable != null ? fontAsset.characterTable.Count : 0) != previousCharacterCount;
            if (changed && fontAsset.atlasTextures != null)
            {
                for (int atlasIndex = 0; atlasIndex < fontAsset.atlasTextures.Length; atlasIndex++)
                {
                    if (fontAsset.atlasTextures[atlasIndex] != null)
                    {
                        EditorUtility.SetDirty(fontAsset.atlasTextures[atlasIndex]);
                    }
                }
            }

            return changed;
        }

        internal static void Generate(GaussianSplatRenderer renderer, bool select = true)
        {
            if (renderer == null)
            {
                return;
            }

            EnsureEventSystemExists();

            // The gallery list, master lock, and custom description are author-set data that live only on the UI
            // component. Regenerating destroys that component, so carry those values across to the rebuilt UI.
            Transform existingUi = renderer.transform.Find("Gaussian Splat UI");
            GaussianSplatObject[] preservedGalleryObjects = null;
            bool preservedGalleryEnabled = true;
            bool preservedGalleryMasterLock = true;
            string preservedCustomSubtitleEnglish = null;
            string preservedCustomSubtitleJapanese = null;
            int preservedGallerySelectedIndex = 0;
            if (existingUi != null)
            {
                GaussianSplatRendererUI existingGeneratedUi = existingUi.GetComponentInChildren<GaussianSplatRendererUI>(true);
                if (existingGeneratedUi != null)
                {
                    preservedGalleryObjects = existingGeneratedUi.galleryObjects;
                    preservedGalleryEnabled = existingGeneratedUi.galleryEnabled;
                    preservedGalleryMasterLock = existingGeneratedUi.galleryMasterLock;
                    preservedCustomSubtitleEnglish = existingGeneratedUi.customSubtitleEnglish;
                    preservedCustomSubtitleJapanese = existingGeneratedUi.customSubtitleJapanese;
                    preservedGallerySelectedIndex = ReadGallerySelectedIndex(existingGeneratedUi);
                }
                Undo.DestroyObjectImmediate(existingUi.gameObject);
            }

            GameObject canvasObject = new GameObject("Gaussian Splat UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create Gaussian Splat UI");
            canvasObject.transform.SetParent(renderer.transform, false);
            canvasObject.transform.localPosition = new Vector3(0.0f, 1.2f, 1.5f);
            canvasObject.transform.localRotation = Quaternion.identity;
            canvasObject.transform.localScale = Vector3.one * 0.0015f;

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            scaler.dynamicPixelsPerUnit = 10.0f;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(1120.0f, 980.0f);

            TryAddVrChatUiShape(canvasObject);

            CreateOpaqueBackgroundPlate(canvasObject.transform, canvasRect.sizeDelta);

            GameObject panelObject = CreateVerticalGroup("Panel", canvasObject.transform, new RectOffset(12, 12, 10, 10), 10.0f, TextAnchor.UpperLeft);
            RectTransform panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(1120.0f, 0.0f);

            GaussianSplatRendererUI generatedUi = AddGeneratedUdonSharpComponent<GaussianSplatRendererUI>(canvasObject, "Add Gaussian Splat Renderer UI");

            GameObject bodyRow = CreateHorizontalGroup("Body Row", panelObject.transform, 18.0f, false);
            SetPreferredHeight(bodyRow, 900.0f, 0.0f);

            GameObject settingsColumn = CreateVerticalGroup("Settings Column", bodyRow.transform, new RectOffset(0, 0, 0, 0), 12.0f, TextAnchor.UpperLeft);
            SetPreferredWidth(settingsColumn, 520.0f, 0.0f);

            Color decrementColor = new Color(0.45f, 0.24f, 0.18f, 1.0f);
            Color incrementColor = new Color(0.18f, 0.4f, 0.24f, 1.0f);
            Color inactiveButtonColor = new Color(0.3f, 0.16f, 0.14f, 1.0f);
            void CreateStepperSetting(string baseName, string labelText, string valueText, string downEvent, string upEvent, out TextMeshProUGUI label, out TextMeshProUGUI value)
            {
                GameObject row = CreateHorizontalGroup(baseName + " Row", settingsColumn.transform, 8.0f, false);
                label = CreateTextElement(baseName + " Label", row.transform, labelText, 16, TextAnchor.MiddleLeft);
                SetPreferredWidth(label.gameObject, 210.0f, 1.0f);
                AddUdonSharpButtonEvent(CreateButtonElement(baseName + " Down", row.transform, "-", decrementColor, 42.0f, 0.0f), generatedUi, downEvent);
                value = CreateTextElement(baseName + " Value", row.transform, valueText, 16, TextAnchor.MiddleCenter);
                SetPreferredWidth(value.gameObject, 72.0f, 0.0f);
                AddUdonSharpButtonEvent(CreateButtonElement(baseName + " Up", row.transform, "+", incrementColor, 42.0f, 0.0f), generatedUi, upEvent);
            }
            void CreateToggleSetting(string baseName, string labelText, string buttonLabel, string eventName, out TextMeshProUGUI label, out Button button)
            {
                GameObject row = CreateHorizontalGroup(baseName + " Row", settingsColumn.transform, 8.0f, false);
                label = CreateTextElement(baseName + " Label", row.transform, labelText, 16, TextAnchor.MiddleLeft);
                SetPreferredWidth(label.gameObject, 210.0f, 1.0f);
                button = CreateButtonElement(baseName + " Button", row.transform, buttonLabel, inactiveButtonColor, 72.0f, 0.0f);
                AddUdonSharpButtonEvent(button, generatedUi, eventName);
            }
            void CreateSliderSetting(string baseName, string labelText, float minValue, float maxValue, bool wholeNumbers, string valueText, float labelFlexibleWidth, out TextMeshProUGUI label, out Slider slider, out TextMeshProUGUI value)
            {
                GameObject row = CreateHorizontalGroup(baseName + " Row", settingsColumn.transform, 8.0f, false);
                label = CreateTextElement(baseName + " Label", row.transform, labelText, 16, TextAnchor.MiddleLeft);
                SetPreferredWidth(label.gameObject, 210.0f, labelFlexibleWidth);
                slider = CreateSliderElement(baseName + " Slider", row.transform, minValue, maxValue, wholeNumbers);
                value = CreateTextElement(baseName + " Value", row.transform, valueText, 16, TextAnchor.MiddleCenter);
                SetPreferredWidth(value.gameObject, 72.0f, 0.0f);
            }
            GameObject headerRow = CreateHeaderRow(settingsColumn.transform);
            CreateSocialSection(generatedUi, headerRow.transform, canvasObject.transform);
            generatedUi.subtitleText = CreateTextElement("Subtitle", settingsColumn.transform, GaussianSplatRendererUI.DefaultSubtitleEnglish, (int)GaussianSplatRendererUI.SubtitleFontSize, TextAnchor.UpperLeft);
            SetPreferredHeight(generatedUi.subtitleText.gameObject, GaussianSplatRendererUI.SubtitlePreferredHeight, 0.0f);
            generatedUi.customSubtitleText = CreateTextElement("Custom Subtitle", settingsColumn.transform, "", (int)GaussianSplatRendererUI.SubtitleFontSize, TextAnchor.UpperLeft);
            SetPreferredHeight(generatedUi.customSubtitleText.gameObject, GaussianSplatRendererUI.CustomSubtitlePreferredHeight, 0.0f);
            generatedUi.customSubtitleText.gameObject.SetActive(false);
            generatedUi.currentSplatText = CreateTextElement("Current Splat", settingsColumn.transform, "Rendered Splats: 0", 16, TextAnchor.MiddleLeft);

            generatedUi.languageSectionText = null;
            GameObject languageRow = CreateHorizontalGroup("Language Row", settingsColumn.transform, 8.0f, false);
            Button englishLanguageButton = CreateButtonElement("English Button", languageRow.transform, "English", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            Button japaneseLanguageButton = CreateButtonElement("Japanese Button", languageRow.transform, "日本語", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            generatedUi.englishLanguageButton = englishLanguageButton;
            generatedUi.japaneseLanguageButton = japaneseLanguageButton;
            AddUdonSharpButtonEvent(englishLanguageButton, generatedUi, nameof(GaussianSplatRendererUI.SetLanguageEnglish));
            AddUdonSharpButtonEvent(japaneseLanguageButton, generatedUi, nameof(GaussianSplatRendererUI.SetLanguageJapanese));

            generatedUi.qualitySectionText = CreateTextElement("Quality Section", settingsColumn.transform, "Quality", 18, TextAnchor.MiddleLeft);
            GameObject qualityRow = CreateHorizontalGroup("Quality Row", settingsColumn.transform, 8.0f, false);
            generatedUi.qualityVeryLowButton = CreateButtonElement("Quality Very Low Button", qualityRow.transform, "Very Low", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            generatedUi.qualityLowButton = CreateButtonElement("Quality Low Button", qualityRow.transform, "Low", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            generatedUi.qualityMediumButton = CreateButtonElement("Quality Medium Button", qualityRow.transform, "Medium", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            generatedUi.qualityHighButton = CreateButtonElement("Quality High Button", qualityRow.transform, "High", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            AddUdonSharpButtonEvent(generatedUi.qualityVeryLowButton, generatedUi, nameof(GaussianSplatRendererUI.SetQualityVeryLow));
            AddUdonSharpButtonEvent(generatedUi.qualityLowButton, generatedUi, nameof(GaussianSplatRendererUI.SetQualityLow));
            AddUdonSharpButtonEvent(generatedUi.qualityMediumButton, generatedUi, nameof(GaussianSplatRendererUI.SetQualityMedium));
            AddUdonSharpButtonEvent(generatedUi.qualityHighButton, generatedUi, nameof(GaussianSplatRendererUI.SetQualityHigh));

            generatedUi.advancedSettingsButton = CreateButtonElement("Advanced Settings Button", settingsColumn.transform, "Show Advanced Settings", inactiveButtonColor, 0.0f, 1.0f);
            AddUdonSharpButtonEvent(generatedUi.advancedSettingsButton, generatedUi, nameof(GaussianSplatRendererUI.ToggleAdvancedSettings));

            generatedUi.materialSectionText = CreateTextElement("Settings Section", settingsColumn.transform, "Material Settings", 18, TextAnchor.MiddleLeft);
            CreateSliderSetting("SH Band", "SH Band", 0.0f, 3.0f, true, "3", 0.0f, out generatedUi.shBandLabelText, out generatedUi.shBandSlider, out generatedUi.shBandText);
            CreateToggleSetting("VRC Light Volumes", "VRC Light Volumes", "Off", nameof(GaussianSplatRendererUI.ToggleVrcLightVolumes), out generatedUi.vrcLightVolumesLabelText, out generatedUi.vrcLightVolumesButton);
            CreateSliderSetting("Light Volume Intensity", "Light Volume Intensity", 0.0f, 4.0f, false, "1", 0.0f, out generatedUi.lightVolumeIntensityLabelText, out generatedUi.lightVolumeIntensitySlider, out generatedUi.lightVolumeIntensityText);
            CreateSliderSetting("AntiAliasing", "Antialiasing", 0.0f, 3.0f, false, "1", 0.0f, out generatedUi.antiAliasingLabelText, out generatedUi.antiAliasingSlider, out generatedUi.antiAliasingText);
            CreateStepperSetting("Gaussian Scale", "Gaussian Scale", "1", nameof(GaussianSplatRendererUI.DecreaseGaussianScale), nameof(GaussianSplatRendererUI.IncreaseGaussianScale), out generatedUi.gaussianScaleLabelText, out generatedUi.gaussianScaleText);
            CreateSliderSetting("Alpha Cutoff", "Alpha Cutoff\n(lower = better quality)", 0.005f, 0.3f, false, "0.04", 0.0f, out generatedUi.alphaCutoffLabelText, out generatedUi.alphaCutoffSlider, out generatedUi.alphaCutoffText);
            generatedUi.alphaCutoffSlider.value = DefaultAlphaCutoff;
            CreateSliderSetting("Alpha Cull", "Alpha Cull\n(higher = fewer splats)", 0.005f, 0.3f, false, "0.04", 0.0f, out generatedUi.alphaCullLabelText, out generatedUi.alphaCullSlider, out generatedUi.alphaCullText);
            generatedUi.alphaCullSlider.value = DefaultAlphaCull;
            if (SceneHasActiveLODContent(renderer.gameObject.scene))
            {
                CreateSliderSetting("LOD Splat Cap", "LOD Splat Cap", 0.0f, 1.0f, false, "3000000", 0.0f, out generatedUi.lodCullLabelText, out generatedUi.lodCullSlider, out generatedUi.lodCullText);
                generatedUi.lodCullSlider.value = DefaultLODSplatCapSlider;
            }

            if (preservedGalleryObjects != null)
            {
                generatedUi.galleryObjects = preservedGalleryObjects;
            }
            generatedUi.galleryEnabled = preservedGalleryEnabled;
            generatedUi.galleryMasterLock = preservedGalleryMasterLock;
            generatedUi.customSubtitleEnglish = preservedCustomSubtitleEnglish;
            generatedUi.customSubtitleJapanese = preservedCustomSubtitleJapanese;
            WriteGallerySelectedIndex(generatedUi, preservedGallerySelectedIndex);
            // Rebuild the gallery section (Body Row child) from the restored list.
            InvokeSyncEditorSerializedState(generatedUi);

            generatedUi.RefreshUI();
            EditorUtility.SetDirty(canvasObject);
            EditorUtility.SetDirty(renderer);
            EditorUtility.SetDirty(generatedUi);
            Component generatedUiBacking = GetBackingUdonBehaviour(generatedUi);
            if (generatedUiBacking != null)
            {
                EditorUtility.SetDirty(generatedUiBacking);
            }

            if (select) Selection.activeGameObject = canvasObject;
        }

        static int ReadGallerySelectedIndex(GaussianSplatRendererUI ui)
        {
            FieldInfo field = typeof(GaussianSplatRendererUI).GetField("_gallerySelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && field.GetValue(ui) is int value)
            {
                return value;
            }
            return 0;
        }

        static void WriteGallerySelectedIndex(GaussianSplatRendererUI ui, int value)
        {
            FieldInfo field = typeof(GaussianSplatRendererUI).GetField("_gallerySelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(ui, value);
            }
        }

        static void InvokeSyncEditorSerializedState(GaussianSplatRendererUI ui)
        {
            MethodInfo method = typeof(GaussianSplatRendererUI).GetMethod("SyncEditorSerializedState", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(ui, null);
            }
        }

        static Type FindTypeInLoadedAssemblies(string fullTypeName, string shortTypeName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type resolvedType = assemblies[i].GetType(fullTypeName);
                if (resolvedType != null)
                {
                    return resolvedType;
                }
            }

            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }

                if (types == null)
                {
                    continue;
                }

                for (int j = 0; j < types.Length; j++)
                {
                    Type candidateType = types[j];
                    if (candidateType != null && candidateType.Name == shortTypeName)
                    {
                        return candidateType;
                    }
                }
            }

            return null;
        }

        static Type GetVrChatUiShapeType()
        {
            if (_cachedVrChatUiShapeType == null)
            {
                _cachedVrChatUiShapeType = FindTypeInLoadedAssemblies("VRC.SDK3.Components.VRCUiShape", "VRCUiShape") ?? FindTypeInLoadedAssemblies("VRC.SDKBase.VRC_UiShape", "VRC_UiShape");
            }
            return _cachedVrChatUiShapeType;
        }

        static void TryAddVrChatUiShape(GameObject targetObject)
        {
            Type vrChatUiShapeType = targetObject != null ? GetVrChatUiShapeType() : null;
            if (vrChatUiShapeType != null && targetObject.GetComponent(vrChatUiShapeType) == null)
            {
                targetObject.AddComponent(vrChatUiShapeType);
            }
        }

        static TMP_FontAsset GetUiTextMeshProFont()
        {
            if (_cachedUiTextMeshProFont != null)
            {
                return _cachedUiTextMeshProFont;
            }

            TMP_FontAsset uiTextMeshProFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(UiTextMeshProFontAssetPath);
            Font uiFont = AssetDatabase.LoadAssetAtPath<Font>(UiFontAssetPath);
            bool changed = false;

            if (uiTextMeshProFont != null)
            {
                bool recreateFontAsset = uiTextMeshProFont.material == null ||
                    uiTextMeshProFont.atlasTexture == null ||
                    uiTextMeshProFont.atlasRenderMode != UiTextMeshProGlyphRenderMode ||
                    uiTextMeshProFont.atlasPadding != UiTextMeshProAtlasPadding ||
                    uiTextMeshProFont.atlasTexture.width != UiTextMeshProAtlasSize ||
                    uiTextMeshProFont.atlasTexture.height != UiTextMeshProAtlasSize ||
                    uiTextMeshProFont.material.mainTexture != uiTextMeshProFont.atlasTexture;
                if (recreateFontAsset)
                {
                    AssetDatabase.DeleteAsset(UiTextMeshProFontAssetPath);
                    uiTextMeshProFont = null;
                }
            }

            if (uiTextMeshProFont == null && uiFont != null)
            {
                uiTextMeshProFont = TMP_FontAsset.CreateFontAsset(
                    uiFont,
                    UiTextMeshProPointSize,
                    UiTextMeshProAtlasPadding,
                    UiTextMeshProGlyphRenderMode,
                    UiTextMeshProAtlasSize,
                    UiTextMeshProAtlasSize,
                    AtlasPopulationMode.Dynamic,
                    true);
                if (uiTextMeshProFont != null)
                {
                    uiTextMeshProFont.name = Path.GetFileNameWithoutExtension(UiTextMeshProFontAssetPath);
                    uiTextMeshProFont.normalStyle = 0f;
                    uiTextMeshProFont.normalSpacingOffset = 0f;
                    uiTextMeshProFont.boldStyle = UiTextMeshProBoldStyle;
                    uiTextMeshProFont.boldSpacing = 0f;
                    EnsureUiTextCharacters(uiTextMeshProFont);
                    Material createdMaterial = uiTextMeshProFont.material;
                    Texture[] createdAtlases = uiTextMeshProFont.atlasTextures;
                    AssetDatabase.CreateAsset(uiTextMeshProFont, UiTextMeshProFontAssetPath);
                    if (createdMaterial != null)
                    {
                        createdMaterial.hideFlags = HideFlags.None;
                        createdMaterial.name = uiTextMeshProFont.name + " Material";
                        AssetDatabase.AddObjectToAsset(createdMaterial, UiTextMeshProFontAssetPath);
                    }
                    if (createdAtlases != null)
                    {
                        for (int atlasIndex = 0; atlasIndex < createdAtlases.Length; atlasIndex++)
                        {
                            Texture atlasTexture = createdAtlases[atlasIndex];
                            if (atlasTexture == null)
                            {
                                continue;
                            }
                            atlasTexture.hideFlags = HideFlags.None;
                            atlasTexture.name = uiTextMeshProFont.name + " Atlas" + (atlasIndex > 0 ? atlasIndex.ToString() : string.Empty);
                            AssetDatabase.AddObjectToAsset(atlasTexture, UiTextMeshProFontAssetPath);
                        }
                    }
                    AssetDatabase.ImportAsset(UiTextMeshProFontAssetPath, ImportAssetOptions.ForceUpdate);
                    uiTextMeshProFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(UiTextMeshProFontAssetPath);
                    changed = true;
                }
            }

            if (uiTextMeshProFont == null)
            {
                return TMP_Settings.defaultFontAsset;
            }

            if (uiTextMeshProFont.normalStyle != 0f)
            {
                uiTextMeshProFont.normalStyle = 0f;
                changed = true;
            }
            if (uiTextMeshProFont.normalSpacingOffset != 0f)
            {
                uiTextMeshProFont.normalSpacingOffset = 0f;
                changed = true;
            }
            if (uiTextMeshProFont.boldStyle != UiTextMeshProBoldStyle)
            {
                uiTextMeshProFont.boldStyle = UiTextMeshProBoldStyle;
                changed = true;
            }
            if (uiTextMeshProFont.boldSpacing != 0f)
            {
                uiTextMeshProFont.boldSpacing = 0f;
                changed = true;
            }
            if (EnsureUiTextCharacters(uiTextMeshProFont))
            {
                changed = true;
            }

            Shader tmpTextShader = Shader.Find(TmpTextShaderName);
            Material fontMaterial = uiTextMeshProFont.material;
            Texture fontAtlas = uiTextMeshProFont.atlasTexture;
            if (fontMaterial != null)
            {
                if (tmpTextShader != null && fontMaterial.shader != tmpTextShader)
                {
                    fontMaterial.shader = tmpTextShader;
                    changed = true;
                }
                if (fontAtlas != null && fontMaterial.mainTexture != fontAtlas)
                {
                    fontMaterial.mainTexture = fontAtlas;
                    changed = true;
                }
                if (fontMaterial.HasProperty("_Color") && fontMaterial.GetColor("_Color") != Color.white)
                {
                    fontMaterial.SetColor("_Color", Color.white);
                    changed = true;
                }
                if (changed)
                {
                    EditorUtility.SetDirty(fontMaterial);
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(uiTextMeshProFont);
                AssetDatabase.SaveAssets();
            }

            _cachedUiTextMeshProFont = uiTextMeshProFont;
            return _cachedUiTextMeshProFont;
        }

        static TextAlignmentOptions ConvertTextAlignment(TextAnchor alignment)
        {
            return alignment switch
            {
                TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
                TextAnchor.UpperCenter => TextAlignmentOptions.Top,
                TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
                TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
                TextAnchor.MiddleCenter => TextAlignmentOptions.Center,
                TextAnchor.MiddleRight => TextAlignmentOptions.Right,
                TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft,
                TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
                TextAnchor.LowerRight => TextAlignmentOptions.BottomRight,
                _ => TextAlignmentOptions.Left,
            };
        }

        static Material GetSupersampledUiMaterial()
        {
            if (_cachedSupersampledUiMaterial != null) return _cachedSupersampledUiMaterial;
            EnsureFolderExists(UiMaterialFolderPath);
            Material supersampledUiMaterial = AssetDatabase.LoadAssetAtPath<Material>(SupersampledUiMaterialAssetPath);
            Shader supersampledUiShader = Shader.Find(VrChatSupersampledUiShaderName);
            if (supersampledUiShader == null) return null;
            if (supersampledUiMaterial == null)
            {
                supersampledUiMaterial = new Material(supersampledUiShader);
                supersampledUiMaterial.name = "GaussianSplatUISupersampled";
                AssetDatabase.CreateAsset(supersampledUiMaterial, SupersampledUiMaterialAssetPath);
            }
            else if (supersampledUiMaterial.shader != supersampledUiShader)
            {
                supersampledUiMaterial.shader = supersampledUiShader;
                EditorUtility.SetDirty(supersampledUiMaterial);
            }

            _cachedSupersampledUiMaterial = supersampledUiMaterial;
            return _cachedSupersampledUiMaterial;
        }

        static void ApplySupersampledUiMaterial(Graphic graphic)
        {
            if (graphic != null) graphic.material = GetSupersampledUiMaterial();
        }

        static T AddGeneratedUdonSharpComponent<T>(GameObject targetObject, string undoLabel) where T : UdonSharpBehaviour
        {
            Undo.RegisterCompleteObjectUndo(targetObject, undoLabel);
            return targetObject.AddUdonSharpComponent<T>();
        }

        static Component GetBackingUdonBehaviour(UdonSharpBehaviour proxyBehaviour)
        {
            if (proxyBehaviour == null) return null;
            MethodInfo method = typeof(UdonSharpEditorUtility).GetMethod("GetBackingUdonBehaviour", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return null;
            return method.Invoke(null, new object[] { proxyBehaviour }) as Component;
        }

        static void EnsureFolderExists(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath)) return;
            string normalizedPath = folderPath.Replace('\\', '/');
            string[] parts = normalizedPath.Split('/');
            if (parts.Length == 0) return;
            string currentPath = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string nextPath = currentPath + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(nextPath)) AssetDatabase.CreateFolder(currentPath, parts[i]);
                currentPath = nextPath;
            }
        }

        static void AddUdonSharpButtonEvent(Button button, UdonSharpBehaviour targetBehaviour, string eventName)
        {
            if (button == null || targetBehaviour == null || string.IsNullOrEmpty(eventName)) return;
            Component backingBehaviour = GetBackingUdonBehaviour(targetBehaviour);
            if (backingBehaviour == null) return;
            MethodInfo sendCustomEventMethod = backingBehaviour.GetType().GetMethod("SendCustomEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            if (sendCustomEventMethod == null) return;
            UnityAction<string> sendCustomEvent = (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), backingBehaviour, sendCustomEventMethod);
            UnityEventTools.AddStringPersistentListener(button.onClick, sendCustomEvent, eventName);
            EditorUtility.SetDirty(backingBehaviour);
        }

        static Material CreateOpaqueBackgroundMaterial(string assetName, Color color)
        {
            const string materialFolderPath = "Assets/VRChatGaussianSplatting/Resources/Materials";
            string materialAssetPath = materialFolderPath + "/" + assetName + ".mat";
            EnsureFolderExists(materialFolderPath);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
            if (material != null) return material;
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            if (shader == null) return null;
            material = new Material(shader);
            material.name = assetName;
            material.color = color;
            AssetDatabase.CreateAsset(material, materialAssetPath);
            return material;
        }

        static GameObject CreateOpaqueBackgroundPlate(Transform parent, Vector2 sizeDelta, Material material = null)
        {
            GameObject backgroundObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(backgroundObject, "Create Gaussian Splat UI Background");
            backgroundObject.name = "Background";
            backgroundObject.transform.SetParent(parent, false);
            backgroundObject.transform.localPosition = new Vector3(0.0f, 0.0f, 6.0f);
            backgroundObject.transform.localRotation = Quaternion.identity;
            backgroundObject.transform.localScale = new Vector3(sizeDelta.x + 24.0f, sizeDelta.y + 24.0f, 1.0f);
            Collider backgroundCollider = backgroundObject.GetComponent<Collider>();
            if (backgroundCollider != null) backgroundCollider.enabled = false;
            MeshRenderer backgroundRenderer = backgroundObject.GetComponent<MeshRenderer>();
            Material backgroundMaterial = material != null ? material : (backgroundRenderer != null ? CreateOpaqueBackgroundMaterial("GaussianSplatUIBackground", new Color(0.08f, 0.08f, 0.1f, 1.0f)) : null);
            if (backgroundMaterial != null) backgroundRenderer.sharedMaterial = backgroundMaterial;
            return backgroundObject;
        }

        static RectTransform CreateRectTransform(string objectName, Transform parent, Vector2 sizeDelta)
        {
            GameObject childObject = new GameObject(objectName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(childObject, "Create Gaussian Splat UI Element");
            childObject.transform.SetParent(parent, false);
            RectTransform rectTransform = childObject.GetComponent<RectTransform>();
            rectTransform.sizeDelta = sizeDelta;
            return rectTransform;
        }

        static TextMeshProUGUI CreateTextElement(string objectName, Transform parent, string textValue, int fontSize, TextAnchor alignment)
        {
            float preferredHeight = (textValue.Split('\n').Length * (fontSize + 6.0f)) + 12.0f;
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, preferredHeight));
            TextMeshProUGUI text = rectTransform.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = GetUiTextMeshProFont();
            text.color = Color.white;
            text.fontSize = fontSize;
            text.fontStyle = FontStyles.Bold;
            text.fontWeight = FontWeight.Bold;
            text.alignment = ConvertTextAlignment(alignment);
            text.text = textValue;
            text.richText = true;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            EditorUtility.SetDirty(text);
            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = layoutElement.minHeight = preferredHeight;
            return text;
        }

        const string LogoAssetPath = "Assets/VRChatGaussianSplatting/VRCGS_Logo.png";

        // Header row: logo pinned to the top-left corner, with a flexible spacer that pushes the
        // social icon buttons (added afterwards by CreateSocialSection) over to the right edge.
        static GameObject CreateHeaderRow(Transform column)
        {
            const float logoHeight = 54.0f;
            const float logoAspect = 2164.0f / 922.0f;
            GameObject row = CreateHorizontalGroup("Header Row", column, 6.0f, false);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;

            Sprite logo = AssetDatabase.LoadAssetAtPath<Sprite>(LogoAssetPath);
            RectTransform logoRect = CreateRectTransform("Title Logo", row.transform, new Vector2(logoHeight * logoAspect, logoHeight));
            Image logoImage = logoRect.gameObject.AddComponent<Image>();
            logoImage.sprite = logo;
            logoImage.color = Color.white;
            logoImage.preserveAspect = true;
            logoImage.raycastTarget = false;
            ApplySupersampledUiMaterial(logoImage);
            LayoutElement logoLayout = logoRect.gameObject.AddComponent<LayoutElement>();
            logoLayout.minHeight = logoLayout.preferredHeight = logoHeight;
            logoLayout.minWidth = logoLayout.preferredWidth = logoHeight * logoAspect;
            logoLayout.flexibleWidth = 0.0f;

            RectTransform spacer = CreateRectTransform("Header Spacer", row.transform, Vector2.zero);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.0f;
            return row;
        }

        static void SetPreferredWidth(GameObject targetObject, float width, float flexibleWidth)
        {
            LayoutElement layoutElement = targetObject.GetComponent<LayoutElement>() ?? targetObject.AddComponent<LayoutElement>();
            if (width > 0.0f)
            {
                layoutElement.minWidth = layoutElement.preferredWidth = width;
            }
            layoutElement.flexibleWidth = flexibleWidth;
        }

        static void SetPreferredHeight(GameObject targetObject, float height, float flexibleHeight)
        {
            LayoutElement layoutElement = targetObject.GetComponent<LayoutElement>() ?? targetObject.AddComponent<LayoutElement>();
            if (height > 0.0f)
            {
                layoutElement.minHeight = layoutElement.preferredHeight = height;
            }
            layoutElement.flexibleHeight = flexibleHeight;
        }

        static GameObject CreateVerticalGroup(string objectName, Transform parent, RectOffset padding, float spacing, TextAnchor childAlignment)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, Vector2.zero);
            VerticalLayoutGroup layoutGroup = rectTransform.gameObject.AddComponent<VerticalLayoutGroup>();
            layoutGroup.padding = padding; layoutGroup.spacing = spacing; layoutGroup.childControlWidth = layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandWidth = true; layoutGroup.childForceExpandHeight = false; layoutGroup.childAlignment = childAlignment;
            ContentSizeFitter fitter = rectTransform.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rectTransform.gameObject;
        }

        static GameObject CreateHorizontalGroup(string objectName, Transform parent, float spacing, bool forceExpandWidth)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, Vector2.zero);
            HorizontalLayoutGroup layoutGroup = rectTransform.gameObject.AddComponent<HorizontalLayoutGroup>();
            layoutGroup.spacing = spacing; layoutGroup.childControlWidth = layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandWidth = forceExpandWidth; layoutGroup.childForceExpandHeight = false; layoutGroup.childAlignment = TextAnchor.UpperLeft;
            ContentSizeFitter fitter = rectTransform.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rectTransform.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.0f;
            return rectTransform.gameObject;
        }

        static Button CreateButtonElement(string objectName, Transform parent, string buttonLabel, Color backgroundColor, float preferredWidth = 0.0f, float flexibleWidth = 1.0f)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(preferredWidth, 38.0f));
            Image image = rectTransform.gameObject.AddComponent<Image>();
            image.color = backgroundColor;
            ApplySupersampledUiMaterial(image);
            Button button = rectTransform.gameObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = backgroundColor; colors.highlightedColor = backgroundColor * 1.1f; colors.pressedColor = backgroundColor * 0.85f;
            colors.selectedColor = backgroundColor; colors.disabledColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0.4f);
            button.colors = colors;
            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = layoutElement.minHeight = 38.0f;
            if (preferredWidth > 0.0f) layoutElement.preferredWidth = layoutElement.minWidth = preferredWidth;
            layoutElement.flexibleWidth = flexibleWidth;
            TextMeshProUGUI label = CreateTextElement("Label", rectTransform, buttonLabel, 16, TextAnchor.MiddleCenter);
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8.0f, 4.0f); labelRect.offsetMax = new Vector2(-8.0f, -4.0f);
            TryAddVrChatUiShape(rectTransform.gameObject);
            return button;
        }

        const string SocialTextureFolder = "Assets/VRChatGaussianSplatting/Resources/Textures/Social/";

        static void CreateSocialSection(GaussianSplatRendererUI ui, Transform iconParent, Transform canvas)
        {
            string[] keys = { "x", "github", "github_sponsors", "booth", "patreon", "gumroad" };
            string[] urls = {
                "https://x.com/Michael_Moroz_",
                "https://github.com/MichaelMoroz/VRChatGaussianSplatting",
                "https://github.com/sponsors/MichaelMoroz",
                "https://misham.booth.pm/",
                "https://patreon.com/misha_m",
                "https://4446040403950.gumroad.com/",
            };
            string[] events = {
                nameof(GaussianSplatRendererUI.OpenSocialX),
                nameof(GaussianSplatRendererUI.OpenSocialGithub),
                nameof(GaussianSplatRendererUI.OpenSocialSponsors),
                nameof(GaussianSplatRendererUI.OpenSocialBooth),
                nameof(GaussianSplatRendererUI.OpenSocialPatreon),
                nameof(GaussianSplatRendererUI.OpenSocialGumroad),
            };

            Sprite[] qrSprites = new Sprite[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                Sprite icon = AssetDatabase.LoadAssetAtPath<Sprite>(SocialTextureFolder + keys[i] + ".png");
                qrSprites[i] = AssetDatabase.LoadAssetAtPath<Sprite>(SocialTextureFolder + "qr_" + keys[i] + ".png");
                Button iconButton = CreateIconButton("Social " + keys[i], iconParent, icon, 25.0f);
                AddUdonSharpButtonEvent(iconButton, ui, events[i]);
            }

            // Floating window with its own opaque plate, centered on the menu and pushed physically forward
            // along the canvas normal so it hovers in front of the menu in 3D. Hidden until an icon is clicked.
            RectTransform window = CreateRectTransform("Social Window", canvas, new Vector2(480.0f, 620.0f));
            window.anchorMin = window.anchorMax = new Vector2(0.5f, 0.5f);
            window.pivot = new Vector2(0.5f, 0.5f);
            window.anchoredPosition3D = new Vector3(0.0f, 0.0f, -120.0f);
            window.SetAsLastSibling();
            Material windowBackground = CreateOpaqueBackgroundMaterial("GaussianSplatUIWindowBackground", new Color(0.16f, 0.20f, 0.32f, 1.0f));
            CreateOpaqueBackgroundPlate(window, window.sizeDelta, windowBackground);

            GameObject content = CreateVerticalGroup("Window Content", window, new RectOffset(24, 24, 20, 20), 14.0f, TextAnchor.UpperCenter);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero; contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero; contentRect.offsetMax = Vector2.zero;
            VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null) { contentLayout.childForceExpandWidth = false; contentLayout.childForceExpandHeight = false; contentLayout.childAlignment = TextAnchor.UpperCenter; }

            CreateTextElement("Window Title", content.transform, "Scan or copy the link", 18, TextAnchor.MiddleCenter);

            RectTransform qrRect = CreateRectTransform("Social QR", content.transform, new Vector2(400.0f, 400.0f));
            Image qrImage = qrRect.gameObject.AddComponent<Image>();
            qrImage.color = Color.white;
            qrImage.preserveAspect = true;
            ApplySupersampledUiMaterial(qrImage);
            LayoutElement qrLayout = qrRect.gameObject.AddComponent<LayoutElement>();
            qrLayout.preferredWidth = qrLayout.minWidth = 400.0f;
            qrLayout.preferredHeight = qrLayout.minHeight = 400.0f;
            qrLayout.flexibleWidth = 0.0f;
            if (qrSprites.Length > 0 && qrSprites[0] != null) qrImage.sprite = qrSprites[0];

            TMP_InputField urlField = CreateUrlField("Social URL", content.transform);
            urlField.text = urls[0];
            SetPreferredWidth(urlField.gameObject, 432.0f, 0.0f);

            Button closeButton = CreateButtonElement("Social Close", content.transform, "Close", new Color(0.3f, 0.16f, 0.14f, 1.0f), 180.0f, 0.0f);
            AddUdonSharpButtonEvent(closeButton, ui, nameof(GaussianSplatRendererUI.CloseSocial));

            window.gameObject.SetActive(false);
            ui.socialPanel = window.gameObject;
            ui.socialQrImage = qrImage;
            ui.socialUrlField = urlField;
            ui.socialQrSprites = qrSprites;
            ui.socialUrls = urls;
        }

        static Button CreateIconButton(string objectName, Transform parent, Sprite icon, float size)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(size, size));
            Image image = rectTransform.gameObject.AddComponent<Image>();
            image.sprite = icon;
            image.color = Color.white;
            image.preserveAspect = true;
            ApplySupersampledUiMaterial(image);
            Button button = rectTransform.gameObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.82f, 0.82f, 0.82f, 1.0f);
            colors.pressedColor = new Color(0.62f, 0.62f, 0.62f, 1.0f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1.0f, 1.0f, 1.0f, 0.4f);
            button.colors = colors;
            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = layoutElement.minWidth = size;
            layoutElement.preferredHeight = layoutElement.minHeight = size;
            layoutElement.flexibleWidth = 0.0f;
            TryAddVrChatUiShape(rectTransform.gameObject);
            return button;
        }

        static TMP_InputField CreateUrlField(string objectName, Transform parent)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, 30.0f));
            Image background = rectTransform.gameObject.AddComponent<Image>();
            background.color = new Color(0.16f, 0.16f, 0.18f, 1.0f);
            ApplySupersampledUiMaterial(background);
            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = layoutElement.minHeight = 30.0f;
            layoutElement.flexibleWidth = 1.0f;

            RectTransform textArea = CreateRectTransform("Text Area", rectTransform, Vector2.zero);
            textArea.anchorMin = Vector2.zero; textArea.anchorMax = Vector2.one;
            textArea.offsetMin = new Vector2(10.0f, 4.0f); textArea.offsetMax = new Vector2(-10.0f, -4.0f);
            textArea.gameObject.AddComponent<RectMask2D>();

            RectTransform textRect = CreateRectTransform("Text", textArea, Vector2.zero);
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero; textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = GetUiTextMeshProFont();
            text.fontSize = 12.0f;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Left;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;

            TMP_InputField inputField = rectTransform.gameObject.AddComponent<TMP_InputField>();
            inputField.textViewport = textArea;
            inputField.textComponent = text;
            inputField.fontAsset = GetUiTextMeshProFont();
            inputField.pointSize = 12.0f;
            inputField.readOnly = true;
            inputField.richText = false;
            inputField.lineType = TMP_InputField.LineType.SingleLine;
            inputField.restoreOriginalTextOnEscape = false;
            TryAddVrChatUiShape(rectTransform.gameObject);
            return inputField;
        }

        static Slider CreateSliderElement(string objectName, Transform parent, float minValue, float maxValue, bool wholeNumbers)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, 34.0f));
            Image background = rectTransform.gameObject.AddComponent<Image>();
            background.color = new Color(0.16f, 0.16f, 0.18f, 1.0f);
            ApplySupersampledUiMaterial(background);
            Slider slider = rectTransform.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight; slider.minValue = minValue; slider.maxValue = maxValue; slider.wholeNumbers = wholeNumbers;
            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = layoutElement.minHeight = 34.0f; layoutElement.flexibleWidth = 1.0f;
            RectTransform fillArea = CreateRectTransform("Fill Area", rectTransform, Vector2.zero);
            fillArea.anchorMin = new Vector2(0.0f, 0.0f); fillArea.anchorMax = new Vector2(1.0f, 1.0f);
            fillArea.offsetMin = new Vector2(12.0f, 10.0f); fillArea.offsetMax = new Vector2(-12.0f, -10.0f);
            RectTransform fill = CreateRectTransform("Fill", fillArea, Vector2.zero);
            fill.anchorMin = new Vector2(0.0f, 0.0f); fill.anchorMax = new Vector2(1.0f, 1.0f);
            fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
            Image fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.color = new Color(0.18f, 0.4f, 0.24f, 1.0f);
            ApplySupersampledUiMaterial(fillImage);
            RectTransform handleSlideArea = CreateRectTransform("Handle Slide Area", rectTransform, Vector2.zero);
            handleSlideArea.anchorMin = Vector2.zero; handleSlideArea.anchorMax = Vector2.one;
            handleSlideArea.offsetMin = new Vector2(12.0f, 10.0f); handleSlideArea.offsetMax = new Vector2(-12.0f, -10.0f);
            RectTransform handle = CreateRectTransform("Handle", handleSlideArea, new Vector2(8.0f, 12.0f));
            handle.anchorMin = new Vector2(0.0f, 0.5f); handle.anchorMax = new Vector2(0.0f, 0.5f); handle.pivot = new Vector2(0.5f, 0.5f);
            Image handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = new Color(0.86f, 0.86f, 0.9f, 1.0f);
            ApplySupersampledUiMaterial(handleImage);
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            TryAddVrChatUiShape(rectTransform.gameObject);
            return slider;
        }

        static void EnsureEventSystemExists()
        {
            EventSystem[] eventSystems = Resources.FindObjectsOfTypeAll<EventSystem>();
            for (int i = 0; i < eventSystems.Length; i++)
            {
                GameObject existingEventSystemObject = eventSystems[i] != null ? eventSystems[i].gameObject : null;
                if (existingEventSystemObject != null && !EditorUtility.IsPersistent(existingEventSystemObject)) return;
            }
            GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(eventSystemObject, "Create EventSystem");
        }
    }
}
#endif
