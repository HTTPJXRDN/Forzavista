using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        new("Pop-up headlights", "openheadlights", "closeheadlights"),
        new("Vents", "openvents", "closevents"),
        new("Left front window", "openwindowLF", "closewindowLF"),
        new("Right front window", "openwindowRF", "closewindowRF"),
        new("Left rear window", "openwindowLR", "closewindowLR"),
        new("Right rear window", "openwindowRR", "closewindowRR"),
    ];
    private static readonly string[] ExplodePanels =
    [
        "doorLF", "doorRF", "doorLR", "doorRR", "hood", "trunk", "aero", "headlights"
    ];

    private readonly Dictionary<string, Button> _buttonsByAction = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baseLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _actionButtons = [];
    private readonly NonQueuedWorkGate _actionGate = new();
    private readonly NonQueuedWorkGate _bindingGate = new();
    private int _bindingGeneration;
    private long _actionGeneration;
    private CancellationTokenSource? _signalPreparation;
    private string? _signalSearchNotice;
    private readonly HashSet<string> _openPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int? _sessionProcessId;
    private ulong? _sessionVehicle;
    private ulong? _sessionComponent;
    private string? _sessionCarToken;
    private bool _ownsPresentationFlag;
    private int _presentationRestoreGeneration;
    private bool _maxDetailOn;
    private NativeLightStatus _lightStatus = new(false, false, false, "Lighting not ready");
    private NativeSignalStatus _signalStatus = new(false, false, SignalMode.Off, "Signals not ready");
    private bool _closing;
    private bool _exitRestored;
    private bool _polling;
    private string? _hashedPath;
    private DateTime _hashedWriteTimeUtc;
    private string? _hashedDigest;

    // ----- hotkeys -----
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private readonly Dictionary<string, (ModifierKeys Mods, Key Key)> _hotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string[]> _hotkeyIdToActions = new();
    private bool _bindMode;
    private string? _capturingAction;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private WinEventDelegate? _foregroundChangedDelegate;
    private IntPtr _foregroundHook;

    // ----- Xbox controller bindings -----
    private readonly DispatcherTimer _controllerTimer = new(DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<string, uint> _controllerBindings = new(StringComparer.OrdinalIgnoreCase);
    private string? _capturingControllerAction;
    private bool _controllerCaptureArmed;
    private uint _controllerCaptureMask;
    private DateTime _controllerCaptureStartedUtc;
    private uint _previousControllerMask;
    private int? _activeControllerIndex;

    private static string BindingDirectory =>
        Environment.GetEnvironmentVariable("FORZAVISTA_BINDINGS_DIR") is { Length: > 0 } directory &&
        Path.IsPathFullyQualified(directory)
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ForzavistaFreeRoam");

    private static string HotkeyPath => Path.Combine(BindingDirectory, "hotkeys.json");

    private static string ControllerBindingPath =>
        Path.Combine(BindingDirectory, "controller-bindings.json");

    private Brush Good => (Brush)FindResource("Good");
    private Brush Warn => (Brush)FindResource("Warn");
    private Brush Dim => (Brush)FindResource("Dim");
    private Brush Ink => (Brush)FindResource("Ink");

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + (typeof(MainWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown");
        BuildPanelRows();
        WireActionButton(AllPanelsButton, "toggleall", "EXPLODE");
        WireActionButton(ResetButton, "resetstate", "RESET STATE");
        WireActionButton(MaxDetailButton, "maxdetail", "MAX DETAIL: OFF");
        WireActionButton(DrlButton, "lightsdrl", "FRONT DRL: —");
        WireActionButton(RearDrlButton, "lightsrear", "REAR DRL: —");
        WireActionButton(HeadlightButton, "lightsheadlights", "HEADLIGHTS: —");
        WireActionButton(AllLightsButton, "lightsall", "ALL LIGHTS: —");
        WireActionButton(LeftIndicatorButton, "signalleft", "LEFT INDICATOR: —");
        WireActionButton(RightIndicatorButton, "signalright", "RIGHT INDICATOR: —");
        WireActionButton(HazardsButton, "signalhazards", "HAZARDS: —");
        SignalsPanel.Visibility = LightingFeaturePolicy.ExperimentalSignalsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        _actionButtons.Add(AllPanelsButton);
        _actionButtons.Add(ResetButton);
        _actionButtons.Add(MaxDetailButton);
        _actionButtons.AddRange([DrlButton, RearDrlButton, HeadlightButton, AllLightsButton]);
        _actionButtons.AddRange([LeftIndicatorButton, RightIndicatorButton, HazardsButton]);
        LoadHotkeys();
        LoadControllerBindings();
        InitializeDrlColors();
        InitializeLightFade();
        RefreshAllLabels();
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _controllerTimer.Tick += ControllerTimer_Tick;
        Loaded += async (_, _) =>
        {
            await PollStatusAsync();
            _statusTimer.Start();
            _controllerTimer.Start();
        };
        Closing += WindowClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _foregroundChangedDelegate = ForegroundWindowChanged;
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            IntPtr.Zero, _foregroundChangedDelegate, 0, 0, WinEventOutOfContext);
        RegisterAllHotkeys();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterAllHotkeys();
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _foregroundChangedDelegate = null;
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
        var left = Parts.Take(8).ToArray(); // doors, hood, trunk, roof, storage
        var right = Parts.Skip(11).Concat(Parts.Skip(8).Take(3)).ToArray();
        PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var leftHeader = HeaderCell("PANELS", 0);
        Grid.SetColumnSpan(leftHeader, 2);
        PanelGrid.Children.Add(leftHeader);
        var rightHeader = HeaderCell("WINDOWS & DYNAMIC", 3);
        Grid.SetColumnSpan(rightHeader, 2);
        PanelGrid.Children.Add(rightHeader);

        var divider = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var row = i + 1;
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (i < left.Length) AddPanelCell(left[i], row, 0, 1, divider, i < left.Length - 1);
            if (i < right.Length) AddPanelCell(right[i], row, 3, 4, divider, i < right.Length - 1);
        }
    }

    private void AddPanelCell(ActionPair part, int row, int labelColumn, int buttonColumn,
        Brush divider, bool addDivider)
    {
        if (addDivider)
        {
            var line = new Border
            {
                Height = 1, Background = divider, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 6, 0)
            };
            Grid.SetRow(line, row); Grid.SetColumn(line, labelColumn);
            Grid.SetColumnSpan(line, 2);
            PanelGrid.Children.Add(line);
        }

        var label = new TextBlock
        {
            Text = part.Label, Foreground = Ink, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 5, 8, 5),
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = part.Label
        };
        Grid.SetRow(label, row); Grid.SetColumn(label, labelColumn);
        PanelGrid.Children.Add(label);

        var toggle = MakeActionButton("OPEN", "PinkButton", "toggle" + part.OpenAction[4..]);
        Grid.SetRow(toggle, row); Grid.SetColumn(toggle, buttonColumn);
        PanelGrid.Children.Add(toggle);
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
            Content = text, Style = (Style)FindResource(styleKey), Height = 32, MinWidth = 150,
            Margin = new Thickness(6, 3, 0, 3), IsEnabled = false, Padding = new Thickness(10, 0, 10, 0)
        };
        WireActionButton(b, action, text);
        _actionButtons.Add(b);
        return b;
    }

    private void WireActionButton(Button b, string action, string baseLabel)
    {
        b.ClickMode = ClickMode.Press;
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
        var action = (string)((Button)sender).Tag;
        if (_bindMode) ClearBinding(action);
    }

    private Task InvokeActionAsync(string action)
    {
        if (_closing) return Task.CompletedTask;
        if (_bindingGate.IsBusy && !action.Equals("resetstate", StringComparison.OrdinalIgnoreCase))
        {
            RejectBusyAction(action, "A shared binding is still executing");
            return Task.CompletedTask;
        }
        return InvokeCoreActionAsync(action);
    }

    private async Task InvokeCoreActionAsync(string action)
    {
        if (_closing) return;
        var clock = Stopwatch.StartNew();
        SessionLog.Write("action_requested", action: action, gamePid: _sessionProcessId,
            carToken: _sessionCarToken, component: _sessionComponent);
        var work = action.ToLowerInvariant() switch
        {
            "toggleall" or "explode" or "implode" => ToggleAllAsync(),
            "resetstate" => ResetAsync(),
            "maxdetail" => MaxDetailAsync(),
            "lightsdrl" or "lightsrear" or "lightsheadlights" or "lightsall" => LightsAsync(action.ToLowerInvariant()),
            "signalleft" or "signalright" or "signalhazards" => SignalsAsync(action.ToLowerInvariant()),
            _ when action.StartsWith("lightsgroup:", StringComparison.OrdinalIgnoreCase)
                => LightsAsync(action.ToLowerInvariant()),
            "openheadlights" => PopupHeadlightsAsync(true),
            "closeheadlights" => PopupHeadlightsAsync(false),
            _ when action.StartsWith("toggle", StringComparison.OrdinalIgnoreCase)
                => TogglePartAsync(action),
            _ when NativeCarControl.SupportedWindowActions.Contains(action, StringComparer.OrdinalIgnoreCase)
                => WindowAsync(action),
            _ => SendActionAsync(action)
        };
        try { await work; }
        finally
        {
            SessionLog.Write("action_finished", $"elapsedMs={clock.ElapsedMilliseconds}", action,
                _sessionProcessId, _sessionCarToken, _sessionComponent);
        }
    }

    private async Task InvokeBoundActionsAsync(IReadOnlyList<string> actions, string source)
    {
        if (_closing) return;
        if (!_bindingGate.TryEnter())
        {
            RejectBusyAction(string.Join(",", actions), "A previous binding is still executing");
            return;
        }
        try
        {
            var generation = _bindingGeneration;
            var parts = Parts.Select(part => (part.OpenAction, part.CloseAction)).ToArray();
            string[]? availableWindows = null;
            if (actions.Any(action => action.StartsWith("togglewindow", StringComparison.OrdinalIgnoreCase) ||
                                      action.StartsWith("openwindow", StringComparison.OrdinalIgnoreCase) ||
                                      action.StartsWith("closewindow", StringComparison.OrdinalIgnoreCase)))
            {
                if (!await TryEnterActionAsync("binding-window-preflight")) return;
                try { availableWindows = await Task.Run(NativeCarControl.GetAvailableWindowPositions); }
                finally { _actionGate.Release(); }
            }
            var selected = BindingActionPlanner.Plan(actions,
                parts, _openPanels, _openWindows, availableWindows);

            SessionLog.Write("binding_triggered", source, action: string.Join(",", selected),
                gamePid: _sessionProcessId, carToken: _sessionCarToken, component: _sessionComponent);
            if (selected.Length == 0)
            {
                SetControlsStatus("No bound controls are available on this car.", Warn);
                return;
            }
            foreach (var action in selected)
            {
                if (_closing || generation != _bindingGeneration) break;
                await InvokeCoreActionAsync(action);
            }
        }
        catch (Exception ex)
        {
            SessionLog.Write("binding_error", ex.ToString(), gamePid: _sessionProcessId,
                carToken: _sessionCarToken, component: _sessionComponent);
            SetControlsStatus($"Binding failed: {ex.Message}", Warn);
        }
        finally { _bindingGate.Release(); }
    }

    private bool IsPartOpen(ActionPair part) =>
        part.OpenAction.StartsWith("openwindow", StringComparison.OrdinalIgnoreCase)
            ? _openWindows.Contains(part.OpenAction["openwindow".Length..])
            : _openPanels.Contains(part.OpenAction[4..]);

    private bool AreAllExplodePanelsOpen() => ExplodePanels.All(_openPanels.Contains);

    private Task ToggleAllAsync() => AreAllExplodePanelsOpen() ? ImplodeAsync() : ExplodeAsync();

    private Task TogglePartAsync(string action)
    {
        var part = Parts.FirstOrDefault(part =>
            action.Equals("toggle" + part.OpenAction[4..], StringComparison.OrdinalIgnoreCase));
        if (part is null) throw new InvalidOperationException("Unknown toggle action.");
        return InvokeCoreActionAsync(IsPartOpen(part) ? part.CloseAction : part.OpenAction);
    }

    private async Task WindowAsync(string action)
    {
        if (!await TryEnterActionAsync(action)) return;
        try
        {
            var message = await Task.Run(() => NativeCarControl.SetWindowOpen(action));
            var opening = action.StartsWith("openwindow", StringComparison.OrdinalIgnoreCase);
            var window = action[(opening ? "openwindow".Length : "closewindow".Length)..];
            if (opening) _openWindows.Add(window);
            else _openWindows.Remove(window);
            RefreshLabel("togglewindow" + window);
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); }
    }

    private async Task PopupHeadlightsAsync(bool open)
    {
        if (!await TryEnterActionAsync("popupheadlights")) return;
        try
        {
            CancelSignalPreparation();
            CancelScheduledPresentationRestore();
            var message = await Task.Run(() => NativeCarControl.TriggerPopupHeadlights(open));
            if (open)
            {
                _openPanels.Add("headlights");
                _ownsPresentationFlag = true;
            }
            else
            {
                _openPanels.Remove("headlights");
                if (_openPanels.Count == 0)
                {
                    SchedulePresentationRestore();
                    message = $"{message}; presentation reset scheduled";
                }
            }
            RefreshLabel("toggleheadlights");
            RefreshLabel("toggleall");
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); }
    }

    private async Task SendActionAsync(string action)
    {
        if (!await TryEnterActionAsync(action)) return;
        try
        {
            CancelSignalPreparation();
            string message;
            if (NativeCarControl.SupportedFreeRoamPanelActions.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                CancelScheduledPresentationRestore();
                message = await Task.Run(() => NativeCarControl.TriggerFreeRoamPanel(action));
                var opening = action.StartsWith("open", StringComparison.OrdinalIgnoreCase);
                var panel = opening ? action[4..] : action[5..];
                if (opening) { _openPanels.Add(panel); _ownsPresentationFlag = true; }
                else
                {
                    _openPanels.Remove(panel);
                    if (_openPanels.Count == 0)
                    {
                        SchedulePresentationRestore();
                        message = $"{message}; presentation reset scheduled";
                    }
                }
                RefreshLabel("toggle" + panel);
                RefreshLabel("toggleall");
            }
            else
            {
                var isRoof = action.Equals("openroof", StringComparison.OrdinalIgnoreCase) ||
                             action.Equals("closeroof", StringComparison.OrdinalIgnoreCase);
                message = isRoof
                    ? await Task.Run(NativeCarControl.ToggleRoof)
                    : await Task.Run(() => NativeCarControl.TriggerPanel(action));
                if (action.StartsWith("open", StringComparison.OrdinalIgnoreCase))
                    _openPanels.Add(action[4..]);
                else if (action.StartsWith("close", StringComparison.OrdinalIgnoreCase))
                    _openPanels.Remove(action[5..]);
                RefreshLabel("toggle" + (action.StartsWith("open", StringComparison.OrdinalIgnoreCase)
                    ? action[4..] : action[5..]));
                RefreshLabel("toggleall");
            }
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); }
    }

    private async Task ExplodeAsync()
    {
        if (!await TryEnterActionAsync("explode")) return;
        try
        {
            CancelSignalPreparation();
            CancelScheduledPresentationRestore();
            var message = await Task.Run(() => NativeCarControl.TriggerFreeRoamExplode(open: true));
            foreach (var panel in ExplodePanels) _openPanels.Add(panel);
            _ownsPresentationFlag = true;
            RefreshAllLabels();
            SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); }
    }

    private async Task ImplodeAsync()
    {
        if (!await TryEnterActionAsync("implode")) return;
        try
        {
            CancelSignalPreparation();
            CancelScheduledPresentationRestore();
            if (_openPanels.Count == 0) { SetControlsStatus("no open panels tracked", Good); return; }
            var message = await Task.Run(() => NativeCarControl.TriggerFreeRoamExplode(open: false));
            foreach (var panel in ExplodePanels) _openPanels.Remove(panel);
            RefreshAllLabels();
            if (_openPanels.Count == 0)
            {
                SchedulePresentationRestore();
                SetControlsStatus($"{message}; presentation reset scheduled", Good);
            }
            else SetControlsStatus(message, Good);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); }
    }

    private async Task ResetAsync()
    {
        if (!await TryEnterActionAsync("resetstate")) return;
        try
        {
            _bindingGeneration++;
            CancelSignalPreparation();
            _signalSearchNotice = null;
            CancelScheduledPresentationRestore();
            CancelDrlColorPreparation();
            var colorResult = await Task.Run(NativeCarControl.RestoreTrackedDrlColor);
            var signalResult = await Task.Run(NativeCarControl.RestoreTrackedSignals);
            var message = await Task.Run(NativeCarControl.RestoreFreeRoamPresentationFlag);
            var lightMessage = await Task.Run(NativeCarControl.RestoreTrackedLights);
            var windowMessage = await Task.Run(NativeCarControl.RestoreTrackedWindows);
            _openPanels.Clear();
            _openWindows.Clear();
            _ownsPresentationFlag = false;
            RefreshAllLabels();
            _drlColorStatus = NativeCarControl.GetDrlColorStatus();
            RefreshDrlColorControls();
            SetControlsStatus($"{message} {lightMessage} {windowMessage} {signalResult.Message} {colorResult.Message}",
                signalResult.Success && colorResult.Success ? Good : Warn);
        }
        catch (Exception ex) { SetControlsStatus(ex.Message, Warn); }
        finally { _actionGate.Release(); await PollStatusAsync(); }
    }

    private void CancelScheduledPresentationRestore() => _presentationRestoreGeneration++;

    private void SchedulePresentationRestore()
    {
        var generation = ++_presentationRestoreGeneration;
        _ = RestorePresentationAfterDelayAsync(generation);
    }

    private async Task RestorePresentationAfterDelayAsync(int generation)
    {
        await Task.Delay(3000);
        if (_closing || generation != _presentationRestoreGeneration) return;
        // Background cleanup may retry later, but must not queue an old write.
        if (!_actionGate.TryEnter())
        {
            _ = RestorePresentationAfterDelayAsync(generation);
            return;
        }
        try
        {
            if (_closing || generation != _presentationRestoreGeneration ||
                !_ownsPresentationFlag || _openPanels.Count != 0) return;
            CancelSignalPreparation();
            var restored = await Task.Run(NativeCarControl.RestoreFreeRoamPresentationFlag);
            _ownsPresentationFlag = false;
            SetControlsStatus(restored, Good);
        }
        catch (Exception ex)
        {
            if (generation == _presentationRestoreGeneration) SetControlsStatus(ex.Message, Warn);
        }
        finally { _actionGate.Release(); }
    }

    private async Task MaxDetailAsync()
    {
        if (!await TryEnterActionAsync("maxdetail")) return;
        MaxDetailButton.IsEnabled = false;
        try
        {
            CancelSignalPreparation();
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

    private async Task SignalsAsync(string action)
    {
        if (_closing) return;
        if (!LightingFeaturePolicy.ExperimentalSignalsEnabled)
        {
            SetControlsStatus(LightingFeaturePolicy.SignalsDeferredMessage, Dim);
            return;
        }
        if (action == "signalhazards" && UsesDrlHazards)
        {
            await ToggleDrlHazardsAsync();
            return;
        }
        if (_signalPreparation is not null || _drlColorPreparation is not null || _actionGate.IsManualBusy)
        {
            RejectBusyAction(action, "A signal search or control is already executing");
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _signalPreparation = cancellation;
        _signalSearchNotice = null;
        var clock = Stopwatch.StartNew();
        SetSignalButtonsEnabled(false);
        try
        {
            SetControlsStatus("Searching player-car lamps (read-only). Other controls remain available; RESET STATE cancels.", Warn);
            var preparation = await Task.Run(() => NativeCarControl.PrepareSignals(action, cancellation.Token));
            if (_closing || cancellation.IsCancellationRequested) return;
            // There is no queued activation: revalidate immediately under the
            // short mutation lock, or discard if another control is executing.
            if (!await TryEnterActionAsync(action)) return;
            try
            {
                var result = await Task.Run(() => NativeCarControl.ToggleSignals(action, preparation, cancellation.Token));
                if (!result.Success) _signalSearchNotice = result.Message;
                _signalStatus = await Task.Run(NativeCarControl.GetSignalStatus);
                if (_closing) return;
                RefreshSignalLabels();
                SetControlsStatus(result.Message, result.Success ? Good : Warn);
            }
            finally { _actionGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            SessionLog.Write("signals_search_cancelled", $"read-only search cancelled; elapsedMs={clock.ElapsedMilliseconds}", action);
        }
        catch (Exception ex)
        {
            SessionLog.Write("signals_action_error", ex.ToString(), action,
                _sessionProcessId, _sessionCarToken, _sessionComponent);
            if (!_closing && !cancellation.IsCancellationRequested)
            {
                _signalSearchNotice = ex.Message;
                SetControlsStatus(ex.Message, Warn);
            }
        }
        finally
        {
            SessionLog.Write("signals_action_finished", $"elapsedMs={clock.ElapsedMilliseconds}; cancelled={cancellation.IsCancellationRequested}", action);
            if (ReferenceEquals(_signalPreparation, cancellation)) _signalPreparation = null;
            if (!_closing)
            {
                SetSignalButtonsEnabled(true);
                RefreshSignalLabels();
                await PollStatusAsync();
            }
        }
    }

    private async Task<bool> TryEnterActionAsync(string action)
    {
        if (_closing) return false;
        var clock = Stopwatch.StartNew();
        if (await _actionGate.TryEnterManualAsync())
        {
            if (!_closing)
            {
                _actionGeneration++;
                SessionLog.Write("action_admitted", $"waitMs={clock.ElapsedMilliseconds}", action,
                    _sessionProcessId, _sessionCarToken, _sessionComponent);
                return true;
            }
            _actionGate.Release();
            return false;
        }
        if (!_closing) RejectBusyAction(action, "Another control is still executing");
        return false;
    }

    private void RejectBusyAction(string action, string reason)
    {
        SessionLog.Write("action_not_queued", reason, action,
            _sessionProcessId, _sessionCarToken, _sessionComponent);
        SetControlsStatus(reason + "; this press was NOT queued. Try again when it finishes.", Warn);
    }

    private void CancelSignalPreparation()
    {
        _signalPreparation?.Cancel();
        NativeCarControl.InvalidateSignalDiscovery();
    }

    private void SetSignalButtonsEnabled(bool enabled)
    {
        enabled &= LightingFeaturePolicy.ExperimentalSignalsEnabled;
        LeftIndicatorButton.IsEnabled = enabled && _signalStatus.Available("signalleft");
        RightIndicatorButton.IsEnabled = enabled && _signalStatus.Available("signalright");
        HazardsButton.IsEnabled = enabled && _drlColorPreparation is null &&
            (_signalStatus.Available("signalhazards") || DrlHazardsAvailable);
    }

    private void RefreshSignalLabels()
    {
        foreach (var action in new[] { "signalleft", "signalright", "signalhazards" })
        {
            var on = LightingFeaturePolicy.ExperimentalSignalsEnabled && !_signalStatus.NeedsReset &&
                _signalStatus.Mode == SignalPolicy.Requested(action);
            if (action == "signalhazards" && UsesDrlHazards)
                on = !_drlColorStatus.NeedsReset && _drlColorStatus.Mode == DrlColorMode.Hazards;
            _buttonsByAction[action].Style = (Style)FindResource(on ? "PinkFilled" : "GhostButton");
            RefreshLabel(action);
        }
        SignalsHint.Text = (UsesDrlHazards
            ? "DRL hazards flash mapped DRLs amber together; other indicators and rear lamps are not included. FRONT DRL must be ON. OFF returns the previous color/RGB/strobe choice."
            : _signalPreparation is { IsCancellationRequested: false }
            ? "Read-only signal search in progress. RESET STATE cancels; repeated signal presses are not queued."
            : _signalSearchNotice ?? _signalStatus.Message) +
            " Turn OFF before changing car/scene. RESET STATE restores routing. Steam development feature.";
    }

    private async Task LightsAsync(string action)
    {
        var clock = Stopwatch.StartNew();
        if (!await TryEnterActionAsync(action)) return;
        try
        {
            double fadeSeconds = _lightFadeEnabled ? _lightFadeSeconds : 0;
            var (result, colorStatus) = await Task.Run(() =>
            {
                var result = NativeCarControl.ToggleLights(action, fadeSeconds);
                var color = result.Success ? NativeCarControl.RefreshNativeDrlStrobeAfterLights() : null;
                return (result, color);
            });
            if (colorStatus is not null)
            {
                _drlColorStatus = colorStatus;
                RefreshDrlColorControls();
            }
            // The native toggle already reads back and verifies this state.
            _lightStatus = result.LightStatus ?? await Task.Run(NativeCarControl.GetLightStatus);
            RefreshLightLabels();
            RefreshDrlColorTimerInterval();
            SetControlsStatus(result.Message, result.Success ? Good : Warn);
        }
        catch (Exception ex)
        {
            SessionLog.Write("lighting_action_error", ex.ToString(), action,
                _sessionProcessId, _sessionCarToken, _sessionComponent);
            SetControlsStatus(ex.Message, Warn);
        }
        finally
        {
            SessionLog.Write("lighting_action_finished", $"elapsedMs={clock.ElapsedMilliseconds}", action,
                _sessionProcessId, _sessionCarToken, _sessionComponent);
            _actionGate.Release();
        }
    }

    private void RefreshLightLabels()
    {
        foreach (var action in new[] { "lightsdrl", "lightsrear", "lightsheadlights", "lightsall" })
        {
            var on = _lightStatus.Available && (action == "lightsdrl" ? _lightStatus.DrlOn :
                action == "lightsrear" ? _lightStatus.RunningLightsOn :
                action == "lightsheadlights" ? _lightStatus.HeadlightsOn : _lightStatus.AllOn);
            _buttonsByAction[action].Style = (Style)FindResource(on ? "PinkFilled" : "GhostButton");
            RefreshLabel(action);
        }
        LightsHint.Text = !_lightStatus.Available ? _lightStatus.Message :
            _lightStatus.Fading ? "Fade running. Press again to reverse." : "RESET STATE restores original lighting.";
    }

    private async Task PollStatusAsync()
    {
        if (_polling || _closing || _lightStatus.Fading || _actionGate.IsManualBusy) return;
        var generation = _actionGeneration;
        _polling = true;
        try
        {
            await UpdateGameStatusAsync();
            if (DeferStatusSnapshot(generation)) return;
            var status = await Task.Run(NativeCarControl.GetStatus);
            if (DeferStatusSnapshot(generation)) return;
            var lights = status.Ready
                ? await Task.Run(NativeCarControl.GetLightStatus)
                : new(false, false, false, "Lighting requires a loaded, supported player car.");
            if (DeferStatusSnapshot(generation)) return;
            _lightStatus = lights;
            RefreshLightLabels();
            if (_closing) return;
            if (_sessionProcessId != status.ProcessId || _sessionVehicle != status.Vehicle ||
                _sessionComponent != status.Component ||
                (status.CarToken is not null &&
                 !string.Equals(_sessionCarToken, status.CarToken, StringComparison.OrdinalIgnoreCase)))
            {
                SessionLog.Write("session_change",
                    $"{_sessionCarToken ?? "<none>"} -> {status.CarToken ?? "<none>"}; ready={status.Ready}; vehicle=0x{status.Vehicle:X}; maxDetail={_maxDetailOn}",
                    gamePid: status.ProcessId, carToken: status.CarToken, component: status.Component);
                NativeCarControl.InvalidateWindowCache();
                _bindingGeneration++;
                CancelSignalPreparation();
                _signalSearchNotice = null;
                CancelDrlColorPreparation();
                await Task.Run(() => NativeCarControl.ReconcileTrackedDrlColor(
                    status.ProcessId, status.Vehicle, status.Component, status.CarToken));
                CancelScheduledPresentationRestore();
                _sessionProcessId = status.ProcessId;
                _sessionVehicle = status.Vehicle;
                _sessionComponent = status.Component;
                _sessionCarToken = status.CarToken;
                _openPanels.Clear();
                _openWindows.Clear();
                _ownsPresentationFlag = false;
                RefreshAllLabels();
            }
            var signals = await Task.Run(NativeCarControl.GetSignalStatus);
            if (DeferStatusSnapshot(generation)) return;
            _signalStatus = signals;
            RefreshSignalLabels();
            var colors = await Task.Run(NativeCarControl.GetDrlColorStatus);
            if (DeferStatusSnapshot(generation)) return;
            _drlColorStatus = colors;
            RefreshDrlColorControls();
            if (_capturingAction is null && _capturingControllerAction is null &&
                _signalPreparation is null &&
                _drlColorPreparation is null &&
                ControlsStatusText.Text != status.Message)
                SetControlsStatus(status.Message, status.Ready ? Good : Warn);

            if (_bindMode) { SetActionsEnabled(true); return; }
            SetActionsEnabled(false);
            if (status.Ready)
            {
                foreach (var action in NativeCarControl.SupportedFreeRoamPanelActions
                    .Where(action => action.StartsWith("open", StringComparison.OrdinalIgnoreCase)))
                    if (_buttonsByAction.TryGetValue("toggle" + action[4..], out var b))
                        b.IsEnabled = true;
                if (NativeCarControl.WindowControlsEnabled && status.WindowControlsMapped)
                    foreach (var part in Parts.Where(part =>
                        part.OpenAction.StartsWith("openwindow", StringComparison.OrdinalIgnoreCase)))
                        if (_buttonsByAction.TryGetValue(
                            "togglewindow" + part.OpenAction["openwindow".Length..], out var b))
                            b.IsEnabled = true;
                if (_buttonsByAction.TryGetValue("toggleroof", out var roof)) roof.IsEnabled = true;
                AllPanelsButton.IsEnabled = true;
                ResetButton.IsEnabled = true;
                MaxDetailButton.IsEnabled = true;
                DrlButton.IsEnabled = _lightStatus.Available;
                RearDrlButton.IsEnabled = _lightStatus.Available && _lightStatus.RearControlAvailable;
                HeadlightButton.IsEnabled = _lightStatus.Available && _lightStatus.HeadlightControlAvailable;
                AllLightsButton.IsEnabled = _lightStatus.Available;
                SetSignalButtonsEnabled(_signalPreparation is null);
            }
        }
        catch (Exception ex)
        {
            SessionLog.Write("status_poll_error", ex.ToString(), gamePid: _sessionProcessId,
                carToken: _sessionCarToken, component: _sessionComponent);
            SetControlsStatus($"status check failed: {ex.Message}", Warn);
        }
        finally { _polling = false; }
    }

    // A poll begun before a button press must not overwrite its verified labels.
    private bool DeferStatusSnapshot(long generation) => _closing ||
        _lightStatus.Fading || _actionGate.IsManualBusy || generation != _actionGeneration;

    private async Task UpdateGameStatusAsync()
    {
        using var process = Process.GetProcessesByName("forzahorizon6").FirstOrDefault();
        if (process is null) { GameStatusText.Text = "game not running"; GameStatusText.Foreground = Dim; return; }
        var liveBuild = await Task.Run(NativeCarControl.GetCurrentBuildName);
        if (liveBuild is not null)
        {
            GameStatusText.Text = $"{liveBuild} — PID {process.Id}";
            GameStatusText.Foreground = Good;
            return;
        }
        string? path;
        try { path = process.MainModule?.FileName; } catch { path = null; }
        if (string.IsNullOrWhiteSpace(path)) { GameStatusText.Text = $"running (PID {process.Id})"; GameStatusText.Foreground = Warn; return; }

        var writeTimeUtc = File.GetLastWriteTimeUtc(path);
        if (!string.Equals(_hashedPath, path, StringComparison.OrdinalIgnoreCase) || _hashedWriteTimeUtc != writeTimeUtc)
        {
            _hashedPath = path;
            _hashedWriteTimeUtc = writeTimeUtc;
            try { _hashedDigest = await Task.Run(() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))); }
            catch (UnauthorizedAccessException) { _hashedDigest = null; }
            catch (IOException) { _hashedDigest = null; }
        }
        var supported = NativeCarControl.IsSupportedDigest(_hashedDigest);
        GameStatusText.Text = supported ? $"supported build — PID {process.Id}" : $"unsupported build — PID {process.Id}";
        GameStatusText.Foreground = supported ? Good : Warn;
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var b in _actionButtons) b.IsEnabled = enabled;
        if (!LightingFeaturePolicy.ExperimentalSignalsEnabled) SetSignalButtonsEnabled(false);
    }

    private void SetControlsStatus(string text, Brush brush)
    {
        ControlsStatusText.Text = text;
        ControlsStatusText.Foreground = brush;
        SessionLog.Write("menu_message", text, gamePid: _sessionProcessId,
            carToken: _sessionCarToken, component: _sessionComponent);
    }

    private NativeLightActionResult RestoreOnExit()
    {
        CancelScheduledPresentationRestore();
        var cleanupClock = System.Diagnostics.Stopwatch.StartNew();
        SessionLog.Write("exit_cleanup_started", $"vehicle=0x{_sessionVehicle:X}; maxDetail={_maxDetailOn}; ownsPresentation={_ownsPresentationFlag}; openPanels={string.Join(',', _openPanels)}; openWindows={string.Join(',', _openWindows)}",
            gamePid: _sessionProcessId, carToken: _sessionCarToken, component: _sessionComponent);
        NativeLightActionResult colors;
        try { colors = NativeCarControl.RestoreTrackedDrlColor(); }
        catch (Exception ex)
        {
            SessionLog.Write("drl_color_restore_on_exit_failed", ex.ToString());
            colors = new(false, ex.Message);
        }
        SessionLog.Write("exit_cleanup_step", $"DRL colors: success={colors.Success}; {colors.Message}");
        NativeLightActionResult signals;
        try { signals = NativeCarControl.RestoreTrackedSignals(); }
        catch (Exception ex)
        {
            SessionLog.Write("signal_restore_on_exit_failed", ex.ToString());
            signals = new(false, ex.Message);
        }
        SessionLog.Write("exit_cleanup_step", $"signals: success={signals.Success}; {signals.Message}");
        try { SessionLog.Write("exit_cleanup_step", "windows: " + NativeCarControl.RestoreTrackedWindows()); }
        catch (Exception ex) { SessionLog.Write("window_restore_on_exit_failed", ex.ToString()); }
        NativeLightActionResult lights;
        try { lights = new(true, NativeCarControl.RestoreTrackedLights()); }
        catch (Exception ex)
        {
            SessionLog.Write("light_restore_on_exit_failed", ex.ToString());
            lights = new(false, "Lighting cleanup failed: " + ex.GetBaseException().Message);
        }
        SessionLog.Write("exit_cleanup_step", $"normal lights: success={lights.Success}; {lights.Message}");
        // Native ownership is also captured if a later trigger fails before
        // the UI marks its panel open. Restore only that captured ownership.
        try { SessionLog.Write("exit_cleanup_step", "presentation: " + NativeCarControl.RestoreFreeRoamPresentationFlag()); }
        catch (Exception ex) { SessionLog.Write("presentation_restore_on_exit_failed", ex.ToString()); }
        _openPanels.Clear();
        _ownsPresentationFlag = false;
        var failures = new[] { colors, signals, lights }.Where(result => !result.Success).ToArray();
        var cleanup = failures.Length == 0 ? new NativeLightActionResult(true, "Tracked lighting restored.")
            : new(false, string.Join(" ", failures.Select(result => result.Message)));
        SessionLog.Write("exit_lighting_cleanup", $"success={cleanup.Success}; elapsedMs={cleanupClock.ElapsedMilliseconds}; {cleanup.Message}");
        return cleanup;
    }

    private async void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRestored) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _bindingGeneration++;
        CancelSignalPreparation();
        CancelScheduledPresentationRestore();
        CancelDrlColorPreparation();
        _drlColorTimer.Stop();
        LogLightingFrameTiming();
        _statusTimer.Stop();
        _controllerTimer.Stop();
        UnregisterAllHotkeys();
        SetActionsEnabled(false);
        // Only the short mutation/restore phase can hold this gate now. The
        // cancelled read-only scanner owns no writable handle and cannot commit.
        await _actionGate.EnterForCleanupAsync();
        var closeAfterRestore = false;
        try
        {
            var result = await Task.Run(RestoreOnExit);
            if (!result.Success && MessageBox.Show(this,
                    result.Message + "\n\nReturn to normal Free Roam and use RESET STATE to retry.\nExit anyway without verified cleanup?",
                    "Lamp cleanup not verified", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                _closing = false;
                _statusTimer.Start();
                _controllerTimer.Start();
                _drlColorTimer.Start();

                RegisterAllHotkeys();
                SetControlsStatus(result.Message, Warn);
                return;
            }
            closeAfterRestore = true;
        }
        finally
        {
            _actionGate.Release();
            if (closeAfterRestore)
            {
                _exitRestored = true;
                Close();
            }
            else if (!_closing) _ = PollStatusAsync();
        }
    }

    // ================= hotkeys =================

    private void BindMode_Click(object sender, RoutedEventArgs e)
    {
        _bindMode = !_bindMode;
        if (!_bindMode && _capturingAction is not null) CancelCapture();
        BindModeButton.Content = _bindMode ? "SET BINDING: ON" : "SET BINDING: OFF";
        BindModeButton.Style = (Style)FindResource(_bindMode ? "PinkFilled" : "GhostButton");
        if (_bindMode)
        {
            SetActionsEnabled(true);
            SetControlsStatus("click an action, then press a keyboard key or Xbox controller combination", Warn);
        }
        else
        {
            RegisterAllHotkeys();
            _ = PollStatusAsync();
        }
    }

    private void ClearBindings_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingAction is not null) CancelCapture();
        _hotkeys.Clear();
        _controllerBindings.Clear();
        SaveHotkeys();
        SaveControllerBindings();
        RegisterAllHotkeys();
        RefreshAllLabels();
        SetControlsStatus("all keyboard and controller bindings cleared", Good);
    }

    private void BeginCapture(string action)
    {
        if (LightingFeaturePolicy.IsSignalAction(action) && !LightingFeaturePolicy.ExperimentalSignalsEnabled) return;
        _capturingAction = action;
        _capturingControllerAction = action;
        _controllerCaptureArmed = false;
        _controllerCaptureMask = 0;
        UnregisterAllHotkeys();   // free the keys so the pressed key reaches this window
        Activate();
        SetControlsStatus($"press a key or Xbox button combination for '{GetBaseLabel(action)}' (Esc to cancel)", Warn);
    }

    private void CancelCapture()
    {
        _capturingAction = null;
        _capturingControllerAction = null;
        _controllerCaptureArmed = false;
        _controllerCaptureMask = 0;
        RegisterAllHotkeys();
        SetControlsStatus("binding cancelled", Dim);
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
        _capturingControllerAction = null;
        _controllerCaptureArmed = false;
        _controllerCaptureMask = 0;
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
        var keyboardRemoved = _hotkeys.Remove(action);
        var controllerRemoved = _controllerBindings.Remove(action);
        if (!keyboardRemoved && !controllerRemoved) return;
        if (keyboardRemoved) SaveHotkeys();
        if (controllerRemoved) SaveControllerBindings();
        RegisterAllHotkeys();
        RefreshLabel(action);
        SetControlsStatus($"cleared bindings for '{GetBaseLabel(action)}'", Good);
    }

    private void ControllerTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (!XboxControllerInput.TryGetFirstConnected(out var controllerIndex, out var currentMask))
            {
                _activeControllerIndex = null;
                _previousControllerMask = 0;
                return;
            }

            if (_activeControllerIndex != controllerIndex)
            {
                _activeControllerIndex = controllerIndex;
                _previousControllerMask = currentMask;
            }

            if (_capturingControllerAction is not null)
            {
                CaptureControllerBinding(currentMask);
                _previousControllerMask = currentMask;
                return;
            }

            if (!_bindMode && IsGameForeground())
            {
                var match = _controllerBindings
                    .Where(binding => LightingFeaturePolicy.ExperimentalSignalsEnabled ||
                        !LightingFeaturePolicy.IsSignalAction(binding.Key))
                    .Where(binding => (currentMask & binding.Value) == binding.Value &&
                                      (_previousControllerMask & binding.Value) != binding.Value)
                    .OrderByDescending(binding => XboxControllerInput.ButtonCount(binding.Value))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(match.Key))
                {
                    var actions = _controllerBindings
                        .Where(binding => binding.Value == match.Value)
                        .Select(binding => binding.Key).ToArray();
                    _ = InvokeBoundActionsAsync(actions, "controller");
                }
            }
            _previousControllerMask = currentMask;
        }
        catch
        {
            // Controller polling is optional and must never interrupt the menu.
            _previousControllerMask = 0;
        }
    }

    private void CaptureControllerBinding(uint currentMask)
    {
        if (!_controllerCaptureArmed)
        {
            if (currentMask == 0) _controllerCaptureArmed = true;
            return;
        }

        if (_controllerCaptureMask == 0)
        {
            if (currentMask == 0) return;
            _controllerCaptureMask = currentMask;
            _controllerCaptureStartedUtc = DateTime.UtcNow;
            return;
        }

        _controllerCaptureMask |= currentMask;
        if (currentMask != 0 && DateTime.UtcNow - _controllerCaptureStartedUtc < TimeSpan.FromMilliseconds(250))
            return;

        var action = _capturingControllerAction!;
        var binding = _controllerCaptureMask;
        _capturingAction = null;
        _capturingControllerAction = null;
        _controllerCaptureArmed = false;
        _controllerCaptureMask = 0;
        _controllerBindings[action] = binding;
        SaveControllerBindings();
        RefreshLabel(action);
        SetControlsStatus($"bound '{GetBaseLabel(action)}' → Xbox {XboxControllerInput.Display(binding)}", Good);
    }

    private void RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();
        // RegisterHotKey reserves the gesture across Windows. Reserve it only
        // while the game owns the foreground window so other apps are unaffected.
        if (_hwnd == IntPtr.Zero || _bindMode || _capturingAction is not null || !IsGameForeground()) return;
        var id = 1;
        foreach (var group in _hotkeys.Where(binding => LightingFeaturePolicy.ExperimentalSignalsEnabled ||
                     !LightingFeaturePolicy.IsSignalAction(binding.Key)).GroupBy(binding => binding.Value))
        {
            var hk = group.Key;
            uint fs = MOD_NOREPEAT
                | (hk.Mods.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
                | (hk.Mods.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
                | (hk.Mods.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0);
            var vk = (uint)KeyInterop.VirtualKeyFromKey(hk.Key);
            if (vk != 0 && RegisterHotKey(_hwnd, id, fs, vk))
                _hotkeyIdToActions[id] = group.Select(binding => binding.Key).ToArray();
            id++;
        }
    }

    private void UnregisterAllHotkeys()
    {
        if (_hwnd != IntPtr.Zero)
            foreach (var id in _hotkeyIdToActions.Keys.ToList())
                UnregisterHotKey(_hwnd, id);
        _hotkeyIdToActions.Clear();
    }

    private void ForegroundWindowChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(new Action(RegisterAllHotkeys));
    }

    private static bool IsGameForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return NativeCarControl.IsGameProcessName(process.ProcessName);
        }
        catch { return false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _capturingAction is null &&
            _hotkeyIdToActions.TryGetValue(wParam.ToInt32(), out var actions))
        {
            if (!IsGameForeground())
            {
                UnregisterAllHotkeys();
                return IntPtr.Zero;
            }
            handled = true;
            _ = Dispatcher.InvokeAsync(async () => await InvokeBoundActionsAsync(actions, "keyboard"));
        }
        return IntPtr.Zero;
    }

    // ================= labels & persistence =================

    private string GetBaseLabel(string action) =>
        action == "signalhazards" && UsesDrlHazards ? DrlHazardsLabel
        : action is "signalleft" or "signalright" or "signalhazards"
            ? SignalPolicy.Label(action, _signalStatus)
        : action is "lightsdrl" or "lightsrear" or "lightsheadlights" or "lightsall"
            ? $"{(action == "lightsdrl" ? "FRONT DRL" : action == "lightsrear" ? "REAR DRL" : action == "lightsheadlights" ? "HEADLIGHTS" : "ALL LIGHTS")}: " +
                (!_lightStatus.Available || action == "lightsheadlights" && !_lightStatus.HeadlightControlAvailable ||
                    action == "lightsrear" && !_lightStatus.RearControlAvailable
                    ? "—" : action == "lightsall" && _lightStatus.AnyOn && !_lightStatus.AllOn
                    ? "MIXED" : (action == "lightsdrl" ? _lightStatus.DrlOn : action == "lightsrear" ? _lightStatus.RunningLightsOn :
                        action == "lightsheadlights" ? _lightStatus.HeadlightsOn : _lightStatus.AllOn) ? "ON" : "OFF")
        :
        action.Equals("toggleall", StringComparison.OrdinalIgnoreCase)
            ? (AreAllExplodePanelsOpen() ? "IMPLODE" : "EXPLODE")
        : action.Equals("maxdetail", StringComparison.OrdinalIgnoreCase)
            ? (_maxDetailOn ? "MAX DETAIL: ON" : "MAX DETAIL: OFF")
            : action.StartsWith("toggle", StringComparison.OrdinalIgnoreCase)
                ? (Parts.FirstOrDefault(part =>
                    action.Equals("toggle" + part.OpenAction[4..], StringComparison.OrdinalIgnoreCase)) is { } part &&
                    IsPartOpen(part) ? "CLOSE" : "OPEN")
            : _baseLabels.TryGetValue(action, out var label) ? label : action;

    private void RefreshLabel(string action)
    {
        if (!_buttonsByAction.TryGetValue(action, out var b)) return;
        var baseLabel = GetBaseLabel(action);
        var bindings = new List<string>();
        if (_hotkeys.TryGetValue(action, out var hk)) bindings.Add(Display(hk.Mods, hk.Key));
        if (_controllerBindings.TryGetValue(action, out var controller))
            bindings.Add($"Xbox {XboxControllerInput.Display(controller)}");
        b.Content = bindings.Count == 0 ? baseLabel : $"{baseLabel}   [{string.Join("] [", bindings)}]";
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
            MigrateLegacyAllBinding(_hotkeys);
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

    private void LoadControllerBindings()
    {
        try
        {
            if (!File.Exists(ControllerBindingPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ControllerBindingPath));
            if (map is null) return;
            foreach (var (action, gesture) in map)
                if (XboxControllerInput.TryParse(gesture, out var mask)) _controllerBindings[action] = mask;
            MigrateLegacyAllBinding(_controllerBindings);
        }
        catch { }
    }

    private void SaveControllerBindings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ControllerBindingPath)!);
            var map = _controllerBindings.ToDictionary(kv => kv.Key, kv => XboxControllerInput.Display(kv.Value));
            File.WriteAllText(ControllerBindingPath,
                JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void MigrateLegacyAllBinding<T>(Dictionary<string, T> bindings)
    {
        if (!bindings.ContainsKey("toggleall"))
        {
            if (bindings.TryGetValue("explode", out var explode))
                bindings["toggleall"] = explode;
            else if (bindings.TryGetValue("implode", out var implode))
                bindings["toggleall"] = implode;
        }
        bindings.Remove("explode");
        bindings.Remove("implode");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHook,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
