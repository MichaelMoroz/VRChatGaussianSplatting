#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using GaussianSplatting.Editor.Utils;

namespace GaussianSplatting
{
    // Shared streamed PLY import front-end used by BOTH the non-LOD importer (GaussianSplatImporter) and the LOD
    // importer (GaussianSplatLODImporter). It lives in the shippable assembly so the non-LOD path never depends
    // on the (strippable) LOD generation code: it reads/linearizes the PLY in a memory-bounded stream, applies
    // crop / horizon / y-flip / normalize, side-stores raw SH, and partitions splats into Hilbert-ordered buckets
    // that callers consume in spatially compact order. LOD-specific generation (k-means, LOD levels, chunk/texture
    // set writing, prefab output) stays in the LOD assembly.
    public static class StreamedSplatReader
    {
        public const int StreamedReadChunkBytes = 64 * 1024 * 1024;
        public const int StreamedBucketMinBits = 0;
        public const int StreamedBucketMaxBits = 9;
        public const int StreamedBucketTargetRecords = 2 * 1024 * 1024;
        public const int StreamedBucketBufferRecords = 1024;
        public const int StreamedBucketSplitBits = 4;
        public const int HilbertKeyBits = 30;

        public struct PLYLayout
        {
            public int count;
            public int stride;
            public int[] splatOffsets;
            public int[] shOffsets;    // [coeff*3 + channel] byte offset, -1 if absent (raw f_rest)
            public int shCoeffCount;   // 0/3/8/15 (SH band coeff count present in the file)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BucketRecord
        {
            public uint key;
            public float px, py, pz;
            public float cr, cg, cb;
            public float opacity;
            public float sx, sy, sz;
            public float rx, ry, rz, rw;
            public float importance;
            public uint sourceIndex;   // stable index into the SH side-store (raw f_rest); 0xFFFFFFFF = synthetic (merged) splat

            public BucketRecord(uint key, ImportSplatData splat)
            {
                this.key = key;
                px = splat.pos.x;
                py = splat.pos.y;
                pz = splat.pos.z;
                cr = splat.dc0.x;
                cg = splat.dc0.y;
                cb = splat.dc0.z;
                opacity = splat.opacity;
                sx = splat.scale.x;
                sy = splat.scale.y;
                sz = splat.scale.z;
                rx = splat.rot.x;
                ry = splat.rot.y;
                rz = splat.rot.z;
                rw = splat.rot.w;
                importance = 0.0f;
                sourceIndex = 0xFFFFFFFFu;
            }

            public ImportSplatData ToSplat()
            {
                return new ImportSplatData
                {
                    pos = new Vector3(px, py, pz),
                    dc0 = new Vector3(cr, cg, cb),
                    opacity = opacity,
                    scale = new Vector3(sx, sy, sz),
                    rot = new Quaternion(rx, ry, rz, rw)
                };
            }
        }

        public sealed class BucketKeyComparer : IComparer<BucketRecord>
        {
            public int Compare(BucketRecord a, BucketRecord b)
            {
                return a.key.CompareTo(b.key);
            }
        }

        public delegate void BucketRecordConsumer(BucketRecord[] records, int count);

        // Chunked float store: the raw-SH side store can exceed .NET's 2 GB single-array limit for huge splats
        // (e.g. 14.5M splats x SH3 = 2.6 GB), so back it with fixed-size blocks instead of one float[].
        public sealed class BigFloatBuffer
        {
            const int BlockShift = 24;            // 16M floats / 64 MB per block
            const int BlockSize = 1 << BlockShift;
            const int BlockMask = BlockSize - 1;
            readonly float[][] _blocks;
            public readonly long Length;

            public BigFloatBuffer(long length)
            {
                Length = length;
                int blockCount = (int)((length + BlockSize - 1) / BlockSize);
                _blocks = new float[Mathf.Max(1, blockCount)][];
                for (int b = 0; b < _blocks.Length; b++)
                {
                    long remaining = length - (long)b * BlockSize;
                    _blocks[b] = new float[(int)System.Math.Min((long)BlockSize, System.Math.Max(0L, remaining))];
                }
            }

            public float this[long i]
            {
                get => _blocks[(int)(i >> BlockShift)][(int)(i & BlockMask)];
                set => _blocks[(int)(i >> BlockShift)][(int)(i & BlockMask)] = value;
            }

            // Copy `count` floats starting at srcIndex into dst (count is small: one splat's SH = coeffCount*3).
            public void CopyTo(long srcIndex, float[] dst, int dstOffset, int count)
            {
                for (int k = 0; k < count; k++) dst[dstOffset + k] = this[srcIndex + k];
            }
        }

