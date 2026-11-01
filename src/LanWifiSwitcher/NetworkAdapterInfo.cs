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
    public required string Name { get; init; }
    public string? Description { get; init; }
    public AdapterKind Kind { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsConnected { get; init; }
    public uint? InterfaceIndex { get; init; }
    public string? PnpDeviceId { get; init; }

    /// <summary>Short type label for picker UIs (e.g. "Wi‑Fi", "Ethernet", "Other").</summary>
    public string KindLabel => Kind switch
    {
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.Wireless => "Wi‑Fi",
        AdapterKind.Other => "Other",
        _ => "Unknown"
    };

    /// <summary>List/combo display: name, type hint, and optional description chipset clue.</summary>
    public string DisplayText
    {
        get
        {
            var status = IsEnabled ? (IsConnected ? "up" : "enabled") : "disabled";
            var desc = string.IsNullOrWhiteSpace(Description) ||
                       string.Equals(Description, Name, StringComparison.OrdinalIgnoreCase)
                ? null
                : Truncate(Description!, 48);
            return desc is null
                ? $"{Name}  [{KindLabel}, {status}]"
                : $"{Name}  [{KindLabel}, {status}] — {desc}";
        }
    }

    public override string ToString() => DisplayText;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
