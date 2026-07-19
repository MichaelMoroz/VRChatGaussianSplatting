#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatBuildStripper : IProcessSceneWithReport
    {
        // MUST run BEFORE UdonSharp's scene post-process (UdonSharpEditorManager.OnSceneBuild, a
        // [PostProcessScene] callback at order 0). When building a player, UdonSharp copies each
        // UdonSharpBehaviour proxy's fields into its backing UdonBehaviour and then DESTROYS the proxy
        // components. If we ran after it (e.g. order 900) the GaussianSplat* proxies would already be gone
        // (so we'd find no combiner/splats and strip nothing) AND the LOD source textures would already be
        // serialized into the shipped backing data. Running first means we null the LOD proxy fields + swap
        // the non-LOD materials, and UdonSharp's CopyProxyToUdon then bakes the stripped (null) values.
        public int callbackOrder => -100;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!BuildPipeline.isBuildingPlayer || report == null)
            {
                return;
            }

            List<GaussianSplatCombiner> combiners = GetCombinedModeCombiners(scene);
            if (combiners.Count == 0)
            {
                return;
            }

            int strippedLods = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                GaussianSplatObject[] lodObjects = roots[rootIndex].GetComponentsInChildren<GaussianSplatObject>(true);
                for (int lodIndex = 0; lodIndex < lodObjects.Length; lodIndex++)
                {
                    GaussianSplatObject lodObject = lodObjects[lodIndex];
                    GaussianSplatCombiner combiner = FindCombinerForLOD(combiners, lodObject);
                    if (combiner == null || IsInsideCombinedObject(lodObject.transform, combiner))
                    {
                        continue;
                    }
                    if (StripSourceLOD(lodObject))
                    {
                        strippedLods++;
                    }
                }
            }

            if (strippedLods > 0)
            {
                Debug.Log("Stripped source Gaussian splat resources from build scene '" + scene.name + "': "
                    + strippedLods + " object(s). Combined texture sets were preserved.");
            }
        }

        static List<GaussianSplatCombiner> GetCombinedModeCombiners(Scene scene)
        {
            List<GaussianSplatCombiner> combiners = new List<GaussianSplatCombiner>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                GaussianSplatRenderer[] renderers = roots[rootIndex].GetComponentsInChildren<GaussianSplatRenderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    GaussianSplatRenderer renderer = renderers[rendererIndex];
                    if (renderer == null)
                    {
                        continue;
                    }
                    GaussianSplatCombiner combiner = renderer.GetCombiner();
                    if (combiner == null || combiner.GetCombinedSortedRenderer() == null || combiners.Contains(combiner))
                    {
                        continue;
                    }
                    combiners.Add(combiner);
                }
            }
            return combiners;
        }

        static GaussianSplatCombiner FindCombinerForLOD(List<GaussianSplatCombiner> combiners, GaussianSplatObject lodObject)
        {
            if (lodObject == null)
            {
                return null;
            }
            for (int i = 0; i < combiners.Count; i++)
            {
                GaussianSplatCombiner combiner = combiners[i];
                if (combiner != null && combiner.ContainsFusedLODObject(lodObject.gameObject))
                {
                    return combiner;
                }
            }
            return null;
        }

        static bool IsInsideCombinedObject(Transform transform, GaussianSplatCombiner combiner)
        {
            GameObject combinedObject = combiner != null ? combiner.GetCombinedObject() : null;
            return transform != null && combinedObject != null && transform.IsChildOf(combinedObject.transform);
        }

        static bool StripSourceLOD(GaussianSplatObject lodObject)
        {
            if (lodObject == null)
            {
                return false;
            }

            lodObject.positions = null;
            lodObject.colors = null;
            lodObject.rotations = null;
            lodObject.scales = null;
            lodObject.sh = null;
            lodObject.chunkBoundsMinTexture = null;
            lodObject.chunkBoundsMaxTexture = null;
            lodObject.chunkRangeTexture = null;
            return true;
        }
    }
}
#endif