        // Shared SH min/range over the whole raw store (per channel, across all coeffs), like the non-LOD path.
        public static void ComputeSharedSHRange(BigFloatBuffer shStore, out Vector4 shMin, out Vector4 shRange)
        {
            Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (long i = 0; i + 2 < shStore.Length; i += 3)
            {
                mn.x = Mathf.Min(mn.x, shStore[i + 0]); mx.x = Mathf.Max(mx.x, shStore[i + 0]);
                mn.y = Mathf.Min(mn.y, shStore[i + 1]); mx.y = Mathf.Max(mx.y, shStore[i + 1]);
                mn.z = Mathf.Min(mn.z, shStore[i + 2]); mx.z = Mathf.Max(mx.z, shStore[i + 2]);
            }
            if (float.IsInfinity(mn.x)) { mn = Vector3.zero; mx = Vector3.zero; }
            shMin = new Vector4(mn.x, mn.y, mn.z, 0.0f);
            shRange = new Vector4(Mathf.Max(mx.x - mn.x, 1e-8f), Mathf.Max(mx.y - mn.y, 1e-8f), Mathf.Max(mx.z - mn.z, 1e-8f), 0.0f);
        }

        // Largest SH band whose every f_rest coefficient/channel is present in the file.
        public static int DetectSHCoeffCount(Dictionary<string, int> offsets)
        {
            int[] bandCoeffCounts = { 15, 8, 3 };
            for (int b = 0; b < bandCoeffCounts.Length; b++)
            {
                int coeffCount = bandCoeffCounts[b];
                bool complete = true;
                for (int coeff = 0; coeff < coeffCount && complete; coeff++)
                {
                    for (int channel = 0; channel < 3; channel++)
                    {
                        if (!offsets.ContainsKey($"f_rest_{coeff + channel * 15}")) { complete = false; break; }
                    }
                }
                if (complete) return coeffCount;
            }
            return 0;
        }

        public static PLYLayout ReadPLYLayout(string plyPath)
        {
            PLYFileReader.ReadFileHeader(plyPath, out int count, out int stride, out List<(string, PLYFileReader.ElementType)> attributes);
            if (count <= 0 || stride <= 0)
            {
                throw new InvalidDataException($"PLY header read failed for '{plyPath}': vertex count {count:N0}, stride {stride}.");
            }

            Dictionary<string, int> offsets = BuildFloatAttributeOffsets(attributes);
            string[] required = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };
            List<string> missing = new List<string>();
            for (int i = 0; i < required.Length; i++)
            {
                if (!offsets.ContainsKey(required[i]))
                {
                    missing.Add(required[i]);
                }
            }
            if (missing.Count > 0)
            {
                throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {string.Join(",", missing)}");
            }

            int[] splatOffsets = new int[required.Length];
            for (int i = 0; i < required.Length; i++)
            {
                splatOffsets[i] = offsets[required[i]];
            }

            int shCoeffCount = DetectSHCoeffCount(offsets);
            int[] shOffsets = new int[shCoeffCount * 3];
            for (int coeff = 0; coeff < shCoeffCount; coeff++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    shOffsets[coeff * 3 + channel] = offsets.TryGetValue($"f_rest_{coeff + channel * 15}", out int o) ? o : -1;
                }
            }
            return new PLYLayout { count = count, stride = stride, splatOffsets = splatOffsets, shOffsets = shOffsets, shCoeffCount = shCoeffCount };
        }

