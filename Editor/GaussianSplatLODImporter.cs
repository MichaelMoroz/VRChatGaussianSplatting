#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using GaussianSplatting.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UdonSharpEditor;
using static GaussianSplatting.StreamedSplatReader;

namespace GaussianSplatting
{
    public static class GaussianSplatLODImporter
    {
        public const int DefaultChunkSize = 4096; // hard MAX per chunk (cap); variable chunks average ~half this (~2k)
        internal const int DefaultLodResamplePercent = 100;
        internal const int DefaultLodReusePercent = 50;
        const int MaxTextureSize = 8192;
        const int OffsetBase = 4096;
        const float StreamedImportProgressStart = 0.03f;

        // Variable-size chunk cut parameters (Hilbert LBVH + SAH gap-ratio). chunkSize is the hard MAX (cap).
        const int MinSplatsPerChunk = 32;          // hard min per chunk = the render mesh's splats-per-point packing
        const float LodChunkGapRatio = 0.85f;      // split a sub-cap node only when it removes empty space

        // Computed-LOD generation (the downsampled LOD pyramid) is a separate feature: GaussianSplatComputedLOD
        // registers this backend when present. The chunked (Normal / full-detail LOD0) import path never uses it.
        internal interface IComputedLodBackend
        {
            int ResolveGpuBatchChunks(int chunkSize, int shCoeffCount);
            ComputeShader LoadComputeShader();
            int ResampledLod0SplatCountForChunk(int sourceCount, int resamplePercent);
            int EstimateStoredSplatCount(int splatCount, int chunkSize, int resamplePercent, int reusePercent);
            void QueueChunk(List<PendingChunk> pendingChunks, int chunkIndex, BucketRecord[] chunkBuffer, int chunkCount, BucketImportanceComparer importanceComparer, StreamedSHContext sh);
            void FlushBatch(ComputeShader shader, List<PendingChunk> pendingChunks, int chunkSize, StreamedSetWriter writer, int resamplePercent, int reusePercent, StreamedSHContext sh);
        }

        internal static IComputedLodBackend computedLodBackend;

        internal struct ChunkInfo
        {
            public int count;
            public int textureSet;
            public int textureOffset;
            public Vector3 boundsMin;
            public Vector3 boundsMax;
            public Vector3 centerOfMass;   // mean of the chunk's splat positions (object-local)
            public float covarianceArea;   // surface area of the splat-distribution covariance ellipsoid (object-local)
        }

        internal sealed class PendingChunk
        {
            public int chunkIndex;
            public int count;
            public BucketRecord[] records;
            public float[] sh;          // raw SH aligned with records: [i*coeffCount*3 + coeff*3 + ch] (null if no SH)
            public Bounds bounds;
            public float sizeMagnitude;
        }


        // Import-global SH state threaded through the streamed write path. coeffCount 0 => no SH.
        internal sealed class StreamedSHContext
        {
            public int coeffCount;
            public Vector4 min;            // shared across all coeffs (matches non-LOD importer)
            public Vector4 range;
            public BigFloatBuffer store;   // raw f_rest, indexed by stable sourceIndex
            public Texture2D[] textures;   // per texture-set SH texture, filled as sets are saved
            public GaussianSplatImporter.SHCompression compression;   // SH texture format (None/BC1/BC7)
            public bool compressColor;     // BC7 the color/alpha texture (shared with the non-LOD path)

            // Flat-store base for a source splat, or -1 for synthetic (merged) / SH-less splats.
            public long BaseFor(uint sourceIndex)
            {
                if (coeffCount <= 0 || store == null || sourceIndex == 0xFFFFFFFFu) return -1;
                return (long)sourceIndex * coeffCount * 3;
            }
        }




        internal sealed class BucketImportanceComparer : IComparer<BucketRecord>
        {
            public int Compare(BucketRecord a, BucketRecord b)
            {
                return Importance(b).CompareTo(Importance(a));
            }

            static float Importance(BucketRecord splat)
            {
                return splat.importance;
            }
        }


        sealed class StreamedTextureSetBuilder
        {
            readonly string _dataFolder;
            readonly string _assetName;
            readonly int _setIndex;
            readonly bool _usePackedPositions;
            // Linear (write-order) accumulation; the POT texture layout is chosen from the final count at Save(),
            // so a set grows as variable-size chunks are appended without knowing its total up front. Growable
            // lists keep memory proportional to the actual content (a tiny import does not allocate a full set).
            readonly List<Color> _positionLinear;          // null when positions are packed
            readonly List<Color32> _packedPositionLinear;  // null when positions are float
            readonly List<Color32> _colorLinear;
            readonly List<Color32> _rotationLinear;
            readonly List<Color> _scaleLinear;

            // Raw quantized SH stored splat-major during accumulation ([splat*coeffCount + coeff]); remapped to the
            // file's coeff-major layout (coeff*count + localIndex) at Save(). RGB565 with the import-shared range.
            readonly int _shCoeffCount;
            readonly Vector4 _shMin;
            readonly Vector4 _shRange;
            readonly List<Color> _shLinear;
            readonly GaussianSplatImporter.SHCompression _shCompression;
            readonly bool _compressColor;
            int _count;

            public int SplatCount => _count;
            public Texture2D PositionTexture { get; private set; }
            public Texture2D ColorTexture { get; private set; }
            public Texture2D RotationTexture { get; private set; }
            public Texture2D ScaleTexture { get; private set; }
            public Texture2D SHTexture { get; private set; }

            // capacity bounds the set: rollover guarantees appended splats never exceed it, so the linear buffers
            // are allocated once. The texture is sized from the filled count at Save().
            public StreamedTextureSetBuilder(string dataFolder, string assetName, int setIndex, int capacity, bool usePackedPositions, int shCoeffCount, Vector4 shMin, Vector4 shRange, GaussianSplatImporter.SHCompression shCompression, bool compressColor)
            {
                _dataFolder = dataFolder;
                _assetName = assetName;
                _setIndex = setIndex;
                _usePackedPositions = usePackedPositions;
                int hint = Mathf.Clamp(capacity, 16, 1 << 16); // initial list capacity only; lists grow as needed
                _positionLinear = usePackedPositions ? null : new List<Color>(hint);
                _packedPositionLinear = usePackedPositions ? new List<Color32>(hint) : null;
                _colorLinear = new List<Color32>(hint);
                _rotationLinear = new List<Color32>(hint);
                _scaleLinear = new List<Color>(hint);
                _shCoeffCount = Mathf.Max(0, shCoeffCount);
                _shMin = shMin;
                _shRange = shRange;
                _shCompression = shCompression;
                _compressColor = compressColor;
                if (_shCoeffCount > 0)
                {
                    _shLinear = new List<Color>(hint * _shCoeffCount);
                }
            }

