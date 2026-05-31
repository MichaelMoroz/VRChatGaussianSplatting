using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon;
using UnityEngine.UI;
using UnityEngine.EventSystems;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.Rendering;
using UdonSharpEditor;
#endif

namespace GaussianSplatting
{

public enum GaussianSplatRenderingMode
{
    SingleSplat = 0,
    CombineAllSplats = 1,
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 2; // Screen camera + Photo camera
    const int COMBINED_SOURCE_BATCH_SIZE = 8;
    const int MAX_COMBINED_SPLAT_COUNT = 1 << 24;
    const int DEFAULT_COMBINED_SPLATS_PER_PASS = 3 * 256 * 1024;
    const int DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT = 1;
    const int SCREEN_CAMERA_ID = 0;
    const int PHOTO_CAMERA_ID = 1;
    const int NO_ACTIVE_SORT = -1;
    const float DEFAULT_ALPHA_CUTOFF = 0.03f;

    private Vector3[] _completedCameraPos;
    private Vector3[] _pendingCameraPos;
    private Vector3[] _pendingCameraWorldPos;
    private bool[] _hasCompletedSort;
    private bool[] _hasPendingSort;
    private int _activeSortCameraId = NO_ACTIVE_SORT;
    private Vector3 _activeSortQuantizedPos = Vector3.positiveInfinity;
    private RadixSort _radixSort;
    private MeshRenderer _sortedRenderer;
    private Material keyValueMat;
    private GameObject splatObject;

    [HideInInspector, SerializeField] GameObject[] cachedSceneSplatObjects;
    [SerializeField] GaussianSplatRenderingMode renderingMode = GaussianSplatRenderingMode.SingleSplat;
    [SerializeField] MeshRenderer combinedSortedRenderer;
    [SerializeField] Material combineDataMaterial;
    [SerializeField] RenderTexture combinedPositions;
    [SerializeField] RenderTexture combinedRotations;
    [SerializeField] RenderTexture combinedScales;
    [SerializeField] RenderTexture combinedColorsCamera;
    [SerializeField] RenderTexture combinedColorsScratch;
    private int _combinedActualSplatCount;
    GaussianSplatRenderingMode _lastValidatedRenderingMode;

    [Header("Render Settings")]
    [Tooltip("Quantization of camera position to avoid unnecessary updates and jitter. Set to 0 to disable. Default is 10 cm.")]
    [SerializeField] float cameraPositionQuantization = 0.1f;
    [Tooltip("If true, the splat render order will be updated every frame. Useful for animated splats. If false, it will only update when the camera position changes.")]
    [SerializeField] bool alwaysUpdate = false;
    [Tooltip("Number of frames used to pipeline the 8 radix sort subpasses. 1 sorts fully in one frame; 8 runs one subpass per frame.")]
    [Range(1, 8)] [SerializeField] int sortPipelineFrames = 2;
    [Tooltip("Render texture array used to store sorted splat render order. Slice 0 is screen, slice 1 is photo.")]
    public RenderTexture splatRenderOrder;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    sealed class EditorPreviewTargetState
    {
        public MeshRenderer sortedRenderer;
        public RenderTexture renderOrder;
        public int generation;
    }

    static GameObject _editorPreviewSorterObject;
    static RadixSort _editorPreviewRadixSort;
    static readonly Dictionary<int, EditorPreviewTargetState> _editorPreviewTargets = new Dictionary<int, EditorPreviewTargetState>();
    static MaterialPropertyBlock _editorPreviewPropertyBlock;
    static int _editorPreviewGeneration;
#endif

    [Tooltip("If true, the material properties will be overridden with the values set in this script. If false, the material properties will be set to their default values.")]
    [UdonSynced, SerializeField] public bool overrideMaterialProperties = false;
    [UdonSynced, Range(0, 3)] [SerializeField] int requestedSHBand = 3;
    [UdonSynced, Range(0.0f, 2.0f)] [SerializeField] public float gaussianScale = 1.0f;
    [Range(0.0f, 1.0f)] [SerializeField] float thinThreshold = 0.005f;
    [Range(0.0f, 3.0f)] [SerializeField] float antiAliasing = 1.0f;
    [Range(-20.0f, 10.0f)] [SerializeField] float log2MinScale = -15.0f;
    [Range(0.005f, 0.1f)] [SerializeField] public float alphaCutoff = DEFAULT_ALPHA_CUTOFF;
    [Range(0.0f, 100.0f)] [SerializeField] float scaleCutoff = 100.0f;
    [Range(0.0f, 5.0f)] [SerializeField] float exposure = 1.0f;
    [Range(0.0f, 5.0f)] [SerializeField] float opacity = 1.0f;
    [SerializeField] Vector3 oklchShift = Vector3.zero;
    [SerializeField] float gamma = 1.0f;
    [UdonSynced, SerializeField] bool useVrcLightVolumes = false;
    [Range(0.0f, 4.0f)] [SerializeField] float lightVolumeIntensity = 1.0f;

    // [Header("Optional Mirror")]
    // [Tooltip("Optional mirror GameObject. If set, the script will also sort splats for the mirror camera position.")]
    // public GameObject mirror;

    void ResetCameraPositions()
    {
        if (_completedCameraPos == null || _completedCameraPos.Length < MAX_CAMERA_COUNT
            || _pendingCameraPos == null || _pendingCameraPos.Length < MAX_CAMERA_COUNT
            || _pendingCameraWorldPos == null || _pendingCameraWorldPos.Length < MAX_CAMERA_COUNT
            || _hasCompletedSort == null || _hasCompletedSort.Length < MAX_CAMERA_COUNT
            || _hasPendingSort == null || _hasPendingSort.Length < MAX_CAMERA_COUNT)
        {
            return;
        }

        for (int i = 0; i < MAX_CAMERA_COUNT; i++)
        {
            _completedCameraPos[i] = Vector3.positiveInfinity;
            _pendingCameraPos[i] = Vector3.positiveInfinity;
            _pendingCameraWorldPos[i] = Vector3.positiveInfinity;
            _hasCompletedSort[i] = false;
            _hasPendingSort[i] = false;
        }

        _activeSortCameraId = NO_ACTIVE_SORT;
        _activeSortQuantizedPos = Vector3.positiveInfinity;
        if (_radixSort != null)
        {
            _radixSort.CancelSort();
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
        if (sortedObject != null)
        {
            sortedObject.SetActive(true);
        }

        Transform rootTransform = rootObject.transform;
        for (int i = 0; i < rootTransform.childCount; i++)
        {
            Transform child = rootTransform.GetChild(i);
            if (child == null || (sortedObject != null && child.gameObject == sortedObject))
            {
                continue;
            }

            if (child.GetComponent(typeof(Renderer)) != null || child.GetComponent(typeof(MeshFilter)) != null)
            {
                child.gameObject.SetActive(false);
            }
        }
    }

    void SetSplatMeshRendererEnabled(GameObject rootObject, bool enabled)
    {
        if (rootObject == null)
        {
            return;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            gaussianSplatObject.SetSortedRendererEnabled(enabled);
            return;
        }

        MeshRenderer renderer = GetSortedRenderer(rootObject);
        if (renderer != null)
        {
            renderer.enabled = enabled;
        }
    }

    void SetCombinedRendererEnabled(bool enabled)
    {
        if (combinedSortedRenderer != null)
        {
            combinedSortedRenderer.enabled = enabled;
            combinedSortedRenderer.gameObject.SetActive(enabled);
        }
    }

    void EnforceSingleSplatMeshRenderer(GameObject activeObject)
    {
#if !COMPILER_UDONSHARP
        GaussianSplatObject[] sceneObjects = FindSceneSplatObjects(true);
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject sceneObject = sceneObjects[i];
            if (sceneObject != null)
            {
                sceneObject.SetSortedRendererEnabled(sceneObject.gameObject == activeObject);
            }
        }
#else
        SetSplatMeshRendererEnabled(activeObject, true);
#endif
        SetCombinedRendererEnabled(false);
    }

    GaussianSplatObject[] FindSceneSplatObjects(bool includeInactive)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return CollectSceneSplatObjects(gameObject.scene, includeInactive);
        }
#endif

        GameObject[] sceneObjectRoots = cachedSceneSplatObjects;
        if (sceneObjectRoots == null || sceneObjectRoots.Length == 0)
        {
#if COMPILER_UDONSHARP
            return new GaussianSplatObject[0];
#else
            return UnityEngine.Object.FindObjectsOfType<GaussianSplatObject>(includeInactive);
#endif
        }

        int sceneObjectCount = 0;
        for (int i = 0; i < sceneObjectRoots.Length; i++)
        {
            GameObject sceneObjectRoot = sceneObjectRoots[i];
            GaussianSplatObject currentObject = sceneObjectRoot != null
                ? sceneObjectRoot.GetComponent<GaussianSplatObject>()
                : null;
            if (currentObject == null || (!includeInactive && !currentObject.gameObject.activeInHierarchy))
            {
                continue;
            }

            sceneObjectCount++;
        }

        if (sceneObjectCount == 0)
        {
            return new GaussianSplatObject[0];
        }

        GaussianSplatObject[] sceneObjects = new GaussianSplatObject[sceneObjectCount];
        int sceneObjectIndex = 0;
        for (int i = 0; i < sceneObjectRoots.Length; i++)
        {
            GameObject sceneObjectRoot = sceneObjectRoots[i];
            GaussianSplatObject currentObject = sceneObjectRoot != null
                ? sceneObjectRoot.GetComponent<GaussianSplatObject>()
                : null;
            if (currentObject == null || (!includeInactive && !currentObject.gameObject.activeInHierarchy))
            {
                continue;
            }

            sceneObjects[sceneObjectIndex] = currentObject;
            sceneObjectIndex++;
        }

        return sceneObjects;
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    GaussianSplatObject[] CollectSceneSplatObjects(UnityEngine.SceneManagement.Scene scene, bool includeInactive)
    {
        List<GaussianSplatObject> sceneObjects = new List<GaussianSplatObject>();
        GaussianSplatObject[] allObjects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
        for (int i = 0; i < allObjects.Length; i++)
        {
            GaussianSplatObject currentObject = allObjects[i];
            if (!IsSceneSplatObject(currentObject, scene))
            {
                continue;
            }

            if (!includeInactive && !currentObject.gameObject.activeInHierarchy)
            {
                continue;
            }

            sceneObjects.Add(currentObject);
        }

        return sceneObjects.ToArray();
    }
#endif

    bool SetCurrentSplatObject(GameObject activeGameObject, bool applyMaterialSettings)
    {
        if (activeGameObject == null)
        {
            splatObject = null;
            _sortedRenderer = null;
            return false;
        }

        if (GetSortedRenderer(activeGameObject) == null)
        {
            return false;
        }

        if (splatObject == activeGameObject)
        {
            SetSplatMeshRendererEnabled(splatObject, true);
            EnforceSingleSplatMeshRenderer(splatObject);
            if (applyMaterialSettings)
            {
                ApplyMaterialSettingsToSelectedObject();
            }

            return true;
        }

        SetSplatMeshRendererEnabled(splatObject, false);
        splatObject = activeGameObject;
        ShowSorted(splatObject);
        EnforceSingleSplatMeshRenderer(splatObject);
        ResetCameraPositions();
        if (applyMaterialSettings)
        {
            ApplyMaterialSettingsToSelectedObject();
        }

        return true;
    }

    GameObject FindFirstActiveSplatObject()
    {
        GaussianSplatObject[] sceneObjects = FindSceneSplatObjects(false);
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject currentObject = sceneObjects[i];
            if (currentObject == null || !currentObject.gameObject.activeInHierarchy)
            {
                continue;
            }

            GameObject currentGameObject = currentObject.gameObject;
            ShowSorted(currentGameObject);
            if (GetSortedRenderer(currentGameObject) != null)
            {
                return currentGameObject;
            }
        }

