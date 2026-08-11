
#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
    /// Parses a Gaussian splat source file and packs the base attributes plus optional
    /// spherical harmonic coefficient textures ready for GPU upload. Only UnityEngine types are
    /// referenced so this class can also be used at runtime. Editor‑only helpers are wrapped in
    /// UNITY_EDITOR guards.
    /// </summary>
    public static class GaussianSplatImporter
    {
        const int SHCoeffCount = 15;
        const int MaxImportSplatCount = 4096 * 4096;
        const string PlyExtension = ".ply";
        const string SpzExtension = ".spz";
        const uint SpzMagic = 0x5053474e; // "NGSP"
        const uint SpzVersion = 2;
        const int MaxSpzSplatCount = 10_000_000;
        const int SpzBaseFloatCount = 14;
        const int SpzRowsPerOutputChunk = 8192;
        public static readonly string[] ImportFilePanelFilters =
        {
            "Gaussian splat files", "ply,spz"
        };

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

        [System.Serializable]
        public struct ImportOptions
        {
            public bool computeBoundingBox;
            public int splatsPerPass;
            public bool standalone;
            public int maxAlphaMaskCount;
            public bool useSRGB;
            public bool importSphericalHarmonics;
            public SHBand defaultSHBand;
            public bool compressColorAlphaToBC7; // compress the color/alpha texture to BC7 (both LOD and non-LOD)
            public int startRenderQueue;
            public bool cropToBounds;
            public Bounds cropBounds;
            public float cropPadding;
            public bool applyHorizonAlignment;
            public Quaternion horizonRotation;
            public Vector3 horizonPivot;
            public bool lodUsePackedPositions;
            public bool lodComputeSplats;
            public int lodResamplePercent;
            public int lodReusePercent;
            public bool normalizeSize;        // scale splats so the floater-robust extent matches normalizeTargetSize
            public float normalizeTargetSize; // target extent (world units) when normalizeSize is on; <=0 -> 1.0
            public SHCompression shCompression; // SH texture format (both LOD and non-LOD): None (RGB565), BC1 (4bpp), BC7 (8bpp)
        }

        // SH texture storage format, shared by both import paths.
        public enum SHCompression { None = 0, BC1 = 1, BC7 = 2 }

        // Stored on each imported splat object (as JSON) so it can be re-imported exactly or with tweaked settings.
        [System.Serializable]
        public class ImportMetadata
        {
            public string sourcePath;     // original import source
            public string prefabPath;     // imported prefab asset path (re-import target)
            public bool importAsLOD;
            public int lodChunkSize = 4096;
            public ImportOptions options;

            public static string ToJson(ImportMetadata m) => JsonUtility.ToJson(m);
            public static ImportMetadata FromJson(string json) => string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<ImportMetadata>(json);
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

        // Color/alpha texture: optional BC7 (shared by both import paths).
        public static void ApplyTexture(Texture2D texture, bool compressToBC7)
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

        // SH texture: None keeps the RGB565 source, BC1 -> DXT1 (4bpp), BC7 -> 8bpp. Shared by both import
        // paths so SH storage format matches regardless of LOD/non-LOD.
        public static void ApplyShTextureCompression(Texture2D texture, SHCompression mode)
        {
            if (mode == SHCompression.BC1 || mode == SHCompression.BC7)
            {
                texture.Apply(false, false);
                TextureFormat fmt = mode == SHCompression.BC7 ? TextureFormat.BC7 : TextureFormat.DXT1;
                EditorUtility.CompressTexture(texture, fmt, TextureCompressionQuality.Normal);
            }
            texture.Apply(false, true);
        }

        static string GetSHPropertyName(int index)
        {
            return $"_GS_SH{(index + 1).ToString("X")}";
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

        public static int SHCoeffCountForBand(SHBand band)
        {
            return band switch
            {
                SHBand.SH0 => 0,
                SHBand.SH1 => 3,
                SHBand.SH2 => 8,
                _ => SHCoeffCount,
            };
        }

        // Highest band fully covered by coeffCount coefficients (inverse of SHCoeffCountForBand).
        public static SHBand SHBandForCoeffCount(int coeffCount)
        {
            if (coeffCount >= SHCoeffCount) return SHBand.SH3;
            if (coeffCount >= 8) return SHBand.SH2;
            if (coeffCount >= 3) return SHBand.SH1;
            return SHBand.SH0;
        }

        // Cap on what the LOD importer will bake as SH: stored splats x coefficients, i.e. two 8K SH textures.
        // Past it the SH textures need many GB and the per-cluster averaging is prohibitively slow, so the import
        // steps down a band. Shared with the wizard so its pre-import estimate uses the same threshold.
        public const long MaxLODImportSHTexels = 2L * 8192 * 8192;

        // Highest band that both the source carries and the SH texel cap allows at storedSplatCount. Steps down a
        // whole band at a time: a partial band would store coefficients the shader's band setting never reads.
        public static SHBand ResolveLODImportSHBand(int availableCoeffCount, int storedSplatCount)
        {
            SHBand band = SHBandForCoeffCount(availableCoeffCount);
            while (band > SHBand.SH0 && (long)storedSplatCount * SHCoeffCountForBand(band) > MaxLODImportSHTexels)
            {
                band--;
            }
            return band;
        }

        // Reports SH imported below the requested Max SH Band because the source file carries fewer coefficients.
        public static void WarnSHBandLimitedBySource(string sourceName, SHBand requestedBand, int fileCoeffCount)
        {
            int requestedCoeffCount = SHCoeffCountForBand(requestedBand);
            if (requestedCoeffCount <= 0 || fileCoeffCount >= requestedCoeffCount)
            {
                return;
            }

            if (fileCoeffCount <= 0)
            {
                Debug.LogWarning($"[GaussianSplat] '{sourceName}': Import Spherical Harmonics is on with Max SH Band {requestedBand}, but the source carries no SH coefficients (no f_rest_* properties). Importing DC-only color.");
            }
            else
            {
                Debug.LogWarning($"[GaussianSplat] '{sourceName}': Max SH Band {requestedBand} needs {requestedCoeffCount} coefficients but the source only carries {fileCoeffCount} ({SHBandForCoeffCount(fileCoeffCount)}). Importing at {SHBandForCoeffCount(fileCoeffCount)}.");
            }
        }

        public static bool IsSupportedImportSourcePath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, PlyExtension, StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, SpzExtension, StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveSourceToPlyPath(string sourcePath, string tempFolder)
        {
            string extension = Path.GetExtension(sourcePath);
            if (string.Equals(extension, PlyExtension, StringComparison.OrdinalIgnoreCase))
            {
                return sourcePath;
            }
            if (string.Equals(extension, SpzExtension, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(tempFolder);
                string tempPlyPath = Path.Combine(tempFolder, SanitizeAssetName(Path.GetFileNameWithoutExtension(sourcePath)) + ".ply");
                WriteResolvedSpzPly(sourcePath, tempPlyPath);
                return tempPlyPath;
            }

            throw new IOException($"File {sourcePath} is not a supported splat import format. Expected .ply or .spz.");
        }

        struct SpzHeader
        {
            public int splatCount;
            public int shLevel;
            public int fractionalBits;
        }

        static void WriteResolvedSpzPly(string spzPath, string plyPath)
        {
            if (!BitConverter.IsLittleEndian)
            {
                throw new PlatformNotSupportedException("SPZ import requires a little-endian editor platform.");
            }

            using FileStream input = new FileStream(spzPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using GZipStream gzip = new GZipStream(input, CompressionMode.Decompress);
            SpzHeader header = ReadSpzHeader(spzPath, gzip);
            int shCoeffCount = SHCoeffCountForSpzLevel(header.shLevel);

            byte[] packedPositions = new byte[checked(header.splatCount * 3 * 3)];
            byte[] packedAlpha = new byte[header.splatCount];
            byte[] packedColors = new byte[checked(header.splatCount * 3)];
            byte[] packedScales = new byte[checked(header.splatCount * 3)];
            byte[] packedRotations = new byte[checked(header.splatCount * 3)];
            byte[] packedSh = new byte[checked(header.splatCount * shCoeffCount * 3)];

            ReadExact(gzip, packedPositions, packedPositions.Length, spzPath);
            ReadExact(gzip, packedAlpha, packedAlpha.Length, spzPath);
            ReadExact(gzip, packedColors, packedColors.Length, spzPath);
            ReadExact(gzip, packedScales, packedScales.Length, spzPath);
            ReadExact(gzip, packedRotations, packedRotations.Length, spzPath);
            ReadExact(gzip, packedSh, packedSh.Length, spzPath);

            string outputFolder = Path.GetDirectoryName(plyPath);
            if (!string.IsNullOrEmpty(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }
            using FileStream output = new FileStream(plyPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            WriteResolvedPlyHeader(output, header.splatCount, shCoeffCount);
            WriteSpzRowsAsPly(output, header, shCoeffCount, packedPositions, packedAlpha, packedColors, packedScales, packedRotations, packedSh);
        }

        static SpzHeader ReadSpzHeader(string spzPath, Stream stream)
        {
            byte[] bytes = new byte[16];
            ReadExact(stream, bytes, bytes.Length, spzPath);

            uint magic = BitConverter.ToUInt32(bytes, 0);
            uint version = BitConverter.ToUInt32(bytes, 4);
            uint splatCountU = BitConverter.ToUInt32(bytes, 8);
            uint packed = BitConverter.ToUInt32(bytes, 12);

            if (magic != SpzMagic)
            {
                throw new IOException($"SPZ {spzPath} read error, header magic unexpected {magic}");
            }
            if (version != SpzVersion)
            {
                throw new IOException($"SPZ {spzPath} read error, header version unexpected {version}");
            }
            if (splatCountU > int.MaxValue)
            {
                throw new IOException($"SPZ {spzPath} read error, splat count exceeds supported range {splatCountU}");
            }

            int splatCount = (int)splatCountU;
            int shLevel = (int)(packed & 0xFF);
            int fractionalBits = (int)((packed >> 8) & 0xFF);

            if (splatCount < 1 || splatCount > MaxSpzSplatCount)
            {
                throw new IOException($"SPZ {spzPath} read error, out of range splat count {splatCount}");
            }
            if (shLevel < 0 || shLevel > 3)
            {
                throw new IOException($"SPZ {spzPath} read error, out of range SH level {shLevel}");
            }
            if (fractionalBits < 0 || fractionalBits > 24)
            {
                throw new IOException($"SPZ {spzPath} read error, out of range fractional bits {fractionalBits}");
            }

            return new SpzHeader
            {
                splatCount = splatCount,
                shLevel = shLevel,
                fractionalBits = fractionalBits
            };
        }

        static int SHCoeffCountForSpzLevel(int level)
        {
            switch (level)
            {
                case 0: return 0;
                case 1: return 3;
                case 2: return 8;
                case 3: return 15;
                default: return 0;
            }
        }

        static void WriteResolvedPlyHeader(Stream output, int splatCount, int shCoeffCount)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.AppendLine("ply");
            sb.AppendLine("format binary_little_endian 1.0");
            sb.AppendLine("comment Resolved by VRChatGaussianSplatting");
            sb.Append("element vertex ").Append(splatCount).Append('\n');
            sb.AppendLine("property float x");
            sb.AppendLine("property float y");
            sb.AppendLine("property float z");
            sb.AppendLine("property float f_dc_0");
            sb.AppendLine("property float f_dc_1");
            sb.AppendLine("property float f_dc_2");
            sb.AppendLine("property float opacity");
            sb.AppendLine("property float scale_0");
            sb.AppendLine("property float scale_1");
            sb.AppendLine("property float scale_2");
            sb.AppendLine("property float rot_0");
            sb.AppendLine("property float rot_1");
            sb.AppendLine("property float rot_2");
            sb.AppendLine("property float rot_3");
            for (int channel = 0; channel < 3; channel++)
            {
                for (int coeff = 0; coeff < shCoeffCount; coeff++)
                {
                    sb.Append("property float f_rest_").Append(coeff + channel * 15).Append('\n');
                }
            }
            sb.AppendLine("end_header");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            output.Write(headerBytes, 0, headerBytes.Length);
        }

        static void WriteSpzRowsAsPly(
            FileStream output,
            SpzHeader header,
            int shCoeffCount,
            byte[] packedPositions,
            byte[] packedAlpha,
            byte[] packedColors,
            byte[] packedScales,
            byte[] packedRotations,
            byte[] packedSh)
        {
            int stride = (SpzBaseFloatCount + shCoeffCount * 3) * sizeof(float);
            byte[] buffer = new byte[SpzRowsPerOutputChunk * stride];
            float positionScale = 1.0f / (1 << header.fractionalBits);

            int processed = 0;
            while (processed < header.splatCount)
            {
                int rows = Math.Min(SpzRowsPerOutputChunk, header.splatCount - processed);
                int writeOffset = 0;
                for (int row = 0; row < rows; row++)
                {
                    int index = processed + row;
                    WriteSpzSplatAsPlyRow(buffer, ref writeOffset, index, positionScale, shCoeffCount, packedPositions, packedAlpha, packedColors, packedScales, packedRotations, packedSh);
                }
                output.Write(buffer, 0, rows * stride);
                processed += rows;
            }
        }

        static void WriteSpzSplatAsPlyRow(
            byte[] output,
            ref int offset,
            int index,
            float positionScale,
            int shCoeffCount,
            byte[] packedPositions,
            byte[] packedAlpha,
            byte[] packedColors,
            byte[] packedScales,
            byte[] packedRotations,
            byte[] packedSh)
        {
            WriteFloat(output, ref offset, UnpackSpzPosition(packedPositions, index * 3 + 0) * positionScale);
            WriteFloat(output, ref offset, UnpackSpzPosition(packedPositions, index * 3 + 1) * positionScale);
            WriteFloat(output, ref offset, UnpackSpzPosition(packedPositions, index * 3 + 2) * positionScale);

            WriteFloat(output, ref offset, UnpackSpzColorDC(packedColors[index * 3 + 0]));
            WriteFloat(output, ref offset, UnpackSpzColorDC(packedColors[index * 3 + 1]));
            WriteFloat(output, ref offset, UnpackSpzColorDC(packedColors[index * 3 + 2]));
            WriteFloat(output, ref offset, Logit(packedAlpha[index] / 255.0f));

            WriteFloat(output, ref offset, UnpackSpzLogScale(packedScales[index * 3 + 0]));
            WriteFloat(output, ref offset, UnpackSpzLogScale(packedScales[index * 3 + 1]));
            WriteFloat(output, ref offset, UnpackSpzLogScale(packedScales[index * 3 + 2]));

            Vector4 q = UnpackSpzRotationWXYZ(packedRotations, index);
            WriteFloat(output, ref offset, q.x);
            WriteFloat(output, ref offset, q.y);
            WriteFloat(output, ref offset, q.z);
            WriteFloat(output, ref offset, q.w);

            int shBase = index * shCoeffCount * 3;
            for (int channel = 0; channel < 3; channel++)
            {
                for (int coeff = 0; coeff < shCoeffCount; coeff++)
                {
                    WriteFloat(output, ref offset, UnpackSpzSH(packedSh[shBase + coeff * 3 + channel]));
                }
            }
        }

        static int UnpackSpzPosition(byte[] packedPositions, int componentIndex)
        {
            int baseIndex = componentIndex * 3;
            int value = packedPositions[baseIndex + 0] | (packedPositions[baseIndex + 1] << 8) | (packedPositions[baseIndex + 2] << 16);
            if ((value & 0x800000) != 0)
            {
                value |= unchecked((int)0xFF000000);
            }
            return value;
        }

        static float UnpackSpzColorDC(byte value)
        {
            return (value / 255.0f - 0.5f) / 0.15f;
        }

        static float UnpackSpzLogScale(byte value)
        {
            return value / 16.0f - 10.0f;
        }

        static Vector4 UnpackSpzRotationWXYZ(byte[] packedRotations, int index)
        {
            float x = packedRotations[index * 3 + 0] * (1.0f / 127.5f) - 1.0f;
            float y = packedRotations[index * 3 + 1] * (1.0f / 127.5f) - 1.0f;
            float z = packedRotations[index * 3 + 2] * (1.0f / 127.5f) - 1.0f;
            float w = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - (x * x + y * y + z * z)));
            float length = Mathf.Sqrt(w * w + x * x + y * y + z * z);
            if (length <= 1e-8f)
            {
                return new Vector4(1.0f, 0.0f, 0.0f, 0.0f);
            }

            float invLength = 1.0f / length;
            return new Vector4(w * invLength, x * invLength, y * invLength, z * invLength);
        }

        static float UnpackSpzSH(byte value)
        {
            return (value - 128.0f) / 128.0f;
        }

        static float Logit(float value)
        {
            const float epsilon = 1e-6f;
            value = Mathf.Clamp(value, epsilon, 1.0f - epsilon);
            return Mathf.Log(value / (1.0f - value));
        }

        static void ReadExact(Stream stream, byte[] buffer, int byteCount, string path)
        {
            int total = 0;
            while (total < byteCount)
            {
                int read = stream.Read(buffer, total, byteCount - total);
                if (read <= 0)
                {
                    throw new IOException($"SPZ {path} read error, expected {byteCount} bytes got {total}");
                }
                total += read;
            }
        }

        static unsafe void WriteFloat(byte[] buffer, ref int offset, float value)
        {
            fixed (byte* ptr = &buffer[offset])
            {
                *(float*)ptr = value;
            }
            offset += sizeof(float);
        }

        // Single source of truth for the horizon-alignment transform, shared with the streamed reader.
        public static ImportSplatData ApplyHorizonAlignment(ImportSplatData splat, Quaternion rotation, Vector3 pivot)
        {
            splat.pos = rotation * (splat.pos - pivot);
            splat.rot = rotation * splat.rot;
            return splat;
        }

        // Shared block-swizzle texel index used by both importers. The LOD importer carried an
        // arithmetically identical copy (division instead of shift, equivalent for POT widths).
        public static int ComputePackedTextureIndex(int index, int width)
        {
            int blocksPerRow = Mathf.Max(1, width >> 2);
            int blockIndex = index >> 4;
            int blockX = blockIndex & (blocksPerRow - 1);
            int blockY = blockIndex >> ComputeTextureCoordShift(blocksPerRow);
            int x = (blockX << 2) | (index & 3);
            int y = (blockY << 2) | ((index >> 2) & 3);
            return y * width + x;
        }

        // Shared 10-bit-per-axis position packing (chunk-bbox-normalized), used by the chunked/LOD
        // importer and the non-LOD packed migration. Decode mirror lives in GSData.cginc /
        // GSLODSelect.cginc / GSLODCombine.shader: lerp(min, max, q/1023).
        public static Color32 EncodePosition10(Vector3 position, Vector3 boundsMin, Vector3 boundsMax)
        {
            Vector3 size = boundsMax - boundsMin;
            uint x = QuantizePositionAxis10(position.x, boundsMin.x, size.x);
            uint y = QuantizePositionAxis10(position.y, boundsMin.y, size.y);
            uint z = QuantizePositionAxis10(position.z, boundsMin.z, size.z);
            byte highBits = (byte)(((x >> 8) & 0x3u) | (((y >> 8) & 0x3u) << 2) | (((z >> 8) & 0x3u) << 4));
            return new Color32((byte)(x & 0xFFu), (byte)(y & 0xFFu), (byte)(z & 0xFFu), highBits);
        }

        public static uint QuantizePositionAxis10(float value, float min, float size)
        {
            if (Mathf.Abs(size) <= 1e-8f)
            {
                return 0u;
            }
            return (uint)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01((value - min) / size) * 1023.0f), 0, 1023);
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
            go.transform.localScale = Vector3.one; // y-flip is baked into coordinates -> identity scale

            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter == null)
                meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials.ToArray();
            meshRenderer.allowOcclusionWhenDynamic = false;

            // Standalone splats are just a mesh + materials with no GaussianSplatObject component (the
            // precomputed-sort shader renders them); combined splats use the array-based GaussianSplatObject
            // emitted by the LOD import path. So the base import never attaches the component - strip any stale one.
            GaussianSplatObject existingSplatObject = go.GetComponent<GaussianSplatObject>();
            if (existingSplatObject != null)
                UnityEngine.Object.DestroyImmediate(existingSplatObject);
        }

        public static GameObject CreatePrefab(List<Material> materials, Mesh mesh, string assetPath, string name, int maxSHBand = -1, bool addGaussianSplatObject = true)
        {
            GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (existingPrefab != null)
            {
                return existingPrefab;
            }

            var go = new GameObject(name);
            ConfigurePrefabRoot(go, materials, mesh, name, maxSHBand, addGaussianSplatObject);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, assetPath);
            GameObject.DestroyImmediate(go); // clean up the temporary GameObject
            return prefab;
        }

        public static void Import(string sourceFile, string prefabOutputPath, bool computeBoundingBox, int splatsPerPass, bool standalone = false, int maxAlphaMaskCount = 1, bool useSRGB = true, bool importSphericalHarmonics = true, SHBand defaultSHBand = SHBand.SH1, bool compressColorAlphaToBC7 = false, SHCompression shCompression = SHCompression.BC7, int startRenderQueue = 4050)
        {
            Import(sourceFile, prefabOutputPath, new ImportOptions
            {
                computeBoundingBox = computeBoundingBox,
                splatsPerPass = splatsPerPass,
                standalone = standalone,
                maxAlphaMaskCount = maxAlphaMaskCount,
                useSRGB = useSRGB,
                importSphericalHarmonics = importSphericalHarmonics,
                defaultSHBand = defaultSHBand,
                compressColorAlphaToBC7 = compressColorAlphaToBC7,
                shCompression = shCompression,
                startRenderQueue = startRenderQueue,
                cropToBounds = false,
                cropBounds = new Bounds(Vector3.zero, Vector3.one),
                cropPadding = 0.0f,
                applyHorizonAlignment = false,
                horizonRotation = Quaternion.identity,
                horizonPivot = Vector3.zero
            });
        }

        // Non-LOD import: the degenerate (single texture set, no LOD levels, no k-means) case of the shared
        // streamed front-end. Reads/transforms/SH-stores/Hilbert-orders the resolved source exactly
        // like the LOD path (so crop, horizon, y-flip bake, normalize and SH behave identically), then collects
        // the ordered stream into a single packed texture set + multipass mesh prefab (GaussianSplatObject).
        public static void Import(string sourceFile, string prefabOutputPath, ImportOptions options)
        {
            if (!File.Exists(sourceFile))
                throw new FileNotFoundException(sourceFile);

            string tempFolder = Path.Combine(Application.temporaryCachePath, "GaussianSplat_" + SanitizeAssetName(Path.GetFileNameWithoutExtension(sourceFile)) + "_" + DateTime.Now.Ticks.ToString());
            Directory.CreateDirectory(tempFolder);
            try
            {
                string resolvedPlyFile = ResolveSourceToPlyPath(sourceFile, tempFolder);
                StreamedSplatReader.PLYLayout layout = StreamedSplatReader.ReadPLYLayout(resolvedPlyFile);
                if (layout.count == 0)
                    throw new Exception("Empty or unsupported splat file");
                if (layout.count > MaxImportSplatCount)
                    throw new InvalidOperationException($"Import aborted: '{Path.GetFileName(sourceFile)}' contains {layout.count:N0} splats, exceeding the importer limit of {MaxImportSplatCount:N0}.");

                int requestedSHCoeffCount = options.importSphericalHarmonics ? SHCoeffCountForBand(options.defaultSHBand) : 0;
                int importedSHCoeffCount = Mathf.Min(requestedSHCoeffCount, layout.shCoeffCount);
                if (options.importSphericalHarmonics)
                {
                    WarnSHBandLimitedBySource(Path.GetFileName(sourceFile), options.defaultSHBand, layout.shCoeffCount);
                }
                bool willAttemptBC7Compression = options.compressColorAlphaToBC7 || (options.shCompression == SHCompression.BC7 && importedSHCoeffCount > 0);
                if (willAttemptBC7Compression && !SystemInfo.SupportsTextureFormat(TextureFormat.BC7))
                    throw new InvalidOperationException("BC7 compression is not supported by the current editor graphics device. Disable BC7 compression or import on a system with BC7 support.");

                // --- shared streamed front-end (crop / horizon / y-flip / normalize / raw-SH side-store) ---
                Bounds bounds = StreamedSplatReader.StreamBounds(resolvedPlyFile, layout, options, out int n, out Vector3 centroid);
                if (n <= 0)
                    throw new InvalidOperationException($"Import aborted: crop bounds exclude all splats in '{Path.GetFileName(sourceFile)}'.");
                float normalizeScale = 1.0f;
                if (options.normalizeSize)
                {
                    normalizeScale = StreamedSplatReader.ComputeNormalizeScale(resolvedPlyFile, layout, options, centroid, bounds, n);
                    bounds.SetMinMax((bounds.min - centroid) * normalizeScale + centroid, (bounds.max - centroid) * normalizeScale + centroid);
                }

                StreamedSplatReader.BigFloatBuffer shStore = importedSHCoeffCount > 0 ? new StreamedSplatReader.BigFloatBuffer((long)n * importedSHCoeffCount * 3) : null;
                int bucketBits = StreamedSplatReader.ResolveStreamedBucketBits(n);
                int bucketCount = 1 << bucketBits;
                string[] bucketPaths = new string[bucketCount];
                long[] bucketCounts = new long[bucketCount];
                StreamedSplatReader.WriteHilbertBuckets(resolvedPlyFile, layout, bounds, options, tempFolder, bucketBits, bucketPaths, bucketCounts, importedSHCoeffCount, shStore, centroid, normalizeScale);

                // Collect the Hilbert-ordered stream (non-LOD is splat-count-capped, so in-memory is fine). The
                // collected order IS the texture order, so no separate Morton sort is needed.
                ImportSplatData[] splats = new ImportSplatData[n];
                uint[] sourceIndices = new uint[n];   // stream index into shStore for each ordered splat
                int filled = 0;
                StreamedSplatReader.BucketKeyComparer keyComparer = new StreamedSplatReader.BucketKeyComparer();
                StreamedSplatReader.BucketRecordConsumer consume = (records, recordCount) =>
                {
                    for (int i = 0; i < recordCount && filled < n; i++)
                    {
                        splats[filled] = records[i].ToSplat();
                        sourceIndices[filled] = records[i].sourceIndex;
                        filled++;
                    }
                };
                for (int bucket = 0; bucket < bucketCount; bucket++)
                {
                    if (bucketCounts[bucket] <= 0) continue;
                    EditorUtility.DisplayProgressBar("Import Gaussian Splat PLY",
                        $"Ordering Hilbert bucket {bucket + 1:N0}/{bucketCount:N0}", 0.25f + 0.55f * (bucket / (float)bucketCount));
                    StreamedSplatReader.ProcessSortedBucketFile(bucketPaths[bucket], bucketCounts[bucket], bucketBits, tempFolder, keyComparer, consume, bucket + 1, bucketCount);
                }
                n = filled;

                Vector3 sharedShMin = Vector3.zero;
                Vector3 sharedShRange = Vector3.zero;
                if (importedSHCoeffCount > 0)
                {
                    StreamedSplatReader.ComputeSharedSHRange(shStore, out Vector4 mn4, out Vector4 rng4);
                    sharedShMin = new Vector3(mn4.x, mn4.y, mn4.z);
                    sharedShRange = new Vector3(rng4.x, rng4.y, rng4.z);
                }
                const float shRangeEpsilon = 1e-8f;
                SHBand effectiveDefaultSHBand = SHBandForCoeffCount(importedSHCoeffCount);

                TextureLayout splatLayout = ChoosePotTextureLayout(n);
                TextureLayout shLayout = importedSHCoeffCount > 0
                    ? ChoosePotTextureLayout(n * importedSHCoeffCount)
                    : new TextureLayout(4, 4);

                EditorUtility.DisplayProgressBar("Import Gaussian Splat PLY",
                    $"Packing {n:N0} splats into {splatLayout.Width}x{splatLayout.Height} textures", 0.84f);

                // Bounding box (positions are already fully transformed by the stream).
                Bounds bbox = new Bounds();
                if (options.cropToBounds || options.computeBoundingBox)
                {
                    Vector3 com = Vector3.zero;
                    int valid = 0;
                    for (int i = 0; i < n; ++i)
                    {
                        Vector3 p = splats[i].pos;
                        if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) continue;
                        com += p; ++valid;
                    }
                    if (valid > 0) com /= valid;
                    Vector3 ext = Vector3.zero;
                    for (int i = 0; i < n; ++i)
                    {
                        Vector3 p = splats[i].pos;
                        if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) continue;
                        Vector3 r = p - com;
                        ext.x = Mathf.Max(ext.x, Mathf.Abs(r.x));
                        ext.y = Mathf.Max(ext.y, Mathf.Abs(r.y));
                        ext.z = Mathf.Max(ext.z, Mathf.Abs(r.z));
                    }
                    bbox.center = com;
                    bbox.extents = ext;
                    if (bbox.extents.x == 0 || bbox.extents.y == 0 || bbox.extents.z == 0)
                    {
                        bbox.extents = new Vector3(1000, 1000, 1000);
                        Debug.LogWarning("Bounding box is zero-sized, using default size.");
                    }
                    if (options.cropToBounds) bbox.extents += Vector3.one * Mathf.Max(0.0f, options.cropPadding);
                }
                else
                {
                    bbox.center = Vector3.zero;
                    bbox.extents = new Vector3(1000, 1000, 1000);
                }

                string materialName = Path.GetFileNameWithoutExtension(prefabOutputPath);
                string outputDataFolder = Path.GetDirectoryName(prefabOutputPath) + "/" + materialName;
                EnsureFolderExists(outputDataFolder);

                // Optional octahedral precomputed sort orders (texture index = stream order, so identity map).
                Texture2DArray sortedTex = null;
                if (options.standalone)
                {
                    Vector3[] octahedral_dirs = {
                        new Vector3( 0.57735027f,  0.57735027f,  0.57735027f), new Vector3( 0.57735027f,  0.57735027f, -0.57735027f), new Vector3( 0.57735027f, -0.57735027f,  0.57735027f),
                        new Vector3( 0.57735027f, -0.57735027f, -0.57735027f), new Vector3( 0.00000000f,  0.35682209f,  0.93417236f), new Vector3( 0.00000000f,  0.35682209f, -0.93417236f),
                        new Vector3( 0.35682209f,  0.93417236f,  0.00000000f), new Vector3( 0.35682209f, -0.93417236f,  0.00000000f), new Vector3( 0.93417236f,  0.00000000f,  0.35682209f),
                        new Vector3( 0.93417236f,  0.00000000f, -0.35682209f)
                    };
                    sortedTex = NewTextureArray(splatLayout.Width, splatLayout.Height, octahedral_dirs.Length, TextureFormat.RFloat, "SortedOctahedralDirections");
                    for (int d = 0; d < octahedral_dirs.Length; ++d)
                    {
                        Vector3 dir = octahedral_dirs[d];
                        int[] order = new int[n];
                        for (int j = 0; j < n; ++j) order[j] = j;
                        Array.Sort(order, (a, b) => Vector3.Dot(splats[a].pos, dir).CompareTo(Vector3.Dot(splats[b].pos, dir)));
                        Color[] sortedPixels = new Color[splatLayout.Capacity];
                        for (int j = 0; j < n; ++j)
                        {
                            int packedIndex = ComputePackedTextureIndex(j, splatLayout.Width);
                            sortedPixels[packedIndex] = new Color(order[j], 0f, 0f, 0f);
                        }
                        sortedTex.SetPixels(sortedPixels, d);
                    }
                    sortedTex.Apply(false, true);
                    sortedTex = SaveTextureAsset(sortedTex, outputDataFolder, materialName + "_sorted_oct_dirs");
                }

                Texture2D colDcTex = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGBA32, "ColorDC");
                Texture2D rotTex = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGBA32, "Rotation");
                Texture2D scaleTex = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGB9e5Float, "Scale");
                Texture2D shTex = importedSHCoeffCount > 0 ? NewTexture(shLayout.Width, shLayout.Height, TextureFormat.RGB565, "SH") : null;

                Shader shader = options.useSRGB
                    ? Shader.Find("VRChatGaussianSplatting/GaussianSplatting")
                    : Shader.Find("VRChatGaussianSplatting/GaussianSplattingSimpleBackToFront");

                Color[] xyzPixels = new Color[splatLayout.Capacity];
                Color[] colPixels = new Color[splatLayout.Capacity];
                Color[] rotPixels = new Color[splatLayout.Capacity];
                Color[] scalePixels = new Color[splatLayout.Capacity];
                Color[] shPixels = importedSHCoeffCount > 0 ? new Color[shLayout.Capacity] : null;

                for (int i = 0; i < n; ++i)
                {
                    int packedIndex = ComputePackedTextureIndex(i, splatLayout.Width);
                    ImportSplatData s = splats[i];
                    xyzPixels[packedIndex] = new Color(s.pos.x, s.pos.y, s.pos.z, 0f);
                    colPixels[packedIndex] = new Color(s.dc0.x, s.dc0.y, s.dc0.z, s.opacity);
                    rotPixels[packedIndex] = new Color(0.5f + 0.5f * s.rot.x, 0.5f + 0.5f * s.rot.y, 0.5f + 0.5f * s.rot.z, 0.5f + 0.5f * s.rot.w);
                    scalePixels[packedIndex] = new Color(s.scale.x, s.scale.y, s.scale.z, 0f);

                    if (importedSHCoeffCount > 0)
                    {
                        long baseIdx = (long)sourceIndices[i] * importedSHCoeffCount * 3;
                        for (int coeff = 0; coeff < importedSHCoeffCount; ++coeff)
                        {
                            long o = baseIdx + coeff * 3;
                            int shPackedIndex = ComputePackedTextureIndex(coeff * n + i, shLayout.Width);
                            shPixels[shPackedIndex] = new Color(
                                sharedShRange.x > shRangeEpsilon ? (shStore[o + 0] - sharedShMin.x) / sharedShRange.x : 0f,
                                sharedShRange.y > shRangeEpsilon ? (shStore[o + 1] - sharedShMin.y) / sharedShRange.y : 0f,
                                sharedShRange.z > shRangeEpsilon ? (shStore[o + 2] - sharedShMin.z) / sharedShRange.z : 0f,
                                0f);
                        }
                    }
                }

                // Pack positions into the unified chunked, per-chunk-bbox-normalized RGBA32 format (shared with
                // the LOD path). Splats are Hilbert-ordered, so fixed-size chunks are spatially compact => good
                // 10-bit precision; chunkId = splatIndex / importChunkSize.
                int importChunkSize = 1024;
                int chunkCount = Mathf.Max(1, (n + importChunkSize - 1) / importChunkSize);
                Vector3[] chunkMin = new Vector3[chunkCount];
                Vector3[] chunkMax = new Vector3[chunkCount];
                for (int c = 0; c < chunkCount; c++)
                {
                    chunkMin[c] = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    chunkMax[c] = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                }
                for (int i = 0; i < n; i++)
                {
                    Color p = xyzPixels[ComputePackedTextureIndex(i, splatLayout.Width)];
                    int c = i / importChunkSize;
                    Vector3 pos = new Vector3(p.r, p.g, p.b);
                    chunkMin[c] = Vector3.Min(chunkMin[c], pos);
                    chunkMax[c] = Vector3.Max(chunkMax[c], pos);
                }
                for (int c = 0; c < chunkCount; c++)
                {
                    if (float.IsInfinity(chunkMin[c].x)) { chunkMin[c] = Vector3.zero; chunkMax[c] = Vector3.zero; }
                }
                Color32[] packedXyz = new Color32[splatLayout.Capacity];
                for (int i = 0; i < n; i++)
                {
                    int packedIndex = ComputePackedTextureIndex(i, splatLayout.Width);
                    Color p = xyzPixels[packedIndex];
                    int c = i / importChunkSize;
                    packedXyz[packedIndex] = EncodePosition10(new Vector3(p.r, p.g, p.b), chunkMin[c], chunkMax[c]);
                }
                Texture2D xyzTex = NewTexture(splatLayout.Width, splatLayout.Height, TextureFormat.RGBA32, "XYZ");
                xyzTex.SetPixels32(packedXyz);
                xyzTex.Apply(false, true);

                int chunkBoundsWidth = Mathf.NextPowerOfTwo(chunkCount);
                Color[] boundsPixels = new Color[chunkBoundsWidth * 2];
                for (int c = 0; c < chunkCount; c++)
                {
                    boundsPixels[c] = new Color(chunkMin[c].x, chunkMin[c].y, chunkMin[c].z, 0f);
                    boundsPixels[chunkBoundsWidth + c] = new Color(chunkMax[c].x, chunkMax[c].y, chunkMax[c].z, 0f);
                }
                Texture2D chunkBoundsTex = NewTexture(chunkBoundsWidth, 2, TextureFormat.RGBAFloat, "ChunkBounds");
                chunkBoundsTex.SetPixels(boundsPixels);
                chunkBoundsTex.Apply(false, true);

                colDcTex.SetPixels(colPixels);
                rotTex.SetPixels(rotPixels);
                scaleTex.SetPixels(scalePixels);
                if (importedSHCoeffCount > 0) shTex.SetPixels(shPixels);

                ApplyTexture(colDcTex, options.compressColorAlphaToBC7);
                rotTex.Apply(false, true);
                scaleTex.Apply(false, true);
                if (importedSHCoeffCount > 0) ApplyShTextureCompression(shTex, options.shCompression);

                xyzTex = SaveTextureAsset(xyzTex, outputDataFolder, materialName + "_xyz");
                chunkBoundsTex = SaveTextureAsset(chunkBoundsTex, outputDataFolder, materialName + "_chunkbounds");
                colDcTex = SaveTextureAsset(colDcTex, outputDataFolder, materialName + "_color_dc");
                rotTex = SaveTextureAsset(rotTex, outputDataFolder, materialName + "_rotation");
                scaleTex = SaveTextureAsset(scaleTex, outputDataFolder, materialName + "_scale");
                if (importedSHCoeffCount > 0) shTex = SaveTextureAsset(shTex, outputDataFolder, materialName + "_sh");

                int splatsPerPass = options.splatsPerPass;
                if (splatsPerPass == 0) splatsPerPass = n;
                splatsPerPass = Mathf.Min(splatsPerPass, n);

                List<Material> materials = new List<Material>();
                List<int> indexCounts = new List<int>();
                List<MeshTopology> topologies = new List<MeshTopology>();
                PassInfo[] passInfos = CreatePassLayout(n, splatsPerPass, options.maxAlphaMaskCount, options.useSRGB);
                AppendMeshLayout(indexCounts, topologies, passInfos, options.useSRGB);

                if (options.useSRGB)
                {
                    Material convertToSRGB = new Material(Shader.Find("VRChatGaussianSplatting/ToSRGB"));
                    convertToSRGB.name = "convert_to_srgb";
                    materials.Add(convertToSRGB);
                }

                Material mainMat = null;
                for (int passInfoIndex = 0; passInfoIndex < passInfos.Length; passInfoIndex++)
                {
                    PassInfo passInfo = passInfos[passInfoIndex];
                    Material splatMat;
                    string splatMatName = materialName + (passInfo.PassIndex > 0 ? $"_pass_{passInfo.PassIndex}" : "_main") + "_splat";
                    if (passInfo.PassIndex == 0)
                    {
                        splatMat = new Material(shader);
                        splatMat.name = splatMatName;
                        mainMat = splatMat;
                    }
                    else
                    {
                        splatMat = new Material(mainMat);
                    }

                    ConfigureSplatMaterial(
                        splatMat, xyzTex, colDcTex, rotTex, scaleTex, shTex,
                        importedSHCoeffCount, n,
                        new Vector4(sharedShMin.x, sharedShMin.y, sharedShMin.z, 0f),
                        new Vector4(Mathf.Max(sharedShRange.x, shRangeEpsilon), Mathf.Max(sharedShRange.y, shRangeEpsilon), Mathf.Max(sharedShRange.z, shRangeEpsilon), 0f),
                        n, (float)effectiveDefaultSHBand, null, false,
                        options.standalone ? sortedTex : null,
                        passInfo.SplatCount, passInfo.SplatOffset);

                    splatMat.SetTexture("_GS_ChunkBounds", chunkBoundsTex);
                    splatMat.SetInt("_GS_ChunkSize", importChunkSize);
                    if (splatMat.HasProperty("_GS_PackedPositions")) splatMat.SetInteger("_GS_PackedPositions", 1);
                    splatMat.EnableKeyword("_GS_PACKED_POSITIONS");

                    if (passInfo.HasAlphaMask)
                    {
                        Material alphaDepthMask = new Material(Shader.Find("VRChatGaussianSplatting/AlphaDepthMask"));
                        alphaDepthMask.name = splatMatName + "_alpha_depth_mask";
                        materials.Add(alphaDepthMask);
                    }
                    splatMat.name = splatMatName;
                    materials.Add(splatMat);
                }

                if (options.useSRGB)
                {
                    Material convertToLinear = new Material(Shader.Find("VRChatGaussianSplatting/ToLinear"));
                    convertToLinear.name = "convert_to_linear";
                    materials.Add(convertToLinear);
                }

                EnsureFolderExists(outputDataFolder + "/materials");
                for (int i = 0; i < materials.Count; ++i)
                {
                    Material splatMat = materials[i];
                    splatMat.renderQueue = options.startRenderQueue + i;
                    string matPath = Path.Combine(outputDataFolder + "/materials", splatMat.name + ".mat");
                    materials[i] = CreateOrReplaceAsset(splatMat, matPath);
                }

                Mesh pointMesh = CreateMultiPassMesh(indexCounts, topologies, bbox);
                pointMesh = CreateOrReplaceAsset(pointMesh, Path.Combine(outputDataFolder, materialName + "_mesh.asset"));
                CreatePrefab(materials, pointMesh, prefabOutputPath, materialName, (int)effectiveDefaultSHBand);

                GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabOutputPath);
                GaussianSplatObject stampObject = prefabRoot != null ? prefabRoot.GetComponent<GaussianSplatObject>() : null;
                if (stampObject != null)
                {
                    stampObject.importMetadataJson = ImportMetadata.ToJson(new ImportMetadata { sourcePath = sourceFile, prefabPath = prefabOutputPath, importAsLOD = false, options = options });
                    EditorUtility.SetDirty(stampObject);
                }
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                try { Directory.Delete(tempFolder, true); } catch { /* temp cleanup best-effort */ }
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
            EnsureFolderExists(folderPath);
            RenderTexture pathTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(assetPath);
            bool changed = false;
            if (pathTexture == null)
            {
                targetTexture = CreateSortRenderTextureAsset(folderPath, assetName, width, height, format, useMipMap, volumeDepth);
                return true;
            }
            if (targetTexture != pathTexture)
            {
                targetTexture = pathTexture;
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
    public class GaussianSplatImportWizard : EditorWindow
    {
        internal const string DefaultOutputFolder = "Assets";
        internal const bool DefaultComputeBoundingBox = true;
        internal const bool DefaultMultiPassRendering = true;
        internal const int DefaultSplatsPerPass = 3 * 256 * 1024;
        internal const bool DefaultStandalone = false;
        internal const int DefaultMaxAlphaMaskCount = 1;
        internal const bool DefaultUseSRGB = true;
        internal const bool DefaultImportSphericalHarmonics = true;
        internal static readonly SHBand DefaultImportedSHBand = SHBand.SH3;
        internal const bool DefaultCompressColorAlphaToBC7 = false;
        internal static readonly GaussianSplatImporter.SHCompression DefaultSHCompression = GaussianSplatImporter.SHCompression.BC7;
        internal const int DefaultStartRenderQueue = 4050;
        internal const int DefaultLODChunkSize = 4096;
        internal const int DefaultLODResamplePercent = 100;
        internal const int DefaultLODReusePercent = 50;
        const int LODMaxSelectionChunkCount = 16384;
        const int ComputedLodMinClusterCount = 1;
        const int MaxPreviewSplats = 32768;
        const float PreviewSplatPixelRadius = 5.0f;

        // LOD: combined GaussianSplatObject with a downsampled LOD pyramid, rendered through the combiner.
        // Standalone: self-rendering mesh + material, no component.
        public enum ImportMode { LOD, Standalone }

        class ImportEntry
        {
            public string path = string.Empty;
            public ImportMode importMode = ImportMode.LOD;
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
            public bool importAsLOD;
            public int lodChunkSize = DefaultLODChunkSize;
            public bool lodUsePackedPositions = true;
            public bool lodComputeSplats = true;
            public int lodResamplePercent = DefaultLODResamplePercent;
            public int lodReusePercent = DefaultLODReusePercent;
            public bool normalizeSize;
            public float normalizeTargetSize = 1.0f;
            public GaussianSplatImporter.SHCompression shCompression = DefaultSHCompression;
            public bool computeBoundingBox = DefaultComputeBoundingBox;
            public bool multiPassRendering = DefaultMultiPassRendering;
            public int splatsPerPass = DefaultSplatsPerPass;
            public bool standalone = DefaultStandalone;
            public int maxAlphaMaskCount = DefaultMaxAlphaMaskCount;
            public bool useSRGB = DefaultUseSRGB;
            public bool importSphericalHarmonics = DefaultImportSphericalHarmonics;
            public SHBand shBand = DefaultImportedSHBand;
            public bool compressColorAlphaToBC7 = DefaultCompressColorAlphaToBC7;
            public int startRenderQueue = DefaultStartRenderQueue;
        }

        class PreviewData
        {
            public string path;
            public Vector3[] positions;
            public Color[] colors;
            public Bounds bounds;
            public int splatCount;
            public int shCoeffCount;   // SH coefficients the source carries (0 = none); valid once splatCount > 0
            public string error;
        }

        sealed class PreviewLoadProgress
        {
            public int processed;
            public int total;
            public string stage = string.Empty;

            public float Normalized => total > 0 ? Mathf.Clamp01(processed / (float)total) : 0.0f;
        }

        class GaussianSplatImportPreviewStage : PreviewSceneStage
        {
            GaussianSplatImportWizard _owner;
            Mesh _previewMesh;
            Material _previewMaterial;
            Bounds _previewBounds;
            bool _showCropBounds;
            Bounds _cropBounds;
            GameObject _previewObject;
            BoxBoundsHandle _boundsHandle = new BoxBoundsHandle();
            bool _visible;
            bool _frameOnRebuild;
            // Saved SceneView camera settings to restore when the preview stage closes (so we don't leave the
            // user's scene navigation with a tiny near clip / slow fly speed).
            SceneView _camView;
            bool _camSaved;
            bool _camDynamicClip;
            float _camNearClip, _camFarClip, _camSpeed, _camSpeedMin, _camSpeedMax;

            public void Initialize(GaussianSplatImportWizard owner)
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
                RestorePreviewCameraSettings();
                _owner?.OnPreviewStageClosed(this);
                base.OnCloseStage();
            }

            // Scale the SceneView near/far clip + fly speed to the preview bbox so a small splat isn't clipped by
            // a giant near plane and navigation speed is proportional. Originals are restored on stage close.
            void ApplyPreviewCameraSettings(SceneView sv)
            {
                if (!_camSaved)
                {
                    _camView = sv;
                    _camDynamicClip = sv.cameraSettings.dynamicClip;
                    _camNearClip = sv.cameraSettings.nearClip;
                    _camFarClip = sv.cameraSettings.farClip;
                    _camSpeed = sv.cameraSettings.speed;
                    _camSpeedMin = sv.cameraSettings.speedMin;
                    _camSpeedMax = sv.cameraSettings.speedMax;
                    _camSaved = true;
                }
                Vector3 size = _previewBounds.size;
                float ext = Mathf.Max(0.001f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
                sv.cameraSettings.dynamicClip = false;
                sv.cameraSettings.nearClip = Mathf.Max(0.0001f, ext * 0.0005f);
                sv.cameraSettings.farClip = Mathf.Max(sv.cameraSettings.nearClip + 1f, ext * 1000f);
                sv.cameraSettings.speedMin = Mathf.Max(0.0001f, ext * 0.01f);
                sv.cameraSettings.speedMax = Mathf.Max(sv.cameraSettings.speedMin + 0.001f, ext * 10f);
                sv.cameraSettings.speed = Mathf.Clamp(ext * 0.5f, sv.cameraSettings.speedMin, sv.cameraSettings.speedMax);
            }

            void RestorePreviewCameraSettings()
            {
                if (!_camSaved || _camView == null)
                {
                    return;
                }
                _camView.cameraSettings.dynamicClip = _camDynamicClip;
                _camView.cameraSettings.nearClip = _camNearClip;
                _camView.cameraSettings.farClip = _camFarClip;
                _camView.cameraSettings.speed = _camSpeed;
                _camView.cameraSettings.speedMin = _camSpeedMin;
                _camView.cameraSettings.speedMax = _camSpeedMax;
                _camSaved = false;
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
                // NOTE: framing is deferred to OnSceneGUI (which consumes _frameOnRebuild). RebuildContent runs
                // BEFORE GoToStage, so SceneView.lastActiveSceneView here is the pre-stage view and an animated
                // Frame is lost when the stage takes over the SceneView. OnSceneGUI runs on the ACTIVE stage view.
            }

            void OnSceneGUI(SceneView sceneView)
            {
                if (!_visible || StageUtility.GetCurrentStage() != this)
                {
                    return;
                }

                // Frame here (not in RebuildContent): this runs on the ACTIVE stage SceneView after GoToStage,
                // so the camera placement actually sticks. Instant frame so there's no animation to lose.
                if (_frameOnRebuild && _previewMesh != null)
                {
                    ApplyPreviewCameraSettings(sceneView);
                    sceneView.Frame(new Bounds(_previewBounds.center, _previewBounds.size), true);
                    sceneView.Repaint();
                    _frameOnRebuild = false;
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
        int _selectedEntryIndex = -1;
        Vector2 scrollPosition = Vector2.zero;
        CancellationTokenSource _previewCancellation;
        Task<PreviewData> _previewTask;
        PreviewLoadProgress _previewProgress;
        string _previewPath;
        PreviewData _previewData;
        Mesh _previewMesh;
        Material _previewMaterial;
        Bounds _previewBounds = new Bounds(Vector3.zero, Vector3.one);
        GaussianSplatImportPreviewStage _previewStage;
        bool _framePreviewOnLoad;

        public static GaussianSplatImportWizard OpenWithSource(string sourcePath)
        {
            GaussianSplatImportWizard window = GetWindow<GaussianSplatImportWizard>();
            window.titleContent = GSEditorText.C("Splat Import", "Splat インポート");
            window.Show();
            window.Focus();

            if (!string.IsNullOrEmpty(sourcePath))
            {
                window._entries.Clear();
                window._entries.Add(new ImportEntry { path = sourcePath });
                window.SelectEntry(0);

                string outputFolder = Path.GetDirectoryName(sourcePath);
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

        public static void ImportWithDefaults(string sourcePath, string prefabPath)
        {
            GaussianSplatImporter.Import(
                sourcePath,
                prefabPath,
                DefaultComputeBoundingBox,
                DefaultSplatsPerPass,
                DefaultStandalone,
                DefaultMaxAlphaMaskCount,
                DefaultUseSRGB,
                DefaultImportSphericalHarmonics,
                DefaultImportedSHBand,
                DefaultCompressColorAlphaToBC7,
                DefaultSHCompression);
        }

        [MenuItem("Gaussian Splatting/Import Splats...")]
        static void Init()
        {
            GaussianSplatImportWizard window = GetWindow<GaussianSplatImportWizard>();
            window.titleContent = GSEditorText.C("Splat Import", "Splat インポート");
            window.Show();
        }

        // Open the import window prefilled from a splat's stored import metadata, so it can be re-imported as-is
        // (just press Import) or with tweaked settings.
        public static void OpenForReimport(GaussianSplatImporter.ImportMetadata md)
        {
            if (md == null) return;
            GaussianSplatImportWizard window = GetWindow<GaussianSplatImportWizard>();
            window.titleContent = GSEditorText.C("Splat Import", "Splat インポート");
            window.Show();
            GaussianSplatImporter.ImportOptions o = md.options;
            ImportMode reimportMode = o.standalone ? ImportMode.Standalone : ImportMode.LOD;
            ImportEntry entry = new ImportEntry
            {
                path = md.sourcePath,
                importMode = reimportMode,
                cropToBounds = o.cropToBounds,
                cropBounds = o.cropBounds,
                applyHorizonAlignment = o.applyHorizonAlignment,
                horizonRotation = o.horizonRotation,
                horizonPivot = o.horizonPivot,
                importAsLOD = md.importAsLOD,
                lodChunkSize = md.lodChunkSize,
                lodUsePackedPositions = o.lodUsePackedPositions,
                lodComputeSplats = o.lodComputeSplats,
                lodResamplePercent = o.lodResamplePercent,
                lodReusePercent = o.lodReusePercent,
                normalizeSize = o.normalizeSize,
                normalizeTargetSize = o.normalizeTargetSize,
                shCompression = o.shCompression,
                computeBoundingBox = o.computeBoundingBox,
                multiPassRendering = o.splatsPerPass > 0,
                splatsPerPass = o.splatsPerPass,
                standalone = o.standalone,
                maxAlphaMaskCount = o.maxAlphaMaskCount,
                useSRGB = o.useSRGB,
                importSphericalHarmonics = o.importSphericalHarmonics,
                shBand = o.defaultSHBand,
                compressColorAlphaToBC7 = o.compressColorAlphaToBC7,
                startRenderQueue = o.startRenderQueue,
            };
            window._entries.Add(entry);
            window.SelectEntry(window._entries.Count - 1);
            // md.prefabPath is project-relative ("Assets/.."); the import loop runs _outputFolder through
            // FileUtil.GetProjectRelativePath, which needs an ABSOLUTE path (else it returns empty and the
            // reimport silently falls back to the project root). Store the absolute path so reimport writes back
            // to the original folder.
            string outFolder = string.IsNullOrEmpty(md.prefabPath) ? null : System.IO.Path.GetDirectoryName(md.prefabPath);
            if (!string.IsNullOrEmpty(outFolder)) window._outputFolder = System.IO.Path.GetFullPath(outFolder).Replace('\\', '/');
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

        static PreviewData LoadPreviewData(string path, CancellationToken token, PreviewLoadProgress progress)
        {
            PreviewData preview = new PreviewData { path = path };
            string tempFolder = Path.Combine(Path.GetTempPath(), "GaussianSplatPreview_" + GaussianSplatImporter.SanitizeAssetName(Path.GetFileNameWithoutExtension(path)) + "_" + DateTime.Now.Ticks.ToString());
            try
            {
                token.ThrowIfCancellationRequested();
                SetPreviewProgress(progress, "Preparing preview", 0, 1);
                string resolvedPlyPath = GaussianSplatImporter.ResolveSourceToPlyPath(path, tempFolder);
                PreviewData resolvedPreview = LoadPlyPreviewDataStreaming(resolvedPlyPath, token, progress);
                resolvedPreview.path = path;
                return resolvedPreview;
            }
            catch (Exception e)
            {
                preview.error = e.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempFolder))
                    {
                        Directory.Delete(tempFolder, true);
                    }
                }
                catch
                {
                    // Preview temp cleanup is best-effort.
                }
            }
            return preview;
        }

        static PreviewData LoadPlyPreviewDataStreaming(string path, CancellationToken token, PreviewLoadProgress progress)
        {
            PreviewData preview = new PreviewData { path = path };
            try
            {
                SetPreviewProgress(progress, "Reading PLY header", 0, 1);
                using FileStream fs = PLYFileReader.OpenDataStream(path, out int splatCount, out int stride, out List<(string, PLYFileReader.ElementType)> attributes);
                if (splatCount <= 0 || stride <= 0)
                {
                    throw new IOException($"PLY preview header read failed for '{path}': vertex count {splatCount:N0}, stride {stride}.");
                }
                Dictionary<string, int> offsets = StreamedSplatReader.BuildFloatAttributeOffsets(attributes);
                string[] required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity" };
                List<string> missing = new List<string>();
                for (int i = 0; i < required.Length; i++)
                {
                    if (!offsets.ContainsKey(required[i]))
                    {
                        missing.Add(required[i]);
                    }
                }
                if (missing.Count > 0)
                {
                    throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {string.Join(",", missing)}");
                }

                preview.splatCount = splatCount;
                preview.shCoeffCount = StreamedSplatReader.DetectSHCoeffCount(offsets);
                int previewCount = Mathf.Min(MaxPreviewSplats, splatCount);
                preview.positions = new Vector3[previewCount];
                preview.colors = new Color[previewCount];
                Bounds bounds = new Bounds();
                bool hasBounds = false;
                byte[] rowBuffer = new byte[stride];
                long dataStart = fs.Position;
                SetPreviewProgress(progress, "Sampling preview", 0, previewCount);

                for (int sampleIndex = 0; sampleIndex < previewCount; sampleIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    long sourceIndex = previewCount > 1
                        ? (long)Math.Round(sampleIndex * (double)(splatCount - 1) / (previewCount - 1))
                        : 0L;
                    fs.Seek(dataStart + sourceIndex * stride, SeekOrigin.Begin);
                    StreamedSplatReader.ReadExact(fs, rowBuffer, stride, path);

                    Vector3 pos = new Vector3(
                        StreamedSplatReader.ReadFloat(rowBuffer, offsets["x"]),
                        StreamedSplatReader.ReadFloat(rowBuffer, offsets["y"]),
                        StreamedSplatReader.ReadFloat(rowBuffer, offsets["z"]));
                    if (hasBounds)
                    {
                        bounds.Encapsulate(pos);
                    }
                    else
                    {
                        bounds = new Bounds(pos, Vector3.zero);
                        hasBounds = true;
                    }

                    Vector3 dc0 = new Vector3(
                        StreamedSplatReader.ReadFloat(rowBuffer, offsets["f_dc_0"]),
                        StreamedSplatReader.ReadFloat(rowBuffer, offsets["f_dc_1"]),
                        StreamedSplatReader.ReadFloat(rowBuffer, offsets["f_dc_2"]));
                    float opacity = StreamedSplatReader.ReadFloat(rowBuffer, offsets["opacity"]);
                    Vector3 color = StreamedSplatReader.SH0ToColor(dc0);
                    preview.positions[sampleIndex] = pos;
                    preview.colors[sampleIndex] = new Color(color.x, color.y, color.z, Mathf.Clamp01(StreamedSplatReader.Sigmoid(opacity)));
                    if ((sampleIndex & 255) == 0)
                    {
                        SetPreviewProgress(progress, "Sampling preview", sampleIndex, previewCount);
                    }
                }
                SetPreviewProgress(progress, "Sampling preview", previewCount, previewCount);

                preview.bounds = hasBounds ? bounds : new Bounds(Vector3.zero, Vector3.one);
            }
            catch (Exception e)
            {
                preview.error = e.Message;
            }
            return preview;
        }

        static void SetPreviewProgress(PreviewLoadProgress progress, string stage, int processed, int total)
        {
            if (progress == null)
            {
                return;
            }
            progress.stage = stage;
            progress.processed = processed;
            progress.total = Mathf.Max(0, total);
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
            _previewProgress = new PreviewLoadProgress { stage = "Queued", processed = 0, total = 1 };
            CancellationToken token = _previewCancellation.Token;
            PreviewLoadProgress progress = _previewProgress;
            _previewTask = Task.Run(() => LoadPreviewData(path, token, progress), token);
        }

        void CancelPreviewLoad()
        {
            if (_previewCancellation != null)
            {
                _previewCancellation.Cancel();
                _previewCancellation = null;
            }
            _previewTask = null;
            _previewProgress = null;
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
                _previewProgress = null;
                return;
            }
            _previewTask = null;
            _previewProgress = null;
            if (preview.path != _previewPath || !string.IsNullOrEmpty(preview.error))
            {
                if (!string.IsNullOrEmpty(preview.error))
                {
                    Debug.LogWarning("Gaussian splat preview failed: " + preview.error);
                }
                return;
            }
            if (preview.splatCount <= 0 || preview.positions == null || preview.positions.Length == 0)
            {
                Debug.LogWarning($"Gaussian splat preview failed: '{preview.path}' produced an empty preview.");
                return;
            }

            _previewData = preview;
            CreatePreviewObject(preview);
            ImportEntry entry = SelectedEntry;
            if (entry != null && entry.cropBounds.size == Vector3.one && entry.cropBounds.center == Vector3.zero)
            {
                entry.cropBounds = preview.bounds;
            }
            if (_framePreviewOnLoad || IsPreviewActive())
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

        // Floater-robust bounds: per-axis 2.5/97.5 percentile of the sampled preview centers, so stray outlier
        // splats don't inflate the framing box. Falls back to the raw bounds for tiny point counts.
        static Bounds ComputeRobustPreviewBounds(Vector3[] centers, int count, Bounds rawBounds)
        {
            if (count <= 16) return rawBounds;
            float[] xs = new float[count];
            float[] ys = new float[count];
            float[] zs = new float[count];
            for (int i = 0; i < count; i++) { xs[i] = centers[i].x; ys[i] = centers[i].y; zs[i] = centers[i].z; }
            System.Array.Sort(xs);
            System.Array.Sort(ys);
            System.Array.Sort(zs);
            int lo = Mathf.Clamp(Mathf.RoundToInt(count * 0.025f), 0, count - 1);
            int hi = Mathf.Clamp(Mathf.RoundToInt(count * 0.975f), 0, count - 1);
            Vector3 min = new Vector3(xs[lo], ys[lo], zs[lo]);
            Vector3 max = new Vector3(xs[hi], ys[hi], zs[hi]);
            return new Bounds((min + max) * 0.5f, Vector3.Max(max - min, Vector3.one * 0.001f));
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
            Vector3[] previewCenters = new Vector3[splatCount];
            Bounds previewBounds = new Bounds();
            bool hasBounds = false;
            for (int i = 0; i < splatCount; i++)
            {
                int vertex = i * 4;
                int index = i * 6;
                Vector3 center = ToPreviewSpace(ApplyPreviewAlignment(entry, preview.positions[i]));
                previewCenters[i] = center;
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
            // Frame on a floater-robust bbox (2.5/97.5 percentile per axis) so a few stray splats don't blow the
            // bounds up and zoom the camera way out. Mesh keeps the full raw bounds for correct frustum culling.
            _previewBounds = hasBounds ? ComputeRobustPreviewBounds(previewCenters, splatCount, previewBounds) : new Bounds(Vector3.zero, Vector3.one);

            _previewMesh = new Mesh { name = "Gaussian Splat Import Preview Mesh", indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            _previewMesh.hideFlags = HideFlags.HideAndDontSave;
            _previewMesh.vertices = vertices;
            _previewMesh.colors = colors;
            _previewMesh.uv = uvs;
            _previewMesh.triangles = triangles;
            _previewMesh.bounds = hasBounds ? previewBounds : _previewBounds;

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

        string GetCropEstimateLabel(ImportEntry entry)
        {
            if (entry == null || !entry.cropToBounds || _previewData == null || _previewData.positions == null || _previewData.positions.Length == 0 || _previewData.splatCount <= 0)
            {
                return string.Empty;
            }

            int sampledInside = 0;
            for (int i = 0; i < _previewData.positions.Length; i++)
            {
                Vector3 pos = ApplyPreviewAlignment(entry, _previewData.positions[i]);
                if (entry.cropBounds.Contains(pos))
                {
                    sampledInside++;
                }
            }

            float ratio = sampledInside / (float)Mathf.Max(1, _previewData.positions.Length);
            int estimatedCount = Mathf.Clamp(Mathf.RoundToInt(_previewData.splatCount * ratio), 0, _previewData.splatCount);
            return $"Estimated crop result: {estimatedCount:N0} / {_previewData.splatCount:N0} splats ({ratio * 100.0f:0.0}%, sampled {sampledInside:N0} / {_previewData.positions.Length:N0})";
        }

        int EstimateSelectedImportSplatCount(ImportEntry entry)
        {
            if (entry == null || _previewData == null || _previewData.splatCount <= 0)
            {
                return -1;
            }

            if (!entry.cropToBounds || _previewData.positions == null || _previewData.positions.Length == 0)
            {
                return _previewData.splatCount;
            }

            int sampledInside = 0;
            for (int i = 0; i < _previewData.positions.Length; i++)
            {
                Vector3 pos = ApplyPreviewAlignment(entry, _previewData.positions[i]);
                if (entry.cropBounds.Contains(pos))
                {
                    sampledInside++;
                }
            }

            float ratio = sampledInside / (float)Mathf.Max(1, _previewData.positions.Length);
            return Mathf.Clamp(Mathf.RoundToInt(_previewData.splatCount * ratio), 0, _previewData.splatCount);
        }

        // Surfaces the two limits that make the importer store less SH than asked for -- a source that carries
        // fewer bands, and (combined modes) the stored-splats x coefficients cap -- before the import runs.
        void DrawSHLimitWarnings(ImportEntry entry)
        {
            if (_previewData == null || _previewData.splatCount <= 0)
            {
                return;
            }

            int requestedCoeffCount = GaussianSplatImporter.SHCoeffCountForBand(entry.shBand);
            if (requestedCoeffCount <= 0)
            {
                return;
            }

            int fileCoeffCount = _previewData.shCoeffCount;
            if (fileCoeffCount <= 0)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "The source carries no SH coefficients (no f_rest_* properties); this splat will import DC-only regardless of the band.",
                    "ソースに SH 係数 (f_rest_* プロパティ) がありません。バンド設定に関わらず DC のみでインポートされます。"), MessageType.Warning);
                return;
            }

            SHBand fileBand = GaussianSplatImporter.SHBandForCoeffCount(fileCoeffCount);
            if (fileCoeffCount < requestedCoeffCount)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    $"The source only carries {fileCoeffCount} SH coefficients ({fileBand}); this splat will import at {fileBand}, not {entry.shBand}.",
                    $"ソースは SH 係数を {fileCoeffCount} 個 ({fileBand}) しか持ちません。{entry.shBand} ではなく {fileBand} でインポートされます。"), MessageType.Warning);
            }

            // Combined modes bake SH per stored splat, so the computed-LOD pyramid counts against the cap too.
            int effectiveCoeffCount = Mathf.Min(fileCoeffCount, requestedCoeffCount);
            int estimatedSourceCount = EstimateSelectedImportSplatCount(entry);
            if (!entry.importAsLOD || estimatedSourceCount <= 0)
            {
                return;
            }

            int storedCount = EstimateStoredLodSplatCount(estimatedSourceCount, entry.lodChunkSize, entry.lodComputeSplats, entry.lodResamplePercent, entry.lodReusePercent);
            SHBand cappedBand = GaussianSplatImporter.ResolveLODImportSHBand(effectiveCoeffCount, storedCount);
            int cappedCoeffCount = GaussianSplatImporter.SHCoeffCountForBand(cappedBand);
            if (cappedCoeffCount >= effectiveCoeffCount)
            {
                return;
            }

            long shTexels = (long)storedCount * effectiveCoeffCount;
            string outcomeEn = cappedCoeffCount > 0
                ? $"This splat will import at {cappedBand} instead."
                : "No SH band fits at this stored splat count, so this splat will import DC-only.";
            string outcomeJa = cappedCoeffCount > 0
                ? $"代わりに {cappedBand} でインポートされます。"
                : "この保存 Splat 数では収まる SH バンドがないため、DC のみでインポートされます。";
            string lodNoteEn = entry.lodComputeSplats
                ? $" Computed LOD raises the stored count above the {estimatedSourceCount:N0} source splats; without it the import stores {estimatedSourceCount:N0}."
                : string.Empty;
            string lodNoteJa = entry.lodComputeSplats
                ? $" 計算 LOD により保存数がソースの {estimatedSourceCount:N0} splat を超えています。無効にすると {estimatedSourceCount:N0} が保存されます。"
                : string.Empty;
            EditorGUILayout.HelpBox(GSEditorText.T(
                $"{GaussianSplatImporter.SHBandForCoeffCount(effectiveCoeffCount)} needs {storedCount:N0} stored splats x {effectiveCoeffCount} coefficients = {shTexels:N0} SH texels, over the import cap of {GaussianSplatImporter.MaxLODImportSHTexels:N0}. {outcomeEn}{lodNoteEn}",
                $"{GaussianSplatImporter.SHBandForCoeffCount(effectiveCoeffCount)} は 保存 {storedCount:N0} splat x 係数 {effectiveCoeffCount} = SH テクセル {shTexels:N0} を要し、インポート上限 {GaussianSplatImporter.MaxLODImportSHTexels:N0} を超えます。{outcomeJa}{lodNoteJa}"), MessageType.Warning);
        }

        string GetComputedLodSplatEstimateLabel(ImportEntry entry)
        {
            int estimatedSourceCount = EstimateSelectedImportSplatCount(entry);
            if (estimatedSourceCount < 0)
            {
                return GSEditorText.T("Effective stored splat count will be shown after the preview has loaded.",
                    "プレビュー読み込み後に有効な保存 Splat 数を表示します。");
            }

            int resamplePercent = NormalizeLODResamplePercent(entry.lodResamplePercent);
            int reusePercent = NormalizeLODReusePercent(entry.lodReusePercent);
            int resampledLod0Count = EstimateResampledLod0SplatCount(estimatedSourceCount, entry.lodChunkSize, resamplePercent);
            int storedCount = EstimateStoredLodSplatCount(estimatedSourceCount, entry.lodChunkSize, true, resamplePercent, reusePercent);
            int lodSplatCount = Mathf.Max(0, storedCount - resampledLod0Count);
            float sourceMultiplier = estimatedSourceCount > 0 ? storedCount / (float)estimatedSourceCount : 0.0f;
            float lod0Multiplier = estimatedSourceCount > 0 ? resampledLod0Count / (float)estimatedSourceCount : 0.0f;
            return $"Estimated splats: source {estimatedSourceCount:N0}, LOD0 {resampledLod0Count:N0} ({resamplePercent}%, {lod0Multiplier:0.00}x), computed LOD {lodSplatCount:N0}, stored {storedCount:N0} ({sourceMultiplier:0.00}x source, {reusePercent}% reused)";
        }

        static int EstimateStoredLodSplatCount(int splatCount, int chunkSize, bool computeLodSplats)
        {
            return EstimateStoredLodSplatCount(splatCount, chunkSize, computeLodSplats, DefaultLODResamplePercent, DefaultLODReusePercent);
        }

        static int EstimateStoredLodSplatCount(int splatCount, int chunkSize, bool computeLodSplats, int resamplePercent)
        {
            return EstimateStoredLodSplatCount(splatCount, chunkSize, computeLodSplats, resamplePercent, DefaultLODReusePercent);
        }

        static int EstimateStoredLodSplatCount(int splatCount, int chunkSize, bool computeLodSplats, int resamplePercent, int reusePercent)
        {
            splatCount = Mathf.Max(0, splatCount);
            chunkSize = Mathf.Max(1, chunkSize);
            resamplePercent = computeLodSplats ? NormalizeLODResamplePercent(resamplePercent) : DefaultLODResamplePercent;
            reusePercent = computeLodSplats ? NormalizeLODReusePercent(reusePercent) : DefaultLODReusePercent;
            if (splatCount > 0)
            {
                chunkSize = Mathf.Max(chunkSize, Mathf.CeilToInt(splatCount / (float)LODMaxSelectionChunkCount));
            }

            int total = 0;
            for (int offset = 0; offset < splatCount; offset += chunkSize)
            {
                int sourceCount = Mathf.Min(chunkSize, splatCount - offset);
                int lod0Count = computeLodSplats ? ComputeResampledLod0SplatCountForChunk(sourceCount, resamplePercent) : sourceCount;
                total += lod0Count;
                if (!computeLodSplats)
                {
                    continue;
                }

                for (int level = 1; level < 30; level++)
                {
                    int outputDivisor = 1 << Mathf.Min(30, level);
                    int outputCount = Mathf.FloorToInt(lod0Count / (float)outputDivisor + 0.5f);
                    int reuseCount = Mathf.Clamp(Mathf.FloorToInt(outputCount * (reusePercent / 100.0f) + 0.5f), 0, outputCount);
                    int clusterCount = outputCount - reuseCount;
                    if (clusterCount < ComputedLodMinClusterCount || clusterCount >= lod0Count)
                    {
                        break;
                    }
                    total += clusterCount;
                }
            }
            return total;
        }

        static int EstimateResampledLod0SplatCount(int splatCount, int chunkSize, int resamplePercent)
        {
            splatCount = Mathf.Max(0, splatCount);
            chunkSize = Mathf.Max(1, chunkSize);
            resamplePercent = NormalizeLODResamplePercent(resamplePercent);
            if (splatCount > 0)
            {
                chunkSize = Mathf.Max(chunkSize, Mathf.CeilToInt(splatCount / (float)LODMaxSelectionChunkCount));
            }

            int total = 0;
            for (int offset = 0; offset < splatCount; offset += chunkSize)
            {
                total += ComputeResampledLod0SplatCountForChunk(Mathf.Min(chunkSize, splatCount - offset), resamplePercent);
            }
            return total;
        }

        static int ComputeResampledLod0SplatCountForChunk(int sourceCount, int resamplePercent)
        {
            sourceCount = Mathf.Max(0, sourceCount);
            if (sourceCount <= 0)
            {
                return 0;
            }

            resamplePercent = NormalizeLODResamplePercent(resamplePercent);
            if (resamplePercent >= DefaultLODResamplePercent)
            {
                return sourceCount;
            }

            return Mathf.Clamp(Mathf.RoundToInt(sourceCount * (resamplePercent / 100.0f)), 1, sourceCount);
        }

        static int NormalizeLODResamplePercent(int resamplePercent)
        {
            return resamplePercent <= 0 ? DefaultLODResamplePercent : Mathf.Clamp(resamplePercent, 1, DefaultLODResamplePercent);
        }

        static int NormalizeLODReusePercent(int reusePercent)
        {
            return reusePercent <= 0 ? DefaultLODReusePercent : Mathf.Clamp(reusePercent, 1, 99);
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

        // The import mode is the source of truth; the derived flags read below (and by ImportSingle) are
        // recomputed here so they are correct even for entries whose foldout was never drawn.
        static void ApplyImportModeFlags(ImportEntry entry)
        {
            entry.standalone = entry.importMode == ImportMode.Standalone;
            entry.importAsLOD = entry.importMode == ImportMode.LOD;
            entry.lodComputeSplats = entry.importMode == ImportMode.LOD;
        }

        GaussianSplatImporter.ImportOptions GetEntryOptions(ImportEntry entry)
        {
            ApplyImportModeFlags(entry);
            // Every splat owns its full settings.
            GaussianSplatImporter.ImportOptions options = new GaussianSplatImporter.ImportOptions
            {
                computeBoundingBox = entry.computeBoundingBox,
                splatsPerPass = entry.multiPassRendering ? entry.splatsPerPass : 0,
                standalone = entry.standalone,
                maxAlphaMaskCount = entry.maxAlphaMaskCount,
                useSRGB = entry.useSRGB,
                importSphericalHarmonics = entry.importSphericalHarmonics,
                defaultSHBand = entry.shBand,
                compressColorAlphaToBC7 = entry.compressColorAlphaToBC7,
                startRenderQueue = entry.startRenderQueue
            };

            options.cropToBounds = entry.cropToBounds;
            options.cropBounds = entry.cropBounds;
            options.applyHorizonAlignment = entry.applyHorizonAlignment;
            options.horizonRotation = (entry.applyWallAlignment ? entry.wallRotation : Quaternion.identity) * entry.horizonRotation;
            options.horizonPivot = entry.horizonPivot;
            options.lodUsePackedPositions = entry.importAsLOD && entry.lodUsePackedPositions;
            options.lodComputeSplats = entry.importAsLOD && entry.lodComputeSplats;
            options.lodResamplePercent = options.lodComputeSplats ? NormalizeLODResamplePercent(entry.lodResamplePercent) : DefaultLODResamplePercent;
            options.lodReusePercent = options.lodComputeSplats ? NormalizeLODReusePercent(entry.lodReusePercent) : DefaultLODReusePercent;
            options.normalizeSize = entry.normalizeSize;
            options.normalizeTargetSize = entry.normalizeTargetSize;
            options.shCompression = entry.shCompression;
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
                    _framePreviewOnLoad = true;
                    StartPreviewLoad(entry.path);
                }
            }
            if (_previewTask != null && !_previewTask.IsCompleted)
            {
                PreviewLoadProgress progress = _previewProgress;
                string stage = progress != null && !string.IsNullOrEmpty(progress.stage) ? progress.stage : "Loading preview";
                float normalized = progress?.Normalized ?? 0.0f;
                Rect progressRect = GUILayoutUtility.GetRect(18.0f, 18.0f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, normalized, $"{stage} {normalized * 100.0f:0.0}%");
                if (progress != null && progress.total > 0)
                {
                    EditorGUILayout.LabelField($"{progress.processed:N0} / {progress.total:N0} splats", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.HelpBox(GSEditorText.T("Loading preview asynchronously...", "プレビューを非同期で読み込み中..."), MessageType.Info);
                }
            }
            else if (_previewData != null && _previewData.positions != null)
            {
                EditorGUILayout.LabelField($"Preview loaded: {_previewData.positions.Length:N0} sampled / {_previewData.splatCount:N0} splats", EditorStyles.miniLabel);
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
            string cropEstimateLabel = GetCropEstimateLabel(entry);
            if (!string.IsNullOrEmpty(cropEstimateLabel))
            {
                EditorGUILayout.LabelField(cropEstimateLabel, EditorStyles.miniLabel);
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

            // --- Import type ---
            // LOD splats import as a combined GaussianSplatObject rendered through the combiner; Standalone
            // splats stay a self-rendering mesh + material with no component. The mode drives the derived flags
            // read below (standalone / importAsLOD / lodComputeSplats), and gates which settings are shown.
            EditorGUILayout.Space(6f);
            entry.importMode = (ImportMode)EditorGUILayout.EnumPopup(GSEditorText.T("Import Mode", "インポートモード"), entry.importMode);
            switch (entry.importMode)
            {
                case ImportMode.LOD:
                    EditorGUILayout.HelpBox(GSEditorText.T(
                        "Combined GaussianSplatObject with a downsampled LOD pyramid; the combiner selects detail by distance and budget.",
                        "ダウンサンプリングした LOD ピラミッドを持つ統合 GaussianSplatObject。combiner が距離と予算に応じて詳細度を選択します。"), MessageType.Info);
                    break;
                case ImportMode.Standalone:
                    EditorGUILayout.HelpBox(GSEditorText.T(
                        "Precomputes sorting for octahedral directions so the splat renders on its own, without the GaussianSplatRenderer/combiner. Uses much more texture memory and may show rendering artifacts.",
                        "八面体方向のソートを事前計算し、GaussianSplatRenderer/combiner なしで Splat を単独描画します。テクスチャメモリを大幅に多く使用し、描画アーティファクトが出る場合があります。"), MessageType.Warning);
                    break;
            }
            ApplyImportModeFlags(entry);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(GSEditorText.T("Splat Settings", "Splat 設定"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            // Combined-object settings (LOD and Normal modes). Chunking and packed positions apply to both;
            // the resampling controls only matter for the LOD pyramid.
            if (entry.importAsLOD)
            {
                entry.lodChunkSize = Mathf.Max(1, EditorGUILayout.IntField(GSEditorText.T("Chunk Size", "チャンクサイズ"), entry.lodChunkSize));
                entry.lodUsePackedPositions = EditorGUILayout.Toggle(GSEditorText.T("Pack Positions", "位置をパック"), entry.lodUsePackedPositions);
                if (entry.lodComputeSplats)
                {
                    entry.lodResamplePercent = EditorGUILayout.IntSlider(GSEditorText.T("LOD Resampling Rate", "LOD リサンプリング率"), NormalizeLODResamplePercent(entry.lodResamplePercent), 1, DefaultLODResamplePercent);
                    entry.lodReusePercent = EditorGUILayout.IntSlider(GSEditorText.T("LOD Reused Splats", "LOD 再利用 Splat"), NormalizeLODReusePercent(entry.lodReusePercent), 1, 99);
                    EditorGUILayout.LabelField(GetComputedLodSplatEstimateLabel(entry), EditorStyles.wordWrappedMiniLabel);
                }
            }

            // Common geometry transforms — apply to both LOD and non-LOD (the streamed front-end bakes them
            // identically into either output, so they are not LOD-specific). (The y-flip is always baked into
            // coordinates -> identity prefab scale; there is no negative-scale option.)
            entry.normalizeSize = EditorGUILayout.Toggle(GSEditorText.T("Normalize Size", "サイズを正規化"), entry.normalizeSize);
            if (entry.normalizeSize)
            {
                entry.normalizeTargetSize = Mathf.Max(0.0001f, EditorGUILayout.FloatField(GSEditorText.T("Target Size", "目標サイズ"), entry.normalizeTargetSize));
            }

            // Spherical harmonics (both LOD and non-LOD), with its compression in the same place.
            entry.importSphericalHarmonics = EditorGUILayout.Toggle(GSEditorText.T("Import Spherical Harmonics", "球面調和をインポート"), entry.importSphericalHarmonics);
            if (entry.importSphericalHarmonics)
            {
                entry.shBand = (SHBand)EditorGUILayout.EnumPopup(GSEditorText.T("Max SH Band", "最大 SH バンド"), entry.shBand);
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Imports higher-order SH coefficient textures only up to the selected max band and sets the imported material to that band. If the selected band has no non-zero coefficients in the file, the importer falls back to the highest lower non-zero band.",
                    "選択した最大バンドまでの高次 SH 係数テクスチャだけをインポートし、インポートされたマテリアルをそのバンドに設定します。選択したバンドに非ゼロ係数がない場合は、より低い非ゼロの最大バンドにフォールバックします。"), MessageType.Info);
                DrawSHLimitWarnings(entry);
                entry.shCompression = (GaussianSplatImporter.SHCompression)EditorGUILayout.EnumPopup(GSEditorText.T("SH Compression", "SH 圧縮"), entry.shCompression);
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "SH texture format (LOD and non-LOD): None = RGB565 (largest), BC1 = 4bpp (smallest), BC7 = 8bpp. Compression is lossy but SH error is small. (LOD only: the import steps down to the highest band that fits its SH cap, and the scene's fused SH must fit one texture; objects past that fall back to DC.)",
                    "SH テクスチャ形式 (LOD・非 LOD 共通): None = RGB565 (最大)、BC1 = 4bpp (最小)、BC7 = 8bpp。圧縮は不可逆ですが SH の誤差は小さいです。(LOD のみ: インポートは SH 上限に収まる最大バンドまで自動的に下げられ、さらにシーン全体の統合 SH は 1 テクスチャに収まる必要があります。超過するオブジェクトは DC にフォールバックします。)"), MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Skips SH coefficient texture generation and forces imported materials to SH0 only.",
                    "SH 係数テクスチャの生成をスキップし、インポートされたマテリアルを SH0 のみにします。"), MessageType.Info);
            }

            entry.computeBoundingBox = EditorGUILayout.Toggle(GSEditorText.T("Compute Bounding Box", "バウンディングボックスを計算"), entry.computeBoundingBox);

            // Color/alpha texture compression — common to both paths (both outputs have a color texture).
            entry.compressColorAlphaToBC7 = EditorGUILayout.Toggle(GSEditorText.T("Compress ColorAlpha (BC7)", "色アルファを圧縮 (BC7)"), entry.compressColorAlphaToBC7);
            if (entry.compressColorAlphaToBC7 || entry.shCompression != GaussianSplatImporter.SHCompression.None)
            {
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "The importer always packs generated splat textures into 4x4-aligned blocks. Compression applies only to the color/alpha and SH textures; position, scale, and sorting textures stay uncompressed.",
                    "インポーターは生成される Splat テクスチャを常に 4x4 境界に合わせたブロックに詰めます。圧縮は色アルファと SH テクスチャにのみ適用され、位置・スケール・ソートテクスチャは非圧縮のままです。"), MessageType.Info);
            }

            // Standalone rendering settings (not used by combined GaussianSplatObject splats, which render
            // via the combiner).
            if (!entry.importAsLOD)
            {
                entry.useSRGB = EditorGUILayout.Toggle(GSEditorText.T("sRGB Color Correction", "sRGB 色補正"), entry.useSRGB);
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Color correction requires 2 additional grab passes, for small splats you might want to disable this. Without this enabled back to front rendering will be used, which makes multi-pass rendering not work. sRGB color correction only works correctly if the world has HDR camera render targets.",
                    "色補正には追加で 2 回の Grab パスが必要です。小さな Splat では無効にした方がよい場合があります。これを有効にしない場合は後ろから前への描画になり、マルチパス描画は機能しません。sRGB 色補正は、ワールドのカメラレンダーターゲットが HDR の場合にのみ正しく動作します。"), MessageType.Info);
                if (entry.useSRGB)
                {
                    entry.multiPassRendering = EditorGUILayout.Toggle(GSEditorText.T("Multi-Pass Rendering", "マルチパス描画"), entry.multiPassRendering);
                    if (entry.multiPassRendering)
                    {
                        entry.splatsPerPass = Mathf.Clamp(EditorGUILayout.IntField(GSEditorText.T("Splat Count Per Pass", "パスごとの Splat 数"), entry.splatsPerPass), 128 * 1024, 8 * 1024 * 1024);
                        EditorGUILayout.HelpBox(GSEditorText.T(
                            "The rendering of the splat is split into multiple sequential chunks, can help with VR rendering performance.",
                            "Splat の描画を複数の連続チャンクに分割します。VR 描画性能の改善に役立つ場合があります。"), MessageType.Info);
                        entry.maxAlphaMaskCount = Mathf.Max(0, EditorGUILayout.IntField(GSEditorText.T("Max Alpha Mask Count", "最大アルファマスク数"), entry.maxAlphaMaskCount));
                        EditorGUILayout.HelpBox(GSEditorText.T(
                            "After each chunk is rendered an optional alpha mask pass is added using a grab pass and stencil. This will occlude the following chunks if they are behind opaque objects. This can help performance, but grab pass can be expensive, so use it with care. If you have more than 4M splats you might want to have more than 1 alpha mask pass.",
                            "各チャンクの描画後に、Grab パスとステンシルを使った任意のアルファマスクパスを追加します。不透明オブジェクトの背後にある後続チャンクを遮蔽できます。性能改善に役立つ場合がありますが、Grab パスは高コストなので注意してください。400 万を超える Splat では、アルファマスクパスを 2 つ以上にした方がよい場合があります。"), MessageType.Info);
                    }
                }
                entry.startRenderQueue = Mathf.Clamp(EditorGUILayout.IntField(GSEditorText.T("Start Render Queue", "開始レンダーキュー"), entry.startRenderQueue), 2000, 5000);
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Starting render queue for the generated splat materials. Each generated material is assigned a sequential queue from this value.",
                    "生成される Splat マテリアルの開始レンダーキューです。各マテリアルにはこの値から順番にキューが割り当てられます。"), MessageType.Info);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4f);
            if (GUILayout.Button(GSEditorText.T("Copy These Settings To All Splats", "この設定を全 Splat にコピー")))
            {
                CopyEntrySettingsToAll(entry);
            }
        }

        // "Globally shared" workflow: apply one splat's import settings to every other entry (crop/horizon stay per-splat).
        void CopyEntrySettingsToAll(ImportEntry source)
        {
            foreach (ImportEntry e in _entries)
            {
                if (e == source) continue;
                e.importMode = source.importMode;
                e.importAsLOD = source.importAsLOD;
                e.lodChunkSize = source.lodChunkSize;
                e.lodUsePackedPositions = source.lodUsePackedPositions;
                e.lodComputeSplats = source.lodComputeSplats;
                e.lodResamplePercent = source.lodResamplePercent;
                e.lodReusePercent = source.lodReusePercent;
                e.normalizeSize = source.normalizeSize;
                e.normalizeTargetSize = source.normalizeTargetSize;
                e.shCompression = source.shCompression;
                e.importSphericalHarmonics = source.importSphericalHarmonics;
                e.shBand = source.shBand;
                e.computeBoundingBox = source.computeBoundingBox;
                e.useSRGB = source.useSRGB;
                e.multiPassRendering = source.multiPassRendering;
                e.splatsPerPass = source.splatsPerPass;
                e.maxAlphaMaskCount = source.maxAlphaMaskCount;
                e.standalone = source.standalone;
                e.compressColorAlphaToBC7 = source.compressColorAlphaToBC7;
                e.startRenderQueue = source.startRenderQueue;
            }
            Debug.Log($"[GaussianSplatting] Copied import settings from '{System.IO.Path.GetFileName(source.path)}' to {_entries.Count - 1} other splat(s).");
        }

        void OnGUI()
        {
            PollPreviewLoad();
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.LabelField(GSEditorText.T("Splat files", "Splat ファイル"), EditorStyles.boldLabel);
            if (GUILayout.Button(GSEditorText.T("Clear All Files", "すべてのファイルをクリア")))
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
                    string path = EditorUtility.OpenFilePanelWithFilters(GSEditorText.T("Select Splat File", "Splat ファイルを選択"), Application.dataPath, GaussianSplatImporter.ImportFilePanelFilters);
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
            if (GUILayout.Button(GSEditorText.T("+ Add Splat File", "+ Splat ファイルを追加"))) AddEntry();
            if (GUILayout.Button(GSEditorText.T("Add All Splats in Folder", "フォルダ内の Splat をすべて追加")))
            {
                string folder = EditorUtility.OpenFolderPanel(GSEditorText.T("Select Folder with Splat Files", "Splat ファイルのあるフォルダを選択"), Application.dataPath, "");
                if (!string.IsNullOrEmpty(folder))
                {
                    string[] files = Directory.GetFiles(folder, "*.*")
                        .Where(GaussianSplatImporter.IsSupportedImportSourcePath)
                        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
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
                "Large imports still depend on available RAM, but the importer streams vertex data so file size is no longer capped by a 2GB raw read buffer. SH import memory still scales with the selected SH band.",
                "大きなインポートは引き続き使用可能な RAM に依存しますが、インポーターは頂点データをストリーミングするため、ファイルサイズは 2GB の生読み込みバッファに制限されなくなりました。SH インポートのメモリ使用量は選択した SH バンドに応じて増えます。"), MessageType.Info);

            DrawSelectedEntrySettings();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(GSEditorText.T("Output Folder", "出力フォルダ"), EditorStyles.boldLabel);
            _outputFolder = EditorGUILayout.TextField(_outputFolder);
            if (GUILayout.Button("…", GUILayout.Width(30)))
                _outputFolder = EditorUtility.OpenFolderPanel(GSEditorText.T("Select Output Folder", "出力フォルダを選択"), _outputFolder, "");

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(GSEditorText.T("Import All Splats", "すべての Splat をインポート")))
            {
                if (!_entries.Any(e => !string.IsNullOrEmpty(e.path)))
                {
                    EditorUtility.DisplayDialog(GSEditorText.T("Splat Import", "Splat インポート"), GSEditorText.T("Add at least one supported splat path.", "対応する Splat パスを少なくとも 1 つ追加してください。"), "OK");
                    return;
                }

                foreach (ImportEntry entry in _entries.Where(e => !string.IsNullOrEmpty(e.path)))
                {
                    string sourcePath = entry.path;
                    string prefabName = Path.GetFileNameWithoutExtension(sourcePath) + ".prefab";
                    string relFolder  = FileUtil.GetProjectRelativePath(_outputFolder);
                    if (string.IsNullOrEmpty(relFolder))
                        relFolder = "Assets";
                    string prefabPath = Path.Combine(relFolder, prefabName).Replace('\\', '/');
                    ImportSingle(entry, prefabPath);
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog(GSEditorText.T("Splat Import", "Splat インポート"), GSEditorText.T("All imports completed.", "すべてのインポートが完了しました。"), "OK");
            }
            EditorGUILayout.EndScrollView();
        }

        void ImportSingle(ImportEntry entry, string prefabPath)
        {
            try
            {
                string sourcePath = entry.path;
                EditorUtility.DisplayProgressBar(GSEditorText.T("Splat Import", "Splat インポート"),
                    GSEditorText.T($"Importing {Path.GetFileName(sourcePath)}", $"{Path.GetFileName(sourcePath)} をインポート中"), 0f);
                GaussianSplatImporter.ImportOptions options = GetEntryOptions(entry);
                if (entry.importAsLOD)
                {
                    ImportLOD(sourcePath, prefabPath, entry.lodChunkSize, options);
                }
                else
                {
                    GaussianSplatImporter.Import(sourcePath, prefabPath, options);
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(GSEditorText.T("Splat Import Failed", "Splat インポート失敗"), e.Message, "OK");
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static void ImportLOD(string sourcePath, string prefabPath, int chunkSize, GaussianSplatImporter.ImportOptions options)
        {
            Type importerType = Type.GetType("GaussianSplatting.GaussianSplatLODImporter, GaussianSplatting.Editor");
            if (importerType == null)
            {
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    importerType = assembly.GetType("GaussianSplatting.GaussianSplatLODImporter");
                    if (importerType != null)
                    {
                        break;
                    }
                }
            }

            System.Reflection.MethodInfo importMethod = importerType?.GetMethod(
                "ImportLODToPrefab",
                new[] { typeof(string), typeof(string), typeof(int), typeof(GaussianSplatImporter.ImportOptions) });
            if (importMethod == null)
            {
                throw new InvalidOperationException("LOD importer is not available. Check that GaussianSplatting.Editor compiled successfully.");
            }

            try
            {
                importMethod.Invoke(null, new object[] { sourcePath, prefabPath, chunkSize, options });
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }
    }
}
#endif
