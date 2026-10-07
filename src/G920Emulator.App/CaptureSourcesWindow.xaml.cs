using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

/// <summary>Listen popup to capture device sources (used for custom Bind FN / Bind Button).</summary>
public partial class CaptureSourcesWindow : Window
{
    private readonly Func<IReadOnlyDictionary<string, DeviceState>> _poll;
    private readonly Action _refreshDevices;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<string?, string?> _resolveProductId;
    private readonly DispatcherTimer _listenTimer;
    private readonly ObservableCollection<SourceRow> _sources = [];

    private Dictionary<string, DeviceState> _baseline = new();

    public bool Applied { get; private set; }
    public IReadOnlyList<SourceRef> CapturedSources { get; private set; } = [];
    public bool Invert { get; private set; }
    public double Deadzone { get; private set; }

    public CaptureSourcesWindow(
        string title,
        string hint,
        IEnumerable<SourceRef> existing,
        bool invert,
        double deadzone,
        Func<IReadOnlyDictionary<string, DeviceState>> poll,
        Action refreshDevices,
        Func<string?, string> resolveName,
        Func<string?, string?>? resolveProductId = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        TitleText.Text = title;
        HintText.Text = hint;
        _poll = poll;
        _refreshDevices = refreshDevices;
        _resolveName = resolveName;
        _resolveProductId = resolveProductId ?? (_ => null);

        InvertCheck.IsChecked = invert;
        SourcesList.ItemsSource = _sources;
        foreach (var source in existing)
            _sources.Add(new SourceRow(CloneSource(source)!, _resolveName));

        var thr = deadzone > 0.001 ? deadzone : 0.5;
        DeadzoneSlider.Value = Math.Clamp(thr, 0.05, 0.95);
        DeadzoneValueText.Text = $"{DeadzoneSlider.Value:P0}";

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

    private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLiveAxisMeter();

    private void UpdateAxisUi()
    {
        if (AxisDetectedPanel is null) return;
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

            for (var i = 0; i < state.Buttons.Length; i++)
            {
                var was = baseline is not null && i < baseline.Buttons.Length && baseline.Buttons[i];
                if (state.Buttons[i] && !was)
                {
                    AcceptCapture(new SourceRef { DeviceId = id, Button = i });
                    return;
                }
            }

            if (state.Hat >= 0 && (baseline is null || baseline.Hat != state.Hat))
            {
                AcceptCapture(new SourceRef { DeviceId = id, IsHat = true });
                return;
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
        if (LiveAxisSlider is null || LiveAxisValueText is null)
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

        if (_sources.Count == 0 &&
            restValue is float rest &&
            source.Axis is not null)
        {
            InvertCheck.IsChecked = rest > 0.7f;
        }

        _sources.Add(new SourceRow(CloneSource(source)!, _resolveName));
        SourcesList.SelectedItem = _sources[^1];
        UpdateListeningText();
        UpdateAxisUi();
        UpdateLiveAxisMeter();
        _baseline = _poll().ToDictionary(kv => kv.Key, kv => CloneState(kv.Value));
    }

    private void UpdateListeningText()
    {
        ListeningText.Text = _sources.Count == 0
            ? "Listening for button, hat, or axis…"
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

    private void DeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeadzoneValueText is null) return;
        DeadzoneValueText.Text = $"{DeadzoneSlider.Value:P0}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        CapturedSources = _sources.Select(s => CloneSource(s.Source)!).ToList();
        Invert = InvertCheck.IsChecked == true;
        Deadzone = HasAxisSource ? DeadzoneSlider.Value : 0;
        Applied = true;
        DialogResult = true;
        Close();
    }

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

    private sealed class SourceRow(SourceRef source, Func<string?, string> resolveName)
    {
        public SourceRef Source { get; } = source;
        public string Display
        {
            get
            {
                var dev = resolveName(Source.DeviceId);
                if (Source.Axis is not null)
                    return $"{dev} · {Source.Axis} (axis→btn)";
                if (Source.IsHat) return $"{dev} · hat";
                if (Source.Button is int b) return $"{dev} · button {b}";
                return dev;
            }
        }
    }
}
