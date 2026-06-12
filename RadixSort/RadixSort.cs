using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
using UnityEditor;
using UnityEngine.Rendering;
#endif

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RadixSort : UdonSharpBehaviour
{
    [SerializeField] public Material computeKeyValues;
    [SerializeField] public Material radixSort;
    [SerializeField] public Material copySortedOrder;

    [SerializeField] public RenderTexture keyValues0;
    [SerializeField] public RenderTexture keyValues1;
    [SerializeField] public RenderTexture histograms;
    [SerializeField] public RenderTexture prefixSums;

    [HideInInspector] [SerializeField] public int elementCount = 1024 * 1024;

    public const int BitsPerPass = 4;
    public const int SortStartBit = 7;
    public const int MaxKeyBits = 31;
    public const int TotalSortPasses = 6;
    private const int groupSizeLog2 = 4;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static Material _editorCopySortedOrderMaterial;
#endif

    // Game: run a complete sort immediately and copy the order.
    public void RunFullSort(RenderTexture renderOrder, int slice)
    {
        BeginSortInternal(false);
        RunSortPassesInternal(false);
        CopySortedOrderInternal(renderOrder, slice, false);
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    // Editor previews: full sort + copy every frame for the given camera slice.
    public void RunFullSortForEditor(RenderTexture renderOrder, int slice)
    {
        BeginSortInternal(true);
        RunSortPassesInternal(true);
        CopySortedOrderInternal(renderOrder, slice, true);
    }
#endif

    void BeginSortInternal(bool useEditorOps)
    {
        // Runtime uniforms that vary each frame
        setStaticUniforms();

        // 1. Evaluate key values
#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Graphics.Blit(null, keyValues0, computeKeyValues);
        }
        else
#endif
        {
            VRCGraphics.Blit(null, keyValues0, computeKeyValues);
        }

        radixSort.SetTexture("_PrefixSums", prefixSums);
        radixSort.SetTexture("_Histograms", histograms);
    }

    void RunSortPassesInternal(bool useEditorOps)
    {
        int currentBit = SortStartBit;
        for (int i = 0; i < TotalSortPasses && currentBit < MaxKeyBits; i++)
        {
            radixSort.SetTexture("_KeyValues", keyValues0);
            radixSort.SetInt("_CurrentBit", currentBit);

#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                Graphics.Blit(null, histograms, radixSort, 0);
                radixSort.SetTexture("_Histograms", histograms);
                Graphics.Blit(null, prefixSums, radixSort, 1);
            }
            else
#endif
            {
                VRCGraphics.Blit(null, histograms, radixSort, 0);
                radixSort.SetTexture("_Histograms", histograms);
                VRCGraphics.Blit(null, prefixSums, radixSort, 1);
            }

            prefixSums.GenerateMips();

#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                Graphics.Blit(null, keyValues1, radixSort, 2);
            }
            else
#endif
            {
                VRCGraphics.Blit(null, keyValues1, radixSort, 2);
            }

            // Ping-pong the buffers
            RenderTexture temp = keyValues0;
            keyValues0 = keyValues1;
            keyValues1 = temp;

            currentBit += BitsPerPass;
        }
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static Material GetEditorCopySortedOrderMaterial()
    {
        if (_editorCopySortedOrderMaterial != null)
        {
            return _editorCopySortedOrderMaterial;
        }

        Shader shader = Shader.Find("Hidden/GaussianSplatting/CopyRenderOrder");
        if (shader == null)
        {
            return null;
        }

        _editorCopySortedOrderMaterial = new Material(shader);
        _editorCopySortedOrderMaterial.name = "GaussianSplatRadixSortCopyRenderOrder";
        _editorCopySortedOrderMaterial.hideFlags = HideFlags.HideAndDontSave;
        return _editorCopySortedOrderMaterial;
    }

    static void DrawFullscreenQuad(Material material)
    {
        if (material == null || !material.SetPass(0))
        {
            return;
        }

        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);
        GL.TexCoord2(0.0f, 0.0f);
        GL.Vertex3(0.0f, 0.0f, 0.0f);
        GL.TexCoord2(1.0f, 0.0f);
        GL.Vertex3(1.0f, 0.0f, 0.0f);
        GL.TexCoord2(1.0f, 1.0f);
        GL.Vertex3(1.0f, 1.0f, 0.0f);
        GL.TexCoord2(0.0f, 1.0f);
        GL.Vertex3(0.0f, 1.0f, 0.0f);
        GL.End();
        GL.PopMatrix();
    }
