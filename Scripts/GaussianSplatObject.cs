using UnityEngine;
using UdonSharp;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
#endif

namespace GaussianSplatting
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GaussianSplatObject : UdonSharpBehaviour
    {
        public const float MAX_LOD_ALPHA_LOG2 = 100.0f;

        [SerializeField, HideInInspector] public GaussianSplatRenderer gaussianSplatRenderer;
        [SerializeField] public string splatName;
        [TextArea(1, 2)] [SerializeField] public string description;
        [SerializeField, HideInInspector] public string importMetadataJson;   // JSON import settings for re-import (editor)

        [Header("LOD Selection")]
        [SerializeField, HideInInspector] public int chunkSize = 4096;
        [SerializeField, HideInInspector] public int chunkCount;
        [SerializeField, HideInInspector] public int totalSplatCount;
        [SerializeField, HideInInspector] public bool usePackedPositions;
        [SerializeField, HideInInspector] public int lodReusePercent = 50;
        [SerializeField, HideInInspector] public float lodZeroOffset = 2.0f;
        [SerializeField, HideInInspector] public float lodSplatRadius = 1.0f;
        [SerializeField, HideInInspector] public float smallestChunkSize = 1.0f;
        [SerializeField, HideInInspector] public Vector3 boundsMin;
        [SerializeField, HideInInspector] public Vector3 boundsMax;

        [Header("Source Textures")]
        [SerializeField, HideInInspector] public Texture2D[] positions;
        [SerializeField, HideInInspector] public Texture2D[] colors;
        [SerializeField, HideInInspector] public Texture2D[] rotations;
        [SerializeField, HideInInspector] public Texture2D[] scales;
        [SerializeField, HideInInspector] public Texture2D[] sh;
        [SerializeField, HideInInspector] public int[] fileSplatCounts;
        [SerializeField, HideInInspector] public int[] fileShCoeffCounts;
        [SerializeField, HideInInspector] public int[] fileShCoeffStrides;
        [SerializeField, HideInInspector] public Vector4[] fileShMins;
        [SerializeField, HideInInspector] public Vector4[] fileShRanges;

        [Header("Chunk Metadata")]
        [SerializeField, HideInInspector] public Texture2D chunkBoundsMinTexture;
        [SerializeField, HideInInspector] public Texture2D chunkBoundsMaxTexture;
        [SerializeField, HideInInspector] public Texture2D chunkRangeTexture;
        [SerializeField, HideInInspector] public Vector4 chunkTextureLayout;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        void Reset()
        {
            gaussianSplatRenderer = GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);
        }

        void OnValidate()
        {
            chunkSize = Mathf.Max(1, chunkSize);
            chunkCount = Mathf.Max(0, chunkCount);
            totalSplatCount = Mathf.Max(0, totalSplatCount);
            lodReusePercent = NormalizeLodReusePercent(lodReusePercent);
            lodZeroOffset = Mathf.Max(0.0f, lodZeroOffset);
            lodSplatRadius = Mathf.Max(0.001f, lodSplatRadius);
            smallestChunkSize = Mathf.Max(0.001f, smallestChunkSize);
            // OnValidate also fires when Unity loads/deserializes a prefab ASSET (e.g. selecting it in the
            // Project window). A persistent asset is not in any scene, so skip the scene refresh - it is pure
            // waste there and contributes to slow asset selection.
            if (!EditorUtility.IsPersistent(this))
            {
                GaussianSplatRenderer.RequestEditorRefresh();
            }
        }
#endif

        void Start()
        {
            NotifyRendererEnabled();
        }

        void OnEnable()
        {
            NotifyRendererEnabled();
        }

        void OnDisable()
        {
            if (gaussianSplatRenderer != null)
            {
                gaussianSplatRenderer.NotifyLODObjectDisabled(this);
            }
        }

        GaussianSplatRenderer ResolveSceneRendererReference()
        {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (gaussianSplatRenderer != null && gaussianSplatRenderer.gameObject != null && gaussianSplatRenderer.gameObject.scene == gameObject.scene)
            {
                return gaussianSplatRenderer;
            }

            return GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);
#else
            if (gaussianSplatRenderer == null)
            {
                GameObject rendererObject = GameObject.Find("GaussianSplatRenderer");
                if (rendererObject != null)
                {
                    gaussianSplatRenderer = rendererObject.GetComponent<GaussianSplatRenderer>();
                }
            }
            return gaussianSplatRenderer;
