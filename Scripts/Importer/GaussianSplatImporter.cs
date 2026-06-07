
#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using Unity.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting;
using UdonSharp;
using UdonSharpEditor;

namespace GaussianSplatting
{
    public enum SHBand
    {
        SH0 = 0,
        SH1 = 1,
        SH2 = 2,
        SH3 = 3
    }

    /// <summary>
    /// Parses a Gaussian‑splat *.ply (or .spz) file and packs the base attributes plus optional
    /// spherical harmonic coefficient textures ready for GPU upload. Only UnityEngine types are
    /// referenced so this class can also be used at runtime. Editor‑only helpers are wrapped in
    /// UNITY_EDITOR guards.
    /// </summary>
    public static class PlySplatImporter
    {
        const int SHCoeffCount = 15;
        const float SHNonZeroEpsilon = 1e-8f;
        const int MaxImportSplatCount = 4096 * 4096;

        public readonly struct TextureLayout
        {
            public readonly int Width;
            public readonly int Height;

            public TextureLayout(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Capacity => Width * Height;
        }

        public readonly struct PassInfo
        {
            public readonly int PassIndex;
            public readonly int SplatOffset;
            public readonly int SplatCount;
            public readonly bool HasAlphaMask;

            public PassInfo(int passIndex, int splatOffset, int splatCount, bool hasAlphaMask)
            {
                PassIndex = passIndex;
                SplatOffset = splatOffset;
                SplatCount = splatCount;
                HasAlphaMask = hasAlphaMask;
            }
        }

        public struct ImportOptions
        {
            public bool computeBoundingBox;
            public int splatsPerPass;
            public bool precomputeSorting;
            public int maxAlphaMaskCount;
            public bool useSRGB;
            public bool importSphericalHarmonics;
            public SHBand defaultSHBand;
            public bool compressColorAlphaToBC7;
            public bool compressSHToBC7;
            public int startRenderQueue;
            public bool cropToBounds;
            public Bounds cropBounds;
            public float cropPadding;
            public bool applyHorizonAlignment;
            public Quaternion horizonRotation;
            public Vector3 horizonPivot;
        }

        static uint Morton3D(float nx, float ny, float nz)
        {
            // Clamp & convert to 10-bit ints (0-1023)
            uint x = (uint)Mathf.Clamp(Mathf.RoundToInt(nx * 1023f), 0, 1023);
            uint y = (uint)Mathf.Clamp(Mathf.RoundToInt(ny * 1023f), 0, 1023);
            uint z = (uint)Mathf.Clamp(Mathf.RoundToInt(nz * 1023f), 0, 1023);

            static uint Part1By2(uint v)          // expands 10 bits → 30 with 00 in-between
            {
                v = (v | (v << 16)) & 0x030000FF;
                v = (v | (v <<  8)) & 0x0300F00F;
                v = (v | (v <<  4)) & 0x030C30C3;
                v = (v | (v <<  2)) & 0x09249249;
                return v;
            }

            return Part1By2(x) | (Part1By2(y) << 1) | (Part1By2(z) << 2);
        }

        static TextureLayout EvaluateTextureLayout(int width, int texelCount)
        {
            int height = Mathf.CeilToInt((float)texelCount / width);
            height = Mathf.Max(4, ((height + 3) / 4) * 4);
            return new TextureLayout(width, height);
        }

        static bool IsBetterTextureLayout(TextureLayout candidate, TextureLayout best, int texelCount)
        {
            int candidateWaste = candidate.Capacity - texelCount;
            int bestWaste = best.Capacity - texelCount;
            if (candidateWaste != bestWaste)
            {
                return candidateWaste < bestWaste;
            }

            int candidateSquareDelta = Mathf.Abs(candidate.Width - candidate.Height);
            int bestSquareDelta = Mathf.Abs(best.Width - best.Height);
            if (candidateSquareDelta != bestSquareDelta)
            {
                return candidateSquareDelta < bestSquareDelta;
            }

            return candidate.Width > best.Width;
        }

        public static TextureLayout ChoosePotTextureLayout(int texelCount)
        {
            const int minWidth = 4;
            if (texelCount <= 0)
            {
                return new TextureLayout(minWidth, minWidth);
            }

            int sqrtTexelCount = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(texelCount)));
            int upperWidth = Mathf.Max(minWidth, Mathf.NextPowerOfTwo(sqrtTexelCount));
            int lowerWidth = upperWidth;
            if (lowerWidth > sqrtTexelCount)
            {
                lowerWidth >>= 1;
            }

            lowerWidth = Mathf.Max(minWidth, lowerWidth);

            TextureLayout best = EvaluateTextureLayout(lowerWidth, texelCount);
            if (upperWidth != lowerWidth)
            {
                TextureLayout candidate = EvaluateTextureLayout(upperWidth, texelCount);
                if (IsBetterTextureLayout(candidate, best, texelCount))
                {
                    best = candidate;
                }
            }

            return best;
        }

        static void ApplyTexture(Texture2D texture, bool compressToBC7)
        {
            if (!compressToBC7)
            {
                texture.Apply(false, true);
                return;
            }

            texture.Apply(false, false);
            EditorUtility.CompressTexture(texture, TextureFormat.BC7, TextureCompressionQuality.Best);
            texture.Apply(false, true);
        }

        static string GetSHPropertyName(int index)
        {
            return $"_GS_SH{(index + 1).ToString("X")}";
        }

        static Vector3 GetSHCoefficient(NativeArray<Vector3> shCoeffs, int shCoeffCount, int splatIndex, int coeffIndex)
        {
            if (!shCoeffs.IsCreated || shCoeffCount <= 0 || coeffIndex < 0 || coeffIndex >= shCoeffCount || splatIndex < 0)
            {
                return Vector3.zero;
            }

            return shCoeffs[splatIndex * shCoeffCount + coeffIndex];
        }

        internal static int ComputeTextureCoordShift(int width)
        {
            int shift = 0;
            width = Mathf.Max(1, width);
            while (width > 1)
            {
                width >>= 1;
                shift++;
            }

            return shift;
        }

        public static void ConfigureSplatMaterial(
            Material splatMat,
            Texture positions,
            Texture colors,
            Texture rotations,
            Texture scales,
            Texture sh,
            int shCoeffCount,
            int shCoeffStride,
            Vector4 shMin,
            Vector4 shRange,
            int actualSplatCount,
            float shBand,
            Texture colorsCamera,
            bool useCameraColorArray,
            Texture precomputedSort,
            int splatCount,
            int splatOffset)
        {
            splatMat.SetTexture("_GS_Positions", positions);
            int positionBlocksPerRow = Mathf.Max(1, (positions != null ? positions.width : 1) >> 2);
            splatMat.SetInt("_GS_Positions_CoordMask", positionBlocksPerRow - 1);
            splatMat.SetInt("_GS_Positions_CoordShift", ComputeTextureCoordShift(positionBlocksPerRow));
            splatMat.SetTexture("_GS_Colors", colors);
            splatMat.SetTexture("_GS_Rotations", rotations);
            splatMat.SetTexture("_GS_Scales", scales);
            splatMat.SetTexture("_GS_SH", sh);

            int shBlocksPerRow = Mathf.Max(1, (sh != null ? sh.width : 1) >> 2);
            splatMat.SetInt("_GS_SH_CoordMask", sh != null ? shBlocksPerRow - 1 : 0);
            splatMat.SetInt("_GS_SH_CoordShift", sh != null ? ComputeTextureCoordShift(shBlocksPerRow) : 0);
            splatMat.SetInt("_GS_SH_CoeffCount", shCoeffCount);
            splatMat.SetInt("_GS_SH_CoeffStride", shCoeffStride);
            splatMat.SetVector("_GS_SH_Min", shMin);
            splatMat.SetVector("_GS_SH_Range", shRange);
            splatMat.SetInt("_ActualSplatCount", actualSplatCount);
            splatMat.SetFloat("_SHBand", shBand);

            splatMat.SetTexture("_GS_ColorsCamera", colorsCamera);
            splatMat.SetFloat("_GS_CameraColorArray", useCameraColorArray ? 1.0f : 0.0f);
            splatMat.DisableKeyword("GS_CAMERA_COLOR_ARRAY");
            splatMat.DisableKeyword("_GS_CAMERACOLORARRAY_ON");

            if (precomputedSort != null)
            {
                splatMat.SetTexture("_GS_RenderOrderPrecomputed", precomputedSort);
                int renderOrderBlocksPerRow = Mathf.Max(1, precomputedSort.width >> 2);
                splatMat.SetInt("_GS_RenderOrderPrecomputed_CoordMask", renderOrderBlocksPerRow - 1);
                splatMat.SetInt("_GS_RenderOrderPrecomputed_CoordShift", ComputeTextureCoordShift(renderOrderBlocksPerRow));
                splatMat.SetInteger("_PRECOMPUTED_SORTING", 1);
                splatMat.EnableKeyword("_PRECOMPUTED_SORTING_ON");
            }
            else
            {
                splatMat.SetTexture("_GS_RenderOrderPrecomputed", null);
                splatMat.SetInt("_GS_RenderOrderPrecomputed_CoordMask", 0);
                splatMat.SetInt("_GS_RenderOrderPrecomputed_CoordShift", 0);
                splatMat.SetInteger("_PRECOMPUTED_SORTING", 0);
                splatMat.DisableKeyword("_PRECOMPUTED_SORTING_ON");
            }

            splatMat.SetInt("_SplatCount", splatCount);
            splatMat.SetInt("_SplatOffset", splatOffset);
        }

        public static PassInfo[] CreatePassLayout(int splatCount, int requestedSplatsPerPass, int maxAlphaMaskCount, bool useSRGB)
        {
            if (splatCount <= 0)
            {
                return new PassInfo[0];
            }

            if (requestedSplatsPerPass <= 0)
            {
                requestedSplatsPerPass = splatCount;
            }

            requestedSplatsPerPass = Mathf.Min(requestedSplatsPerPass, splatCount);
            if (!useSRGB)
            {
                requestedSplatsPerPass = splatCount;
            }

            int totalPassCount = (splatCount + requestedSplatsPerPass - 1) / requestedSplatsPerPass;
            int alphaMaskCount = Mathf.Min(maxAlphaMaskCount, totalPassCount - 1);
            int balancedSplatsPerPass = (splatCount + totalPassCount - 1) / totalPassCount;

            List<PassInfo> passes = new List<PassInfo>(totalPassCount);
            for (int splatOffset = 0; splatOffset < splatCount; splatOffset += balancedSplatsPerPass)
            {
                int passIndex = splatOffset / balancedSplatsPerPass;
                int passCount = Mathf.Min(balancedSplatsPerPass, splatCount - splatOffset);
                passes.Add(new PassInfo(passIndex, splatOffset, passCount, passIndex > 0 && passIndex <= alphaMaskCount));
            }

            return passes.ToArray();
        }

        public static void AppendMeshLayout(List<int> indexCounts, List<MeshTopology> topologies, PassInfo[] passInfos, bool useSRGB)
        {
            if (useSRGB)
            {
                indexCounts.Add(3);
                topologies.Add(MeshTopology.Triangles);
            }

            for (int i = 0; i < passInfos.Length; i++)
            {
                PassInfo passInfo = passInfos[i];
                if (passInfo.HasAlphaMask)
                {
                    indexCounts.Add(3);
                    topologies.Add(MeshTopology.Triangles);
                }

                indexCounts.Add((passInfo.SplatCount + 31) / 32);
                topologies.Add(MeshTopology.Points);
            }

            if (useSRGB)
            {
                indexCounts.Add(3);
                topologies.Add(MeshTopology.Triangles);
            }
        }

        public static Mesh CreateMultiPassMesh(List<int> indexCounts, List<MeshTopology> topologies, Bounds bounds)
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[3];
            mesh.subMeshCount = indexCounts.Count;

            for (int i = 0; i < indexCounts.Count; i++)
            {
                int[] indices = new int[indexCounts[i]];
                if (indices.Length > 0) indices[0] = 0;
                if (indices.Length > 1) indices[1] = 1;
                if (indices.Length > 2) indices[2] = 2;
                mesh.SetIndices(indices, topologies[i], i, false, 0);
            }

