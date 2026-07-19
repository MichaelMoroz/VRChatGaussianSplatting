Shader "Hidden/GaussianSplatting/LODChunkSelect"
{
    // Scene-global LOD chunk selection: one pass over the concatenated chunks of all LOD objects. The
    // selection texture is a POT-square 2D texture (GenerateMips reduces to 1x1 = mean); chunkIndex =
    // y*side + x. Each chunk's object + per-object camera/distance/computed params come from the global
    // range + per-object param textures; a single global alpha (adapt pass) forms the scene LOD budget.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        Texture2D _LODChunkBounds;      // 2D, stacked: rows [0,metaH) = min (xyz, w=splatCount), [metaH,2metaH) = max (xyz, w=fileId)
        Texture2D _LODChunkRange;       // 2D: rows [0,metaH) = range; optional rows [metaH,2metaH) = density stats
        Texture2D _LODObjectParams;     // per-object params, texel (col, objectId)
        Texture2D _LODChunkSelection;   // this texture (for adapt pass mip read)
        Texture2D _LODAlphaState;       // 1x1 global alpha state
        SamplerState sampler_LODChunkSelection;

        #define GSLOD_INITIAL_LOG_ALPHA 4.0
        // Reference splats-per-chunk for the scale-agnostic density target. The per-chunk count is set from
        // projected size * this constant (NOT the chunk's own splat count), so on-screen splat density is the
        // same for every object regardless of scale/source density; the global alpha is the budget knob and
        // absorbs this constant, so its only role is numerical conditioning (match the default import chunk).
        #define LOD_REF_CHUNK_SPLATS 1024.0
        // The budget adapt may need alpha < 1 (more detail than the reference density) to spend the whole
        // scene budget, so the log-alpha floor is negative (alpha down to ~1/1024) rather than clamped at 0.
        #define LOD_MIN_LOG_ALPHA 10.0

        // x = selection side (POT square), y = log2(metaWidth) (metaWidth is POT), z = total chunk count, w = max mip (log2 side)
        float4 _LODUnifiedLayout;
        float4 _LODSelectionParams;  // max log2(alpha), min log2(alpha), max mip, adapt rate
        float4 _LODBudgetParams;     // scene target count, force min alpha, unused, unused
        float4 _LODRangeStatsParams; // x = chunk center+area 2nd-row block is present
        // Surface area of a uniform-cube distribution's covariance ellipsoid is 1/144 of the cube's 12*bbox area,
        // so this rescales the covariance area back to the old bbox-area magnitude (keeps the budget calibration).
        #define LOD_COV_AREA_SCALE 144.0

        uint UnifiedChunkIndex(uint2 pixel) { return pixel.y * (uint)_LODUnifiedLayout.x + pixel.x; }
        // metaWidth is POT -> decode chunk index with shift/mask (no % / ÷).
        uint2 MetaCoord(uint chunkIndex) { uint s=(uint)_LODUnifiedLayout.y; return uint2(chunkIndex & ((1u << s) - 1u), chunkIndex >> s); }
        uint MetaHeight() { uint s=(uint)_LODUnifiedLayout.y; uint w=1u << s; return ((uint)_LODUnifiedLayout.z + w - 1u) >> s; }
        // 2nd stack of the range texture: chunk center of mass (xyz) + covariance-ellipsoid area (w), object-local.
        float4 ChunkCenterArea(uint2 mc) { return _LODChunkRange[mc + uint2(0u, MetaHeight())]; }

        // Per-object param texture columns (texel (col, objectId)).
        float3 ObjCameraPos(uint o)     { return _LODObjectParams[uint2(0, o)].xyz; }
        float3 ObjCameraForward(uint o) { return _LODObjectParams[uint2(1, o)].xyz; }
        float4 ObjDistanceParams(uint o){ return _LODObjectParams[uint2(2, o)]; }   // zeroOffset, radius, smallestChunkSize, dirBias
        float4 ObjComputedParams(uint o){ return _LODObjectParams[uint2(3, o)]; }   // computed, minCluster, reusePercent, active
        float3 ObjLossyScale(uint o)    { return _LODObjectParams[uint2(4, o)].xyz; } // world lossyScale (local->world)

        // World-space surface area of the chunk box. Bounds are object-LOCAL; the world dimensions are the
        // local size scaled per-axis by lossyScale (rotation does not change a box's face areas).
        float BBoxSurfaceAreaWorld(float3 mn, float3 mx, float3 scl)
        {
            float3 size = abs(max(mx - mn, 0.0) * scl);
            return 2.0 * (size.x*size.y + size.x*size.z + size.y*size.z);
        }

        // World-space distance from camera to the chunk box. Use closest-point distance for the LOD angle
        // estimate so large/deep chunks are not under-selected just because their center is far away. The
        // directional bias still uses the center direction.
        float DistanceToChunkWorld(float3 mn, float3 mx, float3 camPos, float3 camFwd, float dirBias, float3 scl)
        {
            float3 closest = clamp(camPos, mn, mx);
            float dist = length((closest - camPos) * scl);
            float3 dir = normalize(((mn + mx) * 0.5 - camPos) * scl + 1e-6);
            float dot0 = dot(normalize(camFwd * scl + 1e-6), dir);
            float divisor = lerp(1.0, max(1.0, dirBias), 0.5*dot0 + 0.5);
            return dist / divisor;
        }

        void LogAlphaBounds(out float minLogAlpha, out float maxLogAlpha)
        {
            minLogAlpha = max(-LOD_MIN_LOG_ALPHA, _LODSelectionParams.y);
            maxLogAlpha = max(minLogAlpha, _LODSelectionParams.x);
        }

        float CurrentLogAlpha()
        {
            float minLogAlpha, maxLogAlpha;
            LogAlphaBounds(minLogAlpha, maxLogAlpha);
            if (_LODBudgetParams.y > 0.5)
            {
                return minLogAlpha;
            }
            float4 state = _LODAlphaState[uint2(0,0)];
            float logAlpha = state.x;
            if (logAlpha <= 0.0 && state.y <= 0.0 && state.z <= 0.0 && state.w <= 0.0)
                return clamp(GSLOD_INITIAL_LOG_ALPHA, minLogAlpha, maxLogAlpha);
            if (logAlpha > maxLogAlpha) logAlpha = log2(max(1.0, logAlpha));
            return clamp(logAlpha, minLogAlpha, maxLogAlpha);
        }

        // --- computed-LOD discrete level fitting (same math as per-object path) ---
        float ReusePercent(float4 comp) { return comp.z <= 0.0 ? 50.0 : clamp(comp.z, 1.0, 99.0); }
        float OutputTarget(float chunkCount, int level)
        { return level <= 0 ? chunkCount : clamp(floor(chunkCount / exp2((float)level) + 0.5), 0.0, chunkCount); }
        float ClusterCount(float chunkCount, int level, float4 comp)
        {
            if (level <= 0) return chunkCount;
            float outCount = OutputTarget(chunkCount, level);
            float reuse = clamp(floor(outCount * (ReusePercent(comp)/100.0) + 0.5), 0.0, outCount);
            float cluster = outCount - reuse;
            if (cluster < max(1.0, comp.y) || cluster >= chunkCount) return 0.0;
            return cluster;
        }
        float OutputCount(float chunkCount, int level, float4 comp)
        { if (level <= 0) return chunkCount; return ClusterCount(chunkCount, level, comp) > 0.0 ? OutputTarget(chunkCount, level) : 0.0; }
        float SelectComputedCount(float chunkCount, float desired, float4 comp, out float lvl)
        {
            if (chunkCount <= 0.0)
            {
                lvl = 0.0;
                return 0.0;
            }
            // desired may round to 0 for far chunks: do NOT cull to 0 here. The loop below then picks the
            // coarsest valid level (>=1 merged splat), so a chunk degrades to its lowest LOD, never vanishes.

            lvl = 0.0;
            float best = OutputCount(chunkCount, 0, comp);
            float bestDelta = abs(best - desired);
            [loop] for (int level = 1; level < 30; level++)
            {
                float oc = OutputCount(chunkCount, level, comp);
                if (oc <= 0.0) break;
                float d = abs(oc - desired);
                if (d < bestDelta) { bestDelta = d; best = oc; lvl = (float)level; }
            }
            return best;
        }

        float4 fragSelect(v2f_img input) : SV_Target
        {
            uint2 pixel = uint2(input.pos.xy);
            uint chunkIndex = UnifiedChunkIndex(pixel);
            if (chunkIndex >= (uint)_LODUnifiedLayout.z) return 0.0; // padding -> 0 keeps the mip sum exact

            uint2 mc = MetaCoord(chunkIndex);
            float4 cmin = _LODChunkBounds[mc];
            float4 cmax = _LODChunkBounds[mc + uint2(0u, MetaHeight())];
            float4 rng  = _LODChunkRange[mc];
            uint objId = (uint)round(rng.w);

            float3 mn = cmin.xyz, mx = cmax.xyz;
            float chunkSplatCount = cmin.w;
            float3 camPos = ObjCameraPos(objId);
            float3 camFwd = ObjCameraForward(objId);
            float4 dp = ObjDistanceParams(objId);
            float4 comp = ObjComputedParams(objId);
            if (comp.w < 0.5) return 0.0;

            // Bring everything into WORLD space so the metric is scale-agnostic: chunk bounds + camera are
            // object-local, so use lossyScale to get world distance/size. The per-object length params
            // (radius, smallestChunkSize) are local lengths -> scale by the representative (cube-root) world scale.
            float3 scl = ObjLossyScale(objId);
            float sScalar = pow(max(1e-12, abs(scl.x * scl.y * scl.z)), 1.0 / 3.0);
            float alpha = exp2(CurrentLogAlpha());
            float lodRadius = max(0.001, dp.y * sScalar);
            if (_LODBudgetParams.y > 0.5)
            {
                return float4(chunkSplatCount, 0.0, 0.0, CurrentLogAlpha());
            }

            // Distance from the bbox CLOSEST POINT: handles chunks elongated toward the camera, so a chunk whose
            // near edge fills the screen reaches LOD0 even though its center of mass is far. Size from the
            // covariance-ellipsoid area (range texture 2nd row, w) when present - a tighter on-screen-size estimate
            // than the bbox surface area; the bbox area is the fallback for old prefabs without the 2nd-row block.
            // (The stored center of mass, ca.xyz, is used only by the editor debug overlay now, not the metric.)
            float dist = DistanceToChunkWorld(mn, mx, camPos, camFwd, dp.w, scl);
            float4 ca = ChunkCenterArea(mc);
            bool hasCA = _LODRangeStatsParams.x > 0.5 && ca.w > 0.0;
            float chunkArea = hasCA ? LOD_COV_AREA_SCALE * ca.w * sScalar * sScalar
                                    : 12.0 * BBoxSurfaceAreaWorld(mn, mx, scl);
            float zeroedDist = max(1e-4, dist);
            float normalizedDistance = saturate(zeroedDist / lodRadius);
            chunkArea = max(chunkArea, 1e-6 * lodRadius * lodRadius);
            float scaledDist = alpha * zeroedDist;
            // keep ~ projected solid angle / alpha^2 (world chunkArea / world dist^2).
            float keep = chunkArea / (1e-6 * chunkArea + scaledDist * scaledDist);
            // Scale-agnostic count: target = projected size * a FIXED reference density (NOT the chunk's own
            // splat count), capped by what the chunk actually stores. alpha (global adapt) is the budget knob.
            float count = floor(clamp(LOD_REF_CHUNK_SPLATS * keep, 0.0, chunkSplatCount) + 0.5);

            float lvl;
            count = SelectComputedCount(chunkSplatCount, count, comp, lvl);
            float selectionMetadata = lvl;
            // Active, non-padding chunk: never select 0 splats. SelectComputedCount floors at the coarsest valid
            // level (>=1 merged splat), so chunks degrade rather than pop out entirely.
            count = max(1.0, min(count, chunkSplatCount));
            return float4(count, dist, selectionMetadata, CurrentLogAlpha());
        }

        // Global adapt pass: converge the single alpha so the scene-wide selected total -> scene budget.
        float4 fragAdaptAlpha(v2f_img input) : SV_Target
        {
            float side = max(1.0, _LODUnifiedLayout.x);
            float maxMip = max(0.0, _LODSelectionParams.z);
            float mean = _LODChunkSelection.SampleLevel(sampler_LODChunkSelection, float2(0.5, 0.5), maxMip).x;
            float total = mean * side * side; // POT-square: top mip (1x1) = mean over all texels
            float target = max(1.0, _LODBudgetParams.x);
            float error = (total - target) / target;
            float logAlpha = CurrentLogAlpha();
            float rate = _LODSelectionParams.w;
            if (rate <= 0.0) return float4(logAlpha, total, target, error);
            logAlpha += error * rate;
            float minLogAlpha, maxLogAlpha;
            LogAlphaBounds(minLogAlpha, maxLogAlpha);
            logAlpha = clamp(logAlpha, minLogAlpha, maxLogAlpha);
            return float4(logAlpha, total, target, error);
        }
        ENDCG

        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragSelect
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment fragAdaptAlpha
            ENDCG }
    }
    Fallback Off
}
