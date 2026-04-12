using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace GaussianSplatting
{

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GaussianSplatRendererUI : UdonSharpBehaviour
{
    public GaussianSplatRenderer gaussianSplatRenderer;
    public Text currentSplatText;
    public Text minSortDistanceText;
    public Text maxSortDistanceText;
    public Text cameraQuantizationText;
    public Text sortingStepsText;
    public Button alwaysUpdateButton;
    public Slider shBandSlider;
    public Text shBandText;
    public Button vrcLightVolumesButton;
    public Slider antiAliasingSlider;
    public Text antiAliasingText;
    public Slider lightVolumeIntensitySlider;
    public Text lightVolumeIntensityText;
    public Text gaussianScaleText;
    public Slider alphaCutoffSlider;
    public Text alphaCutoffText;
    public Button splatScrollUpButton;
    public Button splatScrollDownButton;
    public Button[] splatButtons;
    public int[] splatButtonIndices;
    public string[] splatButtonLabels;

    [SerializeField] float gaussianScaleStep = 0.1f;
    [SerializeField] float sortDistanceStep = 5.0f;
    [SerializeField] float cameraQuantizationStep = 0.05f;

    Color _selectedSplatColor = new Color(0.55f, 0.39f, 0.12f, 1.0f);
    Color _defaultSplatColor = new Color(0.2f, 0.2f, 0.24f, 1.0f);
    Color _scrollEnabledColor = new Color(0.15f, 0.24f, 0.36f, 1.0f);
    Color _scrollDisabledColor = new Color(0.1f, 0.1f, 0.12f, 1.0f);
    Color _toggleEnabledColor = new Color(0.18f, 0.4f, 0.24f, 1.0f);
    Color _toggleDisabledColor = new Color(0.3f, 0.16f, 0.14f, 1.0f);

    int _splatListStartIndex;
    bool _sliderValuesInitialized;
    float _lastShBandSliderValue;
    float _lastAntiAliasingSliderValue;
    float _lastLightVolumeIntensitySliderValue;
    float _lastAlphaCutoffSliderValue;

    void Start()
    {
        RefreshUI();
    }

    void Update()
    {
        RefreshUI();
    }

    string FormatFloat(float value)
    {
        float roundedValue = Mathf.Round(value * 100.0f) * 0.01f;
        return roundedValue.ToString();
    }

    void SetButtonEnabled(Button button, bool enabled, string label, Color enabledColor, Color disabledColor)
    {
        if (button == null)
        {
            return;
        }

        button.gameObject.SetActive(true);
        button.interactable = enabled;
        ApplyButtonVisual(button, label, enabled ? enabledColor : disabledColor);
    }

    void ApplyButtonVisual(Button button, string labelText, Color backgroundColor)
    {
        if (button == null)
        {
            return;
        }

        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            image.color = backgroundColor;
        }

        ColorBlock colors = button.colors;
        colors.normalColor = backgroundColor;
        colors.highlightedColor = backgroundColor * 1.1f;
        colors.pressedColor = backgroundColor * 0.85f;
        colors.selectedColor = backgroundColor;
        colors.disabledColor = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, 0.4f);
        button.colors = colors;

        Text label = button.GetComponentInChildren<Text>();
        if (label != null)
        {
            label.text = labelText;
        }
    }

    void RefreshSplatButtons()
    {
        if (splatButtons == null || splatButtonIndices == null || splatButtonLabels == null)
        {
            return;
        }

        int visibleButtonCount = splatButtons.Length;
        int totalSplatCount = splatButtonLabels.Length;
        if (splatButtonIndices.Length < totalSplatCount)
        {
            totalSplatCount = splatButtonIndices.Length;
        }

        if (totalSplatCount == 0)
        {
            for (int i = 0; i < visibleButtonCount; i++)
            {
                if (splatButtons[i] != null)
                {
                    SetButtonEnabled(splatButtons[i], false, "", _defaultSplatColor, _scrollDisabledColor);
                }
            }

            SetButtonEnabled(splatScrollUpButton, false, "Up", _scrollEnabledColor, _scrollDisabledColor);
            SetButtonEnabled(splatScrollDownButton, false, "Down", _scrollEnabledColor, _scrollDisabledColor);
            return;
        }

        int maxStartIndex = Mathf.Max(0, totalSplatCount - visibleButtonCount);
        if (gaussianSplatRenderer.splatObjectIndex < _splatListStartIndex)
        {
            _splatListStartIndex = gaussianSplatRenderer.splatObjectIndex;
        }
        else if (gaussianSplatRenderer.splatObjectIndex >= _splatListStartIndex + visibleButtonCount)
        {
            _splatListStartIndex = gaussianSplatRenderer.splatObjectIndex - visibleButtonCount + 1;
        }
        _splatListStartIndex = Mathf.Clamp(_splatListStartIndex, 0, maxStartIndex);

        for (int i = 0; i < visibleButtonCount; i++)
        {
            Button slotButton = splatButtons[i];
            if (slotButton == null)
            {
                continue;
            }

            int splatDataIndex = _splatListStartIndex + i;
            bool hasSplat = splatDataIndex < totalSplatCount;
            if (!hasSplat)
            {
                SetButtonEnabled(slotButton, false, "", _defaultSplatColor, _scrollDisabledColor);
                continue;
            }

            bool isCurrent = gaussianSplatRenderer.splatObjectIndex == splatButtonIndices[splatDataIndex];
            string label = splatButtonLabels[splatDataIndex];
            if (isCurrent)
            {
                label += " (Current)";
            }

            SetButtonEnabled(slotButton, true, label, isCurrent ? _selectedSplatColor : _defaultSplatColor, _scrollDisabledColor);
        }

        SetButtonEnabled(splatScrollUpButton, _splatListStartIndex > 0, "Up", _scrollEnabledColor, _scrollDisabledColor);
        SetButtonEnabled(splatScrollDownButton, _splatListStartIndex < maxStartIndex, "Down", _scrollEnabledColor, _scrollDisabledColor);
    }

    void RefreshSortingControls()
    {
        if (minSortDistanceText != null)
        {
            minSortDistanceText.text = FormatFloat(gaussianSplatRenderer.GetMinSortDistance());
        }

        if (maxSortDistanceText != null)
        {
            maxSortDistanceText.text = FormatFloat(gaussianSplatRenderer.GetMaxSortDistance());
        }

        if (cameraQuantizationText != null)
        {
            cameraQuantizationText.text = FormatFloat(gaussianSplatRenderer.GetCameraPositionQuantization());
        }

        if (sortingStepsText != null)
        {
            sortingStepsText.text = gaussianSplatRenderer.GetSortingSteps().ToString();
        }

        if (alwaysUpdateButton != null)
        {
            bool alwaysUpdate = gaussianSplatRenderer.GetAlwaysUpdate();
            ApplyButtonVisual(alwaysUpdateButton, alwaysUpdate ? "On" : "Off", alwaysUpdate ? _toggleEnabledColor : _toggleDisabledColor);
        }
    }

    void RefreshMaterialControls()
    {
        if (vrcLightVolumesButton != null)
        {
            bool enabled = gaussianSplatRenderer.GetUseVrcLightVolumes();
            ApplyButtonVisual(vrcLightVolumesButton, enabled ? "On" : "Off", enabled ? _toggleEnabledColor : _toggleDisabledColor);
        }

        SyncShBandSlider();
        SyncAntiAliasingSlider();
        SyncLightVolumeIntensitySlider();
        SyncAlphaCutoffSlider();
    }

    bool SliderValueChanged(float currentValue, float previousValue)
    {
        return Mathf.Abs(currentValue - previousValue) > 0.0001f;
    }

    void SyncShBandSlider()
    {
        if (shBandSlider == null)
        {
            return;
        }

        int maxBand = gaussianSplatRenderer.GetSelectedSplatMaxSHBand();
        if (!Mathf.Approximately(shBandSlider.maxValue, maxBand))
        {
            shBandSlider.maxValue = maxBand;
        }

        int currentBand = gaussianSplatRenderer.GetCurrentSHBand();
        if (!_sliderValuesInitialized)
        {
            shBandSlider.value = currentBand;
            _lastShBandSliderValue = currentBand;
        }
        else if (SliderValueChanged(currentBand, _lastShBandSliderValue))
        {
            shBandSlider.value = currentBand;
            _lastShBandSliderValue = currentBand;
        }
        else if (SliderValueChanged(shBandSlider.value, _lastShBandSliderValue))
        {
            gaussianSplatRenderer.SetSHBand(Mathf.RoundToInt(shBandSlider.value));
            currentBand = gaussianSplatRenderer.GetCurrentSHBand();
            shBandSlider.value = currentBand;
            _lastShBandSliderValue = currentBand;
        }

        if (shBandText != null)
        {
            shBandText.text = currentBand.ToString();
        }
    }

    void SyncAntiAliasingSlider()
    {
        if (antiAliasingSlider == null)
        {
            return;
        }

        float currentValue = gaussianSplatRenderer.GetAntiAliasing();
        if (!_sliderValuesInitialized)
        {
            antiAliasingSlider.value = currentValue;
            _lastAntiAliasingSliderValue = currentValue;
        }
        else if (SliderValueChanged(antiAliasingSlider.value, _lastAntiAliasingSliderValue))
        {
            gaussianSplatRenderer.SetAntiAliasing(antiAliasingSlider.value);
            currentValue = gaussianSplatRenderer.GetAntiAliasing();
            antiAliasingSlider.value = currentValue;
            _lastAntiAliasingSliderValue = currentValue;
        }
        else if (SliderValueChanged(currentValue, _lastAntiAliasingSliderValue))
        {
            antiAliasingSlider.value = currentValue;
            _lastAntiAliasingSliderValue = currentValue;
        }

        if (antiAliasingText != null)
        {
            antiAliasingText.text = FormatFloat(currentValue);
        }
    }

    void SyncLightVolumeIntensitySlider()
    {
        if (lightVolumeIntensitySlider == null)
        {
            return;
        }

        float currentValue = gaussianSplatRenderer.GetLightVolumeIntensity();
        if (!_sliderValuesInitialized)
        {
            lightVolumeIntensitySlider.value = currentValue;
            _lastLightVolumeIntensitySliderValue = currentValue;
        }
        else if (SliderValueChanged(lightVolumeIntensitySlider.value, _lastLightVolumeIntensitySliderValue))
        {
            gaussianSplatRenderer.SetLightVolumeIntensity(lightVolumeIntensitySlider.value);
            currentValue = gaussianSplatRenderer.GetLightVolumeIntensity();
            lightVolumeIntensitySlider.value = currentValue;
            _lastLightVolumeIntensitySliderValue = currentValue;
        }
        else if (SliderValueChanged(currentValue, _lastLightVolumeIntensitySliderValue))
        {
            lightVolumeIntensitySlider.value = currentValue;
            _lastLightVolumeIntensitySliderValue = currentValue;
        }

        if (lightVolumeIntensityText != null)
        {
            lightVolumeIntensityText.text = FormatFloat(currentValue);
        }
    }

    void SyncAlphaCutoffSlider()
    {
        if (alphaCutoffSlider == null)
        {
            return;
        }

        float currentValue = gaussianSplatRenderer.alphaCutoff;
        if (!_sliderValuesInitialized)
        {
            alphaCutoffSlider.value = currentValue;
            _lastAlphaCutoffSliderValue = currentValue;
        }
        else if (SliderValueChanged(alphaCutoffSlider.value, _lastAlphaCutoffSliderValue))
        {
            gaussianSplatRenderer.SetAlphaCutoff(alphaCutoffSlider.value);
            currentValue = gaussianSplatRenderer.alphaCutoff;
            alphaCutoffSlider.value = currentValue;
            _lastAlphaCutoffSliderValue = currentValue;
        }
        else if (SliderValueChanged(currentValue, _lastAlphaCutoffSliderValue))
        {
            alphaCutoffSlider.value = currentValue;
            _lastAlphaCutoffSliderValue = currentValue;
        }

        if (alphaCutoffText != null)
        {
            alphaCutoffText.text = FormatFloat(currentValue);
        }
    }

    void SelectSplatSlot(int slotIndex)
    {
        if (gaussianSplatRenderer == null || splatButtonIndices == null)
        {
            return;
        }

        int splatDataIndex = _splatListStartIndex + slotIndex;
        if (splatDataIndex < 0 || splatDataIndex >= splatButtonIndices.Length)
        {
            return;
        }

        gaussianSplatRenderer.SelectSplatObject(splatButtonIndices[splatDataIndex]);
        RefreshUI();
    }

    public void SelectSplatSlot0() { SelectSplatSlot(0); }
    public void SelectSplatSlot1() { SelectSplatSlot(1); }
    public void SelectSplatSlot2() { SelectSplatSlot(2); }
    public void SelectSplatSlot3() { SelectSplatSlot(3); }
    public void SelectSplatSlot4() { SelectSplatSlot(4); }
    public void SelectSplatSlot5() { SelectSplatSlot(5); }
    public void SelectSplatSlot6() { SelectSplatSlot(6); }
    public void SelectSplatSlot7() { SelectSplatSlot(7); }
    public void SelectSplatSlot8() { SelectSplatSlot(8); }
    public void SelectSplatSlot9() { SelectSplatSlot(9); }
    public void SelectSplatSlot10() { SelectSplatSlot(10); }
    public void SelectSplatSlot11() { SelectSplatSlot(11); }
    public void SelectSplatSlot12() { SelectSplatSlot(12); }
    public void SelectSplatSlot13() { SelectSplatSlot(13); }
    public void SelectSplatSlot14() { SelectSplatSlot(14); }
    public void SelectSplatSlot15() { SelectSplatSlot(15); }

    public void ScrollSplatListUp()
    {
        _splatListStartIndex = Mathf.Max(0, _splatListStartIndex - 1);
        RefreshUI();
    }

    public void ScrollSplatListDown()
    {
        int visibleButtonCount = splatButtons == null ? 0 : splatButtons.Length;
        int totalSplatCount = splatButtonLabels == null ? 0 : splatButtonLabels.Length;
        int maxStartIndex = Mathf.Max(0, totalSplatCount - visibleButtonCount);
        _splatListStartIndex = Mathf.Min(maxStartIndex, _splatListStartIndex + 1);
        RefreshUI();
    }

    public void RefreshUI()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        if (currentSplatText != null)
        {
            currentSplatText.text = "Current Splat: " + gaussianSplatRenderer.GetCurrentSplatName();
        }

        if (gaussianScaleText != null)
        {
            gaussianScaleText.text = FormatFloat(gaussianSplatRenderer.gaussianScale);
        }

        if (alphaCutoffText != null)
        {
            alphaCutoffText.text = FormatFloat(gaussianSplatRenderer.alphaCutoff);
        }

        RefreshSortingControls();
        RefreshMaterialControls();
        RefreshSplatButtons();
        _sliderValuesInitialized = true;
    }

    public void IncreaseMinSortDistance()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetMinSortDistance(gaussianSplatRenderer.GetMinSortDistance() + sortDistanceStep);
        RefreshUI();
    }

    public void DecreaseMinSortDistance()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetMinSortDistance(gaussianSplatRenderer.GetMinSortDistance() - sortDistanceStep);
        RefreshUI();
    }

    public void IncreaseMaxSortDistance()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetMaxSortDistance(gaussianSplatRenderer.GetMaxSortDistance() + sortDistanceStep);
        RefreshUI();
    }

    public void DecreaseMaxSortDistance()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetMaxSortDistance(gaussianSplatRenderer.GetMaxSortDistance() - sortDistanceStep);
        RefreshUI();
    }

    public void IncreaseCameraQuantization()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetCameraPositionQuantization(gaussianSplatRenderer.GetCameraPositionQuantization() + cameraQuantizationStep);
        RefreshUI();
    }

    public void DecreaseCameraQuantization()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetCameraPositionQuantization(gaussianSplatRenderer.GetCameraPositionQuantization() - cameraQuantizationStep);
        RefreshUI();
    }

    public void IncreaseSortingSteps()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetSortingSteps(gaussianSplatRenderer.GetSortingSteps() + 1);
        RefreshUI();
    }

    public void DecreaseSortingSteps()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetSortingSteps(gaussianSplatRenderer.GetSortingSteps() - 1);
        RefreshUI();
    }

    public void ToggleAlwaysUpdate()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.ToggleAlwaysUpdate();
        RefreshUI();
    }

    public void ToggleVrcLightVolumes()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.ToggleVrcLightVolumes();
        RefreshUI();
    }

    public void IncreaseGaussianScale()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetGaussianScale(gaussianSplatRenderer.gaussianScale + gaussianScaleStep);
        RefreshUI();
    }

    public void DecreaseGaussianScale()
    {
        if (gaussianSplatRenderer == null)
        {
            return;
        }

        gaussianSplatRenderer.SetGaussianScale(gaussianSplatRenderer.gaussianScale - gaussianScaleStep);
        RefreshUI();
    }

}

}
