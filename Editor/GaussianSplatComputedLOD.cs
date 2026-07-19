#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using static GaussianSplatting.StreamedSplatReader;
using static GaussianSplatting.GaussianSplatLODImporter;

namespace GaussianSplatting
{
    // GPU computed-LOD generation (per-chunk k-means cluster merge pyramid) for the chunked importer.
    // This file is the strippable seam of the computed-LOD import feature: without it the importer still
    // performs chunked (Normal / full-detail LOD0) imports, but GaussianSplatLODImporter.computedLodBackend
    // stays null and the computed-LOD option is unavailable.
    static class GaussianSplatComputedLOD
    {
        const int ComputedLodMinClusterCount = 1;
        const int ComputedLodKMeansIterations = 8;
        const int ComputedLodKMeansDimensions = 13;
        const int ComputedLodGpuMinBatchChunks = 128;
        const int ComputedLodGpuMaxBatchChunks = 2048;
        const int ComputedLodGpuTargetBatchSplats = 2 * 1024 * 1024;
        const float ComputedLodFeatureAtomicScale = 4096.0f;
        const string ComputedLodComputeShaderName = "GSLODCompute";

        [InitializeOnLoadMethod]
        static void RegisterBackend()
        {
            GaussianSplatLODImporter.computedLodBackend = new Backend();
        }

        sealed class Backend : IComputedLodBackend
        {
            public int ResolveGpuBatchChunks(int chunkSize, int shCoeffCount)
            {
                return ResolveComputedLodGpuBatchChunks(chunkSize, shCoeffCount);
            }

            public ComputeShader LoadComputeShader()
            {
                return LoadComputedLodShader();
            }

            public int ResampledLod0SplatCountForChunk(int sourceCount, int resamplePercent)
            {
                return ComputeResampledLod0SplatCountForChunk(sourceCount, resamplePercent);
            }

            public int EstimateStoredSplatCount(int splatCount, int chunkSize, int resamplePercent, int reusePercent)
            {
                return EstimateStoredComputedSplatCount(splatCount, chunkSize, resamplePercent, reusePercent);
            }

            public void QueueChunk(List<PendingChunk> pendingChunks, int chunkIndex, BucketRecord[] chunkBuffer, int chunkCount, BucketImportanceComparer importanceComparer, StreamedSHContext sh)
            {
                QueueStreamedChunk(pendingChunks, chunkIndex, chunkBuffer, chunkCount, importanceComparer, sh);
            }

            public void FlushBatch(ComputeShader shader, List<PendingChunk> pendingChunks, int chunkSize, StreamedSetWriter writer, int resamplePercent, int reusePercent, StreamedSHContext sh)
            {
                FlushComputedLodChunkBatch(shader, pendingChunks, chunkSize, writer, resamplePercent, reusePercent, sh);
            }
        }

        static int ComputeLodOutputCount(int lod0Count, int level)
        {
            if (level <= 0)
            {
                return Mathf.Max(0, lod0Count);
            }

            int divisor = 1 << Mathf.Min(30, level);
            return Mathf.Clamp(Mathf.FloorToInt(lod0Count / (float)divisor + 0.5f), 0, lod0Count);
        }

        static int ComputeLodClusterCount(int lod0Count, int level, int reusePercent)
        {
            if (level <= 0)
            {
                return Mathf.Max(0, lod0Count);
            }

            int outputCount = ComputeLodOutputCount(lod0Count, level);
            int reuseCount = Mathf.Clamp(Mathf.FloorToInt(outputCount * (NormalizeLodReusePercent(reusePercent) / 100.0f) + 0.5f), 0, outputCount);
            int clusterCount = outputCount - reuseCount;
            if (clusterCount < ComputedLodMinClusterCount || clusterCount >= lod0Count)
            {
                return 0;
            }
            return clusterCount;
        }

