using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RadixSort : UdonSharpBehaviour
{
    [SerializeField] public Material computeKeyValues;
    [SerializeField] public Material radixSort;

    [SerializeField] public RenderTexture keyValues0;
    [SerializeField] public RenderTexture keyValues1;
    [SerializeField] public RenderTexture prefixSums;

    [HideInInspector] [SerializeField] public int elementCount = 1024 * 1024;

    public const int BitsPerPass = 4;
    public const int TotalSortPasses = 8;
    public const int MaxKeyBits = BitsPerPass * TotalSortPasses;
    private const int groupSizeLog2 = 4;

    private int _currentBit;
    private bool _sortInProgress;

    public void Sort()
    {
        BeginSort();
        StepSort(TotalSortPasses);
    }

    public void BeginSort()
    {
        // Runtime uniforms that vary each frame
        setStaticUniforms();

        // 1. Evaluate key values
        VRCGraphics.Blit(null, keyValues0, computeKeyValues);

        radixSort.SetTexture("_PrefixSums", prefixSums);
        _currentBit = 0;
        _sortInProgress = true;
    }

    public void StepSort(int maxSubpasses)
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

            VRCGraphics.Blit(null, prefixSums, radixSort, 0);
            prefixSums.GenerateMips();
            VRCGraphics.Blit(null, keyValues1, radixSort, 1);

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

    public RenderTexture GetSortedKeyValues()
    {
        return keyValues0;
    }

    public void CancelSort()
    {
        _sortInProgress = false;
        _currentBit = 0;
    }

    public void CopySortedOrder(RenderTexture target)
    {
        CopySortedOrder(target, 0);
    }

    public void CopySortedOrder(RenderTexture target, int slice)
    {
        if (target == null)
        {
            return;
        }

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
