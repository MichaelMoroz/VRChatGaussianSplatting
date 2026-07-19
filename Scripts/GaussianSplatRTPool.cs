#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting
{
    // Shared, scene-independent pool of combined-render textures + draw-pass meshes at x4-stepped splat-count
    // buckets (256K / 1M / 4M / 16M). The assets are PREGENERATED and committed under PoolRoot; this class only
    // LOADS them and hands back typed references, which the editor bake assigns into the components' plain
    // RenderTexture[] bucket arrays (Udon can only see RenderTexture, not a custom container type). Every scene
    // binds one bucket's set at runtime; unused buckets cost no VRAM until first rendered into. (The assets are
    // not regenerated at runtime - if one is ever missing, restore it from git history.)
    public static class GaussianSplatRTPool
    {
        // x4 steps keep the number of distinct resident buckets small (a touched bucket can't be freed in Udon).
        public static readonly int[] BucketCapacities = { 256 * 1024, 1024 * 1024, 4 * 1024 * 1024, 16 * 1024 * 1024 };

        public const string PoolRoot = "Assets/VRChatGaussianSplatting/RTPool";

        public struct BucketSet
        {
            public int capacity;
            public RenderTexture keyValues0, keyValues1, histograms, prefixSums, splatRenderOrder, splatRenderOrderPhoto;
            public RenderTexture combinedPositions, combinedRotations, combinedScales, combinedColors, combinedColorsCamera;
        }

        public static int BucketCount => BucketCapacities.Length;

        // Smallest bucket index whose capacity >= required; -1 if it exceeds the largest bucket.
        public static int BucketIndexForCount(int requiredCount)
        {
            for (int i = 0; i < BucketCapacities.Length; i++)
            {
                if (requiredCount <= BucketCapacities[i])
                {
                    return i;
                }
            }
            return -1;
        }

        public static string BucketFolder(int bucketIndex)
        {
            int cap = BucketCapacities[bucketIndex];
            string label = cap >= 1024 * 1024 ? (cap / (1024 * 1024)) + "M" : (cap / 1024) + "K";
            return PoolRoot + "/B" + label;
        }

        static RenderTexture LoadRT(string folder, string name)
        {
            return AssetDatabase.LoadAssetAtPath<RenderTexture>(folder + "/" + name + ".renderTexture");
        }

        // Load the committed RenderTexture assets for one bucket.
        public static BucketSet LoadBucket(int bucketIndex)
        {
            string folder = BucketFolder(bucketIndex);
            return new BucketSet
            {
                capacity = BucketCapacities[bucketIndex],
                keyValues0 = LoadRT(folder, "KeyValues0"),
                keyValues1 = LoadRT(folder, "KeyValues1"),
                histograms = LoadRT(folder, "Histograms"),
                prefixSums = LoadRT(folder, "PrefixSums"),
                splatRenderOrder = LoadRT(folder, "SplatRenderOrderScreen"),
                splatRenderOrderPhoto = LoadRT(folder, "SplatRenderOrderPhoto"),
                combinedPositions = LoadRT(folder, "CombinedPositions"),
                combinedRotations = LoadRT(folder, "CombinedRotations"),
                combinedScales = LoadRT(folder, "CombinedScales"),
                combinedColors = LoadRT(folder, "CombinedColors"),
                combinedColorsCamera = LoadRT(folder, "CombinedColorsCamera"),
            };
        }

        // Geometric draw-pass ladder (cumulative 512K, 1M, 2M, 4M, 8M, 16M). Each combined object carries one
        // pass renderer per entry; the runtime enables the minimal prefix that covers the rendered count. Pass
        // meshes are shared across every object/scene (procedural: a few point indices), so all stay resident
        // cheaply. The first pass needs no alpha mask (nothing is drawn before it); the rest occlude the prior.
        public static readonly int[] PassSplatCounts = { 512 * 1024, 512 * 1024, 1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024, 8 * 1024 * 1024 };
        public static readonly bool[] PassHasAlphaMask = { false, true, true, true, true, true };

        public static int PassCount => PassSplatCounts.Length;

        // Cumulative splat capacity once passes [0..passIndex] are enabled.
        public static int PassCumulativeCount(int passIndex)
        {
            int sum = 0;
            for (int i = 0; i <= passIndex && i < PassSplatCounts.Length; i++)
            {
                sum += PassSplatCounts[i];
            }
            return sum;
        }

        // Minimal prefix of passes whose cumulative capacity covers maxCount (the bake builds this many pass
        // renderers; the runtime enables a prefix of them per the live rendered count). Always >= 1 pass for a
        // non-empty combine; clamped to the full ladder (16M).
        public static int PassesToCover(int maxCount)
        {
            if (maxCount <= 0)
            {
                return 0;
            }
            int cumulative = 0;
            for (int i = 0; i < PassSplatCounts.Length; i++)
            {
                cumulative += PassSplatCounts[i];
                if (cumulative >= maxCount)
                {
                    return i + 1;
                }
            }
            return PassSplatCounts.Length;
        }

        // The geometric pass ladder as PassInfo entries (offset/count/alpha-mask per pass), for the editor bake
        // to build the combined chunk hierarchy. Each pass is full-size and mesh-aligned; the runtime sets
        // _SplatCount on the boundary pass for the partial remainder.
        public static GaussianSplatImporter.PassInfo[] CreateGeometricPassLayout(int maxCount)
        {
            int passCount = PassesToCover(maxCount);
            GaussianSplatImporter.PassInfo[] passes = new GaussianSplatImporter.PassInfo[passCount];
            int cumulative = 0;
            for (int i = 0; i < passCount; i++)
            {
                passes[i] = new GaussianSplatImporter.PassInfo(i, cumulative, PassSplatCounts[i], PassHasAlphaMask[i]);
                cumulative += PassSplatCounts[i];
            }
            return passes;
        }

        const string PassMeshFolder = PoolRoot + "/Meshes";

        // Load the committed shared mesh for one draw pass.
        public static Mesh LoadPassMesh(int passIndex)
        {
            int splatCount = PassSplatCounts[passIndex];
            bool hasAlphaMask = PassHasAlphaMask[passIndex];
            string label = splatCount >= 1024 * 1024 ? (splatCount / (1024 * 1024)) + "M" : (splatCount / 1024) + "K";
            string assetPath = PassMeshFolder + "/Pass" + passIndex + "_" + label + (hasAlphaMask ? "_Mask" : "") + ".asset";
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }
    }
}
#endif
