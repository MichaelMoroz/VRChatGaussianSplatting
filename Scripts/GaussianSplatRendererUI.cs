using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System.Collections.Generic;
using UnityEditor;
#endif

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GaussianSplatRendererUI : UdonSharpBehaviour
{
    const int LanguageEnglish = 0;
    const int LanguageJapanese = 1;
    const float DefaultAlphaCutoff = 0.03f;
    const float DefaultPanelWidth = 1120.0f;
    const float CombinedPanelWidth = 560.0f;
    const float BackgroundPadding = 24.0f;

    public GaussianSplatRenderer gaussianSplatRenderer;
    public Text currentSplatText;
    public Text sortingSectionText;
    public Text cameraQuantizationLabelText;
    public Text cameraQuantizationText;
    public Text sortingStepsLabelText;
    public Text sortingStepsText;
    public Text alwaysUpdateLabelText;
    public Button alwaysUpdateButton;
    public Text materialSectionText;
    public Text shBandLabelText;
    public Slider shBandSlider;
    public Text shBandText;
    public Text vrcLightVolumesLabelText;
    public Button vrcLightVolumesButton;
    public Text antiAliasingLabelText;
    public Slider antiAliasingSlider;
    public Text antiAliasingText;
    public Text lightVolumeIntensityLabelText;
    public Slider lightVolumeIntensitySlider;
    public Text lightVolumeIntensityText;
    public Text gaussianScaleLabelText;
    public Text gaussianScaleText;
    public Text alphaCutoffLabelText;
    public Slider alphaCutoffSlider;
    public Text alphaCutoffText;
    public Text languageSectionText;
    public Button englishLanguageButton;
    public Button japaneseLanguageButton;
    public Text splatSectionText;
    public Button splatScrollUpButton;
    public Button splatScrollDownButton;
    public Button[] splatButtons;
    [HideInInspector] public GaussianSplatObject[] cachedSceneSplatObjects;

    [UdonSynced, SerializeField] int syncedSelectedSplatObjectIndex = -1;
    [SerializeField] float gaussianScaleStep = 0.1f;
    [SerializeField] float cameraQuantizationStep = 0.05f;
    [SerializeField] int selectedLanguage = LanguageEnglish;

    Color _selectedSplatColor = new Color(0.55f, 0.39f, 0.12f, 1.0f);
    Color _defaultSplatColor = new Color(0.2f, 0.2f, 0.24f, 1.0f);
    Color _scrollEnabledColor = new Color(0.15f, 0.24f, 0.36f, 1.0f);
    Color _scrollDisabledColor = new Color(0.1f, 0.1f, 0.12f, 1.0f);
    Color _toggleEnabledColor = new Color(0.18f, 0.4f, 0.24f, 1.0f);
    Color _toggleDisabledColor = new Color(0.3f, 0.16f, 0.14f, 1.0f);

    int _splatListStartIndex;
    bool _sliderValuesInitialized;
    float _lastShBandSliderValue;
    float _lastAntiAliasingSliderValue;
    float _lastLightVolumeIntensitySliderValue;
    float _lastAlphaCutoffSliderValue;
    GaussianSplatObject[] _sceneSplatObjects;
    RectTransform _canvasRect;
    RectTransform _panelRect;
    Transform _backgroundTransform;
    GameObject _splatColumnObject;
    bool _layoutDefaultsInitialized;
    float _defaultCanvasWidth;
    float _defaultPanelWidth;
    Vector3 _defaultBackgroundScale;

    void Start()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return;
        }
#endif

        ApplySyncedSplatObjectSelection();
        RefreshUI();
    }

    void Update()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return;
        }
