#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEngine;
using UnityEditor;

namespace GaussianSplatting
{
    // Reimporting a texture/source asset that a GaussianSplatObject only references (its packed position/SH/etc.
    // assets) does NOT fire the component's OnValidate, so nothing requests the editor refresh that re-evaluates
    // the fuse signature - a reimport would otherwise go unnoticed until an unrelated interaction. This nudges a
    // refresh when a splat-data asset is (re)imported; the fuse signature (which hashes those textures' content)
    // then decides whether an actual rebake is needed, so an unrelated import costs only a cheap no-op refresh.
    class GaussianSplatReimportWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (Application.isPlaying)
            {
                return;
            }
            if (HasSplatAsset(imported) || HasSplatAsset(deleted) || HasSplatAsset(moved))
            {
                GaussianSplatRenderer.RequestEditorRefresh();
            }
        }

        // Packed splat textures are saved as .asset (Texture2D); sources are .ply/.spz. Other asset kinds can't
        // change what the fuse bakes, so they don't warrant a refresh.
        static bool HasSplatAsset(string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                if (p.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".ply", System.StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".spz", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
#endif
