using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

public partial class BindInputWindow : Window
{
    private readonly G920Control _target;
    private readonly Binding _binding;
    private readonly MappingProfile _profile;
    private readonly Func<IReadOnlyDictionary<string, DeviceState>> _poll;
    private readonly Action _refreshDevices;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<string?, string?> _resolveProductId;
    private readonly DispatcherTimer _listenTimer;
    private readonly bool _wantsAxis;
    private readonly bool _wantsHat;
    private readonly bool _wantsButton;
    private readonly bool _isGearR;
    private readonly ObservableCollection<SourceRow> _sources = [];

    private Dictionary<string, DeviceState> _baseline = new();

    public bool Applied { get; private set; }

    public BindInputWindow(
        G920Control target,
        Binding binding,
        MappingProfile profile,
        Func<IReadOnlyDictionary<string, DeviceState>> poll,
        Action refreshDevices,
        Func<string?, string> resolveName,
        Func<string?, string?>? resolveProductId = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _target = target;
        _binding = binding;
        _profile = profile;
        _poll = poll;
        _refreshDevices = refreshDevices;
        _resolveName = resolveName;
        _resolveProductId = resolveProductId ?? (_ => null);
        _wantsAxis = G920ControlInfo.IsAxis(target);
        _wantsHat = target == G920Control.Hat;
        _wantsButton = !_wantsAxis && !_wantsHat;
        _isGearR = target == G920Control.GearR;

        TitleText.Text = $"Assign {G920ControlInfo.DisplayName(target)}";
        InvertCheck.IsChecked = binding.Invert;
        InvertCheck.Visibility = _wantsHat ? Visibility.Collapsed : Visibility.Visible;

        SourcesList.ItemsSource = _sources;
        foreach (var source in binding.EffectiveSources)
            _sources.Add(new SourceRow(CloneSource(source)!, _resolveName, _wantsAxis));

        ConfigureGearReverseUi();
        ConfigureThresholdUi(binding.Deadzone);

        HintText.Text = _wantsAxis
            ? "Move a stick, trigger, wheel, or pedal to add a source…"
            : _wantsHat
                ? "Move a POV hat / D-pad to bind it. No hat on your pad? Use the D-pad Up/Down/Left/Right rows instead."
                : "Press a button or move an axis to add a source (axis→button supported)…";
        UpdateListeningText();
        UpdateInvertLabel();

        _listenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _listenTimer.Tick += (_, _) => PollListen();

        Loaded += OnLoaded;
        Closed += (_, _) => _listenTimer.Stop();
    }

    private void ConfigureGearReverseUi()
    {
        if (!_isGearR || GearReversePanel is null || GearReverseButtonCombo is null)
            return;

        GearReversePanel.Visibility = Visibility.Visible;
        Height = 580;

        var items = new List<GearReverseOption>();
        for (var btn = 1; btn <= 19; btn++)
        {
            var label = btn switch
            {
                19 => "19 — G920 / LGS (default)",
                12 => "12 — NFS Unbound",
                _ => btn.ToString(),
            };
            items.Add(new GearReverseOption(btn, label));
        }

        GearReverseButtonCombo.ItemsSource = items;
        var selected = Math.Clamp(_profile.GearReverseOutputButton <= 0 ? 19 : _profile.GearReverseOutputButton, 1, 19);
        GearReverseButtonCombo.SelectedItem = items.First(i => i.Button == selected);
    }