#endif

        RefreshUI();
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static bool _editorRefreshRequested = true;

    [InitializeOnLoadMethod]
    static void RegisterEditorRefresh()
    {
        EditorApplication.update -= RefreshEditorUis;
        EditorApplication.update += RefreshEditorUis;
        EditorApplication.hierarchyChanged -= RequestEditorRefresh;
        EditorApplication.hierarchyChanged += RequestEditorRefresh;
    }

    internal static void RequestEditorRefresh()
    {
        _editorRefreshRequested = true;
    }

    static bool IsSceneUi(GaussianSplatRendererUI ui)
    {
        if (ui == null)
        {
            return false;
        }

        GameObject rootObject = ui.transform.root != null ? ui.transform.root.gameObject : ui.gameObject;
        if (rootObject == null || EditorUtility.IsPersistent(rootObject))
        {
            return false;
        }

        if ((ui.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
        {
            return false;
        }

        return true;
    }

    static void RefreshEditorUis()
    {
        if (Application.isPlaying || !_editorRefreshRequested)
        {
            return;
        }

        _editorRefreshRequested = false;

        GaussianSplatRendererUI[] sceneUis = Resources.FindObjectsOfTypeAll<GaussianSplatRendererUI>();
        for (int i = 0; i < sceneUis.Length; i++)
        {
            GaussianSplatRendererUI ui = sceneUis[i];
            if (!IsSceneUi(ui))
            {
                continue;
            }

            if (ui.SyncEditorSerializedState())
            {
                EditorUtility.SetDirty(ui);
            }

            ui.RefreshUI();
        }
    }

    void OnValidate()
    {
        SyncEditorSerializedState();
        RequestEditorRefresh();
    }
#endif

    string FormatFloat(float value)
    {
        float roundedValue = Mathf.Round(value * 100.0f) * 0.01f;
        return roundedValue.ToString();
    }

    string Localize(string english, string japanese)
    {
        return selectedLanguage == LanguageJapanese ? japanese : english;
    }

    string GetCurrentSplatPrefix()
    {
        return Localize("Current Splat: ", "現在のスプラット: ");
    }

    string GetCurrentSplatNoneLabel()
    {
        return Localize("Current Splat: None", "現在のスプラット: なし");
    }

    string GetToggleOnLabel()
    {
        return Localize("On", "オン");
    }

    string GetToggleOffLabel()
    {
        return Localize("Off", "オフ");
    }

    string GetScrollUpLabel()
    {
        return Localize("Up", "上へ");
    }

    string GetScrollDownLabel()
    {
        return Localize("Down", "下へ");
    }

    string GetRenderingSuffix()
    {
        return Localize(" (Rendering)", " (表示中)");
    }

    string GetEnabledSuffix()
    {
        return Localize(" (On)", " (有効)");
    }

    Text ResolveSubtitleText()
    {
        Transform subtitleTransform = transform.Find("Panel/Body Row/Settings Column/Subtitle");
        if (subtitleTransform == null)
        {
            return null;
        }

        return (Text)subtitleTransform.GetComponent(typeof(Text));
    }

    void RefreshLocalizedLabels()
    {
        Text subtitleText = ResolveSubtitleText();
        if (subtitleText != null)
        {
            subtitleText.text = Localize(
                "Github: https://github.com/MichaelMoroz/VRChatGaussianSplatting\nDeveloped by misha_m",
                "Github: https://github.com/MichaelMoroz/VRChatGaussianSplatting\n開発: misha_m");
        }

        if (sortingSectionText != null)
        {
            sortingSectionText.text = Localize("Sorting Settings", "ソート設定");
        }

        if (cameraQuantizationLabelText != null)
        {
            cameraQuantizationLabelText.text = Localize("Camera move amount to trigger resort", "再ソートするカメラ移動量");
        }

        if (sortingStepsLabelText != null)
        {
            sortingStepsLabelText.text = Localize("Pipeline sort over N frames", "ソートを N フレームに分散");
        }

        if (alwaysUpdateLabelText != null)
        {
            alwaysUpdateLabelText.text = Localize("Sort every frame", "毎フレームソート");
        }

        if (materialSectionText != null)
        {
            materialSectionText.text = Localize("Material Settings", "マテリアル設定");
        }

        if (shBandLabelText != null)
        {
            shBandLabelText.text = Localize("SH Band (global)", "SH バンド (共有)");
        }

        if (vrcLightVolumesLabelText != null)
        {
            vrcLightVolumesLabelText.text = Localize("VRC Light Volumes (global)", "VRC Light Volumes (共有)");
        }

        if (lightVolumeIntensityLabelText != null)
        {
            lightVolumeIntensityLabelText.text = Localize("Light Volume Intensity", "Light Volume Intensity");
        }

        if (antiAliasingLabelText != null)
        {
            antiAliasingLabelText.text = Localize("Antialiasing", "アンチエイリアス");
        }

        if (gaussianScaleLabelText != null)
        {
            gaussianScaleLabelText.text = Localize("Gaussian Scale (global)", "Gaussian Scale (共有)");
        }

        if (alphaCutoffLabelText != null)
        {
            alphaCutoffLabelText.text = Localize("Alpha Cutoff\n(lower = better quality)", "アルファカットオフ\n(低いほど高品質)");
        }

        if (languageSectionText != null)
        {
            languageSectionText.text = Localize("Language", "言語");
        }

        if (splatSectionText != null)
        {
            splatSectionText.text = Localize("Splat Object (global)", "スプラットオブジェクト (共有)");
        }

        RefreshLanguageButtons();
    }

    void RefreshLanguageButtons()
    {
        if (englishLanguageButton != null)
        {
            englishLanguageButton.interactable = true;
            ApplyButtonVisual(englishLanguageButton, "English", selectedLanguage == LanguageEnglish ? _selectedSplatColor : _defaultSplatColor);
        }

        if (japaneseLanguageButton != null)
        {
            japaneseLanguageButton.interactable = true;
            ApplyButtonVisual(japaneseLanguageButton, "日本語", selectedLanguage == LanguageJapanese ? _selectedSplatColor : _defaultSplatColor);
        }
    }

    void FindRenderer()
    {
        if (gaussianSplatRenderer != null)
        {
            return;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        gaussianSplatRenderer = GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);
#else
        GameObject rendererObject = GameObject.Find("GaussianSplatRenderer");
        if (rendererObject != null)
        {
            gaussianSplatRenderer = rendererObject.GetComponent<GaussianSplatRenderer>();
        }
#endif
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    bool SyncEditorSerializedState()
    {
        if (EditorUtility.IsPersistent(this))
        {
            return false;
        }

        GaussianSplatRenderer previousRenderer = gaussianSplatRenderer;
        GaussianSplatObject[] previousCachedSceneSplatObjects = cachedSceneSplatObjects;

        FindRenderer();
        RefreshSceneSplatObjects();

        return gaussianSplatRenderer != previousRenderer || !SplatObjectArraysMatch(previousCachedSceneSplatObjects, cachedSceneSplatObjects);
    }
#endif

    static bool SplatObjectArraysMatch(GaussianSplatObject[] left, GaussianSplatObject[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    void RefreshSceneSplatObjects()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        List<GaussianSplatObject> sceneObjects = new List<GaussianSplatObject>();
        GaussianSplatObject[] allObjects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
        for (int i = 0; i < allObjects.Length; i++)
        {
            GaussianSplatObject currentObject = allObjects[i];
            if (currentObject == null)
            {
                continue;
            }

            GameObject rootObject = currentObject.transform.root != null ? currentObject.transform.root.gameObject : currentObject.gameObject;
            if (rootObject == null || EditorUtility.IsPersistent(rootObject))
            {
                continue;
            }

            if ((currentObject.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
            {
                continue;
            }

            if (currentObject.gameObject.scene != gameObject.scene)
            {
                continue;
            }

            sceneObjects.Add(currentObject);
        }

        GaussianSplatObject[] resolvedSceneObjects = sceneObjects.ToArray();
        _sceneSplatObjects = resolvedSceneObjects;
        if (!SplatObjectArraysMatch(cachedSceneSplatObjects, resolvedSceneObjects))
        {
            cachedSceneSplatObjects = resolvedSceneObjects;
        }
#else
        if (cachedSceneSplatObjects != null && cachedSceneSplatObjects.Length > 0)
        {
            _sceneSplatObjects = cachedSceneSplatObjects;
            return;
        }

#if COMPILER_UDONSHARP
        _sceneSplatObjects = new GaussianSplatObject[0];
#else
        _sceneSplatObjects = Object.FindObjectsOfType<GaussianSplatObject>(true);
#endif
#endif
    }

    int GetSceneSplatCount()
    {
        return _sceneSplatObjects == null ? 0 : _sceneSplatObjects.Length;
    }

    GaussianSplatObject GetSceneSplatObject(int index)
    {
        if (_sceneSplatObjects == null || index < 0 || index >= _sceneSplatObjects.Length)
        {
            return null;
        }

        return _sceneSplatObjects[index];
    }

    int FindSceneSplatObjectIndex(GaussianSplatObject targetSplatObject)
    {
        if (targetSplatObject == null)
        {
            return -1;
        }

        int totalSplatCount = GetSceneSplatCount();
        for (int i = 0; i < totalSplatCount; i++)
        {
            if (GetSceneSplatObject(i) == targetSplatObject)
            {
                return i;
            }
        }

        return -1;
    }

    void EnsureLocalOwnership()
    {
        if (Networking.LocalPlayer != null)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }
    }

    void RequestSyncedSelectionUpdate()
    {
        if (Networking.LocalPlayer != null)
        {
            RequestSerialization();
        }
    }

    void ApplySplatObjectSelection(GaussianSplatObject selectedSplatObject)
    {
        if (selectedSplatObject == null)
        {
            return;
        }

        int totalSplatCount = GetSceneSplatCount();
        for (int i = 0; i < totalSplatCount; i++)
        {
            GaussianSplatObject sceneSplatObject = GetSceneSplatObject(i);
            if (sceneSplatObject == null)
            {
                continue;
            }

            if (!sceneSplatObject.gameObject.activeSelf)
            {
                sceneSplatObject.gameObject.SetActive(true);
            }
        }

        if (gaussianSplatRenderer != null)
        {
            gaussianSplatRenderer.NotifySplatObjectEnabled(selectedSplatObject);
        }
        else
        {
            selectedSplatObject.NotifyRendererEnabled();
        }
    }

    bool ApplySyncedSplatObjectSelection()
    {
        RefreshSceneSplatObjects();
        if (syncedSelectedSplatObjectIndex < 0 || syncedSelectedSplatObjectIndex >= GetSceneSplatCount())
        {
            return false;
        }

        GaussianSplatObject selectedSplatObject = GetSceneSplatObject(syncedSelectedSplatObjectIndex);
        if (selectedSplatObject == null)
        {
            return false;
        }

        ApplySplatObjectSelection(selectedSplatObject);
        return true;
    }

    void SelectSplatObject(GaussianSplatObject selectedSplatObject)
    {
        RefreshSceneSplatObjects();
        int selectedIndex = FindSceneSplatObjectIndex(selectedSplatObject);
        if (selectedIndex < 0)
        {
            return;
        }

        EnsureLocalOwnership();
        syncedSelectedSplatObjectIndex = selectedIndex;
        ApplySyncedSplatObjectSelection();
        RequestSyncedSelectionUpdate();
    }

    void SetButtonEnabled(Button button, bool enabled, string label, Color enabledColor, Color disabledColor)
    {
        if (button == null)
        {
            return;
        }

        button.gameObject.SetActive(true);
        button.interactable = enabled;
        ApplyButtonVisual(button, label, enabled ? enabledColor : disabledColor);
    }

    void ApplyButtonVisual(Button button, string labelText, Color backgroundColor)
    {
        if (button == null)
        {
            return;
        }

        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            image.color = backgroundColor;
        }

        ColorBlock colors = button.colors;
        colors.normalColor = backgroundColor;
        colors.highlightedColor = backgroundColor * 1.1f;
        colors.pressedColor = backgroundColor * 0.85f;
        colors.selectedColor = backgroundColor;
        colors.disabledColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0.4f);
        button.colors = colors;

        Text label = button.GetComponentInChildren<Text>();
        if (label != null)
        {
            label.text = labelText;
        }
    }

    void SetSplatListVisible(bool visible)
    {
        if (splatSectionText != null)
        {
            splatSectionText.gameObject.SetActive(visible);
        }

        if (splatScrollUpButton != null)
        {
            splatScrollUpButton.gameObject.SetActive(visible);
        }

        if (splatScrollDownButton != null)
        {
            splatScrollDownButton.gameObject.SetActive(visible);
        }

        if (splatButtons == null)
        {
            return;
        }

        for (int i = 0; i < splatButtons.Length; i++)
        {
            Button slotButton = splatButtons[i];
            if (slotButton != null)
            {
                slotButton.gameObject.SetActive(visible);
            }
        }
    }

    void RefreshRenderingModeLayout(bool combinedMode)
    {
        if (_canvasRect == null)
        {
            _canvasRect = (RectTransform)GetComponent(typeof(RectTransform));
        }

        if (_panelRect == null)
        {
            Transform panelTransform = transform.Find("Panel");
            if (panelTransform != null)
            {
                _panelRect = (RectTransform)panelTransform.GetComponent(typeof(RectTransform));
            }
        }

        if (_backgroundTransform == null)
        {
            _backgroundTransform = transform.Find("Background");
        }

        if (_splatColumnObject == null)
        {
            Transform splatColumnTransform = transform.Find("Panel/Body Row/Splat Column");
            if (splatColumnTransform != null)
            {
                _splatColumnObject = splatColumnTransform.gameObject;
            }
        }

        if (!_layoutDefaultsInitialized)
        {
            _defaultCanvasWidth = _canvasRect != null ? _canvasRect.sizeDelta.x : DefaultPanelWidth;
            _defaultPanelWidth = _panelRect != null ? _panelRect.sizeDelta.x : DefaultPanelWidth;
            _defaultBackgroundScale = _backgroundTransform != null ? _backgroundTransform.localScale : Vector3.one;
            _layoutDefaultsInitialized = true;
        }

        if (_splatColumnObject != null && _splatColumnObject.activeSelf == combinedMode)
        {
            _splatColumnObject.SetActive(!combinedMode);
        }

        float targetWidth = combinedMode ? CombinedPanelWidth : _defaultCanvasWidth;
        if (_canvasRect != null && !Mathf.Approximately(_canvasRect.sizeDelta.x, targetWidth))
        {
            _canvasRect.sizeDelta = new Vector2(targetWidth, _canvasRect.sizeDelta.y);
        }

        float targetPanelWidth = combinedMode ? CombinedPanelWidth : _defaultPanelWidth;
        if (_panelRect != null && !Mathf.Approximately(_panelRect.sizeDelta.x, targetPanelWidth))
        {
            _panelRect.sizeDelta = new Vector2(targetPanelWidth, _panelRect.sizeDelta.y);
        }

        if (_backgroundTransform != null)
        {
            Vector3 targetScale = new Vector3(targetWidth + BackgroundPadding, _defaultBackgroundScale.y, _defaultBackgroundScale.z);
            if (_backgroundTransform.localScale != targetScale)
            {
                _backgroundTransform.localScale = targetScale;
            }
        }
    }

    void RefreshSplatButtons()
    {
        if (splatButtons == null)
        {
            return;
        }

        int visibleButtonCount = splatButtons.Length;
        int totalSplatCount = GetSceneSplatCount();
        bool combinedMode = gaussianSplatRenderer != null && gaussianSplatRenderer.IsCombinedRenderingMode();

        SetSplatListVisible(!combinedMode);
        if (combinedMode)
        {
            return;
        }

        if (totalSplatCount == 0)
        {
            for (int i = 0; i < visibleButtonCount; i++)
            {
                if (splatButtons[i] != null)
                {
                    SetButtonEnabled(splatButtons[i], false, "", _defaultSplatColor, _scrollDisabledColor);
                }
            }

            SetButtonEnabled(splatScrollUpButton, false, GetScrollUpLabel(), _scrollEnabledColor, _scrollDisabledColor);
            SetButtonEnabled(splatScrollDownButton, false, GetScrollDownLabel(), _scrollEnabledColor, _scrollDisabledColor);
            return;
        }

        int maxStartIndex = Mathf.Max(0, totalSplatCount - visibleButtonCount);
        _splatListStartIndex = Mathf.Clamp(_splatListStartIndex, 0, maxStartIndex);

        for (int i = 0; i < visibleButtonCount; i++)
        {
            Button slotButton = splatButtons[i];
            if (slotButton == null)
            {
                continue;
            }

            int splatDataIndex = _splatListStartIndex + i;
            bool hasSplat = splatDataIndex < totalSplatCount;
            if (!hasSplat)
            {
                SetButtonEnabled(slotButton, false, "", _defaultSplatColor, _scrollDisabledColor);
                continue;
            }

            GaussianSplatObject splatObject = GetSceneSplatObject(splatDataIndex);
            if (splatObject == null)
            {
                SetButtonEnabled(slotButton, false, "", _defaultSplatColor, _scrollDisabledColor);
                continue;
            }

            bool isRendered = gaussianSplatRenderer != null && gaussianSplatRenderer.GetCurrentSplatObject() == splatObject.gameObject;
            bool isActive = splatObject.gameObject.activeInHierarchy;
            string label = splatObject.gameObject.name;
            if (isRendered)
            {
                label += GetRenderingSuffix();
            }
            else if (isActive)
            {
                label += GetEnabledSuffix();
            }

            SetButtonEnabled(slotButton, true, label, isRendered ? _selectedSplatColor : _defaultSplatColor, _scrollDisabledColor);
        }

        SetButtonEnabled(splatScrollUpButton, _splatListStartIndex > 0, GetScrollUpLabel(), _scrollEnabledColor, _scrollDisabledColor);
        SetButtonEnabled(splatScrollDownButton, _splatListStartIndex < maxStartIndex, GetScrollDownLabel(), _scrollEnabledColor, _scrollDisabledColor);
    }

    void RefreshSortingControls()
    {
        if (cameraQuantizationText != null)
        {
            cameraQuantizationText.text = FormatFloat(gaussianSplatRenderer.GetCameraPositionQuantization());
        }

        if (sortingStepsText != null)
        {
            sortingStepsText.text = gaussianSplatRenderer.GetSortPipelineFrames().ToString();
        }

        if (alwaysUpdateButton != null)
        {
            bool combinedMode = gaussianSplatRenderer.IsCombinedRenderingMode();
            bool alwaysUpdate = gaussianSplatRenderer.GetAlwaysUpdate();
            alwaysUpdateButton.interactable = !combinedMode;
            ApplyButtonVisual(alwaysUpdateButton, alwaysUpdate ? GetToggleOnLabel() : GetToggleOffLabel(), alwaysUpdate ? _toggleEnabledColor : _toggleDisabledColor);
        }
    }

    void RefreshMaterialControls()
    {
    #if UNITY_EDITOR && !COMPILER_UDONSHARP
        bool allowWriteBack = EditorApplication.isPlaying;
    #else
        bool allowWriteBack = true;
    #endif

        if (vrcLightVolumesButton != null)
        {
            bool enabled = gaussianSplatRenderer.GetUseVrcLightVolumes();
            ApplyButtonVisual(vrcLightVolumesButton, enabled ? GetToggleOnLabel() : GetToggleOffLabel(), enabled ? _toggleEnabledColor : _toggleDisabledColor);
        }

        SyncShBandSlider(allowWriteBack);
        SyncAntiAliasingSlider(allowWriteBack);
        SyncLightVolumeIntensitySlider(allowWriteBack);
        SyncAlphaCutoffSlider(allowWriteBack);
    }

    bool SliderValueChanged(float currentValue, float previousValue)
    {
        return Mathf.Abs(currentValue - previousValue) > 0.0001f;
    }

    void SyncShBandSlider(bool allowWriteBack)
    {
        if (shBandSlider == null)
        {
            return;
        }

        int maxBand = gaussianSplatRenderer.GetSelectedSplatMaxSHBand();
        if (!Mathf.Approximately(shBandSlider.maxValue, maxBand))
        {
            shBandSlider.maxValue = maxBand;
        }

        int currentBand = gaussianSplatRenderer.GetCurrentSHBand();
        if (!_sliderValuesInitialized)
        {
            shBandSlider.SetValueWithoutNotify(currentBand);
            _lastShBandSliderValue = currentBand;
        }
        else if (!allowWriteBack || SliderValueChanged(currentBand, _lastShBandSliderValue))
        {
            shBandSlider.SetValueWithoutNotify(currentBand);
            _lastShBandSliderValue = currentBand;
        }
        else if (SliderValueChanged(shBandSlider.value, _lastShBandSliderValue))
        {
            gaussianSplatRenderer.SetSHBand(Mathf.RoundToInt(shBandSlider.value));
            currentBand = gaussianSplatRenderer.GetCurrentSHBand();
            shBandSlider.SetValueWithoutNotify(currentBand);
            _lastShBandSliderValue = currentBand;
        }

        if (shBandText != null)
        {
            shBandText.text = currentBand.ToString();
        }
    }

    void SyncAntiAliasingSlider(bool allowWriteBack)
    {
        if (antiAliasingSlider == null)
        {
            return;
        }

        float currentValue = gaussianSplatRenderer.GetAntiAliasing();
        if (!_sliderValuesInitialized)
        {
            antiAliasingSlider.SetValueWithoutNotify(currentValue);
            _lastAntiAliasingSliderValue = currentValue;
        }
        else if (!allowWriteBack || SliderValueChanged(currentValue, _lastAntiAliasingSliderValue))
        {
            antiAliasingSlider.SetValueWithoutNotify(currentValue);
            _lastAntiAliasingSliderValue = currentValue;
        }
        else if (SliderValueChanged(antiAliasingSlider.value, _lastAntiAliasingSliderValue))
        {
            gaussianSplatRenderer.SetAntiAliasing(antiAliasingSlider.value);
            currentValue = gaussianSplatRenderer.GetAntiAliasing();
            antiAliasingSlider.SetValueWithoutNotify(currentValue);
            _lastAntiAliasingSliderValue = currentValue;
        }

        if (antiAliasingText != null)
        {
            antiAliasingText.text = FormatFloat(currentValue);
        }
    }

    void SyncLightVolumeIntensitySlider(bool allowWriteBack)
    {
        if (lightVolumeIntensitySlider == null)
        {
            return;
        }

        float currentValue = gaussianSplatRenderer.GetLightVolumeIntensity();
        if (!_sliderValuesInitialized)
        {
            lightVolumeIntensitySlider.SetValueWithoutNotify(currentValue);
            _lastLightVolumeIntensitySliderValue = currentValue;
        }
        else if (!allowWriteBack || SliderValueChanged(currentValue, _lastLightVolumeIntensitySliderValue))
        {
            lightVolumeIntensitySlider.SetValueWithoutNotify(currentValue);
            _lastLightVolumeIntensitySliderValue = currentValue;
        }
        else if (SliderValueChanged(lightVolumeIntensitySlider.value, _lastLightVolumeIntensitySliderValue))
        {
            gaussianSplatRenderer.SetLightVolumeIntensity(lightVolumeIntensitySlider.value);
            currentValue = gaussianSplatRenderer.GetLightVolumeIntensity();
            lightVolumeIntensitySlider.SetValueWithoutNotify(currentValue);
            _lastLightVolumeIntensitySliderValue = currentValue;
        }

        if (lightVolumeIntensityText != null)
        {
            lightVolumeIntensityText.text = FormatFloat(currentValue);
        }
    }

    void SyncAlphaCutoffSlider(bool allowWriteBack)
    {
        if (alphaCutoffSlider == null)
        {
            return;
        }

        float currentValue = gaussianSplatRenderer.alphaCutoff;
        if (!_sliderValuesInitialized)
        {
            alphaCutoffSlider.SetValueWithoutNotify(currentValue);
            _lastAlphaCutoffSliderValue = currentValue;
        }
        else if (!allowWriteBack || SliderValueChanged(currentValue, _lastAlphaCutoffSliderValue))
        {
            alphaCutoffSlider.SetValueWithoutNotify(currentValue);
            _lastAlphaCutoffSliderValue = currentValue;
        }
        else if (SliderValueChanged(alphaCutoffSlider.value, _lastAlphaCutoffSliderValue))
        {
            gaussianSplatRenderer.SetAlphaCutoff(alphaCutoffSlider.value);
            currentValue = gaussianSplatRenderer.alphaCutoff;
            alphaCutoffSlider.SetValueWithoutNotify(currentValue);
            _lastAlphaCutoffSliderValue = currentValue;
        }

        if (alphaCutoffText != null)
        {
            alphaCutoffText.text = FormatFloat(currentValue);
        }
    }

    void SelectSplatSlot(int slotIndex)
    {
        if (gaussianSplatRenderer != null && gaussianSplatRenderer.IsCombinedRenderingMode())
        {
            return;
        }

        int splatDataIndex = _splatListStartIndex + slotIndex;
        GaussianSplatObject selectedSplatObject = GetSceneSplatObject(splatDataIndex);
        if (selectedSplatObject == null)
        {
            return;
        }

        SelectSplatObject(selectedSplatObject);
        RefreshUI();
    }

    public void SelectSplatSlot0() { SelectSplatSlot(0); }
    public void SelectSplatSlot1() { SelectSplatSlot(1); }
    public void SelectSplatSlot2() { SelectSplatSlot(2); }
    public void SelectSplatSlot3() { SelectSplatSlot(3); }
    public void SelectSplatSlot4() { SelectSplatSlot(4); }
    public void SelectSplatSlot5() { SelectSplatSlot(5); }
    public void SelectSplatSlot6() { SelectSplatSlot(6); }
    public void SelectSplatSlot7() { SelectSplatSlot(7); }
    public void SelectSplatSlot8() { SelectSplatSlot(8); }
    public void SelectSplatSlot9() { SelectSplatSlot(9); }
    public void SelectSplatSlot10() { SelectSplatSlot(10); }
    public void SelectSplatSlot11() { SelectSplatSlot(11); }
    public void SelectSplatSlot12() { SelectSplatSlot(12); }
    public void SelectSplatSlot13() { SelectSplatSlot(13); }
    public void SelectSplatSlot14() { SelectSplatSlot(14); }
    public void SelectSplatSlot15() { SelectSplatSlot(15); }

    public void ScrollSplatListUp()
    {
        _splatListStartIndex = Mathf.Max(0, _splatListStartIndex - 1);
        RefreshUI();
    }

    public void ScrollSplatListDown()
    {
        int visibleButtonCount = splatButtons == null ? 0 : splatButtons.Length;
        int totalSplatCount = GetSceneSplatCount();
        int maxStartIndex = Mathf.Max(0, totalSplatCount - visibleButtonCount);
        _splatListStartIndex = Mathf.Min(maxStartIndex, _splatListStartIndex + 1);
        RefreshUI();
    }

    public override void OnDeserialization()
    {
        if (gaussianSplatRenderer == null || !gaussianSplatRenderer.IsCombinedRenderingMode())
        {
            ApplySyncedSplatObjectSelection();
        }
        RefreshUI();
    }

    public void RefreshUI()
    {
        FindRenderer();
        RefreshSceneSplatObjects();
        RefreshLocalizedLabels();
        RefreshRenderingModeLayout(gaussianSplatRenderer != null && gaussianSplatRenderer.IsCombinedRenderingMode());

        if (gaussianSplatRenderer == null)
        {
            if (currentSplatText != null)
            {
                currentSplatText.text = GetCurrentSplatNoneLabel();
            }

            if (alphaCutoffSlider != null)
            {
                alphaCutoffSlider.value = DefaultAlphaCutoff;
            }

            if (alphaCutoffText != null)
            {
                alphaCutoffText.text = FormatFloat(DefaultAlphaCutoff);
            }

            RefreshSplatButtons();
            return;
        }

        if (currentSplatText != null)
        {
            bool combinedMode = gaussianSplatRenderer.IsCombinedRenderingMode();
            string currentSplatName = gaussianSplatRenderer.GetCurrentSplatName();
            currentSplatText.text = combinedMode
                ? Localize("Rendering Mode: Combined", "表示モード: 統合")
                : Localize("Rendering Mode: Single", "表示モード: 単体") + "\n" + (currentSplatName == "None"
                    ? GetCurrentSplatNoneLabel()
                    : GetCurrentSplatPrefix() + currentSplatName);
        }

        if (gaussianScaleText != null)
        {
            gaussianScaleText.text = FormatFloat(gaussianSplatRenderer.gaussianScale);
        }

        if (alphaCutoffText != null)
        {
            alphaCutoffText.text = FormatFloat(gaussianSplatRenderer.alphaCutoff);
        }

        RefreshSortingControls();
        RefreshMaterialControls();
        RefreshSplatButtons();
        _sliderValuesInitialized = true;
    }

    public void IncreaseMinSortDistance()
    {
    }

    public void DecreaseMinSortDistance()
    {
    }

    public void IncreaseMaxSortDistance()
    {
    }

    public void DecreaseMaxSortDistance()
    {
    }

    public void IncreaseCameraQuantization()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetCameraPositionQuantization(gaussianSplatRenderer.GetCameraPositionQuantization() + cameraQuantizationStep);
        RefreshUI();
    }

    public void DecreaseCameraQuantization()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetCameraPositionQuantization(gaussianSplatRenderer.GetCameraPositionQuantization() - cameraQuantizationStep);
        RefreshUI();
    }

    public void IncreaseSortingSteps()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetSortPipelineFrames(gaussianSplatRenderer.GetSortPipelineFrames() + 1);
        RefreshUI();
    }

    public void DecreaseSortingSteps()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetSortPipelineFrames(gaussianSplatRenderer.GetSortPipelineFrames() - 1);
        RefreshUI();
    }

    public void ToggleAlwaysUpdate()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.ToggleAlwaysUpdate();
        RefreshUI();
    }

    public void ToggleVrcLightVolumes()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.ToggleVrcLightVolumes();
        RefreshUI();
    }

    public void IncreaseGaussianScale()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetGaussianScale(gaussianSplatRenderer.gaussianScale + gaussianScaleStep);
        RefreshUI();
    }

    public void DecreaseGaussianScale()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetGaussianScale(gaussianSplatRenderer.gaussianScale - gaussianScaleStep);
        RefreshUI();
    }

    void SetLanguage(int language)
    {
        selectedLanguage = Mathf.Clamp(language, LanguageEnglish, LanguageJapanese);
        RefreshUI();
    }

    public void SetLanguageEnglish()
    {
        SetLanguage(LanguageEnglish);
    }

    public void SetLanguageJapanese()
    {
        SetLanguage(LanguageJapanese);
    }

}

}
