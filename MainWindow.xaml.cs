using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ForzavistaFreeRoam;

public partial class MainWindow : Window
{
    private sealed record ActionPair(string Label, string OpenAction, string CloseAction);

    private static readonly ActionPair[] Parts =
    [
        new("Left front door", "opendoorLF", "closedoorLF"),
        new("Right front door", "opendoorRF", "closedoorRF"),
        new("Left rear door", "opendoorLR", "closedoorLR"),
        new("Right rear door", "opendoorRR", "closedoorRR"),
        new("Hood", "openhood", "closehood"),
        new("Trunk", "opentrunk", "closetrunk"),
        new("Roof", "openroof", "closeroof"),
        new("Storage", "openstorage", "closestorage"),
        new("Active aero", "openaero", "closeaero"),
        new("Vents", "openvents", "closevents"),
    ];
    private static readonly (string Panel, string Action)[] ExplodeActions =
    [
        ("doorLF", "opendoorLF"), ("doorRF", "opendoorRF"), ("doorLR", "opendoorLR"),
        ("doorRR", "opendoorRR"), ("hood", "openhood"), ("trunk", "opentrunk")
    ];
    private static readonly (string Panel, string Action)[] ImplodeActions =
    [
        ("doorLF", "closedoorLF"), ("doorRF", "closedoorRF"), ("doorLR", "closedoorLR"),
        ("doorRR", "closedoorRR"), ("hood", "closehood"), ("trunk", "closetrunk")
    ];

    private readonly Dictionary<string, Button> _buttonsByAction = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baseLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _actionButtons = [];
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly HashSet<string> _openPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int? _sessionProcessId;
    private ulong? _sessionVehicle;
    private bool _ownsPresentationFlag;
    private bool _maxDetailOn;
    private bool _polling;
    private string? _hashedPath;
    private DateTime _hashedWriteTimeUtc;
    private string? _hashedDigest;

    // ----- hotkeys -----
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private readonly Dictionary<string, (ModifierKeys Mods, Key Key)> _hotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _hotkeyIdToAction = new();
    private bool _bindMode;
    private string? _capturingAction;
    private IntPtr _hwnd;
    private HwndSource? _source;

    private static string HotkeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ForzavistaFreeRoam", "hotkeys.json");

    private Brush Good => (Brush)FindResource("Good");
    private Brush Warn => (Brush)FindResource("Warn");
    private Brush Dim => (Brush)FindResource("Dim");
    private Brush Ink => (Brush)FindResource("Ink");