        static int ComputeLodReuseCount(int lod0Count, int level, int reusePercent)
        {
            if (level <= 0)
            {
                return Mathf.Max(0, lod0Count);
            }

            int clusterCount = ComputeLodClusterCount(lod0Count, level, reusePercent);
            if (clusterCount <= 0)
            {
                return lod0Count;
            }

            int outputCount = ComputeLodOutputCount(lod0Count, level);
            return Mathf.Clamp(outputCount - clusterCount, 0, lod0Count);
        }

        static int ComputeResampledLod0SplatCountForChunk(int sourceCount, int resamplePercent)
        {
            sourceCount = Mathf.Max(0, sourceCount);
            if (sourceCount <= 0)
            {
                return 0;
            }

            resamplePercent = NormalizeLodResamplePercent(resamplePercent);
            if (resamplePercent >= DefaultLodResamplePercent)
            {
                return sourceCount;
            }

            return Mathf.Clamp(Mathf.RoundToInt(sourceCount * (resamplePercent / 100.0f)), 1, sourceCount);
        }

        static int ComputeStoredSplatCountForChunk(int lod0Count, int reusePercent)
        {
            int storedCount = Mathf.Max(0, lod0Count);
            for (int level = 1; level < 30; level++)
            {
                int clusterCount = ComputeLodClusterCount(lod0Count, level, reusePercent);
                if (clusterCount <= 0)
                {
                    break;
                }
                storedCount += clusterCount;
            }
            return storedCount;
        }

        static int EstimateStoredComputedSplatCount(int splatCount, int chunkSize, int resamplePercent, int reusePercent)
        {
            splatCount = Mathf.Max(0, splatCount);
            chunkSize = Mathf.Max(1, chunkSize);
            resamplePercent = NormalizeLodResamplePercent(resamplePercent);
            reusePercent = NormalizeLodReusePercent(reusePercent);
            int total = 0;
            for (int offset = 0; offset < splatCount; offset += chunkSize)
            {
                int sourceCount = Mathf.Min(chunkSize, splatCount - offset);
                int lod0Count = ComputeResampledLod0SplatCountForChunk(sourceCount, resamplePercent);
                total += ComputeStoredSplatCountForChunk(lod0Count, reusePercent);
            }
            return total;
        }

        static int ResolveComputedLodGpuBatchChunks(int chunkSize, int shCoeffCount)
        {
            chunkSize = Mathf.Max(1, chunkSize);
            // SH adds coeffCount*3 floats/splat to the per-batch GPU buffers; shrink the batch so peak memory
            // stays comparable to the no-SH case (BucketRecord ~64 bytes/splat).
            int targetSplats = ComputedLodGpuTargetBatchSplats;
            if (shCoeffCount > 0) targetSplats = Mathf.Max(1, (int)((long)ComputedLodGpuTargetBatchSplats * 64 / (64 + shCoeffCount * 3 * 4)));
            int chunks = Mathf.Max(1, targetSplats / chunkSize);
            return Mathf.Clamp(chunks, ComputedLodGpuMinBatchChunks, ComputedLodGpuMaxBatchChunks);
        }

        static ComputeShader LoadComputedLodShader()
        {
            string[] guids = AssetDatabase.FindAssets(ComputedLodComputeShaderName + " t:ComputeShader");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (shader != null && shader.name == ComputedLodComputeShaderName)
                {
                    return shader;
                }
            }
            throw new FileNotFoundException($"Could not find required compute shader '{ComputedLodComputeShaderName}'. Computed LOD splat import requires this shader.");
        }

