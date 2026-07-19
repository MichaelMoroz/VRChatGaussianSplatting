#if UNITY_EDITOR
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Editor
{
    // Rasterizes a collider depth-weight accumulation from the ORIGINAL packed source splats (no fused set,
    // no GPU radix sort, no chunk cap). Read back the packed positions + chunk table, decode + CPU-sort
    // every LOD0 splat inside the box front-to-back, upload the order as a StructuredBuffer<uint>, then
    // raster+blend all of them (batched) through the source-load depth shader (in-shader binary-search
    // chunk decode). The caller resolves the weighted-average height from the accum RT.
    static class ColliderSourceBake
    {
        const string SourceShaderName = "Hidden/GaussianSplatting/HeightmapColliderDepthSource";

        // Reads the source, CPU-sorts the in-box LOD0 splats front-to-back, and rasters them into `accum`
        // (cleared here). accum.r = premultiplied depth-norm, accum.a = accumulated opacity; the caller
        // resolves height = boxHeight * (1 - r/a). accum.width is the (super)sampled raster resolution.
        public static void RasterSourceIntoAccum(GaussianSplatObject target, Matrix4x4 boxToWorld, Vector3 boxSize,
            float opacityMultiplier, float alphaCull, RenderTexture accum, out int inBoxCount, out string timing)
        {
            inBoxCount = 0;
            var sw = Stopwatch.StartNew();

            if (target == null || !target.IsRenderable())
                throw new InvalidOperationException("Source splat is not renderable.");
            if (target.GetFileCount() != 1)
                throw new InvalidOperationException($"Multi-file sources not yet supported (files={target.GetFileCount()}).");
            Shader shader = Shader.Find(SourceShaderName);
            if (shader == null)
                throw new InvalidOperationException($"Missing shader {SourceShaderName}.");

            Texture posTex = target.GetPositions(0);
            Texture sclTex = target.GetScales(0);
            Texture rotTex = target.GetRotations(0);
            Texture colTex = target.GetColors(0);
            Texture2D chunkRange = target.chunkRangeTexture;
            Texture2D chunkMin = target.chunkBoundsMinTexture;
            Texture2D chunkMax = target.chunkBoundsMaxTexture;
            int chunkCount = target.GetChunkCount();
            int chunkWidth = Mathf.RoundToInt(target.chunkTextureLayout.x);
            int chunkShift = Log2(chunkWidth);
            int posWidth = posTex.width;
            uint coordMask = (uint)(posWidth / 4 - 1);
            int coordShift = Log2(posWidth / 4);
            int res = accum.width;

            // ---- Read back the (non-readable) chunk table + packed positions. ----
            Color[] rangeData = ReadbackColor(chunkRange);
            Color[] minData = ReadbackColor(chunkMin);
            Color[] maxData = ReadbackColor(chunkMax);
            Color32[] packed = ReadbackColor32(posTex);
            long readbackMs = sw.ElapsedMilliseconds;

            Matrix4x4 worldToBox = boxToWorld.inverse;
            Matrix4x4 localToWorld = target.transform.localToWorldMatrix;
            Matrix4x4 localToBox = worldToBox * localToWorld;
            float halfY = boxSize.y * 0.5f, halfX = boxSize.x * 0.5f, halfZ = boxSize.z * 0.5f;
            float marginX = halfX * 1.05f, marginZ = halfZ * 1.05f;

            // ---- Decode every LOD0 splat; keep those inside the box; record (texelId, planar depth). ----
            int total = target.totalSplatCount;
            uint[] texelIds = new uint[total];
            float[] depths = new float[total];
            int n = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                Color r = rangeData[c];
                uint offset = (uint)Mathf.Round(r.r) * 4096u + (uint)Mathf.Round(r.g);
                int count = Mathf.RoundToInt(r.b);
                Vector3 bmin = new Vector3(minData[c].r, minData[c].g, minData[c].b);
                Vector3 bmax = new Vector3(maxData[c].r, maxData[c].g, maxData[c].b);
                for (int i = 0; i < count && n < total; i++)
                {
                    uint texelId = offset + (uint)i;
                    BlockCoord(texelId, coordMask, coordShift, out int x, out int y);
                    Color32 p = packed[y * posWidth + x];
                    uint qx = (uint)p.r | (((uint)p.a & 3u) << 8);
                    uint qy = (uint)p.g | ((((uint)p.a >> 2) & 3u) << 8);
                    uint qz = (uint)p.b | ((((uint)p.a >> 4) & 3u) << 8);
                    Vector3 obj = new Vector3(
                        Mathf.Lerp(bmin.x, bmax.x, qx / 1023f),
                        Mathf.Lerp(bmin.y, bmax.y, qy / 1023f),
                        Mathf.Lerp(bmin.z, bmax.z, qz / 1023f));
                    Vector3 box = localToBox.MultiplyPoint3x4(obj);
                    float depth = halfY - box.y;
                    if (depth < -0.02f * boxSize.y || depth > 1.02f * boxSize.y) continue;
                    if (Mathf.Abs(box.x) > marginX || Mathf.Abs(box.z) > marginZ) continue;
                    texelIds[n] = texelId; depths[n] = depth; n++;
                }
            }
            inBoxCount = n;
            long decodeMs = sw.ElapsedMilliseconds - readbackMs;
            if (n == 0)
            {
                var c0 = new CommandBuffer(); c0.SetRenderTarget(accum); c0.ClearRenderTarget(false, true, Color.clear);
                Graphics.ExecuteCommandBuffer(c0); c0.Release();
                timing = $"readback={readbackMs}ms decode={decodeMs}ms (no in-box splats)";
                return;
            }

            // ---- CPU sort front-to-back (ascending planar depth). ----
            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => depths[a].CompareTo(depths[b]));
            long sortMs = sw.ElapsedMilliseconds - readbackMs - decodeMs;

            // ---- Upload sorted source texel indices (exact, unlimited). ----
            uint[] orderTexels = new uint[n];
            for (int rank = 0; rank < n; rank++) orderTexels[rank] = texelIds[order[rank]];
            ComputeBuffer orderBuffer = new ComputeBuffer(n, sizeof(uint), ComputeBufferType.Structured);
            orderBuffer.SetData(orderTexels);

            Material mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Mesh pointMesh = null;
            try
            {
                mat.SetTexture("_GS_Positions", posTex);
                mat.SetTexture("_GS_Scales", sclTex);
                mat.SetTexture("_GS_Rotations", rotTex);
                mat.SetTexture("_GS_Colors", colTex);
                mat.SetTexture("_GS_ColorsCamera", colTex);
                mat.SetTexture("_GS_SH", Texture2D.blackTexture);
                mat.SetTexture("_GS_ChunkBounds", Texture2D.blackTexture);
                mat.SetBuffer("_GS_ColliderOrder", orderBuffer);
                mat.SetTexture("_GS_ColliderChunkRange", chunkRange);
                mat.SetTexture("_GS_ColliderChunkMinTex", chunkMin);
                mat.SetTexture("_GS_ColliderChunkMaxTex", chunkMax);
                mat.SetInt("_GS_ColliderChunkCount", chunkCount);
                mat.SetInt("_GS_ColliderChunkWidth", chunkWidth);
                mat.SetInt("_GS_ColliderChunkShift", chunkShift);
                mat.SetInt("_GS_Positions_CoordMask", (int)coordMask);
                mat.SetInt("_GS_Positions_CoordShift", coordShift);
                mat.SetInt("_GS_ChunkSize", Mathf.Max(1, target.chunkSize));
                mat.SetInt("_GS_SH_CoeffCount", 0);
                mat.SetFloat("_GS_CameraColorArray", 0f);
                mat.SetFloat("_VRChatCameraMode", 0f);
                mat.SetFloat("_GaussianMul", 1f);
                mat.SetFloat("_ThinThreshold", 0.005f);
                mat.SetFloat("_AntiAliasing", 1f);
                mat.SetFloat("_Log2MinScale", -15f);
                mat.SetFloat("_AlphaCutoff", Mathf.Clamp(5e-4f / opacityMultiplier, 1e-8f, 1f));
                mat.SetFloat("_AlphaCull", Mathf.Clamp01(alphaCull));
                mat.SetFloat("_LODCull", 0f);
                mat.SetFloat("_ScaleCutoff", 100f);
                mat.SetFloat("_Opacity", 1f);
                mat.DisableKeyword("_GS_PACKED_POSITIONS");
                mat.DisableKeyword("_PRECOMPUTED_SORTING_ON");
                mat.SetMatrix("_GS_ColliderWorldToBox", worldToBox);
                mat.SetFloat("_GS_ColliderBoxHeight", boxSize.y);
                mat.SetFloat("_GS_ColliderOpacityLogMultiplier", Mathf.Log(Mathf.Max(opacityMultiplier, 0.001f)));
                mat.SetVector("_GS_ColliderScreenParams", new Vector4(res, res, 1f + 1f / res, 1f + 1f / res));

                int batch = 2_000_000;
                pointMesh = BuildPointMesh((batch + 31) / 32);
                Matrix4x4 view = BuildBoxViewMatrix(worldToBox, boxSize.y);
                Matrix4x4 proj = Matrix4x4.Ortho(-halfX, halfX, -halfZ, halfZ, 0.01f, boxSize.y + 0.02f);

                var clear = new CommandBuffer { name = "GSColliderSourceClear" };
                clear.SetRenderTarget(accum);
                clear.ClearRenderTarget(false, true, Color.clear);
                Graphics.ExecuteCommandBuffer(clear);
                clear.Release();

                int batches = (n + batch - 1) / batch;
                var cmd = new CommandBuffer { name = "GSColliderSourceDraw" };
                long drawStart = sw.ElapsedMilliseconds;
                for (int b = 0; b < batches; b++)
                {
                    int offset = b * batch;
                    int count = Mathf.Min(batch, n - offset);
                    mat.SetInt("_SplatOffset", offset);
                    mat.SetInt("_SplatCount", count);
                    mat.SetInt("_ActualSplatCount", n);
                    cmd.Clear();
                    cmd.SetRenderTarget(accum);
                    cmd.SetViewProjectionMatrices(view, proj);
                    cmd.SetViewport(new Rect(0, 0, res, res));
                    cmd.DrawMesh(pointMesh, localToWorld, mat, 0, 0);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                GL.Flush();
                cmd.Release();
                long drawMs = sw.ElapsedMilliseconds - drawStart;
                sw.Stop();
                timing = $"readback={readbackMs}ms decode={decodeMs}ms sort={sortMs}ms raster(submit)={drawMs}ms total={sw.ElapsedMilliseconds}ms batches={batches}";
            }
            finally
            {
                orderBuffer.Release();
                if (pointMesh != null) UnityEngine.Object.DestroyImmediate(pointMesh);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        static int Log2(int v) { int s = 0; while (v > 1) { v >>= 1; s++; } return s; }

        static void BlockCoord(uint index, uint mask, int shift, out int x, out int y)
        {
            uint blockIndex = index >> 4;
            uint blockX = blockIndex & mask;
            uint blockY = blockIndex >> shift;
            x = (int)((blockX << 2) | (index & 3u));
            y = (int)((blockY << 2) | ((index >> 2) & 3u));
        }

        static Color[] ReadbackColor(Texture2D t)
        {
            var req = AsyncGPUReadback.Request(t, 0);
            req.WaitForCompletion();
            if (req.hasError) throw new Exception("readback failed: " + t.name);
            return req.GetData<Color>().ToArray();
        }

        static Color32[] ReadbackColor32(Texture t)
        {
            var req = AsyncGPUReadback.Request(t, 0);
            req.WaitForCompletion();
            if (req.hasError) throw new Exception("readback failed: " + t.name);
            return req.GetData<Color32>().ToArray();
        }

        static Mesh BuildPointMesh(int pointCount)
        {
            Vector3[] verts = new Vector3[pointCount];
            int[] idx = new int[pointCount];
            for (int i = 0; i < pointCount; i++) idx[i] = i;
            Mesh m = new Mesh { name = "GSColliderSourcePoints", indexFormat = IndexFormat.UInt32 };
            m.vertices = verts;
            m.SetIndices(idx, MeshTopology.Points, 0, false);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);
            m.UploadMeshData(false);
            return m;
        }

        static Matrix4x4 BuildBoxViewMatrix(Matrix4x4 worldToBox, float boxHeight)
        {
            Matrix4x4 boxToView = Matrix4x4.identity;
            boxToView.SetRow(0, new Vector4(1, 0, 0, 0));
            boxToView.SetRow(1, new Vector4(0, 0, 1, 0));
            boxToView.SetRow(2, new Vector4(0, 1, 0, -boxHeight * 0.5f - 0.01f));
            boxToView.SetRow(3, new Vector4(0, 0, 0, 1));
            return boxToView * worldToBox;
        }
    }
}
#endif