            // shSource: flat raw-SH store ([base + coeff*3 + ch]); base<0 or null -> SH left at zero for this splat.
            public void WriteSplat(int setIndex, ImportSplatData splat, Vector3 chunkMin, Vector3 chunkMax, float[] shSource, long shBase)
            {
                if (_usePackedPositions)
                {
                    _packedPositionLinear.Add(GaussianSplatImporter.EncodePosition10(splat.pos, chunkMin, chunkMax));
                }
                else
                {
                    _positionLinear.Add(new Color(splat.pos.x, splat.pos.y, splat.pos.z, 0.0f));
                }
                _colorLinear.Add(new Color(
                    Mathf.Clamp01(splat.dc0.x),
                    Mathf.Clamp01(splat.dc0.y),
                    Mathf.Clamp01(splat.dc0.z),
                    Mathf.Clamp01(splat.opacity)));
                _rotationLinear.Add(new Color(
                    Mathf.Clamp01(0.5f + 0.5f * splat.rot.x),
                    Mathf.Clamp01(0.5f + 0.5f * splat.rot.y),
                    Mathf.Clamp01(0.5f + 0.5f * splat.rot.z),
                    Mathf.Clamp01(0.5f + 0.5f * splat.rot.w)));
                _scaleLinear.Add(new Color(splat.scale.x, splat.scale.y, splat.scale.z, 0.0f));

                if (_shCoeffCount > 0 && shSource != null && shBase >= 0)
                {
                    for (int coeff = 0; coeff < _shCoeffCount; coeff++)
                    {
                        long o = shBase + coeff * 3;
                        _shLinear.Add(new Color(
                            _shRange.x > 1e-8f ? (shSource[o + 0] - _shMin.x) / _shRange.x : 0.0f,
                            _shRange.y > 1e-8f ? (shSource[o + 1] - _shMin.y) / _shRange.y : 0.0f,
                            _shRange.z > 1e-8f ? (shSource[o + 2] - _shMin.z) / _shRange.z : 0.0f,
                            0.0f));
                    }
                }
                else if (_shCoeffCount > 0)
                {
                    for (int coeff = 0; coeff < _shCoeffCount; coeff++) _shLinear.Add(default);
                }
                _count++;
            }

            public void Save()
            {
                GaussianSplatImporter.TextureLayout layout = GaussianSplatImporter.ChoosePotTextureLayout(Mathf.Max(1, _count));
                int w = layout.Width;
                string setName = _assetName + "_set" + _setIndex;
                PositionTexture = NewTexture(layout.Width, layout.Height, _usePackedPositions ? TextureFormat.RGBA32 : TextureFormat.RGBAFloat, setName + "_xyz");
                ColorTexture = NewTexture(layout.Width, layout.Height, TextureFormat.RGBA32, setName + "_color_dc");
                RotationTexture = NewTexture(layout.Width, layout.Height, TextureFormat.RGBA32, setName + "_rotation");
                ScaleTexture = NewTexture(layout.Width, layout.Height, TextureFormat.RGB9e5Float, setName + "_scale");

                // Remap from linear write order to the 4x4-block POT texel layout chosen above.
                if (_usePackedPositions)
                {
                    Color32[] px = new Color32[layout.Capacity];
                    for (int i = 0; i < _count; i++) px[GaussianSplatImporter.ComputePackedTextureIndex(i, w)] = _packedPositionLinear[i];
                    PositionTexture.SetPixels32(px);
                }
                else
                {
                    Color[] px = new Color[layout.Capacity];
                    for (int i = 0; i < _count; i++) px[GaussianSplatImporter.ComputePackedTextureIndex(i, w)] = _positionLinear[i];
                    PositionTexture.SetPixels(px);
                }

                Color32[] col = new Color32[layout.Capacity];
                Color32[] rot = new Color32[layout.Capacity];
                Color[] scl = new Color[layout.Capacity];
                for (int i = 0; i < _count; i++)
                {
                    int packed = GaussianSplatImporter.ComputePackedTextureIndex(i, w);
                    col[packed] = _colorLinear[i];
                    rot[packed] = _rotationLinear[i];
                    scl[packed] = _scaleLinear[i];
                }
                ColorTexture.SetPixels32(col);
                RotationTexture.SetPixels32(rot);
                ScaleTexture.SetPixels(scl);

                PositionTexture.Apply(false, true);
                GaussianSplatImporter.ApplyTexture(ColorTexture, _compressColor);   // shared color/alpha BC7 (matches non-LOD)
                RotationTexture.Apply(false, true);
                ScaleTexture.Apply(false, true);
                PositionTexture = GaussianSplatImporter.CreateOrReplaceAsset(PositionTexture, _dataFolder + "/" + PositionTexture.name + ".asset");
                ColorTexture = GaussianSplatImporter.CreateOrReplaceAsset(ColorTexture, _dataFolder + "/" + ColorTexture.name + ".asset");
                RotationTexture = GaussianSplatImporter.CreateOrReplaceAsset(RotationTexture, _dataFolder + "/" + RotationTexture.name + ".asset");
                ScaleTexture = GaussianSplatImporter.CreateOrReplaceAsset(ScaleTexture, _dataFolder + "/" + ScaleTexture.name + ".asset");
                if (_shCoeffCount > 0)
                {
                    GaussianSplatImporter.TextureLayout shLayout = GaussianSplatImporter.ChoosePotTextureLayout(_count * _shCoeffCount);
                    int sw = shLayout.Width;
                    Color[] shPix = new Color[shLayout.Capacity];
                    for (int i = 0; i < _count; i++)
                        for (int coeff = 0; coeff < _shCoeffCount; coeff++)
                            shPix[GaussianSplatImporter.ComputePackedTextureIndex(coeff * _count + i, sw)] = _shLinear[i * _shCoeffCount + coeff];
                    SHTexture = NewTexture(shLayout.Width, shLayout.Height, TextureFormat.RGB565, setName + "_sh");
                    SHTexture.SetPixels(shPix);
                    GaussianSplatImporter.ApplyShTextureCompression(SHTexture, _shCompression);   // shared SH None/BC1/BC7 (matches non-LOD)
                    SHTexture = GaussianSplatImporter.CreateOrReplaceAsset(SHTexture, _dataFolder + "/" + SHTexture.name + ".asset");
                }
            }
        }

