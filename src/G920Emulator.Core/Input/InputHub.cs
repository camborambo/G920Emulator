using System.Diagnostics;
using System.Runtime.InteropServices;
using G920Emulator.Core.Models;
using SharpDX.DirectInput;

namespace G920Emulator.Core.Input;

public sealed class InputHub : IDisposable
{
    private readonly DirectInput _directInput = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, JoystickSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IReadOnlyList<InputDeviceInfo> RefreshDevices()
    {
        lock (_gate)
        {
            var devices = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            var result = new List<InputDeviceInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instance in devices)
            {
                // Never offer our own virtual G920 as an input source (feedback loop).
                if (IsEmulatedG920(instance))
                    continue;

                var id = instance.InstanceGuid.ToString("D");
                seen.Add(id);

                if (!_sessions.TryGetValue(id, out var session))
                {
                    try
                    {
                        session = JoystickSession.Open(_directInput, instance);
                        _sessions[id] = session;
                    }
                    catch
                    {
                        continue;
                    }
                }

                result.Add(session.Info);
            }

            foreach (var orphan in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _sessions[orphan].Dispose();
                _sessions.Remove(orphan);
            }

            return result
                .OrderBy(d => d.Kind)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyDictionary<string, DeviceState> Poll()
    {
        lock (_gate)
        {
            var states = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, session) in _sessions)
            {
                try
                {
                    states[id] = session.Poll();
                }
                catch
                {
                    // Device may have been unplugged mid-frame.
                }
            }
            return states;
        }
    }

    public InputDeviceInfo? FindDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;
        lock (_gate)
            return _sessions.TryGetValue(deviceId, out var s) ? s.Info : null;
    }

    /// <summary>
    /// Returns the live DirectInput joystick for a device so FFB can share the same acquire.
    /// </summary>
    public bool TryGetJoystick(string? deviceId, out Joystick joystick, out string deviceName)
    {
        joystick = null!;
        deviceName = "";
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out var session))
            {
                // Ensure sessions exist.
                RefreshDevices();
                if (!_sessions.TryGetValue(deviceId, out session))
                    return false;
            }

            joystick = session.Joystick;
            deviceName = session.Info.Name;
            return true;
        }
    }

    /// <summary>
    /// After exclusive FFB detach, restore non-exclusive background acquire for input/binding.
    /// </summary>
    public void RestoreNonExclusive(string? deviceId, IntPtr hwnd)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out var session))
                return;
            session.RestoreNonExclusive(hwnd);
        }
    }

    private static bool IsEmulatedG920(DeviceInstance instance)
    {
        if (G920Identity.IsVirtualG920Product(instance.ProductGuid))
            return true;
        // Fallback if ProductGuid packing differs across DI versions.
        return G920Identity.LooksLikeVirtualG920Name(instance.ProductName) ||
               G920Identity.LooksLikeVirtualG920Name(instance.InstanceName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
                session.Dispose();
            _sessions.Clear();
            _directInput.Dispose();
        }
    }

    private sealed class JoystickSession : IDisposable
    {
        private readonly Joystick _joystick;
        public InputDeviceInfo Info { get; }
        public Joystick Joystick => _joystick;

        private JoystickSession(Joystick joystick, InputDeviceInfo info)
        {
            _joystick = joystick;
            Info = info;
        }

        public static JoystickSession Open(DirectInput di, DeviceInstance instance)
        {
            var joy = new Joystick(di, instance.InstanceGuid);
            joy.Properties.BufferSize = 128;
            // Non-exclusive + background so binding still works when a modal dialog
            // steals focus (exclusive acquire is lost as soon as the main window blurs).
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                hwnd = GetDesktopWindow();
            joy.SetCooperativeLevel(hwnd, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
            joy.Acquire();

            var caps = joy.Capabilities;
            var axisCount = Math.Max(caps.AxeCount, 0);
            var buttonCount = caps.ButtonCount;
            var hasHat = caps.PovCount > 0;
            var ffb = caps.Flags.HasFlag(DeviceFlags.ForceFeedback);

            var info = new InputDeviceInfo
            {
                Id = instance.InstanceGuid.ToString("D"),
                Name = instance.InstanceName,
                ProductName = instance.ProductName,
                SupportsForceFeedback = ffb,
                AxisCount = axisCount,
                ButtonCount = buttonCount,
                HasHat = hasHat,
                Kind = Classify(instance.InstanceName, axisCount, buttonCount, ffb),
            };

            return new JoystickSession(joy, info);
        }

        public void RestoreNonExclusive(IntPtr hwnd)
        {
            try { _joystick.Unacquire(); } catch { /* ignore */ }
            if (hwnd == IntPtr.Zero)
                hwnd = GetDesktopWindow();
            try
            {
                _joystick.SetCooperativeLevel(hwnd, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
                _joystick.Acquire();
            }
            catch
            {
                // Best-effort; next Poll() will try Acquire again.
            }
        }

        public DeviceState Poll()
        {
            JoystickState state;
            try
            {
                _joystick.Poll();
                state = _joystick.GetCurrentState();
            }
            catch
            {
                try
                {
                    _joystick.Acquire();
                    _joystick.Poll();
                    state = _joystick.GetCurrentState();
                }
                catch
                {
                    return new DeviceState
                    {
                        DeviceId = Info.Id,
                        Axes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
                        Buttons = new bool[Info.ButtonCount],
                        Hat = -1,
                    };
                }
            }

            var axes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["X"] = NormalizeAxis(state.X),
                ["Y"] = NormalizeAxis(state.Y),
                ["Z"] = NormalizeAxis(state.Z),
                ["Rx"] = NormalizeAxis(state.RotationX),
                ["Ry"] = NormalizeAxis(state.RotationY),
                ["Rz"] = NormalizeAxis(state.RotationZ),
                ["Slider0"] = state.Sliders.Length > 0 ? NormalizeAxis(state.Sliders[0]) : 0f,
                ["Slider1"] = state.Sliders.Length > 1 ? NormalizeAxis(state.Sliders[1]) : 0f,
            };

            var hat = -1;
            if (state.PointOfViewControllers.Length > 0)
            {
                var pov = state.PointOfViewControllers[0];
                if (pov >= 0)
                    hat = pov / 4500; // 0..7
            }

            return new DeviceState
            {
                DeviceId = Info.Id,
                Axes = axes,
                Buttons = state.Buttons.Take(Info.ButtonCount).ToArray(),
                Hat = hat,
            };
        }

        private static float NormalizeAxis(int value)
        {
            // DirectInput typically reports 0..65535 for absolute axes.
            if (value < 0) value = 0;
            if (value > 65535) value = 65535;
            return value / 65535f;
        }

        private static DeviceKind Classify(string name, int axes, int buttons, bool ffb)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("shifter") || n.Contains("gearbox") || n.Contains("h-pattern"))
                return DeviceKind.Shifter;
            if (n.Contains("pedal"))
                return DeviceKind.Pedals;
            if (ffb || n.Contains("wheel") || n.Contains("driving force") || n.Contains("g29") || n.Contains("g920") || n.Contains("g923") ||
                n.Contains("t300") || n.Contains("fanatec") || n.Contains("moza") || n.Contains("simucube") || n.Contains("simagic") ||
                n.Contains("thrustmaster") || n.Contains("asetek") || n.Contains("cammus") || n.Contains("pxn"))
                return DeviceKind.Wheelbase;
            if (n.Contains("pad") || n.Contains("controller") || n.Contains("xbox") || n.Contains("dualshock") || n.Contains("dualsense"))
                return DeviceKind.Gamepad;
            if (axes >= 3 && buttons <= 8)
                return DeviceKind.Pedals;
            if (buttons >= 6 && axes <= 2)
                return DeviceKind.Shifter;
            if (axes >= 2)
                return DeviceKind.MultiAxis;
            return DeviceKind.Unknown;
        }

        public void Dispose()
        {
            try { _joystick.Unacquire(); } catch { /* ignore */ }
            _joystick.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
