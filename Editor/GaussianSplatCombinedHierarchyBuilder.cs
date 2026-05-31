#if UNITY_EDITOR
using System.Collections.Generic;
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
            MeshFilter combinedFilter = combinedObject.GetComponent<MeshFilter>();
            Bounds combinedBounds = combinedFilter != null && combinedFilter.sharedMesh != null ? combinedFilter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 1000.0f);
            string combinedFolderPath = PlySplatImporter.GetSceneTempResourceFolderPath(renderer.gameObject.scene, "RTs/Combined");
            string assetPrefix = PlySplatImporter.SanitizeAssetName(renderer.name);
            PlySplatImporter.EnsureFolderExists(combinedFolderPath);
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
            Mesh parentMesh = PlySplatImporter.CreateOrReplaceAsset(CreateMesh(parentMaterials.Count, 0, combinedBounds), combinedFolderPath + "/" + assetPrefix + "_CombinedConversionMesh.asset");
            MeshFilter parentFilter = parentRenderer.GetComponent<MeshFilter>();
            if (parentFilter != null && parentFilter.sharedMesh != parentMesh)
            {
                Undo.RecordObject(parentFilter, "Update Combined Conversion Mesh");
                parentFilter.sharedMesh = parentMesh;
                EditorUtility.SetDirty(parentFilter);
                changed = true;
            }
            Material[] parentMaterialArray = parentMaterials.ToArray();
            if (!GaussianSplatRenderer.MaterialArraysMatch(parentRenderer.sharedMaterials, parentMaterialArray))
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
                Transform chunkChild = combinedTransform.Find(chunkName);
                GameObject chunkObject = chunkChild != null ? chunkChild.gameObject : null;
                if (chunkObject == null)
                {
                    chunkObject = new GameObject(chunkName);
                    chunkObject.hideFlags = HideFlags.NotEditable;
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
                chunkRenderer.shadowCastingMode = parentRenderer.shadowCastingMode;
                chunkRenderer.receiveShadows = parentRenderer.receiveShadows;
                chunkRenderer.lightProbeUsage = parentRenderer.lightProbeUsage;
                chunkRenderer.reflectionProbeUsage = parentRenderer.reflectionProbeUsage;
                chunkRenderer.motionVectorGenerationMode = parentRenderer.motionVectorGenerationMode;
                chunkRenderer.allowOcclusionWhenDynamic = parentRenderer.allowOcclusionWhenDynamic;
                int splatCount = Mathf.Max(0, splatMaterial.GetInt("_SplatCount"));
                string chunkMeshPath = combinedFolderPath + "/" + assetPrefix + (chunkCount > 0 ? $"_CombinedPass{chunkCount}" : "_CombinedMain") + "_Mesh.asset";
                Mesh chunkMesh = PlySplatImporter.CreateOrReplaceAsset(CreateMesh(0, splatCount, combinedBounds, alphaMask != null), chunkMeshPath);
                if (chunkFilter.sharedMesh != chunkMesh)
                {
                    Undo.RecordObject(chunkFilter, "Update Combined Gaussian Splat Chunk Mesh");
                    chunkFilter.sharedMesh = chunkMesh;
                    EditorUtility.SetDirty(chunkFilter);
                    changed = true;
                }
                Material[] chunkMaterials = alphaMask != null ? new[] { alphaMask, splatMaterial } : new[] { splatMaterial };
                if (!GaussianSplatRenderer.MaterialArraysMatch(chunkRenderer.sharedMaterials, chunkMaterials))
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
                if (!keepChunk)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                    changed = true;
                    childIndex--;
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
                PlySplatImporter.AppendMeshLayout(
                    indexCounts,
                    topologies,
                    new[] { new PlySplatImporter.PassInfo(0, 0, splatCount, hasAlphaMask) },
                    false);
            }
            return PlySplatImporter.CreateMultiPassMesh(indexCounts, topologies, bounds);
        }

    }
}
#endif
