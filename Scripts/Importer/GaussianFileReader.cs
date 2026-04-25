#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
{
    public struct ImportSplatData
    {
        public Vector3 pos;
        public Vector3 dc0;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    [BurstCompile]
    public class GaussianFileReader
    {
        // Returns splat count
        public static int ReadFileHeader(string filePath)
        {
            int vertexCount = 0;
            if (File.Exists(filePath))
            {
                if (isPLY(filePath))
                    PLYFileReader.ReadFileHeader(filePath, out vertexCount, out _, out _);
                else if (isSPZ(filePath))
                    SPZFileReader.ReadFileHeader(filePath, out vertexCount);
            }
            return vertexCount;
        }

        public static unsafe void ReadFile(string filePath, int shCoeffCount, out NativeArray<ImportSplatData> splats, out NativeArray<Vector3> shCoeffs)
        {
            if (isPLY(filePath))
            {
                PLYFileToCompactSplats(filePath, shCoeffCount, out splats, out shCoeffs);
                LinearizeData(splats);
                return;
            }
            if (isSPZ(filePath))
            {
                SPZFileReader.ReadFile(filePath, shCoeffCount, out splats, out shCoeffs);
                return;
            }
            throw new IOException($"File {filePath} is not a supported format");
        }

        static bool isPLY(string filePath) => filePath.EndsWith(".ply", true, CultureInfo.InvariantCulture);
        static bool isSPZ(string filePath) => filePath.EndsWith(".spz", true, CultureInfo.InvariantCulture);

        static string CheckPLYAttributes(List<(string, PLYFileReader.ElementType)> attributes)
        {
            string[] required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };
            List<string> missing = required.Where(req => !attributes.Contains((req, PLYFileReader.ElementType.Float))).ToList();
            if (missing.Count == 0)
                return null;
            return string.Join(",", missing);
        }

        static void PLYFileToCompactSplats(string filePath, int shCoeffCount, out NativeArray<ImportSplatData> splats, out NativeArray<Vector3> shCoeffs)
        {
            using var fs = PLYFileReader.OpenDataStream(filePath, out var count, out var stride, out var attributes);

            string attrError = CheckPLYAttributes(attributes);
            if (!string.IsNullOrEmpty(attrError))
                throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {attrError}");

            Dictionary<string, int> floatAttributeOffsets = BuildFloatAttributeOffsets(attributes);
            int[] srcOffsets = BuildSplatAttributeOffsets(floatAttributeOffsets);
            int[] shSrcOffsets = BuildSHAttributeOffsets(floatAttributeOffsets, shCoeffCount);

            splats = new NativeArray<ImportSplatData>(count, Allocator.Persistent);
            shCoeffs = shCoeffCount > 0 ? new NativeArray<Vector3>(count * shCoeffCount, Allocator.Persistent, NativeArrayOptions.ClearMemory) : default;

            StreamPLYData(fs, filePath, count, stride, srcOffsets, splats, shCoeffCount, shSrcOffsets, shCoeffs);
        }

        static Dictionary<string, int> BuildFloatAttributeOffsets(List<(string, PLYFileReader.ElementType)> attributes)
        {
            var result = new Dictionary<string, int>(attributes.Count);
            int offset = 0;
            for (int ai = 0; ai < attributes.Count; ai++)
            {
                var attr = attributes[ai];
                if (attr.Item2 == PLYFileReader.ElementType.Float)
                    result[attr.Item1] = offset;
                offset += PLYFileReader.TypeToSize(attr.Item2);
            }

            return result;
        }

        static int[] BuildSplatAttributeOffsets(Dictionary<string, int> floatAttributeOffsets)
        {
            string[] splatAttributes =
            {
                "x",
                "y",
                "z",
                "f_dc_0",
                "f_dc_1",
                "f_dc_2",
                "opacity",
                "scale_0",
                "scale_1",
                "scale_2",
                "rot_0",
                "rot_1",
                "rot_2",
                "rot_3",                
            };
            int[] srcOffsets = new int[splatAttributes.Length];
            for (int ai = 0; ai < splatAttributes.Length; ai++)
                srcOffsets[ai] = floatAttributeOffsets.TryGetValue(splatAttributes[ai], out int attrOffset) ? attrOffset : -1;

            if (UnsafeUtility.SizeOf<ImportSplatData>() / 4 != srcOffsets.Length)
                throw new InvalidOperationException("ImportSplatData layout no longer matches the expected PLY float attribute count");

            return srcOffsets;
        }

        static int[] BuildSHAttributeOffsets(Dictionary<string, int> floatAttributeOffsets, int shCoeffCount)
        {
            int[] shSrcOffsets = new int[shCoeffCount * 3];
            for (int coeff = 0; coeff < shCoeffCount; coeff++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    string attrName = $"f_rest_{coeff + (channel * 15)}";
                    shSrcOffsets[coeff * 3 + channel] = floatAttributeOffsets.TryGetValue(attrName, out int attrOffset) ? attrOffset : -1;
                }
            }

            return shSrcOffsets;
        }

        static unsafe void StreamPLYData(FileStream fs, string filePath, int splatCount, int srcStride, int[] srcOffsets, NativeArray<ImportSplatData> splats, int shCoeffCount, int[] shSrcOffsets, NativeArray<Vector3> shCoeffs)
        {
            const int kMaxChunkBytes = 64 * 1024 * 1024;
            int rowsPerChunk = Math.Max(1, kMaxChunkBytes / Math.Max(1, srcStride));
            byte[] chunk = new byte[rowsPerChunk * srcStride];
            int dstStride = UnsafeUtility.SizeOf<ImportSplatData>();
            byte* dstBase = (byte*)splats.GetUnsafePtr();
            float3* shDstBase = shCoeffCount > 0 ? (float3*)shCoeffs.GetUnsafePtr() : null;

            int processed = 0;
            while (processed < splatCount)
            {
                int rowsThisChunk = Math.Min(rowsPerChunk, splatCount - processed);
                int bytesToRead = rowsThisChunk * srcStride;
                ReadExact(fs, chunk, bytesToRead, filePath);

                fixed (byte* srcBase = chunk)
                fixed (int* srcOffsetsPtr = srcOffsets)
                fixed (int* shSrcOffsetsPtr = shSrcOffsets)
                {
                    ReorderPLYChunk(rowsThisChunk, srcBase, srcStride, dstBase + (processed * dstStride), dstStride, srcOffsetsPtr, shDstBase == null ? null : shDstBase + (processed * shCoeffCount), shCoeffCount, shSrcOffsetsPtr);
                }

                processed += rowsThisChunk;
            }
        }

        static void ReadExact(FileStream fs, byte[] buffer, int bytesToRead, string filePath)
        {
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = fs.Read(buffer, totalRead, bytesToRead - totalRead);
                if (read <= 0)
                    throw new IOException($"PLY {filePath} read error, expected {bytesToRead} data bytes got {totalRead}");
                totalRead += read;
            }
        }

        static unsafe void ReorderPLYChunk(int splatCount, byte* src, int srcStride, byte* dst, int dstStride, int* srcOffsets, float3* shDst, int shCoeffCount, int* shSrcOffsets)
        {
            for (int i = 0; i < splatCount; i++)
            {
                byte* rowSrc = src + (i * srcStride);
                byte* rowDst = dst + (i * dstStride);
                for (int attr = 0; attr < dstStride / 4; attr++)
                {
                    if (srcOffsets[attr] >= 0)
                        *(int*)(rowDst + attr * 4) = *(int*)(rowSrc + srcOffsets[attr]);
                    else
                        *(int*)(rowDst + attr * 4) = 0;
                }

                for (int coeff = 0; coeff < shCoeffCount; coeff++)
                {
                    float3 sh = 0f;
                    int baseOffset = coeff * 3;
                    if (shSrcOffsets[baseOffset + 0] >= 0)
                        sh.x = *(float*)(rowSrc + shSrcOffsets[baseOffset + 0]);
                    if (shSrcOffsets[baseOffset + 1] >= 0)
                        sh.y = *(float*)(rowSrc + shSrcOffsets[baseOffset + 1]);
                    if (shSrcOffsets[baseOffset + 2] >= 0)
                        sh.z = *(float*)(rowSrc + shSrcOffsets[baseOffset + 2]);
                    shDst[i * shCoeffCount + coeff] = sh;
                }
            }
        }

        [BurstCompile]
        struct LinearizeDataJob : IJobParallelFor
        {
            public NativeArray<ImportSplatData> splatData;
            public void Execute(int index)
            {
                var splat = splatData[index];

                // rot
                var q = splat.rot;
                var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                // scale
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                // color
                splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
                splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

                splatData[index] = splat;
            }
        }

        static void LinearizeData(NativeArray<ImportSplatData> splatData)
        {
            LinearizeDataJob job = new LinearizeDataJob();
            job.splatData = splatData;
            job.Schedule(splatData.Length, 4096).Complete();
        }
    }
}
#endif
