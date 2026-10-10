using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForzavistaFreeRoam;

public partial class DrlColorPickerWindow : Window
{
    private const int ChartPixels = 256;
    private bool _ready;
    private bool _syncing;
    private bool _chartDragging;
    private bool _hexValid = true;
    private bool _rgbValid = true;
    private double _hue;
    private double _saturation;
    private double _value;
    private byte _red;
    private byte _green;
    private byte _blue;

    internal DrlColorPickerWindow(DrlColorSelection current, bool allowOriginal = true)
    {
        InitializeComponent();
        if (!allowOriginal)
        { OriginalButton.Visibility = Visibility.Collapsed; PickerHint.Text = "Choose a color for this cycle stop."; }
        var rgb = current.Mode == DrlColorMode.Original ? (R: (byte)255, G: (byte)255, B: (byte)255) : current.GetRgb();
        SetRgb(rgb.R, rgb.G, rgb.B);
        _ready = true;
        RefreshControls();
        RenderChart();
    }

    /// <summary>Null means the dialog was canceled. No game writes occur in this window.</summary>
    internal DrlColorSelection? Selection { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionMarker();
        ChartSurface.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        _hue = HueSlider.Value;
        SetRgbFromChart();
        RefreshControls();
        RenderChart();
    }

    private void Chart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ChartSurface.Focus();
        _chartDragging = ChartSurface.CaptureMouse();
        SelectPoint(e.GetPosition(ChartSurface));
        e.Handled = true;
    }

    private void Chart_MouseMove(object sender, MouseEventArgs e)
    {
        if (_chartDragging && e.LeftButton == MouseButtonState.Pressed)
            SelectPoint(e.GetPosition(ChartSurface));
    }

    private void Chart_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_chartDragging) return;
        SelectPoint(e.GetPosition(ChartSurface));
        _chartDragging = false;
        ChartSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Chart_LostMouseCapture(object sender, MouseEventArgs e) => _chartDragging = false;

    private void SelectPoint(Point point)
    {
        if (ChartSurface.ActualWidth <= 0 || ChartSurface.ActualHeight <= 0) return;
        _saturation = Math.Clamp(point.X / ChartSurface.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(point.Y / ChartSurface.ActualHeight, 0, 1);
        SetRgbFromChart();
        RefreshControls();
    }

    private void Chart_KeyDown(object sender, KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.05 : 0.01;
        switch (e.Key)
        {
            case Key.Left: _saturation = Math.Max(0, _saturation - step); break;
            case Key.Right: _saturation = Math.Min(1, _saturation + step); break;
            case Key.Up: _value = Math.Min(1, _value + step); break;
            case Key.Down: _value = Math.Max(0, _value - step); break;
            default: return;
        }
        SetRgbFromChart();
        RefreshControls();
        e.Handled = true;
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) PositionMarker();
    }

    private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        string hex = HexInput.Text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        _hexValid = hex.Length == 6 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
        HexError.Visibility = _hexValid ? Visibility.Hidden : Visibility.Visible;
        if (_hexValid)
        {
            uint color = uint.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            SetRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
            RefreshControls(preserveHex: true);
            RenderChart();
        }
        UpdateApplyEnabled();
    }

    private void RgbInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        bool redValid = byte.TryParse(RedInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out byte red);
        bool greenValid = byte.TryParse(GreenInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out byte green);
        bool blueValid = byte.TryParse(BlueInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out byte blue);
        _rgbValid = redValid && greenValid && blueValid;
        RgbError.Visibility = _rgbValid ? Visibility.Hidden : Visibility.Visible;
        if (_rgbValid)
        {
            SetRgb(red, green, blue);
            RefreshControls(preserveRgb: true);
            RenderChart();
        }
        UpdateApplyEnabled();
    }

    private void SetRgb(byte red, byte green, byte blue)
    {
        _red = red;
        _green = green;
        _blue = blue;
        var hsv = DrlColorSelection.ToHsv(red, green, blue);
        // Keep the chosen hue when moving to white, gray or black, so the next drag remains intuitive.
        if (hsv.Saturation > 0) _hue = hsv.Hue;
        _saturation = hsv.Saturation;
        _value = hsv.Value;
    }

    private void SetRgbFromChart()
    {
        var rgb = DrlColorSelection.FromHsv(_hue, _saturation, _value);
        _red = rgb.R;
        _green = rgb.G;
        _blue = rgb.B;
    }

    private void RefreshControls(bool preserveHex = false, bool preserveRgb = false)
    {
        _syncing = true;
        try
        {
            HueSlider.Value = _hue;
            ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(_red, _green, _blue));
            if (!preserveHex)
            {
                HexInput.Text = $"#{_red:X2}{_green:X2}{_blue:X2}";
                _hexValid = true;
                HexError.Visibility = Visibility.Hidden;
            }
            if (!preserveRgb)
            {
                RedInput.Text = _red.ToString(CultureInfo.InvariantCulture);
                GreenInput.Text = _green.ToString(CultureInfo.InvariantCulture);
                BlueInput.Text = _blue.ToString(CultureInfo.InvariantCulture);
                _rgbValid = true;
                RgbError.Visibility = Visibility.Hidden;
            }
            PositionMarker();
            UpdateApplyEnabled();
        }
        finally { _syncing = false; }
    }

    private void RenderChart()
    {
        byte[] pixels = new byte[ChartPixels * ChartPixels * 4];
        for (int y = 0; y < ChartPixels; y++)
        {
            double value = 1 - y / (double)(ChartPixels - 1);
            for (int x = 0; x < ChartPixels; x++)
            {
                var color = DrlColorSelection.FromHsv(_hue, x / (double)(ChartPixels - 1), value);
                int offset = (y * ChartPixels + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }
        var image = BitmapSource.Create(ChartPixels, ChartPixels, 96, 96, PixelFormats.Bgra32, null, pixels, ChartPixels * 4);
        image.Freeze();
        ChartImage.Source = image;
    }

    private void PositionMarker()
    {
        if (ChartMarkerOuter is null || ChartSurface is null) return;
        double left = _saturation * Math.Max(0, ChartSurface.ActualWidth - 1) - 8;
        double top = (1 - _value) * Math.Max(0, ChartSurface.ActualHeight - 1) - 8;
        Canvas.SetLeft(ChartMarkerOuter, left);
        Canvas.SetTop(ChartMarkerOuter, top);
        Canvas.SetLeft(ChartMarkerInner, left);
        Canvas.SetTop(ChartMarkerInner, top);
    }

    private void UpdateApplyEnabled() => ApplyButton.IsEnabled = _hexValid && _rgbValid;

    private void White_Click(object sender, RoutedEventArgs e)
    {
        SetRgb(255, 255, 255);
        RefreshControls();
        RenderChart();
    }

    private void Original_Click(object sender, RoutedEventArgs e)
    {
        Selection = DrlColorSelection.Stock;
        DialogResult = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_hexValid || !_rgbValid) return;
        Selection = DrlColorSelection.Fixed(_red, _green, _blue);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Selection = null;
        DialogResult = false;
    }
}
