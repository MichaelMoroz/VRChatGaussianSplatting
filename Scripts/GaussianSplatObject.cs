using UnityEngine;
using UdonSharp;

namespace GaussianSplatting
{
    public class GaussianSplatObject : UdonSharpBehaviour
    {
        [SerializeField] public GameObject sortedObject;
        [SerializeField] public GameObject stochasticObject;
        [SerializeField] public MeshRenderer sortedRenderer;
        [SerializeField] public MeshRenderer stochasticRenderer;

        public MeshRenderer GetSortedRenderer()
        {
            if (sortedRenderer != null)
            {
                return sortedRenderer;
            }

            return (MeshRenderer)GetComponent(typeof(MeshRenderer));
        }

        public void ShowSorted()
        {
            if (sortedObject != null)
            {
                sortedObject.SetActive(true);
            }
            if (stochasticObject != null)
            {
                stochasticObject.SetActive(false);
            }
        }

        public void ShowStochastic()
        {
            if (sortedObject != null)
            {
                sortedObject.SetActive(false);
            }
            if (stochasticObject != null)
            {
                stochasticObject.SetActive(true);
            }
        }
    }
}
