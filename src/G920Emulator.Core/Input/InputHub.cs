using System.Diagnostics;
using System.Runtime.InteropServices;
using G920Emulator.Core.Models;
using SharpDX.DirectInput;

namespace G920Emulator.Core.Input;

/// <summary>One DirectInput game-control device as seen at diagnostics export time.</summary>
public sealed record DiagnosticDeviceRow(
    string InstanceId,
    string ProductId,
    string Name,
    string ProductName,
    bool SupportsForceFeedback,
    int AxisCount,
    int ButtonCount,
    int HatCount,
    bool IsVirtualG920,
    string? OpenError);

public sealed class InputHub : IDisposable
{
    private readonly DirectInput _directInput = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, JoystickSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceState> _lastStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _pinnedFfbDeviceId;
    private bool _disposed;

    /// <summary>
    /// Keep this device's joystick session alive while FFB shares it. Transient Poll
    /// failures during exclusive attach must not Dispose the handle under FfbBridge.
    /// While pinned, <see cref="Poll"/> does not touch that joystick (FfbBridge reads it);
    /// other devices keep polling so pedals/buttons never stall on the FFB base.
    /// </summary>
    public void PinFfbDevice(string? deviceId)
    {
        lock (_gate)
            _pinnedFfbDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    /// <summary>
    /// One non-exclusive poll into the cache before FFB exclusive attach, so the bridge
    /// has a last-known state if rim reads fail briefly after pin.
    /// </summary>
    public void CapturePinnedBaseline(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        // Resolve session under lock, Poll outside - a moving FFB wheel's DI Poll can
        // take tens/hundreds of ms and must not stall other InputHub callers.
        JoystickSession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out session))
                return;
        }

        try
        {
            var state = session.Poll();
            if (session.PollFailed)
                return;
            lock (_gate)
                _lastStates[deviceId] = state;
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// All attached game controllers as DirectInput sees them, including the virtual G920.
    /// For diagnostics only - do not use as a bind source list.
    /// </summary>
    public static IReadOnlyList<DiagnosticDeviceRow> EnumerateAllAttachedForDiagnostics()
    {
        using var di = new DirectInput();
        var rows = new List<DiagnosticDeviceRow>();
        foreach (var instance in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            var virtualG920 = IsEmulatedG920(instance);
            var ffb = false;
            var axes = 0;
            var buttons = 0;
            var hats = 0;
            string? openError = null;
            try
            {
                using var joy = new Joystick(di, instance.InstanceGuid);
                var caps = joy.Capabilities;
                ffb = caps.Flags.HasFlag(DeviceFlags.ForceFeedback);
                axes = Math.Max(caps.AxeCount, 0);
                buttons = caps.ButtonCount;
                hats = caps.PovCount;
            }
            catch (Exception ex)
            {
                openError = ex.Message;
            }

            rows.Add(new DiagnosticDeviceRow(
                instance.InstanceGuid.ToString("D"),
                instance.ProductGuid.ToString("D"),
                instance.InstanceName ?? "",
                instance.ProductName ?? "",
                ffb,
                axes,
                buttons,
                hats,
                virtualG920,
                openError));
        }

        return rows
            .OrderByDescending(r => r.IsVirtualG920)
            .ThenByDescending(r => r.SupportsForceFeedback)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<InputDeviceInfo> RefreshDevices()
    {
        // Enumerate outside the session lock - GetDevices can take 10-50ms+ with many
        // HID devices and must not stall the 500 Hz bridge/Poll path.
        DeviceInstance[] devices;
        try
        {
            devices = _directInput
                .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .ToArray();
        }
        catch
        {
            lock (_gate)
            {
                return _sessions.Values
                    .Select(s => s.Info)
                    .OrderBy(d => d.Kind)
                    .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        lock (_gate)
        {
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
                if (_pinnedFfbDeviceId is not null &&
                    string.Equals(orphan, _pinnedFfbDeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;
                _sessions[orphan].Dispose();
                _sessions.Remove(orphan);
            }

            return result
                .OrderBy(d => d.Kind)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Poll sessions. When <paramref name="includeIds"/> is set, only those devices
    /// (plus any pinned FFB cache) are touched - avoids USB work on unused pads every frame.
    /// </summary>
    public IReadOnlyDictionary<string, DeviceState> Poll(IReadOnlyCollection<string>? includeIds = null)
    {
        var needsRescan = false;
        Dictionary<string, DeviceState> states;
        lock (_gate)
        {
            states = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
            var dead = new List<string>();
            foreach (var (id, session) in _sessions)
            {
                if (includeIds is not null &&
                    !includeIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Never Poll the exclusive FFB joystick here - that can block the whole
                // bridge loop (all bindings freeze). Bridge overlays live axes from FfbBridge.
                if (_pinnedFfbDeviceId is not null &&
                    string.Equals(id, _pinnedFfbDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    if (_lastStates.TryGetValue(id, out var cached))
                        states[id] = cached;
                    continue;
                }

                try
                {
                    var state = session.Poll();
                    states[id] = state;
                    _lastStates[id] = state;
                    if (session.PollFailed)
                        dead.Add(id);
                }
                catch
                {
                    dead.Add(id);
                }
            }

            // Drop dead sessions so RefreshDevices can reopen (possibly new instance GUID).
            // Never dispose the pinned FFB source - Unacquire during attach looks like a
            // failed poll and disposing it freezes input + breaks the shared FFB handle.
            foreach (var id in dead)
            {
                if (_pinnedFfbDeviceId is not null &&
                    string.Equals(id, _pinnedFfbDeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_sessions.Remove(id, out var session))
                    session.Dispose();
                states.Remove(id);
                needsRescan = true;
            }
        }

        if (needsRescan)
        {
            foreach (var _ in RefreshDevices())
            {
                // RefreshDevices already opened sessions; re-poll once for this frame.
            }

            lock (_gate)
            {
                states = new Dictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, session) in _sessions)
                {
                    if (includeIds is not null &&
                        !includeIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                        continue;

                    // Still skip pinned - never block the loop on the exclusive FFB base.
                    if (_pinnedFfbDeviceId is not null &&
                        string.Equals(id, _pinnedFfbDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_lastStates.TryGetValue(id, out var cached))
                            states[id] = cached;
                        continue;
                    }

                    try
                    {
                        var state = session.Poll();
                        states[id] = state;
                        _lastStates[id] = state;
                    }
                    catch { /* ignore */ }
                }
            }
        }

        return states;
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
            if (_sessions.TryGetValue(deviceId, out var session))
            {
                joystick = session.Joystick;
                deviceName = session.Info.Name;
                return true;
            }
        }

        // Refresh outside the first lock so we never nest DI work under a held wait
        // that the bridge poll thread also needs (avoids UI↔bridge deadlocks).
        RefreshDevices();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out var session))
                return false;
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
            try
            {
                foreach (var obj in joy.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
                {
                    var n = obj.Name ?? "";
                    if (n.Contains("X", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Wheel", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Steer", StringComparison.OrdinalIgnoreCase))
                    {
                        joy.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, 65535);
                        break;
                    }
                }
            }
            catch { /* range optional */ }
            joy.Acquire();

            var caps = joy.Capabilities;
            var axisCount = Math.Max(caps.AxeCount, 0);
            var buttonCount = caps.ButtonCount;
            var hasHat = caps.PovCount > 0;
            var ffb = caps.Flags.HasFlag(DeviceFlags.ForceFeedback);

            var info = new InputDeviceInfo
            {
                Id = instance.InstanceGuid.ToString("D"),
                ProductId = instance.ProductGuid.ToString("D"),
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

        public bool PollFailed { get; private set; }

        public DeviceState Poll()
        {
            JoystickState state;
            try
            {
                _joystick.Poll();
                state = _joystick.GetCurrentState();
                PollFailed = false;
            }
            catch
            {
                try
                {
                    _joystick.Acquire();
                    _joystick.Poll();
                    state = _joystick.GetCurrentState();
                    PollFailed = false;
                }
                catch
                {
                    PollFailed = true;
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
            // Prefer unsigned 0..65535. Signed -32768..32767 (center 0) is remapped.
            if (value < 0)
                return Math.Clamp((value + 32768) / 65535f, 0f, 1f);
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
