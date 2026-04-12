using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon;
using UnityEngine.UI;
using UnityEngine.EventSystems;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using UnityEditor;
using UnityEditor.Events;
using System.Collections.Generic;
using System.Reflection;
using UdonSharpEditor;
#endif

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 3; // Screen camera + Photo camera + Mirror camera
    private Vector3[] _prevCameraPos;
    private RadixSort _radixSort;
    private MeshRenderer _sortedRenderer;
    private Material keyValueMat;
    private GameObject splatObject;

    [Header("Gaussian Splat Object")]
    [UdonSynced, Tooltip("The index of the currently rendered splat object in the splatObjects array.")]
    public int splatObjectIndex = 0; // Index of the current splat object in the splatObjects array
    [Tooltip("The GameObjects that contain the Gaussian Splat roots.")]
    public GameObject[] splatObjects;

    [Header("Render Settings")]
    [Tooltip("Minimum distance for sorting splats. Splat positions closer than this will not be sorted. The smaller the minmax range the more accurate the sorting")]
    [SerializeField] float minSortDistance = 0.0f;
    [Tooltip("Maximum distance for sorting splats. Splat positions further than this will not be sorted. The smaller the minmax range the more accurate the sorting")]
    [SerializeField] float maxSortDistance = 150.0f;
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    [Tooltip("If true, the splat render order will be updated every frame. Useful for animated splats. If false, it will only update when the camera position changes.")]
    [SerializeField] bool alwaysUpdate = false;
    [Tooltip("Number of sorting steps for the radix sort. The more steps the more bits of the distance can be sorted, so the render order is more accurate. The fewer steps the faster the sorting, so it is a tradeoff between performance and accuracy. Default is 16 bits, which is 4 sorting steps.")]
    [Range(2, 8)] [SerializeField] int sortingSteps = 4;
    [Tooltip("Render texture used to store the sorted splat render order. This should be a RenderTexture with the same dimensions as the sorting textures used in the radix sort.")]
    public RenderTexture splatRenderOrder;

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [SerializeField] public bool overrideMaterialProperties = false;
    [Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] public float alphaCutoff = 0.03f;

    // [Header("Optional Mirror")]
    // [Tooltip("Optional mirror GameObject. If set, the script will also sort splats for the mirror camera position.")]
    // public GameObject mirror;

    void ResetCameraPositions()
    {
        for (int i = 0; i < MAX_CAMERA_COUNT; i++)
        {
            _prevCameraPos[i] = Vector3.positiveInfinity; // Reset to a value that will always trigger an update
        }
    }

    GameObject FindNamedChild(GameObject rootObject, string childName)
    {
        if (rootObject == null)
        {
            return null;
        }

        Transform child = rootObject.transform.Find(childName);
        if (child != null)
        {
            return child.gameObject;
        }

        return null;
    }

    void ShowSorted(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            gaussianSplatObject.ShowSorted();
            return;
        }

        GameObject sortedObject = FindNamedChild(rootObject, "Sorted");
        GameObject stochasticObject = FindNamedChild(rootObject, "Stochastic");
        if (sortedObject != null)
        {
            sortedObject.SetActive(true);
        }
        if (stochasticObject != null)
        {
            stochasticObject.SetActive(false);
        }
    }

    void DeactivateSplatObjects()
    {
        if (splatObjects == null)
        {
            return;
        }

        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject splatObj = splatObjects[i];
            if (splatObj != null)
            {
                ShowSorted(splatObj);
                splatObj.SetActive(false);
            }
        }
    }

    bool HasSplatObjects()
    {
        return splatObjects != null && splatObjects.Length > 0;
    }

    bool IsValidSplatObjectIndex(int index)
    {
        return HasSplatObjects() && index >= 0 && index < splatObjects.Length;
    }

    bool ApplySelectedSplatObject()
    {
        if (!HasSplatObjects())
        {
            return false;
        }

        if (!IsValidSplatObjectIndex(splatObjectIndex))
        {
            Debug.LogError($"Invalid splat object index: {splatObjectIndex}. Must be between 0 and {splatObjects.Length - 1}.");
            return false;
        }

        GameObject selectedSplatObject = splatObjects[splatObjectIndex];
        if (selectedSplatObject == null)
        {
            Debug.LogError($"Splat object at index {splatObjectIndex} is null. Please ensure the splatObjects array is populated correctly.");
            return false;
        }

        if (splatObject == selectedSplatObject)
        {
            return true;
        }

        DeactivateSplatObjects();
        splatObject = selectedSplatObject;
        splatObject.SetActive(true);
        ShowSorted(splatObject);
        return true;
    }

    void InitializeSplatObject()
    {
        if (!HasSplatObjects())
        {
            return;
        }

        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject splatObj = splatObjects[i];
            if (splatObj == null)
            {
                continue;
            }

            ShowSorted(splatObj);
            splatObj.SetActive(false);
        }

        if (!IsValidSplatObjectIndex(splatObjectIndex))
        {
            Debug.LogError($"Invalid splat object index: {splatObjectIndex}. Must be between 0 and {splatObjects.Length - 1}.");
            return;
        }

        splatObject = null;
        if (!ApplySelectedSplatObject())
        {
            return;
        }
    }

    MeshRenderer GetSortedRenderer(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return null;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            MeshRenderer renderer = gaussianSplatObject.GetSortedRenderer();
            if (renderer != null)
            {
                return renderer;
            }
        }

        GameObject sortedObject = FindNamedChild(rootObject, "Sorted");
        if (sortedObject != null)
        {
            MeshRenderer childRenderer = (MeshRenderer)sortedObject.GetComponent(typeof(MeshRenderer));
            if (childRenderer != null)
            {
                return childRenderer;
            }
        }

        return (MeshRenderer)rootObject.GetComponent(typeof(MeshRenderer));
    }

    public void SetSplatObjectIndex(int index)
    {
        if (splatObjectIndex == index && splatObject != null)
        {
            return;
        }

        if (!HasSplatObjects())
        {
            Debug.LogError("No splat objects have been assigned to the GaussianSplatRenderer.");
            return;
        }

        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return;
        }

        ResetCameraPositions();
        splatObjectIndex = index;
        if (!ApplySelectedSplatObject())
        {
            return;
        }
    }

    public GameObject GetObjectByIndex(int index)
    {
        if (!HasSplatObjects())
        {
            Debug.LogError("No splat objects have been assigned to the GaussianSplatRenderer.");
            return null;
        }

        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return null;
        }
        return splatObjects[index];
    }

    public void SelectSplatObject(int index)
    {
        if (Networking.LocalPlayer != null)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        SetSplatObjectIndex(index);
    }

    public void SetGaussianScale(float value)
    {
        overrideMaterialProperties = true;
        gaussianScale = Mathf.Clamp(value, 0.0f, 2.0f);
    }

    public void SetAlphaCutoff(float value)
    {
        overrideMaterialProperties = true;
        alphaCutoff = Mathf.Clamp01(value);
    }

    public string GetCurrentSplatName()
    {
        if (!ApplySelectedSplatObject())
        {
            return "None";
        }

        return splatObject.name;
    }

    void Start()
    {
        _radixSort = (RadixSort)GetComponent<RadixSort>();
        if (_radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return;
        }
        if (splatRenderOrder == null)
        {
            Debug.LogError("Splat Render Order texture is not assigned. Please assign a RenderTexture.");
            return;
        }

        _prevCameraPos = new Vector3[MAX_CAMERA_COUNT];
        ResetCameraPositions();
        InitializeSplatObject();
    }

    Vector3 QuantizePosition(Vector3 position)
    {
        if (cameraPositionQuantization <= 0)
            return position;

        return new Vector3(
            Mathf.Round(position.x / cameraPositionQuantization) * cameraPositionQuantization,
            Mathf.Round(position.y / cameraPositionQuantization) * cameraPositionQuantization,
            Mathf.Round(position.z / cameraPositionQuantization) * cameraPositionQuantization
        );
    }
    bool UpdateMaterials()
    {
        if (!ApplySelectedSplatObject())
        {
            return false;
        }

        _sortedRenderer = GetSortedRenderer(splatObject);
        if (_sortedRenderer == null)
        {
            Debug.LogError($"No sorted MeshRenderer found on {splatObject.name}.");
            return false;
        }

        Material[] splatMats = _sortedRenderer.materials;
        Vector4 minMaxSortDistance = new Vector4(minSortDistance, maxSortDistance, 0, 0);
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
            splatMat.SetVector("_MinMaxSortDistance", minMaxSortDistance);
            if (overrideMaterialProperties)
            {
                splatMat.SetFloat("_GaussianMul", gaussianScale);
                splatMat.SetFloat("_AlphaCutoff", alphaCutoff);
            }
        }

        Texture positions = null;
        if (splatMats.Length > 1)
        {
            positions = splatMats[1].GetTexture("_GS_Positions");
        }
        else
        {
            positions = splatMats[0].GetTexture("_GS_Positions");
        }

        _radixSort.elementCount = positions.width * positions.height;
        _radixSort.maxKeyBits = sortingSteps * 4; // Each sorting step sorts 4 bits, so total bits = steps * 4
        keyValueMat = _radixSort.computeKeyValues;
        keyValueMat.SetTexture("_GS_Positions", positions);
        keyValueMat.SetVector("_MinMaxSortDistance", minMaxSortDistance);
        keyValueMat.SetFloat("_KeyScale", (float)((1 << (sortingSteps * 4)) - 1));
        keyValueMat.SetMatrix("_SplatToWorld", _sortedRenderer.transform.localToWorldMatrix);
        return true;
    }

    bool SortCamera(Vector3 cameraPos, int cameraID, bool forceUpdate = false)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        if (quantizedPos == _prevCameraPos[cameraID] && !alwaysUpdate && !forceUpdate)
        {
            return false;
        }

        _prevCameraPos[cameraID] = quantizedPos;
        keyValueMat.SetVector("_CameraPos", cameraPos);
        _radixSort.Sort();
        VRCGraphics.Blit(_radixSort.keyValues0, splatRenderOrder, 0, cameraID);
        return true;
    }

    public void SortCameras(Vector3 screenCamPos)
    {
        if (!ApplySelectedSplatObject())
        {
            return;
        }

        if (!UpdateMaterials())
        {
            return;
        }

        if (SortCamera(screenCamPos, 0))
        {
            ShowSorted(splatObject);
        }

        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        if (photoCam != null && photoCam.Active) SortCamera(photoCam.Position, 1);

        // if (mirror != null && mirror.activeInHierarchy) //Mirror order is currently broken in VRChat
        // {
        //     Vector3 mirrorZ = mirror.transform.forward;
        //     float zDist = Vector3.Dot(mirrorZ, mirror.transform.position - screenCamPos);
        //     if (zDist > 0)
        //     {
        //         Vector3 mirrorCamPos = screenCamPos + 2 * zDist * mirrorZ;
        //         _sortedRenderer.material.SetVector("_MirrorCameraPos", mirrorCamPos);
        //         SortCamera(mirrorCamPos, 2, true);
        //     }
        // }
    }

    void Update()
    {
        if (!ApplySelectedSplatObject())
        {
            return;
        }

        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        SortCameras(screenCamPos);
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static Type _cachedVrChatUiShapeType;

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

    static T AddGeneratedUdonSharpComponent<T>(GameObject targetObject, string undoLabel) where T : UdonSharpBehaviour
    {
        Undo.RegisterCompleteObjectUndo(targetObject, undoLabel);
        return targetObject.AddUdonSharpComponent<T>();
    }

    static UdonBehaviour GetBackingUdonBehaviour(UdonSharpBehaviour proxyBehaviour)
    {
        if (proxyBehaviour == null)
        {
            return null;
        }

        return UdonSharpEditorUtility.GetBackingUdonBehaviour(proxyBehaviour);
    }

    static void AddUdonSharpButtonEvent(Button button, UdonSharpBehaviour targetBehaviour, string eventName)
    {
        if (button == null || targetBehaviour == null || string.IsNullOrEmpty(eventName))
        {
            return;
        }

        UdonBehaviour backingBehaviour = GetBackingUdonBehaviour(targetBehaviour);
        if (backingBehaviour == null)
        {
            return;
        }

        UnityEventTools.AddStringPersistentListener(button.onClick, backingBehaviour.SendCustomEvent, eventName);
        EditorUtility.SetDirty(backingBehaviour);
    }

    static Material CreateOpaqueBackgroundMaterial()
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            return null;
        }

        Material material = new Material(shader);
        material.name = "Gaussian Splat UI Background";
        material.color = new Color(0.08f, 0.08f, 0.1f, 1.0f);
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
        RectTransform rectTransform = CreateRectTransform(objectName, parent, new Vector2(0.0f, fontSize + 12.0f));
        Text text = rectTransform.gameObject.AddComponent<Text>();
        text.font = GetBuiltinUiFont();
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = textColor;
        text.text = textValue;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        LayoutElement layoutElement = rectTransform.gameObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = fontSize + 18.0f;
        layoutElement.minHeight = fontSize + 18.0f;
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

    [ContextMenu("Generate UI")]
    void GenerateUI()
    {
        EnsureEventSystemExists();

        Transform existingUi = transform.Find("Gaussian Splat UI");
        if (existingUi != null)
        {
            Undo.DestroyObjectImmediate(existingUi.gameObject);
        }

        GameObject canvasObject = new GameObject("Gaussian Splat UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Undo.RegisterCreatedObjectUndo(canvasObject, "Create Gaussian Splat UI");
        canvasObject.transform.SetParent(transform, false);
        canvasObject.transform.localPosition = new Vector3(0.0f, 1.2f, 1.5f);
        canvasObject.transform.localRotation = Quaternion.identity;
        canvasObject.transform.localScale = Vector3.one * 0.0025f;

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
        scaler.dynamicPixelsPerUnit = 10.0f;

        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(980.0f, 640.0f);

        TryAddVrChatUiShape(canvasObject);

        CreateOpaqueBackgroundPlate(canvasObject.transform, canvasRect.sizeDelta);

        GameObject panelObject = CreateVerticalGroup("Panel", canvasObject.transform, new RectOffset(12, 12, 10, 10), 10.0f, TextAnchor.UpperLeft);
        RectTransform panelRect = panelObject.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(980.0f, 0.0f);

        GaussianSplatRendererUI generatedUi = AddGeneratedUdonSharpComponent<GaussianSplatRendererUI>(canvasObject, "Add Gaussian Splat Renderer UI");
        generatedUi.gaussianSplatRenderer = this;

        GameObject bodyRow = CreateHorizontalGroup("Body Row", panelObject.transform, 18.0f, false);
        SetPreferredHeight(bodyRow, 560.0f, 0.0f);

        GameObject settingsColumn = CreateVerticalGroup("Settings Column", bodyRow.transform, new RectOffset(0, 0, 0, 0), 12.0f, TextAnchor.UpperLeft);
        SetPreferredWidth(settingsColumn, 400.0f, 0.0f);

        GameObject splatColumn = CreateVerticalGroup("Splat Column", bodyRow.transform, new RectOffset(0, 0, 0, 0), 10.0f, TextAnchor.UpperLeft);
        SetPreferredWidth(splatColumn, 520.0f, 1.0f);

        CreateTextElement("Title", settingsColumn.transform, "Gaussian Splat Controls", 22, TextAnchor.MiddleLeft, Color.white);
        generatedUi.currentSplatText = CreateTextElement("Current Splat", settingsColumn.transform, "Current Splat: None", 16, TextAnchor.MiddleLeft, new Color(0.9f, 0.9f, 0.9f, 1.0f));

        CreateTextElement("Settings Section", settingsColumn.transform, "Material Settings", 18, TextAnchor.MiddleLeft, Color.white);

        GameObject gaussianScaleRow = CreateHorizontalGroup("Gaussian Scale Row", settingsColumn.transform, 8.0f, false);
        Text gaussianScaleLabel = CreateTextElement("Gaussian Scale Label", gaussianScaleRow.transform, "Gaussian Scale", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(gaussianScaleLabel.gameObject, 210.0f, 1.0f);
        Button gaussianScaleDownButton = CreateButtonElement("Gaussian Scale Down", gaussianScaleRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.gaussianScaleText = CreateTextElement("Gaussian Scale Value", gaussianScaleRow.transform, "1", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.gaussianScaleText.gameObject, 72.0f, 0.0f);
        Button gaussianScaleUpButton = CreateButtonElement("Gaussian Scale Up", gaussianScaleRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(gaussianScaleDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseGaussianScale));
        AddUdonSharpButtonEvent(gaussianScaleUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseGaussianScale));

        GameObject alphaCutoffRow = CreateHorizontalGroup("Alpha Cutoff Row", settingsColumn.transform, 8.0f, false);
        Text alphaCutoffLabel = CreateTextElement("Alpha Cutoff Label", alphaCutoffRow.transform, "Alpha Cutoff", 16, TextAnchor.MiddleLeft, Color.white);
        SetPreferredWidth(alphaCutoffLabel.gameObject, 210.0f, 1.0f);
        Button alphaCutoffDownButton = CreateButtonElement("Alpha Cutoff Down", alphaCutoffRow.transform, "-", new Color(0.45f, 0.24f, 0.18f, 1.0f), 42.0f, 0.0f);
        generatedUi.alphaCutoffText = CreateTextElement("Alpha Cutoff Value", alphaCutoffRow.transform, "0.03", 16, TextAnchor.MiddleCenter, new Color(0.95f, 0.95f, 0.95f, 1.0f));
        SetPreferredWidth(generatedUi.alphaCutoffText.gameObject, 72.0f, 0.0f);
        Button alphaCutoffUpButton = CreateButtonElement("Alpha Cutoff Up", alphaCutoffRow.transform, "+", new Color(0.18f, 0.4f, 0.24f, 1.0f), 42.0f, 0.0f);
        AddUdonSharpButtonEvent(alphaCutoffDownButton, generatedUi, nameof(GaussianSplatRendererUI.DecreaseAlphaCutoff));
        AddUdonSharpButtonEvent(alphaCutoffUpButton, generatedUi, nameof(GaussianSplatRendererUI.IncreaseAlphaCutoff));

        CreateTextElement("Splat Section", splatColumn.transform, "Splat Selection", 18, TextAnchor.MiddleLeft, Color.white);
        GameObject splatListPanel = CreateVerticalGroup("Splat List Panel", splatColumn.transform, new RectOffset(8, 8, 8, 8), 8.0f, TextAnchor.UpperLeft);
        Image splatListPanelImage = splatListPanel.AddComponent<Image>();
        splatListPanelImage.color = new Color(0.09f, 0.09f, 0.11f, 1.0f);
        SetPreferredHeight(splatListPanel, 500.0f, 0.0f);

        GameObject splatScrollRow = CreateHorizontalGroup("Splat Scroll Controls", splatListPanel.transform, 8.0f, false);
        Button scrollUpButton = CreateButtonElement("Splat Scroll Up", splatScrollRow.transform, "Up", new Color(0.15f, 0.24f, 0.36f, 1.0f), 96.0f, 0.0f);
        Button scrollDownButton = CreateButtonElement("Splat Scroll Down", splatScrollRow.transform, "Down", new Color(0.15f, 0.24f, 0.36f, 1.0f), 96.0f, 0.0f);
        generatedUi.splatScrollUpButton = scrollUpButton;
        generatedUi.splatScrollDownButton = scrollDownButton;
        AddUdonSharpButtonEvent(scrollUpButton, generatedUi, nameof(GaussianSplatRendererUI.ScrollSplatListUp));
        AddUdonSharpButtonEvent(scrollDownButton, generatedUi, nameof(GaussianSplatRendererUI.ScrollSplatListDown));

        GameObject splatButtonContainer = CreateVerticalGroup("Splat Button Container", splatListPanel.transform, new RectOffset(0, 0, 0, 0), 8.0f, TextAnchor.UpperLeft);

        List<Button> splatButtons = new List<Button>();
        List<int> splatButtonIndices = new List<int>();
        List<string> splatButtonLabels = new List<string>();

        const int visibleSplatButtonCount = 8;
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
        };

        for (int slotIndex = 0; slotIndex < visibleSplatButtonCount; slotIndex++)
        {
            Button slotButton = CreateButtonElement("Splat Slot " + slotIndex, splatButtonContainer.transform, "", new Color(0.2f, 0.2f, 0.24f, 1.0f), 0.0f, 1.0f);
            SetPreferredHeight(slotButton.gameObject, 42.0f, 0.0f);
            splatButtons.Add(slotButton);
            AddUdonSharpButtonEvent(slotButton, generatedUi, slotSelectEventNames[slotIndex]);
        }


        if (!HasSplatObjects())
        {
            generatedUi.splatButtonIndices = new int[0];
            generatedUi.splatButtonLabels = new string[0];
        }
        else
        {
            for (int i = 0; i < splatObjects.Length; i++)
            {
                GameObject listedSplatObject = splatObjects[i];
                if (listedSplatObject == null)
                {
                    continue;
                }

                splatButtonIndices.Add(i);
                splatButtonLabels.Add(listedSplatObject.name);
            }

            generatedUi.splatButtonIndices = splatButtonIndices.ToArray();
            generatedUi.splatButtonLabels = splatButtonLabels.ToArray();
        }

        generatedUi.splatButtons = splatButtons.ToArray();

        generatedUi.RefreshUI();
        EditorUtility.SetDirty(canvasObject);
        EditorUtility.SetDirty(this);
        EditorUtility.SetDirty(generatedUi);
        UdonBehaviour generatedUiBacking = GetBackingUdonBehaviour(generatedUi);
        if (generatedUiBacking != null)
        {
            EditorUtility.SetDirty(generatedUiBacking);
        }
        Selection.activeGameObject = canvasObject;
    }

    List<GaussianSplatObject> GetAllObjectsOnlyInScene()
    {
        List<GaussianSplatObject> objectsInScene = new List<GaussianSplatObject>();

        foreach (GaussianSplatObject go in Resources.FindObjectsOfTypeAll(typeof(GaussianSplatObject)) as GaussianSplatObject[])
        {
            if (!EditorUtility.IsPersistent(go.transform.root.gameObject) && !(go.hideFlags == HideFlags.NotEditable || go.hideFlags == HideFlags.HideAndDontSave))
                objectsInScene.Add(go);
        }

        return objectsInScene;
    }

    [ContextMenu("Collect Gaussian Splat Objects for the renderer")]
    void CollectSplatObjects()
    {
        GaussianSplatRenderer[] renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.InstanceID);

        if (renderers.Length > 1)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Please ensure only one instance is present in the scene.");
            return;
        }

        foreach (var renderer in renderers)
        {
            List<GaussianSplatObject> objectsInScene = GetAllObjectsOnlyInScene();
            renderer.splatObjects = new GameObject[objectsInScene.Count];

            for (int i = 0; i < objectsInScene.Count; i++)
            {
                GaussianSplatObject go = objectsInScene[i];
                if (go != null)
                {
                    renderer.splatObjects[i] = go.gameObject;
                }
                else
                {
                    Debug.LogWarning($"Gaussian Splat Object at index {i} is null. Please ensure all objects are valid.");
                }
            }

            Debug.Log($"Collected {renderer.splatObjects.Length} Gaussian Splat Objects for the renderer.");
        }
    }
#endif
}

}
