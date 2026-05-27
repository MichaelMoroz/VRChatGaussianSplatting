
#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using Unity.Collections;  
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
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

    static public class PointsMesh
    {
        static public Mesh GetMesh(int splat_count, Bounds bbox)
        {
            int vertices = (splat_count + 31) / 32; // geometry shader will emit 32 quads per point, so we need at least 1 vertex per 32 splats
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[1];
            mesh.bounds = bbox;
            mesh.SetIndices(new int[vertices], MeshTopology.Points, 0, false, 0);
            return mesh;
        }

        public static Mesh GetMultiPassMesh(List<int> indexCounts, List<MeshTopology> topologies, Bounds bbox)
        {
            // Create mesh
            var mesh = new Mesh();
            mesh.vertices = new Vector3[3];
            mesh.subMeshCount = indexCounts.Count;

            // For each sub‑mesh, fill an index buffer with 0‑indices
            for (int i = 0; i < indexCounts.Count; i++)
            {
                int[] indices = new int[indexCounts[i]];
                if (indices.Length > 0) indices[0] = 0;
                if (indices.Length > 1) indices[1] = 1;
                if (indices.Length > 2) indices[2] = 2;
                mesh.SetIndices(indices, topologies[i], i, false, 0);
            }
            
            mesh.bounds = bbox;
            return mesh;
        }
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

        readonly struct TextureLayout
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

        static TextureLayout EvaluateTextureLayout(int width, int texelCount, bool alignHeightToBlocks)
        {
            int height = Mathf.CeilToInt((float)texelCount / width);
            if (alignHeightToBlocks)
            {
                height = Mathf.Max(4, ((height + 3) / 4) * 4);
            }

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

        static TextureLayout ChoosePotTextureLayout(int texelCount, bool alignHeightToBlocks)
        {
            int minWidth = alignHeightToBlocks ? 4 : 1;
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

            TextureLayout best = EvaluateTextureLayout(lowerWidth, texelCount, alignHeightToBlocks);
            if (upperWidth != lowerWidth)
            {
                TextureLayout candidate = EvaluateTextureLayout(upperWidth, texelCount, alignHeightToBlocks);
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

        static void SetMaterialSHBand(Material material, SHBand band)
        {
            material.SetFloat("_SHBand", (float)band);
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

        static int ComputeTextureCoordShift(int width)
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

        static void SetLinearCoordParams(Material material, string maskPropertyName, string shiftPropertyName, int width)
        {
            int clampedWidth = Mathf.Max(1, width);
            material.SetInt(maskPropertyName, clampedWidth - 1);
            material.SetInt(shiftPropertyName, ComputeTextureCoordShift(clampedWidth));
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

        static void ConfigureSplatMaterialTextures(Material splatMat, Texture2D xyzTex, Texture2D colDcTex, Texture2D rotTex, Texture2D scaleTex, Texture2D shTex, int importedSHCoeffCount, Vector3 shMin, Vector3 shRange, float shRangeEpsilon, int actualSplatCount, SHBand defaultSHBand)
        {
            splatMat.SetTexture("_GS_Positions", xyzTex);
            SetLinearCoordParams(splatMat, "_GS_Positions_CoordMask", "_GS_Positions_CoordShift", xyzTex != null ? xyzTex.width : 1);
            splatMat.SetTexture("_GS_Colors", colDcTex);
            splatMat.SetTexture("_GS_Rotations", rotTex);
            splatMat.SetTexture("_GS_Scales", scaleTex);
            splatMat.SetTexture("_GS_SH", shTex);
            SetLinearCoordParams(splatMat, "_GS_SH_CoordMask", "_GS_SH_CoordShift", shTex != null ? shTex.width : 1);
            splatMat.SetInt("_GS_SH_CoeffCount", importedSHCoeffCount);
            splatMat.SetInt("_GS_SH_CoeffStride", actualSplatCount);
            splatMat.SetVector("_GS_SH_Min", new Vector4(shMin.x, shMin.y, shMin.z, 0f));
            splatMat.SetVector("_GS_SH_Range", new Vector4(
                Mathf.Max(shRange.x, shRangeEpsilon),
                Mathf.Max(shRange.y, shRangeEpsilon),
                Mathf.Max(shRange.z, shRangeEpsilon),
                0f
            ));
            splatMat.SetInt("_ActualSplatCount", actualSplatCount);
            SetMaterialSHBand(splatMat, defaultSHBand);
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

        public static void Import(string plyFile, string prefabOutputPath, bool computeBoundingBox, int splatsPerPass, bool precomputeSorting = false, int maxAlphaMaskCount = 1, bool useSRGB = true, bool importSphericalHarmonics = true, SHBand defaultSHBand = SHBand.SH1, bool compressColorAlphaToBC7 = false, bool compressSHToBC7 = true)
        {
            if (!File.Exists(plyFile))
                throw new FileNotFoundException(plyFile);

            // Read header to learn how many splats we need to allocate for.
            int count = GaussianFileReader.ReadFileHeader(plyFile);
            if (count == 0)
                throw new Exception("Empty or unsupported splat file");
            if (count > MaxImportSplatCount)
                throw new InvalidOperationException($"Import aborted: '{Path.GetFileName(plyFile)}' contains {count:N0} splats, exceeding the importer limit of {MaxImportSplatCount:N0}.");

            int requestedSHCoeffCount = importSphericalHarmonics ? SHCoeffCountForBand(defaultSHBand) : 0;
            bool willAttemptBC7Compression = compressColorAlphaToBC7 || (compressSHToBC7 && requestedSHCoeffCount > 0);
            if (willAttemptBC7Compression && !SystemInfo.SupportsTextureFormat(TextureFormat.BC7))
                throw new InvalidOperationException("BC7 compression is not supported by the current editor graphics device. Disable BC7 compression or import on a system with BC7 support.");

            GaussianFileReader.ReadFile(plyFile, requestedSHCoeffCount, out NativeArray<ImportSplatData> splats, out NativeArray<Vector3> shCoeffs);
            try
            {
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
                SHBand effectiveDefaultSHBand = importSphericalHarmonics ? ClampDefaultSHBand(defaultSHBand, hasNonZeroBand) : SHBand.SH0;
                int importedSHCoeffCount = importSphericalHarmonics ? SHCoeffCountForBand(effectiveDefaultSHBand) : 0;
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
                TextureLayout splatLayout = ChoosePotTextureLayout(n, compressColorAlphaToBC7);
                TextureLayout shLayout = importedSHCoeffCount > 0
                    ? ChoosePotTextureLayout(n * importedSHCoeffCount, compressSHToBC7)
                    : new TextureLayout(1, 1);

                Debug.Log($"Importing {count} splats into {splatLayout.Width}x{splatLayout.Height} textures");

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
                if (computeBoundingBox)
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
                Directory.CreateDirectory(outputDataFolder);

                Texture2DArray sortedTex = null;
                if(precomputeSorting) {
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
                            sortedPixels[j] = new Color(textureIndexBySourceIndex[sortedIndices[i][j]], 0f, 0f, 0f); // Store only the texture index in the red channel
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
                if(useSRGB) {
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
                    var s = splats[sourceIndex];
                    xyzPixels[i]   = new Color(s.pos.x,   s.pos.y,   s.pos.z,   0f);
                    colPixels[i]   = new Color(s.dc0.x,   s.dc0.y,   s.dc0.z,   s.opacity);
                    rotPixels[i]   = new Color(0.5f + 0.5f * s.rot.x, 
                                               0.5f + 0.5f * s.rot.y, 
                                               0.5f + 0.5f * s.rot.z, 
                                               0.5f + 0.5f * s.rot.w);
                    scalePixels[i] = new Color(s.scale.x, s.scale.y, s.scale.z, 0f);

                    if (importedSHCoeffCount > 0)
                    {
                        for (int coeff = 0; coeff < importedSHCoeffCount; ++coeff)
                        {
                            Vector3 sh = GetSHCoefficient(shCoeffs, requestedSHCoeffCount, sourceIndex, coeff);
                            shPixels[coeff * n + i] = new Color(
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
                ApplyTexture(colDcTex, compressColorAlphaToBC7);
                rotTex.Apply(false, true);
                scaleTex.Apply(false, true);
                if (importedSHCoeffCount > 0)
                {
                    ApplyTexture(shTex, compressSHToBC7);
                }

                xyzTex = SaveTextureAsset(xyzTex, outputDataFolder, materialName + "_xyz");
                colDcTex = SaveTextureAsset(colDcTex, outputDataFolder, materialName + "_color_dc");
                rotTex = SaveTextureAsset(rotTex, outputDataFolder, materialName + "_rotation");
                scaleTex = SaveTextureAsset(scaleTex, outputDataFolder, materialName + "_scale");
                if (importedSHCoeffCount > 0)
                {
                    shTex = SaveTextureAsset(shTex, outputDataFolder, materialName + "_sh");
                }
                
                if(splatsPerPass == 0) splatsPerPass = n;
                splatsPerPass = Mathf.Min(splatsPerPass, n);
     
                List<Material> materials = new List<Material>();
                List<int> indexCounts = new List<int>();
                List<MeshTopology> topologies = new List<MeshTopology>();

                int totalPassCount = (n + splatsPerPass - 1) / splatsPerPass; // number of passes needed
                int alphaMaskCount = Mathf.Min(maxAlphaMaskCount, totalPassCount - 1); // number of alpha mask passes needed
                //update splats per pass to make equal chunks
                splatsPerPass = (n + totalPassCount - 1) / totalPassCount;

                if(useSRGB) {
                    //Convert screen colors to sRGB
                    indexCounts.Add(3);
                    topologies.Add(MeshTopology.Triangles); // main mesh will be rendered as triangles
                    Material convertToSRGB = new Material(Shader.Find("VRChatGaussianSplatting/ToSRGB"));
                    convertToSRGB.name = "convert_to_srgb";
                    materials.Add(convertToSRGB);
                } else {
                    splatsPerPass = n;
                }
              
                Material mainMat = null;
                for (int i = 0; i < n; i += splatsPerPass)
                {
                    int passCount = Mathf.Min(splatsPerPass, n - i);
                    int pass = i / splatsPerPass;
                    Material splatMat = null;
                    string splatMatName = materialName + (pass > 0 ? $"_pass_{pass}" : "_main") + "_splat";
                    if(pass == 0) {
                        splatMat = new Material(shader);
                        splatMat.name = splatMatName;
                        mainMat = splatMat;
                    } else {
                        splatMat = new Material(mainMat); // copy the base pass settings without relying on parent inheritance
                    }

                    ConfigureSplatMaterialTextures(splatMat, xyzTex, colDcTex, rotTex, scaleTex, shTex, importedSHCoeffCount, sharedShMin, sharedShRange, shRangeEpsilon, n, effectiveDefaultSHBand);
                    if(precomputeSorting)
                    {
                        splatMat.SetTexture("_GS_RenderOrderPrecomputed", sortedTex);
                        SetLinearCoordParams(splatMat, "_GS_RenderOrderPrecomputed_CoordMask", "_GS_RenderOrderPrecomputed_CoordShift", sortedTex != null ? sortedTex.width : 1);
                        splatMat.SetInteger("_PRECOMPUTED_SORTING", 1);
                        splatMat.EnableKeyword("_PRECOMPUTED_SORTING");
                        splatMat.EnableKeyword("_PRECOMPUTED_SORTING_ON");
                    }
                    else
                    {
                        splatMat.SetTexture("_GS_RenderOrderPrecomputed", null);
                        splatMat.SetInteger("_PRECOMPUTED_SORTING", 0);
                        splatMat.DisableKeyword("_PRECOMPUTED_SORTING");
                        splatMat.DisableKeyword("_PRECOMPUTED_SORTING_ON");
                    }

                    if(pass > 0 && pass <= alphaMaskCount) {
                        // Create alpha depth mask pass
                        indexCounts.Add(3);
                        topologies.Add(MeshTopology.Triangles); // alpha depth mask will be rendered as triangles
                        Material alphaDepthMask = new Material(Shader.Find("VRChatGaussianSplatting/AlphaDepthMask"));
                        alphaDepthMask.name = splatMatName + "_alpha_depth_mask";
                        materials.Add(alphaDepthMask);
                    }
                    splatMat.name = splatMatName;
                    splatMat.SetInt("_SplatCount", passCount);
                    splatMat.SetInt("_SplatOffset", i);
                    indexCounts.Add((passCount + 31) / 32); // geometry shader will emit 32 quads per point, so we need at least 1 vertex per 32 splats
                    topologies.Add(MeshTopology.Points);
                    materials.Add(splatMat);
                }

                if(useSRGB) {
                    // Convert screen colors back to linear
                    indexCounts.Add(3);
                    topologies.Add(MeshTopology.Triangles); // main mesh will be rendered as triangles
                    Material convertToLinear = new Material(Shader.Find("VRChatGaussianSplatting/ToLinear"));
                    convertToLinear.name = "convert_to_linear";
                    materials.Add(convertToLinear);
                }

                Directory.CreateDirectory(outputDataFolder + "/materials");
                for (int i = 0; i < materials.Count; ++i) {
                    Material splatMat = materials[i];
                    splatMat.renderQueue = 3500 + i;
                    string matPath = Path.Combine(outputDataFolder + "/materials", splatMat.name + ".mat");
                    materials[i] = CreateOrReplaceAsset(splatMat, matPath);
                }

                Mesh pointMesh = PointsMesh.GetMultiPassMesh(indexCounts, topologies, bbox);
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

        static T CreateOrReplaceAsset<T>(T asset, string path) where T : UnityEngine.Object
        {
            string assetName = Path.GetFileNameWithoutExtension(path);
            asset.name = assetName;

            T savedAsset = null;

            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(asset, existing);
                savedAsset = existing;
                UnityEngine.Object.DestroyImmediate(asset);
            }
            else
            {
                UnityEngine.Object existingMainAsset = AssetDatabase.LoadMainAssetAtPath(path);
                if (existingMainAsset != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }

                AssetDatabase.CreateAsset(asset, path);
                savedAsset = AssetDatabase.LoadAssetAtPath<T>(path) ?? asset;
            }

            if (savedAsset != null && savedAsset.name != assetName)
            {
                savedAsset.name = assetName;
            }

            if (savedAsset != null)
            {
                EditorUtility.SetDirty(savedAsset);
            }

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
        List<string> _plyPaths = new();  
        string _outputFolder = "Assets";
        bool _computeBoundingBox = true;   
        bool _multiPassRendering = true;
        int _splatsPerPass =  3 * 256 * 1024; // 1 million splats per pass
        bool _precomputeSorting = false; // precompute sorting for octahedral directions
        int _maxAlphaMaskCount = 1; // max number of alpha mask passes
        bool _useSRGB = true; // use sRGB color correction
        bool _importSphericalHarmonics = true;
        SHBand _defaultSHBand = SHBand.SH3;
        bool _compressColorAlphaToBC7 = false;
        bool _compressSHToBC7 = true;
        Vector2 scrollPosition = Vector2.zero;
        [MenuItem("Gaussian Splatting/Import PLY Splats…")]
        static void Init()
        {
            GetWindow<PlyImportWizard>().Show();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("PLY files", EditorStyles.boldLabel);
            if (GUILayout.Button("Clear All PLYs"))
            {
                _plyPaths.Clear();
            }
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, true, true, GUILayout.Height(100));	
            for (int i = 0; i < _plyPaths.Count; ++i)
            {
                EditorGUILayout.BeginHorizontal();
                _plyPaths[i] = EditorGUILayout.TextField(_plyPaths[i]);
                if (GUILayout.Button("…", GUILayout.Width(30)))
                    _plyPaths[i] = EditorUtility.OpenFilePanel("Select PLY file", Application.dataPath, "ply");
                if (GUILayout.Button("–", GUILayout.Width(20)))
                {
                    _plyPaths.RemoveAt(i);
                    --i;
                }
                EditorGUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("+ Add PLY file")) _plyPaths.Add(string.Empty);
            if (GUILayout.Button("Add All PLYs in Folder"))
            {
                string folder = EditorUtility.OpenFolderPanel("Select Folder with PLY files", Application.dataPath, "");
                if (!string.IsNullOrEmpty(folder))
                {
                    string[] files = Directory.GetFiles(folder, "*.ply");
                    foreach (string file in files)
                    {
                        _plyPaths.Add(file);
                    }
                }
            }
            
            EditorGUILayout.HelpBox("Large imports still depend on available RAM, but the PLY importer now streams vertex data so file size is no longer capped by a 2GB raw read buffer. SH import memory still scales with the selected SH band.", MessageType.Info);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Output Folder", EditorStyles.boldLabel);
            _outputFolder = EditorGUILayout.TextField(_outputFolder);
            if (GUILayout.Button("…", GUILayout.Width(30)))
                _outputFolder = EditorUtility.OpenFolderPanel("Select Output Folder", _outputFolder, "");

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Splat settings", EditorStyles.boldLabel);
            _computeBoundingBox   = EditorGUILayout.Toggle("Compute Bounding Box", _computeBoundingBox);
            _useSRGB = EditorGUILayout.Toggle("sRGB Color Correction", _useSRGB);
            _importSphericalHarmonics = EditorGUILayout.Toggle("Import Spherical Harmonics", _importSphericalHarmonics);
            EditorGUILayout.Space(5f);
            EditorGUILayout.LabelField("BC7 Compression", EditorStyles.boldLabel);
            _compressColorAlphaToBC7 = EditorGUILayout.Toggle("ColorAlpha", _compressColorAlphaToBC7);
            _compressSHToBC7 = EditorGUILayout.Toggle("SH1+", _compressSHToBC7);
            if (_compressColorAlphaToBC7 || _compressSHToBC7)
            {
                EditorGUILayout.HelpBox("BC7 compression applies only to the selected generated textures. Position, scale, and sorting textures stay uncompressed, and the importer pads texture resolution to 4x4 blocks when needed.", MessageType.Info);
                if (!_importSphericalHarmonics && _compressSHToBC7)
                {
                    EditorGUILayout.HelpBox("SH1+ compression has no effect while Import Spherical Harmonics is disabled.", MessageType.Info);
                }
            }
            if (_importSphericalHarmonics)
            {
                _defaultSHBand = (SHBand)EditorGUILayout.EnumPopup("Max imported SH Band", _defaultSHBand);
                EditorGUILayout.HelpBox("Imports higher-order SH coefficient textures only up to the selected max band and sets the imported material to that band. If the selected band has no non-zero coefficients in the file, the importer falls back to the highest lower non-zero band.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Skips SH coefficient texture generation and forces imported materials to SH0 only.", MessageType.Info);
            }
            EditorGUILayout.HelpBox("Color correction requires 2 additional grab passes, for small splats you might want to disable this. Without this enabled back to front rendering will be used, which makes multi-pass rendering not work. sRGB color correction only works correctly if the world has HDR camera render targets.", MessageType.Info);
            if(_useSRGB) {
                _multiPassRendering   = EditorGUILayout.Toggle("Multi-Pass Rendering", _multiPassRendering);
                if (_multiPassRendering)
                {
                    _splatsPerPass = EditorGUILayout.IntField("Splat Count Per Pass", _splatsPerPass);
                    EditorGUILayout.HelpBox("The rendering of the splat is split into multiple sequential chunks, can help with VR rendering performance.", MessageType.Info);
                    _splatsPerPass = Mathf.Clamp(_splatsPerPass, 128 * 1024, 8 * 1024 * 1024);
                    _maxAlphaMaskCount = EditorGUILayout.IntField("Max Alpha Mask Count", _maxAlphaMaskCount);
                    EditorGUILayout.HelpBox("After each chunk is rendered an optional alpha mask pass is added using a grab pass and stencil. This will occlude the following chunks if they are behind opaque objects. This can help performance, but grab pass can be expensive, so use it with care. If you have more than 4M splats you might want to have more than 1 alpha mask pass.", MessageType.Info);
                }
                else
                {
                    _splatsPerPass = 0; // disable multi-pass rendering
                }
            }
            _precomputeSorting = EditorGUILayout.Toggle("Precompute Sorting", _precomputeSorting);
            if (_precomputeSorting)
            {
                EditorGUILayout.HelpBox("Precomputing sorting for octahedral directions, makes the gaussian splatting work standalone, without the GaussianSplatRenderer. However this takes way more texture memory and might have rendering artifacts. THIS WILL NO LONGER WORK WITH GaussianSplatRenderer", MessageType.Warning);
            }
          
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Import All PLYs"))
            {
                if (!_plyPaths.Any(p => !string.IsNullOrEmpty(p)))
                {
                    EditorUtility.DisplayDialog("PLY Import", "Add at least one PLY path.", "OK");
                    return;
                }

                foreach (string ply in _plyPaths.Where(p => !string.IsNullOrEmpty(p)))
                {
                    string prefabName = Path.GetFileNameWithoutExtension(ply) + ".prefab";
                    string relFolder  = FileUtil.GetProjectRelativePath(_outputFolder);
                    if (string.IsNullOrEmpty(relFolder))
                        relFolder = "Assets";
                    string prefabPath = Path.Combine(relFolder, prefabName).Replace('\\', '/');
                    ImportSingle(ply, prefabPath);
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("PLY Import", "All imports completed.", "OK");
            }
        }

        void ImportSingle(string plyPath, string prefabPath)
        {
            try
            {
                EditorUtility.DisplayProgressBar("PLY Import",
                    $"Importing {Path.GetFileName(plyPath)}", 0f);
                PlySplatImporter.Import(plyPath, prefabPath, _computeBoundingBox, _splatsPerPass, _precomputeSorting, _maxAlphaMaskCount, _useSRGB, _importSphericalHarmonics, _defaultSHBand, _compressColorAlphaToBC7, _compressSHToBC7);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("PLY Import Failed", e.Message, "OK");
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
