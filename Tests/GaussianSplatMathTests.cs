using NUnit.Framework;
using GaussianSplatting;

namespace GaussianSplatting.Tests
{
    // Pure-function tests for the RT-pool / cap-decomposition math. These also guard against drift between the
    // editor pool (GaussianSplatRTPool) and the duplicated Udon-runtime copies in GaussianSplatRenderer /
    // GaussianSplatCombiner, which MUST agree.
    public class GaussianSplatMathTests
    {
        const int K = 1024;
        const int M = 1024 * 1024;

        // ---- RT bucket capacities + selection (editor pool == runtime) ----

        [Test]
        public void BucketCapacities_AreX4FromQuarterMillionTo16M()
        {
            Assert.AreEqual(new[] { 256 * K, 1 * M, 4 * M, 16 * M }, GaussianSplatRTPool.BucketCapacities);
        }

        [Test]
        public void RuntimeBucketCapacity_MatchesPool()
        {
            for (int i = 0; i < GaussianSplatRTPool.BucketCount; i++)
            {
                Assert.AreEqual(GaussianSplatRTPool.BucketCapacities[i], GaussianSplatRenderer.BucketCapacity(i), "bucket " + i);
            }
        }

        [TestCase(0, 0)]
        [TestCase(1, 0)]
        [TestCase(256 * K, 0)]
        [TestCase(256 * K + 1, 1)]
        [TestCase(1 * M, 1)]
        [TestCase(1 * M + 1, 2)]
        [TestCase(4 * M, 2)]
        [TestCase(4 * M + 1, 3)]
        [TestCase(16 * M, 3)]
        public void BucketIndexForCount_PicksSmallestFittingBucket(int count, int expected)
        {
            Assert.AreEqual(expected, GaussianSplatRTPool.BucketIndexForCount(count), "pool");
            Assert.AreEqual(expected, GaussianSplatRenderer.BucketIndexForCount(count), "runtime");
        }

        [Test]
        public void BucketIndexForCount_OverLargest_PoolReturnsMinusOne_RuntimeClampsToTop()
        {
            // Intentional divergence: the editor pool returns -1 so the bake can detect "exceeds 16M" and clamp;
            // the runtime clamps to the top bucket directly.
            Assert.AreEqual(-1, GaussianSplatRTPool.BucketIndexForCount(16 * M + 1));
            Assert.AreEqual(GaussianSplatRTPool.BucketCount - 1, GaussianSplatRenderer.BucketIndexForCount(16 * M + 1));
        }

        // ---- Geometric pass ladder (editor pool == runtime) ----

        [Test]
        public void PassLadder_IsGeometric_512K_512K_1M_2M_4M_8M()
        {
            Assert.AreEqual(new[] { 512 * K, 512 * K, 1 * M, 2 * M, 4 * M, 8 * M }, GaussianSplatRTPool.PassSplatCounts);
            Assert.AreEqual(new[] { false, true, true, true, true, true }, GaussianSplatRTPool.PassHasAlphaMask);
        }

        [Test]
        public void PassCumulativeCount_Is512KShiftedByPassIndex()
        {
            for (int k = 0; k < GaussianSplatRTPool.PassCount; k++)
            {
                Assert.AreEqual((512 * K) << k, GaussianSplatRTPool.PassCumulativeCount(k), "pass " + k);
            }
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(512 * K, 1)]
        [TestCase(512 * K + 1, 2)]
        [TestCase(1 * M, 2)]
        [TestCase(2 * M, 3)]
        [TestCase(4 * M, 4)]
        [TestCase(8 * M, 5)]
        [TestCase(16 * M, 6)]
        [TestCase(16 * M + 1, 6)]
        public void PassesToCover_PicksMinimalPrefix(int count, int expected)
        {
            Assert.AreEqual(expected, GaussianSplatRTPool.PassesToCover(count), "pool");
            Assert.AreEqual(expected, GaussianSplatCombiner.PassesToCover(count), "runtime");
        }

        [Test]
        public void GeometricPassLayout_For4M_Has4FullPasses_FirstWithoutAlphaMask()
        {
            var passes = GaussianSplatRTPool.CreateGeometricPassLayout(4 * M);
            Assert.AreEqual(4, passes.Length);
            int cumulative = 0;
            for (int i = 0; i < passes.Length; i++)
            {
                Assert.AreEqual(i, passes[i].PassIndex, "passIndex " + i);
                Assert.AreEqual(cumulative, passes[i].SplatOffset, "offset " + i);
                Assert.AreEqual(GaussianSplatRTPool.PassSplatCounts[i], passes[i].SplatCount, "count " + i);
                Assert.AreEqual(GaussianSplatRTPool.PassHasAlphaMask[i], passes[i].HasAlphaMask, "alpha " + i);
                cumulative += passes[i].SplatCount;
            }
            Assert.AreEqual(4 * M, cumulative);
        }

        [Test]
        public void GeometricPassLayout_ClampsToFullLadderAt16M()
        {
            Assert.AreEqual(6, GaussianSplatRTPool.CreateGeometricPassLayout(16 * M).Length);
            Assert.AreEqual(6, GaussianSplatRTPool.CreateGeometricPassLayout(32 * M).Length); // over 16M -> clamp
        }

        // ---- Cap: total = min(thinnableSum, budget), clamped to 16M ----

        [TestCase(14500000, 4000000, 4000000)]      // budget caps thinnable
        [TestCase(14500000, 0, 14500000)]           // budget<=0 -> full thinnable (unbudgeted)
        [TestCase(3000000, 5000000, 3000000)]       // budget above thinnable -> full thinnable
        public void ComputeCombinedTierCount_CapsThinnable(int thinnableSum, int budget, int expected)
        {
            Assert.AreEqual(expected, GaussianSplatRenderer.ComputeCombinedTierCount(thinnableSum, budget));
        }

        [Test]
        public void ComputeCombinedTierCount_ClampsToMaxCombined16M()
        {
            Assert.AreEqual(16 * M, GaussianSplatRenderer.ComputeCombinedTierCount(20 * M, 0));
            Assert.AreEqual(16 * M, GaussianSplatRenderer.ComputeCombinedTierCount(20 * M, 18 * M));
        }
    }
}
