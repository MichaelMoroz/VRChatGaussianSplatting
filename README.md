# VRChat Gaussian Splatting

![Example Scene View](image.png)

Gaussian splatting for VRChat worlds: runtime camera-sorted rendering through Udon, a splat importer, an in-world gallery and control UI, automatic editor Scene-view sorting, and editor tools for terrain colliders and re-importing.

## Features

- Sorted-only runtime rendering through `GaussianSplatRenderer`
- Importer for `.ply` and `.spz` splats from `Gaussian Splatting / Import Splats...`
- Import modes: **LOD** (combined, with a level-of-detail hierarchy) and **Standalone** (self-rendering, precomputed sorting)
- Combined runtime rendering — all active splats transformed into world space and sorted together
- **LOD hierarchy** — an import-time LOD pyramid; the runtime selects per-chunk detail against a scene-wide, per-platform splat budget, with in-world quality tiers and an LOD Splat Cap
- In-world **gallery** UI: list splats, show one at a time, optional master lock
- Automatic scene renderer + world-space control UI when splats are present
- Automatic, editor-only Scene-view sorting for every `GaussianSplatObject`
- Editor tools: **terrain collider** generation, and **re-import** (exact / edit-settings) from a splat's stored import metadata
- Android build conversion to no-geometry, fake-sRGB splat shaders
- Bilingual UI (English / 日本語)
- VRC Light Volumes integration

## Quick Start