        // Accumulates variable-size chunks into texture sets, rolling to a new set when the running splat count
        // would exceed the per-set budget. Chunk count is unknown up front, so sets, chunk metadata, and the
        // per-set splat counts are all appended (no pre-pass). One (textureSet, textureOffset, count) per chunk
        // addresses position/color/rotation/scale (shared texel index) and SH (coeff*setSplatCount + offset).
        internal sealed class StreamedSetWriter
        {
            readonly string _dataFolder;
            readonly string _assetName;
            readonly bool _usePackedPositions;
            readonly StreamedSHContext _sh;
            readonly int _setBudget;

            StreamedTextureSetBuilder _set;
            int _setIndex = -1;
            int _setOffset;

            public readonly List<ChunkInfo> Chunks = new List<ChunkInfo>();
            public readonly List<int> FileSplatCounts = new List<int>();
            public readonly List<Texture2D> Positions = new List<Texture2D>();
            public readonly List<Texture2D> Colors = new List<Texture2D>();
            public readonly List<Texture2D> Rotations = new List<Texture2D>();
            public readonly List<Texture2D> Scales = new List<Texture2D>();
            public readonly List<Texture2D> SH = new List<Texture2D>();
            public float SmallestChunkSize = float.PositiveInfinity;
            public int TotalLod0SplatCount;

            public StreamedSetWriter(string dataFolder, string assetName, bool usePackedPositions, StreamedSHContext sh, int setBudget)
            {
                _dataFolder = dataFolder;
                _assetName = assetName;
                _usePackedPositions = usePackedPositions;
                _sh = sh;
                _setBudget = Mathf.Max(1, setBudget);
            }

            // Ensure the current set has room for a whole chunk of storedCount splats (a chunk never spans sets).
            void EnsureRoom(int storedCount)
            {
                if (_set != null && _setOffset + storedCount > _setBudget)
                {
                    SaveCurrent();
                }
                if (_set == null)
                {
                    _setIndex++;
                    _set = new StreamedTextureSetBuilder(_dataFolder, _assetName, _setIndex, Mathf.Min(_setBudget, storedCount * 2), _usePackedPositions, _sh.coeffCount, _sh.min, _sh.range, _sh.compression, _sh.compressColor);
                    _setOffset = 0;
                }
            }

            void SaveCurrent()
            {
                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD", $"Saving texture set {_setIndex + 1}", 0.84f);
                _set.Save();
                Positions.Add(_set.PositionTexture);
                Colors.Add(_set.ColorTexture);
                Rotations.Add(_set.RotationTexture);
                Scales.Add(_set.ScaleTexture);
                SH.Add(_set.SHTexture);
                FileSplatCounts.Add(_set.SplatCount);
                _set = null;
            }

            public void Finish()
            {
                if (_set != null) SaveCurrent();
            }

            // Non-computed-LOD chunk: importance-sort, gather SH by stable sourceIndex, write, record metadata.
            public void WriteChunk(BucketRecord[] buffer, int count, BucketImportanceComparer importanceComparer)
            {
                ComputeChunkImportance(buffer, count);
                Array.Sort(buffer, 0, count, importanceComparer);
                Bounds bounds = new Bounds(PositionOf(buffer[0]), Vector3.zero);
                for (int i = 0; i < count; i++) bounds.Encapsulate(PositionOf(buffer[i]));
                ChunkCenterAndArea(buffer, count, out Vector3 center, out float covArea);

                int shPer = (_sh != null && _sh.coeffCount > 0) ? _sh.coeffCount * 3 : 0;
                float[] chunkSH = null;
                if (shPer > 0 && _sh.store != null)
                {
                    chunkSH = new float[(long)count * shPer];
                    for (int i = 0; i < count; i++)
                    {
                        long b = _sh.BaseFor(buffer[i].sourceIndex);
                        if (b >= 0) _sh.store.CopyTo(b, chunkSH, i * shPer, shPer);
                    }
                }

                EnsureRoom(count);
                for (int i = 0; i < count; i++)
                {
                    long shBase = chunkSH != null ? (long)i * shPer : -1;
                    _set.WriteSplat(_setOffset + i, buffer[i].ToSplat(), bounds.min, bounds.max, chunkSH, shBase);
                }
                Chunks.Add(new ChunkInfo { count = count, textureSet = _setIndex, textureOffset = _setOffset, boundsMin = bounds.min, boundsMax = bounds.max, centerOfMass = center, covarianceArea = covArea });
                _setOffset += count;
                TotalLod0SplatCount += count;
                SmallestChunkSize = Mathf.Min(SmallestChunkSize, bounds.size.magnitude);
            }