            mesh.bounds = bounds;
            return mesh;
        }

        static SHBand ClampDefaultSHBand(SHBand requestedBand, bool[] hasNonZeroBand)
        {
            int requested = Mathf.Clamp((int)requestedBand, (int)SHBand.SH0, (int)SHBand.SH3);
            for (int band = requested; band >= (int)SHBand.SH1; --band)
            {
                if (hasNonZeroBand[band])
                    return (SHBand)band;
            }
            return SHBand.SH0;
        }

        static int SHCoeffCountForBand(SHBand band)
        {
            return band switch
            {
                SHBand.SH0 => 0,
                SHBand.SH1 => 3,
                SHBand.SH2 => 8,
                _ => SHCoeffCount,
            };
        }

        static void FilterSplatsByBounds(ref NativeArray<ImportSplatData> splats, ref NativeArray<Vector3> shCoeffs, int shCoeffCount, Bounds bounds, string sourceName)
        {
            int[] included = new int[splats.Length];
            int includedCount = 0;
            for (int i = 0; i < splats.Length; i++)
            {
                if (bounds.Contains(splats[i].pos))
                {
                    included[includedCount++] = i;
                }
            }

            if (includedCount == splats.Length)
            {
                return;
            }
            if (includedCount == 0)
            {
                throw new InvalidOperationException($"Import aborted: crop bounds exclude all splats in '{sourceName}'.");
            }

            NativeArray<ImportSplatData> filteredSplats = new NativeArray<ImportSplatData>(includedCount, Allocator.Persistent);
            NativeArray<Vector3> filteredShCoeffs = shCoeffCount > 0 ? new NativeArray<Vector3>(includedCount * shCoeffCount, Allocator.Persistent, NativeArrayOptions.ClearMemory) : default;
            for (int i = 0; i < includedCount; i++)
            {
                int sourceIndex = included[i];
                filteredSplats[i] = splats[sourceIndex];
                if (shCoeffCount <= 0)
                {
                    continue;
                }
                for (int coeff = 0; coeff < shCoeffCount; coeff++)
                {
                    filteredShCoeffs[i * shCoeffCount + coeff] = shCoeffs[sourceIndex * shCoeffCount + coeff];
                }
            }

            splats.Dispose();
            if (shCoeffs.IsCreated)
            {
                shCoeffs.Dispose();
            }
            splats = filteredSplats;
            shCoeffs = filteredShCoeffs;
        }

        static void ApplyHorizonAlignment(NativeArray<ImportSplatData> splats, Quaternion rotation, Vector3 pivot)
        {
            for (int i = 0; i < splats.Length; i++)
            {
                ImportSplatData splat = splats[i];
                splat.pos = rotation * (splat.pos - pivot);
                splat.rot = rotation * splat.rot;
                splats[i] = splat;
            }
        }

        static int ComputePackedTextureIndex(int index, int width)
        {
            int blocksPerRow = Mathf.Max(1, width >> 2);
            int blockIndex = index >> 4;
            int blockX = blockIndex & (blocksPerRow - 1);
            int blockY = blockIndex >> ComputeTextureCoordShift(blocksPerRow);
            int x = (blockX << 2) | (index & 3);
            int y = (blockY << 2) | ((index >> 2) & 3);
            return y * width + x;
        }

        static MeshRenderer CreateRendererChild(Transform parent, string name, Mesh mesh, List<Material> materials)
        {
            GameObject child = new GameObject(name);
            child.transform.SetParent(parent, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer meshRenderer = child.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials.ToArray();
            meshRenderer.allowOcclusionWhenDynamic = false;
            return meshRenderer;
        }

        static void ConfigurePrefabRoot(GameObject go, List<Material> materials, Mesh mesh, string name, int maxSHBand, bool addGaussianSplatObject)
        {
            go.name = name;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(1, -1, 1); // flip Y to match unity's coordinate system

            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter == null)
                meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials.ToArray();
            meshRenderer.allowOcclusionWhenDynamic = false;

            if (!addGaussianSplatObject)
            {
                GaussianSplatObject existingSplatObject = go.GetComponent<GaussianSplatObject>();
                if (existingSplatObject != null)
                    UnityEngine.Object.DestroyImmediate(existingSplatObject);
                return;
            }

            GaussianSplatObject splatObject = go.GetComponent<GaussianSplatObject>();
            if (splatObject == null)
                splatObject = go.AddUdonSharpComponent<GaussianSplatObject>();
            splatObject.gaussianSplatRenderer = null;
            splatObject.sortedObject = null;
            splatObject.sortedRenderer = meshRenderer;
            splatObject.SetMaxSHBand(Mathf.Clamp(maxSHBand, 0, 3));
            UdonSharpEditorUtility.CopyProxyToUdon(splatObject);
        }

        public static GameObject CreatePrefab(List<Material> materials, Mesh mesh, string assetPath, string name, int maxSHBand = -1, bool addGaussianSplatObject = true)
        {
            GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (existingPrefab != null)
            {
                GameObject prefabContents = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    ConfigurePrefabRoot(prefabContents, materials, mesh, name, maxSHBand, addGaussianSplatObject);
                    PrefabUtility.SaveAsPrefabAsset(prefabContents, assetPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabContents);
                }

                return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            }

            var go = new GameObject(name);
            ConfigurePrefabRoot(go, materials, mesh, name, maxSHBand, addGaussianSplatObject);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, assetPath);
            GameObject.DestroyImmediate(go); // clean up the temporary GameObject
            return prefab;
        }

        public static void Import(string plyFile, string prefabOutputPath, bool computeBoundingBox, int splatsPerPass, bool precomputeSorting = false, int maxAlphaMaskCount = 1, bool useSRGB = true, bool importSphericalHarmonics = true, SHBand defaultSHBand = SHBand.SH1, bool compressColorAlphaToBC7 = false, bool compressSHToBC7 = true, int startRenderQueue = 4050)
        {
            Import(plyFile, prefabOutputPath, new ImportOptions
            {
                computeBoundingBox = computeBoundingBox,
                splatsPerPass = splatsPerPass,
                precomputeSorting = precomputeSorting,
                maxAlphaMaskCount = maxAlphaMaskCount,
                useSRGB = useSRGB,
                importSphericalHarmonics = importSphericalHarmonics,
                defaultSHBand = defaultSHBand,
                compressColorAlphaToBC7 = compressColorAlphaToBC7,
                compressSHToBC7 = compressSHToBC7,
                startRenderQueue = startRenderQueue,
                cropToBounds = false,
                cropBounds = new Bounds(Vector3.zero, Vector3.one),
                cropPadding = 0.0f,
                applyHorizonAlignment = false,
                horizonRotation = Quaternion.identity,
                horizonPivot = Vector3.zero
            });
        }

        public static void Import(string plyFile, string prefabOutputPath, ImportOptions options)
        {
            if (!File.Exists(plyFile))
                throw new FileNotFoundException(plyFile);

            // Read header to learn how many splats we need to allocate for.
            int count = GaussianFileReader.ReadFileHeader(plyFile);
            if (count == 0)
                throw new Exception("Empty or unsupported splat file");
            if (count > MaxImportSplatCount)
                throw new InvalidOperationException($"Import aborted: '{Path.GetFileName(plyFile)}' contains {count:N0} splats, exceeding the importer limit of {MaxImportSplatCount:N0}.");

            int requestedSHCoeffCount = options.importSphericalHarmonics ? SHCoeffCountForBand(options.defaultSHBand) : 0;
            bool willAttemptBC7Compression = options.compressColorAlphaToBC7 || (options.compressSHToBC7 && requestedSHCoeffCount > 0);
            if (willAttemptBC7Compression && !SystemInfo.SupportsTextureFormat(TextureFormat.BC7))
                throw new InvalidOperationException("BC7 compression is not supported by the current editor graphics device. Disable BC7 compression or import on a system with BC7 support.");

            GaussianFileReader.ReadFile(plyFile, requestedSHCoeffCount, out NativeArray<ImportSplatData> splats, out NativeArray<Vector3> shCoeffs);
            try
            {
                if (options.applyHorizonAlignment)
                {
                    ApplyHorizonAlignment(splats, options.horizonRotation, options.horizonPivot);
                }

                int originalSplatCount = splats.Length;
                if (options.cropToBounds)
                {
                    FilterSplatsByBounds(ref splats, ref shCoeffs, requestedSHCoeffCount, options.cropBounds, Path.GetFileName(plyFile));
                }

                var shMinPerCoeff = requestedSHCoeffCount > 0 ? new Vector3[requestedSHCoeffCount] : Array.Empty<Vector3>();
                var shRangePerCoeff = requestedSHCoeffCount > 0 ? new Vector3[requestedSHCoeffCount] : Array.Empty<Vector3>();
                const float shRangeEpsilon = 1e-8f;
                bool[] hasNonZeroBand = new bool[4];
                if (requestedSHCoeffCount > 0)
                {
                    for (int coeff = 0; coeff < requestedSHCoeffCount; ++coeff)
                    {
                        Vector3 minCoeff = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                        Vector3 maxCoeff = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                        for (int i = 0; i < splats.Length; ++i)
                        {
                            Vector3 sh = GetSHCoefficient(shCoeffs, requestedSHCoeffCount, i, coeff);
                            minCoeff = Vector3.Min(minCoeff, sh);
                            maxCoeff = Vector3.Max(maxCoeff, sh);
                        }

                        if (splats.Length == 0)
                        {
                            minCoeff = Vector3.zero;
                            maxCoeff = Vector3.zero;
                        }

                        shMinPerCoeff[coeff] = minCoeff;
                        shRangePerCoeff[coeff] = maxCoeff - minCoeff;

                        int band = coeff < 3 ? 1 : (coeff < 8 ? 2 : 3);
                        if (!hasNonZeroBand[band])
                        {
                            Vector3 range = shRangePerCoeff[coeff];
                            hasNonZeroBand[band] = range.x > SHNonZeroEpsilon || range.y > SHNonZeroEpsilon || range.z > SHNonZeroEpsilon;
                        }
                    }
                }
                SHBand effectiveDefaultSHBand = options.importSphericalHarmonics ? ClampDefaultSHBand(options.defaultSHBand, hasNonZeroBand) : SHBand.SH0;
                int importedSHCoeffCount = options.importSphericalHarmonics ? SHCoeffCountForBand(effectiveDefaultSHBand) : 0;
                Vector3 sharedShMin = Vector3.zero;
                Vector3 sharedShMax = Vector3.zero;
                if (importedSHCoeffCount > 0)
                {
                    sharedShMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    sharedShMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                    for (int coeff = 0; coeff < importedSHCoeffCount; ++coeff)
                    {
                        Vector3 coeffMin = shMinPerCoeff[coeff];
                        Vector3 coeffMax = coeffMin + shRangePerCoeff[coeff];
                        sharedShMin = Vector3.Min(sharedShMin, coeffMin);
                        sharedShMax = Vector3.Max(sharedShMax, coeffMax);
                    }
                }
                Vector3 sharedShRange = sharedShMax - sharedShMin;

                int n = splats.Length;
                TextureLayout splatLayout = ChoosePotTextureLayout(n);
                TextureLayout shLayout = importedSHCoeffCount > 0
                    ? ChoosePotTextureLayout(n * importedSHCoeffCount)
                    : new TextureLayout(4, 4);

                Debug.Log(options.cropToBounds
                    ? $"Importing {n} / {originalSplatCount} cropped splats into {splatLayout.Width}x{splatLayout.Height} textures"
                    : $"Importing {count} splats into {splatLayout.Width}x{splatLayout.Height} textures");

                // Compute BBOX
                Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < n; ++i)
                {
                    min = Vector3.Min(min, splats[i].pos);
                    max = Vector3.Max(max, splats[i].pos);
                }
                Vector3 size = max - min;

                if (size.x == 0) size.x = 1e-6f;
                if (size.y == 0) size.y = 1e-6f;
                if (size.z == 0) size.z = 1e-6f;

                // Prepare Morton keys
                var keys = new uint[n];
                Vector3 centerOfMass = Vector3.zero;
                int validCount = 0;
                for (int i = 0; i < n; ++i)
                {
                    Vector3 pos = splats[i].pos;
                    if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z))
                    {
                        Debug.LogWarning($"Skipping splat {i} with NaN position: {pos}");
                        continue; // skip invalid splats
                    }
                    centerOfMass += pos;
                    ++validCount;
                    Vector3 np = (pos - min);
                    np.x /= size.x; np.y /= size.y; np.z /= size.z;
                    keys[i] = Morton3D(np.x, np.y, np.z);
                }

                centerOfMass /= validCount; // compute center of mass

                // Compute bounds relative to the center of mass
                Vector3 maxSize = Vector3.zero;
                for (int i = 0; i < n; ++i)
                {
                    Vector3 pos = splats[i].pos;
                    if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z))
                        continue; // skip invalid splats
                    Vector3 relativePos = pos - centerOfMass;
                    maxSize.x = Mathf.Max(maxSize.x, Mathf.Abs(relativePos.x));
                    maxSize.y = Mathf.Max(maxSize.y, Mathf.Abs(relativePos.y));
                    maxSize.z = Mathf.Max(maxSize.z, Mathf.Abs(relativePos.z));
                }

                int[] sortedOrder = new int[n];
                for (int i = 0; i < n; ++i)
                {
                    sortedOrder[i] = i;
                }
                Array.Sort(keys, sortedOrder);

                int[] textureIndexBySourceIndex = new int[n];
                for (int i = 0; i < n; ++i)
                {
                    textureIndexBySourceIndex[sortedOrder[i]] = i;
                }

                Bounds bbox = new Bounds();
                if (options.cropToBounds)
                {
                    bbox = options.cropBounds;
                    bbox.extents += Vector3.one * Mathf.Max(0.0f, options.cropPadding);
                }
                else if (options.computeBoundingBox)
                {
                    // Compute bounding box from splats
                    bbox.center = centerOfMass;
                    bbox.extents = new Vector3(maxSize.x, maxSize.y, maxSize.z);
                    if (bbox.extents.x == 0 || bbox.extents.y == 0 || bbox.extents.z == 0)
                    {
                        // If the bounding box is zero-sized, set a default size
                        bbox.extents = new Vector3(1000, 1000, 1000);
                        Debug.LogWarning("Bounding box is zero-sized, using default size.");
                    }
                }
                else
                {
                    // Use a default bounding box if not computing from splats
                    bbox.center = Vector3.zero;
                    bbox.extents = new Vector3(1000, 1000, 1000);
                }

                // Get name of the material from the path
                string materialName = Path.GetFileNameWithoutExtension(prefabOutputPath);
                string outputDataFolder = Path.GetDirectoryName(prefabOutputPath) + "/" + materialName;

                // Create output data folder if it doesn't exist
                EnsureFolderExists(outputDataFolder);

                Texture2DArray sortedTex = null;
                if(options.precomputeSorting) {
                    Vector3[] octahedral_dirs = {
                        new Vector3( 0.57735027f,  0.57735027f,  0.57735027f), new Vector3( 0.57735027f,  0.57735027f, -0.57735027f), new Vector3( 0.57735027f, -0.57735027f,  0.57735027f),
                        new Vector3( 0.57735027f, -0.57735027f, -0.57735027f), new Vector3( 0.00000000f,  0.35682209f,  0.93417236f), new Vector3( 0.00000000f,  0.35682209f, -0.93417236f),
                        new Vector3( 0.35682209f,  0.93417236f,  0.00000000f), new Vector3( 0.35682209f, -0.93417236f,  0.00000000f), new Vector3( 0.93417236f,  0.00000000f,  0.35682209f),
                        new Vector3( 0.93417236f,  0.00000000f, -0.35682209f)
                    };
                    // Precompute sorting for octahedral directions
                    int[][] sortedIndices = new int[octahedral_dirs.Length][];
                    for (int i = 0; i < octahedral_dirs.Length; ++i)
                    {
                        Vector3 dir = octahedral_dirs[i];
                        sortedIndices[i] = new int[n];
                        for (int j = 0; j < n; ++j)
                        {
                            sortedIndices[i][j] = j;
                        }
                        Array.Sort(sortedIndices[i], (a, b) => Vector3.Dot(splats[a].pos, dir).CompareTo(Vector3.Dot(splats[b].pos, dir)));
                    }

                    sortedTex = NewTextureArray(splatLayout.Width, splatLayout.Height, octahedral_dirs.Length, TextureFormat.RFloat, "SortedOctahedralDirections");
                    for (int i = 0; i < octahedral_dirs.Length; ++i)
                    {
                        Color[] sortedPixels = new Color[splatLayout.Capacity];
                        for (int j = 0; j < n; ++j)
                        {
                            int packedIndex = ComputePackedTextureIndex(j, splatLayout.Width);
                            sortedPixels[packedIndex] = new Color(textureIndexBySourceIndex[sortedIndices[i][j]], 0f, 0f, 0f); // Store only the texture index in the red channel
                        }
                        sortedTex.SetPixels(sortedPixels, i);
                    }
                    sortedTex.Apply(false, true);
                    sortedTex = SaveTextureAsset(sortedTex, outputDataFolder, materialName + "_sorted_oct_dirs");
                }


                Texture2D xyzTex     = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGBAFloat, "XYZ");
                Texture2D colDcTex   = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGBA32, "ColorDC");
                Texture2D rotTex     = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGBA32, "Rotation");
                Texture2D scaleTex   = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGB9e5Float, "Scale");
                Texture2D shTex      = importedSHCoeffCount > 0 ? NewTexture(shLayout.Width, shLayout.Height, TextureFormat.RGB565, "SH") : null;

                Shader shader = null;
                if(options.useSRGB) {
                    shader = Shader.Find("VRChatGaussianSplatting/GaussianSplatting");
                } else {
                    shader = Shader.Find("VRChatGaussianSplatting/GaussianSplattingSimpleBackToFront");
                }

                var xyzPixels   = new Color[splatLayout.Capacity];
                var colPixels   = new Color[splatLayout.Capacity];
                var rotPixels   = new Color[splatLayout.Capacity];
                var scalePixels = new Color[splatLayout.Capacity];
                var shPixels    = importedSHCoeffCount > 0 ? new Color[shLayout.Capacity] : null;

                for (int i = 0; i < n; ++i) {
                    int sourceIndex = sortedOrder[i];
                    int packedIndex = ComputePackedTextureIndex(i, splatLayout.Width);
                    var s = splats[sourceIndex];
                    xyzPixels[packedIndex]   = new Color(s.pos.x,   s.pos.y,   s.pos.z,   0f);
                    colPixels[packedIndex]   = new Color(s.dc0.x,   s.dc0.y,   s.dc0.z,   s.opacity);
                    rotPixels[packedIndex]   = new Color(0.5f + 0.5f * s.rot.x,
                                                         0.5f + 0.5f * s.rot.y,
                                                         0.5f + 0.5f * s.rot.z,
                                                         0.5f + 0.5f * s.rot.w);
                    scalePixels[packedIndex] = new Color(s.scale.x, s.scale.y, s.scale.z, 0f);

                    if (importedSHCoeffCount > 0)
                    {
                        for (int coeff = 0; coeff < importedSHCoeffCount; ++coeff)
                        {
                            Vector3 sh = GetSHCoefficient(shCoeffs, requestedSHCoeffCount, sourceIndex, coeff);
                            int shPackedIndex = ComputePackedTextureIndex(coeff * n + i, shLayout.Width);
                            shPixels[shPackedIndex] = new Color(
                                sharedShRange.x > shRangeEpsilon ? (sh.x - sharedShMin.x) / sharedShRange.x : 0f,
                                sharedShRange.y > shRangeEpsilon ? (sh.y - sharedShMin.y) / sharedShRange.y : 0f,
                                sharedShRange.z > shRangeEpsilon ? (sh.z - sharedShMin.z) / sharedShRange.z : 0f,
                                0f
                            );
                        }
                    }
                }

                xyzTex.SetPixels(xyzPixels);
                colDcTex.SetPixels(colPixels);
                rotTex.SetPixels(rotPixels);
                scaleTex.SetPixels(scalePixels);
                if (importedSHCoeffCount > 0)
                {
                    shTex.SetPixels(shPixels);
                }

                xyzTex.Apply(false, true);
                ApplyTexture(colDcTex, options.compressColorAlphaToBC7);
                rotTex.Apply(false, true);
                scaleTex.Apply(false, true);
                if (importedSHCoeffCount > 0)
                {
                    ApplyTexture(shTex, options.compressSHToBC7);
                }

                xyzTex = SaveTextureAsset(xyzTex, outputDataFolder, materialName + "_xyz");
                colDcTex = SaveTextureAsset(colDcTex, outputDataFolder, materialName + "_color_dc");
                rotTex = SaveTextureAsset(rotTex, outputDataFolder, materialName + "_rotation");
                scaleTex = SaveTextureAsset(scaleTex, outputDataFolder, materialName + "_scale");
                if (importedSHCoeffCount > 0)
                {
                    shTex = SaveTextureAsset(shTex, outputDataFolder, materialName + "_sh");
                }

                int splatsPerPass = options.splatsPerPass;
                if(splatsPerPass == 0) splatsPerPass = n;
                splatsPerPass = Mathf.Min(splatsPerPass, n);

                List<Material> materials = new List<Material>();
                List<int> indexCounts = new List<int>();
                List<MeshTopology> topologies = new List<MeshTopology>();
                PassInfo[] passInfos = CreatePassLayout(n, splatsPerPass, options.maxAlphaMaskCount, options.useSRGB);
                AppendMeshLayout(indexCounts, topologies, passInfos, options.useSRGB);

                if(options.useSRGB) {
                    Material convertToSRGB = new Material(Shader.Find("VRChatGaussianSplatting/ToSRGB"));
                    convertToSRGB.name = "convert_to_srgb";
                    materials.Add(convertToSRGB);
                }

                Material mainMat = null;
                for (int passInfoIndex = 0; passInfoIndex < passInfos.Length; passInfoIndex++)
                {
                    PassInfo passInfo = passInfos[passInfoIndex];
                    Material splatMat = null;
                    string splatMatName = materialName + (passInfo.PassIndex > 0 ? $"_pass_{passInfo.PassIndex}" : "_main") + "_splat";
                    if(passInfo.PassIndex == 0) {
                        splatMat = new Material(shader);
                        splatMat.name = splatMatName;
                        mainMat = splatMat;
                    } else {
                        splatMat = new Material(mainMat); // copy the base pass settings without relying on parent inheritance
                    }

                    ConfigureSplatMaterial(
                        splatMat,
                        xyzTex,
                        colDcTex,
                        rotTex,
                        scaleTex,
                        shTex,
                        importedSHCoeffCount,
                        n,
                        new Vector4(sharedShMin.x, sharedShMin.y, sharedShMin.z, 0f),
                        new Vector4(
                            Mathf.Max(sharedShRange.x, shRangeEpsilon),
                            Mathf.Max(sharedShRange.y, shRangeEpsilon),
                            Mathf.Max(sharedShRange.z, shRangeEpsilon),
                            0f),
                        n,
                        (float)effectiveDefaultSHBand,
                        null,
                        false,
                        options.precomputeSorting ? sortedTex : null,
                        passInfo.SplatCount,
                        passInfo.SplatOffset);

                    if(passInfo.HasAlphaMask) {
                        // Create alpha depth mask pass
                        Material alphaDepthMask = new Material(Shader.Find("VRChatGaussianSplatting/AlphaDepthMask"));
                        alphaDepthMask.name = splatMatName + "_alpha_depth_mask";
                        materials.Add(alphaDepthMask);
                    }
                    splatMat.name = splatMatName;
                    materials.Add(splatMat);
                }

                if(options.useSRGB) {
                    Material convertToLinear = new Material(Shader.Find("VRChatGaussianSplatting/ToLinear"));
                    convertToLinear.name = "convert_to_linear";
                    materials.Add(convertToLinear);
                }

                EnsureFolderExists(outputDataFolder + "/materials");
                for (int i = 0; i < materials.Count; ++i) {
                    Material splatMat = materials[i];
                    splatMat.renderQueue = options.startRenderQueue + i;
                    string matPath = Path.Combine(outputDataFolder + "/materials", splatMat.name + ".mat");
                    materials[i] = CreateOrReplaceAsset(splatMat, matPath);
                }

                Mesh pointMesh = CreateMultiPassMesh(indexCounts, topologies, bbox);
                pointMesh = CreateOrReplaceAsset(pointMesh, Path.Combine(outputDataFolder, materialName + "_mesh.asset"));
                // Create prefab with the splat material and mesh
                CreatePrefab(materials, pointMesh, prefabOutputPath, materialName, (int)effectiveDefaultSHBand);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
                if (shCoeffs.IsCreated)
                    shCoeffs.Dispose();
            }
        }

        // ---------------------------------------------------------------------
        static Texture2D NewTexture(int width, int height, TextureFormat format, string name)
        {
            var tex = new Texture2D(width, height, format, mipChain: false, linear: true)
            {
                name       = name,
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            return tex;
        }

        static Texture2DArray NewTextureArray(int width, int height, int count, TextureFormat format, string name)
        {
            var tex = new Texture2DArray(width, height, count, format, mipChain: false, linear: true)
            {
                name       = name,
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            return tex;
        }

        public static string SanitizeAssetName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "GaussianSplatRenderer";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalidChars, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        public static void EnsureFolderExists(string folderPath)
        {
            folderPath = folderPath?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            Directory.CreateDirectory(folderPath);
            AssetDatabase.ImportAsset(folderPath);
            AssetDatabase.Refresh();
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string[] parts = folderPath.Replace('\\', '/').Split('/');
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
            AssetDatabase.ImportAsset(folderPath);
        }

        static RenderTexture CreateSortRenderTextureAsset(string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
        {
            EnsureFolderExists(folderPath);
            RenderTexture renderTexture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
            renderTexture.name = assetName;
            renderTexture.dimension = volumeDepth > 1 ? UnityEngine.Rendering.TextureDimension.Tex2DArray : UnityEngine.Rendering.TextureDimension.Tex2D;
            renderTexture.volumeDepth = volumeDepth;
            renderTexture.useMipMap = useMipMap;
            renderTexture.autoGenerateMips = false;
            renderTexture.wrapMode = TextureWrapMode.Clamp;
            renderTexture.filterMode = FilterMode.Point;
            renderTexture.anisoLevel = 0;
            renderTexture.antiAliasing = 1;
            renderTexture.Create();
            AssetDatabase.CreateAsset(renderTexture, folderPath + "/" + assetName + ".renderTexture");
            return renderTexture;
        }

        /// <summary>
        /// Ensures the referenced RenderTexture asset exists at the expected scene-local path with the
        /// requested format/size. Re-points the reference when it is missing, in-memory, or owned by a
        /// different folder (e.g. a duplicated scene). Returns true when the reference or asset changed.
        /// </summary>
        public static bool EnsureSortRenderTexture(ref RenderTexture targetTexture, string folderPath, string assetName, int width, int height, RenderTextureFormat format, bool useMipMap, int volumeDepth)
        {
            string assetPath = folderPath + "/" + assetName + ".renderTexture";
            bool changed = false;
            if (targetTexture == null || AssetDatabase.GetAssetPath(targetTexture) != assetPath)
            {
                EnsureFolderExists(folderPath);
                targetTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(assetPath);
                if (targetTexture == null)
                {
                    targetTexture = CreateSortRenderTextureAsset(folderPath, assetName, width, height, format, useMipMap, volumeDepth);
                    return true;
                }
                changed = true;
            }
            bool needsResize = targetTexture.width != width
                || targetTexture.height != height
                || targetTexture.format != format
                || targetTexture.dimension != (volumeDepth > 1 ? UnityEngine.Rendering.TextureDimension.Tex2DArray : UnityEngine.Rendering.TextureDimension.Tex2D)
                || targetTexture.volumeDepth != volumeDepth
                || targetTexture.useMipMap != useMipMap
                || targetTexture.autoGenerateMips
                || targetTexture.wrapMode != TextureWrapMode.Clamp
                || targetTexture.filterMode != FilterMode.Point
                || targetTexture.anisoLevel != 0
                || targetTexture.antiAliasing != 1;
            if (!needsResize)
            {
                return changed;
            }
            Undo.RecordObject(targetTexture, "Resize Gaussian Splat Sort Texture");
            targetTexture.Release();
            targetTexture.width = width;
            targetTexture.height = height;
            targetTexture.format = format;
            targetTexture.dimension = volumeDepth > 1 ? UnityEngine.Rendering.TextureDimension.Tex2DArray : UnityEngine.Rendering.TextureDimension.Tex2D;
            targetTexture.volumeDepth = volumeDepth;
            targetTexture.useMipMap = useMipMap;
            targetTexture.autoGenerateMips = false;
            targetTexture.wrapMode = TextureWrapMode.Clamp;
            targetTexture.filterMode = FilterMode.Point;
            targetTexture.anisoLevel = 0;
            targetTexture.antiAliasing = 1;
            targetTexture.Create();
            EditorUtility.SetDirty(targetTexture);
            return true;
        }

        public static string GetSceneTempResourceFolderPath(UnityEngine.SceneManagement.Scene scene, string subfolder)
        {
            string sceneName = scene.name;
            if (string.IsNullOrEmpty(sceneName) && !string.IsNullOrEmpty(scene.path))
            {
                sceneName = Path.GetFileNameWithoutExtension(scene.path);
            }

            string rootPath = "Assets/Temp/GS_" + SanitizeAssetName(string.IsNullOrEmpty(sceneName) ? "UnsavedScene" : sceneName);
            if (string.IsNullOrEmpty(subfolder))
            {
                return rootPath;
            }

            return rootPath + "/" + subfolder.TrimStart('/');
        }

        public static Material CreateMaterialFromTemplate(Material template, string shaderName, string materialName)
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

        public static T CreateOrReplaceAsset<T>(T asset, string path) where T : UnityEngine.Object
        {
            path = path.Replace('\\', '/');
            string assetName = Path.GetFileNameWithoutExtension(path);
            asset.name = assetName;

            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerializedIfDifferent(asset, existing);
                Material existingMaterial = existing as Material;
                Material sourceMaterial = asset as Material;
                if (existingMaterial != null && sourceMaterial != null && existingMaterial.renderQueue != sourceMaterial.renderQueue)
                {
                    existingMaterial.renderQueue = sourceMaterial.renderQueue;
                    EditorUtility.SetDirty(existingMaterial);
                }
                UnityEngine.Object.DestroyImmediate(asset);
                existing.name = assetName;
                return existing;
            }

            EnsureFolderExists(Path.GetDirectoryName(path));
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.CreateAsset(asset, path);
            T savedAsset = AssetDatabase.LoadAssetAtPath<T>(path) ?? asset;
            savedAsset.name = assetName;
            EditorUtility.SetDirty(savedAsset);
            return savedAsset;
        }

        static Texture2D SaveTextureAsset(Texture2D tex, string folder, string name)
        {
            string path = Path.Combine(folder, $"{name}.asset");
            return CreateOrReplaceAsset(tex, path);
        }

        static Texture2DArray SaveTextureAsset(Texture2DArray tex, string folder, string name)
        {
            string path = Path.Combine(folder, $"{name}.asset");
            return CreateOrReplaceAsset(tex, path);
        }
    }
}