        static void QueueStreamedChunk(List<PendingChunk> pendingChunks, int chunkIndex, BucketRecord[] chunkBuffer, int chunkCount, BucketImportanceComparer importanceComparer, StreamedSHContext sh)
        {
            ComputeChunkImportance(chunkBuffer, chunkCount);
            Array.Sort(chunkBuffer, 0, chunkCount, importanceComparer);
            Bounds chunkBounds = new Bounds(PositionOf(chunkBuffer[0]), Vector3.zero);
            for (int i = 0; i < chunkCount; i++)
            {
                chunkBounds.Encapsulate(PositionOf(chunkBuffer[i]));
            }

            BucketRecord[] records = new BucketRecord[chunkCount];
            Array.Copy(chunkBuffer, records, chunkCount);
            // Gather raw SH for this chunk (post-sort, by each record's stable sourceIndex) so it travels with
            // the records through the GPU k-means stages.
            float[] chunkSH = null;
            if (sh != null && sh.coeffCount > 0 && sh.store != null)
            {
                int per = sh.coeffCount * 3;
                chunkSH = new float[(long)chunkCount * per];
                for (int i = 0; i < chunkCount; i++)
                {
                    long b = sh.BaseFor(records[i].sourceIndex);
                    if (b >= 0) sh.store.CopyTo(b, chunkSH, i * per, per);
                }
            }
            pendingChunks.Add(new PendingChunk
            {
                chunkIndex = chunkIndex,
                count = chunkCount,
                records = records,
                sh = chunkSH,
                bounds = chunkBounds,
                sizeMagnitude = chunkBounds.size.magnitude
            });
        }

        static void FlushComputedLodChunkBatch(ComputeShader shader, List<PendingChunk> pendingChunks, int chunkSize, StreamedSetWriter writer, int resamplePercent, int reusePercent, StreamedSHContext sh)
        {
            if (pendingChunks.Count == 0)
            {
                return;
            }

            int firstChunk = pendingChunks[0].chunkIndex;
            int lastChunk = pendingChunks[pendingChunks.Count - 1].chunkIndex;
            int totalChunkCount = lastChunk + 1; // progress estimate only; total chunk count is not known up front
            EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD",
                $"Computing LOD splats for chunks {firstChunk + 1:N0}-{lastChunk + 1:N0} ({pendingChunks.Count:N0} chunks)", 0.25f + 0.55f * (firstChunk / (float)Mathf.Max(1, totalChunkCount)));