#endif

    void CopySortedOrderInternal(RenderTexture target, int slice, bool useEditorOps)
    {
        if (target == null)
        {
            return;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        if (useEditorOps)
        {
            Material copyMaterial = GetEditorCopySortedOrderMaterial();
            if (copyMaterial == null)
            {
                Debug.LogError("RadixSort: Missing Hidden/GaussianSplatting/CopyRenderOrder shader for editor sorting.");
                return;
            }

            copyMaterial.SetTexture("_KeyValues", keyValues0);

            Graphics.Blit(null, target, copyMaterial, 0);
            return;
        }
#endif

        copySortedOrder.SetTexture("_KeyValues", keyValues0);
        VRCGraphics.Blit(null, target, copySortedOrder, 0);
    }

    private void setStaticUniforms()
    {
        int _OptimalPOT = Mathf.NextPowerOfTwo(Mathf.CeilToInt(elementCount));
        int _OptimalPOTLog2 = Mathf.CeilToInt(Mathf.Log(_OptimalPOT, 2));
        int _OptimalImageSizeLog2Y = _OptimalPOTLog2 / 2;
        int _OptimalImageSizeLog2X = _OptimalImageSizeLog2Y + _OptimalPOTLog2 % 2;
        int _OptimalImageSizeX = 1 << _OptimalImageSizeLog2X;
        int _OptimalImageSizeY = 1 << _OptimalImageSizeLog2Y;

        if(keyValues0 == null || keyValues0.width < _OptimalImageSizeX || keyValues0.height < _OptimalImageSizeY) {
            int currentWidth = keyValues0 != null ? keyValues0.width : 0;
            int currentHeight = keyValues0 != null ? keyValues0.height : 0;
            Debug.LogError($"RadixSort: Texture size ({currentWidth}x{currentHeight}) is smaller than required ({_OptimalImageSizeX}x{_OptimalImageSizeY}). Please resize the textures.");
            return;
        }
        int _HistogramPOTLog2 = Mathf.Max(0, _OptimalPOTLog2 - groupSizeLog2);
        int _HistogramImageSizeLog2Y = _HistogramPOTLog2 / 2;
        int _HistogramImageSizeLog2X = _HistogramImageSizeLog2Y + _HistogramPOTLog2 % 2;
        int _HistogramImageSizeX = 1 << _HistogramImageSizeLog2X;
        int _HistogramImageSizeY = 1 << _HistogramImageSizeLog2Y;
        if(histograms == null || histograms.width < _HistogramImageSizeX || histograms.height < _HistogramImageSizeY) {
            int currentWidth = histograms != null ? histograms.width : 0;
            int currentHeight = histograms != null ? histograms.height : 0;
            Debug.LogError($"RadixSort: Histogram texture size ({currentWidth}x{currentHeight}) is smaller than required ({_HistogramImageSizeX}x{_HistogramImageSizeY}). Please resize the textures.");
            return;
        }

        Vector2 scale = new Vector2((float)_OptimalImageSizeX / keyValues0.width, (float)_OptimalImageSizeY / keyValues0.height);
        Vector2 histogramScale = new Vector2((float)_HistogramImageSizeX / histograms.width, (float)_HistogramImageSizeY / histograms.height);

        computeKeyValues.SetInt("_BitsPerStep", BitsPerPass);
        computeKeyValues.SetInt("_GroupSize", groupSizeLog2);
        computeKeyValues.SetInt("_ElementCount", elementCount);
        computeKeyValues.SetInt("_ImageSizeLog2X", _OptimalImageSizeLog2X);
        computeKeyValues.SetInt("_ImageSizeLog2Y", _OptimalImageSizeLog2Y);
        computeKeyValues.SetInt("_ImageElementsLog2", _OptimalPOTLog2);
        computeKeyValues.SetVector("_Scale", scale);

        radixSort.SetInt("_BitsPerStep", BitsPerPass);
        radixSort.SetInt("_GroupSize", groupSizeLog2);
        radixSort.SetInt("_ElementCount", elementCount);
        radixSort.SetInt("_ImageSizeLog2X", _OptimalImageSizeLog2X);
        radixSort.SetInt("_ImageSizeLog2Y", _OptimalImageSizeLog2Y);
        radixSort.SetInt("_ImageElementsLog2", _OptimalPOTLog2);
        radixSort.SetVector("_Scale", scale);
        radixSort.SetVector("_HistogramScale", histogramScale);
    }
}