1. Import the unitypackage from [releases](https://github.com/MichaelMoroz/VRChatGaussianSplatting/releases), or clone the repo.
2. Open `Gaussian Splatting / Import Splats...`.
3. Add one or more `.ply` / `.spz` files, choose an output folder, and pick an **Import Mode**.
4. Configure the import options (see below) and import.
5. Drag the imported prefabs into the scene. The editor automatically creates the scene `GaussianSplatRenderer` and the world-space control UI when needed.
6. Tune material/render settings from the renderer inspector or the in-world UI.

### Import Modes

- **LOD** (default) — a combined `GaussianSplatObject` with a downsampled LOD pyramid. The combiner selects per-chunk detail by camera distance and a scene-wide splat budget; full-detail LOD0 is preserved up close.
- **Standalone** — a self-rendering mesh + material with **precomputed direction-based sorting** baked in. Renders without `GaussianSplatRenderer` (e.g. avatars, or outside VRChat). Uses more texture memory and can introduce artifacts.

### Import Options

**All modes:**
- `Compute Bounding Box`
- `Import Spherical Harmonics` + `Max SH Band` — memory scales with the chosen band; disabled forces SH0.
- `SH Compression` — `None` (RGB565), `BC1` (4 bpp), or `BC7` (8 bpp).
- `Compress Color+Alpha to BC7`
- **Transform / cleanup**: `Crop To Bounds` (preview box handle), `Horizon Alignment` / `Wall Alignment` (pick points in the preview), `Normalize Size` (scale the floater-robust extent to a target size).

**Combined (LOD) mode:**
- `Chunk Size` — combined-render chunk size.
- `LOD Resampling Rate` / `LOD Reused Splats` — tune the LOD pyramid's stored-splat count.

**Standalone rendering only** (combined splats render through the combiner and ignore these):
- `sRGB Color Correction` — the exact color/compositing path (two grab passes, needs HDR camera targets). **Forced off on Android.** See [Color reproduction & sRGB](#color-reproduction--srgb).
- `Multi-Pass Rendering` + `Splat Count Per Pass` + `Max Alpha Mask Count` — split rendering into sequential chunks; alpha-mask passes can occlude later chunks behind opaque geometry (a tradeoff, since grab passes are expensive). Requires `sRGB Color Correction` on.
- `Start Render Queue` — combined splats instead take their render queue from the `GaussianSplatRenderer` component.

## Runtime Rendering

Use this path to camera-sort splats at runtime in VRChat worlds (Udon):

1. Add imported Gaussian Splat Objects to the scene.
2. Let the editor create the scene `GaussianSplatRenderer` and UI, or select the renderer if it exists.
3. The renderer combines all active splats into one sorted render; use the gallery to show one at a time.
4. Enter play mode or build. The renderer updates sorted render order for the active (screen + photo) cameras.

### How rendering works

The renderer transforms all active splats into world space, writes them into combined render textures, and sorts the result as one renderer — so any number of active splats render together. Combined LOD selection, the per-platform splat budget, and the quality tiers build on top of this.

### Android Builds

A pre-build scene pass converts runtime splat renderers from the geometry-shader path to no-geometry shaders and replaces point meshes with zero-sized quad meshes (quad vertex IDs drive splat lookup, so the mesh stays cheap if drawn with the wrong material). The same pass swaps the fullscreen color-space grab-pass shaders for the fake-sRGB no-geometry path, because VRChat Android lacks the reliable HDR/grab-pass path. It also sets the scene renderer to Low quality and disables camera HDR.

## In-World UI

The renderer creates a world-space control canvas automatically when splats are present and the scene has none. It's intended as a real in-world control surface.

**Networking:** only the **gallery selection** is synced (manual sync). All other controls are **local** per user.

### Gallery mode

- Add splat objects to the renderer UI's **gallery list**. When the list has one or more entries, gallery mode is active and only the selected splat renders; objects not in the list are never touched.
- An inspector-only **enable** toggle keeps the list but shows all listed splats when off.
- A **master lock** restricts changing the selection to the instance master.
- Entry names/descriptions come from each `GaussianSplatObject` (with fallbacks).

### Controls

- Gallery selection (synced)
- Quality presets: Very Low / Low / Medium / High (set alpha cull + alpha cutoff)
- SH Band
- VRC Light Volumes (toggle) + Light Volume Intensity
- Gaussian Scale
- Alpha Cutoff (lower = better quality, higher cost) + Alpha Cull
- Antialiasing
- Camera Quantization
- Language (English / 日本語)
- An **Advanced Settings** toggle reveals the finer sliders
- LOD Splat Cap (shown only when LOD splats are present)

Render queue and the sort/material settings live on the `GaussianSplatRenderer` component inspector. Use `Update Sorting Resource Textures` there to resize the sorting textures to fit the largest assigned splat instead of editing those assets by hand.

## Terrain Collider

`Gaussian Splatting / Generate Terrain Collider…` (or the `GaussianSplatObject` context menu) rasterizes a splat into a heightmap on the GPU and builds a Unity `TerrainData` + `TerrainCollider`. Options cover terrain resolution, GPU batch size, small-splat filtering, and empty-pixel fill. It is a 2.5D (top-down) collider — good for ground/terrain, not overhangs or interiors.

## Editor Scene-View Sorting

- Automatic for every `GaussianSplatObject`; editor-only, no Udon.
- Owns its own transient sorting resources — it does not reuse the runtime `GaussianSplatRenderer` / `RadixSort` textures.
- Skips standalone precomputed-sorting materials; applies only to the sorted runtime path.
- Targets Scene-view cameras; inspector previews are not part of this pass.

## Tips

> [!TIP]
> The renderer automatically disables MSAA for splats in-game. Gaussian splats have very high overdraw, which makes MSAA extremely expensive for little or no benefit.

> [!TIP]
> Lower `Alpha Cutoff` keeps more splats and improves quality at higher cost. Fit sorting textures to the largest splat with `Update Sorting Resource Textures`.

> [!TIP]
> `sRGB Color Correction` (standalone imports only; forced off on Android) is the exact color/compositing path but adds two grab passes. Turning it off falls back to back-to-front blending, disables multi-pass, and makes color non-exact (see [Color reproduction & sRGB](#color-reproduction--srgb)).

> [!TIP]
> `VRC Light Volumes` is a scene-integration control: off keeps the splat near its baked look; on lets it pick up scene lighting (tune with `Light Volume Intensity`).

## Current Limitations

- Standalone precomputed sorting is a separate import path, not the same as runtime sorting.
- Very large splats are limited by available RAM and the combined render's splat-count cap.
- The terrain collider is 2.5D (heightmap), so it cannot represent overhangs, walls, or interiors.

## Credits

- `.PLY` importer adapted from [aras-p's UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
- `.SPZ` format from [Niantic's spz](https://github.com/nianticlabs/spz)
- This repository is a heavily modified version of [lambdalemon's gaussian splats](https://github.com/lambdalemon/vrcsplat)
- The radix sort uses [d4rkpl4y3r's mipmap prefix sum trick](https://github.com/d4rkc0d3r/CompactSparseTextureDemo)

## Worlds in VRChat that use this

- [My Gaussian Splat Gallery](https://vrchat.com/home/launch?worldId=wrld_01df1297-a9de-4d53-9da1-213c29a3012a)
- [My Gaussian Splat Mega Gallery](https://vrchat.com/home/launch?worldId=wrld_91216c98-a1db-4be6-8ebf-05088b335825)
- [双葉水辺公園 ［ 3DGS × Photogrammetry ］ — Tokoyoshi](https://vrchat.com/home/launch?worldId=wrld_29cf640a-5c84-4a61-b954-559809a69880)
- [- 川北東橋 ⁄ Kawakita-higashi Bridge - 3DGS — DEKA_KEIJI777V](https://vrchat.com/home/launch?worldId=wrld_45d430c0-2a0c-4d7b-b848-bd950fda5e5f)
- [Хотинська фортеця - Gaussian Splatting - 3Dimka](https://vrchat.com/home/launch?worldId=wrld_2ccfe926-3b64-4522-97a1-9840f329f5b3)

---

# Technical Notes

## Runtime Radix Sort

As this is VRChat, we only have access to the normal rasterization pipeline without writable textures, buffers, or atomics, so the sorting path has to stay inside ordinary rendering primitives.

The runtime sorter uses a radix sort built on mipmap-based prefix sums. For radix sorting you only need prefix sums over digit occurrences, and from that you can reconstruct the sorted sequence. In this implementation each step sorts 4 bits at a time over 16-value digits, over a fixed number of passes (currently 6) covering the high bits of the distance key, which keeps the cost practical while still giving good ordering accuracy.

This same general sorted-order approach is used both by the runtime renderer and by the automatic editor Scene view renderer, but the editor path owns its own materials and transient render textures instead of reusing the runtime scene resources.

The same core idea could be reused for other VRChat rendering or simulation problems where you need ordering but do not have access to compute-style GPU primitives.

## Ellipsoid Screen Projection

Splats are rendered as projected billboards, with the screen-space ellipse for each one computed exactly rather than approximated.

The current implementation projects the Gaussian ellipsoid's quadric directly into clip space, extracts the resulting screen-space conic, and solves it analytically for the ellipse center, orientation, and axes. It is float-only, with no emulated double precision, and includes guarded divisions, bounded intermediate values, near-plane rejection for splats the camera is inside, and validity checks that keep thin or near-degenerate splats stable.

Compared with normal 3DGS rendering, this avoids the center-Jacobian affine projection approximation entirely. Standard 3DGS uses the Jacobian of a local affine projection around the Gaussian center, which is fast but introduces projection error that shows up as blur, shape drift, and scene inconsistency, especially important for VR applications where the camera can be very near the splats and can have very large fields of view.

This recovers the exact projected ellipse, the same result as ellipsoid-projection approaches such as "Projecting Gaussian Ellipsoids While Avoiding Affine Projection Approximation" (arXiv:2411.07579v2), obtained analytically from the projected conic rather than fitted or approximated.

Because they are rendered as billboards, keeping the projected ellipse tight matters a lot for overdraw. The exact projection preserves a stable, well-fitted screen-space footprint for the Gaussian and stays numerically robust on thin ellipsoids.

## Color reproduction & sRGB

`sRGB Color Correction` applies to the standalone import path only, and Android builds force it off. For standalone splats, exact color reproduction requires the color-space transform grab-pass path.

If you turn `sRGB Color Correction` off, there are two important side effects:

1. Rendering order has to fall back to back-to-front blending because there is no grab pass caching the current view color, so the multi-pass optimization path is no longer applicable.
2. Colors are no longer reproduced exactly. The color conversion still happens per splat, but the blending itself is no longer mathematically valid for the original training color space.

One workaround is to train the splats on images that were already color-converted into inverse sRGB space. Then the splats can be rendered without the runtime color-space conversion path, but you also need to turn off fake sRGB on the material.

## VRC Light Volumes

The shader can integrate with VRC Light Volumes through the `VRC Light Volumes` toggle and the `Light Volume Intensity` control.

- When enabled, the splat shader samples VRC Light Volume spherical-harmonic lighting at the splat world position.
- The sampled lighting is applied to the non-emissive part of the splat color, while values above `1.0` are preserved as emissive.
- `Light Volume Intensity` scales the contribution of the sampled light volume lighting.
- This affects shading only. It does not change the sorting path or render-order generation.
- Some splats look better as mostly self-lit imagery, while others benefit from picking up scene lighting, so this is intentionally exposed as a runtime control.


---

# Future Roadmap

* Custom Gaussian Splat object, with manually provided splat data textures (could be render texture generated procedurally)
* Animated transitions and effects for splat objects
* Progressive LOD load and web request support
* 3D collider generation
* 4DGS support (requires a commonly supported 4DGS format to be established first, it doesnt exist yet. Just a sequence of 3DGS is not an optimal representation for 4DGS.).

