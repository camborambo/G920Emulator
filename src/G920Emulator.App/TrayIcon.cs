using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace G920Emulator.App;

/// <summary>Notification-area icon via Shell_NotifyIcon (no WinForms).</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const int WmAppTray = 0x8000 + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    private readonly NotifyIconData _data;
    private readonly HwndSource _source;
    private bool _visible;
    private bool _added;
    private bool _disposed;

    public event Action? Clicked;
    public event Action? RightClicked;

    public TrayIcon(Window window, string tip)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("No HWND for tray icon.");
        _source.AddHook(WndProc);

        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTray,
            hIcon = LoadAppIcon(),
            szTip = tip,
        };
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_disposed || _visible == value)
                return;
            _visible = value;
            if (value)
            {
                var ok = Shell_NotifyIcon(_added ? NimModify : NimAdd, _data);
                if (ok) _added = true;
            }
            else if (_added)
            {
                Shell_NotifyIcon(NimDelete, _data);
                _added = false;
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmAppTray)
            return IntPtr.Zero;

        var notify = lParam.ToInt32();
        if (notify is WmLButtonUp)
        {
            Clicked?.Invoke();
            handled = true;
        }
        else if (notify is WmRButtonUp)
        {
            SetForegroundWindow(hwnd);
            RightClicked?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                ExtractIconEx(exe, 0, out var large, out var small, 1);
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero && large != small)
                        DestroyIcon(large);
                    return small;
                }
                if (large != IntPtr.Zero)
                    return large;
            }
        }
        catch { /* fall through */ }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Visible = false;
        try { _source.RemoveHook(WndProc); } catch { /* ignore */ }
        if (_data.hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_data.hIcon); } catch { /* ignore */ }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip = "";
    }
}
