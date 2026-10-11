namespace SteeringWheelEmulator.Core.Models;

/// <summary>
/// Bindable virtual controls for one <see cref="EmulatedDeviceKind"/>.
/// Physical SourceRef devices stay shared; only virtual targets are catalog-scoped.
/// </summary>
public interface IControlCatalog
{
    EmulatedDeviceKind Kind { get; }

    /// <summary>Rows in the main binding list.</summary>
    IReadOnlyList<G920Control> StandardBindTargets { get; }

    /// <summary>Targets allowed for named custom bindings.</summary>
    IReadOnlyList<G920Control> CustomBindTargets { get; }

    IReadOnlyList<G920Control> AxisTargets { get; }
    IReadOnlyList<G920Control> ButtonTargets { get; }
    IReadOnlyList<G920Control> HatTargets { get; }

    /// <summary>Max DI button index for Gear R output (G920 = 19).</summary>
    int MaxGearReverseOutputButton { get; }

    int DefaultGearReverseOutputButton { get; }

    string DisplayName(G920Control control);

    bool IsAxis(G920Control control);
    bool IsButton(G920Control control);
    bool IsHat(G920Control control);
    bool IsCustomBindingTarget(G920Control control);
    bool IsStandardBindTarget(G920Control control);
}