namespace GaussianSplatting.Editor.Importers
{
    public class PlyImportWizard : EditorWindow
    {
        internal const string DefaultOutputFolder = "Assets";
        internal const bool DefaultComputeBoundingBox = true;
        internal const bool DefaultMultiPassRendering = true;
        internal const int DefaultSplatsPerPass = 3 * 256 * 1024;
        internal const bool DefaultPrecomputeSorting = false;
        internal const int DefaultMaxAlphaMaskCount = 1;
        internal const bool DefaultUseSRGB = true;
        internal const bool DefaultImportSphericalHarmonics = true;
        internal static readonly SHBand DefaultImportedSHBand = SHBand.SH3;
        internal const bool DefaultCompressColorAlphaToBC7 = false;
        internal const bool DefaultCompressSHToBC7 = true;
        internal const int DefaultStartRenderQueue = 4050;
        const int MaxPreviewSplats = 8192;
        const float PreviewSplatPixelRadius = 5.0f;

        class ImportEntry
        {
            public string path = string.Empty;
            public bool cropToBounds;
            public Bounds cropBounds = new Bounds(Vector3.zero, Vector3.one);
            public bool horizonPickMode;
            public bool applyHorizonAlignment;
            public Quaternion horizonRotation = Quaternion.identity;
            public Vector3 horizonPivot;
            public List<Vector3> horizonPoints = new List<Vector3>();
            public bool wallPickMode;
            public bool applyWallAlignment;
            public Quaternion wallRotation = Quaternion.identity;
            public List<Vector3> wallPoints = new List<Vector3>();
            public bool overrideSettings;
            public bool computeBoundingBox = DefaultComputeBoundingBox;
            public bool multiPassRendering = DefaultMultiPassRendering;
            public int splatsPerPass = DefaultSplatsPerPass;
            public bool precomputeSorting = DefaultPrecomputeSorting;
            public int maxAlphaMaskCount = DefaultMaxAlphaMaskCount;
            public bool useSRGB = DefaultUseSRGB;
            public bool importSphericalHarmonics = DefaultImportSphericalHarmonics;
            public SHBand shBand = DefaultImportedSHBand;
            public bool compressColorAlphaToBC7 = DefaultCompressColorAlphaToBC7;
            public bool compressSHToBC7 = DefaultCompressSHToBC7;
            public int startRenderQueue = DefaultStartRenderQueue;
        }