            // Computed-LOD chunk: LOD0 records + merged lower-LOD levels are already prepared (SH travels with them).
            public void WritePrepared(PendingChunk pendingChunk, List<BucketRecord[]> mergedLevels, List<float[]> mergedSHLevels)
            {
                int shPer = (_sh != null && _sh.coeffCount > 0) ? _sh.coeffCount * 3 : 0;
                int storedCount = pendingChunk.count;
                for (int level = 0; level < mergedLevels.Count; level++) storedCount += mergedLevels[level].Length;
                ChunkCenterAndArea(pendingChunk.records, pendingChunk.count, out Vector3 center, out float covArea);

                EnsureRoom(storedCount);
                int written = 0;
                for (int i = 0; i < pendingChunk.count; i++)
                {
                    long shBase = (shPer > 0 && pendingChunk.sh != null) ? (long)i * shPer : -1;
                    _set.WriteSplat(_setOffset + written, pendingChunk.records[i].ToSplat(), pendingChunk.bounds.min, pendingChunk.bounds.max, pendingChunk.sh, shBase);
                    written++;
                }
                for (int level = 0; level < mergedLevels.Count; level++)
                {
                    BucketRecord[] merged = mergedLevels[level];
                    float[] levelSH = (mergedSHLevels != null && level < mergedSHLevels.Count) ? mergedSHLevels[level] : null;
                    for (int i = 0; i < merged.Length; i++)
                    {
                        long shBase = (shPer > 0 && levelSH != null) ? (long)i * shPer : -1;
                        _set.WriteSplat(_setOffset + written, merged[i].ToSplat(), pendingChunk.bounds.min, pendingChunk.bounds.max, levelSH, shBase);
                        written++;
                    }
                }
                Chunks.Add(new ChunkInfo { count = pendingChunk.count, textureSet = _setIndex, textureOffset = _setOffset, boundsMin = pendingChunk.bounds.min, boundsMax = pendingChunk.bounds.max, centerOfMass = center, covarianceArea = covArea });
                _setOffset += storedCount;
                TotalLod0SplatCount += pendingChunk.count;
                SmallestChunkSize = Mathf.Min(SmallestChunkSize, pendingChunk.sizeMagnitude);
            }
        }

        public static GameObject ImportLOD(string sourcePath, string outputFolder, int chunkSize)
        {
            GaussianSplatImporter.ImportOptions options = default;
            options.lodUsePackedPositions = true;
            options.importSphericalHarmonics = true;
            options.defaultSHBand = SHBand.SH3;   // bare convenience overload imports the full detected SH band
            return ImportLOD(sourcePath, outputFolder, chunkSize, options);
        }

        public static GameObject ImportLOD(string sourcePath, string outputFolder, int chunkSize, GaussianSplatImporter.ImportOptions options)
        {
            string assetName = GaussianSplatImporter.SanitizeAssetName(Path.GetFileNameWithoutExtension(sourcePath));
            string prefabPath = outputFolder.TrimEnd('/', '\\') + "/" + assetName + ".prefab";
            return ImportLODToPrefab(sourcePath, prefabPath, chunkSize, options);
        }

        public static GameObject ImportLODToPrefab(string sourcePath, string prefabPath, int chunkSize, GaussianSplatImporter.ImportOptions options)
        {
            chunkSize = Mathf.Max(1, chunkSize);
            return ImportLODStreamed(sourcePath, prefabPath, chunkSize, options);
        }

        static GameObject ImportLODStreamed(string sourcePath, string prefabPath, int chunkSize, GaussianSplatImporter.ImportOptions options)
        {
            if (options.lodComputeSplats && computedLodBackend == null)
            {
                throw new InvalidOperationException("Computed LOD backend is not registered; cannot build the LOD pyramid.");
            }
            prefabPath = prefabPath.Replace('\\', '/');
            string outputFolder = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(outputFolder))
            {
                outputFolder = "Assets";
                prefabPath = outputFolder + "/" + Path.GetFileName(prefabPath);
            }
            string assetName = GaussianSplatImporter.SanitizeAssetName(Path.GetFileNameWithoutExtension(prefabPath));
            string dataFolder = outputFolder.TrimEnd('/', '\\') + "/" + assetName;
            GaussianSplatImporter.EnsureFolderExists(outputFolder);
            GaussianSplatImporter.EnsureFolderExists(dataFolder);

