using System.ComponentModel;
using System.Windows;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

/// <summary>Standard fixed G920 layout row in the bindings list.</summary>
public sealed class StandardBindingRow : INotifyPropertyChanged
{
    private readonly Binding _binding;
    private readonly Func<string?, string> _nameResolver;
    private readonly Func<int> _gearReverseButton;

    public StandardBindingRow(
        G920Control target,
        Binding binding,
        Func<string?, string> nameResolver,
        Func<int> gearReverseButton)
    {
        Target = target;
        _binding = binding;
        _nameResolver = nameResolver;
        _gearReverseButton = gearReverseButton;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public G920Control Target { get; }
    public string TargetName => G920ControlInfo.DisplayName(Target);

    private bool IsAxisTarget => G920ControlInfo.IsAxis(Target);
    private bool HasAxisSource => _binding.EffectiveSources.Any(s => !string.IsNullOrEmpty(s.Axis));
    private bool ShowsActivateOnAxis => !IsAxisTarget && HasAxisSource;

    public Visibility AxisRangeVisibility =>
        IsAxisTarget && HasAxisSource ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActivateOnAxisVisibility =>
        ShowsActivateOnAxis ? Visibility.Visible : Visibility.Collapsed;

    public string AxisRangeToolTip =>
        "Usable axis throw (after Invert). Below Start → rest; above End → full.";

    public string ActivateOnAxisToolTip =>
        "Button presses when the axis (after Invert) reaches this %.";

    public double AxisStart
    {
        get => _binding.AxisStart;
        set
        {
            var clamped = Math.Clamp(value, 0, 0.95);
            if (clamped > _binding.AxisEnd - 0.05)
                clamped = Math.Max(0, _binding.AxisEnd - 0.05);
            if (Math.Abs(_binding.AxisStart - clamped) < 0.0001) return;
            _binding.AxisStart = clamped;
            if (Target != G920Control.Steering)
                _binding.Deadzone = clamped;
            NotifyAxisRangeChanged();
        }
    }

    public double AxisEnd
    {
        get => _binding.AxisEnd <= 0 ? 1 : _binding.AxisEnd;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 1);
            if (clamped < _binding.AxisStart + 0.05)
                clamped = Math.Min(1, _binding.AxisStart + 0.05);
            if (Math.Abs(_binding.AxisEnd - clamped) < 0.0001) return;
            _binding.AxisEnd = clamped;
            NotifyAxisRangeChanged();
        }
    }

    public string AxisRangeText => $"{AxisStart:P0}–{AxisEnd:P0}";

    public double ActivateOnAxis
    {
        get => _binding.Deadzone > 0.001 ? _binding.Deadzone : 0.5;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.95);
            if (Math.Abs(_binding.Deadzone - clamped) < 0.0001) return;
            _binding.Deadzone = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivateOnAxis)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivateOnAxisText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceText)));
        }
    }

    public string ActivateOnAxisText => $"{ActivateOnAxis:P0}";

    private void NotifyAxisRangeChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AxisStart)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AxisEnd)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AxisRangeText)));
        // Only when a source is bound does SourceText embed the range string.
        if (_binding.EffectiveSources.Count > 0)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceText)));
    }

    public string SourceText
    {
        get
        {
            var outBtn = Target == G920Control.GearR
                ? $" → btn {Math.Clamp(_gearReverseButton() <= 0 ? 19 : _gearReverseButton(), 1, 19)}"
                : "";
            var sources = _binding.EffectiveSources;
            if (sources.Count == 0) return "(click to bind)" + outBtn;
            var inv = _binding.Invert ? " · inverted" : "";
            var detail = IsAxisTarget
                ? $" · {AxisRangeText}"
                : ShowsActivateOnAxis
                    ? $" · activate {ActivateOnAxisText}"
                    : "";
            if (sources.Count == 1)
                return FormatSource(sources[0], _nameResolver, IsAxisTarget) + detail + inv + outBtn;
            var parts = sources.Select(s => FormatSource(s, _nameResolver, IsAxisTarget));
            return string.Join(" + ", parts) + detail + inv + outBtn;
        }
    }

    private static string FormatSource(
        SourceRef source,
        Func<string?, string> nameResolver,
        bool axisTarget)
    {
        var dev = nameResolver(source.DeviceId);
        if (source.Axis is not null)
        {
            return axisTarget
                ? $"{dev} · {source.Axis} (axis)"
                : $"{dev} · {source.Axis} (axis→btn)";
        }
        if (source.IsHat) return $"{dev} · hat";
        if (source.Button is int b) return $"{dev} · button {b}";
        return dev;
    }
}

/// <summary>Named custom / FN binding summary row (edit via Binding Wizard).</summary>
public sealed class CustomBindingRow
{
    private readonly Func<string?, string> _nameResolver;

    public CustomBindingRow(CustomBinding binding, Func<string?, string> nameResolver)
    {
        Binding = binding;
        _nameResolver = nameResolver;
    }

    public CustomBinding Binding { get; }

    public string Title => Binding.Name;

    public string Summary
    {
        get
        {
            if (Binding.IsFnModifier)
            {
                var fnSrc = Binding.Sources;
                if (fnSrc.Count == 0) return "FN modifier (hold) · (no source)";
                var fn = fnSrc.Count == 1
                    ? FormatSource(fnSrc[0], _nameResolver)
                    : $"{fnSrc.Count} sources";
                return "FN modifier (hold) · " + fn;
            }

            var mode = Binding.Toggle ? "Toggle" : "Hold";
            var role = $"{mode} {G920ControlInfo.DisplayName(Binding.Target)}";
            var sources = Binding.Sources;
            if (sources.Count == 0)
                return role + " · (no button)";
            var src = sources.Count == 1
                ? FormatSource(sources[0], _nameResolver)
                : $"{sources.Count} sources";
            var activate = sources.Any(s => s.Axis is not null)
                ? $" · activate {(Binding.Deadzone > 0.001 ? Binding.Deadzone : 0.5):P0}"
                : "";
            var fnPart = Binding.FnSources is { Count: > 0 }
                ? " · FN " + (Binding.FnSources.Count == 1
                    ? FormatSource(Binding.FnSources[0], _nameResolver)
                    : $"{Binding.FnSources.Count} keys")
                : "";
            return role + " · " + src + activate + fnPart;
        }
    }

    private static string FormatSource(SourceRef source, Func<string?, string> nameResolver)
    {
        var dev = nameResolver(source.DeviceId);
        if (source.Axis is not null)
            return $"{dev} · {source.Axis} (axis→btn)";
        if (source.IsHat) return $"{dev} · hat";
        if (source.Button is int b) return $"{dev} · button {b}";
        return dev;
    }
}
