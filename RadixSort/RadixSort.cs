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

    [SerializeField] public RenderTexture keyValues0;
    [SerializeField] public RenderTexture keyValues1;
    [SerializeField] public RenderTexture prefixSums;

    [HideInInspector] [SerializeField] public int elementCount = 1024 * 1024;
    [SerializeField] int pipelinedPassesPerFrame = 1;

    public const int BitsPerPass = 4;
    public const int TotalSortPasses = 8;
    public const int MaxKeyBits = BitsPerPass * TotalSortPasses;
    private const int groupSizeLog2 = 4;

    private int _currentBit;
    private bool _sortInProgress;
    private RenderTexture _targetRenderOrder;
    private int _targetSlice;

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    static Material _editorCopySortedOrderMaterial;
#endif

    // Game: start a pipelined sort whose finished order will be copied into renderOrder[slice].
    public void BeginSort(RenderTexture renderOrder, int slice)
    {
        _targetRenderOrder = renderOrder;
        _targetSlice = slice;
        BeginSortInternal(false);
    }

    // Game: advance the in-flight sort by the configured number of radix subpasses for this frame.
    // Returns true on the frame the sort completes, after the sorted order has been copied into the
    // target render-order texture.
    public bool RunSort()
    {
        if (!_sortInProgress)
        {
            return false;
        }
        StepSortInternal(GetPipelinedPassesPerFrame(), false);
        if (_sortInProgress)
        {
            return false;
        }
        CopySortedOrderInternal(_targetRenderOrder, _targetSlice, false);
        return true;
    }

    // Game: if idle and requested, start a new pipelined sort; then advance the in-flight sort by
    // the configured number of radix passes for this frame. Returns true on the frame the sort is
    // published into the target render-order texture.
    public bool UpdatePipelinedSort(RenderTexture renderOrder, int slice, bool requestSort)
    {
        if (!_sortInProgress)
        {
            if (!requestSort)
            {
                return false;
            }
            _targetRenderOrder = renderOrder;
            _targetSlice = slice;
            BeginSortInternal(false);
        }
        return RunSort();
    }

    // Game: run a complete sort immediately and copy the order (used for the occasional photo camera).
    public void RunFullSort(RenderTexture renderOrder, int slice)
    {
        _targetRenderOrder = renderOrder;
        _targetSlice = slice;
        BeginSortInternal(false);
        StepSortInternal(TotalSortPasses, false);
        CopySortedOrderInternal(renderOrder, slice, false);
    }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
    // Editor previews: full sort + copy every frame for the given camera slice.
    public void RunFullSortForEditor(RenderTexture renderOrder, int slice)
    {
        _targetRenderOrder = renderOrder;
        _targetSlice = slice;
        BeginSortInternal(true);
        StepSortInternal(TotalSortPasses, true);
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
        _currentBit = 0;
        _sortInProgress = true;
    }

    void StepSortInternal(int maxSubpasses, bool useEditorOps)
    {
        if (!_sortInProgress)
        {
            return;
        }

        int subpasses = Mathf.Clamp(maxSubpasses, 0, TotalSortPasses);
        // 2. Radix passes
        for (int i = 0; i < subpasses && _currentBit < MaxKeyBits; i++)
        {
            radixSort.SetTexture("_KeyValues", keyValues0);
            radixSort.SetInt("_CurrentBit", _currentBit);

#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                Graphics.Blit(null, prefixSums, radixSort, 0);
            }
            else
#endif
            {
                VRCGraphics.Blit(null, prefixSums, radixSort, 0);
            }

            prefixSums.GenerateMips();

#if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (useEditorOps)
            {
                Graphics.Blit(null, keyValues1, radixSort, 1);
            }
            else
#endif
            {
                VRCGraphics.Blit(null, keyValues1, radixSort, 1);
            }

            // Ping-pong the buffers
            RenderTexture temp = keyValues0;
            keyValues0 = keyValues1;
            keyValues1 = temp;

            _currentBit += BitsPerPass;
        }

        if (_currentBit >= MaxKeyBits)
        {
            _sortInProgress = false;
        }
    }

    public bool IsSortComplete()
    {
        return !_sortInProgress;
    }

    public bool SetPipelinedPassesPerFrame(int value)
    {
        int clampedValue = Mathf.Clamp(value, 1, TotalSortPasses);
        if (pipelinedPassesPerFrame == clampedValue)
        {
            return false;
        }
        pipelinedPassesPerFrame = clampedValue;
        return true;
    }

    int GetPipelinedPassesPerFrame()
    {
        return Mathf.Clamp(pipelinedPassesPerFrame, 1, TotalSortPasses);
    }

    public void CancelSort()
    {
        _sortInProgress = false;
        _currentBit = 0;
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

            copyMaterial.SetTexture("_MainTex", keyValues0);

            RenderTexture active = RenderTexture.active;
            Graphics.SetRenderTarget(target, 0, CubemapFace.Unknown, slice);
            GL.Clear(false, true, Color.clear);
            DrawFullscreenQuad(copyMaterial);
            RenderTexture.active = active;
            return;
        }
#endif

        VRCGraphics.Blit(keyValues0, target, 0, slice);
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
            Debug.LogError($"RadixSort: Texture size ({keyValues0.width}x{keyValues0.height}) is smaller than required ({_OptimalImageSizeX}x{_OptimalImageSizeY}). Please resize the textures.");
            return;
        }

        Vector2 scale = new Vector2((float)_OptimalImageSizeX / keyValues0.width, (float)_OptimalImageSizeY / keyValues0.height);

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
    }
}