        class PreviewData
        {
            public string path;
            public Vector3[] positions;
            public Color[] colors;
            public Bounds bounds;
            public int splatCount;
            public string error;
        }

        class GaussianSplatImportPreviewStage : PreviewSceneStage
        {
            PlyImportWizard _owner;
            Mesh _previewMesh;
            Material _previewMaterial;
            Bounds _previewBounds;
            bool _showCropBounds;
            Bounds _cropBounds;
            GameObject _previewObject;
            BoxBoundsHandle _boundsHandle = new BoxBoundsHandle();
            bool _visible;
            bool _frameOnRebuild;

            public void Initialize(PlyImportWizard owner)
            {
                _owner = owner;
                name = "Gaussian Splat Preview";
            }

            protected override GUIContent CreateHeaderContent()
            {
                return GSEditorText.C("Gaussian Splat Preview", "Gaussian Splat プレビュー");
            }

            protected override bool OnOpenStage()
            {
                if (!base.OnOpenStage())
                {
                    return false;
                }

                SceneView.duringSceneGui -= OnSceneGUI;
                SceneView.duringSceneGui += OnSceneGUI;
                RebuildContent();
                return true;
            }

            protected override void OnCloseStage()
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                _owner?.OnPreviewStageClosed(this);
                base.OnCloseStage();
            }

            public void SetPreview(Mesh previewMesh, Material previewMaterial, Bounds previewBounds, bool showCropBounds, Bounds cropBounds)
            {
                _previewMesh = previewMesh;
                _previewMaterial = previewMaterial;
                _previewBounds = previewBounds;
                _showCropBounds = showCropBounds;
                _cropBounds = cropBounds;
                if (scene.IsValid())
                {
                    if (_visible) RebuildContent();
                    else DestroyStageObjects();
                }
            }

            public void FrameNextRebuild()
            {
                _frameOnRebuild = true;
            }

            public void SetCropVisible(bool showCropBounds, Bounds cropBounds)
            {
                _showCropBounds = showCropBounds;
                _cropBounds = cropBounds;
                if (scene.IsValid())
                {
                    UpdatePreviewMaterial();
                }
            }

            public void SetVisible(bool visible)
            {
                if (_visible == visible)
                {
                    return;
                }
                _visible = visible;
                if (!scene.IsValid())
                {
                    return;
                }
                if (_visible) RebuildContent();
                else DestroyStageObjects();
                SceneView.RepaintAll();
            }

            public bool TryGetCropBounds(out Bounds bounds)
            {
                if (!_showCropBounds)
                {
                    bounds = default;
                    return false;
                }

                bounds = _cropBounds;
                return true;
            }

