using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace G920Emulator.App.Controls;

/// <summary>Single track with lower/upper thumbs (min-max range).</summary>
public partial class DualRangeSlider : UserControl
{
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(DualRangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(DualRangeSlider),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangeChanged));

    public static readonly DependencyProperty LowerValueProperty =
        DependencyProperty.Register(nameof(LowerValue), typeof(double), typeof(DualRangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty UpperValueProperty =
        DependencyProperty.Register(nameof(UpperValue), typeof(double), typeof(DualRangeSlider),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty TickFrequencyProperty =
        DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(DualRangeSlider),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty IsSnapToTickEnabledProperty =
        DependencyProperty.Register(nameof(IsSnapToTickEnabled), typeof(bool), typeof(DualRangeSlider),
            new PropertyMetadata(false));

    public event EventHandler<RoutedPropertyChangedEventArgs<double>>? LowerValueChanged;
    public event EventHandler<RoutedPropertyChangedEventArgs<double>>? UpperValueChanged;
    public event EventHandler? RangeChanged;

    private bool _suppress;
    private bool _dragging;

    public DualRangeSlider()
    {
        InitializeComponent();
        SizeChanged += (_, _) => LayoutThumbs();
        Loaded += (_, _) => LayoutThumbs();
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double LowerValue
    {
        get => (double)GetValue(LowerValueProperty);
        set => SetValue(LowerValueProperty, value);
    }

    public double UpperValue
    {
        get => (double)GetValue(UpperValueProperty);
        set => SetValue(UpperValueProperty, value);
    }

    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public bool IsSnapToTickEnabled
    {
        get => (bool)GetValue(IsSnapToTickEnabledProperty);
        set => SetValue(IsSnapToTickEnabledProperty, value);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DualRangeSlider s)
            s.CoerceAndLayout();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DualRangeSlider s || s._suppress)
            return;
        s.CoerceAndLayout();
        if (e.Property == LowerValueProperty)
            s.LowerValueChanged?.Invoke(s, new RoutedPropertyChangedEventArgs<double>((double)e.OldValue, (double)e.NewValue));
        else if (e.Property == UpperValueProperty)
            s.UpperValueChanged?.Invoke(s, new RoutedPropertyChangedEventArgs<double>((double)e.OldValue, (double)e.NewValue));
        if (!s._dragging)
            s.RangeChanged?.Invoke(s, EventArgs.Empty);
    }

    private void Thumb_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LowerThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        MoveThumb(isLower: true, e.HorizontalChange);

    private void UpperThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        MoveThumb(isLower: false, e.HorizontalChange);

    private void MoveThumb(bool isLower, double dx)
    {
        var span = Math.Max(1e-9, Maximum - Minimum);
        var width = Math.Max(1.0, ThumbCanvas.ActualWidth);
        var delta = dx / width * span;
        if (isLower)
            LowerValue = Snap(Math.Clamp(LowerValue + delta, Minimum, UpperValue));
        else
            UpperValue = Snap(Math.Clamp(UpperValue + delta, LowerValue, Maximum));
    }

    private double Snap(double value)
    {
        if (!IsSnapToTickEnabled || TickFrequency <= 0)
            return value;
        var steps = Math.Round((value - Minimum) / TickFrequency);
        return Math.Clamp(Minimum + steps * TickFrequency, Minimum, Maximum);
    }

    private void CoerceAndLayout()
    {
        if (Maximum < Minimum)
            Maximum = Minimum;

        var lower = Math.Clamp(LowerValue, Minimum, Maximum);
        var upper = Math.Clamp(UpperValue, Minimum, Maximum);
        if (lower > upper)
            lower = upper;

        _suppress = true;
        try
        {
            if (Math.Abs(LowerValue - lower) > 1e-9) LowerValue = lower;
            if (Math.Abs(UpperValue - upper) > 1e-9) UpperValue = upper;
        }
        finally
        {
            _suppress = false;
        }

        LayoutThumbs();
    }

    private void LayoutThumbs()
    {
        if (!IsLoaded)
            return;

        var width = ThumbCanvas.ActualWidth;
        if (width <= 0)
            width = Math.Max(0, ActualWidth - 14);
        if (width <= 0)
            return;

        var span = Math.Max(1e-9, Maximum - Minimum);
        var lowerX = (LowerValue - Minimum) / span * width;
        var upperX = (UpperValue - Minimum) / span * width;
        const double thumb = 14;
        const double half = thumb / 2;

        var trackY = Math.Max(0, (ActualHeight - 4) / 2);
        Canvas.SetTop(TrackSelected, trackY);
        Canvas.SetLeft(TrackSelected, Math.Min(lowerX, upperX));
        TrackSelected.Width = Math.Max(0, Math.Abs(upperX - lowerX));

        Canvas.SetLeft(LowerThumb, lowerX - half);
        Canvas.SetTop(LowerThumb, (ActualHeight - thumb) / 2);
        Canvas.SetLeft(UpperThumb, upperX - half);
        Canvas.SetTop(UpperThumb, (ActualHeight - thumb) / 2);
    }
}
