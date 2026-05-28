#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GaussianSplatting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatCombinedHierarchyBuilder
    {
        static readonly FieldInfo CombinedSortedRendererField = typeof(GaussianSplatRenderer).GetField("combinedSortedRenderer", BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool EnsureChunkHierarchy(GaussianSplatRenderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }

            MeshRenderer parentRenderer = CombinedSortedRendererField != null ? CombinedSortedRendererField.GetValue(renderer) as MeshRenderer : null;
            if (parentRenderer == null)
            {
                return false;
            }

            if (!renderer.IsCombinedRenderingMode())
            {
                if (parentRenderer.gameObject.activeSelf)
                {
                    Undo.RecordObject(parentRenderer.gameObject, "Disable Combined Gaussian Splat Renderer");
                    parentRenderer.gameObject.SetActive(false);
                    EditorUtility.SetDirty(parentRenderer.gameObject);
                    EditorUtility.SetDirty(renderer);
                }

                return false;
            }

            GameObject combinedObject = parentRenderer.gameObject;
            Material[] combinedMaterials = parentRenderer.sharedMaterials;
            if (combinedMaterials == null || combinedMaterials.Length == 0)
            {
                return false;
            }

            Transform combinedTransform = combinedObject.transform;
            if (combinedTransform.parent != null)
            {
                Undo.SetTransformParent(combinedTransform, null, "Reparent Combined Gaussian Splat Renderer");
            }

            if (combinedTransform.position != Vector3.zero || combinedTransform.rotation != Quaternion.identity || combinedTransform.localScale != Vector3.one)
            {
                Undo.RecordObject(combinedTransform, "Reset Combined Gaussian Splat Renderer Transform");
                combinedTransform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                combinedTransform.localScale = Vector3.one;
                EditorUtility.SetDirty(combinedTransform);
            }

            Bounds combinedBounds = GetCombinedBounds(combinedObject);
            string combinedFolderPath = GetCombinedResourceFolderPath(renderer.gameObject.scene);
            string assetPrefix = SanitizeAssetName(renderer.name);
            EnsureFolderExists(combinedFolderPath);

            List<Material> parentMaterials = new List<Material>();
            int cursor = 0;
            if (IsShader(combinedMaterials[0], "VRChatGaussianSplatting/ToSRGB"))
            {
                parentMaterials.Add(combinedMaterials[cursor]);
                cursor++;
            }

            int end = combinedMaterials.Length;
            if (end > cursor && IsShader(combinedMaterials[end - 1], "VRChatGaussianSplatting/ToLinear"))
            {
                end--;
                parentMaterials.Add(combinedMaterials[end]);
            }

            bool changed = false;
            Mesh parentMesh = CreateOrReplaceAsset(CreateMesh(parentMaterials.Count, 0, combinedBounds), combinedFolderPath + "/" + assetPrefix + "_CombinedConversionMesh.asset");
            MeshFilter parentFilter = parentRenderer.GetComponent<MeshFilter>();
            if (parentFilter != null && parentFilter.sharedMesh != parentMesh)
            {
                Undo.RecordObject(parentFilter, "Update Combined Conversion Mesh");
                parentFilter.sharedMesh = parentMesh;
                EditorUtility.SetDirty(parentFilter);
                changed = true;
            }

            Material[] parentMaterialArray = parentMaterials.ToArray();
            if (!MaterialArraysMatch(parentRenderer.sharedMaterials, parentMaterialArray))
            {
                Undo.RecordObject(parentRenderer, "Update Combined Conversion Materials");
                parentRenderer.sharedMaterials = parentMaterialArray;
                EditorUtility.SetDirty(parentRenderer);
                changed = true;
            }

            if (cursor >= end)
            {
                return changed;
            }

            int chunkCount = 0;
            while (cursor < end)
            {
                Material alphaMask = null;
                Material splatMaterial = combinedMaterials[cursor];
                if (IsShader(splatMaterial, "VRChatGaussianSplatting/AlphaDepthMask"))
                {
                    alphaMask = splatMaterial;
                    cursor++;
                    if (cursor >= end)
                    {
                        break;
                    }

                    splatMaterial = combinedMaterials[cursor];
                }

                cursor++;
                if (splatMaterial == null || !splatMaterial.HasProperty("_SplatCount"))
                {
                    continue;
                }

                string chunkName = "CombinedChunk" + chunkCount;
                GameObject chunkObject = FindImmediateChild(combinedObject, chunkName);
                if (chunkObject == null)
                {
                    chunkObject = new GameObject(chunkName);
                    Undo.RegisterCreatedObjectUndo(chunkObject, "Create Combined Gaussian Splat Chunk");
                    changed = true;
                }

                Transform chunkTransform = chunkObject.transform;
                if (chunkTransform.parent != combinedTransform)
                {
                    Undo.SetTransformParent(chunkTransform, combinedTransform, "Parent Combined Gaussian Splat Chunk");
                    changed = true;
                }

                if (chunkTransform.localPosition != Vector3.zero || chunkTransform.localRotation != Quaternion.identity || chunkTransform.localScale != Vector3.one)
                {
                    Undo.RecordObject(chunkTransform, "Reset Combined Gaussian Splat Chunk Transform");
                    chunkTransform.localPosition = Vector3.zero;
                    chunkTransform.localRotation = Quaternion.identity;
                    chunkTransform.localScale = Vector3.one;
                    EditorUtility.SetDirty(chunkTransform);
                    changed = true;
                }

                MeshFilter chunkFilter = chunkObject.GetComponent<MeshFilter>();
                if (chunkFilter == null)
                {
                    chunkFilter = Undo.AddComponent<MeshFilter>(chunkObject);
                    changed = true;
                }

                MeshRenderer chunkRenderer = chunkObject.GetComponent<MeshRenderer>();
                if (chunkRenderer == null)
                {
                    chunkRenderer = Undo.AddComponent<MeshRenderer>(chunkObject);
                    changed = true;
                }

                CopyRendererSettings(parentRenderer, chunkRenderer);

                int splatCount = Mathf.Max(0, splatMaterial.GetInt("_SplatCount"));
                string chunkMeshPath = combinedFolderPath + "/" + assetPrefix + (chunkCount > 0 ? $"_CombinedPass{chunkCount}" : "_CombinedMain") + "_Mesh.asset";
                Mesh chunkMesh = CreateOrReplaceAsset(CreateMesh(0, splatCount, combinedBounds, alphaMask != null), chunkMeshPath);
                if (chunkFilter.sharedMesh != chunkMesh)
                {
                    Undo.RecordObject(chunkFilter, "Update Combined Gaussian Splat Chunk Mesh");
                    chunkFilter.sharedMesh = chunkMesh;
                    EditorUtility.SetDirty(chunkFilter);
                    changed = true;
                }

                Material[] chunkMaterials = alphaMask != null ? new[] { alphaMask, splatMaterial } : new[] { splatMaterial };
                if (!MaterialArraysMatch(chunkRenderer.sharedMaterials, chunkMaterials))
                {
                    Undo.RecordObject(chunkRenderer, "Update Combined Gaussian Splat Chunk Materials");
                    chunkRenderer.sharedMaterials = chunkMaterials;
                    EditorUtility.SetDirty(chunkRenderer);
                    changed = true;
                }

                chunkCount++;
            }

            for (int childIndex = 0; childIndex < combinedTransform.childCount; childIndex++)
            {
                Transform child = combinedTransform.GetChild(childIndex);
                if (!child.name.StartsWith("CombinedChunk"))
                {
                    continue;
                }

                bool keepChunk = false;
                for (int activeChunkIndex = 0; activeChunkIndex < chunkCount; activeChunkIndex++)
                {
                    if (child.name == "CombinedChunk" + activeChunkIndex)
                    {
                        keepChunk = true;
                        break;
                    }
                }

                if (!keepChunk && child.gameObject.activeSelf)
                {
                    Undo.RecordObject(child.gameObject, "Disable Combined Gaussian Splat Chunk");
                    child.gameObject.SetActive(false);
                    EditorUtility.SetDirty(child.gameObject);
                    changed = true;
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(parentRenderer);
                EditorUtility.SetDirty(renderer);
            }

            return true;
        }

        static bool IsShader(Material material, string shaderName)
        {
            return material != null && material.shader != null && material.shader.name == shaderName;
        }

        static Bounds GetCombinedBounds(GameObject combinedObject)
        {
            MeshFilter filter = combinedObject != null ? combinedObject.GetComponent<MeshFilter>() : null;
            if (filter != null && filter.sharedMesh != null)
            {
                return filter.sharedMesh.bounds;
            }

            return new Bounds(Vector3.zero, Vector3.one * 1000.0f);
        }

        static void CopyRendererSettings(MeshRenderer source, MeshRenderer target)
        {
            if (source == null || target == null)
            {
                return;
            }

            target.shadowCastingMode = source.shadowCastingMode;
            target.receiveShadows = source.receiveShadows;
            target.lightProbeUsage = source.lightProbeUsage;
            target.reflectionProbeUsage = source.reflectionProbeUsage;
            target.motionVectorGenerationMode = source.motionVectorGenerationMode;
            target.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        }

        static GameObject FindImmediateChild(GameObject rootObject, string childName)
        {
            if (rootObject == null)
            {
                return null;
            }

            Transform child = rootObject.transform.Find(childName);
            return child != null ? child.gameObject : null;
        }

        static string GetCombinedResourceFolderPath(UnityEngine.SceneManagement.Scene scene)
        {
            string sceneName = scene.name;
            if (string.IsNullOrEmpty(sceneName) && !string.IsNullOrEmpty(scene.path))
            {
                sceneName = Path.GetFileNameWithoutExtension(scene.path);
            }

            return "Assets/Temp/GS_" + SanitizeAssetName(string.IsNullOrEmpty(sceneName) ? "UnsavedScene" : sceneName) + "/RTs/Combined";
        }

        static string SanitizeAssetName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "GaussianSplatRenderer";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] sanitizedChars = value.ToCharArray();
            for (int i = 0; i < sanitizedChars.Length; i++)
            {
                if (System.Array.IndexOf(invalidChars, sanitizedChars[i]) >= 0)
                {
                    sanitizedChars[i] = '_';
                }
            }

            return new string(sanitizedChars);
        }

        static void EnsureFolderExists(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string normalizedPath = folderPath.Replace('\\', '/');
            string[] parts = normalizedPath.Split('/');
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
        }

        static Mesh CreateMesh(int conversionPassCount, int splatCount, Bounds bounds, bool hasAlphaMask = false)
        {
            List<int> indexCounts = new List<int>();
            List<MeshTopology> topologies = new List<MeshTopology>();

            for (int i = 0; i < conversionPassCount; i++)
            {
                indexCounts.Add(3);
                topologies.Add(MeshTopology.Triangles);
            }

            if (splatCount > 0)
            {
                if (hasAlphaMask)
                {
                    indexCounts.Add(3);
                    topologies.Add(MeshTopology.Triangles);
                }

                indexCounts.Add((splatCount + 31) / 32);
                topologies.Add(MeshTopology.Points);
            }

            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[3];
            mesh.subMeshCount = indexCounts.Count;
            for (int subMeshIndex = 0; subMeshIndex < indexCounts.Count; subMeshIndex++)
            {
                int[] indices = new int[indexCounts[subMeshIndex]];
                if (indices.Length > 0) indices[0] = 0;
                if (indices.Length > 1) indices[1] = 1;
                if (indices.Length > 2) indices[2] = 2;
                mesh.SetIndices(indices, topologies[subMeshIndex], subMeshIndex, false, 0);
            }

            mesh.bounds = bounds;
            return mesh;
        }

        static T CreateOrReplaceAsset<T>(T asset, string path) where T : Object
        {
            if (asset == null)
            {
                return null;
            }

            string assetName = Path.GetFileNameWithoutExtension(path);
            asset.name = assetName;

            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(asset, existing);
                Object.DestroyImmediate(asset);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            Object existingMainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            if (existingMainAsset != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            AssetDatabase.CreateAsset(asset, path);
            T savedAsset = AssetDatabase.LoadAssetAtPath<T>(path) ?? asset;
            EditorUtility.SetDirty(savedAsset);
            return savedAsset;
        }

        static bool MaterialArraysMatch(Material[] left, Material[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
#endif