            string tempFolder = Path.Combine(Application.temporaryCachePath, "GaussianSplatLOD_" + assetName + "_" + DateTime.Now.Ticks.ToString());
            Directory.CreateDirectory(tempFolder);
            try
            {
                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD", "Scanning bounds", StreamedImportProgressStart);
                var _sw = System.Diagnostics.Stopwatch.StartNew();
                long _tBounds = 0, _tNormalize = 0, _tBucketing = 0, _tBuild = 0, _tWrite = 0, _tFinish = 0, _tPrefab = 0, _m;
                string resolvedPlyPath = GaussianSplatImporter.ResolveSourceToPlyPath(sourcePath, tempFolder);
                PLYLayout layout = ReadPLYLayout(resolvedPlyPath);
                _m = _sw.ElapsedMilliseconds;
                Bounds bounds = StreamBounds(resolvedPlyPath, layout, options, out int splatCount, out Vector3 centroid);
                _tBounds = _sw.ElapsedMilliseconds - _m;
                if (splatCount <= 0)
                {
                    throw new InvalidOperationException($"Import aborted: crop bounds exclude all splats in '{Path.GetFileName(sourcePath)}'.");
                }
                float normalizeScale = 1.0f;
                if (options.normalizeSize)
                {
                    EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD", "Measuring normalize extent", 0.025f);
                    _m = _sw.ElapsedMilliseconds;
                    normalizeScale = ComputeNormalizeScale(resolvedPlyPath, layout, options, centroid, bounds, splatCount);
                    bounds.SetMinMax((bounds.min - centroid) * normalizeScale + centroid, (bounds.max - centroid) * normalizeScale + centroid);
                    _tNormalize = _sw.ElapsedMilliseconds - _m;
                }
                chunkSize = ResolveChunkSize(chunkSize, splatCount);
                int lodResamplePercent = options.lodComputeSplats ? NormalizeLodResamplePercent(options.lodResamplePercent) : DefaultLodResamplePercent;
                int lodReusePercent = options.lodComputeSplats ? NormalizeLodReusePercent(options.lodReusePercent) : DefaultLodReusePercent;
                // SH is capped to the requested Max SH Band (shared with the non-LOD path), then stepped down to the
                // highest band whose stored SH fits within ~2 8K textures (storedSplats*coeffCount <= 2*8192^2).
                // Beyond that the SH textures need many GB and the per-cluster averaging is prohibitively slow, so
                // huge splats (e.g. 12M+ at SH3) import at a lower band, or DC-only when not even SH1 fits.
                int requestedShCoeffCount = options.importSphericalHarmonics ? GaussianSplatImporter.SHCoeffCountForBand(options.defaultSHBand) : 0;
                int availableShCoeffCount = Mathf.Min(layout.shCoeffCount, requestedShCoeffCount);
                if (options.importSphericalHarmonics)
                {
                    GaussianSplatImporter.WarnSHBandLimitedBySource(Path.GetFileName(sourcePath), options.defaultSHBand, layout.shCoeffCount);
                }
                int shStoredEstimate = EstimateStoredSplatCount(splatCount, chunkSize, options.lodComputeSplats, lodResamplePercent, lodReusePercent);
                SHBand cappedShBand = GaussianSplatImporter.ResolveLODImportSHBand(availableShCoeffCount, shStoredEstimate);
                int shCoeffCount = GaussianSplatImporter.SHCoeffCountForBand(cappedShBand);
                if (availableShCoeffCount > shCoeffCount)
                {
                    SHBand availableBand = GaussianSplatImporter.SHBandForCoeffCount(availableShCoeffCount);
                    string outcome = shCoeffCount > 0
                        ? $"Importing at {cappedShBand} ({shCoeffCount} coefficients) instead."
                        : "No SH band fits at this stored splat count, so this imports DC-only color.";
                    string lodNote = options.lodComputeSplats
                        ? $" Computed LOD raises the stored count above the {splatCount:N0} source splats; without it the import stores {splatCount:N0}."
                        : string.Empty;
                    Debug.LogWarning($"[GaussianSplatLOD] '{Path.GetFileName(sourcePath)}': {availableBand} needs {shStoredEstimate:N0} stored splats x {availableShCoeffCount} coefficients = {(long)shStoredEstimate * availableShCoeffCount:N0} SH texels, over the import cap of {GaussianSplatImporter.MaxLODImportSHTexels:N0}. {outcome}{lodNote}");
                }
                BigFloatBuffer shStore = shCoeffCount > 0 ? new BigFloatBuffer((long)splatCount * shCoeffCount * 3) : null;
                int bucketBits = ResolveStreamedBucketBits(splatCount);
                int bucketCount = 1 << bucketBits;
                string[] bucketPaths = new string[bucketCount];
                long[] bucketCounts = new long[bucketCount];
                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD", $"Partitioning {bucketCount} Hilbert buckets", 0.1f);
                _m = _sw.ElapsedMilliseconds;
                WriteHilbertBuckets(resolvedPlyPath, layout, bounds, options, tempFolder, bucketBits, bucketPaths, bucketCounts, shCoeffCount, shStore, centroid, normalizeScale);
                _tBucketing = _sw.ElapsedMilliseconds - _m;

                int cap = chunkSize; // ResolveChunkSize already applied above; chunkSize is now the hard MAX per chunk
                int maxLod0SplatsPerChunk = options.lodComputeSplats ? computedLodBackend.ResampledLod0SplatCountForChunk(cap, lodResamplePercent) : cap;
                // Cap a texture set so both the position and (coeffCount x larger) SH textures stay within MaxTextureSize^2.
                int perSetSplatBudget = (MaxTextureSize * MaxTextureSize) / Mathf.Max(1, shCoeffCount);
                int computeStride = cap + MinSplatsPerChunk; // merged chunks can slightly exceed cap; size compute slots for that

                StreamedSHContext sh = new StreamedSHContext { coeffCount = shCoeffCount, store = shStore, textures = null, min = Vector4.zero, range = Vector4.one, compression = options.shCompression, compressColor = options.compressColorAlphaToBC7 };
                if (shCoeffCount > 0)
                {
                    ComputeSharedSHRange(shStore, out sh.min, out sh.range);
                }

                StreamedSetWriter writer = new StreamedSetWriter(dataFolder, assetName, options.lodUsePackedPositions, sh, perSetSplatBudget);
                BucketKeyComparer keyComparer = new BucketKeyComparer();
                BucketImportanceComparer importanceComparer = new BucketImportanceComparer();
                int computedLodGpuBatchChunks = options.lodComputeSplats ? computedLodBackend.ResolveGpuBatchChunks(computeStride, shCoeffCount) : 0;
                List<PendingChunk> pendingComputeChunks = options.lodComputeSplats ? new List<PendingChunk>(computedLodGpuBatchChunks) : null;
                ComputeShader lodComputeShader = options.lodComputeSplats ? computedLodBackend.LoadComputeShader() : null;

                // Quantize positions for the 21-bit Morton key with a single UNIFORM (isotropic) world scale, so
                // Morton cells are world-space cubes rather than bounds-stretched slabs. Per-axis normalization
                // would inherit the scene's anisotropy (e.g. a 2km x 4km x 130m drone scan -> ~16:1 flat cells);
                // a uniform scale maps the longest axis to [0,1] and the shorter axes to [0,<1] (fewer bits, but
                // cubic). Compact tiling chunks + view-invariant projected area for stabler LOD selection.
                Vector3 boundsMin = bounds.min;
                Vector3 boundsSize = bounds.size;
                float maxExtent = Mathf.Max(boundsSize.x, Mathf.Max(boundsSize.y, boundsSize.z));
                float invUniform = maxExtent > 1e-12f ? 1.0f / maxExtent : 0.0f;
                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD", $"Building variable chunks (cap {cap:N0})", 0.22f);

                void ConsumeSortedBucketRecords(BucketRecord[] records, int recordCount)
                {
                    if (recordCount <= 0) return;
                    long _b = _sw.ElapsedMilliseconds;

                    // Refine the coarse bucket order to full 21-bit Morton precision, then cut variable-size chunks.
                    ulong[] keys = new ulong[recordCount];
                    uint[] order = new uint[recordCount];
                    Vector3[] pos = new Vector3[recordCount];
                    for (int i = 0; i < recordCount; i++)
                    {
                        Vector3 p = new Vector3(records[i].px, records[i].py, records[i].pz);
                        keys[i] = MortonLBVH.MortonKey63((p.x - boundsMin.x) * invUniform, (p.y - boundsMin.y) * invUniform, (p.z - boundsMin.z) * invUniform);
                        order[i] = (uint)i;
                        pos[i] = p;
                    }
                    MortonLBVH.RadixSort63(keys, order, recordCount);
                    Vector3[] sortedPos = new Vector3[recordCount];
                    for (int i = 0; i < recordCount; i++) sortedPos[i] = pos[order[i]];

                    MortonLBVH.ChunkRange[] ranges = MortonLBVH.BuildChunks(keys, sortedPos, recordCount, cap, MinSplatsPerChunk, LodChunkGapRatio);
                    _tBuild += _sw.ElapsedMilliseconds - _b;
                    long _w = _sw.ElapsedMilliseconds;
                    BucketRecord[] chunkBuffer = new BucketRecord[computeStride];
                    foreach (MortonLBVH.ChunkRange range in ranges)
                    {
                        if (chunkBuffer.Length < range.count) chunkBuffer = new BucketRecord[range.count];
                        for (int i = 0; i < range.count; i++) chunkBuffer[i] = records[order[range.start + i]];

                        if (options.lodComputeSplats)
                        {
                            computedLodBackend.QueueChunk(pendingComputeChunks, writer.Chunks.Count + pendingComputeChunks.Count, chunkBuffer, range.count, importanceComparer, sh);
                            if (pendingComputeChunks.Count >= computedLodGpuBatchChunks)
                            {
                                computedLodBackend.FlushBatch(lodComputeShader, pendingComputeChunks, computeStride, writer, lodResamplePercent, lodReusePercent, sh);
                            }
                        }
                        else
                        {
                            writer.WriteChunk(chunkBuffer, range.count, importanceComparer);
                        }
                    }
                    _tWrite += _sw.ElapsedMilliseconds - _w;
                }

                for (int bucket = 0; bucket < bucketCount; bucket++)
                {
                    if (bucketCounts[bucket] <= 0)
                    {
                        continue;
                    }

                    EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD",
                        $"Chunking Hilbert bucket {bucket + 1:N0}/{bucketCount:N0} ({bucketCounts[bucket]:N0} splats, {writer.Chunks.Count:N0} chunks)",
                        0.25f + 0.55f * (bucket / (float)bucketCount));
                    ProcessSortedBucketFile(bucketPaths[bucket], bucketCounts[bucket], bucketBits, tempFolder, keyComparer, ConsumeSortedBucketRecords, bucket + 1, bucketCount);
                }