    private void ConfigureThresholdUi(double savedDeadzone)
    {
        if (_wantsHat)
        {
            DeadzonePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_wantsAxis)
        {
            DeadzonePanel.Visibility = Visibility.Visible;
            DeadzoneLabelText.Text = "Deadzone";
            DeadzoneHintText.Visibility = Visibility.Collapsed;
            DeadzoneSlider.Minimum = 0;
            DeadzoneSlider.Maximum = 0.5;
            DeadzoneSlider.Value = Math.Clamp(savedDeadzone, 0, 0.5);
            DeadzoneSlider.ToolTip = "Ignore small axis movement near rest, then rescale the rest of the throw";
            DeadzoneValueText.Text = $"{DeadzoneSlider.Value:P0}";
            return;
        }

        // Button target: threshold shown when any axis source is present (or always, ready for axis capture).
        DeadzonePanel.Visibility = Visibility.Visible;
        DeadzoneLabelText.Text = "Axis threshold";
        DeadzoneHintText.Visibility = Visibility.Visible;
        DeadzoneSlider.Minimum = 0.05;
        DeadzoneSlider.Maximum = 0.95;
        var threshold = savedDeadzone > 0.001 ? savedDeadzone : 0.5;
        DeadzoneSlider.Value = Math.Clamp(threshold, 0.05, 0.95);
        DeadzoneSlider.ToolTip = "Axis→button activates at or above this value (after Invert)";
        DeadzoneValueText.Text = $"{DeadzoneSlider.Value:P0}";
        RefreshThresholdPanelVisibility();
    }

    private void RefreshThresholdPanelVisibility()
    {
        if (!_wantsButton) return;
        // Keep visible so users know they can bind axes; hint explains it.
        DeadzonePanel.Visibility = Visibility.Visible;
    }

    private void UpdateInvertLabel()
    {
        if (_wantsHat) return;
        if (_wantsAxis)
        {
            InvertCheck.Content = "Invert axis";
            return;
        }

        var hasAxis = _sources.Any(s => s.Source.Axis is not null);
        InvertCheck.Content = hasAxis
            ? "Invert axis→button (rest-high)"
            : "Invert button";
        InvertCheck.ToolTip = hasAxis
            ? "For rest-high axes (some pedals/shifters), invert so resting is released and pull/press activates."
            : "Invert digital button sense for this binding.";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { _refreshDevices(); } catch { /* ignore */ }
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
        _listenTimer.Start();
    }

    private void PollListen()
    {
        var current = _poll();
        foreach (var (id, state) in current)
        {
            _baseline.TryGetValue(id, out var baseline);

            if (_wantsAxis)
            {
                foreach (var (axis, value) in state.Axes)
                {
                    var prev = 0.5f;
                    if (baseline?.Axes.TryGetValue(axis, out var p) == true)
                        prev = p;
                    if (Math.Abs(value - prev) > 0.12f)
                    {
                        AcceptCapture(new SourceRef { DeviceId = id, Axis = axis }, restValue: prev);
                        return;
                    }
                }

                continue;
            }

            if (_wantsHat)
            {
                if (state.Hat >= 0 && (baseline is null || baseline.Hat != state.Hat))
                {
                    AcceptCapture(new SourceRef { DeviceId = id, IsHat = true });
                    return;
                }

                continue;
            }

            // Button targets: accept digital buttons OR axes (axis→button).
            for (var i = 0; i < state.Buttons.Length; i++)
            {
                var was = baseline is not null && i < baseline.Buttons.Length && baseline.Buttons[i];
                if (state.Buttons[i] && !was)
                {
                    AcceptCapture(new SourceRef { DeviceId = id, Button = i });
                    return;
                }
            }

            foreach (var (axis, value) in state.Axes)
            {
                var prev = 0.5f;
                if (baseline?.Axes.TryGetValue(axis, out var p) == true)
                    prev = p;
                if (Math.Abs(value - prev) > 0.18f)
                {
                    var fromCenter = prev is >= 0.35f and <= 0.65f;
                    AcceptCapture(new SourceRef
                    {
                        DeviceId = id,
                        Axis = axis,
                        AxisFromCenter = fromCenter,
                    }, restValue: prev);
                    return;
                }
            }
        }
    }

