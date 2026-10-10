using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ForzavistaFreeRoam;

public partial class LightingPresetWindow : Window
{
    private readonly LightingPreset _current;
    private readonly string _path;
    private readonly bool _readOnly;
    private List<LightingPreset> _presets = [];
    internal LightingPreset? Selection { get; private set; }

    internal LightingPresetWindow(LightingPreset current, string path)
    {
        _current = current;
        _path = path;
        InitializeComponent();
        try { _presets = LightingPresetStore.Load(path); }
        catch (Exception error)
        {
            _readOnly = true;
            PresetStatus.Text = "The preset file could not be loaded. It has been preserved. " + error.Message;
            SessionLog.Write("lighting_presets_load_failed", error.Message);
        }
        RefreshList();
        if (!_readOnly) PresetStatus.Text = "Select to preview, APPLY to use. Saving an existing name updates it.";
    }

    private void RefreshList(string? selectedName = null)
    {
        PresetList.ItemsSource = null;
        PresetList.ItemsSource = _presets;
        PresetList.SelectedItem = _presets.FirstOrDefault(p => p.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        RefreshButtons();
    }
    private void RefreshButtons()
    {
        SaveButton.IsEnabled = !_readOnly;
        DeleteButton.IsEnabled = !_readOnly && PresetList.SelectedItem is LightingPreset;
        ApplyButton.IsEnabled = PresetList.SelectedItem is LightingPreset;
    }
    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyButton is null) return;
        PresetDescription.Text = (PresetList.SelectedItem as LightingPreset)?.Description ?? "";
        if (PresetList.SelectedItem is LightingPreset selected) PresetName.Text = selected.Name;
        RefreshButtons();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        try
        {
            var preset = (_current with { Name = PresetName.Text }).Validated();
            var next = _presets.ToList();
            int index = next.FindIndex(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) next.Add(preset); else next[index] = preset;
            LightingPresetStore.Save(_path, next);
            _presets = next;
            RefreshList(preset.Name);
            PresetStatus.Text = $"Saved “{preset.Name}”.";
        }
        catch (Exception error) { PresetStatus.Text = "Preset was not saved. " + error.Message; }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || PresetList.SelectedItem is not LightingPreset selected) return;
        try
        {
            var next = _presets.Where(p => p != selected).ToList();
            LightingPresetStore.Save(_path, next);
            _presets = next; RefreshList();
            PresetStatus.Text = $"Deleted “{selected.Name}”. The previous library is kept in lighting-presets.json.bak.";
        }
        catch (Exception error) { PresetStatus.Text = "Preset was not deleted. " + error.Message; }
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not LightingPreset preset) return;
        Selection = preset.Validated(); DialogResult = true;
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
}
