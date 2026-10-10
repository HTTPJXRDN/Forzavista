using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ForzavistaFreeRoam;

public partial class LightingCycleWindow : Window
{
    private readonly List<DrlCycleColor> _colors;
    private sealed record ColorRow(string Label, SolidColorBrush Brush);
    internal DrlColorCycle? Selection { get; private set; }

    internal LightingCycleWindow(DrlColorCycle cycle)
    {
        _colors = cycle.Colors.ToList();
        InitializeComponent();
        Refresh(0);
    }

    private void Refresh(int index)
    {
        ColorList.ItemsSource = _colors.Select((c, i) => new ColorRow($"{i + 1}.  {c.Hex}",
            new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)))).ToList();
        ColorList.SelectedIndex = Math.Clamp(index, 0, _colors.Count - 1);
        CycleStatus.Text = $"{_colors.Count} / {DrlColorCycle.MaximumColors} colors · Use CYCLE to start; the speed slider sets the full loop duration.";
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        if (AddButton is null) return;
        int index = ColorList.SelectedIndex;
        AddButton.IsEnabled = _colors.Count < DrlColorCycle.MaximumColors;
        EditButton.IsEnabled = index >= 0;
        RemoveButton.IsEnabled = index >= 0 && _colors.Count > 2;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index < _colors.Count - 1;
    }

    private void ColorList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshButtons();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void QuickColor_Click(object sender, RoutedEventArgs e)
    {
        if (_colors.Count >= DrlColorCycle.MaximumColors) { CycleStatus.Text = "The cycle already has 12 colors. Edit or remove a color first."; return; }
        var color = (Color)ColorConverter.ConvertFromString((string)((Button)sender).Tag);
        _colors.Add(new(color.R, color.G, color.B)); Refresh(_colors.Count - 1);
    }
    private void Add_Click(object sender, RoutedEventArgs e) => Pick(edit: false);
    private void Edit_Click(object sender, RoutedEventArgs e) => Pick(edit: true);
    private void Pick(bool edit)
    {
        int index = ColorList.SelectedIndex;
        if (edit ? index < 0 : _colors.Count >= DrlColorCycle.MaximumColors) return;
        var current = edit ? _colors[index] : new DrlCycleColor(255, 255, 255);
        var picker = new DrlColorPickerWindow(DrlColorSelection.Fixed(current.R, current.G, current.B), allowOriginal: false) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selection is not { Mode: DrlColorMode.Fixed } selection) return;
        var color = new DrlCycleColor(selection.R, selection.G, selection.B);
        if (edit) _colors[index] = color;
        else { _colors.Add(color); index = _colors.Count - 1; }
        Refresh(index);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        int index = ColorList.SelectedIndex;
        if (index < 0 || _colors.Count <= 2) return;
        _colors.RemoveAt(index); Refresh(index);
    }
    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int step)
    {
        int index = ColorList.SelectedIndex, next = index + step;
        if (index < 0 || next < 0 || next >= _colors.Count) return;
        (_colors[index], _colors[next]) = (_colors[next], _colors[index]); Refresh(next);
    }
    private void Accept_Click(object sender, RoutedEventArgs e) { Selection = new DrlColorCycle(_colors); DialogResult = true; }
}
