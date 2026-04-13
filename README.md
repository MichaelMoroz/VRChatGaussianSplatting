# VRChat Gaussian Splatting

Gaussian splatting for VRChat worlds, with runtime sorted rendering, standalone precomputed imports, and automatic editor Scene view sorting.

## Current Features

- Sorted-only runtime rendering through `GaussianSplatRenderer`
- Automatic editor-only Scene view sorting for every discovered `GaussianSplatObject`
- Importer for `.ply` splats from `Gaussian Splatting / Import PLY Splats...`
- Optional standalone precomputed-sorting import path for splats that should render without `GaussianSplatRenderer`
- Generated world-space UI from the renderer context menu
- Global/networked controls for:
  - current splat selection
  - SH band
  - VRC Light Volumes
  - Gaussian scale
- Local controls for:
  - min/max sort distance
  - camera quantization
  - sorting steps
  - sort every frame
  - antialiasing
  - light volume intensity
  - alpha cutoff

## Workflow

1. Import the unitypackage from [releases](https://github.com/MichaelMoroz/VRChatGaussianSplatting/releases), or clone the repo directly.
2. Open `Gaussian Splatting / Import PLY Splats...`.
3. Add one or more `.ply` files and choose an output folder.
4. Configure the import options:
   - `Compute Bounding Box`
   - `sRGB Color Correction`
   - `Import Spherical Harmonics`
   - `Default SH Band`
   - `Multi-Pass Rendering`
   - `Splat Count Per Pass`
   - `Max Alpha Mask Count`
   - `Precompute Sorting`
5. Import the splats.
6. For the runtime sorted path, add the imported prefabs to a `GaussianSplatRenderer` in your scene.
7. Optionally use the renderer context menu to collect splats automatically, resize sorting textures, and generate a world-space control UI.

### Import Option Notes

- `sRGB Color Correction` adds 2 extra grab passes. It fixes transparency/compositing behavior, but it is heavier. Without it, the renderer falls back to back-to-front blending, which also means multi-pass rendering will not work correctly.
- `sRGB Color Correction` only works correctly when the world uses HDR camera render targets.
- `Multi-Pass Rendering` splits a splat into sequential chunks. This can improve VR rendering performance for large splats.
- `Max Alpha Mask Count` inserts optional alpha-mask passes between multi-pass chunks to occlude later chunks behind opaque geometry. This can help performance, but grab passes are expensive, so it is a tradeoff.
- `Precompute Sorting` bakes direction-based order into the imported data so the splat can render standalone, including outside the runtime renderer path, but it uses much more texture memory and can introduce artifacts.

### Runtime Sorted Rendering

Use this path when you want the splat to be camera-sorted at runtime in VRChat:

1. Add a `GaussianSplatRenderer` to the scene.
2. Add imported splat prefabs to its `splatObjects` list.
3. Or use the component context menu:
   - `Collect Gaussian Splat Objects for the renderer`
   - `Update Sorting Resource Textures`
   - `Generate UI`
4. Enter play mode or build the world. The renderer selects one splat at a time and updates sorted render order for the active cameras.

### Standalone Precomputed Sorting

Use `Precompute Sorting` in the importer when you want a splat to render without `GaussianSplatRenderer`.

- This path bakes direction-based render order into the imported material data.
- It is a standalone import mode.
- It is not intended to be driven by `GaussianSplatRenderer`.

## Generated UI

`Generate UI` creates a world-space control canvas for the renderer.

Current synced/global controls:

- `Current Splat (global)`
- `Splat Selection (global)`
- `SH Band (global)`
- `VRC Light Volumes (global)`
- `Gaussian Scale (global)`

Current local controls:

- `Min Sort Distance`
- `Max Sort Distance`
- `Camera Quant`
- `Sorting Steps`
- `Sort every frame`
- `Antialiasing`
- `Light Volume Intensity`
- `Alpha Cutoff (lower = better quality)`

The generated UI is intended as a practical in-world control surface, not just a demo. The synced controls behave the same way as the selected splat index and update for other users.

## Import Notes

- Large imports are still limited by available RAM.
- SH import memory now scales with the selected SH band instead of always allocating for the highest band.
- If `Import Spherical Harmonics` is disabled, the importer skips SH textures and forces SH0.
- If SH import is enabled, the importer only creates textures up to the selected max/default band and falls back to the highest lower non-zero band when needed.
- `.ply` files larger than 2 GB are still not supported.
- If you have an especially large splat list on a renderer, use `Update Sorting Resource Textures` on `GaussianSplatRenderer` to resize the sorting textures to fit the largest assigned splat instead of managing those assets by hand.

## Tips

> [!TIP]
> In VRChat, splats should not rely on MSAA. `GaussianSplatRenderer` disables game-mode MSAA, and leaving it enabled is usually just extra cost for little or no visual benefit on splats.

> [!TIP]
> The renderer currently shows one selected splat at a time. If you need a splat to render without the runtime sorter, import it with `Precompute Sorting` instead.

> [!TIP]
> Use `Update Sorting Resource Textures` after assigning splats to a renderer. That resizes the sorting textures to fit the largest assigned splat and is the preferred replacement for manually editing the radix-sort render textures.

> [!TIP]
> Lower `Alpha Cutoff` keeps more splats and improves quality, but it also increases rendering cost. More `Sorting Steps` improve ordering accuracy, but they also make sorting more expensive.

> [!TIP]
> `sRGB Color Correction` gives the correct transparency path, but it adds 2 grab passes. For small or performance-constrained splats you may want to disable it, understanding that it no longer will be rendered exactly correctly.

## Rendering Pipeline

- Runtime rendering is sorted-only.
- The active runtime path uses sorted render-order textures and front-to-back compositing.
- SH selection is controlled numerically through `_SHBand`.
- Runtime SH band is clamped by the textures available on the imported material, so a splat cannot be pushed past the SH data it actually has.
- Material/render controls now include:
  - Gaussian scale
  - antialiasing
  - alpha cutoff
  - VRC Light Volumes on/off
  - light volume intensity
- Game-mode MSAA is disabled by the renderer. Splats should not rely on MSAA for quality or performance.

### Practical Tips

- `GaussianSplatRenderer` currently renders a single selected splat at a time. If you need standalone rendering without the runtime sorter, use the precomputed-sorting import path instead.
- The sort texture size still matters for performance and memory. The renderer helper is now the preferred way to size these textures, but the underlying rule is the same: fit them to the padded element count of the largest splat you want to sort.
- For small splats or performance-constrained scenes, disabling sRGB correction can be worthwhile, but you are trading away correct transparency behavior.
- Lower alpha cutoff keeps more splats alive and improves visual quality, but it also increases rendering cost.
- More sorting steps improve order accuracy by sorting more bits of the distance key, but they also increase the sorting cost.

## Editor Scene View Sorting

- Scene view sorting is automatic for `GaussianSplatObject`.
- It is editor-only and does not depend on Udon.
- It creates and owns its own transient sorting resources.
- It does not reuse the runtime `GaussianSplatRenderer` sorting textures or scene `RadixSort` resources.
- It skips standalone precomputed-sorting materials and only applies to the sorted runtime path.

## Current Limitations

- `GaussianSplatRenderer` renders one selected splat at a time.
- Standalone precomputed sorting is a separate import path and is not the same thing as runtime sorting.
- Very large splats can still be heavy to import and render even with the newer SH memory reductions.
- The current Scene view sorter targets Scene view cameras; inspector previews are not part of this pass.

## Credits

- `.PLY` importer adapted from [aras-p's UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- This repository is a heavily modified version of [lambdalemon's gaussian splats](https://github.com/lambdalemon/vrcsplat)
- The radix sort uses [d4rkpl4y3r's mipmap prefix sum trick](https://github.com/d4rkc0d3r/CompactSparseTextureDemo)

## Worlds in VRChat that use this

- [My Gaussian Splat Gallery](https://vrchat.com/home/launch?worldId=wrld_01df1297-a9de-4d53-9da1-213c29a3012a)
- [My Gaussian Splat Mega Gallery](https://vrchat.com/home/launch?worldId=wrld_91216c98-a1db-4be6-8ebf-05088b335825)
- [双葉水辺公園 ［ 3DGS × Photogrammetry ］ — Tokoyoshi](https://vrchat.com/home/launch?worldId=wrld_29cf640a-5c84-4a61-b954-559809a69880)
- [- 川北東橋 ⁄ Kawakita-higashi Bridge - 3DGS — DEKA_KEIJI777V](https://vrchat.com/home/launch?worldId=wrld_45d430c0-2a0c-4d7b-b848-bd950fda5e5f)
- [Хотинська фортеця - Gaussian Splatting - 3Dimka](https://vrchat.com/home/launch?worldId=wrld_2ccfe926-3b64-4522-97a1-9840f329f5b3)

## Implementation Details

### Cursed Radix Sort

As this is VRChat, we only have access to the normal rasterization pipeline without writable textures, buffers, or atomics, so the sorting path has to stay inside ordinary rendering primitives.

The runtime sorter uses a radix sort built on mipmap-based prefix sums. For radix sorting you only need prefix sums over digit occurrences, and from that you can reconstruct the sorted sequence. In this implementation each step sorts 4 bits at a time over 16-value digits, which keeps the number of passes practical while still giving a useful quality/performance tradeoff through the `Sorting Steps` setting.

This same general sorted-order approach is now used both by the runtime renderer and by the automatic editor Scene view renderer, but the editor path owns its own materials and transient render textures instead of reusing the runtime scene resources.

The same core idea could be reused for other VRChat rendering or simulation problems where you need ordering but do not have access to compute-style GPU primitives.

### Ellipsoid Screen Projection

Splats are still rendered as projected billboards, but the ellipse projection path has been updated substantially.

Instead of relying on emulated double precision, the current implementation uses a more stable float-only ellipse fitting approach built around sampling the projected tangent outline of the ellipsoid and fitting the screen-space ellipse from those samples. The math now includes guarded divisions, bounded intermediate values, and safer normalization paths to keep thin splats stable without the older extended-precision workaround.

This is still an approximation compared with fully perspective-correct Gaussian splatting, but it keeps perspective-correct outlines, stays practical for VRChat, and is much more stable than the older thin-ellipsoid path.

Because splats are rendered as billboards, keeping the projected ellipse tight matters a lot for overdraw. The current projection path is aimed at preserving a practical, stable screen-space footprint for the Gaussian while avoiding the numerical instability that showed up on thin ellipsoids in the older implementation.
