using G920Emulator.Core.Models;

namespace G920Emulator.Core.Profiles;

/// <summary>
/// Keeps profile bindings working across device re-plugs / Windows reinstalls by
/// matching on stable ProductGuid (and product name) when InstanceGuid changes.
/// </summary>
public static class DeviceBindingResolver
{
    /// <summary>
    /// Updates <see cref="SourceRef.DeviceId"/> / FFB source ids in-place when the
    /// saved instance GUID is gone but the same product is still attached.
    /// Returns how many references were remapped.
    /// </summary>
    public static int RemapProfile(MappingProfile profile, IReadOnlyList<InputDeviceInfo> devices)
    {
        if (devices.Count == 0)
            return 0;

        var byInstance = devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var changed = 0;

        if (!string.IsNullOrWhiteSpace(profile.FfbSourceDeviceId) &&
            !byInstance.ContainsKey(profile.FfbSourceDeviceId))
        {
            var match = FindMatch(devices, profile.FfbSourceDeviceId, profile.FfbSourceProductId, preferFfb: true);
            if (match is not null)
            {
                profile.FfbSourceDeviceId = match.Id;
                profile.FfbSourceProductId = match.ProductId;
                changed++;
            }
        }
        else if (!string.IsNullOrWhiteSpace(profile.FfbSourceDeviceId) &&
                 byInstance.TryGetValue(profile.FfbSourceDeviceId, out var ffb) &&
                 string.IsNullOrWhiteSpace(profile.FfbSourceProductId))
        {
            profile.FfbSourceProductId = ffb.ProductId;
        }

        foreach (var binding in profile.Bindings)
        {
            binding.Normalize();
            foreach (var source in binding.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.DeviceId))
                    continue;

                if (byInstance.TryGetValue(source.DeviceId, out var live))
                {
                    if (string.IsNullOrWhiteSpace(source.ProductId))
                        source.ProductId = live.ProductId;
                    continue;
                }

                var match = FindMatch(devices, source.DeviceId, source.ProductId, preferFfb: false);
                if (match is null)
                    continue;

                source.DeviceId = match.Id;
                source.ProductId = match.ProductId;
                changed++;
            }
        }

        return changed;
    }

    public static string? ResolveDeviceId(
        string? deviceId,
        string? productId,
        IReadOnlyList<InputDeviceInfo> devices)
    {
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(productId))
            return null;
        if (!string.IsNullOrWhiteSpace(deviceId) &&
            devices.Any(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)))
            return deviceId;

        return FindMatch(devices, deviceId, productId, preferFfb: false)?.Id;
    }

    private static InputDeviceInfo? FindMatch(
        IReadOnlyList<InputDeviceInfo> devices,
        string? instanceId,
        string? productId,
        bool preferFfb)
    {
        IEnumerable<InputDeviceInfo> pool = devices;
        if (preferFfb)
        {
            var ffb = devices.Where(d => d.SupportsForceFeedback).ToList();
            if (ffb.Count > 0)
                pool = ffb;
        }

        if (!string.IsNullOrWhiteSpace(productId))
        {
            var byProduct = pool
                .Where(d => string.Equals(d.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byProduct.Count == 1)
                return byProduct[0];
            if (byProduct.Count > 1 && !string.IsNullOrWhiteSpace(instanceId))
            {
                // Same product plugged twice — keep previous instance if still present, else first.
                return byProduct.FirstOrDefault(d =>
                           string.Equals(d.Id, instanceId, StringComparison.OrdinalIgnoreCase))
                       ?? byProduct[0];
            }
            if (byProduct.Count > 0)
                return byProduct[0];
        }

        return null;
    }
}
