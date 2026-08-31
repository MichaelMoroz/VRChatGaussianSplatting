using System.Collections.Generic;
using NUnit.Framework;
using GaussianSplatting;

namespace GaussianSplatting.Tests
{
    // f_rest layout detection. Two packings exist in the wild and are told apart only by which property
    // names are present: INRIA separates the SH channels by 15, others pack them at the exported count.
    public class StreamedSplatReaderSHTests
    {
        static Dictionary<string, int> Inria(int coeffCount)
        {
            Dictionary<string, int> offsets = new Dictionary<string, int>();
            for (int coeff = 0; coeff < coeffCount; coeff++)
            {
                for (int channel = 0; channel < 3; channel++) offsets[$"f_rest_{coeff + channel * 15}"] = offsets.Count * 4;
            }
            return offsets;
        }

        static Dictionary<string, int> Consecutive(int coeffCount)
        {
            Dictionary<string, int> offsets = new Dictionary<string, int>();
            for (int i = 0; i < coeffCount * 3; i++) offsets[$"f_rest_{i}"] = i * 4;
            return offsets;
        }

        [TestCase(15)]
        [TestCase(8)]
        [TestCase(3)]
        public void Inria_DetectsBandAtStride15(int coeffCount)
        {
            Assert.AreEqual(coeffCount, StreamedSplatReader.DetectSHCoeffCount(Inria(coeffCount), out int stride));
            Assert.AreEqual(15, stride);
        }

        [TestCase(8)]
        [TestCase(3)]
        public void Consecutive_DetectsBandAtItsOwnStride(int coeffCount)
        {
            Assert.AreEqual(coeffCount, StreamedSplatReader.DetectSHCoeffCount(Consecutive(coeffCount), out int stride));
            Assert.AreEqual(coeffCount, stride);
        }

        [Test]
        public void FullSH3_IsIdenticalUnderBothPackings()
        {
            Assert.AreEqual(15, StreamedSplatReader.DetectSHCoeffCount(Consecutive(15), out int stride));
            Assert.AreEqual(15, stride);
        }

        // A consecutive file carrying more than SH3 must not be mistaken for stride 15 just because
        // f_rest_0..44 happen to be present; the extra coefficients are dropped, the stride is not.
        [Test]
        public void ConsecutiveBeyondSH3_KeepsItsStrideAndCapsAtSH3()
        {
            Assert.AreEqual(15, StreamedSplatReader.DetectSHCoeffCount(Consecutive(24), out int stride));
            Assert.AreEqual(24, stride);
        }

        [Test]
        public void NoRestProperties_IsDcOnly()
        {
            Assert.AreEqual(0, StreamedSplatReader.DetectSHCoeffCount(new Dictionary<string, int>(), out int stride));
            Assert.AreEqual(15, stride);
        }

        // A truncated INRIA write must step down to the largest complete band, not read past the gap.
        [Test]
        public void IncompleteInriaSH3_StepsDownToSH2()
        {
            Dictionary<string, int> offsets = Inria(15);
            offsets.Remove("f_rest_44");
            Assert.AreEqual(8, StreamedSplatReader.DetectSHCoeffCount(offsets, out int stride));
            Assert.AreEqual(15, stride);
        }
    }
}
