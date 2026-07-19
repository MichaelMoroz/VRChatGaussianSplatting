#if UNITY_EDITOR
using System.Collections.Generic;
using GaussianSplatting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatCombinedHierarchyBuilder
    {
        public static bool EnsureChunkHierarchy(GaussianSplatRenderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }
            GaussianSplatCombiner combiner = renderer.GetCombiner();
            MeshRenderer parentRenderer = combiner != null ? combiner.GetCombinedSortedRenderer() : null;
            if (parentRenderer == null)
            {
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
            Bounds combinedBounds = HugeBounds();
            string combinedFolderPath = GaussianSplatImporter.GetSceneTempResourceFolderPath(renderer.gameObject.scene, "RTs/Combined");
            string assetPrefix = GaussianSplatImporter.SanitizeAssetName(renderer.name);
            GaussianSplatImporter.EnsureFolderExists(combinedFolderPath);
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
            Mesh parentMesh = GaussianSplatImporter.CreateOrReplaceAsset(CreateMesh(parentMaterials.Count, 0, combinedBounds), combinedFolderPath + "/" + assetPrefix + "_CombinedConversionMesh.asset");
            MeshFilter parentFilter = parentRenderer.GetComponent<MeshFilter>();
            if (parentFilter != null && (parentFilter.sharedMesh != parentMesh || !BoundsApproximatelyEqual(parentMesh.bounds, combinedBounds)))
            {
                Undo.RecordObject(parentFilter, "Update Combined Conversion Mesh");
                parentFilter.sharedMesh = parentMesh;
                EditorUtility.SetDirty(parentFilter);
                EditorUtility.SetDirty(parentMesh);
                changed = true;
            }
            changed = UpdateExistingCombinedMeshBounds(combinedTransform, combinedBounds, changed);
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
                changed = SetCombinedActive(combinedObject, true, changed);
                if (changed)
                {
                    EditorUtility.SetDirty(parentRenderer);
                    EditorUtility.SetDirty(renderer);
                }
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
                    chunkObject.hideFlags = HideFlags.None;
                    Undo.RegisterCreatedObjectUndo(chunkObject, "Create Combined Gaussian Splat Chunk");
                    changed = true;
                }
                else if (chunkObject.hideFlags != HideFlags.None)
                {
                    chunkObject.hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(chunkObject);
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
                // Shared geometric pass mesh (one per pass index, reused across every object/scene) instead of a
                // per-scene chunk mesh. The pass material's _SplatCount matches the pass mesh capacity.
                Mesh chunkMesh = GaussianSplatRTPool.LoadPassMesh(chunkCount);
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
            changed = SetCombinedActive(combinedObject, true, changed);
            if (changed)
            {
                EditorUtility.SetDirty(parentRenderer);
                EditorUtility.SetDirty(renderer);
            }
            return changed;
        }
        static bool UpdateExistingCombinedMeshBounds(Transform combinedTransform, Bounds combinedBounds, bool changed)
        {
            if (combinedTransform == null)
            {
                return changed;
            }

            MeshFilter parentFilter = combinedTransform.GetComponent<MeshFilter>();
            changed = UpdateMeshFilterBounds(parentFilter, combinedBounds, changed);
            for (int childIndex = 0; childIndex < combinedTransform.childCount; childIndex++)
            {
                Transform child = combinedTransform.GetChild(childIndex);
                if (child == null || !child.name.StartsWith("CombinedChunk"))
                {
                    continue;
                }
                changed = UpdateMeshFilterBounds(child.GetComponent<MeshFilter>(), combinedBounds, changed);
            }
            return changed;
        }

        static bool UpdateMeshFilterBounds(MeshFilter meshFilter, Bounds bounds, bool changed)
        {
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null || BoundsApproximatelyEqual(mesh.bounds, bounds))
            {
                return changed;
            }

            mesh.bounds = bounds;
            EditorUtility.SetDirty(mesh);
            if (meshFilter != null)
            {
                EditorUtility.SetDirty(meshFilter);
            }
            return true;
        }

        static bool SetCombinedActive(GameObject combinedObject, bool active, bool changed)
        {
            if (combinedObject != null && combinedObject.activeSelf != active)
            {
                Undo.RecordObject(combinedObject, "Toggle Combined Gaussian Splat Renderer");
                combinedObject.SetActive(active);
                EditorUtility.SetDirty(combinedObject);
                changed = true;
            }
            return changed;
        }
        static bool IsShader(Material material, string shaderName)
        {
            return material != null && material.shader != null && material.shader.name == shaderName;
        }
        static Bounds ComputeCombinedLocalBounds(UnityEngine.SceneManagement.Scene scene, Transform combinedTransform)
        {
            Bounds combinedBounds = new Bounds();
            bool hasBounds = false;
            bool forceHugeBounds = false;
            GaussianSplatObject[] lodObjects = FindSceneObjects<GaussianSplatObject>(scene);
            for (int i = 0; i < lodObjects.Length; i++)
            {
                GaussianSplatObject lodObject = lodObjects[i];
                if (lodObject == null || !lodObject.gameObject.activeInHierarchy || !lodObject.IsRenderable())
                {
                    continue;
                }
                if (lodObject.transform == combinedTransform || lodObject.transform.IsChildOf(combinedTransform))
                {
                    continue;
                }
                if (!lodObject.TryGetLocalBounds(out Bounds lodLocalBounds))
                {
                    forceHugeBounds = true;
                    continue;
                }
                EncapsulateTransformedBounds(lodObject.transform.localToWorldMatrix, lodLocalBounds, combinedTransform, ref combinedBounds, ref hasBounds);
            }

            if (forceHugeBounds || !hasBounds)
            {
                return HugeBounds();
            }
            Vector3 paddedSize = Vector3.Max(combinedBounds.size, Vector3.one * 0.001f);
            return new Bounds(combinedBounds.center, paddedSize + Vector3.one * 1.0f);
        }

        static T[] FindSceneObjects<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            T[] objects = Resources.FindObjectsOfTypeAll<T>();
            List<T> filtered = new List<T>();
            for (int i = 0; i < objects.Length; i++)
            {
                T obj = objects[i];
                if (obj == null || EditorUtility.IsPersistent(obj))
                {
                    continue;
                }
                GameObject root = obj.transform.root != null ? obj.transform.root.gameObject : obj.gameObject;
                if (root != null && root.scene == scene)
                {
                    filtered.Add(obj);
                }
            }
            return filtered.ToArray();
        }

        static Bounds HugeBounds()
        {
            return new Bounds(Vector3.zero, Vector3.one * 1000000.0f);
        }

        static void EncapsulateWorldBounds(Bounds worldBounds, Transform targetTransform, ref Bounds combinedBounds, ref bool hasBounds)
        {
            Matrix4x4 worldToTarget = targetTransform != null ? targetTransform.worldToLocalMatrix : Matrix4x4.identity;
            EncapsulateBounds(worldToTarget, worldBounds, ref combinedBounds, ref hasBounds);
        }

        static void EncapsulateTransformedBounds(Matrix4x4 localToWorld, Bounds localBounds, Transform targetTransform, ref Bounds combinedBounds, ref bool hasBounds)
        {
            Matrix4x4 worldToTarget = targetTransform != null ? targetTransform.worldToLocalMatrix : Matrix4x4.identity;
            EncapsulateBounds(worldToTarget * localToWorld, localBounds, ref combinedBounds, ref hasBounds);
        }

        static void EncapsulateBounds(Matrix4x4 matrix, Bounds sourceBounds, ref Bounds combinedBounds, ref bool hasBounds)
        {
            Vector3 center = sourceBounds.center;
            Vector3 extents = sourceBounds.extents;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                        Vector3 transformed = matrix.MultiplyPoint3x4(corner);
                        if (hasBounds)
                        {
                            combinedBounds.Encapsulate(transformed);
                        }
                        else
                        {
                            combinedBounds = new Bounds(transformed, Vector3.zero);
                            hasBounds = true;
                        }
                    }
                }
            }
        }

        static bool BoundsApproximatelyEqual(Bounds left, Bounds right)
        {
            const float epsilon = 0.001f;
            return (left.center - right.center).sqrMagnitude <= epsilon * epsilon
                && (left.size - right.size).sqrMagnitude <= epsilon * epsilon;
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
                GaussianSplatImporter.AppendMeshLayout(
                    indexCounts,
                    topologies,
                    new[] { new GaussianSplatImporter.PassInfo(0, 0, splatCount, hasAlphaMask) },
                    false);
            }
            return GaussianSplatImporter.CreateMultiPassMesh(indexCounts, topologies, bounds);
        }

    }
}
#endif
