#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        const float DefaultAlphaCutoff = 0.03f;
        const string UiMaterialFolderPath = "Assets/VRChatGaussianSplatting/Resources/Materials";
        const string SupersampledUiMaterialAssetPath = UiMaterialFolderPath + "/GaussianSplatUISupersampled.mat";
        const string VrChatSupersampledUiShaderName = "VRChat/Mobile/Worlds/Supersampled UI";

        static Type _cachedVrChatUiShapeType;
        static Material _cachedSupersampledUiMaterial;

        [MenuItem("GameObject/Gaussian Splatting/Gaussian Splat UI", false, 11)]
        static void CreateGaussianSplatUI(MenuCommand menuCommand)
        {
            GaussianSplatRenderer renderer = GaussianSplatRenderer.EnsureSceneRendererExists();
            if (renderer == null)
            {
                return;
            }

            Undo.RecordObject(renderer.transform, "Create Gaussian Splat UI");
            Generate(renderer);
        }

        internal static void Generate(GaussianSplatRenderer renderer)
        {
            if (renderer == null)
            {
                return;
            }

            EnsureEventSystemExists();

            Transform existingUi = renderer.transform.Find("Gaussian Splat UI");
            if (existingUi != null)
            {
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

            GameObject splatColumn = CreateVerticalGroup("Splat Column", bodyRow.transform, new RectOffset(0, 0, 0, 0), 10.0f, TextAnchor.UpperLeft);
            SetPreferredWidth(splatColumn, 560.0f, 1.0f);

            CreateTextElement("Title", settingsColumn.transform, "VRChatGaussianSplatting", 22, TextAnchor.MiddleLeft, Color.white);
            CreateTextElement("Subtitle", settingsColumn.transform, "Github: https://github.com/MichaelMoroz/VRChatGaussianSplatting\nDeveloped by misha_m", 12, TextAnchor.MiddleLeft, new Color(0.82f, 0.82f, 0.82f, 1.0f));
            generatedUi.currentSplatText = CreateTextElement("Current Splat", settingsColumn.transform, "Current Splat: None", 16, TextAnchor.MiddleLeft, new Color(0.9f, 0.9f, 0.9f, 1.0f));

            generatedUi.sortingSectionText = CreateTextElement("Sorting Section", settingsColumn.transform, "Sorting Settings", 18, TextAnchor.MiddleLeft, Color.white);

            GameObject cameraQuantizationRow = CreateHorizontalGroup("Camera Resort Move Row", settingsColumn.transform, 8.0f, false);
            Text cameraQuantizationLabel = CreateTextElement("Camera Resort Move Label", cameraQuantizationRow.transform, "Camera move amount to trigger resort", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.cameraQuantizationLabelText = cameraQuantizationLabel;
            SetPreferredWidth(cameraQuantizationLabel.gameObject, 210.0f, 1.0f);
            Button cameraQuantizationDownButton = CreateButtonElement("Camera Resort Move Down", cameraQuantizationRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
            generatedUi.cameraQuantizationText = CreateTextElement("Camera Resort Move Value", cameraQuantizationRow.transform, "0.1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.cameraQuantizationText.gameObject, 72.0f, 0.0f);
            Button cameraQuantizationUpButton = CreateButtonElement("Camera Resort Move Up", cameraQuantizationRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
            AddUdonSharpButtonEvent(cameraQuantizationDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseCameraQuantization));
            AddUdonSharpButtonEvent(cameraQuantizationUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseCameraQuantization));

            GameObject sortingStepsRow = CreateHorizontalGroup("Pipeline Sort Frames Row", settingsColumn.transform, 8.0f, false);
            Text sortingStepsLabel = CreateTextElement("Pipeline Sort Frames Label", sortingStepsRow.transform, "Pipeline sort over N frames", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.sortingStepsLabelText = sortingStepsLabel;
            SetPreferredWidth(sortingStepsLabel.gameObject, 210.0f, 1.0f);
            Button sortingStepsDownButton = CreateButtonElement("Pipeline Sort Frames Down", sortingStepsRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
            generatedUi.sortingStepsText = CreateTextElement("Pipeline Sort Frames Value", sortingStepsRow.transform, "2", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.sortingStepsText.gameObject, 72.0f, 0.0f);
            Button sortingStepsUpButton = CreateButtonElement("Pipeline Sort Frames Up", sortingStepsRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
            AddUdonSharpButtonEvent(sortingStepsDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseSortingSteps));
            AddUdonSharpButtonEvent(sortingStepsUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseSortingSteps));

            GameObject alwaysUpdateRow = CreateHorizontalGroup("Sort Every Frame Row", settingsColumn.transform, 8.0f, false);
            Text alwaysUpdateLabel = CreateTextElement("Sort Every Frame Label", alwaysUpdateRow.transform, "Sort every frame", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.alwaysUpdateLabelText = alwaysUpdateLabel;
            SetPreferredWidth(alwaysUpdateLabel.gameObject, 210.0f, 1.0f);
            Button alwaysUpdateButton = CreateButtonElement("Sort Every Frame Button", alwaysUpdateRow.transform, "Off", new Color(0.3f, 0.16f, 0.14f, 1.0f), 72.0f, 0.0f);
            generatedUi.alwaysUpdateButton = alwaysUpdateButton;
            AddUdonSharpButtonEvent(alwaysUpdateButton, generatedUi, nameof(GaussianSplatRendererUI.ToggleAlwaysUpdate));

            generatedUi.materialSectionText = CreateTextElement("Settings Section", settingsColumn.transform, "Material Settings", 18, TextAnchor.MiddleLeft, Color.white);

            GameObject shBandRow = CreateHorizontalGroup("SH Band Row", settingsColumn.transform, 8.0f, false);
            Text shBandLabel = CreateTextElement("SH Band Label", shBandRow.transform, "SH Band (global)", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.shBandLabelText = shBandLabel;
            SetPreferredWidth(shBandLabel.gameObject, 210.0f, 0.0f);
            generatedUi.shBandSlider = CreateSliderElement("SH Band Slider", shBandRow.transform, 0.0f, 3.0f, true);
            generatedUi.shBandText = CreateTextElement("SH Band Value", shBandRow.transform, "3", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.shBandText.gameObject, 72.0f, 0.0f);

            GameObject vrcLightVolumesRow = CreateHorizontalGroup("VRC Light Volumes Row", settingsColumn.transform, 8.0f, false);
            Text vrcLightVolumesLabel = CreateTextElement("VRC Light Volumes Label", vrcLightVolumesRow.transform, "VRC Light Volumes (global)", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.vrcLightVolumesLabelText = vrcLightVolumesLabel;
            SetPreferredWidth(vrcLightVolumesLabel.gameObject, 210.0f, 1.0f);
            Button vrcLightVolumesButton = CreateButtonElement("VRC Light Volumes Button", vrcLightVolumesRow.transform, "Off", new Color(0.3f, 0.16f, 0.14f, 1.0f), 72.0f, 0.0f);
            generatedUi.vrcLightVolumesButton = vrcLightVolumesButton;
            AddUdonSharpButtonEvent(vrcLightVolumesButton, generatedUi, nameof(GaussianSplatRendererUI.ToggleVrcLightVolumes));

            GameObject lightVolumeIntensityRow = CreateHorizontalGroup("Light Volume Intensity Row", settingsColumn.transform, 8.0f, false);
            Text lightVolumeIntensityLabel = CreateTextElement("Light Volume Intensity Label", lightVolumeIntensityRow.transform, "Light Volume Intensity", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.lightVolumeIntensityLabelText = lightVolumeIntensityLabel;
            SetPreferredWidth(lightVolumeIntensityLabel.gameObject, 210.0f, 0.0f);
            generatedUi.lightVolumeIntensitySlider = CreateSliderElement("Light Volume Intensity Slider", lightVolumeIntensityRow.transform, 0.0f, 4.0f, false);
            generatedUi.lightVolumeIntensityText = CreateTextElement("Light Volume Intensity Value", lightVolumeIntensityRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.lightVolumeIntensityText.gameObject, 72.0f, 0.0f);

            GameObject antiAliasingRow = CreateHorizontalGroup("AntiAliasing Row", settingsColumn.transform, 8.0f, false);
            Text antiAliasingLabel = CreateTextElement("AntiAliasing Label", antiAliasingRow.transform, "Antialiasing", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.antiAliasingLabelText = antiAliasingLabel;
            SetPreferredWidth(antiAliasingLabel.gameObject, 210.0f, 0.0f);
            generatedUi.antiAliasingSlider = CreateSliderElement("AntiAliasing Slider", antiAliasingRow.transform, 0.0f, 3.0f, false);
            generatedUi.antiAliasingText = CreateTextElement("AntiAliasing Value", antiAliasingRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.antiAliasingText.gameObject, 72.0f, 0.0f);

            GameObject gaussianScaleRow = CreateHorizontalGroup("Gaussian Scale Row", settingsColumn.transform, 8.0f, false);
            Text gaussianScaleLabel = CreateTextElement("Gaussian Scale Label", gaussianScaleRow.transform, "Gaussian Scale (global)", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.gaussianScaleLabelText = gaussianScaleLabel;
            SetPreferredWidth(gaussianScaleLabel.gameObject, 210.0f, 1.0f);
            Button gaussianScaleDownButton = CreateButtonElement("Gaussian Scale Down", gaussianScaleRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
            generatedUi.gaussianScaleText = CreateTextElement("Gaussian Scale Value", gaussianScaleRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.gaussianScaleText.gameObject, 72.0f, 0.0f);
            Button gaussianScaleUpButton = CreateButtonElement("Gaussian Scale Up", gaussianScaleRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
            AddUdonSharpButtonEvent(gaussianScaleDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseGaussianScale));
            AddUdonSharpButtonEvent(gaussianScaleUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseGaussianScale));

            GameObject alphaCutoffRow = CreateHorizontalGroup("Alpha Cutoff Row", settingsColumn.transform, 8.0f, false);
            Text alphaCutoffLabel = CreateTextElement("Alpha Cutoff Label", alphaCutoffRow.transform, "Alpha Cutoff\n(lower = better quality)", 16, TextAnchor.MiddleLeft, Color.white);
            generatedUi.alphaCutoffLabelText = alphaCutoffLabel;
            SetPreferredWidth(alphaCutoffLabel.gameObject, 210.0f, 0.0f);
            generatedUi.alphaCutoffSlider = CreateSliderElement("Alpha Cutoff Slider", alphaCutoffRow.transform, 0.005f, 0.1f, false);
            generatedUi.alphaCutoffSlider.value = DefaultAlphaCutoff;
            generatedUi.alphaCutoffText = CreateTextElement("Alpha Cutoff Value", alphaCutoffRow.transform, "0.03", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
            SetPreferredWidth(generatedUi.alphaCutoffText.gameObject, 72.0f, 0.0f);

            generatedUi.languageSectionText = CreateTextElement("Language Section", settingsColumn.transform, "Language", 18, TextAnchor.MiddleLeft, Color.white);
            GameObject languageRow = CreateHorizontalGroup("Language Row", settingsColumn.transform, 8.0f, false);
            Button englishLanguageButton = CreateButtonElement("English Button", languageRow.transform, "English", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            Button japaneseLanguageButton = CreateButtonElement("Japanese Button", languageRow.transform, "日本語", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            generatedUi.englishLanguageButton = englishLanguageButton;
            generatedUi.japaneseLanguageButton = japaneseLanguageButton;
            AddUdonSharpButtonEvent(englishLanguageButton, generatedUi, nameof(GaussianSplatRendererUI.SetLanguageEnglish));
            AddUdonSharpButtonEvent(japaneseLanguageButton, generatedUi, nameof(GaussianSplatRendererUI.SetLanguageJapanese));

            const float splatListPanelHeight = 840.0f;
            const float splatListPanelSpacing = 8.0f;
            const float splatListPanelPadding = 8.0f;
            const float splatScrollButtonHeight = 38.0f;
            const float splatSlotButtonHeight = 42.0f;

            generatedUi.splatSectionText = CreateTextElement("Splat Section", splatColumn.transform, "Splat Object (global)", 18, TextAnchor.MiddleLeft, Color.white);
            GameObject splatListPanel = CreateVerticalGroup("Splat List Panel", splatColumn.transform, new RectOffset(8, 8, 8, 8), 8.0f, TextAnchor.UpperLeft);
            Image splatListPanelImage = splatListPanel.AddComponent<Image>();
            splatListPanelImage.color = new Color(0.09f, 0.09f, 0.11f, 1.0f);
            ApplySupersampledUiMaterial(splatListPanelImage);
            SetPreferredHeight(splatListPanel, splatListPanelHeight, 0.0f);

            GameObject splatScrollRow = CreateHorizontalGroup("Splat Scroll Controls", splatListPanel.transform, 8.0f, false);
            Button scrollUpButton = CreateButtonElement("Splat Scroll Up", splatScrollRow.transform, "Up", new Color(0.15f, 0.24f, 0.36f, 1.0f), 96.0f, 0.0f);
            Button scrollDownButton = CreateButtonElement("Splat Scroll Down", splatScrollRow.transform, "Down", new Color(0.15f, 0.24f, 0.36f, 1.0f), 96.0f, 0.0f);
            generatedUi.splatScrollUpButton = scrollUpButton;
            generatedUi.splatScrollDownButton = scrollDownButton;
            AddUdonSharpButtonEvent(scrollUpButton, generatedUi, nameof(GaussianSplatRendererUI.ScrollSplatListUp));
            AddUdonSharpButtonEvent(scrollDownButton, generatedUi, nameof(GaussianSplatRendererUI.ScrollSplatListDown));

            GameObject splatButtonContainer = CreateVerticalGroup("Splat Button Container", splatListPanel.transform, new RectOffset(0, 0, 0, 0), 8.0f, TextAnchor.UpperLeft);

            List<Button> splatButtons = new List<Button>();
            string[] slotSelectEventNames = new string[]
            {
                nameof(GaussianSplatRendererUI.SelectSplatSlot0),
                nameof(GaussianSplatRendererUI.SelectSplatSlot1),
                nameof(GaussianSplatRendererUI.SelectSplatSlot2),
                nameof(GaussianSplatRendererUI.SelectSplatSlot3),
                nameof(GaussianSplatRendererUI.SelectSplatSlot4),
                nameof(GaussianSplatRendererUI.SelectSplatSlot5),
                nameof(GaussianSplatRendererUI.SelectSplatSlot6),
                nameof(GaussianSplatRendererUI.SelectSplatSlot7),
                nameof(GaussianSplatRendererUI.SelectSplatSlot8),
                nameof(GaussianSplatRendererUI.SelectSplatSlot9),
                nameof(GaussianSplatRendererUI.SelectSplatSlot10),
                nameof(GaussianSplatRendererUI.SelectSplatSlot11),
                nameof(GaussianSplatRendererUI.SelectSplatSlot12),
                nameof(GaussianSplatRendererUI.SelectSplatSlot13),
                nameof(GaussianSplatRendererUI.SelectSplatSlot14),
                nameof(GaussianSplatRendererUI.SelectSplatSlot15),
            };

            float availableSplatButtonHeight = splatListPanelHeight - (splatListPanelPadding * 2.0f) - splatScrollButtonHeight - splatListPanelSpacing;
            int visibleSplatButtonCount = Mathf.Max(1, Mathf.FloorToInt((availableSplatButtonHeight + splatListPanelSpacing) / (splatSlotButtonHeight + splatListPanelSpacing)));
            visibleSplatButtonCount = Mathf.Min(visibleSplatButtonCount, slotSelectEventNames.Length);

            for (int slotIndex = 0; slotIndex < visibleSplatButtonCount; slotIndex++)
            {
                Button slotButton = CreateButtonElement("Splat Slot " + slotIndex, splatButtonContainer.transform, string.Empty, new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
                SetPreferredHeight(slotButton.gameObject, splatSlotButtonHeight, 0.0f);
                splatButtons.Add(slotButton);
                AddUdonSharpButtonEvent(slotButton, generatedUi, slotSelectEventNames[slotIndex]);
            }

            generatedUi.splatButtons = splatButtons.ToArray();

            generatedUi.RefreshUI();
            EditorUtility.SetDirty(canvasObject);
            EditorUtility.SetDirty(renderer);
            EditorUtility.SetDirty(generatedUi);
            Component generatedUiBacking = GetBackingUdonBehaviour(generatedUi);
            if (generatedUiBacking != null)
            {
                EditorUtility.SetDirty(generatedUiBacking);
            }

            Selection.activeGameObject = canvasObject;
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
            if (_cachedVrChatUiShapeType != null)
            {
                return _cachedVrChatUiShapeType;
            }

            _cachedVrChatUiShapeType = FindTypeInLoadedAssemblies("VRC.SDK3.Components.VRCUiShape", "VRCUiShape");
            if (_cachedVrChatUiShapeType == null)
            {
                _cachedVrChatUiShapeType = FindTypeInLoadedAssemblies("VRC.SDKBase.VRC_UiShape", "VRC_UiShape");
            }

            return _cachedVrChatUiShapeType;
        }

        static void TryAddVrChatUiShape(GameObject targetObject)
        {
            if (targetObject == null)
            {
                return;
            }

            Type vrChatUiShapeType = GetVrChatUiShapeType();
            if (vrChatUiShapeType == null)
            {
                return;
            }

            if (targetObject.GetComponent(vrChatUiShapeType) == null)
            {
                targetObject.AddComponent(vrChatUiShapeType);
            }
        }

        static Font GetBuiltinUiFont()
        {
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static Material GetSupersampledUiMaterial()
        {
            if (_cachedSupersampledUiMaterial != null)
            {
                return _cachedSupersampledUiMaterial;
            }

            EnsureFolderExists(UiMaterialFolderPath);

            Material supersampledUiMaterial = AssetDatabase.LoadAssetAtPath<Material>(SupersampledUiMaterialAssetPath);
            Shader supersampledUiShader = Shader.Find(VrChatSupersampledUiShaderName);
            if (supersampledUiShader == null)
            {
                return null;
            }

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
            if (graphic == null)
            {
                return;
            }

            Material supersampledUiMaterial = GetSupersampledUiMaterial();
            if (supersampledUiMaterial == null)
            {
                return;
            }

            graphic.material = supersampledUiMaterial;
        }

        static T AddGeneratedUdonSharpComponent<T>(GameObject targetObject, string undoLabel) where T : UdonSharpBehaviour
        {
            Undo.RegisterCompleteObjectUndo(targetObject, undoLabel);
            return targetObject.AddUdonSharpComponent<T>();
        }

        static Component GetBackingUdonBehaviour(UdonSharpBehaviour proxyBehaviour)
        {
            if (proxyBehaviour == null)
            {
                return null;
            }

            MethodInfo method = typeof(UdonSharpEditorUtility).GetMethod("GetBackingUdonBehaviour", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return null;
            }

            return method.Invoke(null, new object[] { proxyBehaviour }) as Component;
        }

        static void EnsureFolderExists(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string normalizedPath = folderPath.Replace('\\', '/');
            string[] parts = normalizedPath.Split('/');
            if (parts.Length == 0)
            {
                return;
            }

            string currentPath = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string nextPath = currentPath + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(nextPath))
                {
                    AssetDatabase.CreateFolder(currentPath, parts[i]);
                }

                currentPath = nextPath;
            }
        }

        static void AddUdonSharpButtonEvent(Button button, UdonSharpBehaviour targetBehaviour, string eventName)
        {
            if (button == null || targetBehaviour == null || string.IsNullOrEmpty(eventName))
            {
                return;
            }

            Component backingBehaviour = GetBackingUdonBehaviour(targetBehaviour);
            if (backingBehaviour == null)
            {
                return;
            }

            MethodInfo sendCustomEventMethod = backingBehaviour.GetType().GetMethod("SendCustomEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            if (sendCustomEventMethod == null)
            {
                return;
            }

            UnityAction<string> sendCustomEvent = (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), backingBehaviour, sendCustomEventMethod);
            UnityEventTools.AddStringPersistentListener(button.onClick, sendCustomEvent, eventName);
            EditorUtility.SetDirty(backingBehaviour);
        }

        static Material CreateOpaqueBackgroundMaterial()
        {
            const string materialFolderPath = "Assets/VRChatGaussianSplatting/Resources/Materials";
            const string materialAssetPath = materialFolderPath + "/GaussianSplatUIBackground.mat";

            EnsureFolderExists(materialFolderPath);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
            if (material != null)
            {
                return material;
            }

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            if (shader == null)
            {
                return null;
            }

            material = new Material(shader);
            material.name = "Gaussian Splat UI Background";
            material.color = new Color(0.08f, 0.08f, 0.1f, 1.0f);
            AssetDatabase.CreateAsset(material, materialAssetPath);
            return material;
        }

        static GameObject CreateOpaqueBackgroundPlate(Transform parent, Vector2 sizeDelta)
        {
            GameObject backgroundObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(backgroundObject, "Create Gaussian Splat UI Background");
            backgroundObject.name = "Background";
            backgroundObject.transform.SetParent(parent, false);
            backgroundObject.transform.localPosition = new Vector3(0.0f, 0.0f, 6.0f);
            backgroundObject.transform.localRotation = Quaternion.identity;
            backgroundObject.transform.localScale = new Vector3(sizeDelta.x + 24.0f, sizeDelta.y + 24.0f, 1.0f);

            Collider backgroundCollider = backgroundObject.GetComponent<Collider>();
            if (backgroundCollider != null)
            {
                backgroundCollider.enabled = false;
            }

            MeshRenderer backgroundRenderer = backgroundObject.GetComponent<MeshRenderer>();
            if (backgroundRenderer != null)
            {
                Material backgroundMaterial = CreateOpaqueBackgroundMaterial();
                if (backgroundMaterial != null)
                {
                    backgroundRenderer.sharedMaterial = backgroundMaterial;
                }
            }

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

        static Text CreateTextElement(string objectName, Transform parent, string textValue, int fontSize, TextAnchor alignment, Color textColor)
        {
            int lineCount = 1;
            for (int i = 0; i < textValue.Length; i++)
            {
                if (textValue[i] == '\n')
                {
                    lineCount++;
                }
            }

            float preferredHeight = (lineCount * (fontSize + 6.0f)) + 12.0f;
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, preferredHeight));
            Text text = rectTransform.gameObject.AddComponent<Text>();
            text.font = GetBuiltinUiFont();
            ApplySupersampledUiMaterial(text);
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = textColor;
            text.text = textValue;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = preferredHeight;
            layoutElement.minHeight = preferredHeight;
            return text;
        }

        static void SetPreferredWidth(GameObject targetObject, float width, float flexibleWidth)
        {
            LayoutElement layoutElement = targetObject.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = targetObject.AddComponent<LayoutElement>();
            }

            if (width > 0.0f)
            {
                layoutElement.minWidth = width;
                layoutElement.preferredWidth = width;
            }

            layoutElement.flexibleWidth = flexibleWidth;
        }

        static void SetPreferredHeight(GameObject targetObject, float height, float flexibleHeight)
        {
            LayoutElement layoutElement = targetObject.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = targetObject.AddComponent<LayoutElement>();
            }

            if (height > 0.0f)
            {
                layoutElement.minHeight = height;
                layoutElement.preferredHeight = height;
            }

            layoutElement.flexibleHeight = flexibleHeight;
        }

        static GameObject CreateVerticalGroup(string objectName, Transform parent, RectOffset padding, float spacing, TextAnchor childAlignment)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, Vector2.zero);
            VerticalLayoutGroup layoutGroup = rectTransform.gameObject.AddComponent<VerticalLayoutGroup>();
            layoutGroup.padding = padding;
            layoutGroup.spacing = spacing;
            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandWidth = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.childAlignment = childAlignment;

            ContentSizeFitter fitter = rectTransform.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rectTransform.gameObject;
        }

        static GameObject CreateHorizontalGroup(string objectName, Transform parent, float spacing, bool forceExpandWidth)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, Vector2.zero);
            HorizontalLayoutGroup layoutGroup = rectTransform.gameObject.AddComponent<HorizontalLayoutGroup>();
            layoutGroup.spacing = spacing;
            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandWidth = forceExpandWidth;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.childAlignment = TextAnchor.UpperLeft;

            ContentSizeFitter fitter = rectTransform.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.flexibleWidth = 1.0f;
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
            colors.normalColor = backgroundColor;
            colors.highlightedColor = backgroundColor * 1.1f;
            colors.pressedColor = backgroundColor * 0.85f;
            colors.selectedColor = backgroundColor;
            colors.disabledColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0.4f);
            button.colors = colors;

            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 38.0f;
            layoutElement.minHeight = 38.0f;
            if (preferredWidth > 0.0f)
            {
                layoutElement.preferredWidth = preferredWidth;
                layoutElement.minWidth = preferredWidth;
            }
            layoutElement.flexibleWidth = flexibleWidth;

            Text label = CreateTextElement("Label", rectTransform, buttonLabel, 16, TextAnchor.MiddleCenter, Color.white);
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8.0f, 4.0f);
            labelRect.offsetMax = new Vector2(-8.0f, -4.0f);

            TryAddVrChatUiShape(rectTransform.gameObject);
            return button;
        }

        static Slider CreateSliderElement(string objectName, Transform parent, float minValue, float maxValue, bool wholeNumbers)
        {
            RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, 34.0f));
            Image background = rectTransform.gameObject.AddComponent<Image>();
            background.color = new Color(0.16f, 0.16f, 0.18f, 1.0f);
            ApplySupersampledUiMaterial(background);

            Slider slider = rectTransform.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = minValue;
            slider.maxValue = maxValue;
            slider.wholeNumbers = wholeNumbers;

            LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 34.0f;
            layoutElement.minHeight = 34.0f;
            layoutElement.flexibleWidth = 1.0f;

            RectTransform fillArea = CreateRectTransform("Fill Area", rectTransform, Vector2.zero);
            fillArea.anchorMin = new Vector2(0.0f, 0.0f);
            fillArea.anchorMax = new Vector2(1.0f, 1.0f);
            fillArea.offsetMin = new Vector2(12.0f, 10.0f);
            fillArea.offsetMax = new Vector2(-12.0f, -10.0f);

            RectTransform fill = CreateRectTransform("Fill", fillArea, Vector2.zero);
            fill.anchorMin = new Vector2(0.0f, 0.0f);
            fill.anchorMax = new Vector2(1.0f, 1.0f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
            Image fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.color = new Color(0.18f, 0.4f, 0.24f, 1.0f);
            ApplySupersampledUiMaterial(fillImage);

            RectTransform handleSlideArea = CreateRectTransform("Handle Slide Area", rectTransform, Vector2.zero);
            handleSlideArea.anchorMin = Vector2.zero;
            handleSlideArea.anchorMax = Vector2.one;
            handleSlideArea.offsetMin = new Vector2(12.0f, 10.0f);
            handleSlideArea.offsetMax = new Vector2(-12.0f, -10.0f);

            RectTransform handle = CreateRectTransform("Handle", handleSlideArea, new Vector2(8.0f, 12.0f));
            handle.anchorMin = new Vector2(0.0f, 0.5f);
            handle.anchorMax = new Vector2(0.0f, 0.5f);
            handle.pivot = new Vector2(0.5f, 0.5f);
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
                EventSystem existingEventSystem = eventSystems[i];
                if (existingEventSystem == null)
                {
                    continue;
                }

                GameObject existingEventSystemObject = existingEventSystem.gameObject;
                if (existingEventSystemObject == null || EditorUtility.IsPersistent(existingEventSystemObject))
                {
                    continue;
                }

                return;
            }

            GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(eventSystemObject, "Create EventSystem");
        }
    }
}
#endif