    private void AcceptCapture(SourceRef source, float? restValue = null)
    {
        source.ProductId ??= _resolveProductId(source.DeviceId);

        if (_sources.Any(s => SameSource(s.Source, source)))
        {
            _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
            return;
        }

        // Pedals/triggers: rest-high (~1) needs Invert; DualSense triggers rest-low (~0) must not.
        if (_sources.Count == 0 && restValue is float rest)
        {
            if (_wantsAxis &&
                _target is G920Control.Throttle or G920Control.Brake or G920Control.Clutch)
            {
                InvertCheck.IsChecked = rest > 0.5f;
            }
            else if (_wantsButton && source.Axis is not null && !source.AxisFromCenter)
            {
                // Rest-high analog control used as a button.
                InvertCheck.IsChecked = rest > 0.7f;
            }
        }

        _sources.Add(new SourceRow(CloneSource(source)!, _resolveName, _wantsAxis));
        SourcesList.SelectedItem = _sources[^1];
        UpdateListeningText();
        UpdateInvertLabel();
        RefreshThresholdPanelVisibility();
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
    }

    private void UpdateListeningText()
    {
        ListeningText.Text = _sources.Count == 0
            ? "Listening for input…"
            : $"Listening for another input… ({_sources.Count} source{(_sources.Count == 1 ? "" : "s")})";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is SourceRow row)
        {
            _sources.Remove(row);
            UpdateListeningText();
            UpdateInvertLabel();
            RefreshThresholdPanelVisibility();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _sources.Clear();
        UpdateListeningText();
        UpdateInvertLabel();
        RefreshThresholdPanelVisibility();
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeadzoneValueText is null) return;
        DeadzoneValueText.Text = $"{DeadzoneSlider.Value:P0}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _binding.ClearSources();
        foreach (var row in _sources)
            _binding.Sources.Add(CloneSource(row.Source)!);
        _binding.Invert = InvertCheck.IsChecked == true;

        if (_wantsAxis)
            _binding.Deadzone = DeadzoneSlider.Value;
        else if (_wantsButton)
        {
            // Persist threshold whenever any axis source is present; otherwise clear.
            _binding.Deadzone = _sources.Any(s => s.Source.Axis is not null)
                ? DeadzoneSlider.Value
                : 0;
        }

        if (_isGearR && GearReverseButtonCombo?.SelectedItem is GearReverseOption opt)
            _profile.GearReverseOutputButton = opt.Button;

        Applied = true;
        DialogResult = true;
        Close();
    }

    private sealed record GearReverseOption(int Button, string Label);

    private static bool SameSource(SourceRef a, SourceRef b) =>
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Axis, b.Axis, StringComparison.OrdinalIgnoreCase) &&
        a.Button == b.Button &&
        a.IsHat == b.IsHat &&
        a.AxisFromCenter == b.AxisFromCenter;

    private static SourceRef? CloneSource(SourceRef? source)
    {
        if (source is null) return null;
        return new SourceRef
        {
            DeviceId = source.DeviceId,
            ProductId = source.ProductId,
            Axis = source.Axis,
            Button = source.Button,
            IsHat = source.IsHat,
            AxisFromCenter = source.AxisFromCenter,
        };
    }

    private static DeviceState CloneState(DeviceState s) => new()
    {
        DeviceId = s.DeviceId,
        Axes = new Dictionary<string, float>(s.Axes, StringComparer.OrdinalIgnoreCase),
        Buttons = (bool[])s.Buttons.Clone(),
        Hat = s.Hat,
    };

    private sealed class SourceRow(SourceRef source, Func<string?, string> resolveName, bool axisTarget)
    {
        public SourceRef Source { get; } = source;
        public string Display
        {
            get
            {
                var dev = resolveName(Source.DeviceId);
                if (Source.Axis is not null)
                {
                    var mode = axisTarget
                        ? "axis"
                        : Source.AxisFromCenter ? "axis→btn (center)" : "axis→btn";
                    return $"{dev} · {Source.Axis} ({mode})";
                }
                if (Source.IsHat) return $"{dev} · hat";
                if (Source.Button is int b) return $"{dev} · button {b}";
                return dev;
            }
        }
    }
}
