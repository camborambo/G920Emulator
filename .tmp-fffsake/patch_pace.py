from pathlib import Path

path = Path(r"src/G920Emulator.Core/Ffb/FfbBridge.cs")
text = path.read_text(encoding="utf-8")
start = text.index("    /// <summary>\n    /// Non-Fanatec: track latest OEM target")
end = text.index("    private bool PushMagnitudeToDevice", start)
new = r'''    /// <summary>
    /// Non-Fanatec: FFFSake-style device update — keep the latest OEM magnitude and
    /// "slip" SetParameters faster than <see cref="NonFanatecCfPacePeriodMs"/> (same idea
    /// as FFFSake Device Update Period). No EMA / rate-limit of our own.
    /// </summary>
    private void ApplyTorquePaced(Effect effect, Joystick? joy, float torque, int targetMagnitude, bool fromTest)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            _lastCommandTorque = torque;
            _paceTargetMagnitude = targetMagnitude;

            var elapsed = _lastPaceApplyTickMs == 0
                ? long.MaxValue
                : now - _lastPaceApplyTickMs;

            // Still allow an immediate push when magnitude jumps a lot (direction flip /
            // crash), so a 4 ms slip cannot hold stale force across a sign change.
            var jump = _lastAppliedMagnitude == int.MinValue ||
                       Math.Abs(targetMagnitude - _lastAppliedMagnitude) >= 2000;

            if (!jump && elapsed < NonFanatecCfPacePeriodMs)
            {
                _paceSkipCount++;
                return;
            }

            if (!fromTest &&
                targetMagnitude == _lastMagnitude &&
                _lastApplyUtc is { } lastApply &&
                (DateTime.UtcNow - lastApply).TotalMilliseconds < 100)
            {
                _lastPaceApplyTickMs = now;
                return;
            }

            var eps = MagnitudeEpsilon;
            if (!fromTest && eps > 0 &&
                _lastAppliedMagnitude != int.MinValue &&
                Math.Abs(targetMagnitude - _lastAppliedMagnitude) < eps &&
                !(targetMagnitude == 0 && _lastAppliedMagnitude != 0))
            {
                _lastMagnitude = _lastAppliedMagnitude;
                _lastPaceApplyTickMs = now;
                return;
            }
        }

        if (!PushMagnitudeToDevice(effect, joy, targetMagnitude, out var pushError))
        {
            lock (_gate) { _lastError = pushError; }
            return;
        }

        lock (_gate)
        {
            _lastMagnitude = targetMagnitude;
            _lastAppliedMagnitude = targetMagnitude;
            _lastPaceApplyTickMs = Environment.TickCount64;
            _lastApplyUtc = DateTime.UtcNow;
            _applyCount++;
            _paceApplyCount++;
            _lastError = null;
        }
    }

'''
path.write_text(text[:start] + new + text[end:], encoding="utf-8")
print("ok", end - start)
