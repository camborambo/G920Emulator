using System.Windows;
using System.Windows.Media;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

public partial class FfbEffectBindWindow : Window
{
    private readonly G920Control _minus;
    private readonly G920Control _plus;
    private readonly MappingProfile _profile;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<G920Control, bool> _assign;
    private readonly Brush _rowIdle;
    private readonly Brush _rowBound;
    private readonly Brush _borderIdle;
    private readonly Brush _borderBound;

    public bool Changed { get; private set; }

    public FfbEffectBindWindow(
        string effectName,
        G920Control minus,
        G920Control plus,
        MappingProfile profile,
        Func<string?, string> resolveName,
        Func<G920Control, bool> assign)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _minus = minus;
        _plus = plus;
        _profile = profile;
        _resolveName = resolveName;
        _assign = assign;
        _rowIdle = (Brush)FindResource("BgPanel");
        _rowBound = new SolidColorBrush(Color.FromRgb(0x16, 0x2A, 0x22));
        _borderIdle = (Brush)FindResource("BorderSubtle");
        _borderBound = (Brush)FindResource("AccentPrimary");

        TitleText.Text = $"Bind {effectName}";
        RefreshLabels();
    }

    private void AssignMinus_Click(object sender, RoutedEventArgs e) => Assign(_minus);

    private void AssignPlus_Click(object sender, RoutedEventArgs e) => Assign(_plus);

    private void ClearMinus_Click(object sender, RoutedEventArgs e) => Clear(_minus);

    private void ClearPlus_Click(object sender, RoutedEventArgs e) => Clear(_plus);

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
    }

    private void ApplyRow(
        G920Control target,
        System.Windows.Controls.TextBlock bindingText,
        System.Windows.Controls.TextBlock badge,
        System.Windows.Controls.Button clear,
        System.Windows.Controls.Border row)
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
            var mode = source.AxisFromCenter ? "axis→btn·center" : "axis→btn";
            return $"{dev} · {source.Axis} ({mode})";
        }
        if (source.IsHat) return $"{dev} · hat";
        if (source.Button is int b) return $"{dev} · button {b}";
        return dev;
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