        return null;
    }

    bool ApplyActiveSplatObject(bool applyMaterialSettings)
    {
#if COMPILER_UDONSHARP
        if (splatObject != null && splatObject.activeInHierarchy)
        {
            EnforceSingleSplatMeshRenderer(splatObject);
            return true;
        }
#endif

        GameObject activeSplatObject = FindFirstActiveSplatObject();
        if (activeSplatObject == null)
        {
            splatObject = null;
            _sortedRenderer = null;
            return false;
        }

        return SetCurrentSplatObject(activeSplatObject, applyMaterialSettings);
    }

    bool ApplyActiveSplatObject()
    {
        return ApplyActiveSplatObject(true);
    }

    void InitializeSplatObject()
    {
        ApplyActiveSplatObject();
    }

    public void NotifySplatObjectEnabled(GaussianSplatObject activeSplatObject)
    {
        if (activeSplatObject == null || !activeSplatObject.gameObject.activeInHierarchy)
        {
            return;
        }

        GameObject activeObject = activeSplatObject.gameObject;
        GameObject[] sceneObjectRoots = cachedSceneSplatObjects;
        if (sceneObjectRoots == null || sceneObjectRoots.Length == 0)
        {
            cachedSceneSplatObjects = new GameObject[] { activeObject };
        }
        else
        {
            int insertIndex = sceneObjectRoots.Length;
            for (int i = 0; i < sceneObjectRoots.Length; i++)
            {
                GameObject currentObject = sceneObjectRoots[i];
                if (currentObject == activeObject)
                {
                    insertIndex = -1;
                    break;
                }

                if (currentObject == null && insertIndex == sceneObjectRoots.Length)
                {
                    insertIndex = i;
                }
            }

            if (insertIndex >= 0)
            {
                if (insertIndex == sceneObjectRoots.Length)
                {
                    GameObject[] updatedSceneObjects = new GameObject[sceneObjectRoots.Length + 1];
                    for (int i = 0; i < sceneObjectRoots.Length; i++)
                    {
                        updatedSceneObjects[i] = sceneObjectRoots[i];
                    }

                    sceneObjectRoots = updatedSceneObjects;
                    cachedSceneSplatObjects = sceneObjectRoots;
                }

                sceneObjectRoots[insertIndex] = activeObject;
            }
        }

        if (IsCombinedRenderingMode())
        {
            ResetCameraPositions();
            return;
        }

        SetCurrentSplatObject(activeSplatObject.gameObject, true);
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

    void EnsureLocalOwnership()
    {
        if (Networking.LocalPlayer != null)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }
    }

    void RequestSyncedStateUpdate()
    {
        if (Networking.LocalPlayer != null)
        {
            RequestSerialization();
        }
    }

    public void SetGaussianScale(float value)
    {
        EnsureLocalOwnership();
        overrideMaterialProperties = true;
        gaussianScale = Mathf.Clamp(value, 0.0f, 2.0f);
        ApplyMaterialSettingsToSelectedObject();
        RequestSyncedStateUpdate();
    }

    public void SetAntiAliasing(float value)
    {
        overrideMaterialProperties = true;
        antiAliasing = Mathf.Clamp(value, 0.0f, 3.0f);
        ApplyMaterialSettingsToSelectedObject();
    }

    public void SetAlphaCutoff(float value)
    {
        overrideMaterialProperties = true;
        alphaCutoff = Mathf.Clamp(value, 0.005f, 0.1f);
        ApplyMaterialSettingsToSelectedObject();
    }

    Material[] GetRendererMaterialsForWrite(MeshRenderer renderer)
    {
        if (renderer == null)
        {
            return new Material[0];
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return renderer.sharedMaterials;
        }
#endif

        return renderer.materials;
    }

    Material[] GetRendererMaterialsForRead(MeshRenderer renderer)
    {
        if (renderer == null)
        {
            return new Material[0];
        }

        Material[] materials = renderer.sharedMaterials;
        return materials ?? new Material[0];
    }

    static Material ResolvePrimarySplatMaterial(Material[] materials)
    {
        if (materials == null)
        {
            return null;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material != null && material.HasProperty("_GS_Positions"))
            {
                return material;
            }
        }

        return null;
    }

    bool UpdateCombinedTextures(Vector3 screenCameraPos, Vector3 photoCameraPos, bool useEditorOps)
    {
        if (combinedSortedRenderer == null || combinedPositions == null || combinedRotations == null || combinedScales == null || combinedColorsCamera == null || combinedColorsScratch == null || combineDataMaterial == null)
        {
#if !UNITY_EDITOR || COMPILER_UDONSHARP
            Debug.LogError("Combined rendering mode is missing generated resources. Refresh the GaussianSplatRenderer in the editor.");
#endif
            return false;
        }

        GaussianSplatObject[] sceneObjects = FindSceneSplatObjects(false);
        if (sceneObjects == null || sceneObjects.Length == 0)
        {
            _combinedActualSplatCount = 0;
            SetCombinedRendererEnabled(false);
            return false;
        }

        MeshRenderer[] sourceRenderers = new MeshRenderer[sceneObjects.Length];
        Material[] sourceMaterials = new Material[sceneObjects.Length];
        int[] sourceCounts = new int[sceneObjects.Length];
        int sourceCount = 0;

        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject sceneObject = sceneObjects[i];
            if (sceneObject == null)
            {
                continue;
            }

            sceneObject.SetSortedRendererEnabled(false);
            MeshRenderer sourceRenderer = GetSortedRenderer(sceneObject.gameObject);
            if (sourceRenderer == null)
            {
                continue;
            }

            Material sourceMaterial = ResolvePrimarySplatMaterial(GetRendererMaterialsForRead(sourceRenderer));
            if (sourceMaterial == null)
            {
                continue;
            }

            Texture positionsTexture = sourceMaterial.GetTexture("_GS_Positions");
            int textureElementCount = positionsTexture != null ? positionsTexture.width * positionsTexture.height : 0;
            int actualSplatCount = sourceMaterial.HasProperty("_ActualSplatCount") ? sourceMaterial.GetInt("_ActualSplatCount") : 0;
            int sourceSplatCount = actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
            if (positionsTexture == null || sourceSplatCount <= 0)
            {
                continue;
            }

            sourceRenderers[sourceCount] = sourceRenderer;
            sourceMaterials[sourceCount] = sourceMaterial;
            sourceCounts[sourceCount] = sourceSplatCount;
            sourceCount++;
        }

        if (sourceCount == 0)
        {
            _combinedActualSplatCount = 0;
            SetCombinedRendererEnabled(false);
            return false;
        }

        int positionCapacity = combinedPositions.width * combinedPositions.height;
        int colorCapacity = combinedColorsScratch.width * combinedColorsScratch.height;
        int combinedCoordShift = 0;
        int combinedBlocksPerRow = Mathf.Max(1, combinedPositions.width >> 2);
        while (combinedBlocksPerRow > 1)
        {
            combinedBlocksPerRow >>= 1;
            combinedCoordShift++;
        }

        combineDataMaterial.SetInt("_CombinedCoordShift", combinedCoordShift);

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Graphics.Blit(Texture2D.blackTexture, combinedPositions);
            Graphics.Blit(Texture2D.blackTexture, combinedRotations);
            Graphics.Blit(Texture2D.blackTexture, combinedScales);
            Graphics.Blit(Texture2D.blackTexture, combinedColorsScratch);
        }
        else
