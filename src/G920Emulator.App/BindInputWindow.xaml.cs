using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
        ConfigureAxisRangeUi(binding);

        HintText.Text = _wantsAxis
            ? "Move a stick, trigger, wheel, or pedal to add a source…"
            : _wantsHat
                ? "Move a POV hat / D-pad to bind it. No hat on your pad? Use the D-pad Up/Down/Left/Right rows instead."
                : "Press a button or move an axis to add a source (axes become digital presses for this button).";
        UpdateListeningText();
        UpdateAxisUi();

        _listenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _listenTimer.Tick += (_, _) =>
        {
            PollListen();
            UpdateLiveAxisMeter();
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _listenTimer.Stop();
    }

    private bool HasAxisSource => _sources.Any(s => s.Source.Axis is not null);

    private void ConfigureGearReverseUi()
    {
        if (!_isGearR || GearReversePanel is null || GearReverseButtonCombo is null)
            return;

        GearReversePanel.Visibility = Visibility.Visible;
        Height = 620;

        var items = new List<GearReverseOption>();
        for (var btn = 1; btn <= 19; btn++)
        {
            var label = btn switch
            {
                19 => "19 - G920 / LGS (default)",
                12 => "12 - NFS Unbound",
                _ => btn.ToString(),
            };
            items.Add(new GearReverseOption(btn, label));
        }

        GearReverseButtonCombo.ItemsSource = items;
        var selected = Math.Clamp(_profile.GearReverseOutputButton <= 0 ? 19 : _profile.GearReverseOutputButton, 1, 19);
        GearReverseButtonCombo.SelectedItem = items.First(i => i.Button == selected);
    }

    private void ConfigureAxisRangeUi(Binding binding)
    {
        if (_wantsHat)
        {
            if (AxisRangePanel is not null)
                AxisRangePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_wantsAxis && AxisRangePanel is not null && AxisRangeSlider is not null)
        {
            AxisRangePanel.Visibility = Visibility.Visible;
            AxisRangeSlider.LowerValue = Math.Clamp(binding.AxisStart, 0, 0.95);
            AxisRangeSlider.UpperValue = Math.Clamp(binding.AxisEnd <= 0 ? 1 : binding.AxisEnd, 0.05, 1);
            if (AxisRangeSlider.UpperValue < AxisRangeSlider.LowerValue + 0.05)
                AxisRangeSlider.UpperValue = Math.Min(1, AxisRangeSlider.LowerValue + 0.05);
            UpdateAxisRangeLabel();
        }

        if (_wantsButton && AxisThresholdSlider is not null)
        {
            var thr = binding.Deadzone > 0.001 ? binding.Deadzone : 0.5;
            AxisThresholdSlider.Value = Math.Clamp(thr, 0.05, 0.95);
            AxisThresholdValueText.Text = $"{AxisThresholdSlider.Value:P0}";
        }
    }

    private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLiveAxisMeter();

    private void UpdateAxisUi()
    {
        if (!_wantsButton || AxisDetectedPanel is null)
            return;

        AxisDetectedPanel.Visibility = HasAxisSource ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { _refreshDevices(); } catch { /* ignore */ }
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
        _listenTimer.Start();
        UpdateLiveAxisMeter();
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
                    AcceptCapture(new SourceRef
                    {
                        DeviceId = id,
                        Axis = axis,
                        AxisFromCenter = false,
                    }, restValue: prev);
                    return;
                }
            }
        }
    }

    private void UpdateLiveAxisMeter()
    {
        if (!_wantsButton || LiveAxisSlider is null || LiveAxisValueText is null)
            return;

        if (!HasAxisSource)
        {
            LiveAxisSlider.Value = 0;
            LiveAxisValueText.Text = "-";
            return;
        }

        var axisSource = (SourcesList.SelectedItem as SourceRow)?.Source;
        if (axisSource?.Axis is null)
            axisSource = _sources.Select(s => s.Source).FirstOrDefault(s => s.Axis is not null);
        if (axisSource?.Axis is null)
        {
            LiveAxisSlider.Value = 0;
            LiveAxisValueText.Text = "-";
            return;
        }

        var devices = _poll();
        if (!devices.TryGetValue(axisSource.DeviceId, out var device) ||
            !device.Axes.TryGetValue(axisSource.Axis, out var raw))
        {
            LiveAxisSlider.Value = 0;
            LiveAxisValueText.Text = "-";
            return;
        }

        raw = Math.Clamp(raw, 0f, 1f);
        var shown = InvertCheck.IsChecked == true ? 1f - raw : raw;
        LiveAxisSlider.Value = shown;
        LiveAxisValueText.Text = $"{shown:P0}";
    }

    private void AcceptCapture(SourceRef source, float? restValue = null)
    {
        source.AxisFromCenter = false;
        source.ProductId ??= _resolveProductId(source.DeviceId);

        if (_sources.Any(s => SameSource(s.Source, source)))
        {
            _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
            return;
        }

        if (_sources.Count == 0 && restValue is float rest)
        {
            if (_wantsAxis &&
                _target is G920Control.Throttle or G920Control.Brake or G920Control.Clutch)
            {
                InvertCheck.IsChecked = rest > 0.5f;
            }
            else if (_wantsButton && source.Axis is not null)
            {
                InvertCheck.IsChecked = rest > 0.7f;
            }
        }

        _sources.Add(new SourceRow(CloneSource(source)!, _resolveName, _wantsAxis));
        SourcesList.SelectedItem = _sources[^1];
        UpdateListeningText();
        UpdateAxisUi();
        UpdateLiveAxisMeter();
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
    }

    private void UpdateListeningText()
    {
        if (_wantsButton)
        {
            ListeningText.Text = _sources.Count == 0
                ? "Listening for button, hat, or axis…"
                : $"Listening for another input… ({_sources.Count} source{(_sources.Count == 1 ? "" : "s")})";
            return;
        }

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
            UpdateAxisUi();
            UpdateLiveAxisMeter();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _sources.Clear();
        UpdateListeningText();
        UpdateAxisUi();
        UpdateLiveAxisMeter();
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AxisRangeSlider_RangeChanged(object? sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateAxisRangeLabel();

    private void UpdateAxisRangeLabel()
    {
        if (AxisRangeValueText is null || AxisRangeSlider is null) return;
        AxisRangeValueText.Text = $"{AxisRangeSlider.LowerValue:P0}–{AxisRangeSlider.UpperValue:P0}";
    }

    private void AxisThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AxisThresholdValueText is null) return;
        AxisThresholdValueText.Text = $"{AxisThresholdSlider.Value:P0}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var hasAxis = _sources.Any(s => s.Source.Axis is not null);

        _binding.ClearSources();
        foreach (var row in _sources)
        {
            var clone = CloneSource(row.Source)!;
            clone.AxisFromCenter = false;
            _binding.Sources.Add(clone);
        }

        _binding.Invert = InvertCheck.IsChecked == true;

        if (_wantsAxis)
        {
            _binding.UseAxisAsButton = false;
            _binding.AxisStart = AxisRangeSlider.LowerValue;
            _binding.AxisEnd = AxisRangeSlider.UpperValue;
            _binding.Deadzone = _target == G920Control.Steering ? _binding.Deadzone : _binding.AxisStart;
        }
        else if (_wantsButton)
        {
            // Button targets always treat axes as digital presses (Activate on Axis threshold).
            _binding.UseAxisAsButton = hasAxis;
            _binding.AxisStart = 0;
            _binding.AxisEnd = 1;
            _binding.Deadzone = hasAxis ? AxisThresholdSlider.Value : 0;
        }
        else
        {
            _binding.UseAxisAsButton = false;
            _binding.AxisStart = 0;
            _binding.AxisEnd = 1;
            _binding.Deadzone = 0;
        }

        if (_isGearR && GearReverseButtonCombo?.SelectedItem is GearReverseOption opt)
            _profile.GearReverseOutputButton = opt.Button;

        _binding.Normalize();
        Applied = true;
        DialogResult = true;
        Close();
    }

    private sealed record GearReverseOption(int Button, string Label);

    private static bool SameSource(SourceRef a, SourceRef b) =>
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Axis, b.Axis, StringComparison.OrdinalIgnoreCase) &&
        a.Button == b.Button &&
        a.IsHat == b.IsHat;

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
            AxisFromCenter = false,
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
                    return axisTarget
                        ? $"{dev} · {Source.Axis} (axis)"
                        : $"{dev} · {Source.Axis} (axis→btn)";
                if (Source.IsHat) return $"{dev} · hat";
                if (Source.Button is int b) return $"{dev} · button {b}";
                return dev;
            }
        }
    }
}
