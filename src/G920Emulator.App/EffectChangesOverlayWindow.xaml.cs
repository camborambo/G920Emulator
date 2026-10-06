using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace G920Emulator.App;

public partial class EffectChangesOverlayWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    public EffectChangesOverlayWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => BeginFadeOut();
        Loaded += (_, _) => ApplyClickThrough();
        SourceInitialized += (_, _) => ApplyClickThrough();
    }

    public void ShowChange(string category, string effectName, string value)
    {
        BeginAnimation(OpacityProperty, null);
        var hasCategory = !string.IsNullOrWhiteSpace(category)
            && !string.Equals(category, effectName, StringComparison.OrdinalIgnoreCase);
        CategoryText.Text = hasCategory ? category.ToUpperInvariant() : "";
        CategoryText.Visibility = hasCategory ? Visibility.Visible : Visibility.Collapsed;
        NameText.Text = effectName;
        PercentText.Text = value;
        Opacity = 0.92;
        if (!IsVisible)
            Show();
        UpdateLayout();
        PlaceTopCenter();
        Topmost = false;
        Topmost = true;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Hide();
    }

    private void BeginFadeOut()
    {
        _hideTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(280))
        {
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            Opacity = 0;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void PlaceTopCenter()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + 36;
    }

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        const int gwlExStyle = -20;
        const int wsExTransparent = 0x00000020;
        const int wsExNoActivate = 0x08000000;
        const int wsExToolWindow = 0x00000080;
        var style = GetWindowLongPtr(hwnd, gwlExStyle);
        SetWindowLongPtr(hwnd, gwlExStyle, style | (nint)(wsExTransparent | wsExNoActivate | wsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