            void RebuildContent()
            {
                DestroyStageObjects();
                if (_previewMesh == null || _previewMaterial == null)
                {
                    return;
                }

                _previewObject = new GameObject("PLY Preview Points");
                _previewObject.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(_previewObject, scene);
                _previewObject.AddComponent<MeshFilter>().sharedMesh = _previewMesh;
                _previewObject.AddComponent<MeshRenderer>().sharedMaterial = _previewMaterial;

                UpdatePreviewMaterial();
                if (_frameOnRebuild)
                {
                    SceneView.lastActiveSceneView?.Frame(new Bounds(_previewBounds.center, _previewBounds.size), false);
                    _frameOnRebuild = false;
                }
            }

            void OnSceneGUI(SceneView sceneView)
            {
                if (!_visible || StageUtility.GetCurrentStage() != this)
                {
                    return;
                }

                if (_showCropBounds)
                {
                    Bounds previewCrop = new Bounds(ToPreviewSpace(_cropBounds.center), _cropBounds.size);
                    _boundsHandle.center = previewCrop.center;
                    _boundsHandle.size = previewCrop.size;
                    using (new Handles.DrawingScope(Color.yellow))
                    {
                        EditorGUI.BeginChangeCheck();
                        _boundsHandle.DrawHandle();
                        if (EditorGUI.EndChangeCheck())
                        {
                            _cropBounds = new Bounds(FromPreviewSpace(_boundsHandle.center), Abs(_boundsHandle.size));
                            _owner?.SetPreviewCropBoundsFromStage(_cropBounds);
                            UpdatePreviewMaterial();
                        }
                    }
                }
                _owner?.OnPreviewStageSceneGUI(sceneView);
            }

            void UpdatePreviewMaterial()
            {
                SetPreviewMaterialCrop(_previewMaterial, _showCropBounds, _cropBounds);
            }

            void DestroyStageObjects()
            {
                if (_previewObject != null) DestroyImmediate(_previewObject);
                _previewObject = null;
            }

            static Vector3 Abs(Vector3 value)
            {
                return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
            }
        }

        List<ImportEntry> _entries = new();
        string _outputFolder = DefaultOutputFolder;
        bool _computeBoundingBox = DefaultComputeBoundingBox;
        bool _multiPassRendering = DefaultMultiPassRendering;
        int _splatsPerPass = DefaultSplatsPerPass;
        bool _precomputeSorting = DefaultPrecomputeSorting;
        int _maxAlphaMaskCount = DefaultMaxAlphaMaskCount;
        bool _useSRGB = DefaultUseSRGB;
        bool _importSphericalHarmonics = DefaultImportSphericalHarmonics;
        SHBand _defaultSHBand = DefaultImportedSHBand;
        bool _compressColorAlphaToBC7 = DefaultCompressColorAlphaToBC7;
        bool _compressSHToBC7 = DefaultCompressSHToBC7;
        int _startRenderQueue = DefaultStartRenderQueue;
        int _selectedEntryIndex = -1;
        Vector2 scrollPosition = Vector2.zero;
        CancellationTokenSource _previewCancellation;
        Task<PreviewData> _previewTask;
        string _previewPath;
        PreviewData _previewData;
        Mesh _previewMesh;
        Material _previewMaterial;
        Bounds _previewBounds = new Bounds(Vector3.zero, Vector3.one);
        GaussianSplatImportPreviewStage _previewStage;
        bool _framePreviewOnLoad;

        public static PlyImportWizard OpenWithPly(string plyPath)
        {
            PlyImportWizard window = GetWindow<PlyImportWizard>();
            window.titleContent = GSEditorText.C("PLY Import", "PLY インポート");
            window.Show();
            window.Focus();

            if (!string.IsNullOrEmpty(plyPath))
            {
                window._entries.Clear();
                window._entries.Add(new ImportEntry { path = plyPath });
                window.SelectEntry(0);

                string outputFolder = Path.GetDirectoryName(plyPath);
                if (!string.IsNullOrEmpty(outputFolder))
                {
                    window._outputFolder = outputFolder.Replace('\\', '/');
                }
            }

            return window;
        }

        void OnEnable()
        {
            EditorApplication.update -= EditorTick;
            EditorApplication.update += EditorTick;
            ImportEntry entry = SelectedEntry;
            if (entry != null)
            {
                StartPreviewLoad(entry.path);
            }
        }

        void OnDisable()
        {
            EditorApplication.update -= EditorTick;
            CancelPreviewLoad();
            DestroyPreviewObject();
        }

        void OnFocus()
        {
            ImportEntry entry = SelectedEntry;
            if (entry != null)
            {
                if ((_previewMesh == null || _previewMaterial == null) && _previewData != null) CreatePreviewObject(_previewData);
                else if (_previewMesh == null || _previewMaterial == null) StartPreviewLoad(entry.path);
                else OpenPreviewStage(false);
            }
            UpdatePreviewVisibility();
        }

        void OnLostFocus()
        {
            EditorApplication.delayCall += UpdatePreviewVisibility;
        }

        public static void ImportWithDefaults(string plyPath, string prefabPath)
        {
            PlySplatImporter.Import(
                plyPath,
                prefabPath,
                DefaultComputeBoundingBox,
                DefaultSplatsPerPass,
                DefaultPrecomputeSorting,
                DefaultMaxAlphaMaskCount,
                DefaultUseSRGB,
                DefaultImportSphericalHarmonics,
                DefaultImportedSHBand,
                DefaultCompressColorAlphaToBC7,
                DefaultCompressSHToBC7);
        }

        [MenuItem("Gaussian Splatting/Import PLY Splats…")]
        static void Init()
        {
            PlyImportWizard window = GetWindow<PlyImportWizard>();
            window.titleContent = GSEditorText.C("PLY Import", "PLY インポート");
            window.Show();
        }

        ImportEntry SelectedEntry => _selectedEntryIndex >= 0 && _selectedEntryIndex < _entries.Count ? _entries[_selectedEntryIndex] : null;

        void AddEntry(string path = "")
        {
            _entries.Add(new ImportEntry { path = path });
            SelectEntry(_entries.Count - 1);
        }

        void SelectEntry(int index)
        {
            _selectedEntryIndex = Mathf.Clamp(index, -1, _entries.Count - 1);
            ImportEntry entry = SelectedEntry;
            if (entry != null)
            {
                _framePreviewOnLoad = true;
                StartPreviewLoad(entry.path);
            }
            UpdatePreviewVisibility();
        }

        static Vector3 ToPreviewSpace(Vector3 pos) { return new Vector3(pos.x, -pos.y, pos.z); }
        static Vector3 FromPreviewSpace(Vector3 pos) { return new Vector3(pos.x, -pos.y, pos.z); }
        static Vector3 ApplyPreviewAlignment(ImportEntry entry, Vector3 pos)
        {
            if (entry == null || !entry.applyHorizonAlignment)
            {
                return pos;
            }
            Quaternion rotation = (entry.applyWallAlignment ? entry.wallRotation : Quaternion.identity) * entry.horizonRotation;
            return rotation * (pos - entry.horizonPivot);
        }

        static PreviewData LoadPreviewData(string path, CancellationToken token)
        {
            PreviewData preview = new PreviewData { path = path };
            try
            {
                GaussianFileReader.ReadFile(path, 0, out NativeArray<ImportSplatData> splats, out NativeArray<Vector3> shCoeffs);
                try
                {
                    token.ThrowIfCancellationRequested();
                    preview.splatCount = splats.Length;
                    int previewCount = Mathf.Min(MaxPreviewSplats, splats.Length);
                    preview.positions = new Vector3[previewCount];
                    preview.colors = new Color[previewCount];
                    Bounds bounds = new Bounds();
                    bool hasBounds = false;
                    for (int i = 0; i < splats.Length; i++)
                    {
                        Vector3 pos = splats[i].pos;
                        if (!hasBounds)
                        {
                            bounds = new Bounds(pos, Vector3.zero);
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(pos);
                        }
                    }

                    int step = Mathf.Max(1, splats.Length / Mathf.Max(1, previewCount));
                    for (int i = 0; i < previewCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        ImportSplatData splat = splats[Mathf.Min(splats.Length - 1, i * step)];
                        preview.positions[i] = splat.pos;
                        preview.colors[i] = new Color(splat.dc0.x, splat.dc0.y, splat.dc0.z, Mathf.Clamp01(splat.opacity));
                    }
                    preview.bounds = hasBounds ? bounds : new Bounds(Vector3.zero, Vector3.one);
                }
                finally
                {
                    if (splats.IsCreated) splats.Dispose();
                    if (shCoeffs.IsCreated) shCoeffs.Dispose();
                }
            }
            catch (Exception e)
            {
                preview.error = e.Message;
            }
            return preview;
        }

        void StartPreviewLoad(string path)
        {
            CancelPreviewLoad();
            DestroyPreviewObject();
            _previewPath = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            _previewCancellation = new CancellationTokenSource();
            CancellationToken token = _previewCancellation.Token;
            _previewTask = Task.Run(() => LoadPreviewData(path, token), token);
        }

        void CancelPreviewLoad()
        {
            if (_previewCancellation != null)
            {
                _previewCancellation.Cancel();
                _previewCancellation = null;
            }
            _previewTask = null;
        }

        void PollPreviewLoad()
        {
            if (_previewTask == null || !_previewTask.IsCompleted)
            {
                return;
            }

            PreviewData preview;
            try
            {
                preview = _previewTask.Result;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Gaussian splat preview failed: " + e.Message);
                _previewTask = null;
                return;
            }
            _previewTask = null;
            if (preview.path != _previewPath || !string.IsNullOrEmpty(preview.error))
            {
                if (!string.IsNullOrEmpty(preview.error))
                {
                    Debug.LogWarning("Gaussian splat preview failed: " + preview.error);
                }
                return;
            }

            _previewData = preview;
            CreatePreviewObject(preview);
            ImportEntry entry = SelectedEntry;
            if (entry != null && entry.cropBounds.size == Vector3.one && entry.cropBounds.center == Vector3.zero)
            {
                entry.cropBounds = preview.bounds;
            }
            if (IsPreviewActive())
            {
                OpenPreviewStage(_framePreviewOnLoad);
            }
            _framePreviewOnLoad = false;
            Repaint();
        }

        void DestroyPreviewObject()
        {
            if (_previewStage != null)
            {
                StageUtility.GoToMainStage();
                DestroyImmediate(_previewStage);
                _previewStage = null;
            }
            if (_previewMesh != null) DestroyImmediate(_previewMesh);
            if (_previewMaterial != null) DestroyImmediate(_previewMaterial);
            _previewData = null;
            _previewMesh = null;
            _previewMaterial = null;
            _previewBounds = new Bounds(Vector3.zero, Vector3.one);
        }

