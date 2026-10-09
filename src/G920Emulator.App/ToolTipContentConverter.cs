using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace G920Emulator.App;

/// <summary>
/// Copies tooltip text from a templated parent without sharing a <see cref="ToolTip"/> instance
/// (WPF breaks when the same ToolTip object is assigned to two elements).
/// </summary>
public sealed class ToolTipContentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || value == DependencyProperty.UnsetValue)
            return null;
        if (value is string s)
            return string.IsNullOrWhiteSpace(s) ? null : s;
        if (value is ToolTip tip)
        {
            if (tip.Content is string cs)
                return string.IsNullOrWhiteSpace(cs) ? null : cs;
            return tip.Content;
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