    public MainWindow()
    {
        InitializeComponent();
        BuildPanelRows();
        WireActionButton(ExplodeButton, "explode", "EXPLODE");
        WireActionButton(ImplodeButton, "implode", "IMPLODE");
        WireActionButton(ResetButton, "resetstate", "RESET STATE");
        WireActionButton(MaxDetailButton, "maxdetail", "MAX DETAIL: OFF");
        _actionButtons.Add(ExplodeButton);
        _actionButtons.Add(ImplodeButton);
        _actionButtons.Add(ResetButton);
        _actionButtons.Add(MaxDetailButton);
        LoadHotkeys();
        RefreshAllLabels();
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        Loaded += async (_, _) => { await PollStatusAsync(); _statusTimer.Start(); };
        Closing += (_, _) => RestoreOnExit();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        RegisterAllHotkeys();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterAllHotkeys();
        _source?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    // ================= custom title bar =================
    // Dragging, double-click-to-maximize and the right-click system menu on the
    // title bar are handled natively by WindowChrome (CaptionHeight=40). The
    // caption buttons opt back in to hit-testing via IsHitTestVisibleInChrome.

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // Segoe MDL2 "Restore" glyph, and keep content clear of the invisible resize border.
            MaximizeButton.Content = "";
            MaximizeButton.ToolTip = "Restore";
            RootGrid.Margin = new Thickness(7);
            RootBorder.BorderThickness = new Thickness(0);
        }
        else
        {
            MaximizeButton.Content = "";
            MaximizeButton.ToolTip = "Maximize";
            RootGrid.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
        }
    }

    // ================= panel / action buttons =================

    private void BuildPanelRows()
    {
        PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PanelGrid.Children.Add(HeaderCell("PART", 0));
        PanelGrid.Children.Add(HeaderCell("OPEN", 1));
        PanelGrid.Children.Add(HeaderCell("CLOSE", 2));

        var divider = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < Parts.Length; i++)
        {
            var part = Parts[i];
            var row = i + 1;
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // thin divider along the bottom of every row except the last
            if (i < Parts.Length - 1)
            {
                var line = new Border
                {
                    Height = 1, Background = divider, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4, 0, 6, 0)
                };
                Grid.SetRow(line, row); Grid.SetColumn(line, 0); Grid.SetColumnSpan(line, 3);
                PanelGrid.Children.Add(line);
            }

            var label = new TextBlock
            {
                Text = part.Label, Foreground = Ink, FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 5, 14, 5)
            };
            Grid.SetRow(label, row); Grid.SetColumn(label, 0);
            PanelGrid.Children.Add(label);

            var open = MakeActionButton("OPEN", "PinkButton", part.OpenAction);
            Grid.SetRow(open, row); Grid.SetColumn(open, 1);
            PanelGrid.Children.Add(open);

            var close = MakeActionButton("CLOSE", "GhostButton", part.CloseAction);
            Grid.SetRow(close, row); Grid.SetColumn(close, 2);
            PanelGrid.Children.Add(close);
        }
    }

    private TextBlock HeaderCell(string text, int col)
    {
        var t = new TextBlock
        {
            Text = text, Foreground = Dim, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(col == 0 ? 4 : 6, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(t, 0); Grid.SetColumn(t, col);
        return t;
    }

    private Button MakeActionButton(string text, string styleKey, string action)
    {
        var b = new Button
        {
            Content = text, Style = (Style)FindResource(styleKey), Height = 32, MinWidth = 100,
            Margin = new Thickness(6, 3, 0, 3), IsEnabled = false, Padding = new Thickness(10, 0, 10, 0)
        };
        WireActionButton(b, action, text);
        _actionButtons.Add(b);
        return b;
    }

    private void WireActionButton(Button b, string action, string baseLabel)
    {
        b.Tag = action;
        b.Click += ActionButton_Click;
        b.MouseRightButtonUp += ActionButton_RightClick;
        _buttonsByAction[action] = b;
        _baseLabels[action] = baseLabel;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        var action = (string)((Button)sender).Tag;
        if (_bindMode) { BeginCapture(action); return; }
        await InvokeActionAsync(action);
    }

    private void ActionButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (!_bindMode) return;
        ClearBinding((string)((Button)sender).Tag);
    }

    private Task InvokeActionAsync(string action) => action.ToLowerInvariant() switch
    {
        "explode" => ExplodeAsync(),
        "implode" => ImplodeAsync(),
        "resetstate" => ResetAsync(),
        "maxdetail" => MaxDetailAsync(),
        _ => SendActionAsync(action)
    };

    private async Task SendActionAsync(string action)
    {
        await _actionGate.WaitAsync();
        try
        {
            string message;
            if (NativeCarControl.SupportedFreeRoamPanelActions.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                message = await Task.Run(() => NativeCarControl.TriggerFreeRoamPanel(action));
                var opening = action.StartsWith("open", StringComparison.OrdinalIgnoreCase);
                var panel = opening ? action[4..] : action[5..];
                if (opening) { _openPanels.Add(panel); _ownsPresentationFlag = true; }
                else
                {
                    _openPanels.Remove(panel);
                    if (_openPanels.Count == 0)
                    {
                        await Task.Delay(3000);
                        var restored = await Task.Run(NativeCarControl.RestoreFreeRoamPresentationFlag);
                        _ownsPresentationFlag = false;
                        message = $"{message}; {restored}";
                    }
                }
            }
            else
            {
                var isRoof = action.Equals("openroof", StringComparison.OrdinalIgnoreCase) ||
                             action.Equals("closeroof", StringComparison.OrdinalIgnoreCase);
                message = isRoof
                    ? await Task.Run(NativeCarControl.ToggleRoof)
                    : await Task.Run(() => NativeCarControl.TriggerPanel(action));
            }
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); await PollStatusAsync(); }
    }

    private async Task ExplodeAsync()
    {
        await _actionGate.WaitAsync();
        try
        {
            var opened = new List<string>();
            foreach (var (panel, openAction) in ExplodeActions)
            {
                if (_openPanels.Contains(panel)) continue;
                await Task.Run(() => NativeCarControl.TriggerFreeRoamPanel(openAction));
                _openPanels.Add(panel);
                _ownsPresentationFlag = true;
                opened.Add(panel);
                await Task.Delay(100);
            }
            SetControlsStatus(opened.Count == 0 ? "all tracked panels already open" : $"opened {string.Join(", ", opened)}", Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); await PollStatusAsync(); }
    }

    private async Task ImplodeAsync()
    {
        await _actionGate.WaitAsync();
        try
        {
            if (_openPanels.Count == 0) { SetControlsStatus("no open panels tracked", Good); return; }
            var closed = new List<string>();
            foreach (var (panel, closeAction) in ImplodeActions)
            {
                if (!_openPanels.Contains(panel)) continue;
                await Task.Run(() => NativeCarControl.TriggerFreeRoamPanel(closeAction));
                _openPanels.Remove(panel);
                closed.Add(panel);
                await Task.Delay(100);
            }
            await Task.Delay(3000);
            var restored = await Task.Run(NativeCarControl.RestoreFreeRoamPresentationFlag);
            _ownsPresentationFlag = false;
            SetControlsStatus($"closed {string.Join(", ", closed)}; {restored}", Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); await PollStatusAsync(); }
    }

    private async Task ResetAsync()
    {
        await _actionGate.WaitAsync();
        try
        {
            var message = await Task.Run(NativeCarControl.RestoreFreeRoamPresentationFlag);
            _openPanels.Clear();
            _ownsPresentationFlag = false;
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); await PollStatusAsync(); }
    }

    private async Task MaxDetailAsync()
    {
        MaxDetailButton.IsEnabled = false;
        await _actionGate.WaitAsync();
        try
        {
            var turnOn = !_maxDetailOn;
            var message = await Task.Run(() => NativeCarControl.SetMaxDetail(turnOn));
            _maxDetailOn = turnOn;
            MaxDetailButton.Style = (Style)FindResource(_maxDetailOn ? "PinkFilled" : "GhostButton");
            RefreshLabel("maxdetail");
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); await PollStatusAsync(); }
    }

    // ================= status polling =================

    private async Task PollStatusAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            await UpdateGameStatusAsync();
            var status = await Task.Run(NativeCarControl.GetStatus);
            if (_sessionProcessId != status.ProcessId || _sessionVehicle != status.Vehicle)
            {
                _sessionProcessId = status.ProcessId;
                _sessionVehicle = status.Vehicle;
                _openPanels.Clear();
                _ownsPresentationFlag = false;
            }
            if (_capturingAction is null) SetControlsStatus(status.Message, status.Ready ? Good : Warn);

            if (_bindMode) { SetActionsEnabled(true); return; }
            SetActionsEnabled(false);
            if (status.Ready)
            {
                foreach (var action in NativeCarControl.SupportedFreeRoamPanelActions)
                    if (_buttonsByAction.TryGetValue(action, out var b)) b.IsEnabled = true;
                if (_buttonsByAction.TryGetValue("openroof", out var or)) or.IsEnabled = true;
                if (_buttonsByAction.TryGetValue("closeroof", out var cr)) cr.IsEnabled = true;
                ExplodeButton.IsEnabled = true;
                ImplodeButton.IsEnabled = true;
                ResetButton.IsEnabled = true;
                MaxDetailButton.IsEnabled = true;
            }
        }
        finally { _polling = false; }
    }

    private async Task UpdateGameStatusAsync()
    {
        using var process = Process.GetProcessesByName("forzahorizon6").OrderByDescending(p => p.StartTime).FirstOrDefault();
        if (process is null) { GameStatusText.Text = "game not running"; GameStatusText.Foreground = Dim; return; }
        string? path;
        try { path = process.MainModule?.FileName; } catch { path = null; }
        if (string.IsNullOrWhiteSpace(path)) { GameStatusText.Text = $"running (PID {process.Id})"; GameStatusText.Foreground = Warn; return; }

        var writeTimeUtc = File.GetLastWriteTimeUtc(path);
        if (!string.Equals(_hashedPath, path, StringComparison.OrdinalIgnoreCase) || _hashedWriteTimeUtc != writeTimeUtc)
        {
            _hashedPath = path;
            _hashedWriteTimeUtc = writeTimeUtc;
            _hashedDigest = await Task.Run(() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        var supported = string.Equals(_hashedDigest, NativeCarControl.SupportedSha256, StringComparison.OrdinalIgnoreCase);
        GameStatusText.Text = supported ? $"supported build — PID {process.Id}" : $"unsupported build — PID {process.Id}";
        GameStatusText.Foreground = supported ? Good : Warn;
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var b in _actionButtons) b.IsEnabled = enabled;
    }

    private void SetControlsStatus(string text, Brush brush)
    {
        ControlsStatusText.Text = text;
        ControlsStatusText.Foreground = brush;
    }

    private void RestoreOnExit()
    {
        if (!_ownsPresentationFlag) return;
        try { NativeCarControl.RestoreFreeRoamPresentationFlag(); } catch { }
        _openPanels.Clear();
        _ownsPresentationFlag = false;
    }

    // ================= hotkeys =================

    private void BindMode_Click(object sender, RoutedEventArgs e)
    {
        _bindMode = !_bindMode;
        if (!_bindMode && _capturingAction is not null) CancelCapture();
        BindModeButton.Content = _bindMode ? "BIND HOTKEYS: ON" : "BIND HOTKEYS: OFF";
        BindModeButton.Style = (Style)FindResource(_bindMode ? "PinkFilled" : "GhostButton");
        if (_bindMode)
        {
            SetActionsEnabled(true);
            SetControlsStatus("bind mode on — click an action, then press a key (right-click clears)", Warn);
        }
        else
        {
            _ = PollStatusAsync();
        }
    }

    private void ClearHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _hotkeys.Clear();
        SaveHotkeys();
        RegisterAllHotkeys();
        RefreshAllLabels();
        SetControlsStatus("all hotkeys cleared", Good);
    }

    private void BeginCapture(string action)
    {
        _capturingAction = action;
        UnregisterAllHotkeys();   // free the keys so the pressed key reaches this window
        Activate();
        SetControlsStatus($"press a key for '{GetBaseLabel(action)}' … (Esc to cancel)", Warn);
    }

    private void CancelCapture()
    {
        _capturingAction = null;
        RegisterAllHotkeys();
        SetControlsStatus("hotkey binding cancelled", Dim);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingAction is null) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelCapture(); return; }
        if (IsModifierKey(key)) return; // wait for a non-modifier key
        var action = _capturingAction;
        var mods = Keyboard.Modifiers;
        _capturingAction = null;
        _hotkeys[action] = (mods, key);
        SaveHotkeys();
        RegisterAllHotkeys();
        RefreshLabel(action);
        SetControlsStatus($"bound '{GetBaseLabel(action)}' → {Display(mods, key)}", Good);
    }

    private static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System;

    private void ClearBinding(string action)
    {
        if (!_hotkeys.Remove(action)) return;
        SaveHotkeys();
        RegisterAllHotkeys();
        RefreshLabel(action);
        SetControlsStatus($"cleared hotkey for '{GetBaseLabel(action)}'", Good);
    }

    private void RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();
        if (_hwnd == IntPtr.Zero) return;
        var id = 1;
        foreach (var (action, hk) in _hotkeys)
        {
            uint fs = MOD_NOREPEAT
                | (hk.Mods.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
                | (hk.Mods.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
                | (hk.Mods.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0);
            var vk = (uint)KeyInterop.VirtualKeyFromKey(hk.Key);
            if (vk != 0 && RegisterHotKey(_hwnd, id, fs, vk)) _hotkeyIdToAction[id] = action;
            id++;
        }
    }

    private void UnregisterAllHotkeys()
    {
        if (_hwnd != IntPtr.Zero)
            foreach (var id in _hotkeyIdToAction.Keys.ToList())
                UnregisterHotKey(_hwnd, id);
        _hotkeyIdToAction.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _capturingAction is null &&
            _hotkeyIdToAction.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            _ = Dispatcher.InvokeAsync(async () => await InvokeActionAsync(action));
        }
        return IntPtr.Zero;
    }

    // ================= labels & persistence =================

    private string GetBaseLabel(string action) =>
        action.Equals("maxdetail", StringComparison.OrdinalIgnoreCase)
            ? (_maxDetailOn ? "MAX DETAIL: ON" : "MAX DETAIL: OFF")
            : _baseLabels.TryGetValue(action, out var label) ? label : action;

    private void RefreshLabel(string action)
    {
        if (!_buttonsByAction.TryGetValue(action, out var b)) return;
        var baseLabel = GetBaseLabel(action);
        b.Content = _hotkeys.TryGetValue(action, out var hk)
            ? $"{baseLabel}   [{Display(hk.Mods, hk.Key)}]"
            : baseLabel;
    }

    private void RefreshAllLabels()
    {
        foreach (var action in _buttonsByAction.Keys.ToList()) RefreshLabel(action);
    }

    private static string Display(ModifierKeys m, Key k)
    {
        var s = "";
        if (m.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (m.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (m.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        return s + KeyName(k);
    }

    private static string KeyName(Key k)
    {
        var s = k.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString();
        if (s.StartsWith("NumPad", StringComparison.Ordinal)) return "Num" + s[6..];
        return s;
    }

    private static string Serialize(ModifierKeys m, Key k)
    {
        var s = "";
        if (m.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (m.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (m.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        return s + k; // raw Key name so it round-trips through Enum.Parse
    }

    private static bool TryParse(string gesture, out ModifierKeys m, out Key k)
    {
        m = ModifierKeys.None; k = Key.None;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": m |= ModifierKeys.Control; break;
                case "alt": m |= ModifierKeys.Alt; break;
                case "shift": m |= ModifierKeys.Shift; break;
                default: return false;
            }
        }
        return Enum.TryParse(parts[^1], out k) && k != Key.None;
    }

    private void LoadHotkeys()
    {
        try
        {
            if (!File.Exists(HotkeyPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(HotkeyPath));
            if (map is null) return;
            foreach (var (action, gesture) in map)
                if (TryParse(gesture, out var m, out var k)) _hotkeys[action] = (m, k);
        }
        catch { }
    }

    private void SaveHotkeys()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HotkeyPath)!);
            var map = _hotkeys.ToDictionary(kv => kv.Key, kv => Serialize(kv.Value.Mods, kv.Value.Key));
            File.WriteAllText(HotkeyPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
