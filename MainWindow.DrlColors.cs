using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ForzavistaFreeRoam;

public partial class MainWindow
{
    private readonly DispatcherTimer _drlColorTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(150) };
    private NativeDrlColorStatus _drlColorStatus = new(DrlColorMode.Original, false, false, "Original DRL color.");
    private DrlColorSelection _pickedDrlColor = DrlColorSelection.Fixed(255, 255, 255);
    private DrlColorSelection _drlRgbReturnColor = DrlColorSelection.Stock;
    private DrlColorSelection _drlHazardsReturnColor = DrlColorSelection.Stock;
    private CancellationTokenSource? _drlColorPreparation;
    private bool _updatingDrlColor;
    private bool _drlPickerOpen;
    private double _drlRainbowPeriodSeconds = DrlColorSelection.DefaultRainbowPeriodSeconds;
    private double _drlBrightnessPercent = DrlColorSelection.DefaultBrightnessPercent;
    private double _drlStrobeFlashesPerSecond = DrlColorSelection.DefaultStrobeFlashesPerSecond;
    private bool _drlUseNativeStrobe = true;
    private bool _drlSpeedInitialized;
    private readonly DispatcherTimer _drlPreferenceSaveTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(350) };
    private sealed record DrlColorPreference(byte R, byte G, byte B,
        double RainbowPeriodSeconds = DrlColorSelection.DefaultRainbowPeriodSeconds,
        double BrightnessPercent = DrlColorSelection.DefaultBrightnessPercent,
        double StrobeFlashesPerSecond = DrlColorSelection.DefaultStrobeFlashesPerSecond,
        bool UseNativeStrobe = true, bool UseCustomCycle = false, DrlCycleColor[]? CycleColors = null);
    private static string DrlColorPreferencePath => Path.Combine(BindingDirectory, "drl-color.json");

    private void InitializeDrlColors()
    {
        // Remember a picker choice, never automatically resume writes on startup.
        try
        {
            if (File.Exists(DrlColorPreferencePath) &&
                JsonSerializer.Deserialize<DrlColorPreference>(File.ReadAllText(DrlColorPreferencePath)) is { } saved)
            {
                _pickedDrlColor = DrlColorSelection.Fixed(saved.R, saved.G, saved.B);
                _drlUseNativeStrobe = saved.UseNativeStrobe;
                if (saved.CycleColors is not null) _drlCustomCycle = new DrlColorCycle(saved.CycleColors);
                _drlUseCustomCycle = saved.UseCustomCycle;
                if (double.IsFinite(saved.RainbowPeriodSeconds) && saved.RainbowPeriodSeconds >= 1 &&
                    saved.RainbowPeriodSeconds <= 12)
                    _drlRainbowPeriodSeconds = Math.Round(saved.RainbowPeriodSeconds * 2) / 2;
                if (double.IsFinite(saved.BrightnessPercent) && saved.BrightnessPercent is >= 0 and <= DrlColorSelection.MaximumBrightnessPercent)
                    _drlBrightnessPercent = Math.Round(saved.BrightnessPercent);
                if (double.IsFinite(saved.StrobeFlashesPerSecond) &&
                    saved.StrobeFlashesPerSecond >= DrlColorSelection.MinimumStrobeFlashesPerSecond &&
                    saved.StrobeFlashesPerSecond <= DrlColorSelection.MaximumStrobeFlashesPerSecond)
                    _drlStrobeFlashesPerSecond = Math.Round(saved.StrobeFlashesPerSecond);
            }
        }
        catch (Exception error) { SessionLog.Write("drl_color_preference_load_failed", error.Message); }
        DrlRgbSpeedSlider.Value = 13 - _drlRainbowPeriodSeconds;
        DrlBrightnessSlider.Value = _drlBrightnessPercent;
        DrlStrobeSpeedSlider.Value = _drlStrobeFlashesPerSecond;
        DrlNativeStrobeCheck.IsChecked = _drlUseNativeStrobe;
        DrlCustomCycleCheck.IsChecked = _drlUseCustomCycle;
        _drlSpeedInitialized = true;
        RefreshDrlRgbSpeedLabel();
        RefreshDrlBrightnessLabel();
        RefreshDrlStrobeSpeedLabel();
        _drlPreferenceSaveTimer.Tick += (_, _) =>
        {
            _drlPreferenceSaveTimer.Stop();
            SaveDrlColorPreference();
        };
        Closing += (_, _) =>
        {
            if (!_drlPreferenceSaveTimer.IsEnabled) return;
            _drlPreferenceSaveTimer.Stop();
            SaveDrlColorPreference();
        };
        _drlColorTimer.Tick += async (_, _) => await UpdateDrlColorsAsync();
        Loaded += (_, _) => _drlColorTimer.Start();
        RefreshDrlColorControls();
    }

    private void SaveDrlColorPreference()
    {
        try
        {
            Directory.CreateDirectory(BindingDirectory);
            File.WriteAllText(DrlColorPreferencePath, JsonSerializer.Serialize(new DrlColorPreference(
                _pickedDrlColor.R, _pickedDrlColor.G, _pickedDrlColor.B, _drlRainbowPeriodSeconds,
                _drlBrightnessPercent, _drlStrobeFlashesPerSecond, _drlUseNativeStrobe, _drlUseCustomCycle, _drlCustomCycle.Colors.ToArray())));
        }
        catch (Exception error) { SessionLog.Write("drl_color_preference_save_failed", error.Message); }
    }

    private void DrlRgbSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_drlSpeedInitialized || _closing) return;
        _drlRainbowPeriodSeconds = Math.Round((13 - e.NewValue) * 2) / 2;
        RefreshDrlRgbSpeedLabel();
        // Coalesce preference saves; animation reads the latest choice on its next frame.
        _drlPreferenceSaveTimer.Stop();
        _drlPreferenceSaveTimer.Start();
    }

    private void RefreshDrlRgbSpeedLabel() =>
        DrlRgbSpeedText.Text = $"{_drlRainbowPeriodSeconds:0.0}s / cycle";

    private void DrlBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_drlSpeedInitialized || _closing) return;
        _drlBrightnessPercent = Math.Round(e.NewValue);
        RefreshDrlBrightnessLabel();
        _drlPreferenceSaveTimer.Stop();
        _drlPreferenceSaveTimer.Start();
    }

    private void RefreshDrlBrightnessLabel() => DrlBrightnessText.Text = $"{_drlBrightnessPercent:0}%" +
        (_drlBrightnessPercent > 100 ? " · BOOST" : "");

    private void DrlStrobeSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_drlSpeedInitialized || _closing) return;
        _drlStrobeFlashesPerSecond = Math.Round(e.NewValue);
        RefreshDrlStrobeSpeedLabel();
        RefreshDrlColorTimerInterval();
        _drlPreferenceSaveTimer.Stop();
        _drlPreferenceSaveTimer.Start();
    }

    private void RefreshDrlStrobeSpeedLabel() =>
        DrlStrobeSpeedText.Text = _drlUseNativeStrobe ? "GAME TIMING" : $"{_drlStrobeFlashesPerSecond:0} flashes / sec";

    private async void DrlNativeStrobe_Changed(object sender, RoutedEventArgs e)
    {
        if (!_drlSpeedInitialized || _closing) return;
        _drlUseNativeStrobe = DrlNativeStrobeCheck.IsChecked == true;
        RefreshDrlStrobeSpeedLabel();
        RefreshDrlColorControls();
        _drlPreferenceSaveTimer.Stop();
        _drlPreferenceSaveTimer.Start();
        // Selecting the timing option alone never starts strobe or color writes.
        if (_drlColorStatus.Strobe && _drlColorStatus.Selection is { } selection)
            await SetDrlColorAsync(selection.WithNativeStrobe(_drlUseNativeStrobe));
    }

    private void RefreshDrlColorTimerInterval()
    {
        var selection = (_drlColorStatus.Selection ?? DrlColorSelection.Stock)
            .WithStrobeSpeed(_drlStrobeFlashesPerSecond);
        var interval = LightingAnimationPolicy.Interval(selection, _lightStatus.Fading,
            _drlColorStatus.Suspended || _drlColorStatus.NeedsReset);
        if (_drlColorTimer.Interval != interval) _drlColorTimer.Interval = interval;
    }

    private async void DrlColorPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _drlPickerOpen || _bindMode || _drlColorPreparation is not null) return;
        _drlPickerOpen = true;
        try
        {
            var picker = new DrlColorPickerWindow(_pickedDrlColor) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selection is not { } selection) return;
            if (selection.Mode == DrlColorMode.Fixed)
            {
                _pickedDrlColor = selection;
                SaveDrlColorPreference();
            }
            await SetDrlColorAsync(selection.Mode == DrlColorMode.Original
                ? DrlColorSelection.Stock : selection.WithStrobe(_drlColorStatus.Strobe));
        }
        finally { _drlPickerOpen = false; RefreshDrlColorControls(); }
    }

    private async void DrlRgb_Click(object sender, RoutedEventArgs e)
    {
        var current = _drlColorStatus.Selection ?? DrlColorSelection.Stock;
        DrlColorSelection selection;
        if (current.IsCycling)
        {
            // Strobe still needs a color when RGB was enabled from Original.
            selection = _drlRgbReturnColor.Mode == DrlColorMode.Original && current.Strobe
                ? _pickedDrlColor : _drlRgbReturnColor;
            if (selection.Mode != DrlColorMode.Original) selection = selection.WithStrobe(current.Strobe);
        }
        else
        {
            _drlRgbReturnColor = current.WithStrobe(false);
            selection = ChosenDrlCycle().WithStrobe(current.Strobe);
        }
        await SetDrlColorAsync(selection);
    }

    private async void DrlStrobe_Click(object sender, RoutedEventArgs e)
    {
        var current = _drlColorStatus.Selection ?? DrlColorSelection.Stock;
        if (current.Mode == DrlColorMode.Original) current = _pickedDrlColor;
        await SetDrlColorAsync(current.WithStrobe(!_drlColorStatus.Strobe));
    }

    private bool UsesDrlHazards => LightingFeaturePolicy.ExperimentalSignalsEnabled &&
        (_drlColorStatus.Mode == DrlColorMode.Hazards ||
        !SignalPolicy.Capabilities("Steam 6.461.691.0", _sessionCarToken ?? "").Hazards &&
        NativeCarControl.HasDrlColorProfile(_sessionCarToken));

    private bool DrlHazardsAvailable => UsesDrlHazards && _lightStatus.Available &&
        !_drlColorStatus.NeedsReset && !_signalStatus.NeedsReset;

    private string DrlHazardsLabel => "HAZARDS (DRL ONLY): " +
        (!DrlHazardsAvailable ? "—" : _drlColorStatus.Suspended ? "PAUSED" :
            _drlColorStatus.Mode == DrlColorMode.Hazards ? "ON" : "OFF");

    private async Task ToggleDrlHazardsAsync()
    {
        if (!DrlHazardsAvailable) return;
        if (_drlColorStatus.Mode == DrlColorMode.Hazards)
            await SetDrlColorAsync(_drlHazardsReturnColor);
        else
        {
            _drlHazardsReturnColor = _drlColorStatus.Selection ?? DrlColorSelection.Stock;
            await SetDrlColorAsync(DrlColorSelection.Hazards);
        }
    }

    private async Task SetDrlColorAsync(DrlColorSelection selection)
    {
        if (selection.Mode == DrlColorMode.Hazards && !LightingFeaturePolicy.ExperimentalSignalsEnabled)
        {
            SetControlsStatus(LightingFeaturePolicy.SignalsDeferredMessage, Dim);
            return;
        }
        if (_closing || _drlColorPreparation is not null || _signalPreparation is not null)
        {
            if (!_closing) RejectBusyAction("drlcolor", "A lamp search is already executing");
            return;
        }
        _drlPresetChoice = selection;
        if (selection.Mode == DrlColorMode.Original)
        {
            if (!await TryEnterActionAsync("drlcolororiginal")) return;
            try
            {
                var result = await Task.Run(NativeCarControl.RestoreTrackedDrlColor);
                _drlColorStatus = NativeCarControl.GetDrlColorStatus();
                SetControlsStatus(result.Message, result.Success ? Good : Warn);
            }
            finally { _actionGate.Release(); RefreshDrlColorControls(); }
            return;
        }
        selection = selection.WithBrightness(_drlBrightnessPercent).WithStrobeSpeed(_drlStrobeFlashesPerSecond)
            .WithNativeStrobe(_drlUseNativeStrobe);
        // Choosing a color while disconnected only saves the choice.
        if (!_lightStatus.Available)
        {
            SetControlsStatus("DRL color selected. Load a supported car to apply it.", Dim);
            RefreshDrlColorControls();
            return;
        }
        if (!NativeCarControl.HasDrlColorProfile(_sessionCarToken))
        {
            SetControlsStatus("This car's DRL color still needs mapping. The selected color is saved.", Warn);
            RefreshDrlColorControls();
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _drlColorPreparation = cancellation;
        RefreshDrlColorControls();
        try
        {
            SetControlsStatus("Finding current-car DRL color materials…", Dim);
            var prepared = await Task.Run(() => NativeCarControl.PrepareDrlColor(selection, cancellation.Token));
            if (prepared.UnavailableReason is { } unavailable)
            {
                SessionLog.Write("drl_color_unavailable", unavailable, gamePid: _sessionProcessId, carToken: _sessionCarToken);
                if (!_closing && !cancellation.IsCancellationRequested) SetControlsStatus(unavailable, Warn);
                return;
            }
            if (_closing || cancellation.IsCancellationRequested || !await TryEnterActionAsync("drlcolor")) return;
            try
            {
                var result = await Task.Run(() => NativeCarControl.ApplyDrlColor(prepared, cancellation.Token));
                _drlColorStatus = NativeCarControl.GetDrlColorStatus();
                SetControlsStatus(result.Message, result.Success ? Good : Warn);
            }
            finally { _actionGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            SessionLog.Write("drl_color_action_error", error.ToString(), gamePid: _sessionProcessId, carToken: _sessionCarToken);
            if (!_closing && !cancellation.IsCancellationRequested) SetControlsStatus(error.Message, Warn);
        }
        finally
        {
            if (ReferenceEquals(_drlColorPreparation, cancellation)) _drlColorPreparation = null;
            RefreshDrlColorControls();
        }
    }

    private Task UpdateDrlColorsAsync() => UpdateLightingAnimationAsync();

    private void CancelDrlColorPreparation()
    {
        _drlColorPreparation?.Cancel();
        NativeCarControl.InvalidateDrlColorPreparation();
    }

    private void RefreshDrlColorControls()
    {
        if (_lightFadeInitialized) RefreshLightFadeControls();
        // Active rainbow or strobe needs the finer cadence. TryEnter and the in-flight
        // flag still drop late frames instead of queuing them behind actions.
        RefreshDrlColorTimerInterval();
        DrlColorPreview.Background = new SolidColorBrush(Color.FromRgb(_pickedDrlColor.R, _pickedDrlColor.G, _pickedDrlColor.B));
        var paused = _drlColorStatus.Suspended || _drlColorStatus.NeedsReset;
        var hazards = _drlColorStatus.Mode == DrlColorMode.Hazards;
        bool cycling = _drlColorStatus.Mode is DrlColorMode.Rainbow or DrlColorMode.CustomCycle;
        string cycleLabel = _drlUseCustomCycle || _drlColorStatus.Mode == DrlColorMode.CustomCycle ? "CYCLE" : "RGB";
        DrlRgbButton.Content = cycleLabel + ": " + (cycling ? paused ? "PAUSED" : "ON" : "OFF");
        DrlRgbButton.Style = (Style)FindResource(cycling ? "PinkFilled" : "GhostButton");
        DrlStrobeButton.Content = _drlColorStatus.Strobe
            ? paused ? "STROBE: PAUSED" : "STROBE: ON" : "STROBE: OFF";
        DrlStrobeButton.Style = (Style)FindResource(_drlColorStatus.Strobe ? "PinkFilled" : "GhostButton");
        DrlColorModeText.Text = _drlColorStatus.NeedsReset ? "COLOR: STOPPED — RESET REQUIRED" :
            _drlColorStatus.Suspended ? "COLOR: PAUSED FOR INDICATORS" :
            _drlColorStatus.Mode == DrlColorMode.Fixed ? "COLOR: " + _drlColorStatus.Hex :
            hazards ? "COLOR: AMBER DRL HAZARDS" :
            _drlColorStatus.Mode == DrlColorMode.CustomCycle ? "COLOR: CUSTOM CYCLE" :
            _drlColorStatus.Mode == DrlColorMode.Rainbow ? "COLOR: RGB FADE" : "COLOR: ORIGINAL";
        if (_drlColorStatus.Strobe && !paused) DrlColorModeText.Text +=
            _drlColorStatus.Selection?.NativeStrobe == true ? " · NATIVE STROBE" : " · CUSTOM STROBE";
        var idle = !_closing && !_bindMode && _drlColorPreparation is null && _signalPreparation is null;
        DrlRgbSpeedSlider.IsEnabled = idle;
        DrlBrightnessSlider.IsEnabled = idle;
        DrlNativeStrobeCheck.IsEnabled = idle && !hazards && !_drlColorStatus.NeedsReset;
        DrlStrobeSpeedSlider.IsEnabled = idle && !_drlUseNativeStrobe;
        CustomStrobeSettings.Visibility = _drlUseNativeStrobe ? Visibility.Collapsed : Visibility.Visible;
        DrlColorPickerButton.IsEnabled = idle && !hazards;
        DrlRgbButton.IsEnabled = idle && !hazards && _lightStatus.Available && !_drlColorStatus.NeedsReset &&
            (cycling || NativeCarControl.HasDrlColorProfile(_sessionCarToken));
        DrlStrobeButton.IsEnabled = idle && !hazards && _lightStatus.Available && !_drlColorStatus.NeedsReset &&
            (_drlColorStatus.Strobe || NativeCarControl.HasDrlColorProfile(_sessionCarToken));
        DrlColorHint.Text = _drlColorPreparation is not null ? "Finding DRL materials…" :
            _drlColorStatus.NeedsReset ? "DRL effects stopped. Use RESET STATE; details are in STATUS." :
            _drlColorStatus.Suspended ? "DRL effects paused for indicators." :
            _lightStatus.Available && !NativeCarControl.HasDrlColorProfile(_sessionCarToken)
                ? "This car’s DRL color material still needs mapping." :
            _drlColorStatus.Selection is { Strobe: true, NativeStrobe: true }
                ? "Native strobe needs front DRLs and engine ON." : "Front DRLs must be ON to use effects.";
        RefreshLightingPresetControls(idle);
        RefreshSignalLabels();
        SetSignalButtonsEnabled(idle);
    }
}