#endif
        }

        public void NotifyRendererEnabled()
        {
            GaussianSplatRenderer sceneRenderer = ResolveSceneRendererReference();
            if (sceneRenderer != null && gameObject.activeInHierarchy)
            {
                sceneRenderer.NotifyLODObjectEnabled(this);
            }
        }

        public bool IsRenderable()
        {
            int fileCount = positions != null ? positions.Length : 0;
            return chunkSize > 0
                && chunkCount > 0
                && totalSplatCount > 0
                && positions != null
                && colors != null
                && rotations != null
                && scales != null
                && fileSplatCounts != null
                && chunkBoundsMinTexture != null
                && chunkBoundsMaxTexture != null
                && chunkRangeTexture != null
                && chunkTextureLayout.z >= chunkCount
                && colors.Length >= fileCount
                && rotations.Length >= fileCount
                && scales.Length >= fileCount
                && fileSplatCounts.Length >= fileCount;
        }

        public int GetChunkCount()
        {
            return chunkCount > 0 ? chunkCount : Mathf.RoundToInt(chunkTextureLayout.z);
        }

        public int GetFileCount()
        {
            return positions != null ? positions.Length : 0;
        }

        public Texture GetPositions(int fileIndex) { return positions != null && fileIndex >= 0 && fileIndex < positions.Length ? positions[fileIndex] : null; }
        public Texture GetColors(int fileIndex) { return colors != null && fileIndex >= 0 && fileIndex < colors.Length ? colors[fileIndex] : null; }
        public Texture GetRotations(int fileIndex) { return rotations != null && fileIndex >= 0 && fileIndex < rotations.Length ? rotations[fileIndex] : null; }
        public Texture GetScales(int fileIndex) { return scales != null && fileIndex >= 0 && fileIndex < scales.Length ? scales[fileIndex] : null; }
        public Texture GetSH(int fileIndex) { return sh != null && fileIndex >= 0 && fileIndex < sh.Length ? sh[fileIndex] : null; }
        public int GetFileSplatCount(int fileIndex) { return fileSplatCounts != null && fileIndex >= 0 && fileIndex < fileSplatCounts.Length ? fileSplatCounts[fileIndex] : 0; }
        public int GetFileSHCoeffCount(int fileIndex) { return fileShCoeffCounts != null && fileIndex >= 0 && fileIndex < fileShCoeffCounts.Length ? fileShCoeffCounts[fileIndex] : 0; }
        public int GetFileSHCoeffStride(int fileIndex) { return fileShCoeffStrides != null && fileIndex >= 0 && fileIndex < fileShCoeffStrides.Length ? fileShCoeffStrides[fileIndex] : 0; }
        public Vector4 GetFileSHMin(int fileIndex) { return fileShMins != null && fileIndex >= 0 && fileIndex < fileShMins.Length ? fileShMins[fileIndex] : Vector4.zero; }
        public Vector4 GetFileSHRange(int fileIndex) { return fileShRanges != null && fileIndex >= 0 && fileIndex < fileShRanges.Length ? fileShRanges[fileIndex] : Vector4.one; }

        public int GetMaxLOD0SplatCount()
        {
            return totalSplatCount;
        }

        public int GetLodReusePercent()
        {
            return NormalizeLodReusePercent(lodReusePercent);
        }

        static int NormalizeLodReusePercent(int value)
        {
            return value <= 0 ? 50 : Mathf.Clamp(value, 1, 99);
        }

        public string GetDisplayName()
        {
            return !string.IsNullOrEmpty(splatName) ? splatName : gameObject.name;
        }

        // Max SH band carried by this object's source, from its stored coeff count (3->SH1, 8->SH2, 15->SH3).
        public int GetMaxSHBand()
        {
            int coeff = GetFileSHCoeffCount(0);
            if (coeff >= 15) return 3;
            if (coeff >= 8) return 2;
            if (coeff >= 3) return 1;
            return 0;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        public bool TryGetLocalBounds(out Bounds bounds)
        {
            Vector3 size = boundsMax - boundsMin;
            if (size.x > 0.0f && size.y > 0.0f && size.z > 0.0f)
            {
                bounds = new Bounds((boundsMin + boundsMax) * 0.5f, size);
                return true;
            }
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            return false;
        }
#endif
    }
}
