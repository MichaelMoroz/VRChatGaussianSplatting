using UnityEngine;
using UdonSharp;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
#endif

namespace GaussianSplatting
{
    public class GaussianSplatObject : UdonSharpBehaviour
    {
        [SerializeField] public GaussianSplatRenderer gaussianSplatRenderer;
        [SerializeField] public GameObject sortedObject;
        [SerializeField] public MeshRenderer sortedRenderer;
        [SerializeField] int maxShBand = -1;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        void Reset()
        {
            EnsureSceneRenderer(true);
        }

        void EnsureSceneRenderer(bool createIfMissing)
        {
            if (EditorUtility.IsPersistent(this))
            {
                return;
            }

            GaussianSplatRenderer sceneRenderer = createIfMissing
                ? GaussianSplatRenderer.EnsureSceneRendererExists(gameObject.scene)
                : GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);

            if (sceneRenderer == null)
            {
                return;
            }

            if (createIfMissing && gaussianSplatRenderer != sceneRenderer)
            {
                gaussianSplatRenderer = sceneRenderer;
                EditorUtility.SetDirty(this);
            }
        }
#endif

        GaussianSplatRenderer ResolveSceneRendererReference()
        {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (gaussianSplatRenderer != null && gaussianSplatRenderer.gameObject != null && gaussianSplatRenderer.gameObject.scene == gameObject.scene)
            {
                return gaussianSplatRenderer;
            }

            return GaussianSplatRenderer.FindExistingSceneRenderer(gameObject.scene);
#else
            return gaussianSplatRenderer;
#endif
        }

        void Start()
        {
            NotifyRendererEnabled();
        }

        void OnEnable()
        {
            NotifyRendererEnabled();
        }

        public void NotifyRendererEnabled()
        {
            GaussianSplatRenderer sceneRenderer = ResolveSceneRendererReference();
            if (sceneRenderer != null && gameObject.activeInHierarchy)
            {
                sceneRenderer.NotifySplatObjectEnabled(this);
            }
        }

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

        public void SetSortedRendererEnabled(bool enabled)
        {
            MeshRenderer renderer = GetSortedRenderer();
            if (renderer != null)
            {
                renderer.enabled = enabled;
            }
        }

        int InferMaxSHBandFromMaterial(Material material)
        {
            if (material == null)
            {
                return 0;
            }

            if (!material.HasProperty("_GS_SH") || material.GetTexture("_GS_SH") == null || !material.HasProperty("_GS_SH_CoeffCount"))
            {
                return 0;
            }

            int coeffCount = material.GetInt("_GS_SH_CoeffCount");
            if (coeffCount >= 15)
            {
                return 3;
            }

            if (coeffCount >= 8)
            {
                return 2;
            }

            if (coeffCount >= 3)
            {
                return 1;
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

            SetSortedRendererEnabled(true);

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
