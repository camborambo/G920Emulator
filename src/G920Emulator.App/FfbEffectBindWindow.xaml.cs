using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

public partial class FfbEffectBindWindow : Window
{
    private readonly string _sliderId;
    private readonly G920Control _minus;
    private readonly G920Control _plus;
    private readonly G920Control _default;
    private readonly MappingProfile _profile;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<G920Control, bool> _assign;
    private readonly Func<double, string> _formatValue;
    private readonly DefaultEditKind _editKind;
    private readonly Brush _rowIdle;
    private readonly Brush _rowBound;
    private readonly Brush _borderIdle;
    private readonly Brush _borderBound;
    private bool _uiBusy;
    private TextBox? _valueEditBox;

    public bool Changed { get; private set; }
    public double DefaultValue { get; private set; }

    public FfbEffectBindWindow(
        string effectName,
        string sliderId,
        G920Control minus,
        G920Control plus,
        G920Control defaultControl,
        MappingProfile profile,
        double min,
        double max,
        double defaultValue,
        double tickFrequency,
        Func<double, string> formatValue,
        Func<string?, string> resolveName,
        Func<G920Control, bool> assign)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _sliderId = sliderId;
        _minus = minus;
        _plus = plus;
        _default = defaultControl;
        _profile = profile;
        _resolveName = resolveName;
        _assign = assign;
        _formatValue = formatValue;
        _editKind = EditKindFor(sliderId);
        _rowIdle = (Brush)FindResource("BgPanel");
        _rowBound = new SolidColorBrush(Color.FromRgb(0x16, 0x2A, 0x22));
        _borderIdle = (Brush)FindResource("BorderSubtle");
        _borderBound = (Brush)FindResource("AccentPrimary");

