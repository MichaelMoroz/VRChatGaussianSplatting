# VRChat Gaussian Splatting
Gaussian splatting implementation in VRChat
## Usage
1. Import unitypackage from [releases](https://github.com/MichaelMoroz/VRChatGaussianSplatting/releases) 
2. Optionally you can just clone this repo instead (might have more up to date features)
3. Open the built-in importer in the top main menu bar `Gaussian Splatting / Import .PLY Splat...`
4. Select one or several `.PLY` files you want to import
5. Select output folder for the imported prefabs.
6. Select the import parameters
   * Optionally disable usage of correct sRGB color space (removes 2 grab passes but makes transparency incorrect)
   * Optional multi-chunk rendering. Splits the gaussian splat material into chunks of N splats (760k by default). Can improve performance.
   * Optionally add an alpha stencil mask between each pass to occlude any following splats from next chunks. Can improve performance.
   * Optionally import with presorting. Makes it work everywhere without `Gaussian Splat Renderer` including avatars / and non-VRChat unity projects.
7. Click import.
> Next steps are no longer relevant for presorted splats
8. Open the example scene
9. Add the imported prefabs into the scene
10. Add the prefabs into the `GaussianSplattingRenderer` object.
    * Optionally you can do this automatically by right clicking the `Gaussian Splat Renderer` component and selecting `Collect Gaussian Splat objects for this renderer`
    * And you can automatically generate a world-space control canvas by right clicking and selecting `Generate UI`
11.  Select the number of sorting steps and rendering distance min/max depending on your scene.

> [!TIP]
> When using this in VRChat it is recommended for you to turn off MSAA, as it reduces the performance drastically and makes no visual difference for the splats

> [!TIP]
> Ideally you should change the size of the sorting textures to the nearest fitting power of two size for your target splat count. If you have less than 1M splats, then the textures can be 1024^2, if less than 4M then 2048^2, if more then 4096^2 (not recommended)
> The textures are located in `Radix Sort / RTs /`

> [!TIP]
> The renderer can only render a single gaussian splat at once at the moment. You can try using presorted splats for multiple splat rendering, they will somewhat work, but the rendering order / intersection might not look correctly.

> [!TIP]
> The currently rendered object index in the `Gaussian Splat Renderer` is synced between users. The toggles for the splats are also global for everyone.

## Credits
* .PLY importer stolen from [aras-p's UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)  
* This repository is a heavily modified version of [lambdalemon's gaussian splats](https://github.com/lambdalemon/vrcsplat)  
* The Radix Sort uses [d4rkpl4y3r's mipmap prefix sum trick](https://github.com/d4rkc0d3r/CompactSparseTextureDemo)  

## Worlds in VRChat that use this
* [My Gaussian Splat Gallery](https://vrchat.com/home/launch?worldId=wrld_01df1297-a9de-4d53-9da1-213c29a3012a)
* [My Gaussian Splat Mega Gallery](https://vrchat.com/home/launch?worldId=wrld_91216c98-a1db-4be6-8ebf-05088b335825)
* [双葉水辺公園 ［ 3DGS × Photogrammetry ］ — Tokoyoshi](https://vrchat.com/home/launch?worldId=wrld_29cf640a-5c84-4a61-b954-559809a69880)
* [- 川北東橋 ⁄ Kawakita-higashi Bridge - 3DGS — DEKA_KEIJI777V](https://vrchat.com/home/launch?worldId=wrld_45d430c0-2a0c-4d7b-b848-bd950fda5e5f)
* [Хотинська фортеця - Gaussian Splatting - 3Dimka](https://vrchat.com/home/launch?worldId=wrld_2ccfe926-3b64-4522-97a1-9840f329f5b3)

## Implementation details 

### Cursed Radix Sort
As this is VRChat, we only have access to the normal rasterization pipeline without write textures/buffers/atomics/etc, so the only way to implement sorting is avoid doing all that. You could easily use Bitonic sort here, but it would be unfortunately extremely slow.
Turned out there was a really cheap way to compute [prefix sums using mipmap's](https://github.com/d4rkc0d3r/CompactSparseTextureDemo) which initiated the idea of using those to implement a radix sort.
For a radix sort you only need to compute the prefix sum over all the sorted digit occurrences, and then to get the sorted sequence of elements you can do a binary search over the prefix sum.
This was optimized even further by using 4 bit digits (16 possible values) and grouping into 16-element chunks, this keeps the size of the mipmapped texture the same but now sorts 4x faster than normal bit by bit radix sort.

I suspect it is possible to optimize this further by packing the relevant digit key data into a separate texture and using larger chunks with more bits per digit.

This radix sort could potentially be used for other rendering/simulation applications in VRChat in the future.

### Ellipsoid screen projection
In VRChat the only way to render splats is by drawing them as quad billboards, which introduces significant overdraw especially if the quad is not optimally tight around the splat. To try to optimize this as much as possible I wrote an Ellipsoid to Ellipse projector, which finds the actual projected outline of a given gaussian splat isosurface (some code taken from https://www.shadertoy.com/view/Nl2Szm and some help from ChatGPT figuring out how to use the projection matrix).

Since you get an ellipse on the screen you can just use it to compute a gaussian on the screen with the same shape, this is slightly less accurate than [perspective correct gaussian splatting](https://fhahlbohm.github.io/htgs/), but its much faster and still has perspective correct outlines.
There is a large drawback however - it's very numerically unstable for thin ellipsoids. Specifically the estimation of the ellipse screen coefficients, to properly compute it I resorted to use extended precision float's, basically pairs of floats to emulate a float with 48bits of mantissa, it works, but required a hack to make sure the compiler doesnt optimize out the operations that make it work. 

## TODO

* Spherical harmonics support
* Better image compression 
* Animated / Multiple gaussian splats in scene?
