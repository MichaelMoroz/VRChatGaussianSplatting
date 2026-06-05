using UdonSharp;
using TMPro;
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
    const int SliderShBand = 0;
    const int SliderAntiAliasing = 1;
    const int SliderLightVolumeIntensity = 2;
    const int SliderAlphaCutoff = 3;
    const int SliderAlphaCull = 4;
    const int SliderLODCull = 5;
    const float DefaultAlphaCutoff = 0.04f;
    const float MaxAlphaCutoff = 0.3f;
    const float DefaultAlphaCull = 0.04f;
    const float MaxAlphaCull = 0.3f;
    const float DefaultLODCull = 0.0f;
    const float MaxLODCull = 0.1f;
    const float DefaultPanelWidth = 1120.0f;
    const float CombinedPanelWidth = 560.0f;
    const float BackgroundPadding = 24.0f;
    const float SliderChangeThreshold = 0.0001f;

    public GaussianSplatRenderer gaussianSplatRenderer;
    public TextMeshProUGUI currentSplatText, sortingSectionText, cameraQuantizationLabelText, cameraQuantizationText;
    public TextMeshProUGUI alwaysUpdateLabelText, materialSectionText, shBandLabelText, shBandText, vrcLightVolumesLabelText, antiAliasingLabelText, antiAliasingText;
    public TextMeshProUGUI lightVolumeIntensityLabelText, lightVolumeIntensityText, gaussianScaleLabelText, gaussianScaleText, alphaCutoffLabelText, alphaCutoffText;
    public TextMeshProUGUI alphaCullLabelText, alphaCullText;
    public TextMeshProUGUI lodCullLabelText, lodCullText, qualitySectionText;
    public TextMeshProUGUI languageSectionText, splatSectionText;
    public Button alwaysUpdateButton, vrcLightVolumesButton, englishLanguageButton, japaneseLanguageButton, splatScrollUpButton, splatScrollDownButton;
    public Button qualityVeryLowButton, qualityLowButton, qualityMediumButton, qualityHighButton;
    public Slider shBandSlider, antiAliasingSlider, lightVolumeIntensitySlider, alphaCutoffSlider;
    public Slider alphaCullSlider, lodCullSlider;
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
    bool _layoutDefaultsInitialized;
    float _lastShBandSliderValue;
    float _lastAntiAliasingSliderValue;
    float _lastLightVolumeIntensitySliderValue;
    float _lastAlphaCutoffSliderValue;
    float _lastAlphaCullSliderValue;
    float _lastLODCullSliderValue;
    float _defaultCanvasWidth;
    float _defaultPanelWidth;
    GaussianSplatObject[] _sceneSplatObjects;
    RectTransform _canvasRect;
    RectTransform _panelRect;
    Transform _backgroundTransform;
    GameObject _splatColumnObject;
    Vector3 _defaultBackgroundScale;

    void Start()
    {
        if (SkipRuntimeRefresh()) return;
        ApplySyncedSplatObjectSelection();
        RefreshUI();
    }

    void Update()
    {
        if (SkipRuntimeRefresh()) return;
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

    internal static void RequestEditorRefresh() { _editorRefreshRequested = true; }

    static bool IsSceneObject(Component component)
    {
        if (component == null)
        {
            return false;
        }
        GameObject rootObject = component.transform.root != null ? component.transform.root.gameObject : component.gameObject;
        return rootObject != null && !EditorUtility.IsPersistent(rootObject) && !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(rootObject.scene) && (component.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) == 0;
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
            if (!IsSceneObject(ui))
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

    bool SyncEditorSerializedState()
    {
        if (EditorUtility.IsPersistent(this) || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
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

    bool SkipRuntimeRefresh()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        return !Application.isPlaying;
#else
        return false;
#endif
    }

    string Localize(string english, string japanese) { return selectedLanguage == LanguageJapanese ? japanese : english; }
    string FormatFloat(float value) { return (Mathf.Round(value * 100.0f) * 0.01f).ToString(); }
    string ToggleLabel(bool enabled) { return Localize(enabled ? "On" : "Off", enabled ? "オン" : "オフ"); }
    string ScrollLabel(bool up) { return Localize(up ? "Up" : "Down", up ? "上へ" : "下へ"); }
    string CurrentSplatNoneLabel() { return Localize("Current Splat: None", "現在のスプラット: なし"); }
    string RenderedSplatCountLabel(int count) { return Localize("Rendered Splats: ", "描画スプラット数: ") + count; }

    void SetText(TextMeshProUGUI text, string value) { if (text != null && text.text != value) text.text = value; }
    void SetLocalizedText(TextMeshProUGUI text, string english, string japanese) { SetText(text, Localize(english, japanese)); }
    void SetActive(Component component, bool active) { if (component != null && component.gameObject.activeSelf != active) component.gameObject.SetActive(active); }
    void SetInteractable(Selectable selectable, bool interactable) { if (selectable != null && selectable.interactable != interactable) selectable.interactable = interactable; }
    void SetSliderWithoutNotify(Slider slider, float value) { if (slider != null && !Mathf.Approximately(slider.value, value)) slider.SetValueWithoutNotify(value); }
    TextMeshProUGUI ResolveSubtitleText()
    {
        Transform subtitleTransform = transform.Find("Panel/Body Row/Settings Column/Subtitle");
        return subtitleTransform != null ? subtitleTransform.GetComponent<TextMeshProUGUI>() : null;
    }

    void RefreshLocalizedLabels()
    {
        SetText(ResolveSubtitleText(), Localize(
            "Github: https://github.com/MichaelMoroz/VRChatGaussianSplatting\nDeveloped by misha_m",
            "Github: https://github.com/MichaelMoroz/VRChatGaussianSplatting\n開発: misha_m"));
        SetLocalizedText(sortingSectionText, "Sorting Settings", "ソート設定");
        SetLocalizedText(cameraQuantizationLabelText, "Camera move amount to trigger resort", "再ソートするカメラ移動量");
        SetLocalizedText(alwaysUpdateLabelText, "Sort every frame", "毎フレームソート");
        SetLocalizedText(materialSectionText, "Material Settings", "マテリアル設定");
        SetLocalizedText(shBandLabelText, "SH Band (global)", "SH バンド (共有)");
        SetLocalizedText(vrcLightVolumesLabelText, "VRC Light Volumes (global)", "VRC Light Volumes (共有)");
        SetLocalizedText(lightVolumeIntensityLabelText, "Light Volume Intensity", "ライトボリューム強度");
        SetLocalizedText(antiAliasingLabelText, "Antialiasing", "アンチエイリアス");
        SetLocalizedText(gaussianScaleLabelText, "Gaussian Scale (global)", "ガウススケール (共有)");
        SetLocalizedText(alphaCutoffLabelText, "Alpha Cutoff\n(lower = better quality)", "アルファカットオフ\n(低いほど高品質)");
        SetLocalizedText(alphaCullLabelText, "Alpha Cull\n(higher = fewer splats)", "アルファカリング\n(高いほどスプラット減少)");
        SetLocalizedText(lodCullLabelText, "LOD Cull\n(higher = fewer splats)", "距離カリング\n(高いほどスプラット減少)");
        SetLocalizedText(qualitySectionText, "Quality", "品質");
        SetLocalizedText(languageSectionText, "Language", "言語");
        SetLocalizedText(splatSectionText, "Splat Object (global)", "スプラットオブジェクト (共有)");
        RefreshLanguageButtons();
    }

    void RefreshLanguageButtons()
    {
        if (englishLanguageButton != null) { SetInteractable(englishLanguageButton, true); ApplyButtonVisual(englishLanguageButton, "English", selectedLanguage == LanguageEnglish ? _selectedSplatColor : _defaultSplatColor); }
        if (japaneseLanguageButton != null) { SetInteractable(japaneseLanguageButton, true); ApplyButtonVisual(japaneseLanguageButton, "日本語", selectedLanguage == LanguageJapanese ? _selectedSplatColor : _defaultSplatColor); }
    }

    void FindRenderer()
    {
        if (gaussianSplatRenderer != null) return;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        gaussianSplatRenderer = GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);
#else
        GameObject rendererObject = GameObject.Find("GaussianSplatRenderer");
        if (rendererObject != null) gaussianSplatRenderer = rendererObject.GetComponent<GaussianSplatRenderer>();
#endif
    }

    static bool SplatObjectArraysMatch(GaussianSplatObject[] left, GaussianSplatObject[] right)
    {
        if (left == right) return true;
        if (left == null || right == null || left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
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
            if (!IsSceneObject(currentObject) || currentObject.gameObject.scene != gameObject.scene)
            {
                continue;
            }
            sceneObjects.Add(currentObject);
        }
        _sceneSplatObjects = sceneObjects.ToArray();
        if (!SplatObjectArraysMatch(cachedSceneSplatObjects, _sceneSplatObjects))
        {
            cachedSceneSplatObjects = _sceneSplatObjects;
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

    int FindSceneSplatObjectIndex(GaussianSplatObject targetSplatObject)
    {
        if (targetSplatObject == null || _sceneSplatObjects == null)
        {
            return -1;
        }
        for (int i = 0; i < _sceneSplatObjects.Length; i++)
        {
            if (_sceneSplatObjects[i] == targetSplatObject)
            {
                return i;
            }
        }
        return -1;
    }

    void EnsureLocalOwnership() { if (Networking.LocalPlayer != null) Networking.SetOwner(Networking.LocalPlayer, gameObject); }
    void RequestSyncedSelectionUpdate() { if (Networking.LocalPlayer != null) RequestSerialization(); }

    void ApplySplatObjectSelection(GaussianSplatObject selectedSplatObject)
    {
        if (selectedSplatObject == null || _sceneSplatObjects == null)
        {
            return;
        }
        for (int i = 0; i < _sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject sceneSplatObject = _sceneSplatObjects[i];
            if (sceneSplatObject != null && !sceneSplatObject.gameObject.activeSelf)
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
        if (_sceneSplatObjects == null || syncedSelectedSplatObjectIndex < 0 || syncedSelectedSplatObjectIndex >= _sceneSplatObjects.Length)
        {
            return false;
        }
        GaussianSplatObject selectedSplatObject = _sceneSplatObjects[syncedSelectedSplatObjectIndex];
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

    void ApplyButtonVisual(Button button, string labelText, Color backgroundColor)
    {
        if (button == null)
        {
            return;
        }
        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            if (image.color != backgroundColor)
            {
                image.color = backgroundColor;
            }
        }
        ColorBlock colors = button.colors;
        colors.normalColor = backgroundColor;
        colors.highlightedColor = backgroundColor * 1.1f;
        colors.pressedColor = backgroundColor * 0.85f;
        colors.selectedColor = backgroundColor;
        colors.disabledColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0.4f);
        if (!button.colors.Equals(colors))
        {
            button.colors = colors;
        }
        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
        SetText(label, labelText);
    }

    void SetButton(Button button, bool enabled, string label, Color enabledColor, Color disabledColor)
    {
        if (button == null)
        {
            return;
        }
        if (!button.gameObject.activeSelf)
        {
            button.gameObject.SetActive(true);
        }
        SetInteractable(button, enabled);
        ApplyButtonVisual(button, label, enabled ? enabledColor : disabledColor);
    }

    void SetSplatListVisible(bool visible)
    {
        SetActive(splatSectionText, visible);
        SetActive(splatScrollUpButton, visible);
        SetActive(splatScrollDownButton, visible);
        if (splatButtons == null)
        {
            return;
        }
        for (int i = 0; i < splatButtons.Length; i++)
        {
            SetActive(splatButtons[i], visible);
        }
    }

    void EnsureLayoutCache()
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
        if (_layoutDefaultsInitialized)
        {
            return;
        }
        _defaultCanvasWidth = _canvasRect != null ? _canvasRect.sizeDelta.x : DefaultPanelWidth;
        _defaultPanelWidth = _panelRect != null ? _panelRect.sizeDelta.x : DefaultPanelWidth;
        _defaultBackgroundScale = _backgroundTransform != null ? _backgroundTransform.localScale : Vector3.one;
        _layoutDefaultsInitialized = true;
    }

    void RefreshRenderingModeLayout(bool combinedMode)
    {
        EnsureLayoutCache();
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
        int visibleButtonCount = splatButtons == null ? 0 : splatButtons.Length;
        bool combinedMode = gaussianSplatRenderer != null && gaussianSplatRenderer.IsCombinedRenderingMode();
        SetSplatListVisible(!combinedMode);
        if (visibleButtonCount == 0 || combinedMode)
        {
            return;
        }
        int totalSplatCount = _sceneSplatObjects == null ? 0 : _sceneSplatObjects.Length;
        if (totalSplatCount == 0)
        {
            for (int i = 0; i < visibleButtonCount; i++)
            {
                SetButton(splatButtons[i], false, string.Empty, _defaultSplatColor, _scrollDisabledColor);
            }
            SetButton(splatScrollUpButton, false, ScrollLabel(true), _scrollEnabledColor, _scrollDisabledColor);
            SetButton(splatScrollDownButton, false, ScrollLabel(false), _scrollEnabledColor, _scrollDisabledColor);
            return;
        }
        int maxStartIndex = Mathf.Max(0, totalSplatCount - visibleButtonCount);
        _splatListStartIndex = Mathf.Clamp(_splatListStartIndex, 0, maxStartIndex);
        GameObject currentSplatObject = gaussianSplatRenderer != null ? gaussianSplatRenderer.GetCurrentSplatObject() : null;
        string renderingSuffix = Localize(" (Rendering)", " (表示中)");
        string enabledSuffix = Localize(" (On)", " (有効)");
        for (int i = 0; i < visibleButtonCount; i++)
        {
            Button slotButton = splatButtons[i];
            int splatDataIndex = _splatListStartIndex + i;
            if (slotButton == null)
            {
                continue;
            }
            if (splatDataIndex >= totalSplatCount)
            {
                SetButton(slotButton, false, string.Empty, _defaultSplatColor, _scrollDisabledColor);
                continue;
            }
            GaussianSplatObject splatObject = _sceneSplatObjects[splatDataIndex];
            if (splatObject == null)
            {
                SetButton(slotButton, false, string.Empty, _defaultSplatColor, _scrollDisabledColor);
                continue;
            }
            bool isRendered = currentSplatObject == splatObject.gameObject;
            string label = splatObject.gameObject.name;
            if (isRendered)
            {
                label += renderingSuffix;
            }
            else if (splatObject.gameObject.activeInHierarchy)
            {
                label += enabledSuffix;
            }
            SetButton(slotButton, true, label, isRendered ? _selectedSplatColor : _defaultSplatColor, _scrollDisabledColor);
        }
        SetButton(splatScrollUpButton, _splatListStartIndex > 0, ScrollLabel(true), _scrollEnabledColor, _scrollDisabledColor);
        SetButton(splatScrollDownButton, _splatListStartIndex < maxStartIndex, ScrollLabel(false), _scrollEnabledColor, _scrollDisabledColor);
    }

    void RefreshSortingControls()
    {
        SetText(cameraQuantizationText, FormatFloat(gaussianSplatRenderer.GetCameraPositionQuantization()));
        if (alwaysUpdateButton != null)
        {
            bool alwaysUpdate = gaussianSplatRenderer.GetAlwaysUpdate();
            SetInteractable(alwaysUpdateButton, !gaussianSplatRenderer.IsCombinedRenderingMode());
            ApplyButtonVisual(alwaysUpdateButton, ToggleLabel(alwaysUpdate), alwaysUpdate ? _toggleEnabledColor : _toggleDisabledColor);
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
            ApplyButtonVisual(vrcLightVolumesButton, ToggleLabel(enabled), enabled ? _toggleEnabledColor : _toggleDisabledColor);
        }
        SyncSlider(shBandSlider, shBandText, SliderShBand, allowWriteBack);
        SyncSlider(antiAliasingSlider, antiAliasingText, SliderAntiAliasing, allowWriteBack);
        SyncSlider(lightVolumeIntensitySlider, lightVolumeIntensityText, SliderLightVolumeIntensity, allowWriteBack);
        SyncSlider(alphaCutoffSlider, alphaCutoffText, SliderAlphaCutoff, allowWriteBack);
        SyncSlider(alphaCullSlider, alphaCullText, SliderAlphaCull, allowWriteBack);
        SyncSlider(lodCullSlider, lodCullText, SliderLODCull, allowWriteBack);
        RefreshQualityButtons();
    }

    void RefreshQualityButtons()
    {
        int activePreset = gaussianSplatRenderer != null ? gaussianSplatRenderer.GetQualityPresetIndex() : -1;
        SetQualityButton(qualityVeryLowButton, 0, activePreset, Localize("Very Low", "最低"));
        SetQualityButton(qualityLowButton, 1, activePreset, Localize("Low", "低"));
        SetQualityButton(qualityMediumButton, 2, activePreset, Localize("Medium", "中"));
        SetQualityButton(qualityHighButton, 3, activePreset, Localize("High", "高"));
    }

    void SetQualityButton(Button button, int presetIndex, int activePreset, string label)
    {
        if (button == null)
        {
            return;
        }
        ApplyButtonVisual(button, label, presetIndex == activePreset ? _selectedSplatColor : _defaultSplatColor);
    }

    bool SliderValueChanged(float currentValue, float previousValue) { return Mathf.Abs(currentValue - previousValue) > SliderChangeThreshold; }

    float GetSliderValue(int sliderKind)
    {
        switch (sliderKind)
        {
            case SliderShBand: return gaussianSplatRenderer.GetCurrentSHBand();
            case SliderAntiAliasing: return gaussianSplatRenderer.GetAntiAliasing();
            case SliderLightVolumeIntensity: return gaussianSplatRenderer.GetLightVolumeIntensity();
            case SliderAlphaCull: return gaussianSplatRenderer.GetAlphaCull();
            case SliderLODCull: return gaussianSplatRenderer.GetLODCull();
            default: return gaussianSplatRenderer.alphaCutoff;
        }
    }

    float GetLastSliderValue(int sliderKind)
    {
        switch (sliderKind)
        {
            case SliderShBand: return _lastShBandSliderValue;
            case SliderAntiAliasing: return _lastAntiAliasingSliderValue;
            case SliderLightVolumeIntensity: return _lastLightVolumeIntensitySliderValue;
            case SliderAlphaCull: return _lastAlphaCullSliderValue;
            case SliderLODCull: return _lastLODCullSliderValue;
            default: return _lastAlphaCutoffSliderValue;
        }
    }

    void SetLastSliderValue(int sliderKind, float value)
    {
        switch (sliderKind)
        {
            case SliderShBand:
                _lastShBandSliderValue = value;
                return;
            case SliderAntiAliasing:
                _lastAntiAliasingSliderValue = value;
                return;
            case SliderLightVolumeIntensity:
                _lastLightVolumeIntensitySliderValue = value;
                return;
            case SliderAlphaCull:
                _lastAlphaCullSliderValue = value;
                return;
            case SliderLODCull:
                _lastLODCullSliderValue = value;
                return;
            default:
                _lastAlphaCutoffSliderValue = value;
                return;
        }
    }

    void SetSliderValue(int sliderKind, float value)
    {
        switch (sliderKind)
        {
            case SliderShBand:
                gaussianSplatRenderer.SetSHBand(Mathf.RoundToInt(value));
                return;
            case SliderAntiAliasing:
                gaussianSplatRenderer.SetAntiAliasing(value);
                return;
            case SliderLightVolumeIntensity:
                gaussianSplatRenderer.SetLightVolumeIntensity(value);
                return;
            case SliderAlphaCull:
                gaussianSplatRenderer.SetAlphaCull(value);
                return;
            case SliderLODCull:
                gaussianSplatRenderer.SetLODCull(value);
                return;
            default:
                gaussianSplatRenderer.SetAlphaCutoff(value);
                return;
        }
    }

    void SyncSlider(Slider slider, TextMeshProUGUI valueText, int sliderKind, bool allowWriteBack)
    {
        if (slider == null)
        {
            return;
        }
        if (sliderKind == SliderShBand)
        {
            float maxBand = gaussianSplatRenderer.GetSelectedSplatMaxSHBand();
            if (!Mathf.Approximately(slider.maxValue, maxBand))
            {
                slider.maxValue = maxBand;
            }
        }
        else if (sliderKind == SliderAlphaCutoff && !Mathf.Approximately(slider.maxValue, MaxAlphaCutoff))
        {
            slider.maxValue = MaxAlphaCutoff;
        }
        else if (sliderKind == SliderAlphaCull && !Mathf.Approximately(slider.maxValue, MaxAlphaCull))
        {
            slider.maxValue = MaxAlphaCull;
        }
        else if (sliderKind == SliderLODCull && !Mathf.Approximately(slider.maxValue, MaxLODCull))
        {
            slider.maxValue = MaxLODCull;
        }
        float currentValue = GetSliderValue(sliderKind);
        float lastValue = GetLastSliderValue(sliderKind);
        bool sliderNeedsRefresh = !_sliderValuesInitialized || SliderValueChanged(currentValue, lastValue) || (!allowWriteBack && SliderValueChanged(slider.value, currentValue));
        if (sliderNeedsRefresh)
        {
            SetSliderWithoutNotify(slider, currentValue);
            SetLastSliderValue(sliderKind, currentValue);
        }
        else if (allowWriteBack && SliderValueChanged(slider.value, lastValue))
        {
            SetSliderValue(sliderKind, slider.value);
            currentValue = GetSliderValue(sliderKind);
            SetSliderWithoutNotify(slider, currentValue);
            SetLastSliderValue(sliderKind, currentValue);
        }
        if (valueText != null)
        {
            SetText(valueText, sliderKind == SliderShBand ? Mathf.RoundToInt(currentValue).ToString() : FormatFloat(currentValue));
        }
    }

    void SelectSplatSlot(int slotIndex)
    {
        if (gaussianSplatRenderer != null && gaussianSplatRenderer.IsCombinedRenderingMode())
        {
            return;
        }
        RefreshSceneSplatObjects();
        int splatDataIndex = _splatListStartIndex + slotIndex;
        GaussianSplatObject selectedSplatObject = _sceneSplatObjects != null && splatDataIndex >= 0 && splatDataIndex < _sceneSplatObjects.Length ? _sceneSplatObjects[splatDataIndex] : null;
        if (selectedSplatObject == null)
        {
            return;
        }
        SelectSplatObject(selectedSplatObject);
        RefreshUI();
    }

    void StepCameraQuantization(float delta)
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }
        gaussianSplatRenderer.SetCameraPositionQuantization(gaussianSplatRenderer.GetCameraPositionQuantization() + delta);
        RefreshUI();
    }

    void StepGaussianScale(float delta)
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }
        gaussianSplatRenderer.SetGaussianScale(gaussianSplatRenderer.gaussianScale + delta);
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

    public void ScrollSplatListUp() { _splatListStartIndex = Mathf.Max(0, _splatListStartIndex - 1); RefreshUI(); }

    public void ScrollSplatListDown()
    {
        int visibleButtonCount = splatButtons == null ? 0 : splatButtons.Length;
        int totalSplatCount = _sceneSplatObjects == null ? 0 : _sceneSplatObjects.Length;
        _splatListStartIndex = Mathf.Min(Mathf.Max(0, totalSplatCount - visibleButtonCount), _splatListStartIndex + 1);
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
        bool combinedMode = gaussianSplatRenderer != null && gaussianSplatRenderer.IsCombinedRenderingMode();
        RefreshRenderingModeLayout(combinedMode);
        if (gaussianSplatRenderer == null)
        {
            SetText(currentSplatText, CurrentSplatNoneLabel() + "\n" + RenderedSplatCountLabel(0));
            SetSliderWithoutNotify(alphaCutoffSlider, DefaultAlphaCutoff);
            SetText(alphaCutoffText, FormatFloat(DefaultAlphaCutoff));
            SetSliderWithoutNotify(alphaCullSlider, DefaultAlphaCull);
            SetText(alphaCullText, FormatFloat(DefaultAlphaCull));
            SetSliderWithoutNotify(lodCullSlider, DefaultLODCull);
            SetText(lodCullText, FormatFloat(DefaultLODCull));
            RefreshSplatButtons();
            return;
        }
        if (currentSplatText != null)
        {
            string modeLabel = combinedMode ? Localize("Rendering Mode: Combined", "表示モード: 統合") : Localize("Rendering Mode: Single", "表示モード: 単体");
            string currentSplatName = gaussianSplatRenderer.GetCurrentSplatName();
            string renderedCountLabel = RenderedSplatCountLabel(gaussianSplatRenderer.GetCurrentRenderedSplatCount());
            SetText(currentSplatText, combinedMode
                ? modeLabel + "\n" + renderedCountLabel
                : modeLabel + "\n" + (currentSplatName == "None" ? CurrentSplatNoneLabel() : Localize("Current Splat: ", "現在のスプラット: ") + currentSplatName) + "\n" + renderedCountLabel);
        }
        SetText(gaussianScaleText, FormatFloat(gaussianSplatRenderer.gaussianScale));
        SetText(alphaCutoffText, FormatFloat(gaussianSplatRenderer.alphaCutoff));
        SetText(alphaCullText, FormatFloat(gaussianSplatRenderer.alphaCull));
        SetText(lodCullText, FormatFloat(gaussianSplatRenderer.lodCull));
        RefreshSortingControls();
        RefreshMaterialControls();
        RefreshSplatButtons();
        _sliderValuesInitialized = true;
    }

    public void IncreaseCameraQuantization() { StepCameraQuantization(cameraQuantizationStep); }
    public void DecreaseCameraQuantization() { StepCameraQuantization(-cameraQuantizationStep); }

    public void ToggleAlwaysUpdate() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.ToggleAlwaysUpdate(); RefreshUI(); }
    public void ToggleVrcLightVolumes() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.ToggleVrcLightVolumes(); RefreshUI(); }

    public void IncreaseGaussianScale() { StepGaussianScale(gaussianScaleStep); }
    public void DecreaseGaussianScale() { StepGaussianScale(-gaussianScaleStep); }

    void SetLanguage(int language) { selectedLanguage = Mathf.Clamp(language, LanguageEnglish, LanguageJapanese); RefreshUI(); }
    public void SetLanguageEnglish() { SetLanguage(LanguageEnglish); }
    public void SetLanguageJapanese() { SetLanguage(LanguageJapanese); }

    public void SetQualityVeryLow() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityVeryLow(); RefreshUI(); }
    public void SetQualityLow() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityLow(); RefreshUI(); }
    public void SetQualityMedium() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityMedium(); RefreshUI(); }
    public void SetQualityHigh() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityHigh(); RefreshUI(); }
}

}
