using System.Windows;
using System.Windows.Input;

namespace SteeringWheelEmulator.App;

/// <summary>
/// Marks an element so left-clicks are swallowed (e.g. toggle label shows a tooltip
/// but does not toggle the CheckBox).
/// </summary>
public static class MouseSwallow
{
    public static readonly DependencyProperty SwallowLeftClickProperty =
        DependencyProperty.RegisterAttached(
            "SwallowLeftClick",
            typeof(bool),
            typeof(MouseSwallow),
            new PropertyMetadata(false, OnSwallowLeftClickChanged));

    public static void SetSwallowLeftClick(DependencyObject element, bool value) =>
        element.SetValue(SwallowLeftClickProperty, value);

    public static bool GetSwallowLeftClick(DependencyObject element) =>
        (bool)element.GetValue(SwallowLeftClickProperty);

    private static void OnSwallowLeftClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el)
            return;
        el.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if ((bool)e.NewValue)
            el.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;
}
