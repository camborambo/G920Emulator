using System.Windows;
using G920Emulator.Core.Models;

namespace G920Emulator.App;

public partial class FfbEffectBindWindow : Window
{
    private readonly G920Control _minus;
    private readonly G920Control _plus;
    private readonly MappingProfile _profile;
    private readonly Func<string?, string> _resolveName;
    private readonly Func<G920Control, bool> _assign;

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

        TitleText.Text = $"Bind {effectName}";
        RefreshLabels();
    }

    private void AssignMinus_Click(object sender, RoutedEventArgs e) => Assign(_minus);

    private void AssignPlus_Click(object sender, RoutedEventArgs e) => Assign(_plus);

    private void Assign(G920Control target)
    {
        if (!_assign(target))
            return;
        Changed = true;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        MinusBindingText.Text = FormatBinding(_minus);
        PlusBindingText.Text = FormatBinding(_plus);
    }

    private string FormatBinding(G920Control target)
    {
        var binding = _profile.GetOrCreate(target);
        var sources = binding.EffectiveSources;
        if (sources.Count == 0)
            return "Not bound";
        if (sources.Count == 1)
            return FormatSource(sources[0]);
        return $"{sources.Count} sources";
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
