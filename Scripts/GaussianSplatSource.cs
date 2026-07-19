using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// Shared helper for resolving a splat renderer's primary material (the one carrying the splat data).
    /// The combiner uses it to bind the combined render order. Kept as a plain static class (no
    /// UdonSharpBehaviour, no .asset) - the proven way to share static helpers across this project's Udon
    /// behaviours. Touches only whitelisted Material APIs,
    /// so it is safe to call from runtime Udon code.
    /// </summary>
    public static class GaussianSplatSource
    {
        public static Material ResolvePrimarySplatMaterial(Material[] materials)
        {
            if (materials == null)
            {
                return null;
            }
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.HasProperty("_GS_Positions"))
                {
                    return material;
                }
            }
            return null;
        }
    }
}
