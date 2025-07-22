
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

namespace GaussianSplatting
{
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
                go.AddComponent<GaussianSplatObject>();
            }
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, assetPath, InteractionMode.AutomatedAction);
            GameObject.DestroyImmediate(go); // clean up the temporary GameObject
            return prefab;
        }

        public static void Import(string plyFile, string prefabOutputPath, bool computeBoundingBox, int splatsPerPass, bool precomputeSorting = false, int maxAlphaMaskCount = 1, bool useSRGB = true)
        {
            if (!File.Exists(plyFile))
                throw new FileNotFoundException(plyFile);

            // Read header to learn how many splats we need to allocate for.
            int count = GaussianFileReader.ReadFileHeader(plyFile);
            if (count == 0)
                throw new Exception("Empty or unsupported splat file");

            GaussianFileReader.ReadFile(plyFile, out NativeArray<InputSplatData> splats);
            try
            {
                int side = Mathf.CeilToInt(Mathf.Sqrt(count));
                int effectiveCount = side * side; // round up to nearest square

                Debug.Log($"Importing {count} splats into {side}x{side} textures");

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


                Texture2D xyzTex     = NewTexture(side, TextureFormat.RGBAFloat, "XYZ");
                Texture2D colDcTex   = NewTexture(side, TextureFormat.RGBA32, "ColorDC");
                Texture2D rotTex     = NewTexture(side, TextureFormat.RGBA32, "Rotation");
                Texture2D scaleTex   = NewTexture(side, TextureFormat.RGB9e5Float, "Scale");

                Shader shader = null;
                if(useSRGB) {
                    shader = Shader.Find("VRChatGaussianSplatting/GaussianSplatting");
                } else {
                    shader = Shader.Find("VRChatGaussianSplatting/GaussianSplattingSimpleBackToFront");
                }

                var xyzPixels   = new Color[side * side];
                var colPixels   = new Color[side * side];
                var rotPixels   = new Color[side * side];
                var scalePixels = new Color[side * side];

                for (int i = 0; i < data.Length; ++i) {
                    var s = data[i];                      
                    xyzPixels[i]   = new Color(s.pos.x,   s.pos.y,   s.pos.z,   0f);
                    colPixels[i]   = new Color(s.dc0.x,   s.dc0.y,   s.dc0.z,   s.opacity);
                    rotPixels[i]   = new Color(0.5f + 0.5f * s.rot.x, 
                                                0.5f + 0.5f * s.rot.y, 
                                                0.5f + 0.5f * s.rot.z, 
                                                0.5f + 0.5f * s.rot.w);
                    scalePixels[i] = new Color(s.scale.x, s.scale.y, s.scale.z, 0f);
                }

                xyzTex.SetPixels(xyzPixels);
                colDcTex.SetPixels(colPixels);
                rotTex.SetPixels(rotPixels);
                scaleTex.SetPixels(scalePixels);

                xyzTex.Apply(false, true);
                colDcTex.Apply(false, true);
                rotTex.Apply(false, true);
                scaleTex.Apply(false, true);



                SaveTextureAsset(xyzTex, outputDataFolder, materialName + "_xyz");
                SaveTextureAsset(colDcTex, outputDataFolder, materialName + "_color_dc");
                SaveTextureAsset(rotTex, outputDataFolder, materialName + "_rotation");
                SaveTextureAsset(scaleTex, outputDataFolder, materialName + "_scale");  
                
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
                        splatMat.SetTexture("_GS_Positions", xyzTex);
                        splatMat.SetTexture("_GS_Colors", colDcTex);
                        splatMat.SetTexture("_GS_Rotations", rotTex);
                        splatMat.SetTexture("_GS_Scales", scaleTex);
                        splatMat.SetInt("_ActualSplatCount", n);
                        mainMat = splatMat;
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

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Output Folder", EditorStyles.boldLabel);
            _outputFolder = EditorGUILayout.TextField(_outputFolder);
            if (GUILayout.Button("…", GUILayout.Width(30)))
                _outputFolder = EditorUtility.OpenFolderPanel("Select Output Folder", _outputFolder, "");

            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Splat settings", EditorStyles.boldLabel);
            _computeBoundingBox   = EditorGUILayout.Toggle("Compute Bounding Box", _computeBoundingBox);
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
                    string prefabPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(relFolder, prefabName));
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
                PlySplatImporter.Import(plyPath, prefabPath, _computeBoundingBox, _splatsPerPass, _precomputeSorting, _maxAlphaMaskCount, _useSRGB);
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