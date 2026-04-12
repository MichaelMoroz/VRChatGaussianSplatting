#if UNITY_EDITOR && !COMPILER_UDONSHARP
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
using UnityEngine.Assertions;

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
                NativeArray<byte> plyRawData = default;
                try
                {
                    List<(string, PLYFileReader.ElementType)> attributes;
                    PLYFileReader.ReadFile(filePath, out var splatCount, out var vertexStride, out attributes, out plyRawData);
                    string attrError = CheckPLYAttributes(attributes);
                    if (!string.IsNullOrEmpty(attrError))
                        throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {attrError}");
                    PLYDataToCompactSplats(plyRawData, splatCount, vertexStride, attributes, shCoeffCount, out splats, out shCoeffs);
                    LinearizeData(splats);
                    return;
                }
                finally
                {
                    if (plyRawData.IsCreated)
                        plyRawData.Dispose();
                }
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

        static unsafe void PLYDataToCompactSplats(NativeArray<byte> input, int count, int stride, List<(string, PLYFileReader.ElementType)> attributes, int shCoeffCount, out NativeArray<ImportSplatData> splats, out NativeArray<Vector3> shCoeffs)
        {
            NativeArray<int> fileAttrOffsets = new NativeArray<int>(attributes.Count, Allocator.Temp);
            int offset = 0;
            for (var ai = 0; ai < attributes.Count; ai++)
            {
                var attr = attributes[ai];
                fileAttrOffsets[ai] = offset;
                offset += PLYFileReader.TypeToSize(attr.Item2);
            }

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
            Assert.AreEqual(UnsafeUtility.SizeOf<ImportSplatData>() / 4, splatAttributes.Length);
            NativeArray<int> srcOffsets = new NativeArray<int>(splatAttributes.Length, Allocator.Temp);
            for (int ai = 0; ai < splatAttributes.Length; ai++)
            {
                int attrIndex = attributes.IndexOf((splatAttributes[ai], PLYFileReader.ElementType.Float));
                int attrOffset = attrIndex >= 0 ? fileAttrOffsets[attrIndex] : -1;
                srcOffsets[ai] = attrOffset;
            }

            NativeArray<int> shSrcOffsets = new NativeArray<int>(shCoeffCount * 3, Allocator.Temp);
            for (int coeff = 0; coeff < shCoeffCount; coeff++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    string attrName = $"f_rest_{coeff + (channel * 15)}";
                    int attrIndex = attributes.IndexOf((attrName, PLYFileReader.ElementType.Float));
                    shSrcOffsets[coeff * 3 + channel] = attrIndex >= 0 ? fileAttrOffsets[attrIndex] : -1;
                }
            }

            splats = new NativeArray<ImportSplatData>(count, Allocator.Persistent);
            shCoeffs = shCoeffCount > 0 ? new NativeArray<Vector3>(count * shCoeffCount, Allocator.Persistent, NativeArrayOptions.ClearMemory) : default;
            float3* shCoeffPtr = shCoeffCount > 0 ? (float3*)shCoeffs.GetUnsafePtr() : null;
            ReorderPLYData(count, (byte*)input.GetUnsafeReadOnlyPtr(), stride, (byte*)splats.GetUnsafePtr(), UnsafeUtility.SizeOf<ImportSplatData>(), (int*)srcOffsets.GetUnsafeReadOnlyPtr(), shCoeffPtr, shCoeffCount, (int*)shSrcOffsets.GetUnsafeReadOnlyPtr());

            fileAttrOffsets.Dispose();
            srcOffsets.Dispose();
            shSrcOffsets.Dispose();
        }

        [BurstCompile]
        static unsafe void ReorderPLYData(int splatCount, byte* src, int srcStride, byte* dst, int dstStride, int* srcOffsets, float3* shDst, int shCoeffCount, int* shSrcOffsets)
        {
            for (int i = 0; i < splatCount; i++)
            {
                for (int attr = 0; attr < dstStride / 4; attr++)
                {
                    if (srcOffsets[attr] >= 0)
                        *(int*)(dst + attr * 4) = *(int*)(src + srcOffsets[attr]);
                    else
                        *(int*)(dst + attr * 4) = 0;
                }

                for (int coeff = 0; coeff < shCoeffCount; coeff++)
                {
                    float3 sh = 0f;
                    int baseOffset = coeff * 3;
                    if (shSrcOffsets[baseOffset + 0] >= 0)
                        sh.x = *(float*)(src + shSrcOffsets[baseOffset + 0]);
                    if (shSrcOffsets[baseOffset + 1] >= 0)
                        sh.y = *(float*)(src + shSrcOffsets[baseOffset + 1]);
                    if (shSrcOffsets[baseOffset + 2] >= 0)
                        sh.z = *(float*)(src + shSrcOffsets[baseOffset + 2]);
                    shDst[i * shCoeffCount + coeff] = sh;
                }
                src += srcStride;
                dst += dstStride;
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
