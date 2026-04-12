using UnityEngine;
using UdonSharp;

namespace GaussianSplatting
{
    public class GaussianSplatObject : UdonSharpBehaviour
    {
        [SerializeField] public GameObject sortedObject;
        [SerializeField] public MeshRenderer sortedRenderer;

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
