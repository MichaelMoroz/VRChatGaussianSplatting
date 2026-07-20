using UdonSharp;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
using UnityEditor.Events;
using UdonSharpEditor;
#endif

namespace GaussianSplatting
{

// Manual sync carries only the gallery selection (the [UdonSynced] fields below); all other UI state is local.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GaussianSplatRendererUI : UdonSharpBehaviour
{
    const int LanguageEnglish = 0;
    const int LanguageJapanese = 1;
    const float MinAlphaCutoff = 0.005f;
    const float MinPositiveLodSplatCap = 10000.0f;
    const int SliderShBand = 0;
    const int SliderAntiAliasing = 1;
    const int SliderLightVolumeIntensity = 2;
    const int SliderAlphaCutoff = 3;
    const int SliderAlphaCull = 4;
    const int SliderLODSplatCap = 5;
    const float DefaultAlphaCutoff = 0.04f;
    const float MaxAlphaCutoff = 0.3f;
    const float DefaultAlphaCull = 0.04f;
    const float MaxAlphaCull = 0.3f;
    const int DefaultLODSplatCap = 3000000;
    const float PanelWidth = 560.0f;
    const float SettingsColumnWidth = 520.0f;
    const float GalleryColumnSpacing = 18.0f;
    const float GalleryScrollbarWidth = 8.0f;
    const float GalleryPanelWidth = (SettingsColumnWidth * 2.0f) + GalleryColumnSpacing + 24.0f;
    const float BackgroundPadding = 24.0f;
    const float GalleryEntryMinHeight = 76.0f;
    const float GalleryEntryNameFontSize = 16.0f;
    const float GalleryEntryCountFontSize = 12.0f;
    const float GalleryEntryDescriptionFontSize = 12.0f;
    const float SliderChangeThreshold = 0.0001f;
    public const string DefaultSubtitleEnglish = "Developed by misha_m";
    public const string DefaultSubtitleJapanese = "開発: misha_m";
    public const float SubtitleFontSize = 10.0f;
    public const float SubtitlePreferredHeight = 15.0f;
    public const float CustomSubtitlePreferredHeight = 28.0f;

    public GaussianSplatRenderer gaussianSplatRenderer;
    [Header("Custom Subtitle")]
    [TextArea(1, 3)] public string customSubtitleEnglish;
    [TextArea(1, 3)] public string customSubtitleJapanese;
    [Header("UI References")]
    public TextMeshProUGUI subtitleText, customSubtitleText;
    public TextMeshProUGUI currentSplatText, sortingSectionText, cameraQuantizationLabelText, cameraQuantizationText;
    public TextMeshProUGUI materialSectionText, shBandLabelText, shBandText, vrcLightVolumesLabelText, antiAliasingLabelText, antiAliasingText;
    public TextMeshProUGUI lightVolumeIntensityLabelText, lightVolumeIntensityText, gaussianScaleLabelText, gaussianScaleText, alphaCutoffLabelText, alphaCutoffText;
    public TextMeshProUGUI alphaCullLabelText, alphaCullText;
    public TextMeshProUGUI lodCullLabelText, lodCullText, qualitySectionText;
    public TextMeshProUGUI languageSectionText;
    public Button vrcLightVolumesButton, englishLanguageButton, japaneseLanguageButton;
    public Button qualityVeryLowButton, qualityLowButton, qualityMediumButton, qualityHighButton, advancedSettingsButton;
    public Slider shBandSlider, antiAliasingSlider, lightVolumeIntensitySlider, alphaCutoffSlider;
    public Slider alphaCullSlider, lodCullSlider;
    [Header("Gallery")]
    [Tooltip("Splat objects in the gallery, added manually. When 1+ are listed, gallery mode is active and only the selected one renders. Objects NOT in this list are never touched.")]
    [SerializeField] public GaussianSplatObject[] galleryObjects = new GaussianSplatObject[0];
    [Tooltip("Inspector-only switch. If off, the gallery list is kept but gallery UI/selection enforcement are disabled and listed splats are not touched.")]
    [SerializeField] public bool galleryEnabled = true;
    [Tooltip("If on, only the instance master can change the gallery selection. Synced so the master can toggle it at runtime from the in-panel button.")]
    [SerializeField, UdonSynced] public bool galleryMasterLock = true;
    public GameObject gallerySection;
    public TextMeshProUGUI galleryHeaderText;
    public GameObject galleryListRoot;
    public GalleryEntry[] galleryEntries;
    public Button galleryMasterLockButton;          // ON/OFF toggle for galleryMasterLock; interactable for the master only
    public TextMeshProUGUI galleryMasterNameLabel;  // current master's name, shown above the toggle
    public TextMeshProUGUI galleryMasterLockTitle;  // "Master lock:" label left of the toggle
    public TextMeshProUGUI galleryMasterLockLabel;  // toggle button text ("ON"/"OFF")
    [SerializeField, UdonSynced] int _gallerySelectedIndex;
    string _galleryMasterName = "";                 // cached so RefreshUI doesn't scan players every frame

    [Header("Social Links")]
    public GameObject socialPanel;                  // QR + URL container, toggled by the icon buttons
    public UnityEngine.UI.Image socialQrImage;      // shows the selected link's QR code
    public TMPro.TMP_InputField socialUrlField;     // read-only, selectable/copyable URL
    public Sprite[] socialQrSprites;                // QR sprites, index-aligned with socialUrls
    public string[] socialUrls;                     // link URLs (VRChat can't open external URLs, so QR + copy is the route)
    int _socialSelected = -1;

    public void OpenSocialX() { ToggleSocial(0); }
    public void OpenSocialGithub() { ToggleSocial(1); }
    public void OpenSocialSponsors() { ToggleSocial(2); }
    public void OpenSocialBooth() { ToggleSocial(3); }
    public void OpenSocialPatreon() { ToggleSocial(4); }
    public void OpenSocialGumroad() { ToggleSocial(5); }

    public void CloseSocial()
    {
        _socialSelected = -1;
        if (socialPanel != null) socialPanel.SetActive(false);
    }

    void ToggleSocial(int index)
    {
        if (socialPanel == null) return;
        if (_socialSelected == index && socialPanel.activeSelf)
        {
            _socialSelected = -1;
            socialPanel.SetActive(false);
            return;
        }
        _socialSelected = index;
        if (socialQrImage != null && socialQrSprites != null && index >= 0 && index < socialQrSprites.Length)
        {
            socialQrImage.sprite = socialQrSprites[index];
        }
        if (socialUrlField != null && socialUrls != null && index >= 0 && index < socialUrls.Length)
        {
            socialUrlField.text = socialUrls[index];
        }
        socialPanel.SetActive(true);
    }

    [SerializeField] float gaussianScaleStep = 0.1f;
    [SerializeField] float cameraQuantizationStep = 0.05f;
    [SerializeField] int selectedLanguage = LanguageEnglish;
    [SerializeField] bool showAdvancedSettings;

    Color _selectedSplatColor = new Color(0.55f, 0.39f, 0.12f, 1.0f);
    Color _defaultSplatColor = new Color(0.2f, 0.2f, 0.24f, 1.0f);
    Color _toggleEnabledColor = new Color(0.18f, 0.4f, 0.24f, 1.0f);
    Color _toggleDisabledColor = new Color(0.3f, 0.16f, 0.14f, 1.0f);
    Color _galleryListColor = new Color(0.08f, 0.085f, 0.095f, 1.0f);
    Color _galleryRowColor = new Color(0.12f, 0.13f, 0.145f, 1.0f);
    Color _galleryRowHoverColor = new Color(0.16f, 0.17f, 0.19f, 1.0f);
    Color _gallerySelectedColor = new Color(0.10f, 0.32f, 0.42f, 1.0f);
    Color _gallerySelectedHoverColor = new Color(0.12f, 0.38f, 0.50f, 1.0f);
    Color _galleryDescriptionColor = new Color(0.78f, 0.81f, 0.86f, 1.0f);

    bool _sliderValuesInitialized;
    bool _layoutDefaultsInitialized;
    float _lastShBandSliderValue;
    float _lastAntiAliasingSliderValue;
    float _lastLightVolumeIntensitySliderValue;
    float _lastAlphaCutoffSliderValue;
    float _lastAlphaCullSliderValue;
    float _lastLODSplatCapSliderValue;
    RectTransform _canvasRect;
    RectTransform _panelRect;
    Transform _backgroundTransform;
    GameObject _splatColumnObject;
    Vector3 _defaultBackgroundScale;

    void Start()
    {
        if (SkipRuntimeRefresh()) return;
        UpdateGalleryMasterName();
        ApplyGalleryVisibility();
        RefreshUI();
    }

    // The instance master can migrate when players join/leave, so refresh the cached name on those events
    // rather than scanning the player list every frame.
    public override void OnPlayerJoined(VRCPlayerApi player) { UpdateGalleryMasterName(); }
    public override void OnPlayerLeft(VRCPlayerApi player) { UpdateGalleryMasterName(); }

    void UpdateGalleryMasterName()
    {
        int count = VRCPlayerApi.GetPlayerCount();
        if (count <= 0) { _galleryMasterName = ""; return; }
        VRCPlayerApi[] players = new VRCPlayerApi[count];
        VRCPlayerApi.GetPlayers(players);
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].isMaster) { _galleryMasterName = players[i].displayName; return; }
        }
        _galleryMasterName = "";
    }

    // Master-only toggle of the selection lock, wired to the in-panel button's onClick.
    public void ToggleGalleryMasterLock()
    {
        if (Networking.LocalPlayer == null || !Networking.LocalPlayer.isMaster) return;
        GalleryTakeOwnership();
        galleryMasterLock = !galleryMasterLock;
        RequestSerialization();
        RefreshUI();
    }

    void Update()
    {
        if (SkipRuntimeRefresh()) return;
        RefreshUI();
    }

    public override void OnDeserialization()
    {
        ApplyGalleryVisibility();
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
            bool serializedStateChanged = ui.SyncEditorSerializedState();
            if (serializedStateChanged)
            {
                EditorUtility.SetDirty(ui);
                ui.RefreshUI();
            }
        }
    }

    void OnValidate()
    {
        // OnValidate runs in a restricted Unity callback where hierarchy destruction is not allowed.
        // Queue the existing editor update refresh instead; it can safely rebuild/remove gallery UI objects.
        RequestEditorRefresh();
    }

    public void ApplyGalleryInspectorState()
    {
        ApplyGalleryVisibility();
        RefreshUI();
    }

    bool SyncEditorSerializedState()
    {
        if (EditorUtility.IsPersistent(this) || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
        {
            return false;
        }
        GaussianSplatRenderer previousRenderer = gaussianSplatRenderer;
        TextMeshProUGUI previousSubtitleText = subtitleText;
        TextMeshProUGUI previousCustomSubtitleText = customSubtitleText;
        FindRenderer();
        if (subtitleText == null)
        {
            subtitleText = FindSubtitleText();
        }
        if (customSubtitleText == null)
        {
            customSubtitleText = FindCustomSubtitleText();
            if (customSubtitleText == null)
            {
                customSubtitleText = CreateCustomSubtitleText();
            }
        }
        bool backgroundMaterialChanged = EnsureBackgroundMaterial();
        bool subtitleLayoutChanged = ApplySubtitleLayoutDefaults();
        bool galleryUiChanged = EnsureGalleryUI();
        return gaussianSplatRenderer != previousRenderer || subtitleText != previousSubtitleText || customSubtitleText != previousCustomSubtitleText || backgroundMaterialChanged || subtitleLayoutChanged || galleryUiChanged;
    }

    bool EnsureBackgroundMaterial()
    {
        Transform background = transform.Find("Background");
        MeshRenderer backgroundRenderer = background != null ? background.GetComponent<MeshRenderer>() : null;
        if (backgroundRenderer == null || backgroundRenderer.sharedMaterial != null)
        {
            return false;
        }
        Material backgroundMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/VRChatGaussianSplatting/Resources/Materials/GaussianSplatUIBackground.mat");
        if (backgroundMaterial == null)
        {
            return false;
        }
        backgroundRenderer.sharedMaterial = backgroundMaterial;
        EditorUtility.SetDirty(backgroundRenderer);
        return true;
    }

    bool ApplySubtitleLayoutDefaults()
    {
        TextMeshProUGUI subtitle = ResolveSubtitleText();
        if (subtitle == null)
        {
            return false;
        }

        bool changed = false;
        if (!Mathf.Approximately(subtitle.fontSize, SubtitleFontSize))
        {
            subtitle.fontSize = SubtitleFontSize;
            changed = true;
        }
        if (subtitle.alignment != TextAlignmentOptions.TopLeft)
        {
            subtitle.alignment = TextAlignmentOptions.TopLeft;
            changed = true;
        }

        LayoutElement layoutElement = subtitle.GetComponent<LayoutElement>();
        if (layoutElement != null)
        {
            bool layoutChanged = false;
            if (!Mathf.Approximately(layoutElement.minHeight, SubtitlePreferredHeight))
            {
                layoutElement.minHeight = SubtitlePreferredHeight;
                layoutChanged = true;
            }
            if (!Mathf.Approximately(layoutElement.preferredHeight, SubtitlePreferredHeight))
            {
                layoutElement.preferredHeight = SubtitlePreferredHeight;
                layoutChanged = true;
            }
            if (layoutChanged)
            {
                EditorUtility.SetDirty(layoutElement);
                changed = true;
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(subtitle);
        }
        TextMeshProUGUI customSubtitle = ResolveCustomSubtitleText();
        if (customSubtitle != null)
        {
            bool customChanged = false;
            if (!Mathf.Approximately(customSubtitle.fontSize, SubtitleFontSize))
            {
                customSubtitle.fontSize = SubtitleFontSize;
                customChanged = true;
            }
            if (customSubtitle.alignment != TextAlignmentOptions.TopLeft)
            {
                customSubtitle.alignment = TextAlignmentOptions.TopLeft;
                customChanged = true;
            }
            LayoutElement customLayoutElement = customSubtitle.GetComponent<LayoutElement>();
            if (customLayoutElement != null && !Mathf.Approximately(customLayoutElement.preferredHeight, CustomSubtitlePreferredHeight))
            {
                customLayoutElement.minHeight = customLayoutElement.preferredHeight = CustomSubtitlePreferredHeight;
                EditorUtility.SetDirty(customLayoutElement);
                customChanged = true;
            }
            if (customChanged)
            {
                EditorUtility.SetDirty(customSubtitle);
                changed = true;
            }
        }
        return changed;
    }

    TextMeshProUGUI CreateCustomSubtitleText()
    {
        Transform settingsColumn = transform.Find("Panel/Body Row/Settings Column");
        if (settingsColumn == null)
        {
            return null;
        }

        TextMeshProUGUI subtitle = FindSubtitleText();
        GameObject subtitleObject = new GameObject("Custom Subtitle", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        Undo.RegisterCreatedObjectUndo(subtitleObject, "Create Custom Subtitle");
        subtitleObject.transform.SetParent(settingsColumn, false);
        if (subtitle != null)
        {
            subtitleObject.transform.SetSiblingIndex(subtitle.transform.GetSiblingIndex() + 1);
        }

        TextMeshProUGUI text = subtitleObject.GetComponent<TextMeshProUGUI>();
        text.font = subtitle != null ? subtitle.font : text.font;
        text.color = Color.white;
        text.fontSize = SubtitleFontSize;
        text.fontStyle = FontStyles.Bold;
        text.fontWeight = FontWeight.Bold;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;

        LayoutElement layoutElement = subtitleObject.GetComponent<LayoutElement>();
        layoutElement.minHeight = layoutElement.preferredHeight = CustomSubtitlePreferredHeight;
        layoutElement.flexibleHeight = 0.0f;
        subtitleObject.SetActive(false);
        EditorUtility.SetDirty(text);
        EditorUtility.SetDirty(layoutElement);
        return text;
    }

    // Builds (or removes) the right-side gallery column based on whether the manual list has any objects.
    bool EnsureGalleryUI()
    {
        Transform bodyRow = transform.Find("Panel/Body Row");
        Transform panel = transform.Find("Panel");
        if (bodyRow == null || panel == null)
        {
            return false;
        }

        bool changed = false;
        Transform legacyTopSection = panel.Find("Gallery Section");
        if (legacyTopSection != null && legacyTopSection.parent != bodyRow)
        {
            DestroyImmediate(legacyTopSection.gameObject);
            changed = true;
        }
        Transform legacyToggleColumn = bodyRow.Find("Gallery Column");
        if (legacyToggleColumn != null && legacyToggleColumn.Find("Gallery Header Row/Gallery Toggle") != null)
        {
            DestroyImmediate(legacyToggleColumn.gameObject);
            changed = true;
        }
        Transform legacySectionInRow = bodyRow.Find("Gallery Section");
        if (legacySectionInRow != null)
        {
            DestroyImmediate(legacySectionInRow.gameObject);
            changed = true;
        }

        Transform existing = bodyRow.Find("Gallery Column");
        if (GalleryActive())
        {
            if (existing != null)
            {
                bool missingScrollbar = existing.Find("Gallery List/Scrollbar Vertical") == null;
                bool missingMasterLock = existing.Find("Gallery Header Row/Master Lock") == null;
                bool missingEntryCount = GalleryEntriesMissingCountText();
                if (galleryEntries == null || galleryEntries.Length < galleryObjects.Length || missingScrollbar || missingMasterLock || missingEntryCount)
                {
                    DestroyImmediate(existing.gameObject);
                    BuildGalleryUI(bodyRow);
                    return true;
                }
                TextMeshProUGUI existingHeader = FindGalleryHeaderText(existing);
                bool refChanged = gallerySection != existing.gameObject || galleryHeaderText != existingHeader;
                gallerySection = existing.gameObject;
                galleryHeaderText = existingHeader;
                return changed || refChanged || EnsureGalleryColumnLayout(existing.gameObject);
            }
            BuildGalleryUI(bodyRow);
            return true;
        }
        if (existing != null)
        {
            DestroyImmediate(existing.gameObject);
            gallerySection = null;
            galleryHeaderText = null;
            galleryListRoot = null;
            galleryEntries = new GalleryEntry[0];
            return true;
        }
        return changed;
    }

    bool EnsureGalleryColumnLayout(GameObject column)
    {
        bool changed = false;
        Image image = column.GetComponent<Image>();
        if (image != null)
        {
            DestroyImmediate(image);
            changed = true;
        }
        VerticalLayoutGroup vlg = column.GetComponent<VerticalLayoutGroup>();
        if (vlg == null)
        {
            vlg = column.AddComponent<VerticalLayoutGroup>();
            changed = true;
        }
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 8.0f;
        vlg.padding = new RectOffset(12, 12, 12, 12);

        LayoutElement columnLayout = column.GetComponent<LayoutElement>();
        if (columnLayout == null)
        {
            columnLayout = column.AddComponent<LayoutElement>();
            changed = true;
        }
        if (!Mathf.Approximately(columnLayout.preferredWidth, SettingsColumnWidth) || !Mathf.Approximately(columnLayout.minWidth, SettingsColumnWidth))
        {
            columnLayout.preferredWidth = SettingsColumnWidth;
            columnLayout.minWidth = SettingsColumnWidth;
            changed = true;
        }
        if (!Mathf.Approximately(columnLayout.preferredHeight, 900.0f) || !Mathf.Approximately(columnLayout.minHeight, 900.0f))
        {
            columnLayout.preferredHeight = 900.0f;
            columnLayout.minHeight = 900.0f;
            changed = true;
        }
        return changed;
    }

    void BuildGalleryUI(Transform bodyRow)
    {
        GameObject section = new GameObject("Gallery Column", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        Undo.RegisterCreatedObjectUndo(section, "Build Gallery UI");
        section.transform.SetParent(bodyRow, false);
        section.transform.SetAsLastSibling();
        EnsureGalleryColumnLayout(section);
        gallerySection = section;

        CreateGalleryHeader(section.transform);

        Transform content;
        galleryListRoot = CreateScrollList(section.transform, out content);

        int entryCapacity = Mathf.Max(64, galleryObjects != null ? galleryObjects.Length : 0);
        galleryEntries = new GalleryEntry[entryCapacity];
        for (int i = 0; i < galleryEntries.Length; i++)
        {
            galleryEntries[i] = CreateGalleryEntry(content, i);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(this);
        EditorUtility.SetDirty(this);
    }

    TextMeshProUGUI CreateGalleryText(Transform parent, string objectName, string value, float fontSize, FontStyles style, float height)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        TextMeshProUGUI reference = FindSubtitleText();
        text.font = reference != null ? reference.font : text.font;
        text.color = Color.white; text.fontSize = fontSize; text.fontStyle = style;
        text.alignment = TextAlignmentOptions.TopLeft; text.enableWordWrapping = true; text.overflowMode = TextOverflowModes.Truncate;
        text.raycastTarget = false; text.text = value;
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (height > 0.0f) { le.minHeight = le.preferredHeight = height; }
        return text;
    }

    // Header row: "Gallery (global)" on the left; on the right the master's name ABOVE a "Master lock:" label
    // and an ON/OFF toggle button (green when ON). Master-only click is enforced in RefreshGalleryMasterLock.
    void CreateGalleryHeader(Transform parent)
    {
        GameObject headerRow = new GameObject("Gallery Header Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        headerRow.transform.SetParent(parent, false);
        HorizontalLayoutGroup hlg = headerRow.GetComponent<HorizontalLayoutGroup>();
        hlg.childControlWidth = true; hlg.childControlHeight = true; hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false; hlg.childAlignment = TextAnchor.MiddleLeft; hlg.spacing = 10.0f;
        headerRow.GetComponent<LayoutElement>().minHeight = 56.0f;

        galleryHeaderText = CreateGalleryText(headerRow.transform, "Gallery Header", "Gallery (global)", 18.0f, FontStyles.Bold, 0.0f);
        galleryHeaderText.alignment = TextAlignmentOptions.MidlineLeft;
        galleryHeaderText.GetComponent<LayoutElement>().flexibleWidth = 1.0f; // takes the remaining width; pushes the lock block right

        GameObject right = new GameObject("Master Lock", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement), typeof(ContentSizeFitter));
        right.transform.SetParent(headerRow.transform, false);
        VerticalLayoutGroup rvlg = right.GetComponent<VerticalLayoutGroup>();
        rvlg.childControlWidth = true; rvlg.childControlHeight = true; rvlg.childForceExpandWidth = true; rvlg.childForceExpandHeight = false; rvlg.childAlignment = TextAnchor.UpperRight; rvlg.spacing = 2.0f;
        right.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        LayoutElement rightLe = right.GetComponent<LayoutElement>(); rightLe.minWidth = rightLe.preferredWidth = 210.0f; rightLe.flexibleWidth = 0.0f;

        // master's name, above the button
        galleryMasterNameLabel = CreateGalleryText(right.transform, "Master Name", "", 12.0f, FontStyles.Normal, 0.0f);
        galleryMasterNameLabel.color = _galleryDescriptionColor;
        galleryMasterNameLabel.alignment = TextAlignmentOptions.MidlineRight;

        GameObject lockRow = new GameObject("Lock Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        lockRow.transform.SetParent(right.transform, false);
        HorizontalLayoutGroup lhlg = lockRow.GetComponent<HorizontalLayoutGroup>();
        lhlg.childControlWidth = true; lhlg.childControlHeight = true; lhlg.childForceExpandWidth = false; lhlg.childForceExpandHeight = false; lhlg.childAlignment = TextAnchor.MiddleRight; lhlg.spacing = 8.0f;
        lockRow.GetComponent<LayoutElement>().minHeight = 34.0f;

        galleryMasterLockTitle = CreateGalleryText(lockRow.transform, "Title", "Master lock:", 14.0f, FontStyles.Normal, 0.0f);
        galleryMasterLockTitle.alignment = TextAlignmentOptions.MidlineRight;
        galleryMasterLockTitle.GetComponent<LayoutElement>().flexibleWidth = 1.0f;

        GameObject btnGo = new GameObject("Toggle", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        btnGo.transform.SetParent(lockRow.transform, false);
        Image bg = btnGo.GetComponent<Image>(); bg.color = GalleryMasterLockColor();
        Button button = btnGo.GetComponent<Button>(); button.targetGraphic = bg; button.transition = Selectable.Transition.ColorTint;
        LayoutElement btnLe = btnGo.GetComponent<LayoutElement>(); btnLe.minWidth = btnLe.preferredWidth = 64.0f; btnLe.minHeight = 32.0f; btnLe.flexibleWidth = 0.0f; btnLe.flexibleHeight = 0.0f;

        galleryMasterLockLabel = CreateGalleryText(btnGo.transform, "State", galleryMasterLock ? "ON" : "OFF", 14.0f, FontStyles.Bold, 0.0f);
        galleryMasterLockLabel.alignment = TextAlignmentOptions.Center;
        RectTransform lblRect = (RectTransform)galleryMasterLockLabel.transform;
        lblRect.anchorMin = Vector2.zero; lblRect.anchorMax = Vector2.one; lblRect.offsetMin = Vector2.zero; lblRect.offsetMax = Vector2.zero;

        galleryMasterLockButton = button;
        WireUdonClick(button, UdonSharpEditorUtility.GetBackingUdonBehaviour(this), "ToggleGalleryMasterLock");
    }

    GameObject CreateScrollList(Transform parent, out Transform content)
    {
        GameObject scrollGo = new GameObject("Gallery List", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
        scrollGo.transform.SetParent(parent, false);
        scrollGo.GetComponent<Image>().color = _galleryListColor;
        LayoutElement scrollLayout = scrollGo.GetComponent<LayoutElement>();
        scrollLayout.preferredHeight = 826.0f; scrollLayout.minHeight = 160.0f; scrollLayout.flexibleHeight = 1.0f;

        GameObject viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
        viewportGo.transform.SetParent(scrollGo.transform, false);
        RectTransform viewportRect = (RectTransform)viewportGo.transform;
        viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one; viewportRect.pivot = new Vector2(0.0f, 1.0f);
        // Inset the right edge so the visual scrollbar strip doesn't overlap the entries.
        viewportRect.offsetMin = Vector2.zero; viewportRect.offsetMax = new Vector2(-GalleryScrollbarWidth, 0.0f);
        viewportGo.GetComponent<Image>().color = new Color(0, 0, 0, 0);

        GameObject contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGo.transform.SetParent(viewportGo.transform, false);
        RectTransform contentRect = (RectTransform)contentGo.transform;
        contentRect.anchorMin = new Vector2(0.0f, 1.0f); contentRect.anchorMax = new Vector2(1.0f, 1.0f); contentRect.pivot = new Vector2(0.5f, 1.0f); contentRect.sizeDelta = new Vector2(0.0f, 0.0f);
        VerticalLayoutGroup contentVlg = contentGo.GetComponent<VerticalLayoutGroup>();
        contentVlg.childControlWidth = true; contentVlg.childControlHeight = true; contentVlg.childForceExpandWidth = true; contentVlg.childForceExpandHeight = false; contentVlg.spacing = 6.0f; contentVlg.padding = new RectOffset(8, 8, 8, 8);
        ContentSizeFitter contentCsf = contentGo.GetComponent<ContentSizeFitter>();
        contentCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = scrollGo.GetComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true; scroll.scrollSensitivity = 32.0f;
        scroll.viewport = viewportRect; scroll.content = contentRect;

        // Visual-only scrollbar: the ScrollRect drives its handle size (how much fits) and position (where you
        // are) even though it's non-interactable, so it indicates scrollability without being draggable.
        Scrollbar scrollbar = CreateVisualScrollbar(scrollGo.transform, GalleryScrollbarWidth);
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        scroll.verticalScrollbarSpacing = 0.0f;

        content = contentGo.transform;
        return scrollGo;
    }

    // A non-interactable vertical scrollbar wired to the gallery ScrollRect: a track + handle whose size and
    // position the ScrollRect updates automatically (handle filling the track == the list isn't scrollable).
    Scrollbar CreateVisualScrollbar(Transform parent, float width)
    {
        Color trackColor = new Color(0.05f, 0.055f, 0.065f, 1.0f);
        Color handleColor = new Color(0.45f, 0.48f, 0.54f, 1.0f);

        GameObject barGo = new GameObject("Scrollbar Vertical", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        barGo.transform.SetParent(parent, false);
        RectTransform barRect = (RectTransform)barGo.transform;
        barRect.anchorMin = new Vector2(1.0f, 0.0f); barRect.anchorMax = new Vector2(1.0f, 1.0f); barRect.pivot = new Vector2(1.0f, 0.5f);
        barRect.sizeDelta = new Vector2(width, 0.0f); barRect.anchoredPosition = Vector2.zero;
        barGo.GetComponent<Image>().color = trackColor;

        GameObject areaGo = new GameObject("Sliding Area", typeof(RectTransform));
        areaGo.transform.SetParent(barGo.transform, false);
        RectTransform areaRect = (RectTransform)areaGo.transform;
        areaRect.anchorMin = Vector2.zero; areaRect.anchorMax = Vector2.one; areaRect.sizeDelta = Vector2.zero; areaRect.anchoredPosition = Vector2.zero;

        GameObject handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGo.transform.SetParent(areaGo.transform, false);
        RectTransform handleRect = (RectTransform)handleGo.transform;
        handleRect.sizeDelta = Vector2.zero;
        handleGo.GetComponent<Image>().color = handleColor;

        Scrollbar bar = barGo.GetComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.handleRect = handleRect;
        bar.targetGraphic = handleGo.GetComponent<Image>();
        bar.interactable = false; // visual only; the ScrollRect still drives size/value
        return bar;
    }

    GalleryEntry CreateGalleryEntry(Transform content, int index)
    {
        GameObject entryGo = new GameObject("Gallery Entry " + index, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        entryGo.transform.SetParent(content, false);
        Image background = entryGo.GetComponent<Image>();
        background.color = _galleryRowColor;
        Button button = entryGo.GetComponent<Button>();
        button.targetGraphic = background;
        button.transition = Selectable.Transition.ColorTint;
        ColorBlock buttonColors = button.colors;
        buttonColors.normalColor = _galleryRowColor;
        buttonColors.highlightedColor = _galleryRowHoverColor;
        buttonColors.pressedColor = _gallerySelectedHoverColor;
        buttonColors.selectedColor = _galleryRowHoverColor;
        buttonColors.disabledColor = _galleryRowColor;
        button.colors = buttonColors;

        VerticalLayoutGroup rowVlg = entryGo.GetComponent<VerticalLayoutGroup>();
        rowVlg.childControlWidth = true; rowVlg.childControlHeight = true; rowVlg.childForceExpandWidth = true; rowVlg.childForceExpandHeight = false; rowVlg.spacing = 3.0f; rowVlg.padding = new RectOffset(14, 14, 11, 11);
        ContentSizeFitter rowCsf = entryGo.GetComponent<ContentSizeFitter>();
        rowCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        LayoutElement rowLayout = entryGo.GetComponent<LayoutElement>();
        rowLayout.minHeight = GalleryEntryMinHeight;
        rowLayout.flexibleHeight = 0.0f;

        GameObject titleRow = new GameObject("Title Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        titleRow.transform.SetParent(entryGo.transform, false);
        HorizontalLayoutGroup titleLayout = titleRow.GetComponent<HorizontalLayoutGroup>();
        titleLayout.childControlWidth = true; titleLayout.childControlHeight = true; titleLayout.childForceExpandWidth = false; titleLayout.childForceExpandHeight = false; titleLayout.spacing = 8.0f;
        LayoutElement titleRowLayout = titleRow.GetComponent<LayoutElement>();
        titleRowLayout.minHeight = titleRowLayout.preferredHeight = 20.0f;

        TextMeshProUGUI nameText = CreateGalleryText(titleRow.transform, "Name", "", GalleryEntryNameFontSize, FontStyles.Bold, 0.0f);
        LayoutElement nameLayout = nameText.GetComponent<LayoutElement>();
        if (nameLayout != null) nameLayout.flexibleWidth = 1.0f;
        TextMeshProUGUI countText = CreateGalleryText(titleRow.transform, "Splat Count", "", GalleryEntryCountFontSize, FontStyles.Normal, 0.0f);
        countText.alignment = TextAlignmentOptions.MidlineRight;
        countText.color = _galleryDescriptionColor;
        LayoutElement countLayout = countText.GetComponent<LayoutElement>();
        if (countLayout != null)
        {
            countLayout.minWidth = 92.0f;
            countLayout.preferredWidth = 112.0f;
            countLayout.flexibleWidth = 0.0f;
        }
        TextMeshProUGUI descriptionText = CreateGalleryText(entryGo.transform, "Description", "", GalleryEntryDescriptionFontSize, FontStyles.Normal, 0.0f);
        descriptionText.color = _galleryDescriptionColor;
        // Multiline: wrap and grow the row (the row has a ContentSizeFitter) instead of truncating.
        descriptionText.overflowMode = TextOverflowModes.Overflow;

        GalleryEntry entry = UdonSharpUndo.AddComponent<GalleryEntry>(entryGo);
        entry.ui = this; entry.index = index; entry.button = button; entry.background = background; entry.nameText = nameText; entry.countText = countText; entry.descriptionText = descriptionText;
        UdonSharpEditorUtility.CopyProxyToUdon(entry);
        WireUdonClick(button, UdonSharpEditorUtility.GetBackingUdonBehaviour(entry), "Select");

        entryGo.SetActive(false);
        return entry;
    }

    void ApplyGalleryEntryEditorLayout(GalleryEntry entry)
    {
        if (entry == null)
        {
            return;
        }
        VerticalLayoutGroup rowVlg = entry.GetComponent<VerticalLayoutGroup>();
        if (rowVlg != null)
        {
            rowVlg.childControlWidth = true;
            rowVlg.childControlHeight = true;
            rowVlg.childForceExpandWidth = true;
            rowVlg.childForceExpandHeight = false;
            rowVlg.spacing = 3.0f;
            rowVlg.padding = new RectOffset(14, 14, 11, 11);
        }
        LayoutElement rowLayout = entry.GetComponent<LayoutElement>();
        if (rowLayout != null)
        {
            rowLayout.minHeight = GalleryEntryMinHeight;
            rowLayout.flexibleHeight = 0.0f;
        }
        if (entry.nameText != null)
        {
            entry.nameText.fontSize = GalleryEntryNameFontSize;
        }
        if (entry.countText != null)
        {
            entry.countText.fontSize = GalleryEntryCountFontSize;
            entry.countText.color = _galleryDescriptionColor;
            entry.countText.alignment = TextAlignmentOptions.MidlineRight;
        }
        if (entry.descriptionText != null)
        {
            entry.descriptionText.fontSize = GalleryEntryDescriptionFontSize;
            entry.descriptionText.color = _galleryDescriptionColor;
            entry.descriptionText.enableWordWrapping = true;
            entry.descriptionText.overflowMode = TextOverflowModes.Overflow;
        }
    }

    bool GalleryEntriesMissingCountText()
    {
        if (galleryEntries == null)
        {
            return true;
        }
        for (int i = 0; i < galleryEntries.Length; i++)
        {
            GalleryEntry entry = galleryEntries[i];
            if (entry != null && entry.countText == null)
            {
                return true;
            }
        }
        return false;
    }

    static void WireUdonClick(Button button, VRC.Udon.UdonBehaviour backing, string eventName)
    {
        if (button == null || backing == null)
        {
            return;
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, backing.SendCustomEvent, eventName);
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
        string FormatFloat(float value) { return (Mathf.Round(value * 10000.0f) * 0.0001f).ToString("0.####"); }
    string ToggleLabel(bool enabled) { return Localize(enabled ? "On" : "Off", enabled ? "オン" : "オフ"); }
    string AdvancedSettingsLabel() { return Localize(showAdvancedSettings ? "Hide Advanced Settings" : "Show Advanced Settings", showAdvancedSettings ? "詳細設定を隠す" : "詳細設定を表示"); }
    string RenderedSplatCountLabel(int count) { return Localize("Rendered Splats: ", "描画スプラット数: ") + count; }
    bool SliderCanWriteBack(int sliderKind, bool allowWriteBack) { return allowWriteBack; }
    bool ShouldShowLODControls() { return gaussianSplatRenderer != null && gaussianSplatRenderer.HasActiveLODObjects(); }

    void SetText(TextMeshProUGUI text, string value) { if (text != null && text.text != value) text.text = value; }
    void SetLocalizedText(TextMeshProUGUI text, string english, string japanese) { SetText(text, Localize(english, japanese)); }
    void SetActive(Component component, bool active) { if (component != null && component.gameObject.activeSelf != active) component.gameObject.SetActive(active); }
    void SetParentActive(Component component, bool active)
    {
        Transform parent = component != null ? component.transform.parent : null;
        if (parent != null && parent.gameObject.activeSelf != active) parent.gameObject.SetActive(active);
    }
    void SetInteractable(Selectable selectable, bool interactable) { if (selectable != null && selectable.interactable != interactable) selectable.interactable = interactable; }
    void SetSliderWithoutNotify(Slider slider, float value) { if (slider != null && !Mathf.Approximately(slider.value, value)) slider.SetValueWithoutNotify(value); }

    float ToLogSliderValue(float value, float minValue, float maxValue)
    {
        float clampedValue = Mathf.Clamp(value, minValue, maxValue);
        return Mathf.InverseLerp(Mathf.Log(minValue), Mathf.Log(maxValue), Mathf.Log(clampedValue));
    }

    float FromLogSliderValue(float sliderValue, float minValue, float maxValue)
    {
        return Mathf.Exp(Mathf.Lerp(Mathf.Log(minValue), Mathf.Log(maxValue), Mathf.Clamp01(sliderValue)));
    }

    float ToZeroLogSliderValue(float value, float minPositiveValue, float maxValue)
    {
        if (value <= 0.0f)
        {
            return 0.0f;
        }
        return ToLogSliderValue(Mathf.Max(value, minPositiveValue), minPositiveValue, maxValue);
    }

    float FromZeroLogSliderValue(float sliderValue, float minPositiveValue, float maxValue)
    {
        if (sliderValue <= 0.0f)
        {
            return 0.0f;
        }
        return FromLogSliderValue(sliderValue, minPositiveValue, maxValue);
    }

    float GetLodSplatCapSliderMax()
    {
        return Mathf.Max(0.0f, gaussianSplatRenderer != null ? gaussianSplatRenderer.GetCombinedLodSplatBudgetSliderMax() : DefaultLODSplatCap);
    }

    float GetLodSplatCapSliderMin()
    {
        return Mathf.Max(0.0f, gaussianSplatRenderer != null ? gaussianSplatRenderer.GetCombinedLodSplatBudgetSliderMin() : 0.0f);
    }

    float ToLodSplatCapSliderValue(float value)
    {
        float minValue = GetLodSplatCapSliderMin();
        float maxValue = GetLodSplatCapSliderMax();
        if (maxValue <= minValue)
        {
            return 0.0f;
        }
        if (minValue <= 0.0f)
        {
            if (maxValue <= MinPositiveLodSplatCap)
            {
                return Mathf.InverseLerp(0.0f, maxValue, Mathf.Clamp(value, 0.0f, maxValue));
            }
            return ToZeroLogSliderValue(Mathf.Clamp(value, minValue, maxValue), MinPositiveLodSplatCap, maxValue);
        }
        return ToLogSliderValue(value, Mathf.Max(1.0f, minValue), maxValue);
    }

    float FromLodSplatCapSliderValue(float sliderValue)
    {
        float minValue = GetLodSplatCapSliderMin();
        float maxValue = GetLodSplatCapSliderMax();
        if (maxValue <= minValue)
        {
            return minValue;
        }
        if (minValue <= 0.0f)
        {
            if (maxValue <= MinPositiveLodSplatCap)
            {
                return Mathf.Lerp(0.0f, maxValue, Mathf.Clamp01(sliderValue));
            }
            return FromZeroLogSliderValue(sliderValue, MinPositiveLodSplatCap, maxValue);
        }
        return FromLogSliderValue(sliderValue, Mathf.Max(1.0f, minValue), maxValue);
    }

    float GetSliderDisplayValue(int sliderKind, float actualValue)
    {
        switch (sliderKind)
        {
            case SliderAlphaCutoff: return ToLogSliderValue(actualValue, MinAlphaCutoff, MaxAlphaCutoff);
            case SliderLODSplatCap: return ToLodSplatCapSliderValue(actualValue);
            default: return actualValue;
        }
    }

    float GetActualSliderValue(int sliderKind, float sliderValue)
    {
        switch (sliderKind)
        {
            case SliderAlphaCutoff: return FromLogSliderValue(sliderValue, MinAlphaCutoff, MaxAlphaCutoff);
            case SliderLODSplatCap: return FromLodSplatCapSliderValue(sliderValue);
            default: return sliderValue;
        }
    }

    TextMeshProUGUI FindSubtitleText()
    {
        Transform subtitleTransform = transform.Find("Panel/Body Row/Settings Column/Subtitle");
        return subtitleTransform != null ? subtitleTransform.GetComponent<TextMeshProUGUI>() : null;
    }

    TextMeshProUGUI FindCustomSubtitleText()
    {
        Transform subtitleTransform = transform.Find("Panel/Body Row/Settings Column/Custom Subtitle");
        return subtitleTransform != null ? subtitleTransform.GetComponent<TextMeshProUGUI>() : null;
    }

    TextMeshProUGUI ResolveSubtitleText() { return subtitleText != null ? subtitleText : FindSubtitleText(); }
    TextMeshProUGUI ResolveCustomSubtitleText() { return customSubtitleText != null ? customSubtitleText : FindCustomSubtitleText(); }
    TextMeshProUGUI FindGalleryHeaderText(Transform galleryColumn)
    {
        Transform headerTransform = galleryColumn != null ? galleryColumn.Find("Gallery Header Row/Gallery Header") : null;
        return headerTransform != null ? headerTransform.GetComponent<TextMeshProUGUI>() : null;
    }
    TextMeshProUGUI ResolveGalleryHeaderText()
    {
        if (galleryHeaderText != null)
        {
            return galleryHeaderText;
        }
        Transform galleryColumn = transform.Find("Panel/Body Row/Gallery Column");
        return FindGalleryHeaderText(galleryColumn);
    }

    void RefreshLocalizedLabels()
    {
        SetText(ResolveSubtitleText(), Localize(DefaultSubtitleEnglish, DefaultSubtitleJapanese));
        TextMeshProUGUI customSubtitle = ResolveCustomSubtitleText();
        string customSubtitleValue = Localize(customSubtitleEnglish, customSubtitleJapanese);
        SetText(customSubtitle, customSubtitleValue);
        SetActive(customSubtitle, !string.IsNullOrEmpty(customSubtitleValue));
        SetLocalizedText(sortingSectionText, "Sorting Settings", "ソート設定");
        SetLocalizedText(cameraQuantizationLabelText, "Camera move amount to trigger resort", "再ソートするカメラ移動量");
        SetLocalizedText(materialSectionText, "Material Settings", "マテリアル設定");
        SetLocalizedText(shBandLabelText, "SH Band", "SH バンド");
        SetLocalizedText(vrcLightVolumesLabelText, "VRC Light Volumes", "VRC Light Volumes");
        SetLocalizedText(lightVolumeIntensityLabelText, "Light Volume Intensity", "ライトボリューム強度");
        SetLocalizedText(antiAliasingLabelText, "Antialiasing", "アンチエイリアス");
        SetLocalizedText(gaussianScaleLabelText, "Gaussian Scale", "ガウススケール");
        SetLocalizedText(alphaCutoffLabelText, "Alpha Cutoff\n(lower = better quality)", "アルファカットオフ\n(低いほど高品質)");
        SetLocalizedText(alphaCullLabelText, "Alpha Cull\n(higher = fewer splats)", "アルファカリング\n(高いほどスプラット減少)");
        SetLocalizedText(lodCullLabelText, "LOD Splat Cap", "LOD スプラット上限");
        SetLocalizedText(qualitySectionText, "Quality", "品質");
        SetLocalizedText(languageSectionText, "Language", "言語");
        SetLocalizedText(ResolveGalleryHeaderText(), "Gallery (global)", "ギャラリー (全体)");
        RefreshLanguageButtons();
    }

    void RefreshLanguageButtons()
    {
        if (englishLanguageButton != null) { SetInteractable(englishLanguageButton, true); ApplyButtonVisual(englishLanguageButton, "English", selectedLanguage == LanguageEnglish ? _selectedSplatColor : _defaultSplatColor); }
        if (japaneseLanguageButton != null) { SetInteractable(japaneseLanguageButton, true); ApplyButtonVisual(japaneseLanguageButton, "日本語", selectedLanguage == LanguageJapanese ? _selectedSplatColor : _defaultSplatColor); }
    }

    bool AdvancedSettingsVisible() { return showAdvancedSettings || advancedSettingsButton == null; }

    void RefreshAdvancedSettingsButton()
    {
        if (advancedSettingsButton != null)
        {
            SetInteractable(advancedSettingsButton, true);
            ApplyButtonVisual(advancedSettingsButton, AdvancedSettingsLabel(), showAdvancedSettings ? _selectedSplatColor : _defaultSplatColor);
        }
    }

    void SetAdvancedMaterialControlsVisible(bool visible)
    {
        SetActive(materialSectionText, visible);
        SetParentActive(shBandLabelText, visible);
        SetParentActive(vrcLightVolumesLabelText, visible);
        SetParentActive(lightVolumeIntensityLabelText, visible);
        SetParentActive(antiAliasingLabelText, visible);
        SetParentActive(gaussianScaleLabelText, visible);
        SetParentActive(alphaCutoffLabelText, visible);
        SetParentActive(alphaCullLabelText, visible);
        SetParentActive(lodCullLabelText, visible && ShouldShowLODControls());
    }

    void RefreshSortingVisibility()
    {
        SetActive(sortingSectionText, false);
        SetParentActive(cameraQuantizationLabelText, false);
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
        _defaultBackgroundScale = _backgroundTransform != null ? _backgroundTransform.localScale : Vector3.one;
        _layoutDefaultsInitialized = true;
    }

    void ApplyPanelLayout()
    {
        EnsureLayoutCache();
        if (_splatColumnObject != null && _splatColumnObject.activeSelf)
        {
            _splatColumnObject.SetActive(false);
        }
        float targetPanelWidth = GalleryActive() ? GalleryPanelWidth : PanelWidth;
        if (_canvasRect != null && !Mathf.Approximately(_canvasRect.sizeDelta.x, targetPanelWidth))
        {
            _canvasRect.sizeDelta = new Vector2(targetPanelWidth, _canvasRect.sizeDelta.y);
        }
        if (_panelRect != null && !Mathf.Approximately(_panelRect.sizeDelta.x, targetPanelWidth))
        {
            _panelRect.sizeDelta = new Vector2(targetPanelWidth, _panelRect.sizeDelta.y);
        }
        // Size + center the opaque background mesh to the panel's live world bounds, so it covers the whole
        // panel (the gallery section grows it taller) and nothing floats over the splats.
        if (_backgroundTransform != null && _panelRect != null)
        {
            Vector3[] corners = new Vector3[4];
            _panelRect.GetWorldCorners(corners);
            Transform bgParent = _backgroundTransform.parent;
            Vector3 worldCenter = (corners[0] + corners[2]) * 0.5f;
            Vector3 localCenter = bgParent != null ? bgParent.InverseTransformPoint(worldCenter) : worldCenter;
            localCenter.z = _backgroundTransform.localPosition.z;
            if ((_backgroundTransform.localPosition - localCenter).sqrMagnitude > 0.0001f)
            {
                _backgroundTransform.localPosition = localCenter;
            }
            Vector3 targetScale = new Vector3(_panelRect.rect.width + BackgroundPadding, _panelRect.rect.height + BackgroundPadding, _defaultBackgroundScale.z);
            if (_backgroundTransform.localScale != targetScale)
            {
                _backgroundTransform.localScale = targetScale;
            }
        }
    }

    void RefreshMaterialControls()
    {
        RefreshAdvancedSettingsButton();
        bool advancedVisible = AdvancedSettingsVisible();
        SetAdvancedMaterialControlsVisible(advancedVisible);
        RefreshQualityButtons();
        if (!advancedVisible)
        {
            return;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        bool allowWriteBack = EditorApplication.isPlaying;
#else
        bool allowWriteBack = true;
#endif
        if (vrcLightVolumesButton != null)
        {
            bool enabled = gaussianSplatRenderer.GetUseVrcLightVolumes();
            SetInteractable(vrcLightVolumesButton, true);
            ApplyButtonVisual(vrcLightVolumesButton, ToggleLabel(enabled), enabled ? _toggleEnabledColor : _toggleDisabledColor);
        }
        SyncSlider(shBandSlider, shBandText, SliderShBand, SliderCanWriteBack(SliderShBand, allowWriteBack));
        SyncSlider(antiAliasingSlider, antiAliasingText, SliderAntiAliasing, allowWriteBack);
        SyncSlider(lightVolumeIntensitySlider, lightVolumeIntensityText, SliderLightVolumeIntensity, allowWriteBack);
        SyncSlider(alphaCutoffSlider, alphaCutoffText, SliderAlphaCutoff, allowWriteBack);
        SyncSlider(alphaCullSlider, alphaCullText, SliderAlphaCull, allowWriteBack);
        bool showLODControls = ShouldShowLODControls();
        SetParentActive(lodCullLabelText, showLODControls);
        if (showLODControls)
        {
            SyncSlider(lodCullSlider, lodCullText, SliderLODSplatCap, allowWriteBack);
        }
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
            case SliderLODSplatCap: return gaussianSplatRenderer.GetEffectiveCombinedLodSplatBudget();
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
            case SliderLODSplatCap: return _lastLODSplatCapSliderValue;
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
            case SliderLODSplatCap:
                _lastLODSplatCapSliderValue = value;
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
            case SliderLODSplatCap:
                gaussianSplatRenderer.SetEffectiveCombinedLodSplatBudget(Mathf.RoundToInt(value));
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
            if (!Mathf.Approximately(slider.minValue, 0.0f))
            {
                slider.minValue = 0.0f;
            }
            float maxBand = gaussianSplatRenderer.GetSelectedSplatMaxSHBand();
            if (!Mathf.Approximately(slider.maxValue, maxBand))
            {
                slider.maxValue = maxBand;
            }
        }
        else if (sliderKind == SliderAlphaCutoff)
        {
            if (!Mathf.Approximately(slider.minValue, 0.0f))
            {
                slider.minValue = 0.0f;
            }
            if (!Mathf.Approximately(slider.maxValue, 1.0f))
            {
                slider.maxValue = 1.0f;
            }
        }
        else if (sliderKind == SliderAlphaCull)
        {
            if (!Mathf.Approximately(slider.minValue, MinAlphaCutoff))
            {
                slider.minValue = MinAlphaCutoff;
            }
            if (!Mathf.Approximately(slider.maxValue, MaxAlphaCull))
            {
                slider.maxValue = MaxAlphaCull;
            }
        }
        else if (sliderKind == SliderLODSplatCap)
        {
            if (!Mathf.Approximately(slider.minValue, 0.0f))
            {
                slider.minValue = 0.0f;
            }
            if (!Mathf.Approximately(slider.maxValue, 1.0f))
            {
                slider.maxValue = 1.0f;
            }
        }
        float currentValue = GetSliderValue(sliderKind);
        float lastValue = GetLastSliderValue(sliderKind);
        float displayedCurrentValue = GetSliderDisplayValue(sliderKind, currentValue);
        float displayedLastValue = GetSliderDisplayValue(sliderKind, lastValue);
        bool sliderNeedsRefresh = !_sliderValuesInitialized || SliderValueChanged(currentValue, lastValue) || (!allowWriteBack && SliderValueChanged(slider.value, displayedCurrentValue));
        if (sliderNeedsRefresh)
        {
            SetSliderWithoutNotify(slider, displayedCurrentValue);
            SetLastSliderValue(sliderKind, currentValue);
        }
        else if (allowWriteBack && SliderValueChanged(slider.value, displayedLastValue))
        {
            SetSliderValue(sliderKind, GetActualSliderValue(sliderKind, slider.value));
            currentValue = GetSliderValue(sliderKind);
            SetSliderWithoutNotify(slider, GetSliderDisplayValue(sliderKind, currentValue));
            SetLastSliderValue(sliderKind, currentValue);
        }
        if (valueText != null)
        {
            SetText(valueText, SliderValueText(sliderKind, currentValue));
        }
    }

    string SliderValueText(int sliderKind, float currentValue)
    {
        if (sliderKind == SliderShBand)
        {
            return Mathf.RoundToInt(currentValue).ToString();
        }
        if (sliderKind == SliderLODSplatCap)
        {
            int cap = Mathf.RoundToInt(currentValue);
            return cap <= 0 ? Localize("No cap", "上限なし") : cap.ToString();
        }
        return FormatFloat(currentValue);
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

    public void RefreshUI()
    {
        FindRenderer();
        RefreshLocalizedLabels();
        RefreshSortingVisibility();
        ApplyPanelLayout();
        RefreshGallery();
        if (gaussianSplatRenderer == null)
        {
            SetText(currentSplatText, RenderedSplatCountLabel(0));
            SetSliderWithoutNotify(alphaCutoffSlider, GetSliderDisplayValue(SliderAlphaCutoff, DefaultAlphaCutoff));
            SetText(alphaCutoffText, FormatFloat(DefaultAlphaCutoff));
            SetSliderWithoutNotify(alphaCullSlider, DefaultAlphaCull);
            SetText(alphaCullText, FormatFloat(DefaultAlphaCull));
            SetSliderWithoutNotify(lodCullSlider, GetSliderDisplayValue(SliderLODSplatCap, DefaultLODSplatCap));
            SetText(lodCullText, DefaultLODSplatCap.ToString());
            return;
        }
        SetText(currentSplatText, RenderedSplatCountLabel(gaussianSplatRenderer.GetCurrentRenderedSplatCount()));
        SetText(gaussianScaleText, FormatFloat(gaussianSplatRenderer.gaussianScale));
        SetText(alphaCutoffText, FormatFloat(gaussianSplatRenderer.alphaCutoff));
        SetText(alphaCullText, FormatFloat(gaussianSplatRenderer.alphaCull));
        SetText(lodCullText, SliderValueText(SliderLODSplatCap, gaussianSplatRenderer.GetEffectiveCombinedLodSplatBudget()));
        RefreshMaterialControls();
        _sliderValuesInitialized = true;
    }

    bool HasGalleryObjects()
    {
        if (galleryObjects == null)
        {
            return false;
        }
        for (int i = 0; i < galleryObjects.Length; i++)
        {
            if (galleryObjects[i] != null)
            {
                return true;
            }
        }
        return false;
    }

    // Gallery UI and one-at-a-time visibility enforcement exist only when the inspector toggle is on and the
    // manual list holds at least one real object. Disabling gallery keeps the list but stops touching objects.
    bool GalleryActive()
    {
        return galleryEnabled && HasGalleryObjects();
    }

    void RefreshGallery()
    {
        ApplyGalleryVisibility();
        bool active = GalleryActive();
        if (gallerySection != null && gallerySection.activeSelf != active)
        {
            gallerySection.SetActive(active);
        }
        RefreshGalleryMasterLock();
        if (!active || galleryEntries == null)
        {
            return;
        }
        int count = galleryObjects.Length;
        int selected = NormalizedGallerySelectedIndex();
        for (int i = 0; i < galleryEntries.Length; i++)
        {
            GalleryEntry entry = galleryEntries[i];
            if (entry == null)
            {
                continue;
            }
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            ApplyGalleryEntryEditorLayout(entry);
#endif
            bool used = i < count && galleryObjects[i] != null;
            if (entry.gameObject.activeSelf != used)
            {
                entry.gameObject.SetActive(used);
            }
            if (!used)
            {
                continue;
            }
            entry.ui = this;
            entry.index = i;
            SetText(entry.nameText, GalleryObjectName(i));
            SetText(entry.countText, GalleryObjectSplatCountText(i));
            SetText(entry.descriptionText, GalleryObjectDescription(i));
            if (entry.button != null)
            {
                SetInteractable(entry.button, true);
            }
            if (entry.background != null)
            {
                bool selectedEntry = i == selected;
                Color rowColor = selectedEntry ? _gallerySelectedColor : _galleryRowColor;
                if (entry.background.color != rowColor)
                {
                    entry.background.color = rowColor;
                }
                if (entry.button != null)
                {
                    ColorBlock colors = entry.button.colors;
                    colors.normalColor = rowColor;
                    colors.highlightedColor = selectedEntry ? _gallerySelectedHoverColor : _galleryRowHoverColor;
                    colors.pressedColor = _gallerySelectedHoverColor;
                    colors.selectedColor = colors.highlightedColor;
                    colors.disabledColor = rowColor;
                    if (!entry.button.colors.Equals(colors))
                    {
                        entry.button.colors = colors;
                    }
                }
            }
        }
    }

    void RefreshGalleryMasterLock()
    {
        bool isMaster = Networking.LocalPlayer != null && Networking.LocalPlayer.isMaster;
        SetInteractable(galleryMasterLockButton, isMaster); // only the master can toggle the lock
        SetText(galleryMasterNameLabel, _galleryMasterName);
        SetText(galleryMasterLockTitle, Localize("Master lock:", "マスターロック:"));
        SetText(galleryMasterLockLabel, galleryMasterLock ? "ON" : "OFF");
        if (galleryMasterLockButton != null)
        {
            Color c = GalleryMasterLockColor();
            Color hover = new Color(Mathf.Min(1.0f, c.r + 0.08f), Mathf.Min(1.0f, c.g + 0.08f), Mathf.Min(1.0f, c.b + 0.08f), 1.0f);
            ColorBlock cb = galleryMasterLockButton.colors;
            cb.normalColor = c; cb.highlightedColor = hover; cb.selectedColor = hover; cb.pressedColor = c; cb.disabledColor = c;
            if (!galleryMasterLockButton.colors.Equals(cb)) galleryMasterLockButton.colors = cb;
            if (galleryMasterLockButton.targetGraphic != null && galleryMasterLockButton.targetGraphic.color != c)
            {
                galleryMasterLockButton.targetGraphic.color = c;
            }
        }
    }

    Color GalleryMasterLockColor()
    {
        // Green means lock is active; off is intentionally neutral, not another enabled-looking state.
        return galleryMasterLock ? _toggleEnabledColor : new Color(0.28f, 0.30f, 0.34f, 1.0f);
    }

    // Master lock is optional (galleryMasterLock): when on, only the instance master may change the selection.
    bool GalleryCanModify() { return !galleryMasterLock || Networking.LocalPlayer == null || Networking.LocalPlayer.isMaster; }
    void GalleryTakeOwnership() { if (Networking.LocalPlayer != null && !Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject); }

    string GalleryObjectName(int index)
    {
        if (galleryObjects == null || index < 0 || index >= galleryObjects.Length || galleryObjects[index] == null) return "";
        string n = galleryObjects[index].splatName;
        if (n == null || n.Length == 0) n = galleryObjects[index].gameObject.name; // fall back to the object name
        return n;
    }

    string GalleryObjectSplatCountText(int index)
    {
        if (galleryObjects == null || index < 0 || index >= galleryObjects.Length || galleryObjects[index] == null) return "";
        int count = galleryObjects[index].GetMaxLOD0SplatCount();
        if (count <= 0) return "";
        return CompactSplatCount(count) + Localize(" splats", " Splat");
    }

    string CompactSplatCount(int count)
    {
        if (count >= 1000000) return CompactCount(count, 1000000, "M");
        if (count >= 1000) return CompactCount(count, 1000, "K");
        return count.ToString();
    }

    string CompactCount(int count, int unit, string suffix)
    {
        int tenths = (count * 10 + unit / 2) / unit;
        int whole = tenths / 10;
        int fraction = tenths - whole * 10;
        return fraction == 0 ? whole + suffix : whole + "." + fraction + suffix;
    }

    string GalleryObjectDescription(int index)
    {
        if (galleryObjects == null || index < 0 || index >= galleryObjects.Length || galleryObjects[index] == null) return "";
        string d = galleryObjects[index].description;
        return d != null ? d : "";
    }

    int NormalizedGallerySelectedIndex()
    {
        if (galleryObjects == null || galleryObjects.Length == 0)
        {
            return -1;
        }
        int selected = Mathf.Clamp(_gallerySelectedIndex, 0, galleryObjects.Length - 1);
        if (galleryObjects[selected] == null)
        {
            selected = -1;
            for (int i = 0; i < galleryObjects.Length; i++)
            {
                if (galleryObjects[i] != null)
                {
                    selected = i;
                    break;
                }
            }
        }
        if (selected != _gallerySelectedIndex)
        {
            _gallerySelectedIndex = selected;
        }
        return selected;
    }

    public void SelectGalleryIndex(int index)
    {
        if (!GalleryCanModify()) return;
        if (galleryObjects == null || index < 0 || index >= galleryObjects.Length) return;
        GalleryTakeOwnership();
        _gallerySelectedIndex = index;
        ApplyGalleryVisibility();
        RequestSerialization();
        RefreshUI();
    }

    // When the gallery is enabled, exactly one LISTED object is shown; the rest of the list is hidden. When the
    // inspector toggle is off, the list/selection are left intact and no listed object active state is changed.
    // Objects that are not in the list are never touched. Runs in edit mode too. Visibility is just GameObject
    // active state - the renderer's combine already excludes inactive objects, so the gallery never touches the renderer.
    void ApplyGalleryVisibility()
    {
        if (!GalleryActive())
        {
            return;
        }
        int selected = NormalizedGallerySelectedIndex();
        if (selected < 0)
        {
            return;
        }
        GaussianSplatObject selectedObject = galleryObjects[selected];
        for (int i = 0; i < galleryObjects.Length; i++)
        {
            GaussianSplatObject obj = galleryObjects[i];
            if (obj == null)
            {
                continue;
            }
            bool show = obj == selectedObject;
            if (obj.gameObject.activeSelf != show)
            {
                obj.gameObject.SetActive(show);
            }
        }
    }

    public void IncreaseCameraQuantization() { StepCameraQuantization(cameraQuantizationStep); }
    public void DecreaseCameraQuantization() { StepCameraQuantization(-cameraQuantizationStep); }

    public void ToggleVrcLightVolumes() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.ToggleVrcLightVolumes(); RefreshUI(); }

    public void IncreaseGaussianScale() { StepGaussianScale(gaussianScaleStep); }
    public void DecreaseGaussianScale() { StepGaussianScale(-gaussianScaleStep); }

    void SetLanguage(int language) { selectedLanguage = Mathf.Clamp(language, LanguageEnglish, LanguageJapanese); RefreshUI(); }
    public void SetLanguageEnglish() { SetLanguage(LanguageEnglish); }
    public void SetLanguageJapanese() { SetLanguage(LanguageJapanese); }
    public void ToggleAdvancedSettings() { showAdvancedSettings = !showAdvancedSettings; RefreshUI(); }

    public void SetQualityVeryLow() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityVeryLow(); RefreshUI(); }
    public void SetQualityLow() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityLow(); RefreshUI(); }
    public void SetQualityMedium() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityMedium(); RefreshUI(); }
    public void SetQualityHigh() { if (gaussianSplatRenderer == null) return; gaussianSplatRenderer.SetQualityHigh(); RefreshUI(); }
}

}
