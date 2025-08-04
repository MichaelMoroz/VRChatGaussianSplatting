
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
    public struct UInt4 { public uint x, y, z, w; public UInt4(uint a,uint b,uint c,uint d){x=a;y=b;z=c;w=d;} }

    public static class Float3Packing
    {
        /* ---------- helpers ---------- */
        private static int SignExtend(uint v, int bits)
        {
            int shift = 32 - bits;
            return ((int)v << shift) >> shift;
        }

        public static unsafe uint SingleToUInt32Bits(float value) {
            return *(uint*)(&value);
        }
        public static unsafe float UInt32BitsToSingle(uint value) {
            return *(float*)(&value);
        }

        private static float ScaleFromExponent(int e)
            => BitConverter.Int32BitsToSingle((127 + e) << 23);

        private static int GetExponentFromScale(float s)
            => ((BitConverter.SingleToInt32Bits(s) >> 23) & 0xFF) - 127;

        private static uint UX(int v, int bits)  => (uint)v & ((1u << bits) - 1);
        private static int  SX(uint v, int bits) => ((int)(v << (32 - bits))) >> (32 - bits);

        // -------------------- constants -----------------------------
        private const float M9  = 255f;        //  9-bit mantissa (F3U1)
        private const float M19 = 262143f;     // 19-bit mantissa (F3U2)

        // ============================================================
        // F3U1  (32-bit)
        // ============================================================
        public static uint PackF3U1(Vector3 v)
        {
            float maxv = Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

            int e;
            if (maxv == 0f) e = 0;
            else
            {
                int floorE = GetExponentFromScale(maxv);
                e = floorE + ((BitConverter.SingleToInt32Bits(maxv) & 0x007FFFFF) != 0 ? 1 : 0);
                e = Mathf.Clamp(e, -16, 15);
            }

            float scale = ScaleFromExponent(-e);

            int mxI = Mathf.RoundToInt(Mathf.Clamp(v.x * scale, -1f, 1f) * M9);
            int myI = Mathf.RoundToInt(Mathf.Clamp(v.y * scale, -1f, 1f) * M9);
            int mzI = Mathf.RoundToInt(Mathf.Clamp(v.z * scale, -1f, 1f) * M9);

            uint mx = UX(mxI, 9);
            uint my = UX(myI, 9);
            uint mz = UX(mzI, 9);
            uint eb = UX(e,   5);

            return  mx
                | (my << 9)
                | ((mz & 0x1Fu) << 18)
                | (eb << 23)
                | ((mz >> 5) << 28);
        }

        public static Vector3 UnpackF3U1(uint w)
        {
            if (w == 0u) return Vector3.zero;

            uint mxBits =  w        & 0x1FFu;
            uint myBits = (w >>  9) & 0x1FFu;
            uint mzBits = ((w >> 28) & 0xFu) << 5 | ((w >> 18) & 0x1Fu);
            uint ebBits = (w >> 23) & 0x1Fu;

            int  mx = SX(mxBits, 9);
            int  my = SX(myBits, 9);
            int  mz = SX(mzBits, 9);
            int  e  = SX(ebBits, 5);

            float scale = ScaleFromExponent(e);
            return new Vector3(mx, my, mz) / M9 * scale;
        }

        // ============================================================
        // F3U2  (64-bit → UInt2)
        // ============================================================
        public static UInt2 PackF3U2(Vector3 v)
        {
            float maxv = Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

            int e;
            if (maxv == 0f) e = 0;
            else
            {
                int floorE = GetExponentFromScale(maxv);
                e = floorE + ((BitConverter.SingleToInt32Bits(maxv) & 0x007FFFFF) != 0 ? 1 : 0);
                e = Mathf.Clamp(e, -64, 63);
            }

            float scale = ScaleFromExponent(-e);

            int mxI = Mathf.RoundToInt(Mathf.Clamp(v.x * scale, -1f, 1f) * M19);
            int myI = Mathf.RoundToInt(Mathf.Clamp(v.y * scale, -1f, 1f) * M19);
            int mzI = Mathf.RoundToInt(Mathf.Clamp(v.z * scale, -1f, 1f) * M19);

            uint mx = UX(mxI, 19);
            uint my = UX(myI, 19);
            uint mz = UX(mzI, 19);
            uint eb = UX(e,   7);

            uint lo =  mx | (eb << 19) | ((my & 0x3Fu) << 26);
            uint hi = (my >> 6) | (mz << 13);

            return new UInt2(lo, hi);
        }

        public static Vector3 UnpackF3U2(UInt2 d)
        {
            if (d.x == 0 && d.y == 0) return Vector3.zero;

            uint lo = d.x, hi = d.y;

            uint mxBits =  lo & 0x7FFFFu;
            uint myBits = ((lo >> 26) & 0x3Fu) | ((hi & 0x1FFFu) << 6);
            uint mzBits =  (hi >> 13) & 0x7FFFFu;
            uint ebBits = (lo >> 19) & 0x7Fu;

            int  mx = SX(mxBits, 19);
            int  my = SX(myBits, 19);
            int  mz = SX(mzBits, 19);
            int  e  = SX(ebBits, 7);

            float scale = ScaleFromExponent(e);
            return new Vector3(mx, my, mz) / M19 * scale;
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

        public static GameObject CreatePrefab(List<Material> materials, Mesh mesh, string assetPath, string name, bool addGaussianSplatObject = true)
        {
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
            bool useSRGB = true, bool animated = false, int animatedSplatCount = 128 * 1024
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
                RenderTexture packedPosTexRT = null;
                RenderTexture packedColTexRT = null;
                if (!animated) {
                    packedPosTex = NewTexture(side, TextureFormat.RGBAFloat, "PackedPositions");
                    packedColTex = NewTexture(side, TextureFormat.RGBA32,  "PackedColors");

                    Color[] posPixels = new Color[side * side];
                    Color[] colPixels = new Color[side * side];

                    for (int i = 0; i < data.Length; ++i)
                    {
                        var s     = data[i];
                        UInt2 pos  = Float3Packing.PackF3U2(s.pos);
                        uint  scl  = Float3Packing.PackF3U1(s.scale);
                        uint  rot  = Float3Packing.PackF3U1(QuaternionToAxisAngle(new Vector4(s.rot.x, s.rot.y, s.rot.z, s.rot.w)));

                        // bit‑preserving cast: uint → float
                        posPixels[i] = new Color
                        (
                            Float3Packing.UInt32BitsToSingle(pos.x),
                            Float3Packing.UInt32BitsToSingle(pos.y),
                            Float3Packing.UInt32BitsToSingle(scl),
                            Float3Packing.UInt32BitsToSingle(rot) 
                        );

                        colPixels[i] = new Color(s.dc0.x, s.dc0.y, s.dc0.z, s.opacity);
                    }

                    packedPosTex.SetPixels(posPixels);
                    packedColTex.SetPixels(colPixels);
                    packedPosTex.Apply(false, true);
                    packedColTex.Apply(false, true);

                    SaveTextureAsset(packedPosTex, outputDataFolder, materialName + "_packed_positions");
                    SaveTextureAsset(packedColTex, outputDataFolder, materialName + "_packed_colors");
                } else {
                    packedPosTexRT = NewRenderTexture(side, RenderTextureFormat.ARGBFloat, "PackedPositions");
                    packedColTexRT = NewRenderTexture(side, RenderTextureFormat.ARGB32, "PackedColors");

                    packedPosTexRT.Create();
                    packedColTexRT.Create();

                    SaveTextureAsset(packedPosTexRT, outputDataFolder, materialName + "_packed_positions");
                    SaveTextureAsset(packedColTexRT, outputDataFolder, materialName + "_packed_colors");
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
                            splatMat.SetTexture("_GS_PackedPositions", packedPosTexRT);
                            splatMat.SetTexture("_GS_PackedColors", packedColTexRT);
                        }
                        splatMat.SetInt("_ActualSplatCount", n);
                        splatMat.SetInt("_ActualSplatCountSqrt", side);
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
                    splatMat.renderQueue = 3500 + i;
                    string matPath = Path.Combine(outputDataFolder + "/materials", splatMat.name + ".mat");
                    AssetDatabase.CreateAsset(splatMat, matPath);
                }

                Mesh pointMesh = PointsMesh.GetMultiPassMesh(indexCounts, topologies, bbox);
                AssetDatabase.CreateAsset(pointMesh, Path.Combine(outputDataFolder, materialName + "_mesh.asset"));
                // Create prefab with the splat material and mesh
                GameObject prefab = CreatePrefab(materials, pointMesh, prefabOutputPath, materialName, !precomputeSorting);
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
            _useSRGB = EditorGUILayout.Toggle("sRGB Color Correction", _useSRGB);
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
                PlySplatImporter.Import(plyPath, prefabPath, _computeBoundingBox, _splatsPerPass, _precomputeSorting, _maxAlphaMaskCount, _useSRGB, _animated, _animatedSplatCount);
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