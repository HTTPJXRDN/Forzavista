using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ForzavistaFreeRoam;

public partial class MainWindow
{
    private bool _lightFadeEnabled, _lightFadeInitialized;
    private double _lightFadeSeconds = LightFadePolicy.DefaultSeconds;
    private readonly DispatcherTimer _lightFadeSaveTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(350) };
    private sealed record LightFadePreference(bool Enabled = false, double Seconds = LightFadePolicy.DefaultSeconds);
    private static string LightFadePreferencePath => Path.Combine(BindingDirectory, "light-fade.json");

    private void InitializeLightFade()
    {
        try
        {
            if (File.Exists(LightFadePreferencePath) && JsonSerializer.Deserialize<LightFadePreference>(
                File.ReadAllText(LightFadePreferencePath)) is { } saved)
            { _lightFadeEnabled = saved.Enabled; _lightFadeSeconds = LightFadePolicy.Duration(saved.Seconds); }
        }
        catch (Exception error) { SessionLog.Write("light_fade_preference_load_failed", error.Message); }
        LightFadeCheck.IsChecked = _lightFadeEnabled;
        LightFadeDurationSlider.Value = _lightFadeSeconds;
        _lightFadeInitialized = true;
        RefreshLightFadeControls();
        _lightFadeSaveTimer.Tick += (_, _) => { _lightFadeSaveTimer.Stop(); SaveLightFadePreference(); };
        Closing += (_, _) =>
        {
            if (!_lightFadeSaveTimer.IsEnabled) return;
            _lightFadeSaveTimer.Stop(); SaveLightFadePreference();
        };
    }

    private void LightFade_Changed(object sender, RoutedEventArgs e)
    {
        if (!_lightFadeInitialized || _closing) return;
        _lightFadeEnabled = LightFadeCheck.IsChecked == true;
        RefreshLightFadeControls();
        _lightFadeSaveTimer.Stop(); _lightFadeSaveTimer.Start();
    }

    private void LightFadeDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_lightFadeInitialized || _closing) return;
        _lightFadeSeconds = LightFadePolicy.Duration(e.NewValue);
        RefreshLightFadeControls();
        _lightFadeSaveTimer.Stop(); _lightFadeSaveTimer.Start();
    }

    private void RefreshLightFadeControls()
    {
        LightFadeCheck.IsEnabled = !_closing && !_bindMode;
        LightFadeDurationSlider.IsEnabled = !_closing && !_bindMode && _lightFadeEnabled;
        LightFadeDurationText.Text = $"{_lightFadeSeconds:0.0}s";
    }

    private void SaveLightFadePreference()
    {
        try
        {
            Directory.CreateDirectory(BindingDirectory);
            File.WriteAllText(LightFadePreferencePath, JsonSerializer.Serialize(new LightFadePreference(_lightFadeEnabled, _lightFadeSeconds)));
        }
        catch (Exception error) { SessionLog.Write("light_fade_preference_save_failed", error.Message); }
    }

}
