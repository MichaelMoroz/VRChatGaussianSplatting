#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting
{
    // Variable-size chunk construction for the LOD importer:
    //   * 63-bit Morton (Z-order) encode/decode (21 bits/axis) used as the sort key + radix-tree key.
    //   * A multithreaded LSD radix sort over (ulong key, uint payload). The payload is the original record
    //     index, so callers sort an index permutation cheaply instead of moving fat BucketRecord structs.
    //   * A Karras LBVH + gap-ratio cut that turns the sorted codes into contiguous variable-size chunks.
    //
    // The radix sort is CPU-only and has no 2^24 element limit (the payload is a real uint32, unlike the GPU
    // radix whose float value channel is exact only to 2^24). It is the single path for all input sizes.
    //
    // Morton is used (rather than Hilbert) because EVERY prefix of a Morton code is an axis-aligned box: the
    // bits interleave x,y,z, so any prefix fixes the top bits of each axis. A Karras node is always an exact
    // prefix group, so the cut frontier is a set of disjoint, tiling cells with tight AABBs (minimal overlap)
    // - which keeps the LOD projected-size estimate accurate and tightens the per-chunk 10-bit position packing.
    // (Hilbert has better intra-chunk ordering locality, but the importance sort reorders within a chunk anyway,
    // and Hilbert's non-octree-level nodes are not axis-aligned, giving loose overlapping chunk boxes.)
    public static class MortonLBVH
    {
        public const int MortonBitsPerAxis = 21;          // 3 * 21 = 63 bits, fits in a ulong with one spare bit.
        public const uint MortonAxisMax = (1u << MortonBitsPerAxis) - 1u;

        // Encode an integer 3D coordinate (each axis in [0, 2^21)) into a 63-bit Morton (Z-order) code.
        public static ulong EncodeMorton63(uint x, uint y, uint z)
        {
            x &= MortonAxisMax; y &= MortonAxisMax; z &= MortonAxisMax;
            // Interleave MSB-first, x then y then z within each level: source bit b of axis {x,y,z} lands at
            // index bit {3b+2, 3b+1, 3b+0}. Every prefix is therefore an axis-aligned box.
            ulong index = 0;
            for (int bit = MortonBitsPerAxis - 1; bit >= 0; bit--)
            {
                index = (index << 1) | ((x >> bit) & 1u);
                index = (index << 1) | ((y >> bit) & 1u);
                index = (index << 1) | ((z >> bit) & 1u);
            }
            return index;
        }

        // Inverse of EncodeMorton63: recover the integer coordinate from a 63-bit Morton code.
        public static void DecodeMorton63(ulong index, out uint x, out uint y, out uint z)
        {
            uint ax = 0, ay = 0, az = 0;
            for (int b = 0; b < MortonBitsPerAxis; b++)
            {
                ax |= (uint)((index >> (3 * b + 2)) & 1UL) << b;
                ay |= (uint)((index >> (3 * b + 1)) & 1UL) << b;
                az |= (uint)((index >> (3 * b + 0)) & 1UL) << b;
            }
            x = ax; y = ay; z = az;
        }

        // Quantize a normalized position (each component in [0,1]) to a 63-bit Morton key.
        public static ulong MortonKey63(float nx, float ny, float nz)
        {
            uint x = (uint)Mathf.Clamp(Mathf.RoundToInt(nx * MortonAxisMax), 0, (int)MortonAxisMax);
            uint y = (uint)Mathf.Clamp(Mathf.RoundToInt(ny * MortonAxisMax), 0, (int)MortonAxisMax);
            uint z = (uint)Mathf.Clamp(Mathf.RoundToInt(nz * MortonAxisMax), 0, (int)MortonAxisMax);
            return EncodeMorton63(x, y, z);
        }

        // Stable, multithreaded LSD radix sort of (keys, vals) by key. 8-bit digit, 8 passes (covers 64 bits).
        // Both arrays are sorted in place; vals carries the caller's payload (typically the original index).
        public static void RadixSort63(ulong[] keys, uint[] vals, int count)
        {
            if (count <= 1)
            {
                return;
            }
            if (keys.Length < count || vals.Length < count)
            {
                throw new ArgumentException("RadixSort63: key/value arrays shorter than count.");
            }

            ulong[] keysTmp = new ulong[count];
            uint[] valsTmp = new uint[count];

            // One segment per worker; keep segments large enough that the per-segment histogram overhead pays off.
            int segments = Mathf.Clamp(count / 65536, 1, Math.Max(1, Environment.ProcessorCount));
            int[] segStart = new int[segments + 1];
            for (int s = 0; s <= segments; s++)
            {
                segStart[s] = (int)((long)count * s / segments);
            }

            int[][] hist = new int[segments][];
            int[][] offset = new int[segments][];
            for (int s = 0; s < segments; s++)
            {
                hist[s] = new int[256];
                offset[s] = new int[256];
            }

            for (int pass = 0; pass < 8; pass++)
            {
                int shift = pass * 8;

                Parallel.For(0, segments, s =>
                {
                    int[] h = hist[s];
                    Array.Clear(h, 0, 256);
                    for (int i = segStart[s]; i < segStart[s + 1]; i++)
                    {
                        h[(int)((keys[i] >> shift) & 0xFFu)]++;
                    }
                });

                // Global stable ordering: for each digit, lay out segment 0..S-1 contiguously, digits ascending.
                int running = 0;
                for (int d = 0; d < 256; d++)
                {
                    for (int s = 0; s < segments; s++)
                    {
                        offset[s][d] = running;
                        running += hist[s][d];
                    }
                }

                // Each segment scatters into its own disjoint output ranges -> parallel and stable.
                Parallel.For(0, segments, s =>
                {
                    int[] local = offset[s];
                    int[] cursor = new int[256];
                    Array.Copy(local, cursor, 256);
                    for (int i = segStart[s]; i < segStart[s + 1]; i++)
                    {
                        int d = (int)((keys[i] >> shift) & 0xFFu);
                        int pos = cursor[d]++;
                        keysTmp[pos] = keys[i];
                        valsTmp[pos] = vals[i];
                    }
                });

                // Swap source/destination. After 8 (even) passes the data is back in the caller's arrays.
                ulong[] tk = keys; keys = keysTmp; keysTmp = tk;
                uint[] tv = vals; vals = valsTmp; valsTmp = tv;
            }
        }

        // Round-trip + cell alignment (8 consecutive codes from a multiple of 8 occupy a 2x2x2 cube, i.e. a
        // prefix is an octree cell): the properties the LBVH relies on.
        public static bool ValidateMorton(int samples)
        {
            var rng = new System.Random(12345);
            bool ok = true;

            for (int i = 0; i < samples; i++)
            {
                uint x = (uint)rng.Next(0, (int)MortonAxisMax + 1);
                uint y = (uint)rng.Next(0, (int)MortonAxisMax + 1);
                uint z = (uint)rng.Next(0, (int)MortonAxisMax + 1);
                ulong h = EncodeMorton63(x, y, z);
                DecodeMorton63(h, out uint rx, out uint ry, out uint rz);
                if (rx != x || ry != y || rz != z)
                {
                    Debug.LogError($"Morton round-trip failed: ({x},{y},{z}) -> {h} -> ({rx},{ry},{rz})");
                    ok = false;
                    break;
                }
            }

            // Cell alignment: 8 consecutive codes from a multiple of 8 occupy a 2x2x2 cube (prefix = octree cell).
            for (int t = 0; t < 4096 && ok; t++)
            {
                ulong baseIdx = ((ulong)(uint)rng.Next() << 3);
                uint mnx = uint.MaxValue, mny = uint.MaxValue, mnz = uint.MaxValue;
                uint mxx = 0, mxy = 0, mxz = 0;
                for (ulong j = 0; j < 8; j++)
                {
                    DecodeMorton63(baseIdx + j, out uint cx, out uint cy, out uint cz);
                    mnx = Math.Min(mnx, cx); mny = Math.Min(mny, cy); mnz = Math.Min(mnz, cz);
                    mxx = Math.Max(mxx, cx); mxy = Math.Max(mxy, cy); mxz = Math.Max(mxz, cz);
                }
                if (mxx - mnx != 1 || mxy - mny != 1 || mxz - mnz != 1)
                {
                    Debug.LogError($"Morton cell alignment failed: extent=({mxx - mnx},{mxy - mny},{mxz - mnz})");
                    ok = false;
                }
            }

            Debug.Log(ok ? $"Morton validation passed ({samples} round-trips + cell alignment)."
                         : "Morton validation FAILED.");
            return ok;
        }

        // Radix sort correctness vs a reference sort, including stability of equal keys.
        public static bool ValidateRadix(int count)
        {
            var rng = new System.Random(999);
            ulong[] keys = new ulong[count];
            uint[] vals = new uint[count];
            for (int i = 0; i < count; i++)
            {
                // Mix in collisions so stability is actually exercised.
                keys[i] = ((ulong)(uint)rng.Next() << 31) ^ (ulong)(uint)(rng.Next() & 0x3FF);
                vals[i] = (uint)i;
            }

            var refIdx = new uint[count];
            for (uint i = 0; i < count; i++) refIdx[i] = i;
            ulong[] refKeys = (ulong[])keys.Clone();
            Array.Sort(refIdx, (a, b) =>
            {
                int c = refKeys[a].CompareTo(refKeys[b]);
                return c != 0 ? c : a.CompareTo(b); // stable tiebreak by original index
            });

            RadixSort63(keys, vals, count);

            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                if (keys[i] != refKeys[refIdx[i]] || vals[i] != refIdx[i])
                {
                    Debug.LogError($"Radix mismatch at {i}: key {keys[i]} vs {refKeys[refIdx[i]]}, val {vals[i]} vs {refIdx[i]}");
                    ok = false;
                    break;
                }
            }

            Debug.Log(ok ? $"Radix validation passed ({count} elements, stable)." : "Radix validation FAILED.");
            return ok;
        }

        // ------------------------------------------------------------------------------------------------------
        // Karras LBVH + gap-ratio cut
        // ------------------------------------------------------------------------------------------------------

        // A contiguous run of the SORTED splat array forming one variable-size chunk.
        public struct ChunkRange
        {
            public int start;
            public int count;
        }

        // Build variable-size chunks from a sorted Morton-keyed splat set via a Karras LBVH + gap-ratio cut.
        //   keys, pos : SORTED by Morton key (pos aligned to keys); n = element count.
        //   cap       : hard max splats per chunk (always split above it).
        //   minChunk  : hard min splats per chunk (default 32 = the render mesh's splats-per-point packing).
        //   gapRatio  : split a sub-cap node only when the children's combined surface area drops below this
        //               fraction of the parent's, i.e. the split removes empty space (separates sub-clusters).
        //               A dense region (children nearly as large as the parent) is kept whole up to the cap;
        //               corner-peeling is rejected because the large child keeps the combined area near the
        //               parent's. ~0.85 keeps dense regions cap-sized and only subdivides across real gaps.
        public static ChunkRange[] BuildChunks(ulong[] keys, Vector3[] pos, int n, int cap, int minChunk, float gapRatio)
        {
            if (n <= 0) return Array.Empty<ChunkRange>();
            if (n <= minChunk) return new[] { new ChunkRange { start = 0, count = n } };

            // Node id space: leaves [0, n), internal nodes [n, 2n-1). Internal node k is stored at id (n + k).
            int internalCount = n - 1;
            int[] left = new int[internalCount];
            int[] right = new int[internalCount];
            int[] rangeFirst = new int[internalCount];
            int[] rangeLast = new int[internalCount];
            int[] parent = new int[2 * n - 1];

            // --- Karras radix-tree topology (one internal node per parallel iteration). ---
            Parallel.For(0, internalCount, i =>
            {
                int d = Delta(keys, n, i, i + 1) >= Delta(keys, n, i, i - 1) ? 1 : -1;
                int deltaMin = Delta(keys, n, i, i - d);

                int lMax = 2;
                while (Delta(keys, n, i, i + lMax * d) > deltaMin) lMax <<= 1;

                int l = 0;
                for (int t = lMax >> 1; t >= 1; t >>= 1)
                    if (Delta(keys, n, i, i + (l + t) * d) > deltaMin) l += t;

                int j = i + l * d;
                int first = Math.Min(i, j);
                int last = Math.Max(i, j);

                int split = FindSplit(keys, n, first, last);
                int leftId = (split == first) ? split : (n + split);
                int rightId = (split + 1 == last) ? (split + 1) : (n + split + 1);

                rangeFirst[i] = first;
                rangeLast[i] = last;
                left[i] = leftId;
                right[i] = rightId;
                parent[leftId] = n + i;
                parent[rightId] = n + i;
            });
            parent[n] = -1; // root is internal node 0

            // --- Bottom-up AABB (Karras atomic parent-walk: second child to arrive merges + ascends). ---
            Vector3[] bmin = new Vector3[2 * n - 1];
            Vector3[] bmax = new Vector3[2 * n - 1];
            Parallel.For(0, n, k => { bmin[k] = pos[k]; bmax[k] = pos[k]; });

            int[] flags = new int[internalCount];
            Parallel.For(0, n, k =>
            {
                int node = parent[k];
                while (node >= 0)
                {
                    int ii = node - n;
                    if (Interlocked.Increment(ref flags[ii]) == 1) break; // first child; sibling not ready yet
                    int l = left[ii], r = right[ii];
                    bmin[node] = Vector3.Min(bmin[l], bmin[r]);
                    bmax[node] = Vector3.Max(bmax[l], bmax[r]);
                    node = parent[node];
                }
            });

            // --- Top-down gap-ratio cut. ---
            var chunks = new List<ChunkRange>();
            var stack = new Stack<int>();
            stack.Push(n);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (id < n) { chunks.Add(new ChunkRange { start = id, count = 1 }); continue; }

                int ii = id - n;
                int first = rangeFirst[ii], last = rangeLast[ii];
                int count = last - first + 1;
                int l = left[ii], r = right[ii];
                int nL = NodeCount(l, n, rangeFirst, rangeLast);
                int nR = NodeCount(r, n, rangeFirst, rangeLast);

                bool canSplit = nL >= minChunk && nR >= minChunk;
                bool productive = false;
                if (canSplit)
                {
                    float aP = SurfaceArea(bmax[id] - bmin[id]);
                    float aL = SurfaceArea(bmax[l] - bmin[l]);
                    float aR = SurfaceArea(bmax[r] - bmin[r]);
                    productive = aL + aR < gapRatio * aP; // split only when it removes empty space between sub-clusters
                }

                if (count > cap || (productive && canSplit))
                {
                    stack.Push(l);
                    stack.Push(r);
                }
                else
                {
                    chunks.Add(new ChunkRange { start = first, count = count });
                }
            }

            chunks.Sort((a, b) => a.start.CompareTo(b.start));
            MergeSmall(chunks, minChunk); // mandatory cap-splits can leave a sub-min child; merge it into a neighbor
            return chunks.ToArray();
        }

        // Longest common prefix length of keys[i],keys[j] (Karras delta), with an index tiebreak for equal keys.
        static int Delta(ulong[] keys, int n, int i, int j)
        {
            if (j < 0 || j >= n) return -1;
            ulong x = keys[i] ^ keys[j];
            if (x == 0) return 64 + (Clz64((uint)(i ^ j)) - 32); // identical keys: order by index
            return Clz64(x);
        }

        static int FindSplit(ulong[] keys, int n, int first, int last)
        {
            int commonPrefix = Delta(keys, n, first, last);
            int split = first;
            int step = last - first;
            do
            {
                step = (step + 1) >> 1;
                int newSplit = split + step;
                if (newSplit < last && Delta(keys, n, first, newSplit) > commonPrefix) split = newSplit;
            } while (step > 1);
            return split;
        }

        static int NodeCount(int id, int n, int[] rangeFirst, int[] rangeLast)
            => id < n ? 1 : rangeLast[id - n] - rangeFirst[id - n] + 1;

        static float SurfaceArea(Vector3 size)
        {
            float x = Mathf.Abs(size.x), y = Mathf.Abs(size.y), z = Mathf.Abs(size.z);
            return 2f * (x * y + y * z + z * x);
        }

        // Merge contiguous chunks so each holds >= minChunk (except a single chunk when the whole input is smaller).
        static void MergeSmall(List<ChunkRange> chunks, int minChunk)
        {
            if (chunks.Count <= 1) return;
            var merged = new List<ChunkRange>(chunks.Count);
            ChunkRange cur = chunks[0];
            for (int i = 1; i < chunks.Count; i++)
            {
                if (cur.count < minChunk) cur.count += chunks[i].count; // absorb contiguous neighbor
                else { merged.Add(cur); cur = chunks[i]; }
            }
            if (cur.count < minChunk && merged.Count > 0)
            {
                ChunkRange last = merged[merged.Count - 1];
                last.count += cur.count;
                merged[merged.Count - 1] = last;
            }
            else merged.Add(cur);
            chunks.Clear();
            chunks.AddRange(merged);
        }

        static int Clz64(ulong x)
        {
            if (x == 0) return 64;
            int n = 0;
            if (x <= 0x00000000FFFFFFFFUL) { n += 32; x <<= 32; }
            if (x <= 0x0000FFFFFFFFFFFFUL) { n += 16; x <<= 16; }
            if (x <= 0x00FFFFFFFFFFFFFFUL) { n += 8; x <<= 8; }
            if (x <= 0x0FFFFFFFFFFFFFFFUL) { n += 4; x <<= 4; }
            if (x <= 0x3FFFFFFFFFFFFFFFUL) { n += 2; x <<= 2; }
            if (x <= 0x7FFFFFFFFFFFFFFFUL) { n += 1; }
            return n;
        }

        // Build chunks on a mixed distribution (tight clusters + sparse uniform) and verify the partition is
        // valid: contiguous, full coverage, every chunk >= minChunk.
        public static bool ValidateLBVH(int n, int cap, int minChunk)
        {
            var rng = new System.Random(7);
            Vector3[] pts = new Vector3[n];
            int clusters = 64;
            Vector3[] centers = new Vector3[clusters];
            for (int c = 0; c < clusters; c++)
                centers[c] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            for (int i = 0; i < n; i++)
            {
                if (rng.NextDouble() < 0.85) // dense cluster member
                {
                    Vector3 c = centers[rng.Next(clusters)];
                    pts[i] = c + 0.01f * new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
                }
                else // sparse uniform background
                {
                    pts[i] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
                }
            }

            ulong[] keys = new ulong[n];
            uint[] vals = new uint[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = MortonKey63(Mathf.Clamp01(pts[i].x), Mathf.Clamp01(pts[i].y), Mathf.Clamp01(pts[i].z));
                vals[i] = (uint)i;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            RadixSort63(keys, vals, n);
            long tSort = sw.ElapsedMilliseconds;

            Vector3[] sortedPts = new Vector3[n];
            for (int i = 0; i < n; i++) sortedPts[i] = pts[vals[i]];

            sw.Restart();
            ChunkRange[] chunks = BuildChunks(keys, sortedPts, n, cap, minChunk, 0.85f);
            long tBuild = sw.ElapsedMilliseconds;

            bool ok = true;
            int total = 0, expectedStart = 0, minC = int.MaxValue, maxC = 0, under = 0;
            foreach (var ch in chunks)
            {
                if (ch.start != expectedStart) { Debug.LogError($"Chunk gap/overlap at start {ch.start}, expected {expectedStart}"); ok = false; break; }
                expectedStart += ch.count;
                total += ch.count;
                minC = Math.Min(minC, ch.count);
                maxC = Math.Max(maxC, ch.count);
                if (ch.count < minChunk) under++;
            }
            if (total != n) { Debug.LogError($"Coverage mismatch: {total} vs {n}"); ok = false; }
            if (under > 0) { Debug.LogError($"{under} chunk(s) below minChunk {minChunk}"); ok = false; }

            Debug.Log(ok
                ? $"LBVH+gap-ratio validation passed: {n:N0} pts -> {chunks.Length:N0} chunks (min {minC}, max {maxC}, avg {(float)n / chunks.Length:F0}); sort {tSort} ms, build {tBuild} ms."
                : "LBVH+gap-ratio validation FAILED.");
            return ok;
        }
    }
}
#endif
