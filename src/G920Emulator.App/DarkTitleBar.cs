using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace G920Emulator.App;

/// <summary>Match every window caption to the app canvas (#121418).</summary>
internal static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyHandle(window);
        window.Loaded += (_, _) => ApplyHandle(window);
    }

    private static void ApplyHandle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        TryApply(hwnd);
        HideCaptionIcon(hwnd);
    }

    public static void TryApply(nint hwnd)
    {
        if (hwnd == 0) return;
        try
        {
            const int useImmersiveDarkMode = 20;
            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, useImmersiveDarkMode, ref dark, sizeof(int));

            // DWMWA_CAPTION_COLOR = 35. COLORREF is 0x00BBGGRR.
            const int captionColor = 35;
            int colorRef = 0x00181412; // #121418
            _ = DwmSetWindowAttribute(hwnd, captionColor, ref colorRef, sizeof(int));

            const int borderColor = 34;
            int borderRef = 0x0040312A; // #2A3140
            _ = DwmSetWindowAttribute(hwnd, borderColor, ref borderRef, sizeof(int));

            const int textColor = 36;
            int textRef = 0x00EDEAE8; // #E8EAED
            _ = DwmSetWindowAttribute(hwnd, textColor, ref textRef, sizeof(int));
        }
        catch
        {
            /* older Windows / DWM unavailable */
        }
    }

    /// <summary>Drop the caption icon; the in-window header already brands the app. Taskbar still uses Window.Icon.</summary>
    private static void HideCaptionIcon(nint hwnd)
    {
        if (hwnd == 0) return;
        try
        {
            const int wmSetIcon = 0x0080;
            const int iconSmall = 0;
            SendMessage(hwnd, wmSetIcon, iconSmall, 0);
        }
        catch
        {
            /* ignore */
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
}