        void CreatePreviewObject(PreviewData preview)
        {
            if (_previewMesh != null) DestroyImmediate(_previewMesh);
            if (_previewMaterial != null) DestroyImmediate(_previewMaterial);

            ImportEntry entry = SelectedEntry;
            int splatCount = Mathf.Min(preview.positions.Length, preview.colors.Length);
            Vector3[] vertices = new Vector3[splatCount * 4];
            Color[] colors = new Color[splatCount * 4];
            Vector2[] uvs = new Vector2[splatCount * 4];
            int[] triangles = new int[splatCount * 6];
            Bounds previewBounds = new Bounds();
            bool hasBounds = false;
            for (int i = 0; i < splatCount; i++)
            {
                int vertex = i * 4;
                int index = i * 6;
                Vector3 center = ToPreviewSpace(ApplyPreviewAlignment(entry, preview.positions[i]));
                Color color = preview.colors[i];
                vertices[vertex] = center;
                vertices[vertex + 1] = center;
                vertices[vertex + 2] = center;
                vertices[vertex + 3] = center;
                colors[vertex] = color;
                colors[vertex + 1] = color;
                colors[vertex + 2] = color;
                colors[vertex + 3] = color;
                uvs[vertex] = new Vector2(-1.0f, -1.0f);
                uvs[vertex + 1] = new Vector2(1.0f, -1.0f);
                uvs[vertex + 2] = new Vector2(-1.0f, 1.0f);
                uvs[vertex + 3] = new Vector2(1.0f, 1.0f);
                triangles[index] = vertex;
                triangles[index + 1] = vertex + 2;
                triangles[index + 2] = vertex + 1;
                triangles[index + 3] = vertex + 1;
                triangles[index + 4] = vertex + 2;
                triangles[index + 5] = vertex + 3;
                if (hasBounds) previewBounds.Encapsulate(center);
                else
                {
                    previewBounds = new Bounds(center, Vector3.zero);
                    hasBounds = true;
                }
            }
            _previewBounds = hasBounds ? previewBounds : new Bounds(Vector3.zero, Vector3.one);

            _previewMesh = new Mesh { name = "Gaussian Splat Import Preview Mesh", indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            _previewMesh.hideFlags = HideFlags.HideAndDontSave;
            _previewMesh.vertices = vertices;
            _previewMesh.colors = colors;
            _previewMesh.uv = uvs;
            _previewMesh.triangles = triangles;
            _previewMesh.bounds = _previewBounds;

            _previewMaterial = CreatePreviewMaterial(PreviewSplatPixelRadius);
            UpdatePreviewStage();
        }

        static Material CreatePreviewMaterial(float pointSizePixels)
        {
            Material material = new Material(Shader.Find("Hidden/VRChatGaussianSplatting/ImportPreviewSplat"));
            material.hideFlags = HideFlags.HideAndDontSave;
            material.SetFloat("_PointSize", pointSizePixels);
            SetPreviewMaterialCrop(material, false, new Bounds(Vector3.zero, Vector3.one));
            return material;
        }

        static void SetPreviewMaterialCrop(Material material, bool cropEnabled, Bounds cropBounds)
        {
            if (material == null)
            {
                return;
            }
            Bounds previewBounds = new Bounds(ToPreviewSpace(cropBounds.center), cropBounds.size);
            material.SetFloat("_CropEnabled", cropEnabled ? 1.0f : 0.0f);
            material.SetVector("_CropMin", previewBounds.min);
            material.SetVector("_CropMax", previewBounds.max);
        }

        void EditorTick()
        {
            if (_previewTask != null && !_previewTask.IsCompleted)
            {
                Repaint();
            }
            UpdatePreviewVisibility();
            SyncPreviewStageCrop();
        }

        void OpenPreviewStage(bool framePreview)
        {
            if (_previewStage == null)
            {
                _previewStage = CreateInstance<GaussianSplatImportPreviewStage>();
                _previewStage.hideFlags = HideFlags.HideAndDontSave;
                _previewStage.Initialize(this);
            }
            if (framePreview)
            {
                _previewStage.FrameNextRebuild();
            }
            UpdatePreviewStage();
            StageUtility.GoToStage(_previewStage, true);
            UpdatePreviewVisibility();
        }

        void ClosePreviewStage()
        {
            if (_previewStage == null)
            {
                return;
            }
            StageUtility.GoToMainStage();
            DestroyImmediate(_previewStage);
            _previewStage = null;
        }

        void OnPreviewStageClosed(GaussianSplatImportPreviewStage stage)
        {
            if (_previewStage == stage)
            {
                _previewStage = null;
            }
        }

        void SetPreviewCropBoundsFromStage(Bounds cropBounds)
        {
            ImportEntry entry = SelectedEntry;
            if (entry == null)
            {
                return;
            }
            entry.cropBounds = cropBounds;
            Repaint();
        }

        internal void OnPreviewStageSceneGUI(SceneView sceneView)
        {
            ImportEntry entry = SelectedEntry;
            if (entry == null || _previewData == null)
            {
                return;
            }

            DrawHorizonPoints(entry);
            DrawWallPoints(entry);
            if (!entry.horizonPickMode && !entry.wallPickMode)
            {
                return;
            }

            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event evt = Event.current;
            if (evt.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(controlId);
            }
            if (evt.type == EventType.MouseDown && evt.button == 0 && !evt.alt && TryPickPreviewSplat(entry, evt.mousePosition, out Vector3 point))
            {
                if (entry.horizonPickMode) TogglePoint(entry.horizonPoints, point);
                else TogglePoint(entry.wallPoints, point);
                evt.Use();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        void DrawWallPoints(ImportEntry entry)
        {
            for (int i = 0; i < entry.wallPoints.Count; i++)
            {
                Vector3 previewPos = ToPreviewSpace(ApplyPreviewAlignment(entry, entry.wallPoints[i]));
                float size = HandleUtility.GetHandleSize(previewPos) * 0.045f;
                Handles.color = Color.red;
                Handles.SphereHandleCap(0, previewPos, Quaternion.identity, size, EventType.Repaint);
            }

            if (entry.wallPoints.Count >= 3 && TryFitPlane(entry.wallPoints, out Vector3 normal, out Vector3 center))
            {
                DrawFittedPlane(entry, normal, center, new Color(1.0f, 0.05f, 0.0f, 0.8f));
            }
        }

        void DrawHorizonPoints(ImportEntry entry)
        {
            for (int i = 0; i < entry.horizonPoints.Count; i++)
            {
                Vector3 previewPos = ToPreviewSpace(ApplyPreviewAlignment(entry, entry.horizonPoints[i]));
                float size = HandleUtility.GetHandleSize(previewPos) * 0.045f;
                Handles.color = Color.cyan;
                Handles.SphereHandleCap(0, previewPos, Quaternion.identity, size, EventType.Repaint);
            }

            if (entry.horizonPoints.Count >= 3 && TryFitPlane(entry.horizonPoints, out Vector3 normal, out Vector3 center))
            {
                DrawFittedPlane(entry, normal, center, new Color(0.0f, 0.8f, 1.0f, 0.8f));
            }
        }

        void DrawFittedPlane(ImportEntry entry, Vector3 normal, Vector3 center, Color color)
        {
            Vector3 tangent = Vector3.Cross(normal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-6f) tangent = Vector3.Cross(normal, Vector3.right);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
            float size = Mathf.Max(0.01f, _previewBounds.extents.magnitude * 0.35f);
            Vector3 p0 = ToPreviewSpace(ApplyPreviewAlignment(entry, center - tangent * size - bitangent * size));
            Vector3 p1 = ToPreviewSpace(ApplyPreviewAlignment(entry, center + tangent * size - bitangent * size));
            Vector3 p2 = ToPreviewSpace(ApplyPreviewAlignment(entry, center + tangent * size + bitangent * size));
            Vector3 p3 = ToPreviewSpace(ApplyPreviewAlignment(entry, center - tangent * size + bitangent * size));
            Handles.color = color;
            Handles.DrawAAPolyLine(3.0f, p0, p1, p2, p3, p0);
        }

        bool TryPickPreviewSplat(ImportEntry entry, Vector2 mousePosition, out Vector3 point)
        {
            point = default;
            if (_previewData == null || _previewData.positions == null)
            {
                return false;
            }

            float bestDistance = 14.0f;
            bool found = false;
            for (int i = 0; i < _previewData.positions.Length; i++)
            {
                Vector3 previewPos = ToPreviewSpace(ApplyPreviewAlignment(entry, _previewData.positions[i]));
                Vector2 guiPos = HandleUtility.WorldToGUIPoint(previewPos);
                float distance = Vector2.Distance(guiPos, mousePosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    point = _previewData.positions[i];
                    found = true;
                }
            }
            return found;
        }

        void TogglePoint(List<Vector3> points, Vector3 point)
        {
            float threshold = Mathf.Max(1e-6f, _previewData.bounds.extents.magnitude * 1e-5f);
            float thresholdSqr = threshold * threshold;
            for (int i = 0; i < points.Count; i++)
            {
                if ((points[i] - point).sqrMagnitude <= thresholdSqr)
                {
                    points.RemoveAt(i);
                    return;
                }
            }
            points.Add(point);
        }

        void ApplyHorizonAlignment(ImportEntry entry)
        {
            if (!TryFitPlane(entry.horizonPoints, out Vector3 normal, out Vector3 center))
            {
                return;
            }
            if (normal.y < 0.0f)
            {
                normal = -normal;
            }
            entry.applyHorizonAlignment = true;
            entry.horizonRotation = Quaternion.FromToRotation(normal, Vector3.up);
            entry.horizonPivot = center;
            entry.applyWallAlignment = false;
            entry.wallRotation = Quaternion.identity;
            UpdateCropBoundsToPreview(entry);
            RebuildPreviewFromCache(true);
        }

        void ApplyWallAlignment(ImportEntry entry)
        {
            if (!entry.applyHorizonAlignment || !TryFitPlane(entry.wallPoints, out Vector3 normal, out _))
            {
                return;
            }

            Vector3 alignedNormal = entry.horizonRotation * normal;
            alignedNormal.y = 0.0f;
            if (alignedNormal.sqrMagnitude < 1e-6f)
            {
                return;
            }
            alignedNormal.Normalize();
            entry.applyWallAlignment = true;
            entry.wallRotation = Quaternion.FromToRotation(alignedNormal, Vector3.right);
            UpdateCropBoundsToPreview(entry);
            RebuildPreviewFromCache(true);
        }

        void ResetWallAlignment(ImportEntry entry)
        {
            entry.applyWallAlignment = false;
            entry.wallRotation = Quaternion.identity;
            RebuildPreviewFromCache(true);
        }

        void ResetHorizonAlignment(ImportEntry entry)
        {
            entry.applyHorizonAlignment = false;
            entry.horizonRotation = Quaternion.identity;
            entry.horizonPivot = Vector3.zero;
            entry.applyWallAlignment = false;
            entry.wallRotation = Quaternion.identity;
            UpdateCropBoundsToPreview(entry);
            RebuildPreviewFromCache(true);
        }

        void UpdateCropBoundsToPreview(ImportEntry entry)
        {
            if (entry == null || _previewData == null || !entry.cropToBounds)
            {
                return;
            }
            Bounds bounds = new Bounds();
            bool hasBounds = false;
            for (int i = 0; i < _previewData.positions.Length; i++)
            {
                Vector3 pos = ApplyPreviewAlignment(entry, _previewData.positions[i]);
                if (hasBounds) bounds.Encapsulate(pos);
                else
                {
                    bounds = new Bounds(pos, Vector3.zero);
                    hasBounds = true;
                }
            }
            if (!hasBounds)
            {
                return;
            }
            entry.cropBounds = bounds;
            _previewStage?.SetCropVisible(entry.cropToBounds, entry.cropBounds);
        }

        void RebuildPreviewFromCache(bool framePreview)
        {
            if (_previewData == null)
            {
                return;
            }
            CreatePreviewObject(_previewData);
            if (IsPreviewActive())
            {
                OpenPreviewStage(framePreview);
            }
            Repaint();
            SceneView.RepaintAll();
        }

        static bool TryFitPlane(List<Vector3> points, out Vector3 normal, out Vector3 center)
        {
            normal = Vector3.up;
            center = Vector3.zero;
            if (points == null || points.Count < 3)
            {
                return false;
            }

            for (int i = 0; i < points.Count; i++)
            {
                center += points[i];
            }
            center /= points.Count;

            float[,] a = new float[3, 3];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 d = points[i] - center;
                a[0, 0] += d.x * d.x; a[0, 1] += d.x * d.y; a[0, 2] += d.x * d.z;
                a[1, 1] += d.y * d.y; a[1, 2] += d.y * d.z;
                a[2, 2] += d.z * d.z;
            }
            a[1, 0] = a[0, 1];
            a[2, 0] = a[0, 2];
            a[2, 1] = a[1, 2];

            float[,] v = new float[3, 3];
            v[0, 0] = 1.0f; v[1, 1] = 1.0f; v[2, 2] = 1.0f;
            for (int iter = 0; iter < 12; iter++)
            {
                int p = 0, q = 1;
                float max = Mathf.Abs(a[0, 1]);
                if (Mathf.Abs(a[0, 2]) > max) { p = 0; q = 2; max = Mathf.Abs(a[0, 2]); }
                if (Mathf.Abs(a[1, 2]) > max) { p = 1; q = 2; max = Mathf.Abs(a[1, 2]); }
                if (max < 1e-8f) break;

                float theta = (a[q, q] - a[p, p]) / (2.0f * a[p, q]);
                float t = Mathf.Abs(theta) < 1e-8f ? 1.0f : Mathf.Sign(theta) / (Mathf.Abs(theta) + Mathf.Sqrt(theta * theta + 1.0f));
                if (float.IsNaN(t)) t = 1.0f;
                float c = 1.0f / Mathf.Sqrt(t * t + 1.0f);
                float s = t * c;
                float app = a[p, p];
                float aqq = a[q, q];
                float apq = a[p, q];
                a[p, p] = c * c * app - 2.0f * s * c * apq + s * s * aqq;
                a[q, q] = s * s * app + 2.0f * s * c * apq + c * c * aqq;
                a[p, q] = 0.0f;
                a[q, p] = 0.0f;
                for (int r = 0; r < 3; r++)
                {
                    if (r == p || r == q) continue;
                    float arp = a[r, p];
                    float arq = a[r, q];
                    a[r, p] = c * arp - s * arq;
                    a[p, r] = a[r, p];
                    a[r, q] = s * arp + c * arq;
                    a[q, r] = a[r, q];
                }
                for (int r = 0; r < 3; r++)
                {
                    float vrp = v[r, p];
                    float vrq = v[r, q];
                    v[r, p] = c * vrp - s * vrq;
                    v[r, q] = s * vrp + c * vrq;
                }
            }

            int minIndex = a[1, 1] < a[0, 0] ? 1 : 0;
            if (a[2, 2] < a[minIndex, minIndex]) minIndex = 2;
            normal = new Vector3(v[0, minIndex], v[1, minIndex], v[2, minIndex]).normalized;
            return normal.sqrMagnitude > 0.5f;
        }

        void UpdatePreviewStage()
        {
            ImportEntry entry = SelectedEntry;
            if (_previewStage == null || entry == null)
            {
                return;
            }
            _previewStage.SetPreview(_previewMesh, _previewMaterial, _previewBounds, entry.cropToBounds, entry.cropBounds);
            _previewStage.SetVisible(IsPreviewActive());
        }

        bool IsPreviewActive()
        {
            EditorWindow currentFocusedWindow = UnityEditor.EditorWindow.focusedWindow;
            return currentFocusedWindow == this || (currentFocusedWindow is SceneView && _previewStage != null && StageUtility.GetCurrentStage() == _previewStage);
        }

        void UpdatePreviewVisibility()
        {
            if (_previewStage != null)
            {
                bool active = IsPreviewActive();
                if (!active)
                {
                    ClosePreviewStage();
                    return;
                }
                _previewStage.SetVisible(_previewMesh != null && _previewMaterial != null && SelectedEntry != null);
            }
        }

        void SyncPreviewStageCrop()
        {
            ImportEntry entry = SelectedEntry;
            if (_previewStage == null || entry == null || !entry.cropToBounds)
            {
                return;
            }
            if (!_previewStage.TryGetCropBounds(out Bounds cropBounds))
            {
                return;
            }
            if (entry.cropBounds.center == cropBounds.center && entry.cropBounds.size == cropBounds.size)
            {
                return;
            }
            entry.cropBounds = cropBounds;
            Repaint();
        }

        PlySplatImporter.ImportOptions GetGlobalOptions()
        {
            return new PlySplatImporter.ImportOptions
            {
                computeBoundingBox = _computeBoundingBox,
                splatsPerPass = _multiPassRendering ? _splatsPerPass : 0,
                precomputeSorting = _precomputeSorting,
                maxAlphaMaskCount = _maxAlphaMaskCount,
                useSRGB = _useSRGB,
                importSphericalHarmonics = _importSphericalHarmonics,
                defaultSHBand = _defaultSHBand,
                compressColorAlphaToBC7 = _compressColorAlphaToBC7,
                compressSHToBC7 = _compressSHToBC7,
                startRenderQueue = _startRenderQueue
            };
        }

        PlySplatImporter.ImportOptions GetEntryOptions(ImportEntry entry)
        {
            PlySplatImporter.ImportOptions options = entry.overrideSettings
                ? new PlySplatImporter.ImportOptions
                {
                    computeBoundingBox = entry.computeBoundingBox,
                    splatsPerPass = entry.multiPassRendering ? entry.splatsPerPass : 0,
                    precomputeSorting = entry.precomputeSorting,
                    maxAlphaMaskCount = entry.maxAlphaMaskCount,
                    useSRGB = entry.useSRGB,
                    importSphericalHarmonics = entry.importSphericalHarmonics,
                    defaultSHBand = entry.shBand,
                    compressColorAlphaToBC7 = entry.compressColorAlphaToBC7,
                    compressSHToBC7 = entry.compressSHToBC7,
                    startRenderQueue = entry.startRenderQueue
                }
                : GetGlobalOptions();

            options.cropToBounds = entry.cropToBounds;
            options.cropBounds = entry.cropBounds;
            options.applyHorizonAlignment = entry.applyHorizonAlignment;
            options.horizonRotation = (entry.applyWallAlignment ? entry.wallRotation : Quaternion.identity) * entry.horizonRotation;
            options.horizonPivot = entry.horizonPivot;
            return options;
        }

        void DrawSelectedEntrySettings()
        {
            ImportEntry entry = SelectedEntry;
            if (entry == null)
            {
                return;
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(GSEditorText.T("Selected Splat", "選択中の Splat"), EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_previewTask != null && !_previewTask.IsCompleted))
            {
                if (GUILayout.Button(GSEditorText.T("Reload Preview", "プレビューを再読み込み")))
                {
                    StartPreviewLoad(entry.path);
                }
            }
            if (_previewTask != null && !_previewTask.IsCompleted)
            {
                EditorGUILayout.HelpBox(GSEditorText.T("Loading preview asynchronously...", "プレビューを非同期で読み込み中..."), MessageType.Info);
            }
            using (new EditorGUI.DisabledScope(_previewMesh == null || _previewMaterial == null))
            {
                if (GUILayout.Button(GSEditorText.T("Open Isolated Preview Stage", "隔離プレビューステージを開く")))
                {
                    OpenPreviewStage(true);
                }
            }
            if (_previewStage != null && GUILayout.Button(GSEditorText.T("Close Preview Stage", "プレビューステージを閉じる")))
            {
                ClosePreviewStage();
            }
            EditorGUILayout.HelpBox(GSEditorText.T(
                "The preview stage uses temporary editor objects only. Use Scene View move/scale tools on Crop Bounds to edit the import crop.",
                "プレビューステージは一時的なエディターオブジェクトのみを使用します。Scene ビューの移動/スケールツールで Crop Bounds を編集できます。"), MessageType.Info);

            EditorGUI.BeginChangeCheck();
            entry.cropToBounds = EditorGUILayout.Toggle(GSEditorText.T("Crop To Bounds", "境界でクロップ"), entry.cropToBounds);
            using (new EditorGUI.DisabledScope(!entry.cropToBounds))
            {
                entry.cropBounds.center = EditorGUILayout.Vector3Field(GSEditorText.T("Crop Center", "クロップ中心"), entry.cropBounds.center);
                entry.cropBounds.size = Vector3.Max(Vector3.one * 0.0001f, EditorGUILayout.Vector3Field(GSEditorText.T("Crop Size", "クロップサイズ"), entry.cropBounds.size));
            }
            if (EditorGUI.EndChangeCheck())
            {
                _previewStage?.SetCropVisible(entry.cropToBounds, entry.cropBounds);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(GSEditorText.T("Horizon Alignment", "水平線合わせ"), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            entry.horizonPickMode = EditorGUILayout.Toggle(GSEditorText.T("Pick Horizon Points", "水平点を選択"), entry.horizonPickMode);
            if (EditorGUI.EndChangeCheck())
            {
                if (entry.horizonPickMode)
                {
                    entry.wallPickMode = false;
                    if (_previewMesh != null && _previewMaterial != null) OpenPreviewStage(true);
                }
            }
            EditorGUILayout.LabelField(GSEditorText.T("Picked Points", "選択点"), entry.horizonPoints.Count.ToString());
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(entry.horizonPoints.Count == 0))
            {
                if (GUILayout.Button(GSEditorText.T("Clear Points", "点をクリア")))
                {
                    entry.horizonPoints.Clear();
                    SceneView.RepaintAll();
                }
            }
            using (new EditorGUI.DisabledScope(entry.horizonPoints.Count < 3))
            {
                if (GUILayout.Button(GSEditorText.T("Apply Alignment", "合わせを適用")))
                {
                    ApplyHorizonAlignment(entry);
                }
            }
            using (new EditorGUI.DisabledScope(!entry.applyHorizonAlignment))
            {
                if (GUILayout.Button(GSEditorText.T("Reset Alignment", "合わせをリセット")))
                {
                    ResetHorizonAlignment(entry);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            entry.wallPickMode = EditorGUILayout.Toggle(GSEditorText.T("Pick Wall Points", "壁点を選択"), entry.wallPickMode);
            if (EditorGUI.EndChangeCheck())
            {
                if (entry.wallPickMode)
                {
                    entry.horizonPickMode = false;
                    if (_previewMesh != null && _previewMaterial != null) OpenPreviewStage(true);
                }
            }
            EditorGUILayout.LabelField(GSEditorText.T("Picked Wall Points", "選択した壁点"), entry.wallPoints.Count.ToString());
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(entry.wallPoints.Count == 0))
            {
                if (GUILayout.Button(GSEditorText.T("Clear Wall Points", "壁点をクリア")))
                {
                    entry.wallPoints.Clear();
                    SceneView.RepaintAll();
                }
            }
            using (new EditorGUI.DisabledScope(!entry.applyHorizonAlignment || entry.wallPoints.Count < 3))
            {
                if (GUILayout.Button(GSEditorText.T("Apply Wall", "壁合わせを適用")))
                {
                    ApplyWallAlignment(entry);
                }
            }
            using (new EditorGUI.DisabledScope(!entry.applyWallAlignment))
            {
                if (GUILayout.Button(GSEditorText.T("Reset Wall", "壁合わせをリセット")))
                {
                    ResetWallAlignment(entry);
                }
            }
            EditorGUILayout.EndHorizontal();

            entry.overrideSettings = EditorGUILayout.Toggle(GSEditorText.T("Override Import Settings", "インポート設定を上書き"), entry.overrideSettings);
            if (!entry.overrideSettings)
            {
                return;
            }

            EditorGUI.indentLevel++;
            entry.computeBoundingBox = EditorGUILayout.Toggle(GSEditorText.T("Compute Bounding Box", "バウンディングボックスを計算"), entry.computeBoundingBox);
            entry.useSRGB = EditorGUILayout.Toggle(GSEditorText.T("sRGB Color Correction", "sRGB 色補正"), entry.useSRGB);
            entry.importSphericalHarmonics = EditorGUILayout.Toggle(GSEditorText.T("Import Spherical Harmonics", "球面調和をインポート"), entry.importSphericalHarmonics);
            if (entry.importSphericalHarmonics)
            {
                entry.shBand = (SHBand)EditorGUILayout.EnumPopup(GSEditorText.T("Max imported SH Band", "インポート最大 SH バンド"), entry.shBand);
            }
            entry.compressColorAlphaToBC7 = EditorGUILayout.Toggle(GSEditorText.T("Compress ColorAlpha", "色アルファを圧縮"), entry.compressColorAlphaToBC7);
            entry.compressSHToBC7 = EditorGUILayout.Toggle(GSEditorText.T("Compress SH1+", "SH1+ を圧縮"), entry.compressSHToBC7);
            if (entry.useSRGB)
            {
                entry.multiPassRendering = EditorGUILayout.Toggle(GSEditorText.T("Multi-Pass Rendering", "マルチパス描画"), entry.multiPassRendering);
                if (entry.multiPassRendering)
                {
                    entry.splatsPerPass = Mathf.Clamp(EditorGUILayout.IntField(GSEditorText.T("Splat Count Per Pass", "パスごとの Splat 数"), entry.splatsPerPass), 128 * 1024, 8 * 1024 * 1024);
                    entry.maxAlphaMaskCount = Mathf.Max(0, EditorGUILayout.IntField(GSEditorText.T("Max Alpha Mask Count", "最大アルファマスク数"), entry.maxAlphaMaskCount));
                }
            }
            entry.precomputeSorting = EditorGUILayout.Toggle(GSEditorText.T("Precompute Sorting", "ソートを事前計算"), entry.precomputeSorting);
            entry.startRenderQueue = Mathf.Clamp(EditorGUILayout.IntField(GSEditorText.T("Start Render Queue", "開始レンダーキュー"), entry.startRenderQueue), 2000, 5000);
            EditorGUI.indentLevel--;
        }

        void OnGUI()
        {
            PollPreviewLoad();
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.LabelField(GSEditorText.T("PLY files", "PLY ファイル"), EditorStyles.boldLabel);
            if (GUILayout.Button(GSEditorText.T("Clear All PLYs", "すべての PLY をクリア")))
            {
                _entries.Clear();
                _selectedEntryIndex = -1;
                DestroyPreviewObject();
            }
            EditorGUILayout.BeginVertical(GUILayout.Height(100));
            for (int i = 0; i < _entries.Count; ++i)
            {
                ImportEntry entry = _entries[i];
                EditorGUILayout.BeginHorizontal();
                bool selected = GUILayout.Toggle(_selectedEntryIndex == i, GUIContent.none, GUILayout.Width(18));
                if (selected && _selectedEntryIndex != i)
                {
                    SelectEntry(i);
                }
                EditorGUI.BeginChangeCheck();
                entry.path = EditorGUILayout.TextField(entry.path);
                if (EditorGUI.EndChangeCheck() && _selectedEntryIndex == i)
                {
                    StartPreviewLoad(entry.path);
                }
                if (GUILayout.Button("…", GUILayout.Width(30)))
                {
                    string path = EditorUtility.OpenFilePanel(GSEditorText.T("Select PLY file", "PLY ファイルを選択"), Application.dataPath, "ply");
                    if (!string.IsNullOrEmpty(path))
                    {
                        entry.path = path;
                        SelectEntry(i);
                    }
                }
                if (GUILayout.Button("–", GUILayout.Width(20)))
                {
                    _entries.RemoveAt(i);
                    if (_selectedEntryIndex == i)
                    {
                        _selectedEntryIndex = -1;
                        DestroyPreviewObject();
                    }
                    else if (_selectedEntryIndex > i)
                    {
                        _selectedEntryIndex--;
                    }
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
            if (GUILayout.Button(GSEditorText.T("+ Add PLY file", "+ PLY ファイルを追加"))) AddEntry();
            if (GUILayout.Button(GSEditorText.T("Add All PLYs in Folder", "フォルダ内の PLY をすべて追加")))
            {
                string folder = EditorUtility.OpenFolderPanel(GSEditorText.T("Select Folder with PLY files", "PLY ファイルのあるフォルダを選択"), Application.dataPath, "");
                if (!string.IsNullOrEmpty(folder))
                {
                    string[] files = Directory.GetFiles(folder, "*.ply");
                    foreach (string file in files)
                    {
                        _entries.Add(new ImportEntry { path = file });
                    }
                    if (_selectedEntryIndex < 0 && _entries.Count > 0)
                    {
                        SelectEntry(0);
                    }
                }
            }

            EditorGUILayout.HelpBox(GSEditorText.T(
                "Large imports still depend on available RAM, but the PLY importer now streams vertex data so file size is no longer capped by a 2GB raw read buffer. SH import memory still scales with the selected SH band.",
                "大きなインポートは引き続き使用可能な RAM に依存しますが、PLY インポーターは頂点データをストリーミングするため、ファイルサイズは 2GB の生読み込みバッファに制限されなくなりました。SH インポートのメモリ使用量は選択した SH バンドに応じて増えます。"), MessageType.Info);

            DrawSelectedEntrySettings();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(GSEditorText.T("Output Folder", "出力フォルダ"), EditorStyles.boldLabel);
            _outputFolder = EditorGUILayout.TextField(_outputFolder);
            if (GUILayout.Button("…", GUILayout.Width(30)))
                _outputFolder = EditorUtility.OpenFolderPanel(GSEditorText.T("Select Output Folder", "出力フォルダを選択"), _outputFolder, "");

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField(GSEditorText.T("Splat settings", "Splat 設定"), EditorStyles.boldLabel);
            _computeBoundingBox   = EditorGUILayout.Toggle(GSEditorText.T("Compute Bounding Box", "バウンディングボックスを計算"), _computeBoundingBox);
            _useSRGB = EditorGUILayout.Toggle(GSEditorText.T("sRGB Color Correction", "sRGB 色補正"), _useSRGB);
            _importSphericalHarmonics = EditorGUILayout.Toggle(GSEditorText.T("Import Spherical Harmonics", "球面調和をインポート"), _importSphericalHarmonics);
            EditorGUILayout.Space(5f);
            EditorGUILayout.LabelField(GSEditorText.T("BC7 Compression", "BC7 圧縮"), EditorStyles.boldLabel);
            _compressColorAlphaToBC7 = EditorGUILayout.Toggle(GSEditorText.T("ColorAlpha", "色アルファ"), _compressColorAlphaToBC7);
            _compressSHToBC7 = EditorGUILayout.Toggle("SH1+", _compressSHToBC7);
            if (_compressColorAlphaToBC7 || _compressSHToBC7)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "The importer always packs generated splat textures into 4x4-aligned blocks. BC7 compression applies only to the selected generated textures; position, scale, and sorting textures stay uncompressed.",
                    "インポーターは生成される Splat テクスチャを常に 4x4 境界に合わせたブロックに詰めます。BC7 圧縮は選択した生成テクスチャにのみ適用され、位置・スケール・ソートテクスチャは非圧縮のままです。"), MessageType.Info);
                if (!_importSphericalHarmonics && _compressSHToBC7)
                {
                    EditorGUILayout.HelpBox(GSEditorText.T(
                        "SH1+ compression has no effect while Import Spherical Harmonics is disabled.",
                        "球面調和のインポートが無効な場合、SH1+ 圧縮は効果がありません。"), MessageType.Info);
                }
            }
            if (_importSphericalHarmonics)
            {
                _defaultSHBand = (SHBand)EditorGUILayout.EnumPopup(GSEditorText.T("Max imported SH Band", "インポート最大 SH バンド"), _defaultSHBand);
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Imports higher-order SH coefficient textures only up to the selected max band and sets the imported material to that band. If the selected band has no non-zero coefficients in the file, the importer falls back to the highest lower non-zero band.",
                    "選択した最大バンドまでの高次 SH 係数テクスチャだけをインポートし、インポートされたマテリアルをそのバンドに設定します。選択したバンドに非ゼロ係数がない場合は、より低い非ゼロの最大バンドにフォールバックします。"), MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Skips SH coefficient texture generation and forces imported materials to SH0 only.",
                    "SH 係数テクスチャの生成をスキップし、インポートされたマテリアルを SH0 のみにします。"), MessageType.Info);
            }
            EditorGUILayout.HelpBox(GSEditorText.T(
                "Color correction requires 2 additional grab passes, for small splats you might want to disable this. Without this enabled back to front rendering will be used, which makes multi-pass rendering not work. sRGB color correction only works correctly if the world has HDR camera render targets.",
                "色補正には追加で 2 回の Grab パスが必要です。小さな Splat では無効にした方がよい場合があります。これを有効にしない場合は後ろから前への描画になり、マルチパス描画は機能しません。sRGB 色補正は、ワールドのカメラレンダーターゲットが HDR の場合にのみ正しく動作します。"), MessageType.Info);
            if(_useSRGB) {
                _multiPassRendering   = EditorGUILayout.Toggle(GSEditorText.T("Multi-Pass Rendering", "マルチパス描画"), _multiPassRendering);
                if (_multiPassRendering)
                {
                    _splatsPerPass = EditorGUILayout.IntField(GSEditorText.T("Splat Count Per Pass", "パスごとの Splat 数"), _splatsPerPass);
                    EditorGUILayout.HelpBox(GSEditorText.T(
                        "The rendering of the splat is split into multiple sequential chunks, can help with VR rendering performance.",
                        "Splat の描画を複数の連続チャンクに分割します。VR 描画性能の改善に役立つ場合があります。"), MessageType.Info);
                    _splatsPerPass = Mathf.Clamp(_splatsPerPass, 128 * 1024, 8 * 1024 * 1024);
                    _maxAlphaMaskCount = EditorGUILayout.IntField(GSEditorText.T("Max Alpha Mask Count", "最大アルファマスク数"), _maxAlphaMaskCount);
                    EditorGUILayout.HelpBox(GSEditorText.T(
                        "After each chunk is rendered an optional alpha mask pass is added using a grab pass and stencil. This will occlude the following chunks if they are behind opaque objects. This can help performance, but grab pass can be expensive, so use it with care. If you have more than 4M splats you might want to have more than 1 alpha mask pass.",
                        "各チャンクの描画後に、Grab パスとステンシルを使った任意のアルファマスクパスを追加します。不透明オブジェクトの背後にある後続チャンクを遮蔽できます。性能改善に役立つ場合がありますが、Grab パスは高コストなので注意してください。400 万を超える Splat では、アルファマスクパスを 2 つ以上にした方がよい場合があります。"), MessageType.Info);
                }
                else
                {
                    _splatsPerPass = 0; // disable multi-pass rendering
                }
            }
            _precomputeSorting = EditorGUILayout.Toggle(GSEditorText.T("Precompute Sorting", "ソートを事前計算"), _precomputeSorting);
            if (_precomputeSorting)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Precomputing sorting for octahedral directions, makes the gaussian splatting work standalone, without the GaussianSplatRenderer. However this takes way more texture memory and might have rendering artifacts. THIS WILL NO LONGER WORK WITH GaussianSplatRenderer",
                    "八面体方向のソートを事前計算すると、GaussianSplatRenderer なしで Gaussian Splatting を単独動作させられます。ただし、テクスチャメモリを大幅に多く使用し、描画アーティファクトが出る場合があります。これは GaussianSplatRenderer では今後動作しません。"), MessageType.Warning);
            }

            EditorGUILayout.Space(5f);
            _startRenderQueue = EditorGUILayout.IntField(GSEditorText.T("Start Render Queue", "開始レンダーキュー"), _startRenderQueue);
            EditorGUILayout.HelpBox(GSEditorText.T(
                "Starting render queue for the generated splat materials. Each generated material is assigned a sequential queue from this value.",
                "生成される Splat マテリアルの開始レンダーキューです。各マテリアルにはこの値から順番にキューが割り当てられます。"), MessageType.Info);
            _startRenderQueue = Mathf.Clamp(_startRenderQueue, 2000, 5000);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(GSEditorText.T("Import All PLYs", "すべての PLY をインポート")))
            {
                if (!_entries.Any(e => !string.IsNullOrEmpty(e.path)))
                {
                    EditorUtility.DisplayDialog(GSEditorText.T("PLY Import", "PLY インポート"), GSEditorText.T("Add at least one PLY path.", "PLY パスを少なくとも 1 つ追加してください。"), "OK");
                    return;
                }

                foreach (ImportEntry entry in _entries.Where(e => !string.IsNullOrEmpty(e.path)))
                {
                    string ply = entry.path;
                    string prefabName = Path.GetFileNameWithoutExtension(ply) + ".prefab";
                    string relFolder  = FileUtil.GetProjectRelativePath(_outputFolder);
                    if (string.IsNullOrEmpty(relFolder))
                        relFolder = "Assets";
                    string prefabPath = Path.Combine(relFolder, prefabName).Replace('\\', '/');
                    ImportSingle(entry, prefabPath);
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog(GSEditorText.T("PLY Import", "PLY インポート"), GSEditorText.T("All imports completed.", "すべてのインポートが完了しました。"), "OK");
            }
            EditorGUILayout.EndScrollView();
        }

        void ImportSingle(ImportEntry entry, string prefabPath)
        {
            try
            {
                string plyPath = entry.path;
                EditorUtility.DisplayProgressBar(GSEditorText.T("PLY Import", "PLY インポート"),
                    GSEditorText.T($"Importing {Path.GetFileName(plyPath)}", $"{Path.GetFileName(plyPath)} をインポート中"), 0f);
                PlySplatImporter.Import(plyPath, prefabPath, GetEntryOptions(entry));
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(GSEditorText.T("PLY Import Failed", "PLY インポート失敗"), e.Message, "OK");
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
#endif
