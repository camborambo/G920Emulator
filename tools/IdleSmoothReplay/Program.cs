using System.Globalization;
using System.Text.RegularExpressions;
using SteeringWheelEmulator.Core.Ffb;

/// <summary>
/// Replay emuffb-effects.log CF samples through Interpolate (full-path INT)
/// and print grain metrics (low-mag steps, sign flips, ΔRMS).
/// Usage: IdleSmoothReplay [path-to-emuffb-effects.log] [blendMs=50] [gapFillMs=80]
/// </summary>
static class Program
{
    private static readonly Regex CfLine = new(
        @"^(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})\.(?<ms>\d{3}).*type=0\(ConstantForce\).*flags=0x(?!80000000)(?<flags>[0-9A-Fa-f]+).*extra=(?<extra>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MixLine = new(
        @"^(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})\.(?<ms>\d{3}).*MIX axis=(?<axis>-?\d+) vel=(?<vel>-?\d+) cf=(?<cf>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static int Main(string[] args)
    {
        var logPath = args.Length > 0
            ? args[0]
            : FindDefaultLog();
        var blendMs = args.Length > 1 && float.TryParse(args[1], out var b) ? b : 50f;
        var gapFill = args.Length > 2 && float.TryParse(args[2], out var g) ? g : 80f;

        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            Console.Error.WriteLine("Usage: IdleSmoothReplay <emuffb-effects.log> [blendMs] [gapFillMs]");
            Console.Error.WriteLine("No log found. Pass a path from a Debug export with sparse CF.");
            return 1;
        }

        Console.WriteLine($"Log: {logPath}");
        Console.WriteLine($"interpolateMs={blendMs:0} gapFillMs={gapFill:0}");

        var samples = ParseSamples(logPath);
        Console.WriteLine($"CF samples (excl Start): {samples.Count}");
        if (samples.Count < 10)
        {
            Console.Error.WriteLine("Too few CF samples to compare.");
            return 2;
        }

        var raw = RunPassthrough(samples);
        var interp = RunInterpolate(samples, blendMs, gapFill);

        PrintMetrics("raw", raw);
        PrintMetrics("interpolate", interp);
        return 0;
    }

    private static string? FindDefaultLog()
    {
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            Directory.GetCurrentDirectory(),
        };
        foreach (var root in roots)
        {
            foreach (var dir in new[]
                     {
                         "tmp-diag-20261010-030926",
                         "tmp-diag-20261010-023146",
                         "tmp-diag-20261010-020834",
                         ".tmp-diag-20261010-030926",
                         ".tmp-diag-20261010-023146",
                         ".tmp-diag-20261010-020834",
                     })
            {
                var p = Path.Combine(root, dir, "logs", "emuffb-effects.log");
                if (File.Exists(p)) return p;
                p = Path.Combine(root, ".tmp-diag-20261010-030926", "logs", "emuffb-effects.log");
                if (File.Exists(p)) return p;
            }

            try
            {
                foreach (var candidate in Directory.EnumerateFiles(root, "emuffb-effects.log", SearchOption.AllDirectories)
                             .Take(20))
                {
                    if (candidate.Contains("20261010", StringComparison.Ordinal))
                        return candidate;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private sealed record Sample(long TMs, float Torque, float RimPos, float RimVelAbs);

    private static List<Sample> ParseSamples(string path)
    {
        // Latest MIX rim before each CF update (axis/10000 ≈ rim -1..1; vel/10000).
        float rim = 0f, velAbs = 0f;
        var list = new List<Sample>();
        foreach (var line in File.ReadLines(path))
        {
            var mix = MixLine.Match(line);
            if (mix.Success)
            {
                rim = int.Parse(mix.Groups["axis"].Value, CultureInfo.InvariantCulture) / 10000f;
                velAbs = Math.Abs(int.Parse(mix.Groups["vel"].Value, CultureInfo.InvariantCulture)) / 10000f;
                continue;
            }

            var cf = CfLine.Match(line);
            if (!cf.Success) continue;
            var mag = int.Parse(cf.Groups["extra"].Value, CultureInfo.InvariantCulture);
            // Skip flag-only / boot zeros that are not real DI magnitudes in ±10000.
            if (Math.Abs(mag) > 10000) continue;
            var t = ToMs(cf);
            // App torque convention: DI mag / 10000 (sign as logged).
            list.Add(new Sample(t, mag / 10000f, rim, Math.Clamp(velAbs, 0f, 1f)));
        }

        return list;
    }

    private static long ToMs(Match m)
    {
        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var ms = int.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture);
        return ((h * 60L + min) * 60L + s) * 1000L + ms;
    }

    private static List<float> RunPassthrough(List<Sample> samples) =>
        samples.Select(s => s.Torque).ToList();

    private static List<float> RunInterpolate(
        List<Sample> samples,
        float blendMs,
        float gapFill)
    {
        var sm = new FfbOutputSmoother
        {
            BootEaseIn = false,
            ReconstructionMs = blendMs,
            IdleGapHoldMs = gapFill,
            SoftClipStart = 1f,
        };
        sm.Reset();
        var t0 = samples[0].TMs;
        var outList = new List<float>(samples.Count);
        foreach (var s in samples)
        {
            var now = s.TMs - t0 + 1;
            outList.Add(sm.ProcessAt(s.Torque, 0, 0, true, ReadOnlySpan<int>.Empty, s.RimVelAbs, s.RimPos, now));
        }

        return outList;
    }

    private static void PrintMetrics(string name, List<float> series)
    {
        var steps = new List<float>();
        var lowSteps = new List<float>();
        var flips = 0;
        double sumSq = 0;
        for (var i = 1; i < series.Count; i++)
        {
            var d = Math.Abs(series[i] - series[i - 1]);
            steps.Add(d);
            sumSq += d * d;
            var a0 = Math.Abs(series[i - 1]);
            var a1 = Math.Abs(series[i]);
            if (a0 < 0.08f && a1 < 0.08f)
                lowSteps.Add(d);
            if (Math.Sign(series[i]) != Math.Sign(series[i - 1]) &&
                series[i] != 0 && series[i - 1] != 0 &&
                a0 < 0.15f && a1 < 0.15f)
                flips++;
        }

        steps.Sort();
        lowSteps.Sort();
        var rms = steps.Count == 0 ? 0 : Math.Sqrt(sumSq / steps.Count);
        Console.WriteLine(
            $"{name,-12} n={series.Count} Δp50={Pct(steps, 0.5):0.0000} Δp90={Pct(steps, 0.9):0.0000} " +
            $"Δrms={rms:0.0000} lowΔp50={Pct(lowSteps, 0.5):0.0000} lowΔp90={Pct(lowSteps, 0.9):0.0000} " +
            $"lowSignFlips={flips}");
    }

    private static float Pct(List<float> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var i = (int)Math.Clamp(Math.Round((sorted.Count - 1) * p), 0, sorted.Count - 1);
        return sorted[i];
    }
}
