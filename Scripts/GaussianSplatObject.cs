using UnityEngine;
using UdonSharp;

namespace GaussianSplatting
{
    public class GaussianSplatObject : UdonSharpBehaviour
    {
        [SerializeField] public GameObject sortedObject;
        [SerializeField] public MeshRenderer sortedRenderer;
        [SerializeField] int maxShBand = -1;

        GameObject ResolveChildObject(GameObject childObject, string childName)
        {
            if (childObject != null)
            {
                return childObject;
            }

            Transform child = transform.Find(childName);
            if (child != null)
            {
                return child.gameObject;
            }

            return null;
        }

        MeshRenderer ResolveRenderer(MeshRenderer renderer, GameObject childObject, string childName)
        {
            if (renderer != null)
            {
                return renderer;
            }

            GameObject resolvedChildObject = ResolveChildObject(childObject, childName);
            if (resolvedChildObject != null)
            {
                MeshRenderer childRenderer = (MeshRenderer)resolvedChildObject.GetComponent(typeof(MeshRenderer));
                if (childRenderer != null)
                {
                    return childRenderer;
                }
            }

            return (MeshRenderer)GetComponent(typeof(MeshRenderer));
        }

        public MeshRenderer GetSortedRenderer()
        {
            sortedObject = ResolveChildObject(sortedObject, "Sorted");
            sortedRenderer = ResolveRenderer(sortedRenderer, sortedObject, "Sorted");
            return sortedRenderer;
        }

        int InferMaxSHBandFromMaterial(Material material)
        {
            if (material == null)
            {
                return 0;
            }

            if (material.HasProperty("_GS_SH9") && material.GetTexture("_GS_SH9") != null)
            {
                return 3;
            }

            if (material.HasProperty("_GS_SH4") && material.GetTexture("_GS_SH4") != null)
            {
                return 2;
            }

            if (material.HasProperty("_GS_SH1") && material.GetTexture("_GS_SH1") != null)
            {
                return 1;
            }

            if (!material.HasProperty("_SHBand"))
            {
                return 0;
            }

            return 0;
        }

        public int GetMaxSHBand()
        {
            MeshRenderer renderer = GetSortedRenderer();
            if (renderer == null)
            {
                return 0;
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
            {
                return 0;
            }

            int inferredMax = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                int materialMax = InferMaxSHBandFromMaterial(materials[i]);
                if (materialMax > inferredMax)
                {
                    inferredMax = materialMax;
                }
            }

            if (inferredMax >= 0)
            {
                maxShBand = inferredMax;
            }

            return inferredMax;
        }

        public void SetMaxSHBand(int value)
        {
            maxShBand = Mathf.Clamp(value, 0, 3);
        }

        public void ShowSorted()
        {
            sortedObject = ResolveChildObject(sortedObject, "Sorted");
            if (sortedObject != null)
            {
                sortedObject.SetActive(true);
            }

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == null || (sortedObject != null && child.gameObject == sortedObject))
                {
                    continue;
                }

                if (child.GetComponent(typeof(Renderer)) != null || child.GetComponent(typeof(MeshFilter)) != null)
                {
                    child.gameObject.SetActive(false);
                }
            }
        }
    }
}