        TitleText.Text = $"Bind {effectName}";
        _uiBusy = true;
        DefaultValueSlider.Minimum = min;
        DefaultValueSlider.Maximum = max;
        DefaultValueSlider.TickFrequency = tickFrequency > 0 ? tickFrequency : 0.01;
        DefaultValue = Math.Clamp(defaultValue, min, max);
        DefaultValueSlider.Value = DefaultValue;
        DefaultValueText.Text = _formatValue(DefaultValue);
        _uiBusy = false;
        RefreshLabels();
    }

    private void AssignMinus_Click(object sender, RoutedEventArgs e) => Assign(_minus);

    private void AssignPlus_Click(object sender, RoutedEventArgs e) => Assign(_plus);

    private void AssignDefault_Click(object sender, RoutedEventArgs e) => Assign(_default);

    private void ClearMinus_Click(object sender, RoutedEventArgs e) => Clear(_minus);

    private void ClearPlus_Click(object sender, RoutedEventArgs e) => Clear(_plus);

    private void ClearDefault_Click(object sender, RoutedEventArgs e) => Clear(_default);

    private void DefaultValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uiBusy || !IsLoaded)
            return;
        DefaultValue = e.NewValue;
        if (_valueEditBox is null)
            DefaultValueText.Text = _formatValue(DefaultValue);
        Changed = true;
    }

    private void DefaultValueText_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginValueEdit();
    }

    private void BeginValueEdit()
    {
        CancelValueEdit(save: false);
        if (DefaultValueText.Parent is not Panel panel)
            return;

        var edit = new TextBox
        {
            Width = Math.Max(56, DefaultValueText.Width),
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = DefaultValueText.FontSize,
            Text = FormatEditSeed(DefaultValueSlider.Value, _editKind),
            Margin = DefaultValueText.Margin,
        };
        DockPanel.SetDock(edit, Dock.Right);

        var index = panel.Children.IndexOf(DefaultValueText);
        panel.Children.Remove(DefaultValueText);
        if (index < 0) panel.Children.Add(edit);
        else panel.Children.Insert(index, edit);

        _valueEditBox = edit;
        edit.KeyDown += ValueEdit_KeyDown;
        edit.LostKeyboardFocus += ValueEdit_LostFocus;
        edit.Focus();
        edit.SelectAll();
    }

    private void ValueEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitValueEdit(save: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelValueEdit(save: false);
        }
    }

    private void ValueEdit_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitValueEdit(save: true);

    private void CommitValueEdit(bool save)
    {
        var edit = _valueEditBox;
        if (edit is null)
            return;

        _valueEditBox = null;
        if (save &&
            TryParseEdit(edit.Text, _editKind, DefaultValueSlider.Minimum, DefaultValueSlider.Maximum, out var value))
        {
            _uiBusy = true;
            DefaultValueSlider.Value = value;
            DefaultValue = value;
            _uiBusy = false;
            Changed = true;
        }

        EndValueEdit(edit);
        DefaultValueText.Text = _formatValue(DefaultValueSlider.Value);
    }

    private void CancelValueEdit(bool save)
    {
        if (_valueEditBox is null)
            return;
        if (save)
            CommitValueEdit(save: true);
        else
        {
            var edit = _valueEditBox;
            _valueEditBox = null;
            EndValueEdit(edit!);
            DefaultValueText.Text = _formatValue(DefaultValueSlider.Value);
        }
    }

    private void EndValueEdit(TextBox edit)
    {
        edit.KeyDown -= ValueEdit_KeyDown;
        edit.LostKeyboardFocus -= ValueEdit_LostFocus;
        if (edit.Parent is not Panel panel)
            return;
        var index = panel.Children.IndexOf(edit);
        panel.Children.Remove(edit);
        if (index < 0) panel.Children.Add(DefaultValueText);
        else panel.Children.Insert(index, DefaultValueText);
    }

    private static DefaultEditKind EditKindFor(string sliderId) => sliderId switch
    {
        "Master" or "Constant" or "Spring" or "Damper" or "Friction" or "Inertia"
            or "Periodic" or "Ramp" or "Custom" or "PeakSoft" or "Spike"
            or "CenterStrength" or "CenterRange" or "DampVel" or "DampDead" => DefaultEditKind.Percent,
        "SoftStart" or "Smoothing" or "Slew" or "Epsilon" => DefaultEditKind.Integer,
        "Deadband" or "CenterDeadzone" => DefaultEditKind.Decimal,
        _ => DefaultEditKind.Decimal,
    };

    private static string FormatEditSeed(double value, DefaultEditKind kind) => kind switch
    {
        DefaultEditKind.Percent => $"{value * 100:0.##}",
        DefaultEditKind.Integer => $"{value:0}",
        _ => value.ToString("0.###", CultureInfo.InvariantCulture),
    };

    private static bool TryParseEdit(string text, DefaultEditKind kind, double min, double max, out double value)
    {
        value = 0;
        var raw = text.Trim();
        if (raw.Length == 0)
            return false;
        if (raw.EndsWith('%'))
            raw = raw[..^1].Trim();
        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^2].Trim();
        if (raw.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^2].Trim();

        var hadPercent = text.Trim().EndsWith('%');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            return false;

        if (kind == DefaultEditKind.Percent && (hadPercent || parsed > max + 0.0001))
            parsed /= 100.0;

        value = Math.Clamp(parsed, min, max);
        return true;
    }

    private void Assign(G920Control target)
    {
        if (!_assign(target))
            return;
        Changed = true;
        RefreshLabels();
    }

    private void Clear(G920Control target)
    {
        var binding = _profile.GetOrCreate(target);
        if (binding.EffectiveSources.Count == 0)
            return;
        binding.ClearSources();
        Changed = true;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        ApplyRow(_minus, MinusBindingText, MinusBoundBadge, ClearMinus, MinusRow);
        ApplyRow(_plus, PlusBindingText, PlusBoundBadge, ClearPlus, PlusRow);
        ApplyRow(_default, DefaultBindingText, DefaultBoundBadge, ClearDefault, DefaultRow);
    }

    private void ApplyRow(
        G920Control target,
        TextBlock bindingText,
        TextBlock badge,
        Button clear,
        Border row)
    {
        var sources = _profile.GetOrCreate(target).EffectiveSources;
        var bound = sources.Count > 0;
        bindingText.Text = bound
            ? sources.Count == 1
                ? FormatSource(sources[0])
                : $"{sources.Count} sources"
            : "Not bound";
        bindingText.Opacity = bound ? 1 : 0.55;
        badge.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
        clear.IsEnabled = bound;
        row.Background = bound ? _rowBound : _rowIdle;
        row.BorderBrush = bound ? _borderBound : _borderIdle;
    }

    private string FormatSource(SourceRef source)
    {
        var dev = _resolveName(source.DeviceId);
        if (source.Axis is not null)
        {
            return $"{dev} · {source.Axis} (axis→btn)";
        }
        if (source.IsHat) return $"{dev} · hat";
        if (source.Button is int b) return $"{dev} · button {b}";
        return dev;
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        CommitValueEdit(save: true);
        _profile.SetFfbBindDefault(_sliderId, DefaultValue);
        DialogResult = true;
        Close();
    }

    private enum DefaultEditKind
    {
        Percent,
        Integer,
        Decimal,
    }
}
