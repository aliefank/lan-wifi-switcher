namespace LanWifiSwitcher;

public enum AdapterKind
{
    Unknown,
    Ethernet,
    Wireless,
    Other
}

public enum ConnectionMode
{
    Unknown,
    Lan,
    Wifi,
    Both,
    Neither
}

public sealed class NetworkAdapterInfo
{
    public required string DeviceId { get; init; }
    /// <summary>NetConnectionID / interface alias shown in ncpa.cpl (e.g. "Wi-Fi", "Ethernet").</summary>
    public required string Name { get; init; }
    public string? Description { get; init; }
    public AdapterKind Kind { get; init; }
    public bool IsEnabled { get; init; }
    /// <summary>Media connected (link up). Unplugged Ethernet can still be <see cref="IsEnabled"/>.</summary>
    public bool IsConnected { get; init; }
    public uint? InterfaceIndex { get; init; }
    public string? PnpDeviceId { get; init; }
    /// <summary>Virtual / VPN / Bluetooth / TAP — never auto-picked for LAN/Wi‑Fi roles.</summary>
    public bool IsVirtualOrIgnored { get; init; }

    public string KindLabel => Kind switch
    {
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.Wireless => "Wi‑Fi",
        AdapterKind.Other => "Other",
        _ => "Unknown"
    };

    public string StatusLabel
    {
        get
        {
            if (!IsEnabled) return "disabled";
            if (IsConnected) return "connected";
            // Cable unplugged / no association — adapter can still be toggled.
            return Kind == AdapterKind.Ethernet ? "enabled · no cable" : "enabled · not associated";
        }
    }

    public string Subtitle
    {
        get
        {
            var chip = string.IsNullOrWhiteSpace(Description) ||
                       string.Equals(Description, Name, StringComparison.OrdinalIgnoreCase)
                ? KindLabel
                : Truncate(Description!, 56);
            var ignore = IsVirtualOrIgnored ? " · ignore" : "";
            return $"{KindLabel} · {StatusLabel} · {chip}{ignore}";
        }
    }

    public string DisplayText => $"{Name}  [{KindLabel}, {StatusLabel}]";

    public override string ToString() => DisplayText;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
