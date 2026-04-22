#if UNITY_EDITOR
using System.Collections.Generic;
using GaussianSplatting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Editor
{
    [InitializeOnLoad]
    static class GaussianSplatEditorRenderManager
    {
        const float CameraPositionQuantization = 0.1f;
        const int BitsPerPass = 4;
        const int TotalSortPasses = 8;
        const int MaxKeyBits = BitsPerPass * TotalSortPasses;
        const int GroupSizeLog2 = 4;

        static readonly Dictionary<int, GaussianSplatEditorRenderState> States = new Dictionary<int, GaussianSplatEditorRenderState>();
        static readonly List<int> RemovalBuffer = new List<int>();

        static readonly int GSPositionsId = Shader.PropertyToID("_GS_Positions");
        static readonly int GSRenderOrderId = Shader.PropertyToID("_GS_RenderOrder");
        static readonly int ActualSplatCountId = Shader.PropertyToID("_ActualSplatCount");
        static readonly int GaussianMulId = Shader.PropertyToID("_GaussianMul");
        static readonly int AlphaCutoffId = Shader.PropertyToID("_AlphaCutoff");
        static readonly int CameraPosId = Shader.PropertyToID("_CameraPos");
        static readonly int PrefixSumsId = Shader.PropertyToID("_PrefixSums");
        static readonly int KeyValuesId = Shader.PropertyToID("_KeyValues");
        static readonly int CurrentBitId = Shader.PropertyToID("_CurrentBit");
        static readonly int BitsPerStepId = Shader.PropertyToID("_BitsPerStep");
        static readonly int GroupSizeId = Shader.PropertyToID("_GroupSize");
        static readonly int ElementCountId = Shader.PropertyToID("_ElementCount");
        static readonly int ImageSizeLog2XId = Shader.PropertyToID("_ImageSizeLog2X");
        static readonly int ImageSizeLog2YId = Shader.PropertyToID("_ImageSizeLog2Y");
        static readonly int ImageElementsLog2Id = Shader.PropertyToID("_ImageElementsLog2");
        static readonly int ScaleId = Shader.PropertyToID("_Scale");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        static GaussianSplatEditorRenderManager()
        {
            Camera.onPreCull += OnCameraPreCull;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += CleanupAll;
            EditorApplication.quitting += CleanupAll;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            if (stateChange == PlayModeStateChange.ExitingEditMode || stateChange == PlayModeStateChange.EnteredPlayMode)
            {
                CleanupAll();
            }
        }

        static void OnCameraPreCull(Camera camera)
        {
            if (camera == null || Application.isPlaying || camera.cameraType != CameraType.SceneView)
            {
                return;
            }

            RenderForSceneView(camera);
        }

        static void RenderForSceneView(Camera camera)
        {
            GaussianSplatObject[] objects = Resources.FindObjectsOfTypeAll<GaussianSplatObject>();
            HashSet<int> liveIds = new HashSet<int>();

            for (int i = 0; i < objects.Length; i++)
            {
                GaussianSplatObject splatObject = objects[i];
                if (!ShouldProcessObject(splatObject))
                {
                    continue;
                }

                int instanceId = splatObject.GetInstanceID();
                liveIds.Add(instanceId);

                GaussianSplatEditorRenderState state = GetOrCreateState(instanceId);
                if (state == null)
                {
                    continue;
                }

                if (!state.Bind(splatObject))
                {
                    ReleaseState(instanceId);
                    continue;
                }

                state.UpdateForCamera(camera);
            }

            RemovalBuffer.Clear();
            foreach (KeyValuePair<int, GaussianSplatEditorRenderState> entry in States)
            {
                if (!liveIds.Contains(entry.Key))
                {
                    RemovalBuffer.Add(entry.Key);
                }
            }

            for (int i = 0; i < RemovalBuffer.Count; i++)
            {
                ReleaseState(RemovalBuffer[i]);
            }
        }

        static bool ShouldProcessObject(GaussianSplatObject splatObject)
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

            if (!splatObject.gameObject.activeInHierarchy)
            {
                return false;
            }

            return true;
        }

        static GaussianSplatEditorRenderState GetOrCreateState(int instanceId)
        {
            GaussianSplatEditorRenderState state;
            if (States.TryGetValue(instanceId, out state))
            {
                return state;
            }

            state = new GaussianSplatEditorRenderState();
            States[instanceId] = state;
            return state;
        }

        static void ReleaseState(int instanceId)
        {
            GaussianSplatEditorRenderState state;
            if (!States.TryGetValue(instanceId, out state))
            {
                return;
            }

            state.Dispose();
            States.Remove(instanceId);
        }

        static void CleanupAll()
        {
            foreach (KeyValuePair<int, GaussianSplatEditorRenderState> entry in States)
            {
                entry.Value.Dispose();
            }

            States.Clear();
            RemovalBuffer.Clear();
        }

        static Vector3 QuantizePosition(Vector3 position)
        {
            return new Vector3(
                Mathf.Round(position.x / CameraPositionQuantization) * CameraPositionQuantization,
                Mathf.Round(position.y / CameraPositionQuantization) * CameraPositionQuantization,
                Mathf.Round(position.z / CameraPositionQuantization) * CameraPositionQuantization
            );
        }

        static RenderTexture CreateRenderTexture(string name, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth = 1)
        {
            RenderTexture renderTexture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
            renderTexture.name = name;
            renderTexture.dimension = volumeDepth > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
            renderTexture.volumeDepth = volumeDepth;
            renderTexture.useMipMap = useMipMap;
            renderTexture.autoGenerateMips = false;
            renderTexture.enableRandomWrite = false;
            renderTexture.wrapMode = TextureWrapMode.Clamp;
            renderTexture.filterMode = FilterMode.Point;
            renderTexture.hideFlags = HideFlags.HideAndDontSave;
            renderTexture.Create();
            return renderTexture;
        }

        static void SafeDestroy(Object target)
        {
            if (target == null)
            {
                return;
            }

            Object.DestroyImmediate(target);
        }

        static void DrawFullscreenQuad(Material material)
        {
            if (material == null || !material.SetPass(0))
            {
                return;
            }

            GL.PushMatrix();
            GL.LoadOrtho();
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.TexCoord2(1.0f, 0.0f);
            GL.Vertex3(1.0f, 0.0f, 0.0f);
            GL.TexCoord2(1.0f, 1.0f);
            GL.Vertex3(1.0f, 1.0f, 0.0f);
            GL.TexCoord2(0.0f, 1.0f);
            GL.Vertex3(0.0f, 1.0f, 0.0f);
            GL.End();
            GL.PopMatrix();
        }

        sealed class GaussianSplatEditorRenderState
        {
            readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();

            GaussianSplatObject _splatObject;
            MeshRenderer _renderer;
            Material _sourceMaterial;
            Material _computeKeyValues;
            Material _radixSort;
            Material _copyRenderOrder;
            RenderTexture _keyValues0;
            RenderTexture _keyValues1;
            RenderTexture _prefixSums;
            RenderTexture _renderOrder;
            Vector3 _previousLocalCameraPosition = Vector3.positiveInfinity;
            int _elementCount;
            int _imageSizeX;
            int _imageSizeY;
            MaterialPropertyBlock[] _originalPropertyBlocks;

            public bool Bind(GaussianSplatObject splatObject)
            {
                _splatObject = splatObject;
                MeshRenderer resolvedRenderer = splatObject.GetSortedRenderer();
                if (resolvedRenderer == null || !resolvedRenderer.enabled || !resolvedRenderer.gameObject.activeInHierarchy)
                {
                    return false;
                }

                if (_renderer != resolvedRenderer)
                {
                    RestorePropertyOverrides();
                    _renderer = resolvedRenderer;
                    CaptureOriginalPropertyBlocks();
                    _previousLocalCameraPosition = Vector3.positiveInfinity;
                }

                _sourceMaterial = ResolveSourceMaterial(_renderer.sharedMaterials);
                if (_sourceMaterial == null)
                {
                    return false;
                }

                if (UsesPrecomputedSorting(_sourceMaterial))
                {
                    RestorePropertyOverrides();
                    return false;
                }

                Texture positions = _sourceMaterial.GetTexture(GSPositionsId);
                if (positions == null || positions.width <= 0 || positions.height <= 0)
                {
                    return false;
                }

                int textureElementCount = positions.width * positions.height;
                int actualSplatCount = _sourceMaterial.HasProperty(ActualSplatCountId) ? _sourceMaterial.GetInt(ActualSplatCountId) : 0;
                _elementCount = actualSplatCount > 0 && actualSplatCount <= textureElementCount ? actualSplatCount : textureElementCount;
                ComputeImageSize(_elementCount, out _imageSizeX, out _imageSizeY);

                EnsureMaterials();
                EnsureTextures();
                ApplySharedMaterialOverrides();
                return _computeKeyValues != null && _radixSort != null && _copyRenderOrder != null && _keyValues0 != null && _keyValues1 != null && _prefixSums != null && _renderOrder != null;
            }

            public void UpdateForCamera(Camera camera)
            {
                if (_renderer == null || camera == null)
                {
                    return;
                }

                Vector3 localCameraPosition = _renderer.transform.InverseTransformPoint(camera.transform.position);
                Vector3 quantizedLocalCameraPosition = QuantizePosition(localCameraPosition);
                if (quantizedLocalCameraPosition == _previousLocalCameraPosition)
                {
                    ApplyPropertyBlock();
                    return;
                }

                _previousLocalCameraPosition = quantizedLocalCameraPosition;

                ConfigureStaticUniforms();
                _computeKeyValues.SetVector(CameraPosId, localCameraPosition);

                Graphics.Blit(null, _keyValues0, _computeKeyValues);

                _radixSort.SetTexture(PrefixSumsId, _prefixSums);
                for (int bit = 0; bit < MaxKeyBits; bit += BitsPerPass)
                {
                    _radixSort.SetTexture(KeyValuesId, _keyValues0);
                    _radixSort.SetInt(CurrentBitId, bit);

                    Graphics.Blit(null, _prefixSums, _radixSort, 0);
                    _prefixSums.GenerateMips();
                    Graphics.Blit(null, _keyValues1, _radixSort, 1);

                    RenderTexture temp = _keyValues0;
                    _keyValues0 = _keyValues1;
                    _keyValues1 = temp;
                }

                CopySortedOrder();
                ApplyPropertyBlock();
            }

            public void Dispose()
            {
                RestorePropertyOverrides();
                SafeDestroy(_computeKeyValues);
                SafeDestroy(_radixSort);
                SafeDestroy(_copyRenderOrder);
                SafeDestroy(_keyValues0);
                SafeDestroy(_keyValues1);
                SafeDestroy(_prefixSums);
                SafeDestroy(_renderOrder);
                _computeKeyValues = null;
                _radixSort = null;
                _copyRenderOrder = null;
                _keyValues0 = null;
                _keyValues1 = null;
                _prefixSums = null;
                _renderOrder = null;
                _renderer = null;
                _sourceMaterial = null;
                _splatObject = null;
                _originalPropertyBlocks = null;
                _previousLocalCameraPosition = Vector3.positiveInfinity;
            }

            static Material ResolveSourceMaterial(Material[] materials)
            {
                if (materials == null)
                {
                    return null;
                }

                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material != null && material.HasProperty(GSPositionsId))
                    {
                        return material;
                    }
                }

                return null;
            }

            static bool UsesPrecomputedSorting(Material material)
            {
                if (material == null)
                {
                    return false;
                }

                return material.IsKeywordEnabled("_PRECOMPUTED_SORTING_ON");
            }

            static void ComputeImageSize(int elementCount, out int width, out int height)
            {
                int optimalPot = Mathf.NextPowerOfTwo(Mathf.CeilToInt(elementCount));
                int optimalPotLog2 = Mathf.CeilToInt(Mathf.Log(optimalPot, 2));
                int imageSizeLog2Y = optimalPotLog2 / 2;
                int imageSizeLog2X = imageSizeLog2Y + (optimalPotLog2 % 2);
                width = 1 << imageSizeLog2X;
                height = 1 << imageSizeLog2Y;
            }

            void EnsureMaterials()
            {
                if (_computeKeyValues == null)
                {
                    Shader shader = Shader.Find("VRChatGaussianSplatting/ComputeKeyValue");
                    if (shader != null)
                    {
                        _computeKeyValues = new Material(shader);
                        _computeKeyValues.name = "GaussianSplatEditorComputeKeyValue";
                        _computeKeyValues.hideFlags = HideFlags.HideAndDontSave;
                    }
                }

                if (_radixSort == null)
                {
                    Shader shader = Shader.Find("Misha/RadixSort");
                    if (shader != null)
                    {
                        _radixSort = new Material(shader);
                        _radixSort.name = "GaussianSplatEditorRadixSort";
                        _radixSort.hideFlags = HideFlags.HideAndDontSave;
                    }
                }

                if (_copyRenderOrder == null)
                {
                    Shader shader = Shader.Find("Hidden/GaussianSplatting/CopyRenderOrder");
                    if (shader != null)
                    {
                        _copyRenderOrder = new Material(shader);
                        _copyRenderOrder.name = "GaussianSplatEditorCopyRenderOrder";
                        _copyRenderOrder.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
            }

            void EnsureTextures()
            {
                if (_keyValues0 != null && (_keyValues0.width != _imageSizeX || _keyValues0.height != _imageSizeY))
                {
                    SafeDestroy(_keyValues0);
                    _keyValues0 = null;
                }

                if (_keyValues1 != null && (_keyValues1.width != _imageSizeX || _keyValues1.height != _imageSizeY))
                {
                    SafeDestroy(_keyValues1);
                    _keyValues1 = null;
                }

                if (_prefixSums != null && (_prefixSums.width != _imageSizeX || _prefixSums.height != _imageSizeY))
                {
                    SafeDestroy(_prefixSums);
                    _prefixSums = null;
                }

                if (_renderOrder != null && (_renderOrder.width != _imageSizeX || _renderOrder.height != _imageSizeY || _renderOrder.dimension != TextureDimension.Tex2DArray || _renderOrder.volumeDepth < 2))
                {
                    SafeDestroy(_renderOrder);
                    _renderOrder = null;
                }

                if (_keyValues0 == null)
                {
                    _keyValues0 = CreateRenderTexture("GaussianSplatEditorKeyValue0", _imageSizeX, _imageSizeY, RenderTextureFormat.RGFloat, false);
                }

                if (_keyValues1 == null)
                {
                    _keyValues1 = CreateRenderTexture("GaussianSplatEditorKeyValue1", _imageSizeX, _imageSizeY, RenderTextureFormat.RGFloat, false);
                }

                if (_prefixSums == null)
                {
                    _prefixSums = CreateRenderTexture("GaussianSplatEditorPrefixSums", _imageSizeX, _imageSizeY, RenderTextureFormat.RFloat, true);
                }

                if (_renderOrder == null)
                {
                    _renderOrder = CreateRenderTexture("GaussianSplatEditorRenderOrder", _imageSizeX, _imageSizeY, RenderTextureFormat.RFloat, false, 2);
                }
            }

            void ConfigureStaticUniforms()
            {
                int optimalPot = Mathf.NextPowerOfTwo(Mathf.CeilToInt(_elementCount));
                int optimalPotLog2 = Mathf.CeilToInt(Mathf.Log(optimalPot, 2));
                int imageSizeLog2Y = optimalPotLog2 / 2;
                int imageSizeLog2X = imageSizeLog2Y + (optimalPotLog2 % 2);
                Vector2 scale = new Vector2((float)_imageSizeX / _keyValues0.width, (float)_imageSizeY / _keyValues0.height);

                _computeKeyValues.SetInt(BitsPerStepId, BitsPerPass);
                _computeKeyValues.SetInt(GroupSizeId, GroupSizeLog2);
                _computeKeyValues.SetInt(ElementCountId, _elementCount);
                _computeKeyValues.SetInt(ImageSizeLog2XId, imageSizeLog2X);
                _computeKeyValues.SetInt(ImageSizeLog2YId, imageSizeLog2Y);
                _computeKeyValues.SetInt(ImageElementsLog2Id, optimalPotLog2);
                _computeKeyValues.SetVector(ScaleId, scale);

                _radixSort.SetInt(BitsPerStepId, BitsPerPass);
                _radixSort.SetInt(GroupSizeId, GroupSizeLog2);
                _radixSort.SetInt(ElementCountId, _elementCount);
                _radixSort.SetInt(ImageSizeLog2XId, imageSizeLog2X);
                _radixSort.SetInt(ImageSizeLog2YId, imageSizeLog2Y);
                _radixSort.SetInt(ImageElementsLog2Id, optimalPotLog2);
                _radixSort.SetVector(ScaleId, scale);
            }

            void ApplySharedMaterialOverrides()
            {
                if (_sourceMaterial == null || _computeKeyValues == null)
                {
                    return;
                }

                Texture positions = _sourceMaterial.GetTexture(GSPositionsId);
                if (positions == null)
                {
                    return;
                }

                _computeKeyValues.SetTexture(GSPositionsId, positions);
            }

            void CopySortedOrder()
            {
                if (_copyRenderOrder == null || _keyValues0 == null || _renderOrder == null)
                {
                    return;
                }

                _copyRenderOrder.SetTexture(MainTexId, _keyValues0);

                RenderTexture active = RenderTexture.active;
                for (int slice = 0; slice < 2; slice++)
                {
                    Graphics.SetRenderTarget(_renderOrder, 0, CubemapFace.Unknown, slice);
                    GL.Clear(false, true, Color.clear);
                    DrawFullscreenQuad(_copyRenderOrder);
                }
                RenderTexture.active = active;
            }

            void ApplyPropertyBlock()
            {
                if (_renderer == null || _sourceMaterial == null || _renderOrder == null)
                {
                    return;
                }

                Material[] sharedMaterials = _renderer.sharedMaterials;
                if (sharedMaterials == null)
                {
                    return;
                }

                float gaussianMul = _sourceMaterial.HasProperty(GaussianMulId) ? _sourceMaterial.GetFloat(GaussianMulId) : 1.0f;
                float alphaCutoff = _sourceMaterial.HasProperty(AlphaCutoffId) ? _sourceMaterial.GetFloat(AlphaCutoffId) : 0.03f;

                for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                {
                    Material sharedMaterial = sharedMaterials[materialIndex];
                    if (sharedMaterial == null || !sharedMaterial.HasProperty(GSRenderOrderId))
                    {
                        continue;
                    }

                    _renderer.GetPropertyBlock(_propertyBlock, materialIndex);
                    _propertyBlock.SetTexture(GSRenderOrderId, _renderOrder);
                    if (sharedMaterial.HasProperty(GaussianMulId))
                    {
                        _propertyBlock.SetFloat(GaussianMulId, gaussianMul);
                    }
                    if (sharedMaterial.HasProperty(AlphaCutoffId))
                    {
                        _propertyBlock.SetFloat(AlphaCutoffId, alphaCutoff);
                    }
                    _renderer.SetPropertyBlock(_propertyBlock, materialIndex);
                    _propertyBlock.Clear();
                }
            }

            void CaptureOriginalPropertyBlocks()
            {
                if (_renderer == null)
                {
                    return;
                }

                Material[] sharedMaterials = _renderer.sharedMaterials;
                if (sharedMaterials == null)
                {
                    return;
                }

                _originalPropertyBlocks = new MaterialPropertyBlock[sharedMaterials.Length];
                for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                {
                    MaterialPropertyBlock originalBlock = new MaterialPropertyBlock();
                    _renderer.GetPropertyBlock(originalBlock, materialIndex);
                    _originalPropertyBlocks[materialIndex] = originalBlock;
                }
            }

            void RestorePropertyOverrides()
            {
                if (_renderer == null)
                {
                    return;
                }

                Material[] sharedMaterials = _renderer.sharedMaterials;
                if (sharedMaterials == null)
                {
                    return;
                }

                if (_originalPropertyBlocks == null || _originalPropertyBlocks.Length != sharedMaterials.Length)
                {
                    for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                    {
                        _renderer.SetPropertyBlock(null, materialIndex);
                    }
                    return;
                }

                for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                {
                    _renderer.SetPropertyBlock(_originalPropertyBlocks[materialIndex], materialIndex);
                }
            }
        }
    }
}
#endif
