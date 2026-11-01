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

    public override string ToString() =>
        $"{Name}{(IsEnabled ? "" : " [disabled]")}";
}