#endif
        {
            VRCGraphics.Blit(Texture2D.blackTexture, combinedPositions);
            VRCGraphics.Blit(Texture2D.blackTexture, combinedRotations);
            VRCGraphics.Blit(Texture2D.blackTexture, combinedScales);
            VRCGraphics.Blit(Texture2D.blackTexture, combinedColorsScratch);
        }

        int combinedOffset = 0;
        for (int batchStart = 0; batchStart < sourceCount; batchStart += COMBINED_SOURCE_BATCH_SIZE)
        {
            combineDataMaterial.SetVector("_CameraPosWorld", Vector3.zero);
            for (int slotIndex = 0; slotIndex < COMBINED_SOURCE_BATCH_SIZE; slotIndex++)
            {
                int sourceIndex = batchStart + slotIndex;
                Material sourceMaterial = sourceIndex < sourceCount ? sourceMaterials[sourceIndex] : null;
                MeshRenderer sourceRenderer = sourceIndex < sourceCount ? sourceRenderers[sourceIndex] : null;
                int sourceSplatCount = sourceIndex < sourceCount ? sourceCounts[sourceIndex] : 0;
                int sourceOffset = combinedOffset;
                if (sourceIndex < sourceCount)
                {
                    if (combinedOffset + sourceSplatCount > positionCapacity || combinedOffset + sourceSplatCount > colorCapacity)
                    {
                        _combinedActualSplatCount = 0;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
                        if (!Application.isPlaying)
                        {
                            SetCombinedRendererEnabled(false);
                            return false;
                        }
#endif
                        Debug.LogError("Combined Gaussian splat resources are too small for the active scene splats. Refresh the renderer resources in the editor.");
                        SetCombinedRendererEnabled(false);
                        return false;
                    }

                    combinedOffset += sourceSplatCount;
                }

                string slotSuffix = slotIndex.ToString();
                combineDataMaterial.SetTexture("_GS_SourcePositions" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Positions") : null);
                combineDataMaterial.SetTexture("_GS_SourceColors" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Colors") : null);
                combineDataMaterial.SetTexture("_GS_SourceRotations" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Rotations") : null);
                combineDataMaterial.SetTexture("_GS_SourceScales" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Scales") : null);
                combineDataMaterial.SetTexture("_GS_SourceSH" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_SH") : null);
                combineDataMaterial.SetVector("_GS_SourceLayout" + slotSuffix, new Vector4(
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_Positions_CoordMask") ? sourceMaterial.GetInt("_GS_Positions_CoordMask") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_Positions_CoordShift") ? sourceMaterial.GetInt("_GS_Positions_CoordShift") : 0,
                    sourceOffset,
                    sourceSplatCount));
                combineDataMaterial.SetVector("_GS_SourceShLayout" + slotSuffix, new Vector4(
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoeffCount") ? sourceMaterial.GetInt("_GS_SH_CoeffCount") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoeffStride") ? sourceMaterial.GetInt("_GS_SH_CoeffStride") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoordMask") ? sourceMaterial.GetInt("_GS_SH_CoordMask") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoordShift") ? sourceMaterial.GetInt("_GS_SH_CoordShift") : 0));
                combineDataMaterial.SetVector("_GS_SourceDecode" + slotSuffix, new Vector4(
                    sourceMaterial != null && sourceMaterial.HasProperty("_Log2MinScale") ? sourceMaterial.GetFloat("_Log2MinScale") : -15.0f,
                    sourceMaterial != null && sourceMaterial.HasProperty("_Opacity") ? sourceMaterial.GetFloat("_Opacity") : 1.0f,
                    sourceMaterial != null && sourceMaterial.HasProperty("_SHBand") ? sourceMaterial.GetFloat("_SHBand") : 0.0f,
                    0.0f));
                combineDataMaterial.SetVector("_GS_SourceShMin" + slotSuffix, sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_Min") ? sourceMaterial.GetVector("_GS_SH_Min") : Vector4.zero);
                combineDataMaterial.SetVector("_GS_SourceShRange" + slotSuffix, sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_Range") ? sourceMaterial.GetVector("_GS_SH_Range") : Vector4.one);
                combineDataMaterial.SetMatrix("_GS_SourceLocalToWorld" + slotSuffix, sourceRenderer != null ? sourceRenderer.transform.localToWorldMatrix : Matrix4x4.identity);
                combineDataMaterial.SetMatrix("_GS_SourceWorldToLocal" + slotSuffix, sourceRenderer != null ? sourceRenderer.transform.worldToLocalMatrix : Matrix4x4.identity);
            }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                Graphics.Blit(null, combinedPositions, combineDataMaterial, 0);
                Graphics.Blit(null, combinedRotations, combineDataMaterial, 1);
                Graphics.Blit(null, combinedScales, combineDataMaterial, 2);
            }
            else
#endif
            {
                VRCGraphics.Blit(null, combinedPositions, combineDataMaterial, 0);
                VRCGraphics.Blit(null, combinedRotations, combineDataMaterial, 1);
                VRCGraphics.Blit(null, combinedScales, combineDataMaterial, 2);
            }

            combineDataMaterial.SetVector("_CameraPosWorld", screenCameraPos);
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                Graphics.Blit(null, combinedColorsScratch, combineDataMaterial, 3);
            }
            else
#endif
            {
                VRCGraphics.Blit(null, combinedColorsScratch, combineDataMaterial, 3);
            }
        }

        if (combinedOffset == 0)
        {
            _combinedActualSplatCount = 0;
            SetCombinedRendererEnabled(false);
            return false;
        }

        _combinedActualSplatCount = combinedOffset;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Graphics.CopyTexture(combinedColorsScratch, 0, 0, combinedColorsCamera, 0, 0);
            Graphics.CopyTexture(combinedColorsScratch, 0, 0, combinedColorsCamera, 1, 0);
            SetCombinedRendererEnabled(true);
            return true;
        }
#endif

        VRCGraphics.Blit(combinedColorsScratch, combinedColorsCamera, 0, SCREEN_CAMERA_ID);
        VRCGraphics.Blit(Texture2D.blackTexture, combinedColorsScratch);

        combinedOffset = 0;
        for (int batchStart = 0; batchStart < sourceCount; batchStart += COMBINED_SOURCE_BATCH_SIZE)
        {
            combineDataMaterial.SetVector("_CameraPosWorld", photoCameraPos);
            for (int slotIndex = 0; slotIndex < COMBINED_SOURCE_BATCH_SIZE; slotIndex++)
            {
                int sourceIndex = batchStart + slotIndex;
                Material sourceMaterial = sourceIndex < sourceCount ? sourceMaterials[sourceIndex] : null;
                MeshRenderer sourceRenderer = sourceIndex < sourceCount ? sourceRenderers[sourceIndex] : null;
                int sourceSplatCount = sourceIndex < sourceCount ? sourceCounts[sourceIndex] : 0;
                int sourceOffset = combinedOffset;
                if (sourceIndex < sourceCount)
                {
                    combinedOffset += sourceSplatCount;
                }

                string slotSuffix = slotIndex.ToString();
                combineDataMaterial.SetTexture("_GS_SourcePositions" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Positions") : null);
                combineDataMaterial.SetTexture("_GS_SourceColors" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Colors") : null);
                combineDataMaterial.SetTexture("_GS_SourceRotations" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Rotations") : null);
                combineDataMaterial.SetTexture("_GS_SourceScales" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_Scales") : null);
                combineDataMaterial.SetTexture("_GS_SourceSH" + slotSuffix, sourceMaterial != null ? sourceMaterial.GetTexture("_GS_SH") : null);
                combineDataMaterial.SetVector("_GS_SourceLayout" + slotSuffix, new Vector4(
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_Positions_CoordMask") ? sourceMaterial.GetInt("_GS_Positions_CoordMask") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_Positions_CoordShift") ? sourceMaterial.GetInt("_GS_Positions_CoordShift") : 0,
                    sourceOffset,
                    sourceSplatCount));
                combineDataMaterial.SetVector("_GS_SourceShLayout" + slotSuffix, new Vector4(
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoeffCount") ? sourceMaterial.GetInt("_GS_SH_CoeffCount") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoeffStride") ? sourceMaterial.GetInt("_GS_SH_CoeffStride") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoordMask") ? sourceMaterial.GetInt("_GS_SH_CoordMask") : 0,
                    sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_CoordShift") ? sourceMaterial.GetInt("_GS_SH_CoordShift") : 0));
                combineDataMaterial.SetVector("_GS_SourceDecode" + slotSuffix, new Vector4(
                    sourceMaterial != null && sourceMaterial.HasProperty("_Log2MinScale") ? sourceMaterial.GetFloat("_Log2MinScale") : -15.0f,
                    sourceMaterial != null && sourceMaterial.HasProperty("_Opacity") ? sourceMaterial.GetFloat("_Opacity") : 1.0f,
                    sourceMaterial != null && sourceMaterial.HasProperty("_SHBand") ? sourceMaterial.GetFloat("_SHBand") : 0.0f,
                    0.0f));
                combineDataMaterial.SetVector("_GS_SourceShMin" + slotSuffix, sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_Min") ? sourceMaterial.GetVector("_GS_SH_Min") : Vector4.zero);
                combineDataMaterial.SetVector("_GS_SourceShRange" + slotSuffix, sourceMaterial != null && sourceMaterial.HasProperty("_GS_SH_Range") ? sourceMaterial.GetVector("_GS_SH_Range") : Vector4.one);
                combineDataMaterial.SetMatrix("_GS_SourceLocalToWorld" + slotSuffix, sourceRenderer != null ? sourceRenderer.transform.localToWorldMatrix : Matrix4x4.identity);
                combineDataMaterial.SetMatrix("_GS_SourceWorldToLocal" + slotSuffix, sourceRenderer != null ? sourceRenderer.transform.worldToLocalMatrix : Matrix4x4.identity);
            }

            VRCGraphics.Blit(null, combinedColorsScratch, combineDataMaterial, 3);
        }

        VRCGraphics.Blit(combinedColorsScratch, combinedColorsCamera, 0, PHOTO_CAMERA_ID);
        SetCombinedRendererEnabled(true);
        return true;
    }

    public bool IsCombinedRenderingMode()
    {
        return renderingMode == GaussianSplatRenderingMode.CombineAllSplats;
    }

    void ApplyConfiguredMaterialSettings(Material material, int currentSHBand)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_SHBand")) material.SetFloat("_SHBand", Mathf.Clamp(currentSHBand, 0, 3));
        if (useVrcLightVolumes) material.EnableKeyword("_VRC_LIGHT_VOLUMES_ON");
        else material.DisableKeyword("_VRC_LIGHT_VOLUMES_ON");
        if (material.HasProperty("_LightVolumeIntensity")) material.SetFloat("_LightVolumeIntensity", lightVolumeIntensity);

        if (!overrideMaterialProperties)
        {
            return;
        }

        if (material.HasProperty("_GaussianMul")) material.SetFloat("_GaussianMul", gaussianScale);
        if (material.HasProperty("_ThinThreshold")) material.SetFloat("_ThinThreshold", thinThreshold);
        if (material.HasProperty("_AntiAliasing")) material.SetFloat("_AntiAliasing", antiAliasing);
        if (material.HasProperty("_Log2MinScale")) material.SetFloat("_Log2MinScale", log2MinScale);
        if (material.HasProperty("_AlphaCutoff")) material.SetFloat("_AlphaCutoff", alphaCutoff);
        if (material.HasProperty("_ScaleCutoff")) material.SetFloat("_ScaleCutoff", scaleCutoff);
        if (material.HasProperty("_Exposure")) material.SetFloat("_Exposure", exposure);
        if (material.HasProperty("_Opacity")) material.SetFloat("_Opacity", opacity);
        if (material.HasProperty("_OKLCHShift")) material.SetVector("_OKLCHShift", new Vector4(oklchShift.x, oklchShift.y, oklchShift.z, 0.0f));
        if (material.HasProperty("_Gamma")) material.SetFloat("_Gamma", Mathf.Max(0.001f, gamma));
    }

    int GetSplatObjectMaxSHBand(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return 0;
        }

        GaussianSplatObject gaussianSplatObject = rootObject.GetComponent<GaussianSplatObject>();
        if (gaussianSplatObject != null)
        {
            return gaussianSplatObject.GetMaxSHBand();
        }

        MeshRenderer renderer = GetSortedRenderer(rootObject);
        if (renderer == null)
        {
            return 0;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
        {
            return 0;
        }

        int inferredMax = 0;
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
            {
                continue;
            }

            if (!material.HasProperty("_GS_SH") || material.GetTexture("_GS_SH") == null || !material.HasProperty("_GS_SH_CoeffCount"))
            {
                continue;
            }

            int coeffCount = material.GetInt("_GS_SH_CoeffCount");
            if (coeffCount >= 15)
            {
                inferredMax = Mathf.Max(inferredMax, 3);
                continue;
            }

            if (coeffCount >= 8)
            {
                inferredMax = Mathf.Max(inferredMax, 2);
                continue;
            }

            if (coeffCount >= 3)
            {
                inferredMax = Mathf.Max(inferredMax, 1);
            }
        }

        return inferredMax;
    }

    public int GetSelectedSplatMaxSHBand()
    {
        if (IsCombinedRenderingMode())
        {
            return 0;
        }

        if ((splatObject == null || !splatObject.activeInHierarchy) && !ApplyActiveSplatObject())
        {
            return 0;
        }

        return GetSplatObjectMaxSHBand(splatObject);
    }

    public int GetCurrentSHBand()
    {
        return Mathf.Clamp(requestedSHBand, 0, GetSelectedSplatMaxSHBand());
    }

    public void SetSHBand(int value)
    {
        EnsureLocalOwnership();
        requestedSHBand = Mathf.Clamp(value, 0, 3);
        ApplyMaterialSettingsToSelectedObject();
        RequestSyncedStateUpdate();
    }

    void ApplyMaterialSettingsToSelectedObject()
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (!Application.isPlaying)
        {
            return;
        }
#endif

        if (!IsCombinedRenderingMode() && splatObject == null)
        {
            return;
        }

        int currentSHBand = IsCombinedRenderingMode() ? 0 : GetCurrentSHBand();
        if (IsCombinedRenderingMode())
        {
            if (combinedSortedRenderer == null)
            {
                return;
            }

            Material[] combinedMats = GetRendererMaterialsForWrite(combinedSortedRenderer);
            for (int i = 0; i < combinedMats.Length; i++)
            {
                ApplyConfiguredMaterialSettings(combinedMats[i], currentSHBand);
            }

            Transform combinedTransform = combinedSortedRenderer.transform;
            for (int i = 0; i < combinedTransform.childCount; i++)
            {
                MeshRenderer childRenderer = combinedTransform.GetChild(i).GetComponent<MeshRenderer>();
                if (childRenderer == null)
                {
                    continue;
                }

                Material[] childMats = GetRendererMaterialsForWrite(childRenderer);
                for (int materialIndex = 0; materialIndex < childMats.Length; materialIndex++)
                {
                    ApplyConfiguredMaterialSettings(childMats[materialIndex], currentSHBand);
                }
            }

            return;
        }

        MeshRenderer renderer = GetSortedRenderer(splatObject);
        if (renderer == null)
        {
            return;
        }

        Material[] splatMats = GetRendererMaterialsForWrite(renderer);
        for (int i = 0; i < splatMats.Length; i++)
        {
            Material splatMat = splatMats[i];
            ApplyConfiguredMaterialSettings(splatMat, currentSHBand);
        }
    }

    public float GetCameraPositionQuantization()
    {
        return cameraPositionQuantization;
    }

    public void SetCameraPositionQuantization(float value)
    {
        cameraPositionQuantization = Mathf.Max(0.0f, value);
        ResetCameraPositions();
    }

    public int GetSortPipelineFrames()
    {
        return sortPipelineFrames;
    }

    public void SetSortPipelineFrames(int value)
    {
        sortPipelineFrames = Mathf.Clamp(value, 1, 8);
        ResetCameraPositions();
    }

    public int GetSortingSteps()
    {
        return GetSortPipelineFrames();
    }

    public void SetSortingSteps(int value)
    {
        SetSortPipelineFrames(value);
    }

    public bool GetAlwaysUpdate()
    {
        return ShouldAlwaysUpdate();
    }

    public void SetAlwaysUpdate(bool value)
    {
        alwaysUpdate = value;
        ResetCameraPositions();
    }

    public void ToggleAlwaysUpdate()
    {
        SetAlwaysUpdate(!alwaysUpdate);
    }

    public bool GetUseVrcLightVolumes()
    {
        return useVrcLightVolumes;
    }

    public void SetUseVrcLightVolumes(bool value)
    {
        EnsureLocalOwnership();
        useVrcLightVolumes = value;
        ApplyMaterialSettingsToSelectedObject();
        RequestSyncedStateUpdate();
    }

    public void ToggleVrcLightVolumes()
    {
        SetUseVrcLightVolumes(!useVrcLightVolumes);
    }

    public float GetAntiAliasing()
    {
        return antiAliasing;
    }

    public float GetLightVolumeIntensity()
    {
        return lightVolumeIntensity;
    }

    public void SetLightVolumeIntensity(float value)
    {
        overrideMaterialProperties = true;
        lightVolumeIntensity = Mathf.Clamp(value, 0.0f, 4.0f);
        ApplyMaterialSettingsToSelectedObject();
    }

    public string GetCurrentSplatName()
    {
        if (IsCombinedRenderingMode())
        {
            return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject.name : "Combined";
        }

        if ((splatObject == null || !splatObject.activeInHierarchy) && !ApplyActiveSplatObject(false))
        {
            return "None";
        }

        return splatObject.name;
    }

    public GameObject GetCurrentSplatObject()
    {
        if (IsCombinedRenderingMode())
        {
            return combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : null;
        }

        if ((splatObject == null || !splatObject.activeInHierarchy) && !ApplyActiveSplatObject(false))
        {
            return null;
        }

        return splatObject;
    }

    void DisableMsaaInGame()
    {
        if (VRCCameraSettings.ScreenCamera != null)
        {
            VRCCameraSettings.ScreenCamera.AllowMSAA = false;
        }
        // Commented out to prevent the VRChat Photo Camera from crashing!
       // if (VRCCameraSettings.PhotoCamera != null)
       // {
       //     VRCCameraSettings.PhotoCamera.AllowMSAA = false;
       // }
    }

    bool IsPrimaryRendererInstance()
    {
#if COMPILER_UDONSHARP
        return true;
#else
        GaussianSplatRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<GaussianSplatRenderer>();
        if (renderers == null || renderers.Length <= 1)
        {
            return true;
        }

        GaussianSplatRenderer primaryRenderer = renderers[0];
        int primaryInstanceId = primaryRenderer.GetInstanceID();
        for (int i = 1; i < renderers.Length; i++)
        {
            GaussianSplatRenderer candidate = renderers[i];
            if (candidate == null)
            {
                continue;
            }

            int candidateInstanceId = candidate.GetInstanceID();
            if (candidateInstanceId < primaryInstanceId)
            {
                primaryRenderer = candidate;
                primaryInstanceId = candidateInstanceId;
            }
        }

        if (primaryRenderer != this)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Only one renderer can be active in a scene.");
            return false;
        }

        Debug.LogError("Multiple GaussianSplatRenderer instances found. This renderer will be used; disable or remove the duplicates.");
        return true;
#endif
    }

    void Start()
    {
        if (!EnsureRendererInitialized())
        {
            return;
        }

        DisableMsaaInGame();
    }

    bool EnsureRenderTextureCreated(RenderTexture renderTexture)
    {
        if (renderTexture == null || renderTexture.IsCreated())
        {
            return renderTexture != null;
        }

        renderTexture.Create();
        return renderTexture.IsCreated();
    }

    bool EnsureRendererInitialized()
    {
        if (!IsPrimaryRendererInstance())
        {
            enabled = false;
            return false;
        }

        if (_radixSort == null)
        {
            _radixSort = (RadixSort)GetComponent<RadixSort>();
        }

        if (_radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return false;
        }

        if (splatRenderOrder == null)
        {
            Debug.LogError("Splat Render Order texture is not assigned. Please assign a RenderTexture.");
            return false;
        }

        if (!EnsureRenderTextureCreated(splatRenderOrder))
        {
            Debug.LogError("Splat Render Order texture could not be created at runtime.");
            return false;
        }

        if (!EnsureRenderTextureCreated(_radixSort.keyValues0)
            || !EnsureRenderTextureCreated(_radixSort.keyValues1)
            || !EnsureRenderTextureCreated(_radixSort.prefixSums))
        {
            Debug.LogError("Radix sort render textures could not be created at runtime.");
            return false;
        }

        if ((combinedPositions != null && !EnsureRenderTextureCreated(combinedPositions))
            || (combinedRotations != null && !EnsureRenderTextureCreated(combinedRotations))
            || (combinedScales != null && !EnsureRenderTextureCreated(combinedScales))
            || (combinedColorsCamera != null && !EnsureRenderTextureCreated(combinedColorsCamera))
            || (combinedColorsScratch != null && !EnsureRenderTextureCreated(combinedColorsScratch)))
        {
            Debug.LogError("Combined render textures could not be created at runtime.");
            return false;
        }

        bool initializedCameraState = _completedCameraPos != null && _completedCameraPos.Length >= MAX_CAMERA_COUNT
            && _pendingCameraPos != null && _pendingCameraPos.Length >= MAX_CAMERA_COUNT
            && _pendingCameraWorldPos != null && _pendingCameraWorldPos.Length >= MAX_CAMERA_COUNT
            && _hasCompletedSort != null && _hasCompletedSort.Length >= MAX_CAMERA_COUNT
            && _hasPendingSort != null && _hasPendingSort.Length >= MAX_CAMERA_COUNT;
        if (!initializedCameraState)
        {
            _completedCameraPos = new Vector3[MAX_CAMERA_COUNT];
            _pendingCameraPos = new Vector3[MAX_CAMERA_COUNT];
            _pendingCameraWorldPos = new Vector3[MAX_CAMERA_COUNT];
            _hasCompletedSort = new bool[MAX_CAMERA_COUNT];
            _hasPendingSort = new bool[MAX_CAMERA_COUNT];
            ResetCameraPositions();
            InitializeSplatObject();
        }

        return true;
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
        if (!IsCombinedRenderingMode() && !ApplyActiveSplatObject(false))
        {
            return false;
        }

        Material positionsMaterial = null;
        int currentSHBand = IsCombinedRenderingMode() ? 0 : GetCurrentSHBand();
        if (IsCombinedRenderingMode())
        {
            if (combinedSortedRenderer == null)
            {
                Debug.LogError("No combined MeshRenderer found on the GaussianSplatRenderer.");
                return false;
            }

            Material[] combinedMats = GetRendererMaterialsForWrite(combinedSortedRenderer);
            for (int i = 0; i < combinedMats.Length; i++)
            {
                ApplyConfiguredMaterialSettings(combinedMats[i], currentSHBand);
            }

            _sortedRenderer = null;
            Transform combinedTransform = combinedSortedRenderer.transform;
            for (int i = 0; i < combinedTransform.childCount; i++)
            {
                MeshRenderer childRenderer = combinedTransform.GetChild(i).GetComponent<MeshRenderer>();
                if (childRenderer == null)
                {
                    continue;
                }

                Material[] childMats = GetRendererMaterialsForWrite(childRenderer);
                Material childPositionsMaterial = ResolvePrimarySplatMaterial(childMats);
                if (childPositionsMaterial == null)
                {
                    childRenderer.enabled = false;
                    continue;
                }

                int splatOffset = childPositionsMaterial.HasProperty("_SplatOffset") ? childPositionsMaterial.GetInt("_SplatOffset") : 0;
                bool shouldRender = _combinedActualSplatCount > splatOffset;
#if UNITY_EDITOR && !COMPILER_UDONSHARP
                if (!Application.isPlaying)
                {
                    if (shouldRender && !childRenderer.gameObject.activeSelf)
                    {
                        childRenderer.gameObject.SetActive(true);
                    }

                    childRenderer.enabled = shouldRender;
                }
                else
#endif
                {
                    childRenderer.gameObject.SetActive(shouldRender);
                    childRenderer.enabled = shouldRender;
                }

                for (int materialIndex = 0; materialIndex < childMats.Length; materialIndex++)
                {
                    Material splatMat = childMats[materialIndex];
                    if (splatMat == null)
                    {
                        continue;
                    }

                    if (splatMat.HasProperty("_GS_RenderOrder"))
                    {
                        splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
                    }

                    if (splatMat.HasProperty("_ActualSplatCount"))
                    {
                        splatMat.SetInt("_ActualSplatCount", _combinedActualSplatCount);
                    }

                    ApplyConfiguredMaterialSettings(splatMat, currentSHBand);
                }

                if (!shouldRender)
                {
                    continue;
                }

                if (_sortedRenderer == null)
                {
                    _sortedRenderer = childRenderer;
                    positionsMaterial = childPositionsMaterial;
                }
            }

            if (_sortedRenderer == null)
            {
                Debug.LogError("No active combined chunk MeshRenderer found on the GaussianSplatRenderer.");
                return false;
            }
        }
        else
        {
            _sortedRenderer = GetSortedRenderer(splatObject);
            if (_sortedRenderer == null)
            {
                Debug.LogError($"No sorted MeshRenderer found on {splatObject.name}.");
                return false;
            }

            Material[] splatMats = GetRendererMaterialsForWrite(_sortedRenderer);
            for (int i = 0; i < splatMats.Length; i++)
            {
                Material splatMat = splatMats[i];
                splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
                ApplyConfiguredMaterialSettings(splatMat, currentSHBand);
            }

            positionsMaterial = ResolvePrimarySplatMaterial(splatMats);
        }

        Texture positions = null;
        if (positionsMaterial != null)
        {
            positions = positionsMaterial.GetTexture("_GS_Positions");
        }

        if (positions == null)
        {
            Debug.LogError(IsCombinedRenderingMode()
                ? "No combined _GS_Positions texture found on the GaussianSplatRenderer chunks."
                : $"No _GS_Positions texture found on {splatObject.name}.");
            return false;
        }

        int textureElementCount = positions.width * positions.height;
        int actualSplatCount = IsCombinedRenderingMode()
            ? _combinedActualSplatCount
            : (positionsMaterial != null && positionsMaterial.HasProperty("_ActualSplatCount")
                ? positionsMaterial.GetInt("_ActualSplatCount")
                : 0);

        if (_radixSort == null)
        {
            _radixSort = (RadixSort)GetComponent<RadixSort>();
        }

        if (_radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return false;
        }

        _radixSort.elementCount = actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
        keyValueMat = _radixSort.computeKeyValues;
        if (keyValueMat == null)
        {
            Debug.LogError("ComputeKeyValues material is not assigned on the RadixSort component.");
            return false;
        }

        keyValueMat.SetTexture("_GS_Positions", positions);
        keyValueMat.SetInt("_GS_Positions_CoordMask", positionsMaterial.GetInt("_GS_Positions_CoordMask"));
        keyValueMat.SetInt("_GS_Positions_CoordShift", positionsMaterial.GetInt("_GS_Positions_CoordShift"));
        return true;
    }

    int GetSortSubpassBudget()
    {
        return Mathf.CeilToInt((float)RadixSort.TotalSortPasses / Mathf.Clamp(sortPipelineFrames, 1, RadixSort.TotalSortPasses));
    }

    void BeginSort(bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            _radixSort.BeginSortForEditor();
            return;
        }
#endif

        _radixSort.BeginSort();
    }

    void StepSort(int maxSubpasses, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            _radixSort.StepSortForEditor(maxSubpasses);
            return;
        }
