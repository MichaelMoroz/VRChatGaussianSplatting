﻿using UnityEngine;
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class GaussianSplatRenderer : UdonSharpBehaviour
{
    const int MAX_CAMERA_COUNT = 3; // Screen camera + Photo camera + Mirror camera
    private Vector3[] _prevCameraPos;
    private RadixSort _radixSort;
    private MeshRenderer _meshRenderer;
    private Material keyValueMat;
    private GameObject splatObject;
    private int prevSplatObjectIndex = -1; // To track the previous splat object index

    [Header("Gaussian Splat Object")]
    [UdonSynced, Tooltip("The index of the currently rendered splat object in the splatObjects array.")]
    public int splatObjectIndex = 0; // Index of the current splat object in the splatObjects array
    [Tooltip("The GameObject that contains the Gaussian Splat mesh. This should have a MeshRenderer component with a material that uses the Gaussian Splat shader.")]
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
    [Tooltip("Render texture used to store the sorted splat render order. This should be a RenderTexture with the same dimensions as the sorting textures used in the radix sort.")]
    public RenderTexture splatRenderOrder;

    [Header("Optional Mirror")]
    [Tooltip("Optional mirror GameObject. If set, the script will also sort splats for the mirror camera position.")]
    public GameObject mirror;

    void ResetCameraPositions()
    {
        for (int i = 0; i < MAX_CAMERA_COUNT; i++)
        {
            _prevCameraPos[i] = Vector3.positiveInfinity; // Reset to a value that will always trigger an update
        }
    }

    void DeactivateSplatObjects()
    {
        for (int i = 0; i < splatObjects.Length; i++)
        {
            GameObject splatObj = splatObjects[i];
            if (splatObj != null)
            {
                splatObj.SetActive(false);
            }
        }
    }

    public void SetSplatObjectIndex(int index)
    {
        if (prevSplatObjectIndex == index)
        {
            // No change in splat object index, skip update
            return;
        }
        if (index < 0 || index >= splatObjects.Length)
        {
            Debug.LogError($"Invalid splat object index: {index}. Must be between 0 and {splatObjects.Length - 1}.");
            return;
        }

        ResetCameraPositions();
        DeactivateSplatObjects();

        prevSplatObjectIndex = splatObjectIndex; // Store the previous index before changing
        splatObjectIndex = index;
       
        splatObject = splatObjects[splatObjectIndex];
        if (splatObject == null)
        {
            Debug.LogError($"Splat object at index {splatObjectIndex} is null. Please ensure the splatObjects array is populated correctly.");
            return;
        }
        splatObject.SetActive(true); // Activate the new splat object
    }

    void Start()
    {
        VRCCameraSettings.ScreenCamera.AllowMSAA = false; // MSAA is too slow for Gaussian Splatting, disable it
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

    void UpdateMaterials()
    {
        SetSplatObjectIndex(splatObjectIndex);

        _meshRenderer = splatObject.GetComponent<MeshRenderer>();
        Material splatMat = _meshRenderer.material;
        splatMat.SetTexture("_GS_RenderOrder", splatRenderOrder);
        Texture positions = splatMat.GetTexture("_GS_Positions");
        _radixSort.elementCount = positions.width * positions.height;
        keyValueMat = _radixSort.computeKeyValues;
        keyValueMat.SetTexture("_GS_Positions", positions);
        keyValueMat.SetVector("_MinMaxSortDistance", new Vector4(minSortDistance, maxSortDistance, 0, 0));
        keyValueMat.SetMatrix("_SplatToWorld", splatObject.transform.localToWorldMatrix);
    }

    void SortCamera(Vector3 cameraPos, int cameraID, bool forceUpdate = false)
    {
        Vector3 quantizedPos = QuantizePosition(cameraPos);
        if (quantizedPos == _prevCameraPos[cameraID] && !alwaysUpdate && !forceUpdate)
        {
            // No change in camera position, skip update
            return;
        }
        //Debug.Log($"Camera {cameraID} position updated: {quantizedPos}");
        _prevCameraPos[cameraID] = quantizedPos;
        keyValueMat.SetVector("_CameraPos", cameraPos);
        _radixSort.Sort();
        // Copy the sorted results to the splat render order texture
        VRCGraphics.Blit(_radixSort.keyValues0, splatRenderOrder, 0, cameraID);
    }

    public void SortCameras(Vector3 screenCamPos) {
        UpdateMaterials();
       
        SortCamera(screenCamPos, 0);

        VRCCameraSettings photoCam = VRCCameraSettings.PhotoCamera;
        if (photoCam != null && photoCam.Active) SortCamera(photoCam.Position, 1);
        
        if (mirror != null && mirror.activeInHierarchy)
        {
            Vector3 mirrorZ = mirror.transform.forward;
            float zDist = Vector3.Dot(mirrorZ, mirror.transform.position - screenCamPos);
            if (zDist > 0)
            {
                Vector3 mirrorCamPos = screenCamPos + 2 * zDist * mirrorZ;
                _meshRenderer.material.SetVector("_MirrorCameraPos", mirrorCamPos);
                SortCamera(mirrorCamPos, 2, true);
            }
        }
    }

    void Update()
    {
        Vector3 screenCamPos = VRCCameraSettings.ScreenCamera.Position;
        SortCameras(screenCamPos);
    }
}
