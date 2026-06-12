// VRCGS_LOD_PLACEHOLDER
using UnityEngine;
using UdonSharp;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
#endif

namespace GaussianSplatting
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GaussianSplatLODObject : UdonSharpBehaviour
    {
        public const float MAX_LOD_ALPHA_LOG2 = 100.0f;
        [SerializeField] public GaussianSplatRenderer gaussianSplatRenderer;
        [SerializeField] public string splatName;
        [TextArea(1, 2)] [SerializeField] public string description;
        [SerializeField] public int chunkSize = 4096;
        [SerializeField] public int chunkCount;
        [SerializeField] public int totalSplatCount;
        [SerializeField] public bool usePackedPositions;
        [SerializeField] public float lodZeroOffset = 2.0f;
        [SerializeField] public float lodSplatRadius = 1.0f;
        [SerializeField] public float smallestChunkSize = 1.0f;
        [SerializeField] public Vector3 boundsMin;
        [SerializeField] public Vector3 boundsMax;
        [SerializeField] public Texture2D[] positions;
        [SerializeField] public Texture2D[] colors;
        [SerializeField] public Texture2D[] rotations;
        [SerializeField] public Texture2D[] scales;
        [SerializeField] public Texture2D[] sh;
        [SerializeField] public int[] fileSplatCounts;
        [SerializeField] public int[] fileShCoeffCounts;
        [SerializeField] public int[] fileShCoeffStrides;
        [SerializeField] public Vector4[] fileShMins;
        [SerializeField] public Vector4[] fileShRanges;
        [SerializeField] public Texture2D chunkBoundsMinTexture;
        [SerializeField] public Texture2D chunkBoundsMaxTexture;
        [SerializeField] public Texture2D chunkRangeTexture;
        [SerializeField] public Vector4 chunkTextureLayout;
        void Start() { }
        void OnEnable() { }
        void OnDisable() { }
        public void NotifyRendererEnabled() { }
        public bool IsRenderable() { return false; }
        public int GetChunkCount() { return 0; }
        public int GetFileCount() { return 0; }
        public Texture GetPositions(int fileIndex) { return null; }
        public Texture GetColors(int fileIndex) { return null; }
        public Texture GetRotations(int fileIndex) { return null; }
        public Texture GetScales(int fileIndex) { return null; }
        public Texture GetSH(int fileIndex) { return null; }
        public int GetFileSplatCount(int fileIndex) { return 0; }
        public int GetFileSHCoeffCount(int fileIndex) { return 0; }
        public int GetFileSHCoeffStride(int fileIndex) { return 0; }
        public Vector4 GetFileSHMin(int fileIndex) { return Vector4.zero; }
        public Vector4 GetFileSHRange(int fileIndex) { return Vector4.one; }
        public int GetMaxLOD0SplatCount() { return 0; }
        public string GetDisplayName() { return !string.IsNullOrEmpty(splatName) ? splatName : gameObject.name; }
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        void Reset() { gaussianSplatRenderer = GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene); }
        void OnValidate() { GaussianSplatRenderer.RequestEditorRefresh(); }
        public bool TryGetLocalBounds(out Bounds bounds) { bounds = new Bounds(Vector3.zero, Vector3.zero); return false; }
#endif
    }
}