                long _w2 = _sw.ElapsedMilliseconds;
                if (options.lodComputeSplats && pendingComputeChunks.Count > 0)
                {
                    computedLodBackend.FlushBatch(lodComputeShader, pendingComputeChunks, computeStride, writer, lodResamplePercent, lodReusePercent, sh);
                }
                _tWrite += _sw.ElapsedMilliseconds - _w2;

                _m = _sw.ElapsedMilliseconds;
                writer.Finish();
                _tFinish = _sw.ElapsedMilliseconds - _m;
                sh.textures = writer.SH.ToArray();
                ChunkInfo[] chunks = writer.Chunks.ToArray();
                int[] fileSplatCounts = writer.FileSplatCounts.ToArray();
                Texture2D[] positions = writer.Positions.ToArray();
                Texture2D[] colors = writer.Colors.ToArray();
                Texture2D[] rotations = writer.Rotations.ToArray();
                Texture2D[] scales = writer.Scales.ToArray();
                int totalLod0SplatCount = writer.TotalLod0SplatCount;
                float smallestChunkSize = float.IsPositiveInfinity(writer.SmallestChunkSize) ? 1.0f : writer.SmallestChunkSize;

                if (chunks.Length == 0)
                {
                    throw new InvalidDataException("Streamed LOD import produced no chunks.");
                }

                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD", "Writing chunk metadata", 0.92f);
                _m = _sw.ElapsedMilliseconds;
                CreateChunkMetadata(dataFolder, assetName, chunks, out Texture2D chunkMin, out Texture2D chunkMax, out Texture2D chunkRange, out Vector4 chunkLayout);
                string metadataJson = GaussianSplatImporter.ImportMetadata.ToJson(new GaussianSplatImporter.ImportMetadata
                {
                    sourcePath = sourcePath, prefabPath = prefabPath, importAsLOD = true, lodChunkSize = chunkSize, options = options
                });
                GameObject prefab = CreatePrefab(prefabPath, assetName, sourcePath, maxLod0SplatsPerChunk, totalLod0SplatCount, chunks, bounds,
                    options.lodUsePackedPositions, lodReusePercent, positions, colors, rotations, scales, sh, fileSplatCounts, chunkMin, chunkMax, chunkRange, chunkLayout, smallestChunkSize, metadataJson);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                _tPrefab = _sw.ElapsedMilliseconds - _m;
                Debug.Log($"[LOD import] {splatCount:N0} splats -> {chunks.Length:N0} chunks | bounds {_tBounds} normalize {_tNormalize} bucketing {_tBucketing} chunkBuild {_tBuild} chunkWrite {_tWrite} finishSave {_tFinish} meta+prefab {_tPrefab} (ms) | total {_sw.ElapsedMilliseconds}ms");
                return prefab;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                try
                {
                    Directory.Delete(tempFolder, true);
                }
                catch
                {
                    // Temp cleanup is best-effort; import output assets are already in the project.
                }
            }
        }




        internal static int ResolveChunkSize(int requestedChunkSize, int splatCount)
        {
            // The requested value is the hard MAX chunk size (cap). Chunk metadata is a 2D power-of-two texture,
            // so total chunk count is no longer bounded by a single-row 16384 limit; honor the cap as authored.
            return Mathf.Max(1, requestedChunkSize);
        }

        internal static int NormalizeLodResamplePercent(int resamplePercent)
        {
            return resamplePercent <= 0 ? DefaultLodResamplePercent : Mathf.Clamp(resamplePercent, 1, DefaultLodResamplePercent);
        }

        internal static int NormalizeLodReusePercent(int reusePercent)
        {
            return reusePercent <= 0 ? DefaultLodReusePercent : Mathf.Clamp(reusePercent, 1, 99);
        }

        public static int EstimateStoredSplatCount(int splatCount, int chunkSize, bool computeLodSplats)
        {
            return EstimateStoredSplatCount(splatCount, chunkSize, computeLodSplats, DefaultLodResamplePercent, DefaultLodReusePercent);
        }

        public static int EstimateStoredSplatCount(int splatCount, int chunkSize, bool computeLodSplats, int resamplePercent)
        {
            return EstimateStoredSplatCount(splatCount, chunkSize, computeLodSplats, resamplePercent, DefaultLodReusePercent);
        }

        public static int EstimateStoredSplatCount(int splatCount, int chunkSize, bool computeLodSplats, int resamplePercent, int reusePercent)
        {
            splatCount = Mathf.Max(0, splatCount);
            if (!computeLodSplats || computedLodBackend == null)
            {
                // Chunked (LOD0-only) storage is exactly the source count; chunking never duplicates splats.
                return splatCount;
            }
            return computedLodBackend.EstimateStoredSplatCount(splatCount, ResolveChunkSize(chunkSize, splatCount), resamplePercent, reusePercent);
        }

        internal static void ComputeChunkImportance(BucketRecord[] chunkBuffer, int chunkCount)
        {
            const int localContrastRadius = 16;
            const float contrastWeight = 4.0f;
            int windowStart = 0;
            int windowEnd = Mathf.Min(chunkCount - 1, localContrastRadius);
            int windowCount = 0;
            Vector3 windowPremultipliedSum = Vector3.zero;
            for (int i = windowStart; i <= windowEnd; i++)
            {
                windowPremultipliedSum += PremultipliedColor(chunkBuffer[i]);
                windowCount++;
            }

            for (int i = 0; i < chunkCount; i++)
            {
                BucketRecord splat = chunkBuffer[i];
                float alpha = Mathf.Clamp01(splat.opacity);
                float alphaWeight = Mathf.Pow(alpha, 1.35f);
                Vector3 premultipliedColor = new Vector3(splat.cr, splat.cg, splat.cb) * alpha;
                int targetStart = Mathf.Max(0, i - localContrastRadius);
                int targetEnd = Mathf.Min(chunkCount - 1, i + localContrastRadius);
                while (windowStart < targetStart)
                {
                    windowPremultipliedSum -= PremultipliedColor(chunkBuffer[windowStart]);
                    windowStart++;
                    windowCount--;
                }
                while (windowEnd < targetEnd)
                {
                    windowEnd++;
                    windowPremultipliedSum += PremultipliedColor(chunkBuffer[windowEnd]);
                    windowCount++;
                }
                int localCount = Mathf.Max(0, windowCount - 1);
                Vector3 localPremultipliedMean = (windowPremultipliedSum - premultipliedColor) / Mathf.Max(1, localCount);
                float contrast = localCount > 0 ? (premultipliedColor - localPremultipliedMean).magnitude : 0.0f;
                float ab = Mathf.Abs(splat.sx * splat.sy);
                float ac = Mathf.Abs(splat.sx * splat.sz);
                float bc = Mathf.Abs(splat.sy * splat.sz);
                splat.importance = (alphaWeight + contrastWeight * contrast) * (ab + ac + bc);
                chunkBuffer[i] = splat;
            }
        }

        // Center of mass + the surface area of the splat-distribution covariance ellipsoid (a better proxy for
        // the chunk's on-screen size than the loose bbox). Both object-local; the shader scales by lossyScale.
        static void ChunkCenterAndArea(BucketRecord[] records, int count, out Vector3 center, out float area)
        {
            center = Vector3.zero; area = 0f;
            int n = records != null ? Mathf.Min(count, records.Length) : 0;
            if (n <= 0) return;

            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += records[i].px; cy += records[i].py; cz += records[i].pz; }
            center = new Vector3((float)(cx / n), (float)(cy / n), (float)(cz / n));

            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = records[i].px - center.x, dy = records[i].py - center.y, dz = records[i].pz - center.z;
                xx += dx * dx; yy += dy * dy; zz += dz * dz; xy += dx * dy; xz += dx * dz; yz += dy * dz;
            }
            double inv = 1.0 / n;
            Vector3 eig = SymmetricEigenvalues((float)(xx * inv), (float)(yy * inv), (float)(zz * inv), (float)(xy * inv), (float)(xz * inv), (float)(yz * inv));
            float a = Mathf.Sqrt(Mathf.Max(0f, eig.x)), b = Mathf.Sqrt(Mathf.Max(0f, eig.y)), c = Mathf.Sqrt(Mathf.Max(0f, eig.z));
            area = 2f * (a * b + a * c + b * c);
        }

        // Eigenvalues of a 3x3 symmetric matrix (Smith's analytic method). Values only (no vectors).
        static Vector3 SymmetricEigenvalues(float a00, float a11, float a22, float a01, float a02, float a12)
        {
            double p1 = (double)a01 * a01 + (double)a02 * a02 + (double)a12 * a12;
            if (p1 <= 1e-20) return new Vector3(a00, a11, a22); // already diagonal
            double q = (a00 + a11 + a22) / 3.0;
            double p2 = (a00 - q) * (a00 - q) + (a11 - q) * (a11 - q) + (a22 - q) * (a22 - q) + 2.0 * p1;
            double p = Math.Sqrt(p2 / 6.0);
            double b00 = (a00 - q) / p, b11 = (a11 - q) / p, b22 = (a22 - q) / p, b01 = a01 / p, b02 = a02 / p, b12 = a12 / p;
            double detB = b00 * (b11 * b22 - b12 * b12) - b01 * (b01 * b22 - b12 * b02) + b02 * (b01 * b12 - b11 * b02);
            double r = Math.Max(-1.0, Math.Min(1.0, detB / 2.0));
            double phi = Math.Acos(r) / 3.0;
            double e1 = q + 2.0 * p * Math.Cos(phi);
            double e3 = q + 2.0 * p * Math.Cos(phi + 2.0 * Math.PI / 3.0);
            double e2 = 3.0 * q - e1 - e3;
            return new Vector3((float)e1, (float)e2, (float)e3);
        }

        static Vector3 PremultipliedColor(BucketRecord splat)
        {
            float alpha = Mathf.Clamp01(splat.opacity);
            return new Vector3(splat.cr, splat.cg, splat.cb) * alpha;
        }

        internal static Vector3 PositionOf(BucketRecord splat)
        {
            return new Vector3(splat.px, splat.py, splat.pz);
        }


        static void CreateChunkMetadata(string dataFolder, string assetName, ChunkInfo[] chunks, out Texture2D chunkMin, out Texture2D chunkMax, out Texture2D chunkRange, out Vector4 chunkLayout)
        {
            // 2D row-major metadata: a variable-chunk object can exceed the 16384 single-row texture limit.
            // Width is a power of two so chunk c decodes as (c & (width-1), c >> log2(width)) without a modulo;
            // height holds the overflow rows. The fuse reads these linearly (GetPixels[c]), so 2D is transparent.
            int width = Mathf.Min(4096, Mathf.NextPowerOfTwo(Mathf.Max(1, chunks.Length)));
            int height = (Mathf.Max(1, chunks.Length) + width - 1) / width;
            Color[] minPixels = new Color[width * height];
            Color[] maxPixels = new Color[width * height];
            Color[] rangePixels = new Color[width * height * 2];
            int centerAreaOffset = width * height;
            for (int i = 0; i < chunks.Length; i++)
            {
                ChunkInfo chunk = chunks[i];
                minPixels[i] = new Color(chunk.boundsMin.x, chunk.boundsMin.y, chunk.boundsMin.z, chunk.count);
                maxPixels[i] = new Color(chunk.boundsMax.x, chunk.boundsMax.y, chunk.boundsMax.z, chunk.textureSet);
                int hi = chunk.textureOffset / OffsetBase;
                int lo = chunk.textureOffset - hi * OffsetBase;
                rangePixels[i] = new Color(hi, lo, chunk.count, 0.0f);
                // 2nd stack: chunk center of mass (xyz) + covariance-ellipsoid area (w), object-local; the LOD
                // selection uses the center for distance and the area for on-screen size (scaled by lossyScale).
                rangePixels[centerAreaOffset + i] = new Color(chunk.centerOfMass.x, chunk.centerOfMass.y, chunk.centerOfMass.z, chunk.covarianceArea);
            }

            chunkMin = NewTexture(width, height, TextureFormat.RGBAFloat, assetName + "_chunk_min");
            chunkMax = NewTexture(width, height, TextureFormat.RGBAFloat, assetName + "_chunk_max");
            chunkRange = NewTexture(width, height * 2, TextureFormat.RGBAFloat, assetName + "_chunk_range");
            chunkMin.SetPixels(minPixels);
            chunkMax.SetPixels(maxPixels);
            chunkRange.SetPixels(rangePixels);
            chunkMin.Apply(false, true);
            chunkMax.Apply(false, true);
            chunkRange.Apply(false, true);
            chunkMin = GaussianSplatImporter.CreateOrReplaceAsset(chunkMin, dataFolder + "/" + chunkMin.name + ".asset");
            chunkMax = GaussianSplatImporter.CreateOrReplaceAsset(chunkMax, dataFolder + "/" + chunkMax.name + ".asset");
            chunkRange = GaussianSplatImporter.CreateOrReplaceAsset(chunkRange, dataFolder + "/" + chunkRange.name + ".asset");
            chunkLayout = new Vector4(width, 1.0f / width, chunks.Length, 0.0f);
        }

        static GameObject CreatePrefab(string prefabPath, string assetName, string sourcePath, int chunkSize, int splatCount, ChunkInfo[] chunks, Bounds bounds, bool usePackedPositions, int lodReusePercent,
            Texture2D[] positions, Texture2D[] colors, Texture2D[] rotations, Texture2D[] scales, StreamedSHContext sh, int[] fileSplatCounts,
            Texture2D chunkMin, Texture2D chunkMax, Texture2D chunkRange, Vector4 chunkLayout, float smallestChunkSize, string metadataJson)
        {
            GameObject root = new GameObject(assetName);
            try
            {
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one; // y-flip is baked into coordinates -> identity scale

                GaussianSplatObject lod = root.GetComponent<GaussianSplatObject>();
                if (lod == null)
                {
                    lod = root.AddUdonSharpComponent<GaussianSplatObject>();
                }
                lod.splatName = assetName;
                lod.description = "Chunk LOD imported from " + sourcePath;
                lod.importMetadataJson = metadataJson;
                lod.chunkSize = chunkSize;
                lod.chunkCount = chunks.Length;
                lod.totalSplatCount = splatCount;
                lod.usePackedPositions = usePackedPositions;
                lod.lodReusePercent = NormalizeLodReusePercent(lodReusePercent);
                lod.lodZeroOffset = 2.0f;
                lod.lodSplatRadius = Mathf.Max(0.001f, bounds.size.magnitude);
                lod.smallestChunkSize = Mathf.Max(0.001f, smallestChunkSize);
                lod.boundsMin = bounds.min;
                lod.boundsMax = bounds.max;
                lod.positions = positions;
                lod.colors = colors;
                lod.rotations = rotations;
                lod.scales = scales;
                lod.fileSplatCounts = fileSplatCounts;
                int fileCount = positions.Length;
                if (sh != null && sh.coeffCount > 0 && sh.textures != null)
                {
                    // SH stored coeff-major within each file (stride = file splat count); shMin/shRange shared
                    // across the whole import, so every file carries the same range.
                    lod.sh = sh.textures;
                    lod.fileShCoeffCounts = FillIntArray(fileCount, sh.coeffCount);
                    int[] strides = new int[fileCount];
                    for (int i = 0; i < fileCount; i++) strides[i] = fileSplatCounts[i];
                    lod.fileShCoeffStrides = strides;
                    lod.fileShMins = FillVector4Array(fileCount, sh.min);
                    lod.fileShRanges = FillVector4Array(fileCount, sh.range);
                }
                else
                {
                    lod.sh = Array.Empty<Texture2D>();
                    lod.fileShCoeffCounts = new int[fileCount];
                    lod.fileShCoeffStrides = new int[fileCount];
                    lod.fileShMins = new Vector4[fileCount];
                    lod.fileShRanges = FillVector4Array(fileCount, Vector4.one);
                }
                lod.chunkBoundsMinTexture = chunkMin;
                lod.chunkBoundsMaxTexture = chunkMax;
                lod.chunkRangeTexture = chunkRange;
                lod.chunkTextureLayout = chunkLayout;
                EditorUtility.SetDirty(lod);
                EditorUtility.SetDirty(root);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }


        static Texture2D NewTexture(int width, int height, TextureFormat format, string name)
        {
            Texture2D texture = new Texture2D(width, height, format, false, true);
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;
            return texture;
        }

        static Vector4[] FillVector4Array(int count, Vector4 value)
        {
            Vector4[] values = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = value;
            }
            return values;
        }

        static int[] FillIntArray(int count, int value)
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = value;
            }
            return values;
        }

    }

}
#endif
