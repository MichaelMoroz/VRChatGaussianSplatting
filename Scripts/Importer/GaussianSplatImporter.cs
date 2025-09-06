
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

using UdonSharpEditor;


namespace GaussianSplatting
{
    public struct UInt2 { public uint x, y; public UInt2(uint a, uint b){x=a;y=b;} }

    public struct UInt4 {
        public uint x, y, z, w;

        public UInt4(uint x, uint y, uint z, uint w)
        {
            this.x = x; this.y = y; this.z = z; this.w = w;
        }

        public uint this[int i]
        {
            readonly get => i switch { 0 => x, 1 => y, 2 => z, 3 => w, _ => 0u };
            set
            {
                switch (i)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    case 3: w = value; break;
                }
            }
        }
    }

    public struct Matrix3x3
    {
        // Row-major
        public float m00, m01, m02;
        public float m10, m11, m12;
        public float m20, m21, m22;

         public Matrix3x3(
            float m00, float m01, float m02,
            float m10, float m11, float m12,
            float m20, float m21, float m22)
        {
            this.m00 = m00; this.m01 = m01; this.m02 = m02;
            this.m10 = m10; this.m11 = m11; this.m12 = m12;
            this.m20 = m20; this.m21 = m21; this.m22 = m22;
        }

        public Vector3 GetRow(int r) => new Vector3(this[r, 0], this[r, 1], this[r, 2]);
        public void SetRow(int r, Vector3 v) { this[r, 0] = v.x; this[r, 1] = v.y; this[r, 2] = v.z; }

        public float this[int r, int c]
        {
            readonly get
            {
                if (r == 0) return c == 0 ? m00 : (c == 1 ? m01 : m02);
                if (r == 1) return c == 0 ? m10 : (c == 1 ? m11 : m12);
                return c == 0 ? m20 : (c == 1 ? m21 : m22);
            }
            set
            {
                if (r == 0) { if (c == 0) m00 = value; else if (c == 1) m01 = value; else m02 = value; }
                else if (r == 1) { if (c == 0) m10 = value; else if (c == 1) m11 = value; else m12 = value; }
                else { if (c == 0) m20 = value; else if (c == 1) m21 = value; else m22 = value; }
            }
        }

        public static Matrix3x3 operator *(Matrix3x3 a, float s) => new Matrix3x3
        {
            m00 = a.m00 * s, m01 = a.m01 * s, m02 = a.m02 * s,
            m10 = a.m10 * s, m11 = a.m11 * s, m12 = a.m12 * s,
            m20 = a.m20 * s, m21 = a.m21 * s, m22 = a.m22 * s,
        };

        public static Matrix3x3 operator *(float s, Matrix3x3 a) => a * s;

        public Matrix3x3 Abs() => new Matrix3x3
        {
            m00 = Mathf.Abs(m00), m01 = Mathf.Abs(m01), m02 = Mathf.Abs(m02),
            m10 = Mathf.Abs(m10), m11 = Mathf.Abs(m11), m12 = Mathf.Abs(m12),
            m20 = Mathf.Abs(m20), m21 = Mathf.Abs(m21), m22 = Mathf.Abs(m22),
        };
    }


    public static class GaussianCodec
    {
        // ---- Helpers ----

        // rows → lower-triangular
        public static Matrix3x3 Triangularize3x3_L(Matrix3x3 M)
        {
            const float eps = 1e-8f;

            Vector3 r0 = M.GetRow(0);
            float l00 = r0.magnitude;                 if (l00 < eps) return default;
            Vector3 q0 = r0 / l00;

            Vector3 r1 = M.GetRow(1);
            float l10 = Vector3.Dot(r1, q0);
            Vector3 v1 = r1 - l10 * q0;
            float l11 = v1.magnitude;                 if (l11 < eps) return default;
            Vector3 q1 = v1 / l11;

            Vector3 r2 = M.GetRow(2);
            float l20 = Vector3.Dot(r2, q0);
            float l21 = Vector3.Dot(r2, q1);
            Vector3 v2 = r2 - l20 * q0 - l21 * q1;
            float l22 = v2.magnitude;                 if (l22 < eps) return default;

            return new Matrix3x3(
                l00, 0f,  0f,
                l10, l11, 0f,
                l20, l21, l22
            );
        }

        public static Matrix3x3 Scale(Vector3 s)
        {
            return new Matrix3x3(
                s.x, 0f, 0f,
                0f,  s.y, 0f,
                0f,  0f,  s.z
            );
        }

        public static Matrix3x3 RotationScale(Quaternion q, Vector3 s)
        {
            // R*S where S is diagonal: scale columns of R.
            Matrix3x3 R = Q2M(q);
            return new Matrix3x3(
                R.m00 * s.x, R.m01 * s.y, R.m02 * s.z,
                R.m10 * s.x, R.m11 * s.y, R.m12 * s.z,
                R.m20 * s.x, R.m21 * s.y, R.m22 * s.z
            );
        }

        public static Matrix3x3 CholeskyFromQS(Quaternion q, Vector3 sigma)
        {
            return Triangularize3x3_L(RotationScale(q, sigma));
        }

        // Quaternion → 3x3 rotation (row-major). Normalizes to avoid drift.
        static Matrix3x3 Q2M(Quaternion q)
        {
            q = q.normalized;
            float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
            float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
            float xw = q.x * q.w, yw = q.y * q.w, zw = q.z * q.w;

            return new Matrix3x3(
                1f - 2f * (yy + zz), 2f * (xy - zw),     2f * (xz + yw),
                2f * (xy + zw),       1f - 2f * (xx + zz), 2f * (yz - xw),
                2f * (xz - yw),       2f * (yz + xw),     1f - 2f * (xx + yy)
            );
        }

        static float Exp2(float x) => Mathf.Pow(2f, x);
        static float Log2(float x) => Mathf.Log(x, 2f);

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        static Vector4 Abs(Vector4 v) => new Vector4(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z), Mathf.Abs(v.w));

        static float MaxC(Vector3 v) => Mathf.Max(Mathf.Max(v.x, v.y), v.z);
        static float MaxC(Vector4 v) => Mathf.Max(Mathf.Max(Mathf.Max(v.x, v.y), v.z), v.w);
        static float MaxC(Matrix3x3 m)
        {
            var r0 = new Vector3(m.m00, m.m01, m.m02);
            var r1 = new Vector3(m.m10, m.m11, m.m12);
            var r2 = new Vector3(m.m20, m.m21, m.m22);
            return Mathf.Max(Mathf.Max(MaxC(r0), MaxC(r1)), MaxC(r2));
        }

        // ---- Log mapping ----

        static float FromLog(float lg, float minv, float maxv)
        {
            if (lg == 0f) return 0f; 
            return Exp2(lg * (maxv - minv) + minv) / lg;
        }

        static float ToLog(float ex, float minv, float maxv)
        {
            if (ex == 0f) return 0f;
            return (Log2(ex) - minv) / (ex * (maxv - minv));
        }

        static float FromLogF1(float v, float minv, float maxv)
        {
            if (v == 0f) return v;
            float scale = FromLog(v, minv, maxv);
            return v * scale;
        }

        static float ToLogF1(float v, float minv, float maxv)
        {
            if (v == 0f) return v;
            float scale = ToLog(v, minv, maxv);
            return v * scale;
        }

        static Vector3 FromLogF3(Vector3 v, float minv, float maxv)
        {
            float max_v = MaxC(Abs(v));
            if (max_v == 0f) return v;
            float scale = FromLog(max_v, minv, maxv);
            return v * scale;
        }

        static Vector3 ToLogF3(Vector3 v, float minv, float maxv)
        {
            float max_v = MaxC(Abs(v));
            if (max_v == 0f) return v;
            float scale = ToLog(max_v, minv, maxv);
            return v * scale;
        }

        static Matrix3x3 FromLogF3x3(Matrix3x3 v, float minv, float maxv)
        {
            float max_v = MaxC(v.Abs());
            if (max_v == 0f) return v;
            float scale = FromLog(max_v, minv, maxv);
            return v * scale;
        }

        static Matrix3x3 ToLogF3x3(Matrix3x3 v, float minv, float maxv)
        {
            float max_v = MaxC(v.Abs());
            if (max_v == 0f) return v;
            float scale = ToLog(max_v, minv, maxv);
            return v * scale;
        }

        // ---- Quantize / Dequantize ----
        static uint Quantize(float v, float mn, float mx, uint bits)
        {
            float levels = (float)(1u << (int)bits);
            float t = Mathf.Clamp01((v - mn) / (mx - mn));
            return (uint)Mathf.Clamp(Mathf.RoundToInt(t * levels), 0.0f, levels - 1.0f);
        }

        static float Dequantize(uint q, float mn, float mx, uint bits)
        {
            float levels = (float)(1u << (int)bits);
            return ((float)q / levels) * (mx - mn) + mn;
        }

        // ---- Bit packing ----
        static void WriteDataAt(ref UInt4 info, ref int bitOffset, uint data, uint dataBits)
        {
            int wordIndex = bitOffset >> 5;
            int wordBit = bitOffset & 31;
            int dataBitsStart = (int)(32u - dataBits);
            int dataBitsOffset = dataBitsStart - wordBit;

            if (dataBitsOffset >= 0)
            {
                info[wordIndex] |= data << dataBitsOffset;
            }
            else
            {
                info[wordIndex] |= data >> (-dataBitsOffset);
                info[wordIndex + 1] |= data << (dataBitsOffset + 32);
            }
            bitOffset += (int)dataBits;
        }

        static uint ReadDataAt(UInt4 info, ref int bitOffset, uint dataBits)
        {
            int wordIndex = bitOffset >> 5;
            int wordBit = bitOffset & 31;
            int dataBitsStart = (int)(32u - dataBits);
            int dataBitsOffset = dataBitsStart - wordBit;

            uint data;
            if (dataBitsOffset >= 0)
            {
                data = info[wordIndex] >> dataBitsOffset;
            }
            else
            {
                data = info[wordIndex] << (-dataBitsOffset);
                data |= info[wordIndex + 1] >> (dataBitsOffset + 32);
            }
            bitOffset += (int)dataBits;
            return data & ((1u << (int)dataBits) - 1u);
        }

         // ---- Bit layout (sums to 128) ----
        public const uint X_POS_BITS = 21;
        public const uint Y_POS_BITS = 21;
        public const uint Z_POS_BITS = 21;
        public const uint XX_RS_BITS = 11;
        public const uint YY_RS_BITS = 11;
        public const uint ZZ_RS_BITS = 10;
        public const uint XY_RS_BITS = 11;
        public const uint XZ_RS_BITS = 11;
        public const uint YZ_RS_BITS = 11;

        public struct GaussianData
        {
            public Vector3   P;   // position
            public Matrix3x3 RS;  // Cholesky factor (rotation * scale)
            public Vector4   C;   // color.xyz, density.w (not packed here)
        }
        // Signed in [-1,1]
        static void WriteQuantizedSigned(ref UInt4 data, ref int bitOffset, float v, uint bits)
        {
            uint q = Quantize(v, -1f, 1f, bits);
            WriteDataAt(ref data, ref bitOffset, q, bits);
        }

        // Unsigned in [0,1]
        static void WriteQuantizedUnsigned(ref UInt4 data, ref int bitOffset, float v, uint bits)
        {
            uint q = Quantize(v, 0f, 1f, bits);
            WriteDataAt(ref data, ref bitOffset, q, bits);
        }

        static float ReadQuantizedSigned(UInt4 data, ref int bitOffset, uint bits)
        {
            uint q = ReadDataAt(data, ref bitOffset, bits);
            return Dequantize(q, -1f, 1f, bits);
        }

        static float ReadQuantizedUnsigned(UInt4 data, ref int bitOffset, uint bits)
        {
            uint q = ReadDataAt(data, ref bitOffset, bits);
            return Dequantize(q, 0f, 1f, bits);
        }

        static float SafeDist(Vector3 p) => MathF.Max(1e-8f, MathF.Sqrt(p.x * p.x + p.y * p.y + p.z * p.z));

        public static UInt4 PackGaussianData(GaussianData g, Vector4 ScalesLOG2)
        {
            var data = new UInt4(0, 0, 0, 0);
            int bitOffset = 0;

            float dist = SafeDist(g.P);
            Vector3   Plog2 = ToLogF3(g.P, ScalesLOG2.x, ScalesLOG2.y);
            Matrix3x3 RSlog2 = ToLogF3x3(g.RS * (1f / dist), ScalesLOG2.z, ScalesLOG2.w); // store RS relative to |P|

            WriteQuantizedSigned  (ref data, ref bitOffset, Plog2.x, X_POS_BITS);
            WriteQuantizedSigned  (ref data, ref bitOffset, Plog2.y, Y_POS_BITS);
            WriteQuantizedSigned  (ref data, ref bitOffset, Plog2.z, Z_POS_BITS);

            WriteQuantizedUnsigned(ref data, ref bitOffset, RSlog2[0, 0], XX_RS_BITS);
            WriteQuantizedUnsigned(ref data, ref bitOffset, RSlog2[1, 1], YY_RS_BITS);
            WriteQuantizedUnsigned(ref data, ref bitOffset, RSlog2[2, 2], ZZ_RS_BITS);

            WriteQuantizedSigned  (ref data, ref bitOffset, RSlog2[1, 0], XY_RS_BITS);
            WriteQuantizedSigned  (ref data, ref bitOffset, RSlog2[2, 0], XZ_RS_BITS);
            WriteQuantizedSigned  (ref data, ref bitOffset, RSlog2[2, 1], YZ_RS_BITS);

            return data;
        }

        public static GaussianData UnpackGaussianData(UInt4 data, Vector4 ScalesLOG2)
        {
            GaussianData g = default;
            int bitOffset = 0;

            g.P.x = ReadQuantizedSigned(data, ref bitOffset, X_POS_BITS);
            g.P.y = ReadQuantizedSigned(data, ref bitOffset, Y_POS_BITS);
            g.P.z = ReadQuantizedSigned(data, ref bitOffset, Z_POS_BITS);

            Matrix3x3 RS = default;
            RS[0, 0] = ReadQuantizedUnsigned(data, ref bitOffset, XX_RS_BITS);
            RS[1, 1] = ReadQuantizedUnsigned(data, ref bitOffset, YY_RS_BITS);
            RS[2, 2] = ReadQuantizedUnsigned(data, ref bitOffset, ZZ_RS_BITS);
            RS[1, 0] = ReadQuantizedSigned  (data, ref bitOffset, XY_RS_BITS);
            RS[2, 0] = ReadQuantizedSigned  (data, ref bitOffset, XZ_RS_BITS);
            RS[2, 1] = ReadQuantizedSigned  (data, ref bitOffset, YZ_RS_BITS);

            g.P  = FromLogF3 (g.P,  ScalesLOG2.x, ScalesLOG2.y);
            g.RS = FromLogF3x3(RS,   ScalesLOG2.z, ScalesLOG2.w);
            g.RS = g.RS * SafeDist(g.P); // restore RS scale using |P|

            g.C = default; // unchanged

            return g;
        }

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
                indices[0] = 0;
                indices[1] = 1; 
                indices[2] = 2; 
                mesh.SetIndices(indices, topologies[i], i, false, 0);
            }
            
            mesh.bounds = bbox;
            return mesh;
        }
    }

    /// <summary>
    /// Parses a Gaussian‑splat *.ply (or .spz) file and packs the attributes into five square
    /// textures ready for GPU upload. Only UnityEngine types are referenced so this class can
    /// also be used at runtime. Editor‑only helpers are wrapped in UNITY_EDITOR guards.
    /// </summary>
    public static class PlySplatImporter
    {
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

        public static GameObject CreatePrefab(
            List<Material> materials, Material mainMaterial, Mesh mesh,
            Texture2D packedPosTex, Texture2D packedColTex, RenderTexture packedPosTexRT0, 
            RenderTexture packedColTexRT0, RenderTexture packedPosTexRT1, RenderTexture packedColTexRT1, 
            string assetPath, string name,  bool addGaussianSplatObject = true
        ) {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = new Vector3(1, -1, 1); // flip Y to match unity's coordinate system
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials.ToArray();
            meshRenderer.allowOcclusionWhenDynamic = false;
            if (addGaussianSplatObject) {
                // Add the GaussianSplatObject component to the GameObject
                // This is necessary for the prefab to be recognized as a Gaussian Splat Object for the renderer
                GaussianSplatObject splatcomponent = go.AddUdonSharpComponent<GaussianSplatObject>();
                splatcomponent.mainMaterial = mainMaterial;
                splatcomponent.positionData = packedPosTex;
                splatcomponent.colorData = packedColTex;
                splatcomponent.positionBuffer0 = packedPosTexRT0;
                splatcomponent.colorBuffer0 = packedColTexRT0;
                splatcomponent.positionBuffer1 = packedPosTexRT1;
                splatcomponent.colorBuffer1 = packedColTexRT1;
            }
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, assetPath, InteractionMode.AutomatedAction);
            GameObject.DestroyImmediate(go); // clean up the temporary GameObject
            return prefab;
        }

        static float UIntToFloat(uint v) => BitConverter.Int32BitsToSingle((int)v);

        static Vector3 QuaternionToAxisAngle(Vector4 q)
        {
            Vector3 axis = new Vector3(q.x, q.y, q.z);
            float   len  = axis.magnitude;
            if (len < 1e-8f) return Vector3.zero;            // degenerates to 0‑angle
            axis /= len;
            float angle = Mathf.Atan2(len, q.w) * 2f;
            return axis * angle;
        }

        public static void Import(
            string plyFile, string prefabOutputPath, bool computeBoundingBox, 
            int splatsPerPass, bool precomputeSorting = false, int maxAlphaMaskCount = 1, 
            bool useSRGB = true, bool animated = false, int animatedSplatCount = 128 * 1024,
            int renderQueue = 3500
        ) {
            NativeArray<InputSplatData> splats;
            int count = 0; // number of splats in the file
            if(!animated) {
                if (!File.Exists(plyFile)) throw new FileNotFoundException(plyFile);

                // Read header to learn how many splats we need to allocate for.
                count = GaussianFileReader.ReadFileHeader(plyFile);
                if (count == 0)
                    throw new Exception("Empty or unsupported splat file");

                GaussianFileReader.ReadFile(plyFile, out splats);
            } else {
                //Fill with empty splats
                count = animatedSplatCount;
                splats = new NativeArray<InputSplatData>(count, Allocator.Temp);
                for (int i = 0; i < count; ++i)
                {
                    splats[i] = new InputSplatData
                    {
                        pos     = Vector3.zero,
                        dc0     = Vector3.zero,
                        rot     = Quaternion.identity,
                        scale   = Vector3.one,
                        opacity = 0f
                    };
                }
            }
           
            try
            {
                int side = Mathf.CeilToInt(Mathf.Sqrt(count));
                int effectiveCount = side * side; // round up to nearest square

                Debug.Log($"Importing {count} splats into {side}x{side} textures");

                // Pad splats to a square texture size with zero color zero size splats
                if (count < effectiveCount)
                {
                    NativeArray<InputSplatData> paddedSplats = new NativeArray<InputSplatData>(effectiveCount, Allocator.Temp);
                    for (int i = 0; i < count; ++i)
                    {
                        paddedSplats[i] = splats[i];
                    }
                    for (int i = count; i < effectiveCount; ++i)
                    {
                        paddedSplats[i] = new InputSplatData
                        {
                            pos     = Vector3.zero,
                            dc0     = Vector3.zero,
                            rot     = Quaternion.identity,
                            scale   = Vector3.one,
                            opacity = 0f
                        };
                    }
                    splats.Dispose();
                    splats = paddedSplats;
                }

                InputSplatData[] data = splats.ToArray();          // managed copy – easier to sort
                int n = data.Length;

                // Compute BBOX
                Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < n; ++i)
                {
                    min = Vector3.Min(min, data[i].pos);
                    max = Vector3.Max(max, data[i].pos);
                }
                Vector3 size = max - min;

                if (size.x == 0) size.x = 1e-6f;
                if (size.y == 0) size.y = 1e-6f;
                if (size.z == 0) size.z = 1e-6f;

                Bounds bbox = new Bounds();
                bbox.center = Vector3.zero;
                bbox.extents = new Vector3(1000, 1000, 1000);
                if(!animated) {
                     // Prepare Morton keys
                    var keys = new uint[n];
                    Vector3 centerOfMass = Vector3.zero;
                    int validCount = 0;
                    for (int i = 0; i < n; ++i)
                    {
                        Vector3 pos = data[i].pos;
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
                        Vector3 pos = data[i].pos;
                        if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z))
                            continue; // skip invalid splats
                        Vector3 relativePos = pos - centerOfMass;
                        maxSize.x = Mathf.Max(maxSize.x, Mathf.Abs(relativePos.x));
                        maxSize.y = Mathf.Max(maxSize.y, Mathf.Abs(relativePos.y));
                        maxSize.z = Mathf.Max(maxSize.z, Mathf.Abs(relativePos.z));
                    }

                    // Sort splats by Morton key – in-place for data[]
                    Array.Sort(keys, data);

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
                        Array.Sort(sortedIndices[i], (a, b) => Vector3.Dot(data[a].pos, dir).CompareTo(Vector3.Dot(data[b].pos, dir)));
                    }
                    
                    sortedTex = NewTextureArray(side, octahedral_dirs.Length, TextureFormat.RFloat, "SortedOctahedralDirections");
                    for (int i = 0; i < octahedral_dirs.Length; ++i)
                    {
                        Color[] sortedPixels = new Color[side * side];
                        for (int j = 0; j < n; ++j)
                        {
                            sortedPixels[j] = new Color(sortedIndices[i][j], 0f, 0f, 0f); // Store only the index in the red channel
                        }
                        sortedTex.SetPixels(sortedPixels, i);
                    }
                    sortedTex.Apply(false, true);
                    SaveTextureAsset(sortedTex, outputDataFolder, materialName + "_sorted_oct_dirs");
                }

                Shader shader = null;
                if(useSRGB) {
                    shader = Shader.Find("VRChatGaussianSplatting/GaussianSplatting");
                } else {
                    shader = Shader.Find("VRChatGaussianSplatting/GaussianSplattingSimpleBackToFront");
                }

                Texture2D packedPosTex = null;
                Texture2D packedColTex = null;
                RenderTexture packedPosTexRT0 = null;
                RenderTexture packedColTexRT0 = null;
                RenderTexture packedPosTexRT1 = null;
                RenderTexture packedColTexRT1 = null;
                
                //TODO estimate scales from splat data
                Vector4 scalesLOG2 = new Vector4( -15, 15, -16, 4 );
                if (!animated) {
                    packedPosTex = NewTexture(side, TextureFormat.RGBAFloat, "PackedPositions");
                    uint[]  posRaw = new uint[data.Length * 4];

                    for (int i = 0; i < data.Length; ++i)
                    {
                        var s = data[i];

                        Matrix3x3 cholesky = GaussianCodec.CholeskyFromQS(s.rot, s.scale);
                        var gData = new GaussianCodec.GaussianData
                        {
                            P  = s.pos,
                            RS = cholesky,
                            C  = new Vector4(s.dc0.x, s.dc0.y, s.dc0.z, s.opacity)
                        };

                        UInt4 packed = GaussianCodec.PackGaussianData(gData, scalesLOG2);

                        int o = i << 2;
                        posRaw[o + 0] = packed.x;
                        posRaw[o + 1] = packed.y;
                        posRaw[o + 2] = packed.z;
                        posRaw[o + 3] = packed.w;
                    }

                    packedPosTex.SetPixelData(posRaw, 0);
                    packedPosTex.Apply(false, true);
                    
                    packedColTex = NewTexture(side, TextureFormat.RGBA32,  "PackedColors");
                    Color[] colPixels = new Color[side * side];
                    for (int i = 0; i < data.Length; ++i)
                    {
                        var s = data[i];
                        colPixels[i] = new Color(s.dc0.x, s.dc0.y, s.dc0.z, s.opacity);
                    }

                    packedColTex.SetPixels(colPixels);
                    packedColTex.Apply(false, true);

                    SaveTextureAsset(packedPosTex, outputDataFolder, materialName + "_packed_positions");
                    SaveTextureAsset(packedColTex, outputDataFolder, materialName + "_packed_colors");
                } else {
                    packedPosTexRT0 = NewRenderTexture(side, RenderTextureFormat.ARGBFloat, "PackedPositions");
                    packedColTexRT0 = NewRenderTexture(side, RenderTextureFormat.ARGBHalf, "PackedColors");
                    packedPosTexRT0.Create();
                    packedColTexRT0.Create();
                    packedPosTexRT1 = NewRenderTexture(side, RenderTextureFormat.ARGBFloat, "PackedPositions");
                    packedColTexRT1 = NewRenderTexture(side, RenderTextureFormat.ARGBHalf, "PackedColors");
                    packedPosTexRT1.Create();
                    packedColTexRT1.Create();
                    
                    SaveTextureAsset(packedPosTexRT0, outputDataFolder, materialName + "_packed_positions_rt0");
                    SaveTextureAsset(packedColTexRT0, outputDataFolder, materialName + "_packed_colors_rt0");
                    SaveTextureAsset(packedPosTexRT1, outputDataFolder, materialName + "_packed_positions_rt1");
                    SaveTextureAsset(packedColTexRT1, outputDataFolder, materialName + "_packed_colors_rt1");
                }
               
                
                if(splatsPerPass == 0) splatsPerPass = effectiveCount;
                splatsPerPass = Mathf.Min(splatsPerPass, effectiveCount);
     
                List<Material> materials = new List<Material>();
                List<int> indexCounts = new List<int>();
                List<MeshTopology> topologies = new List<MeshTopology>();

                int totalPassCount = (effectiveCount + splatsPerPass - 1) / splatsPerPass; // number of passes needed
                int alphaMaskCount = Mathf.Min(maxAlphaMaskCount, totalPassCount - 1); // number of alpha mask passes needed
                //update splats per pass to make equal chunks
                splatsPerPass = (effectiveCount + totalPassCount - 1) / totalPassCount;

                if(useSRGB) {
                    //Convert screen colors to sRGB
                    indexCounts.Add(3);
                    topologies.Add(MeshTopology.Triangles); // main mesh will be rendered as triangles
                    Material convertToSRGB = new Material(Shader.Find("VRChatGaussianSplatting/ToSRGB"));
                    convertToSRGB.name = "convert_to_srgb";
                    materials.Add(convertToSRGB);
                } else {
                    splatsPerPass = effectiveCount;
                }
              
                Material mainMat = null;
                for (int i = 0; i < effectiveCount; i += splatsPerPass)
                {
                    int passCount = Mathf.Min(splatsPerPass, effectiveCount - i);
                    int pass = i / splatsPerPass;
                    Material splatMat = null;
                    string splatMatName = materialName + (pass > 0 ? $"_pass_{pass}" : "_main") + "_splat";
                    if(pass == 0) {
                        splatMat = new Material(shader);
                        splatMat.name = splatMatName;
                        if (!animated) {
                            splatMat.SetTexture("_GS_PackedPositions", packedPosTex);
                            splatMat.SetTexture("_GS_PackedColors", packedColTex);
                        } else {
                            splatMat.SetTexture("_GS_PackedPositions", packedPosTexRT0);
                            splatMat.SetTexture("_GS_PackedColors", packedColTexRT0);
                        }
                        splatMat.SetInt("_ActualSplatCount", n);
                        splatMat.SetInt("_ActualSplatCountSqrt", side);
                        splatMat.SetVector("_GS_ScalesLOG2", scalesLOG2);
                        mainMat = splatMat;
                        if(!useSRGB) {
                            splatMat.SetInteger("_FAKE_SRGB", 1);
                            splatMat.EnableKeyword("_FAKE_SRGB");
                            splatMat.EnableKeyword("_FAKE_SRGB_ON");
                        }
                        if(precomputeSorting)
                        {
                            splatMat.SetTexture("_GS_RenderOrderPrecomputed", sortedTex);
                            splatMat.SetInteger("_PRECOMPUTED_SORTING", 1);
                            splatMat.EnableKeyword("_PRECOMPUTED_SORTING");
                            splatMat.EnableKeyword("_PRECOMPUTED_SORTING_ON");
                        }
                    } else {
                        splatMat = new Material(mainMat); // make a material variant
                        splatMat.parent = mainMat;
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
                    splatMat.renderQueue = renderQueue + i;
                    string matPath = Path.Combine(outputDataFolder + "/materials", splatMat.name + ".mat");
                    AssetDatabase.CreateAsset(splatMat, matPath);
                }

                Mesh pointMesh = PointsMesh.GetMultiPassMesh(indexCounts, topologies, bbox);
                AssetDatabase.CreateAsset(pointMesh, Path.Combine(outputDataFolder, materialName + "_mesh.asset"));
                // Create prefab with the splat material and mesh
                GameObject prefab = CreatePrefab(materials, mainMat, pointMesh, 
                    packedPosTex, packedColTex, packedPosTexRT0, packedColTexRT0, packedPosTexRT1, packedColTexRT1,
                    prefabOutputPath, materialName, !precomputeSorting);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
            }
        }

        // ---------------------------------------------------------------------
        static Texture2D NewTexture(int size, TextureFormat format, string name)
        {
            var tex = new Texture2D(size, size, format, mipChain: false, linear: true)
            {
                name       = name,
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            return tex;
        }

        static Texture2DArray NewTextureArray(int size, int count, TextureFormat format, string name)
        {
            var tex = new Texture2DArray(size, size, count, format, mipChain: false, linear: true)
            {
                name       = name,
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            return tex;
        }

        static RenderTexture NewRenderTexture(int size, RenderTextureFormat format, string name)
        {
            var tex = new RenderTexture(size, size, 0, format)
            {
                name       = name,
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                useMipMap  = false,
                autoGenerateMips = false
            };
            return tex;
        }

        static void SaveTextureAsset(Texture2D tex, string folder, string name)
        {
            string path = Path.Combine(folder, $"{name}.asset");
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            AssetDatabase.CreateAsset(tex, path);
        }

        static void SaveTextureAsset(Texture2DArray tex, string folder, string name)
        {
            string path = Path.Combine(folder, $"{name}.asset");
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            AssetDatabase.CreateAsset(tex, path);
        }

        static void SaveTextureAsset(RenderTexture tex, string folder, string name)
        {
            string path = Path.Combine(folder, $"{name}.asset");
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            AssetDatabase.CreateAsset(tex, path);
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

        bool _animated = false; 
        int _animatedSplatCount = 512*512;
        int _renderQueue = 3500;
        Vector2 scrollPosition = Vector2.zero;
        [MenuItem("Gaussian Splatting/Import PLY Splats…")]
        static void Init()
        {
            GetWindow<PlyImportWizard>().Show();
        }

        void OnGUI()
        {
            _animated = EditorGUILayout.Toggle("Procedurally Animated", _animated);
            if (!_animated)
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
                EditorGUILayout.HelpBox("At the moment more than 8M splats or .PLY files larger than 2GB don't work. ", MessageType.Info);
            } else {
                _animatedSplatCount = EditorGUILayout.IntField("Splat Count", _animatedSplatCount);
            }
        
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Output Folder", EditorStyles.boldLabel);
            _outputFolder = EditorGUILayout.TextField(_outputFolder);
            if (GUILayout.Button("…", GUILayout.Width(30)))
                _outputFolder = EditorUtility.OpenFolderPanel("Select Output Folder", _outputFolder, "");

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Splat settings", EditorStyles.boldLabel);
            if (!_animated) {
                _computeBoundingBox = EditorGUILayout.Toggle("Compute Bounding Box", _computeBoundingBox);
            } else {
                _computeBoundingBox = false; // disable bounding box computation for animated splats
            }
            _renderQueue = EditorGUILayout.IntField("Base Render Queue", _renderQueue);
            _useSRGB = EditorGUILayout.Toggle("sRGB Color Correction", _useSRGB);
            EditorGUILayout.HelpBox("Color correction requires 2 additional grab passes, for small splats you might want to disable this. Without this enabled back to front rendering will be used, which makes multi-pass rendering not work. sRGB color correction only works correctly if the world has HDR camera render targets.", MessageType.Info);
            if(_useSRGB) {
                _multiPassRendering   = EditorGUILayout.Toggle("Multi-Pass Rendering", _multiPassRendering);
                if (_multiPassRendering)
                {
                    _splatsPerPass = EditorGUILayout.IntField("Splat Count Per Pass", _splatsPerPass);
                    EditorGUILayout.HelpBox("The rendering of the splat is split into multiple sequential chunks, can help with VR rendering performance.", MessageType.Info);
                    _splatsPerPass = Mathf.Clamp(_splatsPerPass, 16 * 1024, 8 * 1024 * 1024);
                    _maxAlphaMaskCount = EditorGUILayout.IntField("Max Alpha Mask Count", _maxAlphaMaskCount);
                    EditorGUILayout.HelpBox("After each chunk is rendered an optional alpha mask pass is added using a grab pass and stencil. This will occlude the following chunks if they are behind opaque objects. This can help performance, but grab pass can be expensive, so use it with care. If you have more than 4M splats you might want to have more than 1 alpha mask pass.", MessageType.Info);
                }
                else
                {
                    _splatsPerPass = 0; // disable multi-pass rendering
                }
            }

            if (!_animated) {
                _precomputeSorting = EditorGUILayout.Toggle("Precompute Sorting", _precomputeSorting);
                if (_precomputeSorting)
                {
                    EditorGUILayout.HelpBox("Precomputing sorting for octahedral directions, makes the gaussian splatting work standalone, without the GaussianSplatRenderer. However this takes way more texture memory and might have rendering artifacts. THIS WILL NO LONGER WORK WITH GaussianSplatRenderer", MessageType.Warning);
                }
            } else {
                _precomputeSorting = false; // disable precompute sorting for animated splats
            }
            
            GUILayout.FlexibleSpace();

            if(!_animated) {
                if (GUILayout.Button("Import All PLYs"))
                {
                    if (!_plyPaths.Any(p => !string.IsNullOrEmpty(p)))
                    {
                        EditorUtility.DisplayDialog("PLY Import", "Add at least one PLY path.", "OK");
                        return;
                    }

                    foreach (string ply in _plyPaths.Where(p => !string.IsNullOrEmpty(p)))
                    {
                        string plyName = Path.GetFileNameWithoutExtension(ply);
                        ImportSingle(ply, plyName);
                    }
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    EditorUtility.DisplayDialog("PLY Import", "All imports completed.", "OK");
                }
            } else {
                if (GUILayout.Button("Generate Animated Splat Prefab"))
                {
                    string plyName = "procedural_splat_" + _animatedSplatCount;
                    ImportSingle("", plyName);
                }
            }
        }

        void ImportSingle(string plyPath, string name)
        {
            string prefabName = name + ".prefab";
            string relFolder  = FileUtil.GetProjectRelativePath(_outputFolder);
            if (string.IsNullOrEmpty(relFolder))
                relFolder = "Assets";
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(relFolder, prefabName));
            try
            {
                EditorUtility.DisplayProgressBar("PLY Import",
                    $"Importing {Path.GetFileName(plyPath)}", 0f);
                PlySplatImporter.Import(plyPath, prefabPath, _computeBoundingBox, _splatsPerPass, _precomputeSorting, _maxAlphaMaskCount, _useSRGB, _animated, _animatedSplatCount, _renderQueue);
            }
            catch (Exception e)
            {
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