#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UdonSharpEditor;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatAndroidBuildProcessor : IProcessSceneWithReport
    {
        const string GeomShaderName = "VRChatGaussianSplatting/GaussianSplatting";
        const string NoGeomShaderName = "VRChatGaussianSplatting/GaussianSplattingNoGeom";
        const string FakeSrgbNoGeomShaderName = "VRChatGaussianSplatting/GaussianSplattingNoGeomSimpleBackToFront";
        const string ToSrgbShaderName = "VRChatGaussianSplatting/ToSRGB";
        const string ToLinearShaderName = "VRChatGaussianSplatting/ToLinear";
        const string AlphaDepthMaskShaderName = "VRChatGaussianSplatting/AlphaDepthMask";

        public int callbackOrder => 1000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            BuildTarget buildTarget = report != null ? report.summary.platform : EditorUserBuildSettings.activeBuildTarget;
            if (buildTarget != BuildTarget.Android)
            {
                return;
            }

            Shader fallbackShader = Shader.Find(FakeSrgbNoGeomShaderName);
            if (fallbackShader == null)
            {
                Debug.LogWarning("Gaussian splat Android build conversion skipped: shader '" + FakeSrgbNoGeomShaderName + "' was not found.");
                return;
            }
            if (HasGaussianSplats(scene))
            {
                GaussianSplatRenderer renderer = GaussianSplatRenderer.EnsureSceneRendererExists(scene);
                ApplyAndroidLowQuality(scene);
            }

            int convertedCount = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                MeshRenderer[] renderers = roots[i].GetComponentsInChildren<MeshRenderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    if (ConvertRenderer(renderers[rendererIndex], fallbackShader))
                    {
                        convertedCount++;
                    }
                }
            }

            if (convertedCount > 0)
            {
                Debug.Log("Converted " + convertedCount + " Gaussian splat renderer(s) to Android fake-sRGB no-geometry meshes in scene '" + scene.name + "'.");
            }
        }

        static void ApplyAndroidLowQuality(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GaussianSplatRenderer[] renderers = roots[i].GetComponentsInChildren<GaussianSplatRenderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    GaussianSplatRenderer renderer = renderers[rendererIndex];
                    renderer.SetQualityLow();
                    UdonSharpEditorUtility.CopyProxyToUdon(renderer);
                }
            }
        }

        static bool HasGaussianSplats(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].GetComponentInChildren<GaussianSplatObject>(true) != null)
                {
                    return true;
                }
            }
            return false;
        }

        static bool ConvertRenderer(MeshRenderer renderer, Shader fallbackShader)
        {
            if (renderer == null)
            {
                return false;
            }

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            Mesh sourceMesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (meshFilter == null || sourceMesh == null)
            {
                return false;
            }

            Material[] materials = renderer.sharedMaterials;
            List<SubmeshConversion> submeshes = new List<SubmeshConversion>(materials.Length);
            List<Material> convertedMaterials = new List<Material>(materials.Length);
            bool changed = false;
            int drawableCount = Mathf.Min(materials.Length, sourceMesh.subMeshCount);
            for (int i = 0; i < drawableCount; i++)
            {
                Material material = materials[i];
                string shaderName = GetShaderName(material);
                if (IsRemovedGrabPassShader(shaderName))
                {
                    changed = true;
                    continue;
                }

                bool convert = IsSplatShader(shaderName);
                Material outputMaterial = material;
                if (convert)
                {
                    changed = true;
                }
                if (convert && shaderName != FakeSrgbNoGeomShaderName)
                {
                    outputMaterial = new Material(material);
                    outputMaterial.shader = fallbackShader;
                    outputMaterial.name = material.name + "_AndroidFakeSRGB";
                    outputMaterial.renderQueue = material.renderQueue;
                }

                submeshes.Add(new SubmeshConversion(i, convertedMaterials.Count, convert));
                convertedMaterials.Add(outputMaterial);
            }

            if (!changed)
            {
                return false;
            }

            Mesh convertedMesh = CreateNoGeomMesh(sourceMesh, convertedMaterials, submeshes);
            renderer.sharedMaterials = convertedMaterials.ToArray();
            meshFilter.sharedMesh = convertedMesh;
            return true;
        }

        static Mesh CreateNoGeomMesh(Mesh sourceMesh, List<Material> materials, List<SubmeshConversion> submeshes)
        {
            int vertexCount = Mathf.Max(3, sourceMesh.vertexCount);
            for (int i = 0; i < submeshes.Count; i++)
            {
                if (!submeshes[i].convertToQuads)
                {
                    continue;
                }
                vertexCount = Mathf.Max(vertexCount, GetSubmeshSplatCount(sourceMesh, materials[submeshes[i].materialIndex], submeshes[i].sourceSubmesh) * 4);
            }

            Mesh mesh = new Mesh();
            mesh.name = sourceMesh.name + "_AndroidNoGeom";
            mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : sourceMesh.indexFormat;
            mesh.vertices = CreateQuadVertices(vertexCount);
            mesh.subMeshCount = submeshes.Count;

            for (int i = 0; i < submeshes.Count; i++)
            {
                SubmeshConversion submesh = submeshes[i];
                if (submesh.convertToQuads)
                {
                    mesh.SetIndices(CreateQuadIndices(GetSubmeshSplatCount(sourceMesh, materials[submesh.materialIndex], submesh.sourceSubmesh)), MeshTopology.Triangles, i, false, 0);
                }
                else
                {
                    mesh.SetIndices(sourceMesh.GetIndices(submesh.sourceSubmesh), sourceMesh.GetTopology(submesh.sourceSubmesh), i, false, 0);
                }
            }

            mesh.bounds = sourceMesh.bounds;
            return mesh;
        }

        static string GetShaderName(Material material)
        {
            return material != null && material.shader != null ? material.shader.name : string.Empty;
        }

        static bool IsSplatShader(string shaderName)
        {
            return shaderName == GeomShaderName || shaderName == NoGeomShaderName || shaderName == FakeSrgbNoGeomShaderName;
        }

        static bool IsRemovedGrabPassShader(string shaderName)
        {
            return shaderName == ToSrgbShaderName || shaderName == ToLinearShaderName || shaderName == AlphaDepthMaskShaderName;
        }

        static int GetSubmeshSplatCount(Mesh sourceMesh, Material material, int subMesh)
        {
            int materialSplatCount = material != null && material.HasProperty("_SplatCount") ? material.GetInt("_SplatCount") : 0;
            if (materialSplatCount > 0)
            {
                return materialSplatCount;
            }
            return sourceMesh != null && subMesh >= 0 && subMesh < sourceMesh.subMeshCount && sourceMesh.GetTopology(subMesh) == MeshTopology.Points
                ? (int)sourceMesh.GetIndexCount(subMesh) * 32
                : 0;
        }

        static Vector3[] CreateQuadVertices(int vertexCount)
        {
            Vector3[] vertices = new Vector3[vertexCount];
            return vertices;
        }

        static int[] CreateQuadIndices(int splatCount)
        {
            int[] indices = new int[splatCount * 6];
            for (int splatIndex = 0; splatIndex < splatCount; splatIndex++)
            {
                int vertex = splatIndex * 4;
                int index = splatIndex * 6;
                indices[index] = vertex;
                indices[index + 1] = vertex + 1;
                indices[index + 2] = vertex + 2;
                indices[index + 3] = vertex + 2;
                indices[index + 4] = vertex + 1;
                indices[index + 5] = vertex + 3;
            }
            return indices;
        }

        struct SubmeshConversion
        {
            public readonly int sourceSubmesh;
            public readonly int materialIndex;
            public readonly bool convertToQuads;

            public SubmeshConversion(int sourceSubmesh, int materialIndex, bool convertToQuads)
            {
                this.sourceSubmesh = sourceSubmesh;
                this.materialIndex = materialIndex;
                this.convertToQuads = convertToQuads;
            }
        }
    }
}
#endif