        public static Dictionary<string, int> BuildFloatAttributeOffsets(List<(string, PLYFileReader.ElementType)> attributes)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(attributes.Count);
            int offset = 0;
            for (int i = 0; i < attributes.Count; i++)
            {
                (string name, PLYFileReader.ElementType type) = attributes[i];
                if (type == PLYFileReader.ElementType.Float)
                {
                    result[name] = offset;
                }
                offset += PLYFileReader.TypeToSize(type);
            }
            return result;
        }

        // The import transform is split into horizon-align (pre-crop) and y-flip (post-crop). The crop bounds
        // are authored in the horizon-aligned, PRE-y-flip space (the preview crop handle maps through
        // FromPreviewSpace, which is exactly that space), so the crop test MUST run between these two steps.
        // Baking the y-flip first would compare flipped positions against an un-flipped crop box and reject
        // everything (crop centered at +y vs positions now at -y).
        public static Vector3 ApplyHorizonPosition(Vector3 pos, GaussianSplatImporter.ImportOptions options)
        {
            if (options.applyHorizonAlignment) pos = options.horizonRotation * (pos - options.horizonPivot);
            return pos;
        }

        public static ImportSplatData ApplyHorizonSplat(ImportSplatData splat, GaussianSplatImporter.ImportOptions options)
        {
            if (options.applyHorizonAlignment) splat = GaussianSplatImporter.ApplyHorizonAlignment(splat, options.horizonRotation, options.horizonPivot);
            return splat;
        }

        // The y-flip is ALWAYS baked into coordinates (reflect y on position + covariance) so the prefab uses
        // identity scale — there is no negative-scale prefab alternative. Applied AFTER the crop test. SH parity
        // is reflected in ReadSplatSH.
        public static Vector3 FlipYPosition(Vector3 pos)
        {
            return new Vector3(pos.x, -pos.y, pos.z);
        }

        public static ImportSplatData FlipYSplat(ImportSplatData splat)
        {
            splat.pos = new Vector3(splat.pos.x, -splat.pos.y, splat.pos.z);
            splat.rot = FlipYRotation(splat.rot);
            return splat;
        }

        // Rotation whose covariance equals F * (R Σ Rᵀ) * F under the y-reflection F = diag(1,-1,1). Using
        // R' = F R G with G = diag(1,1,-1) keeps R' a proper rotation (det +1) while reflecting the ellipsoid.
        public static Quaternion FlipYRotation(Quaternion q)
        {
            Matrix4x4 r = Matrix4x4.Rotate(q);
            Matrix4x4 rp = Matrix4x4.Scale(new Vector3(1f, -1f, 1f)) * r * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Vector3 fwd = rp.GetColumn(2);
            Vector3 up = rp.GetColumn(1);
            if (fwd.sqrMagnitude < 1e-12f || up.sqrMagnitude < 1e-12f) return q;
            return Quaternion.LookRotation(fwd, up);
        }

        // y-odd SH coefficient indices (band1: sh1; band2: sh4,sh5; band3: sh9,shA,shB) flip sign under the
        // y reflection; even-in-y coefficients are unchanged. Keeps view-dependent color correct post-flip.
        public static bool IsYOddSHCoeff(int coeff)
        {
            return coeff == 0 || coeff == 3 || coeff == 4 || coeff == 8 || coeff == 9 || coeff == 10;
        }

        public static Bounds GetImportCropBounds(GaussianSplatImporter.ImportOptions options)
        {
            Bounds bounds = options.cropBounds;
            float padding = Mathf.Max(0.0f, options.cropPadding);
            if (padding > 0.0f)
            {
                bounds.extents += Vector3.one * padding;
            }
            return bounds;
        }

        public static bool ShouldIncludePosition(Vector3 pos, GaussianSplatImporter.ImportOptions options)
        {
            return !options.cropToBounds || GetImportCropBounds(options).Contains(pos);
        }

        public static bool ShouldIncludePosition(Vector3 pos, Bounds cropBounds)
        {
            return cropBounds.Contains(pos);
        }

        public static Bounds StreamBounds(string plyPath, PLYLayout layout, GaussianSplatImporter.ImportOptions options, out int acceptedCount, out Vector3 centroid)
        {
            using FileStream fs = PLYFileReader.OpenDataStream(plyPath, out _, out _, out _);
            byte[] buffer = new byte[Mathf.Max(layout.stride, StreamedReadChunkBytes / layout.stride * layout.stride)];
            bool hasBounds = false;
            Vector3 gmin = Vector3.zero, gmax = Vector3.zero;
            double sx = 0, sy = 0, sz = 0;
            int processed = 0;
            acceptedCount = 0;
            bool cropToBounds = options.cropToBounds;
            Bounds cropBounds = cropToBounds ? GetImportCropBounds(options) : default;
            while (processed < layout.count)
            {
                int rowsThisChunk = Math.Min(buffer.Length / layout.stride, layout.count - processed);
                ReadExact(fs, buffer, rowsThisChunk * layout.stride, plyPath);

                // Parse the block in parallel (read-only buffer); reduce per-segment bounds/sum/count after.
                int segs = Mathf.Clamp(rowsThisChunk / 16384, 1, Environment.ProcessorCount);
                Vector3[] segMin = new Vector3[segs]; Vector3[] segMax = new Vector3[segs]; bool[] segHas = new bool[segs];
                double[] segSum = new double[segs * 3]; int[] segCount = new int[segs];
                Parallel.For(0, segs, s =>
                {
                    int lo = (int)((long)rowsThisChunk * s / segs), hi = (int)((long)rowsThisChunk * (s + 1) / segs);
                    Vector3 mn = Vector3.zero, mx = Vector3.zero; bool has = false; double lsx = 0, lsy = 0, lsz = 0; int cnt = 0;
                    for (int row = lo; row < hi; row++)
                    {
                        int rowOffset = row * layout.stride;
                        Vector3 pos = new Vector3(
                            ReadFloat(buffer, rowOffset + layout.splatOffsets[0]),
                            ReadFloat(buffer, rowOffset + layout.splatOffsets[1]),
                            ReadFloat(buffer, rowOffset + layout.splatOffsets[2]));
                        pos = ApplyHorizonPosition(pos, options);
                        if (cropToBounds && !ShouldIncludePosition(pos, cropBounds)) continue;
                        pos = FlipYPosition(pos);
                        lsx += pos.x; lsy += pos.y; lsz += pos.z; cnt++;
                        if (has) { mn = Vector3.Min(mn, pos); mx = Vector3.Max(mx, pos); } else { mn = pos; mx = pos; has = true; }
                    }
                    segMin[s] = mn; segMax[s] = mx; segHas[s] = has;
                    segSum[s * 3] = lsx; segSum[s * 3 + 1] = lsy; segSum[s * 3 + 2] = lsz; segCount[s] = cnt;
                });
                for (int s = 0; s < segs; s++)
                {
                    if (!segHas[s]) continue;
                    if (hasBounds) { gmin = Vector3.Min(gmin, segMin[s]); gmax = Vector3.Max(gmax, segMax[s]); }
                    else { gmin = segMin[s]; gmax = segMax[s]; hasBounds = true; }
                    sx += segSum[s * 3]; sy += segSum[s * 3 + 1]; sz += segSum[s * 3 + 2]; acceptedCount += segCount[s];
                }

                processed += rowsThisChunk;
                EditorUtility.DisplayProgressBar("Import Gaussian Splat PLY",
                    $"Scanning PLY bounds {processed:N0}/{layout.count:N0} rows, accepted {acceptedCount:N0} splats",
                    0.03f + 0.07f * (processed / (float)layout.count));
            }
            centroid = acceptedCount > 0 ? new Vector3((float)(sx / acceptedCount), (float)(sy / acceptedCount), (float)(sz / acceptedCount)) : Vector3.zero;
            Bounds bounds = new Bounds();
            if (hasBounds) bounds.SetMinMax(gmin, gmax);
            return bounds;
        }

        // Floater-robust normalize scale: stream a radial-distance histogram around the centroid, take the
        // 95th-percentile radius as the true extent (raw bbox is inflated by stray floater splats), and scale
        // so that extent matches the requested target size.
        public static float ComputeNormalizeScale(string plyPath, PLYLayout layout, GaussianSplatImporter.ImportOptions options, Vector3 centroid, Bounds rawBounds, int splatCount)
        {
            float targetSize = options.normalizeTargetSize > 0.0f ? options.normalizeTargetSize : 1.0f;
            float maxRadius = Mathf.Max(1e-6f, rawBounds.size.magnitude * 0.5f);
            const int kBins = 1024;
            long[] hist = new long[kBins];
            using FileStream fs = PLYFileReader.OpenDataStream(plyPath, out _, out _, out _);
            byte[] buffer = new byte[Mathf.Max(layout.stride, StreamedReadChunkBytes / layout.stride * layout.stride)];
            int processed = 0;
            long total = 0;
            bool cropToBounds = options.cropToBounds;
            Bounds cropBounds = cropToBounds ? GetImportCropBounds(options) : default;
            while (processed < layout.count)
            {
                int rowsThisChunk = Math.Min(buffer.Length / layout.stride, layout.count - processed);
                ReadExact(fs, buffer, rowsThisChunk * layout.stride, plyPath);

                int segs = Mathf.Clamp(rowsThisChunk / 16384, 1, Environment.ProcessorCount);
                long[][] segHist = new long[segs][]; long[] segTotal = new long[segs];
                Parallel.For(0, segs, s =>
                {
                    int lo = (int)((long)rowsThisChunk * s / segs), hi = (int)((long)rowsThisChunk * (s + 1) / segs);
                    long[] h = new long[kBins]; long t = 0;
                    for (int row = lo; row < hi; row++)
                    {
                        int rowOffset = row * layout.stride;
                        Vector3 pos = new Vector3(
                            ReadFloat(buffer, rowOffset + layout.splatOffsets[0]),
                            ReadFloat(buffer, rowOffset + layout.splatOffsets[1]),
                            ReadFloat(buffer, rowOffset + layout.splatOffsets[2]));
                        pos = ApplyHorizonPosition(pos, options);
                        if (cropToBounds && !ShouldIncludePosition(pos, cropBounds)) continue;
                        pos = FlipYPosition(pos);
                        float r = (pos - centroid).magnitude;
                        int bin = Mathf.Clamp((int)(r / maxRadius * kBins), 0, kBins - 1);
                        h[bin]++; t++;
                    }
                    segHist[s] = h; segTotal[s] = t;
                });
                for (int s = 0; s < segs; s++)
                {
                    long[] h = segHist[s];
                    for (int b = 0; b < kBins; b++) hist[b] += h[b];
                    total += segTotal[s];
                }
                processed += rowsThisChunk;
            }
            if (total <= 0) return 1.0f;
            long cutoff = (long)(total * 0.95);
            long acc = 0;
            int p95Bin = kBins - 1;
            for (int i = 0; i < kBins; i++) { acc += hist[i]; if (acc >= cutoff) { p95Bin = i; break; } }
            float p95Radius = Mathf.Max(1e-6f, (p95Bin + 1) / (float)kBins * maxRadius);
            return targetSize / (2.0f * p95Radius);
        }

        public static int ResolveStreamedBucketBits(int splatCount)
        {
            int targetBucketCount = Mathf.NextPowerOfTwo(Mathf.Max(1, Mathf.CeilToInt(splatCount / (float)StreamedBucketTargetRecords)));
            int bits = 0;
            while ((1 << bits) < targetBucketCount)
            {
                bits++;
            }
            return Mathf.Clamp(bits, StreamedBucketMinBits, StreamedBucketMaxBits);
        }

        public static void WriteHilbertBuckets(string plyPath, PLYLayout layout, Bounds bounds, GaussianSplatImporter.ImportOptions options, string tempFolder, int bucketBits, string[] bucketPaths, long[] bucketCounts, int shCoeffCount, BigFloatBuffer shStore, Vector3 normalizeCentroid, float normalizeScale)
        {
            int bucketCount = 1 << bucketBits;
            uint sourceCounter = 0;
            FileStream[] streams = new FileStream[bucketCount];
            BucketRecord[][] buffers = new BucketRecord[bucketCount][];
            int[] bufferCounts = new int[bucketCount];
            try
            {
                for (int bucket = 0; bucket < bucketCount; bucket++)
                {
                    bucketPaths[bucket] = Path.Combine(tempFolder, bucket.ToString("D4") + ".bin");
                    streams[bucket] = new FileStream(bucketPaths[bucket], FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
                    buffers[bucket] = new BucketRecord[StreamedBucketBufferRecords];
                }

                using FileStream fs = PLYFileReader.OpenDataStream(plyPath, out _, out _, out _);
                byte[] buffer = new byte[Mathf.Max(layout.stride, StreamedReadChunkBytes / layout.stride * layout.stride)];
                int processed = 0;
                bool cropToBounds = options.cropToBounds;
                Bounds cropBounds = cropToBounds ? GetImportCropBounds(options) : default;
                bool[] accepted = null;
                BucketRecord[] recs = null;
                int[] rowBucket = null;
                while (processed < layout.count)
                {
                    int rowsThisChunk = Math.Min(buffer.Length / layout.stride, layout.count - processed);
                    ReadExact(fs, buffer, rowsThisChunk * layout.stride, plyPath);

                    // Parse + transform + key in parallel over the read block (read-only buffer). The SH side-store
                    // write, sourceIndex assignment and bucket scatter stay sequential below, in row order, so the
                    // bucket files + SH store are byte-identical to the single-threaded path.
                    if (accepted == null || accepted.Length < rowsThisChunk)
                    {
                        accepted = new bool[rowsThisChunk];
                        recs = new BucketRecord[rowsThisChunk];
                        rowBucket = new int[rowsThisChunk];
                    }
                    Array.Clear(accepted, 0, rowsThisChunk);
                    int segs = Mathf.Clamp(rowsThisChunk / 16384, 1, Environment.ProcessorCount);
                    Parallel.For(0, segs, s =>
                    {
                        int lo = (int)((long)rowsThisChunk * s / segs), hi = (int)((long)rowsThisChunk * (s + 1) / segs);
                        for (int row = lo; row < hi; row++)
                        {
                            ImportSplatData splat = ReadLinearizedSplat(buffer, row * layout.stride, layout.splatOffsets);
                            splat = ApplyHorizonSplat(splat, options);
                            if (cropToBounds && !ShouldIncludePosition(splat.pos, cropBounds)) continue;
                            splat = FlipYSplat(splat);
                            if (normalizeScale != 1.0f)
                            {
                                splat.pos = (splat.pos - normalizeCentroid) * normalizeScale + normalizeCentroid;
                                splat.scale *= normalizeScale;
                            }
                            uint key = HilbertKeyForPosition(splat.pos, bounds);
                            recs[row] = new BucketRecord(key, splat);
                            rowBucket[row] = (int)(key >> (30 - bucketBits));
                            accepted[row] = true;
                        }
                    });

                    for (int row = 0; row < rowsThisChunk; row++)
                    {
                        if (!accepted[row]) continue;
                        BucketRecord record = recs[row];
                        int bucket = rowBucket[row];
                        if (shStore != null)
                        {
                            ReadSplatSH(buffer, row * layout.stride, layout.shOffsets, shCoeffCount, shStore, sourceCounter);
                            record.sourceIndex = sourceCounter;
                        }
                        sourceCounter++;
                        BucketRecord[] bucketBuffer = buffers[bucket];
                        int bucketBufferCount = bufferCounts[bucket];
                        bucketBuffer[bucketBufferCount++] = record;
                        bucketCounts[bucket]++;
                        if (bucketBufferCount == bucketBuffer.Length)
                        {
                            WriteBucketRecords(streams[bucket], bucketBuffer, bucketBufferCount);
                            bucketBufferCount = 0;
                        }
                        bufferCounts[bucket] = bucketBufferCount;
                    }
                    processed += rowsThisChunk;
                    EditorUtility.DisplayProgressBar("Import Gaussian Splat PLY",
                        $"Partitioning Hilbert buckets {processed:N0}/{layout.count:N0} rows into {bucketCount:N0} buckets",
                        0.1f + 0.15f * (processed / (float)layout.count));
                }

                for (int bucket = 0; bucket < bucketCount; bucket++)
                {
                    if (bufferCounts[bucket] > 0)
                    {
                        WriteBucketRecords(streams[bucket], buffers[bucket], bufferCounts[bucket]);
                    }
                }
            }
            finally
            {
                for (int bucket = 0; bucket < streams.Length; bucket++)
                {
                    streams[bucket]?.Dispose();
                }
            }
        }

        public static uint HilbertKeyForPosition(Vector3 p, Bounds bounds)
        {
            float nx = bounds.size.x > 1e-8f ? (p.x - bounds.min.x) / bounds.size.x : 0.5f;
            float ny = bounds.size.y > 1e-8f ? (p.y - bounds.min.y) / bounds.size.y : 0.5f;
            float nz = bounds.size.z > 1e-8f ? (p.z - bounds.min.z) / bounds.size.z : 0.5f;
            return Hilbert3D10(nx, ny, nz);
        }

        public static ImportSplatData ReadLinearizedSplat(byte[] buffer, int rowOffset, int[] offsets)
        {
            ImportSplatData splat = new ImportSplatData
            {
                pos = new Vector3(ReadFloat(buffer, rowOffset + offsets[0]), ReadFloat(buffer, rowOffset + offsets[1]), ReadFloat(buffer, rowOffset + offsets[2])),
                dc0 = new Vector3(ReadFloat(buffer, rowOffset + offsets[3]), ReadFloat(buffer, rowOffset + offsets[4]), ReadFloat(buffer, rowOffset + offsets[5])),
                opacity = ReadFloat(buffer, rowOffset + offsets[6]),
                scale = new Vector3(ReadFloat(buffer, rowOffset + offsets[7]), ReadFloat(buffer, rowOffset + offsets[8]), ReadFloat(buffer, rowOffset + offsets[9])),
                rot = new Quaternion(ReadFloat(buffer, rowOffset + offsets[10]), ReadFloat(buffer, rowOffset + offsets[11]), ReadFloat(buffer, rowOffset + offsets[12]), ReadFloat(buffer, rowOffset + offsets[13]))
            };

            splat.rot = NormalizeSwizzleRotation(splat.rot);
            splat.scale = LinearScale(splat.scale);
            splat.dc0 = SH0ToColor(splat.dc0);
            splat.opacity = Sigmoid(splat.opacity);
            return splat;
        }

        // Reads one splat's raw SH (f_rest) into the side-store at its stable sourceIndex. SH is stored raw
        // (unlike pos/scale/rot/color, SH is decoded at eval time, not linearized on import).
        public static void ReadSplatSH(byte[] buffer, int rowOffset, int[] shOffsets, int coeffCount, BigFloatBuffer shStore, uint sourceIndex)
        {
            long baseIdx = (long)sourceIndex * coeffCount * 3;
            for (int coeff = 0; coeff < coeffCount; coeff++)
            {
                float sign = IsYOddSHCoeff(coeff) ? -1.0f : 1.0f;   // y-flip is always baked -> reflect SH parity
                for (int channel = 0; channel < 3; channel++)
                {
                    int off = shOffsets[coeff * 3 + channel];
                    shStore[baseIdx + coeff * 3 + channel] = off >= 0 ? sign * ReadFloat(buffer, rowOffset + off) : 0.0f;
                }
            }
        }

        public static float Sigmoid(float value)
        {
            return 1.0f / (1.0f + Mathf.Exp(-value));
        }

        public static Vector3 SH0ToColor(Vector3 dc0)
        {
            const float SHC0 = 0.2820948f;
            return dc0 * SHC0 + Vector3.one * 0.5f;
        }

        public static Vector3 LinearScale(Vector3 logScale)
        {
            return new Vector3(Mathf.Abs(Mathf.Exp(logScale.x)), Mathf.Abs(Mathf.Exp(logScale.y)), Mathf.Abs(Mathf.Exp(logScale.z)));
        }

        public static Quaternion NormalizeSwizzleRotation(Quaternion wxyz)
        {
            float length = Mathf.Sqrt(wxyz.x * wxyz.x + wxyz.y * wxyz.y + wxyz.z * wxyz.z + wxyz.w * wxyz.w);
            if (length <= 1e-8f)
            {
                return Quaternion.identity;
            }
            float invLength = 1.0f / length;
            return new Quaternion(wxyz.y * invLength, wxyz.z * invLength, wxyz.w * invLength, wxyz.x * invLength);
        }

        public static float ReadFloat(byte[] buffer, int offset)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        public static void ReadExact(FileStream fs, byte[] buffer, int bytesToRead, string filePath)
        {
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = fs.Read(buffer, totalRead, bytesToRead - totalRead);
                if (read <= 0)
                {
                    throw new IOException($"PLY {filePath} read error, expected {bytesToRead} data bytes got {totalRead}");
                }
                totalRead += read;
            }
        }

        public static void WriteBucketRecords(FileStream stream, BucketRecord[] records, int count)
        {
            int byteCount = count * Marshal.SizeOf<BucketRecord>();
            byte[] bytes = new byte[byteCount];
            GCHandle handle = GCHandle.Alloc(records, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(handle.AddrOfPinnedObject(), bytes, 0, byteCount);
            }
            finally
            {
                handle.Free();
            }
            stream.Write(bytes, 0, byteCount);
        }

        public static void ReadBucketRecords(FileStream stream, BucketRecord[] records, int count, byte[] bytes, string path)
        {
            int byteCount = count * Marshal.SizeOf<BucketRecord>();
            ReadExact(stream, bytes, byteCount, path);
            GCHandle handle = GCHandle.Alloc(records, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), byteCount);
            }
            finally
            {
                handle.Free();
            }
        }

        public static void ProcessSortedBucketFile(string path, long count, int prefixBits, string tempFolder, BucketKeyComparer keyComparer, BucketRecordConsumer consumeRecords, int bucketOrdinal, int bucketTotal)
        {
            if (count <= 0)
            {
                return;
            }

            if (count > StreamedBucketTargetRecords && prefixBits < HilbertKeyBits)
            {
                EditorUtility.DisplayProgressBar("Import Gaussian Splat PLY",
                    $"Splitting large Hilbert bucket {bucketOrdinal:N0}/{bucketTotal:N0} ({count:N0} splats, prefix {prefixBits:N0} bits)",
                    0.25f + 0.55f * ((bucketOrdinal - 1) / (float)Mathf.Max(1, bucketTotal)));
                SplitBucketFile(path, count, prefixBits, tempFolder, out string[] childPaths, out long[] childCounts, out int childPrefixBits);
                TryDeleteFile(path);
                for (int i = 0; i < childPaths.Length; i++)
                {
                    ProcessSortedBucketFile(childPaths[i], childCounts[i], childPrefixBits, tempFolder, keyComparer, consumeRecords, bucketOrdinal, bucketTotal);
                }
                return;
            }

            if (count > int.MaxValue)
            {
                throw new InvalidDataException($"LOD bucket '{path}' contains {count:N0} records with identical {prefixBits:N0}-bit Hilbert prefix; this exceeds the importer record limit.");
            }

            if (count > StreamedBucketTargetRecords)
            {
                StreamBucketFileUnsorted(path, count, consumeRecords);
                TryDeleteFile(path);
                return;
            }

            EditorUtility.DisplayProgressBar("Import Gaussian Splat PLY",
                $"Sorting Hilbert bucket {bucketOrdinal:N0}/{bucketTotal:N0} leaf ({count:N0} splats, prefix {prefixBits:N0} bits)",
                0.25f + 0.55f * ((bucketOrdinal - 1) / (float)Mathf.Max(1, bucketTotal)));
            BucketRecord[] records = ReadBucket(path, checked((int)count));
            if (records.Length > 1)
            {
                Array.Sort(records, keyComparer);
            }
            consumeRecords(records, records.Length);
            TryDeleteFile(path);
        }

        public static void SplitBucketFile(string path, long count, int prefixBits, string tempFolder, out string[] childPaths, out long[] childCounts, out int childPrefixBits)
        {
            int splitBits = Mathf.Min(StreamedBucketSplitBits, HilbertKeyBits - prefixBits);
            int childCount = 1 << splitBits;
            childPrefixBits = prefixBits + splitBits;
            childPaths = new string[childCount];
            childCounts = new long[childCount];
            FileStream[] streams = new FileStream[childCount];
            BucketRecord[][] buffers = new BucketRecord[childCount][];
            int[] bufferCounts = new int[childCount];
            try
            {
                string splitId = Guid.NewGuid().ToString("N");
                for (int child = 0; child < childCount; child++)
                {
                    childPaths[child] = Path.Combine(tempFolder, $"split_{childPrefixBits:D2}_{splitId}_{child:D2}.bin");
                    streams[child] = new FileStream(childPaths[child], FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                    buffers[child] = new BucketRecord[StreamedBucketBufferRecords];
                }

                int recordSize = Marshal.SizeOf<BucketRecord>();
                BucketRecord[] readRecords = new BucketRecord[StreamedBucketBufferRecords];
                byte[] readBytes = new byte[StreamedBucketBufferRecords * recordSize];
                using FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                long remaining = count;
                while (remaining > 0)
                {
                    int readCount = (int)Math.Min(readRecords.Length, remaining);
                    ReadBucketRecords(source, readRecords, readCount, readBytes, path);
                    for (int i = 0; i < readCount; i++)
                    {
                        BucketRecord record = readRecords[i];
                        int child = (int)((record.key >> (HilbertKeyBits - childPrefixBits)) & (uint)(childCount - 1));
                        BucketRecord[] writeBuffer = buffers[child];
                        int writeCount = bufferCounts[child];
                        writeBuffer[writeCount++] = record;
                        childCounts[child]++;
                        if (writeCount == writeBuffer.Length)
                        {
                            WriteBucketRecords(streams[child], writeBuffer, writeCount);
                            writeCount = 0;
                        }
                        bufferCounts[child] = writeCount;
                    }
                    remaining -= readCount;
                }

                for (int child = 0; child < childCount; child++)
                {
                    if (bufferCounts[child] > 0)
                    {
                        WriteBucketRecords(streams[child], buffers[child], bufferCounts[child]);
                    }
                }
            }
            finally
            {
                for (int child = 0; child < streams.Length; child++)
                {
                    streams[child]?.Dispose();
                }
            }
        }

        public static void StreamBucketFileUnsorted(string path, long count, BucketRecordConsumer consumeRecords)
        {
            int recordSize = Marshal.SizeOf<BucketRecord>();
            BucketRecord[] records = new BucketRecord[StreamedBucketBufferRecords];
            byte[] bytes = new byte[StreamedBucketBufferRecords * recordSize];
            using FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            long remaining = count;
            while (remaining > 0)
            {
                int readCount = (int)Math.Min(records.Length, remaining);
                ReadBucketRecords(source, records, readCount, bytes, path);
                BucketRecord[] batch = new BucketRecord[readCount];
                Array.Copy(records, batch, readCount);
                consumeRecords(batch, readCount);
                remaining -= readCount;
            }
        }

        public static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // The temp folder cleanup at the end of import will remove any leftover bucket files.
            }
        }

        public static BucketRecord[] ReadBucket(string path, int count)
        {
            int recordSize = Marshal.SizeOf<BucketRecord>();
            long byteCountLong = (long)count * recordSize;
            if (byteCountLong > int.MaxValue)
            {
                throw new InvalidDataException($"LOD bucket '{path}' has {count:N0} records ({byteCountLong:N0} bytes), exceeding the in-memory bucket limit.");
            }
            int byteCount = (int)byteCountLong;
            FileInfo fileInfo = new FileInfo(path);
            if (fileInfo.Length != byteCountLong)
            {
                throw new IOException($"LOD bucket '{path}' expected {byteCountLong} bytes but found {fileInfo.Length}.");
            }
            BucketRecord[] records = new BucketRecord[count];
            byte[] buffer = new byte[Math.Min(16 * 1024 * 1024, Math.Max(recordSize, byteCount))];
            GCHandle handle = GCHandle.Alloc(records, GCHandleType.Pinned);
            try
            {
                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                IntPtr basePtr = handle.AddrOfPinnedObject();
                int copied = 0;
                while (copied < byteCount)
                {
                    int readSize = Math.Min(buffer.Length, byteCount - copied);
                    ReadExact(stream, buffer, readSize, path);
                    Marshal.Copy(buffer, 0, IntPtr.Add(basePtr, copied), readSize);
                    copied += readSize;
                }
            }
            finally
            {
                handle.Free();
            }
            return records;
        }

        public static uint Hilbert3D10(float nx, float ny, float nz)
        {
            uint[] axes =
            {
                (uint)Mathf.Clamp(Mathf.RoundToInt(nx * 1023.0f), 0, 1023),
                (uint)Mathf.Clamp(Mathf.RoundToInt(ny * 1023.0f), 0, 1023),
                (uint)Mathf.Clamp(Mathf.RoundToInt(nz * 1023.0f), 0, 1023)
            };
            AxesToHilbertTranspose(axes, 10);
            uint index = 0;
            for (int bit = 9; bit >= 0; bit--)
            {
                index = (index << 1) | ((axes[0] >> bit) & 1u);
                index = (index << 1) | ((axes[1] >> bit) & 1u);
                index = (index << 1) | ((axes[2] >> bit) & 1u);
            }
            return index;
        }

        public static void AxesToHilbertTranspose(uint[] axes, int bits)
        {
            int n = axes.Length;
            uint m = 1u << (bits - 1);
            for (uint q = m; q > 1; q >>= 1)
            {
                uint p = q - 1;
                for (int i = 0; i < n; i++)
                {
                    if ((axes[i] & q) != 0)
                    {
                        axes[0] ^= p;
                    }
                    else
                    {
                        uint t = (axes[0] ^ axes[i]) & p;
                        axes[0] ^= t;
                        axes[i] ^= t;
                    }
                }
            }

            for (int i = 1; i < n; i++)
            {
                axes[i] ^= axes[i - 1];
            }

            uint t2 = 0;
            for (uint q = m; q > 1; q >>= 1)
            {
                if ((axes[n - 1] & q) != 0)
                {
                    t2 ^= q - 1;
                }
            }

            for (int i = 0; i < n; i++)
            {
                axes[i] ^= t2;
            }
        }
    }
}
#endif