#endif

        _radixSort.StepSort(maxSubpasses);
    }

    void CopySortedOrder(int slice, bool useEditorOps)
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            _radixSort.CopySortedOrderForEditor(splatRenderOrder, slice);
            return;
        }
#endif

        _radixSort.CopySortedOrder(splatRenderOrder, slice);
    }

    bool ShouldAlwaysUpdate()
    {
        return IsCombinedRenderingMode() || alwaysUpdate;
    }

    void RequestCameraSort(Vector3 cameraPos, int cameraID, bool forceUpdate)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        if (!forceUpdate && !ShouldAlwaysUpdate() && _hasCompletedSort[cameraID] && quantizedPos == _completedCameraPos[cameraID])
        {
            return;
        }

        _pendingCameraPos[cameraID] = quantizedPos;
        _pendingCameraWorldPos[cameraID] = cameraPos;
        _hasPendingSort[cameraID] = true;
    }

    bool TryStartPendingSort(int cameraID, bool useEditorOps)
    {
        if (!_hasPendingSort[cameraID])
        {
            return false;
        }

        if (!ShouldAlwaysUpdate() && _hasCompletedSort[cameraID] && _pendingCameraPos[cameraID] == _completedCameraPos[cameraID])
        {
            _hasPendingSort[cameraID] = false;
            return false;
        }

        keyValueMat.SetVector("_CameraPos", _sortedRenderer.transform.InverseTransformPoint(_pendingCameraWorldPos[cameraID]));
        BeginSort(useEditorOps);
        _activeSortCameraId = cameraID;
        _activeSortQuantizedPos = _pendingCameraPos[cameraID];
        _hasPendingSort[cameraID] = false;
        return true;
    }

    void StartNextPendingSort(bool useEditorOps)
    {
        if (_activeSortCameraId != NO_ACTIVE_SORT)
        {
            return;
        }

        if (TryStartPendingSort(SCREEN_CAMERA_ID, useEditorOps))
        {
            return;
        }

        TryStartPendingSort(PHOTO_CAMERA_ID, useEditorOps);
    }

    void PublishActiveSort(bool useEditorOps)
    {
        if (_activeSortCameraId == NO_ACTIVE_SORT)
        {
            return;
        }

        CopySortedOrder(_activeSortCameraId, useEditorOps);
        _completedCameraPos[_activeSortCameraId] = _activeSortQuantizedPos;
        _hasCompletedSort[_activeSortCameraId] = true;

        if (_activeSortCameraId == SCREEN_CAMERA_ID)
        {
            if (IsCombinedRenderingMode())
            {
                SetCombinedRendererEnabled(true);
            }
            else
            {
                ShowSorted(splatObject);
            }
        }

        _activeSortCameraId = NO_ACTIVE_SORT;
        _activeSortQuantizedPos = Vector3.positiveInfinity;
    }

    void ProcessSortPipeline(bool useEditorOps)
    {
        StartNextPendingSort(useEditorOps);
        if (_activeSortCameraId == NO_ACTIVE_SORT)
        {
            return;
        }

        StepSort(GetSortSubpassBudget(), useEditorOps);
        if (_radixSort.IsSortComplete())
        {
            PublishActiveSort(useEditorOps);
            StartNextPendingSort(useEditorOps);
        }
    }

    void RunBlockingSort(Vector3 cameraPos, int cameraID, bool useEditorOps)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        keyValueMat.SetVector("_CameraPos", _sortedRenderer.transform.InverseTransformPoint(cameraPos));
        BeginSort(useEditorOps);
        _activeSortCameraId = cameraID;
        _activeSortQuantizedPos = quantizedPos;
        StepSort(RadixSort.TotalSortPasses, useEditorOps);
        PublishActiveSort(useEditorOps);
    }

    void SortCameraViews(Vector3 screenCamPos, Vector3 photoCamPos, bool sortPhotoCamera, bool useEditorOps)
    {
        if (!EnsureRendererInitialized())
        {
            return;
        }

        if (!IsCombinedRenderingMode() && !ApplyActiveSplatObject(false))
        {
            return;
        }

        if (IsCombinedRenderingMode() && !UpdateCombinedTextures(screenCamPos, photoCamPos, useEditorOps))
        {
            return;
        }

        if (!UpdateMaterials())
        {
            return;
        }

        if (!_hasCompletedSort[SCREEN_CAMERA_ID])
        {
            RunBlockingSort(screenCamPos, SCREEN_CAMERA_ID, useEditorOps);
        }
        else
        {
            RequestCameraSort(screenCamPos, SCREEN_CAMERA_ID, false);
        }

        if (sortPhotoCamera)
        {
            Vector3 quantizedPhotoPos = QuantizePosition(photoCamPos);
            if (!_hasCompletedSort[PHOTO_CAMERA_ID]
                || ShouldAlwaysUpdate()
                || quantizedPhotoPos != _completedCameraPos[PHOTO_CAMERA_ID])
            {
                _hasPendingSort[PHOTO_CAMERA_ID] = false;
                RunBlockingSort(photoCamPos, PHOTO_CAMERA_ID, useEditorOps);
            }
        }

        ProcessSortPipeline(useEditorOps);

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

    public void SortCameras(Vector3 screenCamPos)
    {
        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        bool sortPhotoCamera = photoCam != null && photoCam.Active;
        Vector3 photoCamPos = sortPhotoCamera ? photoCam.Position : screenCamPos;

        SortCameraViews(screenCamPos, photoCamPos, sortPhotoCamera, false);
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    void SortEditorCamera(Camera camera)
    {
        if (camera == null || camera.cameraType != CameraType.SceneView || Application.isPlaying)
        {
            return;
        }

        Vector3 cameraPos = camera.transform.position;
        if (!EnsureRendererInitialized())
        {
            return;
        }

        if (!IsCombinedRenderingMode() && !ApplyActiveSplatObject(false))
        {
            return;
        }

        if (IsCombinedRenderingMode() && !UpdateCombinedTextures(cameraPos, cameraPos, true))
        {
            return;
        }

        if (!UpdateMaterials())
        {
            return;
        }

        RunBlockingSort(cameraPos, SCREEN_CAMERA_ID, true);
    }
#endif

    void Update()
    {
        DisableMsaaInGame();

        if (!IsCombinedRenderingMode() && !ApplyActiveSplatObject(false))
        {
            return;
        }

        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        SortCameras(screenCamPos);
    }

    public override void OnDeserialization()
    {
        ResetCameraPositions();
        if (!IsCombinedRenderingMode() && !ApplyActiveSplatObject())
        {
            return;
        }

        ApplyMaterialSettingsToSelectedObject();
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static bool IsSceneSplatObject(GaussianSplatObject splatObject, UnityEngine.SceneManagement.Scene scene)
    {
        if (splatObject == null)
        {
            return false;
        }

        GameObject rootObject = splatObject.transform.root != null ? splatObject.transform.root.gameObject : splatObject.gameObject;
        if (rootObject == null || EditorUtility.IsPersistent(rootObject))
        {
            return false;
        }

        if ((splatObject.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
        {
            return false;
        }

        if (!ShouldUseEditorPreviewScene(rootObject.scene))
        {
            return false;
        }

        if (scene.IsValid() && rootObject.scene != scene)
        {
            return false;
        }

        return true;
    }

    [InitializeOnLoadMethod]
    static void RegisterSceneAutomation()
    {
        Camera.onPreCull -= OnEditorCameraPreCull;
        Camera.onPreCull += OnEditorCameraPreCull;
        EditorApplication.hierarchyChanged -= OnEditorHierarchyChanged;
        EditorApplication.hierarchyChanged += OnEditorHierarchyChanged;
        EditorApplication.delayCall -= OnEditorHierarchyChanged;
        EditorApplication.delayCall += OnEditorHierarchyChanged;
    }

    static bool ShouldUseEditorPreviewScene(UnityEngine.SceneManagement.Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
        {
            return false;
        }

        if (!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene))
        {
            return true;
        }

        UnityEditor.SceneManagement.PrefabStage prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
        return prefabStage != null && prefabStage.scene == scene;
    }

    static MaterialPropertyBlock GetEditorPreviewPropertyBlock()
    {
        if (_editorPreviewPropertyBlock == null)
        {
            _editorPreviewPropertyBlock = new MaterialPropertyBlock();
        }

        return _editorPreviewPropertyBlock;
    }

    static RenderTexture CreateTemporarySortRenderTexture(string textureName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        RenderTexture renderTexture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
        renderTexture.name = textureName;
        renderTexture.hideFlags = HideFlags.HideAndDontSave;
        renderTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        renderTexture.volumeDepth = volumeDepth;
        renderTexture.useMipMap = useMipMap;
        renderTexture.autoGenerateMips = false;
        renderTexture.wrapMode = TextureWrapMode.Clamp;
        renderTexture.filterMode = FilterMode.Point;
        renderTexture.enableRandomWrite = false;
        renderTexture.anisoLevel = 0;
        renderTexture.antiAliasing = 1;
        renderTexture.Create();
        return renderTexture;
    }

    static void ReleaseTemporarySortRenderTexture(ref RenderTexture renderTexture)
    {
        if (renderTexture == null)
        {
            return;
        }

        renderTexture.Release();
        DestroyImmediate(renderTexture);
        renderTexture = null;
    }

    static void EnsureTemporarySortRenderTexture(ref RenderTexture targetTexture, string textureName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        bool needsRecreate = targetTexture == null
            || targetTexture.width < width
            || targetTexture.height < height
            || targetTexture.format != format
            || targetTexture.dimension != (volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D)
            || targetTexture.volumeDepth != volumeDepth
            || targetTexture.useMipMap != useMipMap
            || targetTexture.autoGenerateMips
            || targetTexture.wrapMode != TextureWrapMode.Clamp
            || targetTexture.filterMode != FilterMode.Point
            || targetTexture.enableRandomWrite
            || targetTexture.anisoLevel != 0
            || targetTexture.antiAliasing != 1
            || !targetTexture.IsCreated();
        if (!needsRecreate)
        {
            return;
        }

        ReleaseTemporarySortRenderTexture(ref targetTexture);
        targetTexture = CreateTemporarySortRenderTexture(textureName, width, height, format, useMipMap, volumeDepth);
    }

    static Material CreateEditorPreviewMaterial(string assetPath, string materialName)
    {
        Material sourceMaterial = LoadPackageMaterial(assetPath);
        if (sourceMaterial == null)
        {
            Debug.LogError($"Missing Gaussian splat editor preview material at '{assetPath}'.");
            return null;
        }

        Material previewMaterial = new Material(sourceMaterial);
        previewMaterial.name = materialName;
        previewMaterial.hideFlags = HideFlags.HideAndDontSave;
        return previewMaterial;
    }

    static bool EnsureEditorPreviewSorter(int requiredWidth, int requiredHeight)
    {
        if (_editorPreviewSorterObject == null)
        {
            _editorPreviewSorterObject = new GameObject("GaussianSplatEditorPreviewSorter");
            _editorPreviewSorterObject.hideFlags = HideFlags.HideAndDontSave;
            _editorPreviewRadixSort = _editorPreviewSorterObject.AddComponent<RadixSort>();
            _editorPreviewRadixSort.hideFlags = HideFlags.HideAndDontSave;
            _editorPreviewRadixSort.computeKeyValues = CreateEditorPreviewMaterial("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ComputeKeyValue.mat", "GaussianSplatEditorPreviewComputeKeyValue");
            _editorPreviewRadixSort.radixSort = CreateEditorPreviewMaterial("Assets/VRChatGaussianSplatting/RadixSort/Materials/Misha_RadixSort.mat", "GaussianSplatEditorPreviewRadixSort");
        }

        if (_editorPreviewRadixSort == null || _editorPreviewRadixSort.computeKeyValues == null || _editorPreviewRadixSort.radixSort == null)
        {
            return false;
        }

        EnsureTemporarySortRenderTexture(ref _editorPreviewRadixSort.keyValues0, "GaussianSplatEditorPreview_KeyValues0", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        EnsureTemporarySortRenderTexture(ref _editorPreviewRadixSort.keyValues1, "GaussianSplatEditorPreview_KeyValues1", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        EnsureTemporarySortRenderTexture(ref _editorPreviewRadixSort.prefixSums, "GaussianSplatEditorPreview_PrefixSums", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, true, 1);
        return true;
    }

    static void ApplyEditorPreviewRenderOrder(MeshRenderer renderer, RenderTexture renderOrder, int actualSplatCount)
    {
        if (renderer == null)
        {
            return;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
        {
            return;
        }

        MaterialPropertyBlock propertyBlock = GetEditorPreviewPropertyBlock();

        for (int i = 0; i < materials.Length; i++)
        {
            renderer.GetPropertyBlock(propertyBlock, i);
            propertyBlock.SetTexture("_GS_RenderOrder", renderOrder);
            propertyBlock.SetInt("_ActualSplatCount", actualSplatCount);
            renderer.SetPropertyBlock(propertyBlock, i);
            propertyBlock.Clear();
        }
    }

    static void ClearEditorPreviewRenderOrder(MeshRenderer renderer)
    {
        if (renderer == null)
        {
            return;
        }

        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
        {
            return;
        }

        for (int i = 0; i < materials.Length; i++)
        {
            renderer.SetPropertyBlock(null, i);
        }
    }

    static EditorPreviewTargetState GetOrCreateEditorPreviewTargetState(GaussianSplatObject splatObject, MeshRenderer sortedRenderer, int requiredWidth, int requiredHeight)
    {
        int targetId = splatObject.GetInstanceID();
        if (!_editorPreviewTargets.TryGetValue(targetId, out EditorPreviewTargetState state))
        {
            state = new EditorPreviewTargetState();
            _editorPreviewTargets[targetId] = state;
        }

        state.sortedRenderer = sortedRenderer;
        state.generation = _editorPreviewGeneration;
        EnsureTemporarySortRenderTexture(ref state.renderOrder, sortedRenderer.name + "_EditorPreviewRenderOrder", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 2);
        return state;
    }

    static void ReleaseEditorPreviewTargetState(int targetId, EditorPreviewTargetState state)
    {
        if (state != null)
        {
            ClearEditorPreviewRenderOrder(state.sortedRenderer);
            ReleaseTemporarySortRenderTexture(ref state.renderOrder);
        }

        _editorPreviewTargets.Remove(targetId);
    }

    static void CleanupUnusedEditorPreviewTargets()
    {
        if (_editorPreviewTargets.Count == 0)
        {
            return;
        }

        List<int> staleTargetIds = new List<int>();
        foreach (KeyValuePair<int, EditorPreviewTargetState> pair in _editorPreviewTargets)
        {
            EditorPreviewTargetState state = pair.Value;
            if (state == null || state.generation != _editorPreviewGeneration)
            {
                staleTargetIds.Add(pair.Key);
            }
        }

        for (int i = 0; i < staleTargetIds.Count; i++)
        {
            int staleTargetId = staleTargetIds[i];
            if (_editorPreviewTargets.TryGetValue(staleTargetId, out EditorPreviewTargetState staleState))
            {
                ReleaseEditorPreviewTargetState(staleTargetId, staleState);
            }
        }
    }

    static bool ShouldProcessEditorPreviewSplatObject(GaussianSplatObject splatObject)
    {
        if (!IsSceneSplatObject(splatObject, default(UnityEngine.SceneManagement.Scene)))
        {
            return false;
        }

        if (splatObject.enabled == false || !splatObject.gameObject.activeInHierarchy)
        {
            return false;
        }

        return FindExistingSceneRenderer(splatObject.gameObject.scene) == null;
    }

    static void SortOrphanEditorPreview(GaussianSplatObject splatObject, Camera camera)
    {
        if (splatObject == null || camera == null)
        {
            return;
        }

        MeshRenderer sortedRenderer = splatObject.GetSortedRenderer();
        if (sortedRenderer == null)
        {
            return;
        }

        Material[] materials = sortedRenderer.sharedMaterials;
        Material positionsMaterial = ResolvePrimarySplatMaterial(materials);
        if (positionsMaterial == null || !positionsMaterial.HasProperty("_GS_Positions"))
        {
            return;
        }

        Texture positionsTexture = positionsMaterial.GetTexture("_GS_Positions");
        if (positionsTexture == null)
        {
            return;
        }

        int textureElementCount = positionsTexture.width * positionsTexture.height;
        int actualSplatCount = positionsMaterial.HasProperty("_ActualSplatCount") ? positionsMaterial.GetInt("_ActualSplatCount") : 0;
        int elementCount = actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
        if (elementCount <= 0)
        {
            return;
        }

        ComputeRequiredSortTextureSize(elementCount, out int requiredWidth, out int requiredHeight);
        if (!EnsureEditorPreviewSorter(requiredWidth, requiredHeight))
        {
            return;
        }

        EditorPreviewTargetState state = GetOrCreateEditorPreviewTargetState(splatObject, sortedRenderer, requiredWidth, requiredHeight);
        ApplyEditorPreviewRenderOrder(sortedRenderer, state.renderOrder, elementCount);

        _editorPreviewRadixSort.elementCount = elementCount;
        Material keyValuesMaterial = _editorPreviewRadixSort.computeKeyValues;
        keyValuesMaterial.SetTexture("_GS_Positions", positionsTexture);
        keyValuesMaterial.SetInt("_GS_Positions_CoordMask", positionsMaterial.HasProperty("_GS_Positions_CoordMask") ? positionsMaterial.GetInt("_GS_Positions_CoordMask") : 0);
        keyValuesMaterial.SetInt("_GS_Positions_CoordShift", positionsMaterial.HasProperty("_GS_Positions_CoordShift") ? positionsMaterial.GetInt("_GS_Positions_CoordShift") : 0);
        keyValuesMaterial.SetVector("_CameraPos", sortedRenderer.transform.InverseTransformPoint(camera.transform.position));

        _editorPreviewRadixSort.BeginSortForEditor();
        _editorPreviewRadixSort.StepSortForEditor(RadixSort.TotalSortPasses);
        _editorPreviewRadixSort.CopySortedOrderForEditor(state.renderOrder, SCREEN_CAMERA_ID);
        splatObject.ShowSorted();
    }

    static void SortOrphanEditorPreviews(Camera camera)
    {
        GaussianSplatObject[] sceneSplatObjects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
        for (int i = 0; i < sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject splatObject = sceneSplatObjects[i];
            if (!ShouldProcessEditorPreviewSplatObject(splatObject))
            {
                continue;
            }

            SortOrphanEditorPreview(splatObject, camera);
        }
    }

    static void OnEditorCameraPreCull(Camera camera)
    {
        if (camera == null || Application.isPlaying || camera.cameraType != CameraType.SceneView)
        {
            return;
        }

        _editorPreviewGeneration++;

        GaussianSplatRenderer[] sceneRenderers = FindSceneRenderers();
        for (int i = 0; i < sceneRenderers.Length; i++)
        {
            GaussianSplatRenderer renderer = sceneRenderers[i];
            if (!ShouldProcessEditorRenderer(renderer))
            {
                continue;
            }

            renderer.SortEditorCamera(camera);
        }

        SortOrphanEditorPreviews(camera);
        CleanupUnusedEditorPreviewTargets();
    }

    static bool ShouldProcessEditorRenderer(GaussianSplatRenderer renderer)
    {
        if (renderer == null || !renderer.enabled)
        {
            return false;
        }

        GameObject rootObject = renderer.transform.root != null ? renderer.transform.root.gameObject : renderer.gameObject;
        if (rootObject == null || EditorUtility.IsPersistent(rootObject))
        {
            return false;
        }

        if (!ShouldUseEditorPreviewScene(rootObject.scene))
        {
            return false;
        }

        return (renderer.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) == 0;
    }

    static void OnEditorHierarchyChanged()
    {
        GaussianSplatRenderer[] sceneRenderers = FindSceneRenderers();
        for (int i = 0; i < sceneRenderers.Length; i++)
        {
            GaussianSplatRenderer renderer = sceneRenderers[i];
            if (renderer == null)
            {
                continue;
            }

            bool changed = renderer.RefreshCachedSceneSplatObjects();
            if (renderer.IsCombinedRenderingMode())
            {
                renderer.UpdateSortingResourceTextures();
            }

            if (changed)
            {
                EditorUtility.SetDirty(renderer);
            }
        }

        CleanupUnusedEditorPreviewTargets();

        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    static GaussianSplatRenderer[] FindSceneRenderers(UnityEngine.SceneManagement.Scene scene)
    {
        List<GaussianSplatRenderer> sceneRenderers = new List<GaussianSplatRenderer>();
        GaussianSplatRenderer[] allRenderers = Resources.FindObjectsOfTypeAll<GaussianSplatRenderer>();
        for (int i = 0; i < allRenderers.Length; i++)
        {
            GaussianSplatRenderer renderer = allRenderers[i];
            if (renderer == null)
            {
                continue;
            }

            GameObject rootObject = renderer.transform.root != null ? renderer.transform.root.gameObject : renderer.gameObject;
            if (rootObject == null || EditorUtility.IsPersistent(rootObject))
            {
                continue;
            }

            if ((renderer.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
            {
                continue;
            }

            if (!ShouldUseEditorPreviewScene(rootObject.scene))
            {
                continue;
            }

            if (scene.IsValid() && rootObject.scene != scene)
            {
                continue;
            }

            sceneRenderers.Add(renderer);
        }

        return sceneRenderers.ToArray();
    }

    static GaussianSplatRenderer[] FindSceneRenderers()
    {
        return FindSceneRenderers(default(UnityEngine.SceneManagement.Scene));
    }

    static GaussianSplatRenderer GetPrimarySceneRenderer(UnityEngine.SceneManagement.Scene scene)
    {
        GaussianSplatRenderer[] renderers = FindSceneRenderers(scene);
        if (renderers.Length == 0)
        {
            return null;
        }

        GaussianSplatRenderer primaryRenderer = renderers[0];
        int primaryInstanceId = primaryRenderer.GetInstanceID();
        for (int i = 1; i < renderers.Length; i++)
        {
            GaussianSplatRenderer candidate = renderers[i];
            if (candidate == null)
            {
                continue;
            }

            int candidateInstanceId = candidate.GetInstanceID();
            if (candidateInstanceId < primaryInstanceId)
            {
                primaryRenderer = candidate;
                primaryInstanceId = candidateInstanceId;
            }
        }

        return primaryRenderer;
    }

    static GaussianSplatRenderer GetPrimarySceneRenderer()
    {
        return GetPrimarySceneRenderer(default(UnityEngine.SceneManagement.Scene));
    }

    static Material LoadPackageMaterial(string assetPath)
    {
        return AssetDatabase.LoadAssetAtPath<Material>(assetPath);
    }

    [MenuItem("GameObject/Gaussian Splatting/Gaussian Splat Renderer", false, 10)]
    static void CreateGaussianSplatRenderer(MenuCommand menuCommand)
    {
        GaussianSplatRenderer renderer = EnsureSceneRendererExists();
        if (renderer != null)
        {
            Selection.activeGameObject = renderer.gameObject;
        }
    }

    public static GaussianSplatRenderer FindExistingSceneRenderer()
    {
        return FindExistingSceneRenderer(default(UnityEngine.SceneManagement.Scene));
    }

    public static GaussianSplatRenderer FindExistingSceneRenderer(UnityEngine.SceneManagement.Scene scene)
    {
        return GetPrimarySceneRenderer(scene);
    }

    public static GaussianSplatRenderer EnsureSceneRendererExists()
    {
        return EnsureSceneRendererExists(default(UnityEngine.SceneManagement.Scene));
    }

    public static GaussianSplatRenderer EnsureSceneRendererExists(UnityEngine.SceneManagement.Scene scene)
    {
        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer(scene);
        if (primaryRenderer == null)
        {
            GameObject rendererObject = new GameObject("GaussianSplatRenderer");
            Undo.RegisterCreatedObjectUndo(rendererObject, "Create Gaussian Splat Renderer");

            if (scene.IsValid())
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rendererObject, scene);
            }

            primaryRenderer = AddGeneratedUdonSharpComponent<GaussianSplatRenderer>(rendererObject, "Add Gaussian Splat Renderer");
            RadixSort radixSort = AddGeneratedUdonSharpComponent<RadixSort>(rendererObject, "Add Radix Sort");
            radixSort.computeKeyValues = LoadPackageMaterial("Assets/VRChatGaussianSplatting/Resources/Materials/VRChatGaussianSplatting_ComputeKeyValue.mat");
            radixSort.radixSort = LoadPackageMaterial("Assets/VRChatGaussianSplatting/RadixSort/Materials/Misha_RadixSort.mat");
            EditorUtility.SetDirty(rendererObject);
            EditorUtility.SetDirty(primaryRenderer);
            EditorUtility.SetDirty(radixSort);
        }

        GaussianSplatRenderer[] renderers = FindSceneRenderers(scene);
        if (renderers.Length > 1)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Keep one renderer in the scene; Gaussian splats now share a single renderer.");
            for (int i = 0; i < renderers.Length; i++)
            {
                GaussianSplatRenderer renderer = renderers[i];
                if (renderer != null && renderer != primaryRenderer)
                {
                    renderer.enabled = false;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        if (!primaryRenderer.enabled)
        {
            primaryRenderer.enabled = true;
            EditorUtility.SetDirty(primaryRenderer);
        }

        if (primaryRenderer.RefreshCachedSceneSplatObjects())
        {
            EditorUtility.SetDirty(primaryRenderer);
        }

        primaryRenderer.UpdateSortingResourceTextures();
        return primaryRenderer;
    }

    bool RefreshCachedSceneSplatObjects()
    {
        GaussianSplatObject[] sceneObjects = CollectSceneSplatObjects(gameObject.scene, true);
        GameObject[] sceneObjectRoots = new GameObject[sceneObjects.Length];
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject sceneObject = sceneObjects[i];
            sceneObjectRoots[i] = sceneObject != null ? sceneObject.gameObject : null;
        }

        bool changed = !SplatObjectArraysMatch(cachedSceneSplatObjects, sceneObjectRoots);
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject sceneObject = sceneObjects[i];
            if (sceneObject == null || sceneObject.gaussianSplatRenderer == this)
            {
                continue;
            }

            sceneObject.gaussianSplatRenderer = this;
            EditorUtility.SetDirty(sceneObject);
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        cachedSceneSplatObjects = sceneObjectRoots;
        return true;
    }

    static bool SplatObjectArraysMatch(GameObject[] left, GameObject[] right)
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

    void RestoreSceneSplatObjectsForCombinedMode()
    {
        GaussianSplatObject[] sceneObjects = CollectSceneSplatObjects(gameObject.scene, true);
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GaussianSplatObject sceneObject = sceneObjects[i];
            if (sceneObject == null || sceneObject.gameObject.activeSelf)
            {
                continue;
            }

            sceneObject.gameObject.SetActive(true);
            EditorUtility.SetDirty(sceneObject.gameObject);
        }
    }

    void OnValidate()
    {
        if (EditorUtility.IsPersistent(this))
        {
            return;
        }

        GaussianSplatRenderer primaryRenderer = GetPrimarySceneRenderer(gameObject.scene);
        if (primaryRenderer != null && primaryRenderer != this)
        {
            Debug.LogError("Multiple GaussianSplatRenderer instances found. Only one GaussianSplatRenderer is supported per scene.");
            if (enabled)
            {
                enabled = false;
                EditorUtility.SetDirty(this);
            }

            GaussianSplatRendererUI.RequestEditorRefresh();
            return;
        }

        if (RefreshCachedSceneSplatObjects())
        {
            EditorUtility.SetDirty(this);
        }

        if (_lastValidatedRenderingMode != renderingMode)
        {
            if (renderingMode == GaussianSplatRenderingMode.CombineAllSplats)
            {
                RestoreSceneSplatObjectsForCombinedMode();
            }
            else
            {
                ApplyActiveSplatObject();
            }

            _lastValidatedRenderingMode = renderingMode;
            EditorUtility.SetDirty(this);
        }

        UpdateSortingResourceTextures();
        GaussianSplatRendererUI.RequestEditorRefresh();
    }

    static T AddGeneratedUdonSharpComponent<T>(GameObject targetObject, string undoLabel) where T : UdonSharpBehaviour
    {
        Undo.RegisterCompleteObjectUndo(targetObject, undoLabel);
        return targetObject.AddUdonSharpComponent<T>();
    }

    static string SanitizeAssetName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "GaussianSplatRenderer";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] sanitizedChars = value.ToCharArray();
        for (int i = 0; i < sanitizedChars.Length; i++)
        {
            if (Array.IndexOf(invalidChars, sanitizedChars[i]) >= 0)
            {
                sanitizedChars[i] = '_';
            }
        }

        return new string(sanitizedChars);
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

    static string GetSceneSortResourceFolderName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return "GS_UnsavedScene";
        }

        string sanitizedSceneName = SanitizeAssetName(sceneName);
        if (string.IsNullOrEmpty(sanitizedSceneName))
        {
            return "GS_UnsavedScene";
        }

        return "GS_" + sanitizedSceneName;
    }

    string GetSortResourceFolderPath()
    {
        string sceneName = string.Empty;
        if (gameObject != null)
        {
            sceneName = gameObject.scene.name;
            if (string.IsNullOrEmpty(sceneName) && !string.IsNullOrEmpty(gameObject.scene.path))
            {
                sceneName = Path.GetFileNameWithoutExtension(gameObject.scene.path);
            }
        }

        return "Assets/Temp/" + GetSceneSortResourceFolderName(sceneName) + "/RTs";
    }

    string GetCombinedResourceFolderPath()
    {
        return GetSortResourceFolderPath() + "/Combined";
    }

    static bool MaterialArraysMatch(Material[] left, Material[] right)
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

    static Material CreateMaterialFromTemplate(Material template, string shaderName, string materialName)
    {
        Shader shader = template != null ? template.shader : Shader.Find(shaderName);
        if (shader == null)
        {
            return null;
        }

        Material material = template != null ? new Material(template) : new Material(shader);
        material.name = materialName;
        return material;
    }

    bool TryGetCombinedSourceTemplates(
        GaussianSplatObject[] sceneSplatObjects,
        out MeshRenderer templateRenderer,
        out Material primaryTemplate,
        out Material alphaMaskTemplate,
        out Material toSrgbTemplate,
        out Material toLinearTemplate)
    {
        templateRenderer = null;
        primaryTemplate = null;
        alphaMaskTemplate = null;
        toSrgbTemplate = null;
        toLinearTemplate = null;

        if (sceneSplatObjects == null)
        {
            return false;
        }

        for (int i = 0; i < sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject sceneSplatObject = sceneSplatObjects[i];
            if (sceneSplatObject == null)
            {
                continue;
            }

            MeshRenderer renderer = GetSortedRenderer(sceneSplatObject.gameObject);
            if (renderer == null)
            {
                continue;
            }

            Material[] materials = GetRendererMaterialsForRead(renderer);
            Material candidatePrimary = ResolvePrimarySplatMaterial(materials);
            if (candidatePrimary == null)
            {
                continue;
            }

            templateRenderer = renderer;
            primaryTemplate = candidatePrimary;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null || material.shader == null)
                {
                    continue;
                }

                string shaderName = material.shader.name;
                if (alphaMaskTemplate == null && shaderName == "VRChatGaussianSplatting/AlphaDepthMask")
                {
                    alphaMaskTemplate = material;
                }
                else if (toSrgbTemplate == null && shaderName == "VRChatGaussianSplatting/ToSRGB")
                {
                    toSrgbTemplate = material;
                }
                else if (toLinearTemplate == null && shaderName == "VRChatGaussianSplatting/ToLinear")
                {
                    toLinearTemplate = material;
                }
            }

            return true;
        }

        return false;
    }

    static void EncapsulateWorldBounds(ref Bounds localBounds, ref bool hasBounds, Bounds worldBounds, Matrix4x4 worldToLocal)
    {
        Vector3 center = worldBounds.center;
        Vector3 extents = worldBounds.extents;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    Vector3 localCorner = worldToLocal.MultiplyPoint3x4(corner);
                    if (!hasBounds)
                    {
                        localBounds = new Bounds(localCorner, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        localBounds.Encapsulate(localCorner);
                    }
                }
            }
        }
    }

    Bounds ComputeCombinedLocalBounds(GaussianSplatObject[] sceneSplatObjects)
    {
        Matrix4x4 worldToLocal = Matrix4x4.identity;
        Bounds localBounds = default;
        bool hasBounds = false;

        if (sceneSplatObjects != null)
        {
            for (int i = 0; i < sceneSplatObjects.Length; i++)
            {
                GaussianSplatObject sceneSplatObject = sceneSplatObjects[i];
                if (sceneSplatObject == null)
                {
                    continue;
                }

                MeshRenderer renderer = GetSortedRenderer(sceneSplatObject.gameObject);
                if (renderer == null)
                {
                    continue;
                }

                EncapsulateWorldBounds(ref localBounds, ref hasBounds, renderer.bounds, worldToLocal);
            }
        }

        if (!hasBounds)
        {
            localBounds = new Bounds(Vector3.zero, Vector3.one * 1000.0f);
        }

        return localBounds;
    }

    bool EnsureCombinedRendererObject(Material[] combinedMaterials, Mesh templateMesh, MeshRenderer templateRenderer)
    {
        bool changed = false;
        GameObject childObject = combinedSortedRenderer != null ? combinedSortedRenderer.gameObject : FindNamedChild(gameObject, "CombinedSorted");
        if (childObject == null)
        {
            childObject = new GameObject("CombinedSorted");
            Undo.RegisterCreatedObjectUndo(childObject, "Create Combined Gaussian Splat Renderer");
            changed = true;
        }

        Transform childTransform = childObject.transform;
        if (childTransform.parent != null)
        {
            Undo.SetTransformParent(childTransform, null, "Reparent Combined Gaussian Splat Renderer");
            changed = true;
        }

        if (childTransform.position != Vector3.zero || childTransform.rotation != Quaternion.identity || childTransform.localScale != Vector3.one)
        {
            Undo.RecordObject(childTransform, "Reset Combined Gaussian Splat Renderer Transform");
            childTransform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            childTransform.localScale = Vector3.one;
            EditorUtility.SetDirty(childTransform);
            changed = true;
        }

        MeshFilter meshFilter = childObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = Undo.AddComponent<MeshFilter>(childObject);
            changed = true;
        }

        MeshRenderer meshRenderer = childObject.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
        {
            meshRenderer = Undo.AddComponent<MeshRenderer>(childObject);
            changed = true;
        }

        combinedSortedRenderer = meshRenderer;

        Bounds combinedBounds = templateMesh != null
            ? templateMesh.bounds
            : (meshFilter.sharedMesh != null ? meshFilter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 1000.0f));

        string combinedFolderPath = GetCombinedResourceFolderPath();
        string assetPrefix = SanitizeAssetName(name);
        int parentStart = 0;
        int parentEnd = combinedMaterials != null ? combinedMaterials.Length : 0;
        List<Material> parentMaterials = new List<Material>();

        if (combinedMaterials != null && parentEnd > 0)
        {
            Material leadingMaterial = combinedMaterials[0];
            if (leadingMaterial != null && leadingMaterial.shader != null && leadingMaterial.shader.name == "VRChatGaussianSplatting/ToSRGB")
            {
                parentMaterials.Add(leadingMaterial);
                parentStart = 1;
            }

            Material trailingMaterial = combinedMaterials[parentEnd - 1];
            if (parentEnd > parentStart && trailingMaterial != null && trailingMaterial.shader != null && trailingMaterial.shader.name == "VRChatGaussianSplatting/ToLinear")
            {
                parentMaterials.Add(trailingMaterial);
                parentEnd--;
            }
        }

        if (templateMesh == null && parentMaterials.Count > 0)
        {
            List<int> parentIndexCounts = new List<int>();
            List<MeshTopology> parentTopologies = new List<MeshTopology>();
            for (int materialIndex = 0; materialIndex < parentMaterials.Count; materialIndex++)
            {
                parentIndexCounts.Add(3);
                parentTopologies.Add(MeshTopology.Triangles);
            }

            Mesh conversionMesh = PlySplatImporter.CreateMultiPassMesh(parentIndexCounts, parentTopologies, combinedBounds);
            templateMesh = PlySplatImporter.CreateOrReplaceAsset(conversionMesh, combinedFolderPath + "/" + assetPrefix + "_CombinedConversionMesh.asset");
        }

        if (meshFilter.sharedMesh != templateMesh)
        {
            Undo.RecordObject(meshFilter, "Update Combined Gaussian Splat Mesh");
            meshFilter.sharedMesh = templateMesh;
            EditorUtility.SetDirty(meshFilter);
            changed = true;
        }

        Material[] parentMaterialArray = parentMaterials.ToArray();
        if (!MaterialArraysMatch(meshRenderer.sharedMaterials, parentMaterialArray))
        {
            Undo.RecordObject(meshRenderer, "Update Combined Gaussian Splat Materials");
            meshRenderer.sharedMaterials = parentMaterialArray;
            EditorUtility.SetDirty(meshRenderer);
            changed = true;
        }

        int chunkCount = 0;
        for (int materialIndex = parentStart; materialIndex < parentEnd; chunkCount++)
        {
            Material alphaMask = null;
            Material splatMaterial = combinedMaterials[materialIndex];
            if (splatMaterial != null && splatMaterial.shader != null && splatMaterial.shader.name == "VRChatGaussianSplatting/AlphaDepthMask")
            {
                alphaMask = splatMaterial;
                materialIndex++;
                if (materialIndex >= parentEnd)
                {
                    break;
                }

                splatMaterial = combinedMaterials[materialIndex];
            }

            materialIndex++;
            if (splatMaterial == null || !splatMaterial.HasProperty("_SplatCount"))
            {
                continue;
            }

            int splatCount = splatMaterial.GetInt("_SplatCount");
            List<Material> chunkMaterialList = new List<Material>();
            List<int> chunkIndexCounts = new List<int>();
            List<MeshTopology> chunkTopologies = new List<MeshTopology>();
            if (alphaMask != null)
            {
                chunkMaterialList.Add(alphaMask);
                chunkIndexCounts.Add(3);
                chunkTopologies.Add(MeshTopology.Triangles);
            }

            chunkMaterialList.Add(splatMaterial);
            chunkIndexCounts.Add((Mathf.Max(0, splatCount) + 31) / 32);
            chunkTopologies.Add(MeshTopology.Points);

            string chunkName = "CombinedChunk" + chunkCount;
            GameObject chunkObject = FindNamedChild(childObject, chunkName);
            if (chunkObject == null)
            {
                chunkObject = new GameObject(chunkName);
                Undo.RegisterCreatedObjectUndo(chunkObject, "Create Combined Gaussian Splat Chunk");
                changed = true;
            }

            Transform chunkTransform = chunkObject.transform;
            if (chunkTransform.parent != childTransform)
            {
                Undo.SetTransformParent(chunkTransform, childTransform, "Parent Combined Gaussian Splat Chunk");
                changed = true;
            }

            if (chunkTransform.localPosition != Vector3.zero || chunkTransform.localRotation != Quaternion.identity || chunkTransform.localScale != Vector3.one)
            {
                Undo.RecordObject(chunkTransform, "Reset Combined Gaussian Splat Chunk Transform");
                chunkTransform.localPosition = Vector3.zero;
                chunkTransform.localRotation = Quaternion.identity;
                chunkTransform.localScale = Vector3.one;
                EditorUtility.SetDirty(chunkTransform);
                changed = true;
            }

            MeshFilter chunkMeshFilter = chunkObject.GetComponent<MeshFilter>();
            if (chunkMeshFilter == null)
            {
                chunkMeshFilter = Undo.AddComponent<MeshFilter>(chunkObject);
                changed = true;
            }

            MeshRenderer chunkMeshRenderer = chunkObject.GetComponent<MeshRenderer>();
            if (chunkMeshRenderer == null)
            {
                chunkMeshRenderer = Undo.AddComponent<MeshRenderer>(chunkObject);
                changed = true;
            }

            string chunkMeshName = assetPrefix + (chunkCount > 0 ? $"_CombinedPass{chunkCount}" : "_CombinedMain") + "_Mesh";
            Mesh chunkMesh = PlySplatImporter.CreateMultiPassMesh(chunkIndexCounts, chunkTopologies, combinedBounds);
            chunkMesh = PlySplatImporter.CreateOrReplaceAsset(chunkMesh, combinedFolderPath + "/" + chunkMeshName + ".asset");

            if (chunkMeshFilter.sharedMesh != chunkMesh)
            {
                Undo.RecordObject(chunkMeshFilter, "Update Combined Gaussian Splat Chunk Mesh");
                chunkMeshFilter.sharedMesh = chunkMesh;
                EditorUtility.SetDirty(chunkMeshFilter);
                changed = true;
            }

            Material[] chunkMaterialArray = chunkMaterialList.ToArray();
            if (!MaterialArraysMatch(chunkMeshRenderer.sharedMaterials, chunkMaterialArray))
            {
                Undo.RecordObject(chunkMeshRenderer, "Update Combined Gaussian Splat Chunk Materials");
                chunkMeshRenderer.sharedMaterials = chunkMaterialArray;
                EditorUtility.SetDirty(chunkMeshRenderer);
                changed = true;
            }

            if (templateRenderer != null)
            {
                chunkMeshRenderer.shadowCastingMode = templateRenderer.shadowCastingMode;
                chunkMeshRenderer.receiveShadows = templateRenderer.receiveShadows;
                chunkMeshRenderer.lightProbeUsage = templateRenderer.lightProbeUsage;
                chunkMeshRenderer.reflectionProbeUsage = templateRenderer.reflectionProbeUsage;
                chunkMeshRenderer.motionVectorGenerationMode = templateRenderer.motionVectorGenerationMode;
                chunkMeshRenderer.allowOcclusionWhenDynamic = templateRenderer.allowOcclusionWhenDynamic;
            }

            bool shouldChunkBeActive = IsCombinedRenderingMode();
            if (chunkObject.activeSelf != shouldChunkBeActive)
            {
                Undo.RecordObject(chunkObject, "Toggle Combined Gaussian Splat Chunk");
                chunkObject.SetActive(shouldChunkBeActive);
                EditorUtility.SetDirty(chunkObject);
                changed = true;
            }
        }

        for (int childIndex = 0; childIndex < childTransform.childCount; childIndex++)
        {
            Transform chunkTransform = childTransform.GetChild(childIndex);
            if (!chunkTransform.name.StartsWith("CombinedChunk"))
            {
                continue;
            }

            bool keepChunk = false;
            for (int activeChunkIndex = 0; activeChunkIndex < chunkCount; activeChunkIndex++)
            {
                if (chunkTransform.name == "CombinedChunk" + activeChunkIndex)
                {
                    keepChunk = true;
                    break;
                }
            }

            if (!keepChunk && chunkTransform.gameObject.activeSelf)
            {
                Undo.RecordObject(chunkTransform.gameObject, "Toggle Combined Gaussian Splat Chunk");
                chunkTransform.gameObject.SetActive(false);
                EditorUtility.SetDirty(chunkTransform.gameObject);
                changed = true;
            }
        }

        if (templateRenderer != null)
        {
            meshRenderer.shadowCastingMode = templateRenderer.shadowCastingMode;
            meshRenderer.receiveShadows = templateRenderer.receiveShadows;
            meshRenderer.lightProbeUsage = templateRenderer.lightProbeUsage;
            meshRenderer.reflectionProbeUsage = templateRenderer.reflectionProbeUsage;
            meshRenderer.motionVectorGenerationMode = templateRenderer.motionVectorGenerationMode;
            meshRenderer.allowOcclusionWhenDynamic = templateRenderer.allowOcclusionWhenDynamic;
        }

        bool shouldBeActive = IsCombinedRenderingMode();
        if (childObject.activeSelf != shouldBeActive)
        {
            Undo.RecordObject(childObject, "Toggle Combined Gaussian Splat Renderer");
            childObject.SetActive(shouldBeActive);
            EditorUtility.SetDirty(childObject);
            changed = true;
        }

        return changed;
    }

    void UpdateCombinedResources(GaussianSplatObject[] sceneSplatObjects, int combinedElementCount)
    {
        if (combinedElementCount <= 0)
        {
            return;
        }

        if (!TryGetCombinedSourceTemplates(sceneSplatObjects, out MeshRenderer templateRenderer, out Material primaryTemplate, out Material alphaMaskTemplate, out Material toSrgbTemplate, out Material toLinearTemplate))
        {
            return;
        }

        var combinedLayout = PlySplatImporter.ChoosePotTextureLayout(combinedElementCount);
        int combinedWidth = combinedLayout.Width;
        int combinedHeight = combinedLayout.Height;
        string combinedFolderPath = GetCombinedResourceFolderPath();
        string assetPrefix = SanitizeAssetName(name);

        Undo.RecordObject(this, "Update Combined Gaussian Splat Resources");

        EnsureSortRenderTexture(ref combinedPositions, combinedFolderPath, assetPrefix + "_CombinedPositions", combinedWidth, combinedHeight, RenderTextureFormat.ARGBFloat, false, 1);
        EnsureSortRenderTexture(ref combinedRotations, combinedFolderPath, assetPrefix + "_CombinedRotations", combinedWidth, combinedHeight, RenderTextureFormat.ARGB32, false, 1);
        EnsureSortRenderTexture(ref combinedScales, combinedFolderPath, assetPrefix + "_CombinedScales", combinedWidth, combinedHeight, RenderTextureFormat.ARGBHalf, false, 1);
        EnsureSortRenderTexture(ref combinedColorsCamera, combinedFolderPath, assetPrefix + "_CombinedColorsCamera", combinedWidth, combinedHeight, RenderTextureFormat.ARGB32, false, MAX_CAMERA_COUNT);
        EnsureSortRenderTexture(ref combinedColorsScratch, combinedFolderPath, assetPrefix + "_CombinedColorsScratch", combinedWidth, combinedHeight, RenderTextureFormat.ARGB32, false, 1);

        Shader combineShader = Shader.Find("Hidden/GaussianSplatting/CombineData");
        if (combineShader == null)
        {
            Debug.LogError("Hidden/GaussianSplatting/CombineData shader not found.");
            return;
        }

        Material combineMaterial = new Material(combineShader);
        combineMaterial.name = assetPrefix + "_CombineData";
        combineDataMaterial = PlySplatImporter.CreateOrReplaceAsset(combineMaterial, combinedFolderPath + "/" + assetPrefix + "_CombineData.mat");

        bool useSrgb = toSrgbTemplate != null || toLinearTemplate != null;
        int splatsPerPass = Mathf.Min(DEFAULT_COMBINED_SPLATS_PER_PASS, combinedElementCount);
        int maxAlphaMaskCount = DEFAULT_COMBINED_MAX_ALPHA_MASK_COUNT;
        var passInfos = PlySplatImporter.CreatePassLayout(combinedElementCount, splatsPerPass, maxAlphaMaskCount, useSrgb);
        Bounds combinedBounds = ComputeCombinedLocalBounds(sceneSplatObjects);
        string materialsFolderPath = combinedFolderPath + "/Materials";
        EnsureFolderExists(materialsFolderPath);
        int renderQueue = 3500;

        List<Material> generatedMaterials = new List<Material>();

        if (useSrgb)
        {
            Material convertToSrgb = CreateMaterialFromTemplate(toSrgbTemplate, "VRChatGaussianSplatting/ToSRGB", assetPrefix + "_CombinedToSRGB");
            if (convertToSrgb != null)
            {
                convertToSrgb.renderQueue = renderQueue++;
                generatedMaterials.Add(convertToSrgb);
            }
        }

        Material mainMat = null;
        for (int passIndex = 0; passIndex < passInfos.Length; passIndex++)
        {
            PlySplatImporter.PassInfo passInfo = passInfos[passIndex];
            string splatMatName = assetPrefix + (passInfo.PassIndex > 0 ? $"_CombinedPass{passInfo.PassIndex}" : "_CombinedMain") + "_Splat";
            Material splatMat = passInfo.PassIndex == 0
                ? CreateMaterialFromTemplate(primaryTemplate, "VRChatGaussianSplatting/GaussianSplatting", splatMatName)
                : (mainMat != null ? new Material(mainMat) : CreateMaterialFromTemplate(primaryTemplate, "VRChatGaussianSplatting/GaussianSplatting", splatMatName));
            if (splatMat == null)
            {
                continue;
            }

            splatMat.name = splatMatName;
            if (passInfo.PassIndex == 0)
            {
                mainMat = splatMat;
            }

            List<Material> chunkMaterials = new List<Material>();

            splatMat.SetTexture("_GS_Positions", combinedPositions);
            int positionBlocksPerRow = Mathf.Max(1, combinedPositions.width >> 2);
            splatMat.SetInt("_GS_Positions_CoordMask", positionBlocksPerRow - 1);
            splatMat.SetInt("_GS_Positions_CoordShift", PlySplatImporter.ComputeTextureCoordShift(positionBlocksPerRow));
            splatMat.SetTexture("_GS_Colors", null);
            splatMat.SetTexture("_GS_Rotations", combinedRotations);
            splatMat.SetTexture("_GS_Scales", combinedScales);
            splatMat.SetTexture("_GS_SH", null);
            splatMat.SetInt("_GS_SH_CoordMask", 0);
            splatMat.SetInt("_GS_SH_CoordShift", 0);
            splatMat.SetInt("_GS_SH_CoeffCount", 0);
            splatMat.SetInt("_GS_SH_CoeffStride", combinedElementCount);
            splatMat.SetVector("_GS_SH_Min", Vector4.zero);
            splatMat.SetVector("_GS_SH_Range", Vector4.one);
            splatMat.SetInt("_ActualSplatCount", combinedElementCount);
            splatMat.SetFloat("_SHBand", 0.0f);
            splatMat.SetTexture("_GS_ColorsCamera", combinedColorsCamera);
            splatMat.SetFloat("_GS_CameraColorArray", 1.0f);
            splatMat.EnableKeyword("GS_CAMERA_COLOR_ARRAY");
            splatMat.SetTexture("_GS_RenderOrderPrecomputed", null);
            splatMat.SetInt("_GS_RenderOrderPrecomputed_CoordMask", 0);
            splatMat.SetInt("_GS_RenderOrderPrecomputed_CoordShift", 0);
            splatMat.SetInteger("_PRECOMPUTED_SORTING", 0);
            splatMat.DisableKeyword("_PRECOMPUTED_SORTING_ON");
            splatMat.SetInt("_SplatCount", passInfo.SplatCount);
            splatMat.SetInt("_SplatOffset", passInfo.SplatOffset);
            ApplyConfiguredMaterialSettings(splatMat, 0);

            if (passInfo.HasAlphaMask)
            {
                Material alphaMask = CreateMaterialFromTemplate(alphaMaskTemplate, "VRChatGaussianSplatting/AlphaDepthMask", splatMatName + "_AlphaDepthMask");
                if (alphaMask != null)
                {
                    alphaMask.renderQueue = renderQueue++;
                    generatedMaterials.Add(alphaMask);
                    chunkMaterials.Add(alphaMask);
                }
            }

            splatMat.renderQueue = renderQueue++;
            generatedMaterials.Add(splatMat);
            chunkMaterials.Add(splatMat);

        }

        if (useSrgb)
        {
            Material convertToLinear = CreateMaterialFromTemplate(toLinearTemplate, "VRChatGaussianSplatting/ToLinear", assetPrefix + "_CombinedToLinear");
            if (convertToLinear != null)
            {
                convertToLinear.renderQueue = renderQueue++;
                generatedMaterials.Add(convertToLinear);
            }
        }

        for (int materialIndex = 0; materialIndex < generatedMaterials.Count; materialIndex++)
        {
            Material generatedMaterial = generatedMaterials[materialIndex];
            if (generatedMaterial == null)
            {
                continue;
            }

            generatedMaterials[materialIndex] = PlySplatImporter.CreateOrReplaceAsset(generatedMaterial, materialsFolderPath + "/" + generatedMaterial.name + ".mat");
        }

        Mesh generatedMesh = null;
        if (useSrgb)
        {
            List<int> parentIndexCounts = new List<int>();
            List<MeshTopology> parentTopologies = new List<MeshTopology>();
            if (toSrgbTemplate != null)
            {
                parentIndexCounts.Add(3);
                parentTopologies.Add(MeshTopology.Triangles);
            }

            if (toLinearTemplate != null)
            {
                parentIndexCounts.Add(3);
                parentTopologies.Add(MeshTopology.Triangles);
            }

            if (parentIndexCounts.Count > 0)
            {
                generatedMesh = PlySplatImporter.CreateMultiPassMesh(parentIndexCounts, parentTopologies, combinedBounds);
                generatedMesh = PlySplatImporter.CreateOrReplaceAsset(generatedMesh, combinedFolderPath + "/" + assetPrefix + "_CombinedConversionMesh.asset");
            }
        }

        if (EnsureCombinedRendererObject(generatedMaterials.ToArray(), generatedMesh, templateRenderer))
        {
            EditorUtility.SetDirty(this);
        }
    }

    static void ComputeRequiredSortTextureSize(int elementCount, out int width, out int height)
    {
        int optimalPot = Mathf.NextPowerOfTwo(Mathf.Max(1, elementCount));
        int optimalPotLog2 = Mathf.CeilToInt(Mathf.Log(optimalPot, 2));
        int imageSizeLog2Y = optimalPotLog2 / 2;
        int imageSizeLog2X = imageSizeLog2Y + (optimalPotLog2 % 2);
        width = 1 << imageSizeLog2X;
        height = 1 << imageSizeLog2Y;
    }

    RenderTexture CreateSortRenderTextureAsset(string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        EnsureFolderExists(folderPath);

        RenderTexture renderTexture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
        renderTexture.name = assetName;
        renderTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        renderTexture.volumeDepth = volumeDepth;
        renderTexture.useMipMap = useMipMap;
        renderTexture.autoGenerateMips = false;
        renderTexture.wrapMode = TextureWrapMode.Clamp;
        renderTexture.filterMode = FilterMode.Point;
        renderTexture.enableRandomWrite = false;
        renderTexture.anisoLevel = 0;
        renderTexture.antiAliasing = 1;
        renderTexture.Create();

        string assetPath = AssetDatabase.GenerateUniqueAssetPath(folderPath + "/" + assetName + ".renderTexture");
        AssetDatabase.CreateAsset(renderTexture, assetPath);
        return renderTexture;
    }

    bool EnsureSortRenderTexture(ref RenderTexture targetTexture, string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
    {
        string normalizedFolderPath = folderPath.Replace('\\', '/');
        string expectedAssetPath = normalizedFolderPath + "/" + assetName + ".renderTexture";
        string currentAssetPath = targetTexture == null ? string.Empty : AssetDatabase.GetAssetPath(targetTexture).Replace('\\', '/');
        bool needsPackageResource = targetTexture == null || string.IsNullOrEmpty(currentAssetPath) || !currentAssetPath.StartsWith(normalizedFolderPath + "/");

        if (needsPackageResource)
        {
            EnsureFolderExists(normalizedFolderPath);
            RenderTexture existingTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(expectedAssetPath);
            targetTexture = existingTexture != null
                ? existingTexture
                : CreateSortRenderTextureAsset(normalizedFolderPath, assetName, width, height, format, useMipMap, volumeDepth);
        }

        bool needsResize = targetTexture.width != width
            || targetTexture.height != height
            || targetTexture.format != format
            || targetTexture.dimension != (volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D)
            || targetTexture.volumeDepth != volumeDepth
            || targetTexture.useMipMap != useMipMap
            || targetTexture.autoGenerateMips
            || targetTexture.wrapMode != TextureWrapMode.Clamp
            || targetTexture.filterMode != FilterMode.Point
            || targetTexture.enableRandomWrite
            || targetTexture.anisoLevel != 0
            || targetTexture.antiAliasing != 1;
        if (!needsPackageResource && !needsResize)
        {
            return false;
        }

        Undo.RecordObject(targetTexture, "Resize Gaussian Splat Sort Texture");
        targetTexture.Release();
        targetTexture.width = width;
        targetTexture.height = height;
        targetTexture.format = format;
        targetTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
        targetTexture.volumeDepth = volumeDepth;
        targetTexture.useMipMap = useMipMap;
        targetTexture.autoGenerateMips = false;
        targetTexture.wrapMode = TextureWrapMode.Clamp;
        targetTexture.filterMode = FilterMode.Point;
        targetTexture.enableRandomWrite = false;
        targetTexture.anisoLevel = 0;
        targetTexture.antiAliasing = 1;
        targetTexture.Create();
        EditorUtility.SetDirty(targetTexture);
        return true;
    }

    void UpdateSortingResourceTextures()
    {
        RadixSort radixSort = GetComponent<RadixSort>();
        if (radixSort == null)
        {
            Debug.LogError("RadixSort component not found on the GaussianSplatRenderer GameObject.");
            return;
        }

        int largestElementCount = 0;
        int combinedElementCount = 0;
        string largestSplatName = null;
        GaussianSplatObject[] sceneSplatObjects = FindSceneSplatObjects(true);
        for (int i = 0; i < sceneSplatObjects.Length; i++)
        {
            GaussianSplatObject currentSplatObject = sceneSplatObjects[i];
            if (currentSplatObject == null)
            {
                continue;
            }

            MeshRenderer renderer = GetSortedRenderer(currentSplatObject.gameObject);
            if (renderer == null)
            {
                continue;
            }

            Material[] materials = GetRendererMaterialsForRead(renderer);
            Material positionsMaterial = ResolvePrimarySplatMaterial(materials);
            if (positionsMaterial == null || !positionsMaterial.HasProperty("_GS_Positions"))
            {
                continue;
            }

            Texture positionsTexture = positionsMaterial.GetTexture("_GS_Positions");
            if (positionsTexture == null)
            {
                continue;
            }

            int textureElementCount = positionsTexture.width * positionsTexture.height;
            int actualSplatCount = positionsMaterial.HasProperty("_ActualSplatCount") ? positionsMaterial.GetInt("_ActualSplatCount") : 0;
            int elementCount = actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;

            combinedElementCount += elementCount;

            if (elementCount > largestElementCount)
            {
                largestElementCount = elementCount;
                largestSplatName = currentSplatObject.gameObject.name;
            }
        }

        if (largestElementCount <= 0)
        {
            Debug.LogWarning("No valid _GS_Positions textures were found on the scene Gaussian Splat Objects.");
            return;
        }

        int safeCombinedElementCount = Mathf.Min(combinedElementCount, MAX_COMBINED_SPLAT_COUNT);
        if (combinedElementCount > MAX_COMBINED_SPLAT_COUNT)
        {
            Debug.LogError($"Combined Gaussian splat resources are capped at {MAX_COMBINED_SPLAT_COUNT} splats, but the cached scene total is {combinedElementCount} splats.");
        }

        int requiredSortElementCount = IsCombinedRenderingMode()
            ? Mathf.Max(largestElementCount, safeCombinedElementCount)
            : largestElementCount;
        ComputeRequiredSortTextureSize(requiredSortElementCount, out int requiredWidth, out int requiredHeight);

        string resourceFolderPath = GetSortResourceFolderPath();
        string assetPrefix = SanitizeAssetName(name);

        bool resourcesChanged = false;
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues0, resourceFolderPath, assetPrefix + "_KeyValues0", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.keyValues1, resourceFolderPath, assetPrefix + "_KeyValues1", requiredWidth, requiredHeight, RenderTextureFormat.RGFloat, false, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref radixSort.prefixSums, resourceFolderPath, assetPrefix + "_PrefixSums", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, true, 1);
        resourcesChanged |= EnsureSortRenderTexture(ref splatRenderOrder, resourceFolderPath, assetPrefix + "_SplatRenderOrder", requiredWidth, requiredHeight, RenderTextureFormat.RFloat, false, 2);

        if (resourcesChanged)
        {
            EditorUtility.SetDirty(radixSort);
            EditorUtility.SetDirty(this);
            Debug.Log($"Updated sorting textures to {requiredWidth}x{requiredHeight} for up to {requiredSortElementCount} splats. Largest source '{largestSplatName}' has {largestElementCount} splats; combined cached scene total is {combinedElementCount} splats.");
        }

        if (IsCombinedRenderingMode())
        {
            UpdateCombinedResources(sceneSplatObjects, safeCombinedElementCount);
        }
        else if (combinedSortedRenderer != null && combinedSortedRenderer.gameObject.activeSelf)
        {
            Undo.RecordObject(combinedSortedRenderer.gameObject, "Toggle Combined Gaussian Splat Renderer");
            combinedSortedRenderer.gameObject.SetActive(false);
            EditorUtility.SetDirty(combinedSortedRenderer.gameObject);
            EditorUtility.SetDirty(this);
        }
    }

#endif
}

}
