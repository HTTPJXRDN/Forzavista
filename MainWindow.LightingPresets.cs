using System.IO;
using System.Windows;

namespace ForzavistaFreeRoam;

public partial class MainWindow
{
    private DrlColorCycle _drlCustomCycle = DrlColorCycle.Default;
    private bool _drlUseCustomCycle;
    private DrlColorSelection _drlPresetChoice = DrlColorSelection.Stock;
    private static string LightingPresetPath => Path.Combine(BindingDirectory, "lighting-presets.json");

    private LightingPreset CaptureLightingPreset()
    {
        var selection = _drlColorStatus.Selection ?? _drlPresetChoice;
        return new LightingPreset
        {
            Name = "Current settings", Mode = selection.Mode,
            R = _pickedDrlColor.R, G = _pickedDrlColor.G, B = _pickedDrlColor.B,
            Colors = _drlCustomCycle.Colors.ToArray(), UseCustomCycle = _drlUseCustomCycle,
            CycleSeconds = _drlRainbowPeriodSeconds, BrightnessPercent = _drlBrightnessPercent,
            Strobe = selection.Strobe, StrobeFlashesPerSecond = _drlStrobeFlashesPerSecond,
            NativeStrobe = _drlUseNativeStrobe, FadeEnabled = _lightFadeEnabled, FadeSeconds = _lightFadeSeconds
        };
    }

    private async void LightingPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _drlPickerOpen || _bindMode || _drlColorPreparation is not null || _signalPreparation is not null) return;
        _drlPickerOpen = true;
        try
        {
            var dialog = new LightingPresetWindow(CaptureLightingPreset(), LightingPresetPath) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Selection is not { } preset || _closing) return;
            await ApplyLightingPresetAsync(preset);
        }
        finally { _drlPickerOpen = false; RefreshDrlColorControls(); }
    }

    private async Task ApplyLightingPresetAsync(LightingPreset preset)
    {
        preset = preset.Validated();
        // Updating several options is one UI operation. Suppress checkbox handlers
        // so no partial preset can start a native action before its final selection.
        _drlSpeedInitialized = false;
        _lightFadeInitialized = false;
        try
        {
            _pickedDrlColor = DrlColorSelection.Fixed(preset.R, preset.G, preset.B);
            _drlCustomCycle = new DrlColorCycle(preset.Colors);
            _drlUseCustomCycle = preset.UseCustomCycle || preset.Mode == DrlColorMode.CustomCycle;
            _drlRainbowPeriodSeconds = preset.CycleSeconds;
            _drlBrightnessPercent = preset.BrightnessPercent;
            _drlStrobeFlashesPerSecond = preset.StrobeFlashesPerSecond;
            _drlUseNativeStrobe = preset.NativeStrobe;
            _lightFadeEnabled = preset.FadeEnabled;
            _lightFadeSeconds = preset.FadeSeconds;
            DrlRgbSpeedSlider.Value = 13 - _drlRainbowPeriodSeconds;
            DrlBrightnessSlider.Value = _drlBrightnessPercent;
            DrlStrobeSpeedSlider.Value = _drlStrobeFlashesPerSecond;
            DrlNativeStrobeCheck.IsChecked = _drlUseNativeStrobe;
            DrlCustomCycleCheck.IsChecked = _drlUseCustomCycle;
            LightFadeCheck.IsChecked = _lightFadeEnabled;
            LightFadeDurationSlider.Value = _lightFadeSeconds;
        }
        finally { _drlSpeedInitialized = true; _lightFadeInitialized = true; }
        SaveDrlColorPreference(); SaveLightFadePreference();
        RefreshDrlRgbSpeedLabel(); RefreshDrlBrightnessLabel(); RefreshDrlStrobeSpeedLabel();
        RefreshDrlColorControls();
        _drlRgbReturnColor = _pickedDrlColor;
        await SetDrlColorAsync(preset.ToSelection()); // Existing fresh car/material/ownership checks.
    }

    private async void DrlCycleEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _drlPickerOpen || _bindMode || _drlColorPreparation is not null || _signalPreparation is not null) return;
        _drlPickerOpen = true;
        try
        {
            var dialog = new LightingCycleWindow(_drlCustomCycle) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Selection is not { } cycle || _closing) return;
            _drlCustomCycle = cycle;
            _drlUseCustomCycle = true;
            // Configure the choice without starting effects merely by editing colors.
            _drlSpeedInitialized = false;
            DrlCustomCycleCheck.IsChecked = true;
            _drlSpeedInitialized = true;
            SaveDrlColorPreference();
            if (_drlColorStatus.Selection is { IsCycling: true } current)
                await SetDrlColorAsync(DrlColorSelection.CustomCycle(cycle, _drlRainbowPeriodSeconds).WithStrobe(current.Strobe));
            else if (_drlColorStatus.Selection is null)
                _drlPresetChoice = DrlColorSelection.CustomCycle(cycle, _drlRainbowPeriodSeconds);
        }
        finally { _drlPickerOpen = false; RefreshDrlColorControls(); }
    }

    private async void DrlCustomCycle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_drlSpeedInitialized || _closing) return;
        _drlUseCustomCycle = DrlCustomCycleCheck.IsChecked == true;
        _drlPreferenceSaveTimer.Stop(); _drlPreferenceSaveTimer.Start();
        if (_drlColorStatus.Selection is { IsCycling: true } current)
            await SetDrlColorAsync(ChosenDrlCycle().WithStrobe(current.Strobe));
        else if (_drlColorStatus.Selection is null)
            _drlPresetChoice = ChosenDrlCycle();
        RefreshDrlColorControls();
    }

    private DrlColorSelection ChosenDrlCycle() => _drlUseCustomCycle
        ? DrlColorSelection.CustomCycle(_drlCustomCycle, _drlRainbowPeriodSeconds)
        : DrlColorSelection.Rainbow(_drlRainbowPeriodSeconds);

    private void RefreshLightingPresetControls(bool idle)
    {
        LightingPresetsButton.IsEnabled = idle && !_drlPickerOpen;
        DrlCycleEditButton.IsEnabled = idle && !_drlPickerOpen;
        DrlCustomCycleCheck.IsEnabled = idle && !_drlColorStatus.NeedsReset && _drlColorStatus.Mode != DrlColorMode.Hazards;
        DrlCycleSummary.Text = _drlUseCustomCycle
            ? string.Join(" → ", _drlCustomCycle.Colors.Select(c => c.Hex))
            : "Full rainbow";
    }
}
