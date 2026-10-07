using System.Windows;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

public partial class CustomBindingWindow : Window
{
    private readonly CustomBinding _binding;
    private readonly Func<IReadOnlyDictionary<string, DeviceState>> _poll;
    private readonly Action _refreshDevices;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<string?, string?> _resolveProductId;
    private readonly List<TargetOption> _targets;

    private List<SourceRef> _buttonSources = [];
    private List<SourceRef> _fnSources = [];
    private bool _buttonInvert;
    private bool _fnInvert;
    private double _buttonDeadzone;
    private double _fnDeadzone;

    public bool Applied { get; private set; }

    public CustomBindingWindow(
        CustomBinding binding,
        Func<IReadOnlyDictionary<string, DeviceState>> poll,
        Action refreshDevices,
        Func<string?, string> resolveName,
        Func<string?, string?>? resolveProductId = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _binding = binding;
        _poll = poll;
        _refreshDevices = refreshDevices;
        _resolveName = resolveName;
        _resolveProductId = resolveProductId ?? (_ => null);

        NameBox.Text = binding.Name;
        ToggleCheck.IsChecked = binding.Toggle;
        _buttonSources = binding.Sources.Select(CloneSource).Where(s => s is not null).Cast<SourceRef>().ToList();
        _fnSources = (binding.FnSources ?? []).Select(CloneSource).Where(s => s is not null).Cast<SourceRef>().ToList();
        _buttonInvert = binding.Invert;
        _fnInvert = binding.FnInvert;
        _buttonDeadzone = binding.Deadzone;
        _fnDeadzone = binding.FnDeadzone;

        _targets = G920ControlInfo.CustomBindingTargets
            .Select(t => new TargetOption(t, G920ControlInfo.DisplayName(t)))
            .ToList();
        TargetCombo.ItemsSource = _targets;
        var target = G920ControlInfo.IsCustomBindingTarget(binding.Target)
            ? binding.Target
            : G920Control.ButtonA;
        TargetCombo.SelectedItem = _targets.First(t => t.Target == target);

        RefreshSummaries();
    }

    private void BindFn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CaptureSourcesWindow(
            "Bind FN",
            "Press a button or move an axis for the optional FN hold key. While this key is held, Bind Button can activate the G920 target.",
            _fnSources,
            _fnInvert,
            _fnDeadzone,
            _poll,
            _refreshDevices,
            _resolveName,
            _resolveProductId)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true || !dlg.Applied)
            return;

        _fnSources = dlg.CapturedSources.Select(CloneSource).Where(s => s is not null).Cast<SourceRef>().ToList();
        _fnInvert = dlg.Invert;
        _fnDeadzone = dlg.Deadzone;
        RefreshSummaries();
    }

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CaptureSourcesWindow(
            "Bind Button",
            "Press a button or move an axis that drives the G920 target. If FN is bound, FN must be held for this to fire.",
            _buttonSources,
            _buttonInvert,
            _buttonDeadzone,
            _poll,
            _refreshDevices,
            _resolveName,
            _resolveProductId)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true || !dlg.Applied)
            return;

        _buttonSources = dlg.CapturedSources.Select(CloneSource).Where(s => s is not null).Cast<SourceRef>().ToList();
        _buttonInvert = dlg.Invert;
        _buttonDeadzone = dlg.Deadzone;
        RefreshSummaries();
    }

    private void ClearFn_Click(object sender, RoutedEventArgs e)
    {
        _fnSources = [];
        _fnInvert = false;
        _fnDeadzone = 0;
        RefreshSummaries();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _buttonSources = [];
        _buttonInvert = false;
        _buttonDeadzone = 0;
        RefreshSummaries();
    }

    private void RefreshSummaries()
    {
        FnSummaryText.Text = _fnSources.Count == 0
            ? "(optional — not bound)"
            : FormatSources(_fnSources, _fnInvert, _fnDeadzone);
        ButtonSummaryText.Text = _buttonSources.Count == 0
            ? "(not bound)"
            : FormatSources(_buttonSources, _buttonInvert, _buttonDeadzone);
    }

    private string FormatSources(IReadOnlyList<SourceRef> sources, bool invert, double deadzone)
    {
        var parts = sources.Select(s =>
        {
            var dev = _resolveName(s.DeviceId);
            if (s.Axis is not null) return $"{dev} · {s.Axis}";
            if (s.IsHat) return $"{dev} · hat";
            if (s.Button is int b) return $"{dev} · button {b}";
            return dev;
        });
        var text = string.Join(" + ", parts);
        if (invert) text += " · inverted";
        if (sources.Any(s => s.Axis is not null))
            text += $" · activate {(deadzone > 0.001 ? deadzone : 0.5):P0}";
        return text;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a name for this binding.", "Custom binding",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_buttonSources.Count == 0)
        {
            MessageBox.Show(this, "Use Bind Button to capture at least one input.", "Custom binding",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TargetCombo.SelectedItem is not TargetOption opt)
        {
            MessageBox.Show(this, "Choose a G920 target button.", "Custom binding",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _binding.Name = name;
        _binding.IsFnModifier = false;
        _binding.Toggle = ToggleCheck.IsChecked == true;
        _binding.Target = opt.Target;
        _binding.Invert = _buttonInvert;
        _binding.FnInvert = _fnInvert;
        _binding.Deadzone = _buttonDeadzone;
        _binding.FnDeadzone = _fnDeadzone;
        _binding.AxisStart = 0;
        _binding.AxisEnd = 1;
        _binding.FnAxisStart = 0;
        _binding.FnAxisEnd = 1;
        _binding.ClearSources();
        foreach (var source in _buttonSources)
            _binding.Sources.Add(CloneSource(source)!);
        _binding.FnSources = _fnSources.Select(s => CloneSource(s)!).ToList();
        _binding.RequiresFn = _binding.FnSources.Count > 0;
        _binding.UseAxisAsButton = _binding.Sources.Any(s => s.Axis is not null) ||
                                   _binding.FnSources.Any(s => s.Axis is not null);
        _binding.Normalize();

        Applied = true;
        DialogResult = true;
        Close();
    }

    private sealed record TargetOption(G920Control Target, string Label);

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
}
