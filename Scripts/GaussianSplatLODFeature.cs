using UnityEngine;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System.IO;
#endif

namespace GaussianSplatting
{
    public static class GaussianSplatLODFeature
    {
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        const string LodObjectPath = "Assets/VRChatGaussianSplatting/Scripts/GaussianSplatLODObject.cs";
        const string LodPlaceholderMarker = "VRCGS_LOD_PLACEHOLDER";

        public static bool IsAvailable()
        {
            if (!File.Exists(LodObjectPath))
            {
                return false;
            }
            string lodObjectSource = File.ReadAllText(LodObjectPath);
            return lodObjectSource.IndexOf(LodPlaceholderMarker, System.StringComparison.Ordinal) < 0
                && Shader.Find("Hidden/GaussianSplatting/LODChunkSelect") != null
                && Shader.Find("Hidden/GaussianSplatting/LODCombineData") != null;
        }
#else
        public static bool IsAvailable()
        {
            return true;
        }
#endif
    }
}
