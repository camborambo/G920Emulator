using SteeringWheelEmulator.Core.Mapping;

namespace SteeringWheelEmulator.Core.Models;

/// <summary>Registry of emulated device profiles (identity + catalog + report builder).</summary>
public static class EmulatedDeviceProfiles
{
    private static readonly IEmulatedDeviceProfile G920 = new G920EmulatedDeviceProfile();
    // Product strings must match live physical DI names.
    // Unbound keys Fanatec FFB profiles off "FANATEC Podium Wheel Base DD1", not a custom Emulator title.
    private static readonly IEmulatedDeviceProfile Dd1PcComp = new FanatecDd1EmulatedDeviceProfile(
        EmulatedDeviceKind.FanatecDd1PcComp,
        FanatecDd1Identity.ProductIdPcComp,
        "Fanatec",
        "FANATEC Podium Wheel Base DD1",
        new Guid("0EB70004-DD01-4C01-9A01-0EB700040001"));

    public static IEmulatedDeviceProfile Get(EmulatedDeviceKind kind) => kind switch
    {
        EmulatedDeviceKind.FanatecDd1PcComp => Dd1PcComp,
        _ => G920,
    };

    public static IReadOnlyList<EmulatedDeviceKind> AllKinds { get; } =
    [
        EmulatedDeviceKind.LogitechG920,
        EmulatedDeviceKind.FanatecDd1PcComp,
    ];

    private sealed class G920EmulatedDeviceProfile : IEmulatedDeviceProfile
    {
        public EmulatedDeviceKind Kind => EmulatedDeviceKind.LogitechG920;
        public string DisplayName => EmulatedDeviceKindInfo.DisplayName(Kind);
        public ushort VendorId => G920Identity.VendorId;
        public ushort ProductId => G920Identity.ProductId;
        public ushort VersionNumber => 0x9601;
        public string Manufacturer => "Logitech";
        public string Product => "G920 Driving Force Racing Wheel for Xbox One";
        public Guid ContainerId => new("8F3C2A1E-46D0-C262-9A01-046DC2620001");
        public string OemKeyName => "VID_046D&PID_C262";
        public IControlCatalog Catalog { get; } = new WheelControlCatalog(EmulatedDeviceKind.LogitechG920);

        public byte[] BuildReport(MappedG920State state) => G920ReportBuilder.Build(state);
        public byte[] BuildIdleReport() => G920ReportBuilder.Build(new MappedG920State());
    }

    private sealed class FanatecDd1EmulatedDeviceProfile : IEmulatedDeviceProfile
    {
        public FanatecDd1EmulatedDeviceProfile(
            EmulatedDeviceKind kind,
            ushort productId,
            string manufacturer,
            string product,
            Guid containerId)
        {
            Kind = kind;
            ProductId = productId;
            Manufacturer = manufacturer;
            Product = product;
            ContainerId = containerId;
            OemKeyName = $"VID_{FanatecDd1Identity.VendorId:X4}&PID_{productId:X4}";
            Catalog = WheelControlCatalog.CreateFanatec(kind);
        }

        public EmulatedDeviceKind Kind { get; }
        public string DisplayName => EmulatedDeviceKindInfo.DisplayName(Kind);
        public ushort VendorId => FanatecDd1Identity.VendorId;
        public ushort ProductId { get; }
        public ushort VersionNumber => FanatecDd1Identity.VersionNumber;
        public string Manufacturer { get; }
        public string Product { get; }
        public Guid ContainerId { get; }
        public string OemKeyName { get; }
        public IControlCatalog Catalog { get; }

        public byte[] BuildReport(MappedG920State state) => FanatecDd1ReportBuilder.Build(state);
        public byte[] BuildIdleReport() => FanatecDd1ReportBuilder.BuildIdle();
    }
}