            int shCoeffCount = sh != null ? sh.coeffCount : 0;
            bool didResample = ResamplePendingChunksGpu(shader, pendingChunks, chunkSize, resamplePercent, totalChunkCount, shCoeffCount);
            List<float[]>[] mergedSHByChunk;
            List<BucketRecord[]>[] mergedByChunk = ComputeMergedLodSplatsGpu(shader, pendingChunks, chunkSize, totalChunkCount, reusePercent, didResample ? 0.35f : 0.0f, didResample ? 0.65f : 1.0f, shCoeffCount, out mergedSHByChunk);
            for (int i = 0; i < pendingChunks.Count; i++)
            {
                writer.WritePrepared(pendingChunks[i], mergedByChunk[i], mergedSHByChunk[i]);
            }
            pendingChunks.Clear();
        }

        static bool ResamplePendingChunksGpu(ComputeShader shader, List<PendingChunk> pendingChunks, int chunkSize, int resamplePercent, int totalChunkCount, int shCoeffCount)
        {
            resamplePercent = NormalizeLodResamplePercent(resamplePercent);
            if (resamplePercent >= DefaultLodResamplePercent || pendingChunks.Count == 0)
            {
                return false;
            }

            int batchChunkCount = pendingChunks.Count;
            int maxClusterCount = 0;
            int maxSourceCount = 0;
            for (int chunk = 0; chunk < batchChunkCount; chunk++)
            {
                int sourceCount = pendingChunks[chunk].count;
                int targetCount = ComputeResampledLod0SplatCountForChunk(sourceCount, resamplePercent);
                if (targetCount >= sourceCount)
                {
                    continue;
                }

                maxClusterCount = Mathf.Max(maxClusterCount, targetCount);
                maxSourceCount = Mathf.Max(maxSourceCount, sourceCount);
            }

            if (maxClusterCount <= 0 || maxSourceCount <= 0)
            {
                return false;
            }

            BucketRecord[] splatData = new BucketRecord[batchChunkCount * chunkSize];
            int[] chunkCounts = new int[batchChunkCount];
            for (int chunk = 0; chunk < batchChunkCount; chunk++)
            {
                PendingChunk pending = pendingChunks[chunk];
                chunkCounts[chunk] = pending.count;
                Array.Copy(pending.records, 0, splatData, chunk * chunkSize, pending.count);
            }

            int splatStride = Marshal.SizeOf<BucketRecord>();
            int shPer = shCoeffCount * 3;
            using ComputeBuffer splatBuffer = new ComputeBuffer(splatData.Length, splatStride);
            using ComputeBuffer chunkCountBuffer = new ComputeBuffer(batchChunkCount, sizeof(int));
            splatBuffer.SetData(splatData);
            chunkCountBuffer.SetData(chunkCounts);
            float[] splatSHData = new float[shCoeffCount > 0 ? (long)batchChunkCount * chunkSize * shPer : 1];
            if (shCoeffCount > 0)
            {
                for (int chunk = 0; chunk < batchChunkCount; chunk++)
                {
                    PendingChunk pending = pendingChunks[chunk];
                    if (pending.sh != null) Array.Copy(pending.sh, 0, splatSHData, (long)chunk * chunkSize * shPer, (long)pending.count * shPer);
                }
            }
            using ComputeBuffer splatSHBuffer = new ComputeBuffer(splatSHData.Length, sizeof(float));
            splatSHBuffer.SetData(splatSHData);

            int clearKernel = shader.FindKernel("ClearKMeansAccum");
            int normKernel = shader.FindKernel("ComputeChunkNorms");
            int initKernel = shader.FindKernel("InitializeCenters");
            int assignKernel = shader.FindKernel("AssignAndAccumulate");
            int normalizeKernel = shader.FindKernel("NormalizeCenters");
            int mergeKernel = shader.FindKernel("MergeClusters");

            shader.SetInt("_ChunkSize", chunkSize);
            shader.SetInt("_BatchChunkCount", batchChunkCount);
            shader.SetInt("_ShCoeffCount", shCoeffCount);
            shader.SetFloat("_FeatureAtomicScale", ComputedLodFeatureAtomicScale);

            int maxClusterSlotCount = batchChunkCount * maxClusterCount;
            BucketRecord[] mergedData = new BucketRecord[maxClusterSlotCount];
            float[] mergedSHData = new float[shCoeffCount > 0 ? (long)maxClusterSlotCount * shPer : 1];
            using ComputeBuffer chunkNormBuffer = new ComputeBuffer(batchChunkCount, sizeof(float) * 4);
            using ComputeBuffer centerBuffer = new ComputeBuffer(Mathf.Max(1, maxClusterSlotCount * ComputedLodKMeansDimensions), sizeof(float));
            using ComputeBuffer assignmentBuffer = new ComputeBuffer(batchChunkCount * chunkSize, sizeof(int));
            using ComputeBuffer featureSumBuffer = new ComputeBuffer(maxClusterSlotCount * ComputedLodKMeansDimensions, sizeof(int));
            using ComputeBuffer clusterCountBuffer = new ComputeBuffer(maxClusterSlotCount, sizeof(int));
            using ComputeBuffer mergedBuffer = new ComputeBuffer(maxClusterSlotCount, splatStride);
            using ComputeBuffer mergedSHBuffer = new ComputeBuffer(mergedSHData.Length, sizeof(float));
            SetComputedLodCommonBuffers(shader, clearKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, normKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, initKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, assignKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, normalizeKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, mergeKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            shader.SetBuffer(mergeKernel, "_SplatSH", splatSHBuffer);
            shader.SetBuffer(mergeKernel, "_MergedSH", mergedSHBuffer);

            int firstChunk = pendingChunks[0].chunkIndex;
            int lastChunk = pendingChunks[batchChunkCount - 1].chunkIndex;
            float progressStart = 0.25f + 0.55f * (firstChunk / (float)Mathf.Max(1, totalChunkCount));
            float progressSpan = 0.55f * ((lastChunk - firstChunk + 1) / (float)Mathf.Max(1, totalChunkCount)) * 0.35f;
            RunKMeansMergeGpu(shader, clearKernel, normKernel, initKernel, assignKernel, normalizeKernel, mergeKernel,
                batchChunkCount, maxClusterCount, maxSourceCount, 0, DefaultLodReusePercent, 0, resamplePercent, 0,
                $"Resampling chunks {firstChunk + 1:N0}-{lastChunk + 1:N0}/{totalChunkCount:N0} to {resamplePercent}%",
                progressStart, progressSpan);

            mergedBuffer.GetData(mergedData, 0, 0, maxClusterSlotCount);
            if (shCoeffCount > 0) mergedSHBuffer.GetData(mergedSHData, 0, 0, (int)Mathf.Min(mergedSHData.Length, (long)maxClusterSlotCount * shPer));
            for (int chunk = 0; chunk < batchChunkCount; chunk++)
            {
                PendingChunk pending = pendingChunks[chunk];
                int targetCount = ComputeResampledLod0SplatCountForChunk(pending.count, resamplePercent);
                if (targetCount >= pending.count)
                {
                    continue;
                }

                BucketRecord[] records = new BucketRecord[targetCount];
                Array.Copy(mergedData, chunk * maxClusterCount, records, 0, targetCount);
                ComputeChunkImportance(records, targetCount);
                // Index-sort by importance so the parallel SH array can be reordered to match.
                int[] order = new int[targetCount];
                for (int i = 0; i < targetCount; i++) order[i] = i;
                Array.Sort(order, (a, b) => records[b].importance.CompareTo(records[a].importance));
                BucketRecord[] sortedRecords = new BucketRecord[targetCount];
                float[] sortedSH = shCoeffCount > 0 ? new float[(long)targetCount * shPer] : null;
                for (int i = 0; i < targetCount; i++)
                {
                    sortedRecords[i] = records[order[i]];
                    if (shCoeffCount > 0) Array.Copy(mergedSHData, ((long)chunk * maxClusterCount + order[i]) * shPer, sortedSH, (long)i * shPer, shPer);
                }
                pending.records = sortedRecords;
                pending.sh = sortedSH;
                pending.count = targetCount;
            }
            return true;
        }

        static void RunKMeansMergeGpu(ComputeShader shader, int clearKernel, int normKernel, int initKernel, int assignKernel, int normalizeKernel, int mergeKernel,
            int batchChunkCount, int maxClusterCount, int maxSourceCount, int level, int reusePercent, int clusterCountOverride, int clusterCountPercentOverride, int sourceStartOverride,
            string progressLabel, float progressStart, float progressSpan)
        {
            int clusterSlotCount = batchChunkCount * maxClusterCount;
            shader.SetInt("_Level", level);
            shader.SetInt("_MaxClusterCount", maxClusterCount);
            shader.SetInt("_ReusePercent", NormalizeLodReusePercent(reusePercent));
            shader.SetInt("_ClusterCountOverride", clusterCountOverride);
            shader.SetInt("_ClusterCountPercentOverride", clusterCountPercentOverride);
            shader.SetInt("_SourceStartOverride", sourceStartOverride);
            shader.Dispatch(normKernel, batchChunkCount, 1, 1);
            shader.Dispatch(initKernel, Mathf.Max(1, Mathf.CeilToInt(maxClusterCount / 256.0f)), batchChunkCount, 1);

            for (int iteration = 0; iteration < ComputedLodKMeansIterations; iteration++)
            {
                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD",
                    $"{progressLabel}, k-means iteration {iteration + 1:N0}/{ComputedLodKMeansIterations:N0}, {maxClusterCount:N0} max clusters",
                    progressStart + progressSpan * ((iteration + 0.5f) / (ComputedLodKMeansIterations + 1.0f)));
                Dispatch1D(shader, clearKernel, clusterSlotCount);
                shader.Dispatch(assignKernel, Mathf.Max(1, Mathf.CeilToInt(maxSourceCount / 256.0f)), batchChunkCount, 1);
                Dispatch1D(shader, normalizeKernel, clusterSlotCount);
            }

            EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD",
                $"{progressLabel}, merging",
                progressStart + progressSpan * (ComputedLodKMeansIterations / (ComputedLodKMeansIterations + 1.0f)));
            shader.Dispatch(mergeKernel, maxClusterCount, batchChunkCount, 1);
        }

        static List<BucketRecord[]>[] ComputeMergedLodSplatsGpu(ComputeShader shader, List<PendingChunk> pendingChunks, int chunkSize, int totalChunkCount, int reusePercent, float batchProgressOffset, float batchProgressScale, int shCoeffCount, out List<float[]>[] mergedSHByChunk)
        {
            int batchChunkCount = pendingChunks.Count;
            int shPer = shCoeffCount * 3;
            BucketRecord[] splatData = new BucketRecord[batchChunkCount * chunkSize];
            float[] splatSHData = new float[shCoeffCount > 0 ? (long)batchChunkCount * chunkSize * shPer : 1];
            int[] chunkCounts = new int[batchChunkCount];
            List<BucketRecord[]>[] mergedByChunk = new List<BucketRecord[]>[batchChunkCount];
            mergedSHByChunk = new List<float[]>[batchChunkCount];
            for (int chunk = 0; chunk < batchChunkCount; chunk++)
            {
                PendingChunk pending = pendingChunks[chunk];
                chunkCounts[chunk] = pending.count;
                Array.Copy(pending.records, 0, splatData, chunk * chunkSize, pending.count);
                if (shCoeffCount > 0 && pending.sh != null) Array.Copy(pending.sh, 0, splatSHData, (long)chunk * chunkSize * shPer, (long)pending.count * shPer);
                mergedByChunk[chunk] = new List<BucketRecord[]>();
                mergedSHByChunk[chunk] = new List<float[]>();
            }

            int splatStride = Marshal.SizeOf<BucketRecord>();
            using ComputeBuffer splatBuffer = new ComputeBuffer(splatData.Length, splatStride);
            using ComputeBuffer chunkCountBuffer = new ComputeBuffer(batchChunkCount, sizeof(int));
            using ComputeBuffer splatSHBuffer = new ComputeBuffer(splatSHData.Length, sizeof(float));
            splatBuffer.SetData(splatData);
            chunkCountBuffer.SetData(chunkCounts);
            splatSHBuffer.SetData(splatSHData);

            int clearKernel = shader.FindKernel("ClearKMeansAccum");
            int normKernel = shader.FindKernel("ComputeChunkNorms");
            int initKernel = shader.FindKernel("InitializeCenters");
            int assignKernel = shader.FindKernel("AssignAndAccumulate");
            int normalizeKernel = shader.FindKernel("NormalizeCenters");
            int mergeKernel = shader.FindKernel("MergeClusters");

            shader.SetInt("_ChunkSize", chunkSize);
            shader.SetInt("_BatchChunkCount", batchChunkCount);
            shader.SetInt("_ShCoeffCount", shCoeffCount);
            shader.SetFloat("_FeatureAtomicScale", ComputedLodFeatureAtomicScale);
            SetComputedLodCommonBuffers(shader, clearKernel, splatBuffer, chunkCountBuffer, null, null, null, null, null, null);
            SetComputedLodCommonBuffers(shader, normKernel, splatBuffer, chunkCountBuffer, null, null, null, null, null, null);
            SetComputedLodCommonBuffers(shader, initKernel, splatBuffer, chunkCountBuffer, null, null, null, null, null, null);
            SetComputedLodCommonBuffers(shader, assignKernel, splatBuffer, chunkCountBuffer, null, null, null, null, null, null);
            SetComputedLodCommonBuffers(shader, normalizeKernel, splatBuffer, chunkCountBuffer, null, null, null, null, null, null);
            SetComputedLodCommonBuffers(shader, mergeKernel, splatBuffer, chunkCountBuffer, null, null, null, null, null, null);

            int maxClusterCapacity = 0;
            int lodLevelCount = 0;
            for (int level = 1; level < 30; level++)
            {
                int levelMaxClusterCount = 0;
                for (int chunk = 0; chunk < batchChunkCount; chunk++)
                {
                    levelMaxClusterCount = Mathf.Max(levelMaxClusterCount, ComputeLodClusterCount(chunkCounts[chunk], level, reusePercent));
                }
                if (levelMaxClusterCount <= 0)
                {
                    break;
                }
                maxClusterCapacity = Mathf.Max(maxClusterCapacity, levelMaxClusterCount);
                lodLevelCount++;
            }
            if (maxClusterCapacity <= 0)
            {
                return mergedByChunk;
            }

            int maxClusterSlotCount = batchChunkCount * maxClusterCapacity;
            BucketRecord[] mergedData = new BucketRecord[maxClusterSlotCount];
            float[] mergedSHData = new float[shCoeffCount > 0 ? (long)maxClusterSlotCount * shPer : 1];
            using ComputeBuffer chunkNormBuffer = new ComputeBuffer(batchChunkCount, sizeof(float) * 4);
            using ComputeBuffer centerBuffer = new ComputeBuffer(Mathf.Max(1, maxClusterSlotCount * ComputedLodKMeansDimensions), sizeof(float));
            using ComputeBuffer assignmentBuffer = new ComputeBuffer(batchChunkCount * chunkSize, sizeof(int));
            using ComputeBuffer featureSumBuffer = new ComputeBuffer(maxClusterSlotCount * ComputedLodKMeansDimensions, sizeof(int));
            using ComputeBuffer clusterCountBuffer = new ComputeBuffer(maxClusterSlotCount, sizeof(int));
            using ComputeBuffer mergedBuffer = new ComputeBuffer(maxClusterSlotCount, splatStride);
            using ComputeBuffer mergedSHBuffer = new ComputeBuffer(mergedSHData.Length, sizeof(float));
            SetComputedLodCommonBuffers(shader, clearKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, normKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, initKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, assignKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, normalizeKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            SetComputedLodCommonBuffers(shader, mergeKernel, splatBuffer, chunkCountBuffer, chunkNormBuffer, centerBuffer, assignmentBuffer, featureSumBuffer, clusterCountBuffer, mergedBuffer);
            shader.SetBuffer(mergeKernel, "_SplatSH", splatSHBuffer);
            shader.SetBuffer(mergeKernel, "_MergedSH", mergedSHBuffer);

            int firstChunk = pendingChunks[0].chunkIndex;
            int lastChunk = pendingChunks[batchChunkCount - 1].chunkIndex;
            float fullBatchProgressStart = 0.25f + 0.55f * (firstChunk / (float)Mathf.Max(1, totalChunkCount));
            float fullBatchProgressSpan = 0.55f * ((lastChunk - firstChunk + 1) / (float)Mathf.Max(1, totalChunkCount));
            float batchProgressStart = fullBatchProgressStart + fullBatchProgressSpan * Mathf.Clamp01(batchProgressOffset);
            float batchProgressSpan = fullBatchProgressSpan * Mathf.Clamp01(batchProgressScale);
            int lodLevelOrdinal = 0;
            for (int level = 1; level < 30; level++)
            {
                int maxClusterCount = 0;
                int maxLowerCount = 0;
                for (int chunk = 0; chunk < batchChunkCount; chunk++)
                {
                    int clusterCount = ComputeLodClusterCount(chunkCounts[chunk], level, reusePercent);
                    maxClusterCount = Mathf.Max(maxClusterCount, clusterCount);
                    if (clusterCount > 0)
                    {
                        maxLowerCount = Mathf.Max(maxLowerCount, chunkCounts[chunk] - ComputeLodReuseCount(chunkCounts[chunk], level, reusePercent));
                    }
                }
                if (maxClusterCount <= 0)
                {
                    break;
                }
                float levelProgressStart = batchProgressStart + batchProgressSpan * (lodLevelOrdinal / (float)Mathf.Max(1, lodLevelCount));
                float levelProgressSpan = batchProgressSpan / Mathf.Max(1, lodLevelCount);
                EditorUtility.DisplayProgressBar("Import Gaussian Splat LOD",
                    $"Computing LOD splats chunks {firstChunk + 1:N0}-{lastChunk + 1:N0}/{totalChunkCount:N0}, LOD {lodLevelOrdinal + 1:N0}/{lodLevelCount:N0}, {maxClusterCount:N0} max clusters",
                    levelProgressStart);

                int clusterSlotCount = batchChunkCount * maxClusterCount;
                RunKMeansMergeGpu(shader, clearKernel, normKernel, initKernel, assignKernel, normalizeKernel, mergeKernel,
                    batchChunkCount, maxClusterCount, maxLowerCount, level, reusePercent, 0, 0, 0,
                    $"LOD chunks {firstChunk + 1:N0}-{lastChunk + 1:N0}/{totalChunkCount:N0}, level {lodLevelOrdinal + 1:N0}/{lodLevelCount:N0}",
                    levelProgressStart, levelProgressSpan);
                mergedBuffer.GetData(mergedData, 0, 0, clusterSlotCount);
                if (shCoeffCount > 0) mergedSHBuffer.GetData(mergedSHData, 0, 0, (int)Mathf.Min(mergedSHData.Length, (long)clusterSlotCount * shPer));
                for (int chunk = 0; chunk < batchChunkCount; chunk++)
                {
                    int clusterCount = ComputeLodClusterCount(chunkCounts[chunk], level, reusePercent);
                    if (clusterCount <= 0)
                    {
                        continue;
                    }
                    BucketRecord[] levelRecords = new BucketRecord[clusterCount];
                    Array.Copy(mergedData, chunk * maxClusterCount, levelRecords, 0, clusterCount);
                    mergedByChunk[chunk].Add(levelRecords);
                    // Merged levels are stored in cluster order (no importance sort) so SH copies directly.
                    if (shCoeffCount > 0)
                    {
                        float[] levelSH = new float[(long)clusterCount * shPer];
                        Array.Copy(mergedSHData, (long)chunk * maxClusterCount * shPer, levelSH, 0, (long)clusterCount * shPer);
                        mergedSHByChunk[chunk].Add(levelSH);
                    }
                    else
                    {
                        mergedSHByChunk[chunk].Add(null);
                    }
                }
                lodLevelOrdinal++;
            }

            return mergedByChunk;
        }

        static void SetComputedLodCommonBuffers(ComputeShader shader, int kernel, ComputeBuffer splats, ComputeBuffer chunkCounts, ComputeBuffer chunkNorms, ComputeBuffer centers,
            ComputeBuffer assignments, ComputeBuffer featureSums, ComputeBuffer clusterCounts, ComputeBuffer merged)
        {
            if (splats != null) shader.SetBuffer(kernel, "_Splats", splats);
            if (chunkCounts != null) shader.SetBuffer(kernel, "_ChunkCounts", chunkCounts);
            if (chunkNorms != null) shader.SetBuffer(kernel, "_ChunkNorms", chunkNorms);
            if (centers != null) shader.SetBuffer(kernel, "_Centers", centers);
            if (assignments != null) shader.SetBuffer(kernel, "_Assignments", assignments);
            if (featureSums != null) shader.SetBuffer(kernel, "_FeatureSums", featureSums);
            if (clusterCounts != null) shader.SetBuffer(kernel, "_ClusterCounts", clusterCounts);
            if (merged != null) shader.SetBuffer(kernel, "_Merged", merged);
        }

        static void Dispatch1D(ComputeShader shader, int kernel, int itemCount)
        {
            shader.Dispatch(kernel, Mathf.Max(1, Mathf.CeilToInt(itemCount / 256.0f)), 1, 1);
        }
    }
}
#endif
