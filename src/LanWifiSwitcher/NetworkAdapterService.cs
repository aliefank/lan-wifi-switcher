using System.Management;
using System.Net.NetworkInformation;
using System.Text;

namespace LanWifiSwitcher;

/// <summary>
/// Discovers adapters and enables/disables them via WMI (Win32_NetworkAdapter).
/// Falls back to netsh when WMI methods are unavailable.
/// Classification prefers .NET <see cref="NetworkInterfaceType"/> and Wi‑Fi chipset
/// signals over weak name heuristics (e.g. bare "Realtek" must not force Ethernet —
/// Realtek 8922AE / RTL88xx are Wi‑Fi NICs common on ASUS boards).
/// </summary>
public sealed class NetworkAdapterService
{
    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var results = new List<NetworkAdapterInfo>();
        var nicByIndex = BuildNetworkInterfaceMap();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, NetConnectionID, NetEnabled, NetConnectionStatus, AdapterTypeId, InterfaceIndex, Description, PNPDeviceID, ProductName, Manufacturer " +
                "FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var connectionName = mo["NetConnectionID"]?.ToString();
                    if (string.IsNullOrWhiteSpace(connectionName))
                    {
                        continue;
                    }

                    var deviceId = mo["DeviceID"]?.ToString() ?? connectionName;
                    var description = mo["Description"]?.ToString()
                                      ?? mo["ProductName"]?.ToString();
                    var name = mo["Name"]?.ToString() ?? connectionName;
                    var manufacturer = mo["Manufacturer"]?.ToString();
                    var enabled = mo["NetEnabled"] is bool b && b;
                    var status = Convert.ToUInt16(mo["NetConnectionStatus"] ?? 0);
                    // 2 = Connected
                    var connected = status == 2;
                    uint? ifIndex = mo["InterfaceIndex"] is null ? null : Convert.ToUInt32(mo["InterfaceIndex"]);
                    var pnp = mo["PNPDeviceID"]?.ToString();
                    ushort? adapterTypeId = mo["AdapterTypeId"] is null
                        ? null
                        : Convert.ToUInt16(mo["AdapterTypeId"]);

                    NetworkInterface? matchedNic = null;
                    if (ifIndex is not null && nicByIndex.TryGetValue(ifIndex.Value, out var byIdx))
                    {
                        matchedNic = byIdx;
                    }
                    else
                    {
                        matchedNic = nicByIndex.Values.FirstOrDefault(n =>
                            string.Equals(n.Name, connectionName, StringComparison.OrdinalIgnoreCase));
                    }

                    var kind = Classify(
                        connectionName,
                        description,
                        name,
                        manufacturer,
                        pnp,
                        adapterTypeId,
                        matchedNic?.NetworkInterfaceType);

                    results.Add(new NetworkAdapterInfo
                    {
                        DeviceId = deviceId,
                        Name = connectionName,
                        Description = description ?? name,
                        Kind = kind,
                        IsEnabled = enabled,
                        IsConnected = connected,
                        InterfaceIndex = ifIndex,
                        PnpDeviceId = pnp
                    });
                }
            }
        }
        catch
        {
            // Fall back to NetworkInterface enumeration (status only; toggle still needs WMI/netsh).
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var kind = ClassifyFromNetworkInterfaceType(nic.NetworkInterfaceType)
                           ?? Classify(
                               nic.Name,
                               nic.Description,
                               nic.Name,
                               manufacturer: null,
                               pnpDeviceId: null,
                               adapterTypeId: null,
                               networkInterfaceType: nic.NetworkInterfaceType);

                results.Add(new NetworkAdapterInfo
                {
                    DeviceId = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    Kind = kind,
                    IsEnabled = nic.OperationalStatus != OperationalStatus.NotPresent,
                    IsConnected = nic.OperationalStatus == OperationalStatus.Up,
                    InterfaceIndex = null
                });
            }
        }

        return results
            .OrderBy(a => KindSortOrder(a.Kind))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public NetworkAdapterInfo? FindByNameOrId(string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return null;
        }

        var adapters = GetAdapters();
        return adapters.FirstOrDefault(a =>
                   string.Equals(a.DeviceId, nameOrId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(a.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? adapters.FirstOrDefault(a =>
                   a.Description is not null &&
                   string.Equals(a.Description, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    public void AutoSelectDefaults(AppConfig config)
    {
        var adapters = GetAdapters();

        if (string.IsNullOrWhiteSpace(config.EthernetAdapterName) &&
            string.IsNullOrWhiteSpace(config.EthernetAdapterId))
        {
            var ethernet = adapters
                .Where(a => a.Kind == AdapterKind.Ethernet)
                .OrderByDescending(a => a.IsConnected)
                .ThenByDescending(a => a.IsEnabled)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (ethernet is not null)
            {
                config.EthernetAdapterId = ethernet.DeviceId;
                config.EthernetAdapterName = ethernet.Name;
            }
        }

        if (string.IsNullOrWhiteSpace(config.WifiAdapterName) &&
            string.IsNullOrWhiteSpace(config.WifiAdapterId))
        {
            var wifi = adapters
                .Where(a => a.Kind == AdapterKind.Wireless)
                .OrderByDescending(a => a.IsConnected)
                .ThenByDescending(a => a.IsEnabled)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            // Last resort: scored "Other" entries that look wireless (odd ASUS/MediaTek names).
            wifi ??= adapters
                .Where(a => a.Kind == AdapterKind.Other && LooksWireless(BuildHaystack(a)))
                .OrderByDescending(a => a.IsConnected)
                .ThenByDescending(a => a.IsEnabled)
                .FirstOrDefault();

            if (wifi is not null)
            {
                config.WifiAdapterId = wifi.DeviceId;
                config.WifiAdapterName = wifi.Name;
            }
        }
    }

    public ConnectionMode DetectCurrentMode(AppConfig config)
    {
        var ethernet = Resolve(config.EthernetAdapterId, config.EthernetAdapterName);
        var wifi = Resolve(config.WifiAdapterId, config.WifiAdapterName);

        var lanOn = ethernet?.IsEnabled == true;
        var wifiOn = wifi?.IsEnabled == true;

        if (lanOn && !wifiOn) return ConnectionMode.Lan;
        if (wifiOn && !lanOn) return ConnectionMode.Wifi;
        if (lanOn && wifiOn) return ConnectionMode.Both;
        if (!lanOn && !wifiOn) return ConnectionMode.Neither;
        return ConnectionMode.Unknown;
    }

    public ConnectionMode Toggle(AppConfig config)
    {
        var ethernet = ResolveRequired(config.EthernetAdapterId, config.EthernetAdapterName, "Ethernet / LAN");
        var wifi = ResolveRequired(config.WifiAdapterId, config.WifiAdapterName, "Wi‑Fi");

        var mode = DetectCurrentMode(config);
        // Prefer switching TO Wi‑Fi when both/neither/unknown so laptop users can go mobile;
        // when already on Wi‑Fi, switch to LAN.
        var target = mode == ConnectionMode.Wifi ? ConnectionMode.Lan : ConnectionMode.Wifi;

        if (target == ConnectionMode.Lan)
        {
            SetEnabled(ethernet, enabled: true);
            SetEnabled(wifi, enabled: false);
        }
        else
        {
            SetEnabled(wifi, enabled: true);
            SetEnabled(ethernet, enabled: false);
        }

        // Brief settle time so status queries reflect the change.
        Thread.Sleep(400);
        return DetectCurrentMode(config);
    }

    public void SwitchTo(AppConfig config, ConnectionMode target)
    {
        if (target is not (ConnectionMode.Lan or ConnectionMode.Wifi))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        var ethernet = ResolveRequired(config.EthernetAdapterId, config.EthernetAdapterName, "Ethernet / LAN");
        var wifi = ResolveRequired(config.WifiAdapterId, config.WifiAdapterName, "Wi‑Fi");

        if (target == ConnectionMode.Lan)
        {
            SetEnabled(ethernet, enabled: true);
            SetEnabled(wifi, enabled: false);
        }
        else
        {
            SetEnabled(wifi, enabled: true);
            SetEnabled(ethernet, enabled: false);
        }

        Thread.Sleep(400);
    }

    public void SetEnabled(NetworkAdapterInfo adapter, bool enabled)
    {
        var errors = new List<string>();

        if (TryWmiSetEnabled(adapter, enabled, out var wmiError))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(wmiError))
        {
            errors.Add(wmiError);
        }

        try
        {
            RunNetsh(adapter.Name, enabled);
            return;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        var action = enabled ? "enable" : "disable";
        var detail = errors.Count == 0
            ? "WMI and netsh both failed with no detail."
            : string.Join(" | ", errors);
        throw new InvalidOperationException(
            $"Could not {action} adapter '{adapter.Name}' (id {adapter.DeviceId}). {detail} " +
            "Confirm the app is elevated (UAC) and the name matches Control Panel → Network Connections (ncpa.cpl).");
    }

    private NetworkAdapterInfo? Resolve(string? id, string? name) =>
        FindByNameOrId(id) ?? FindByNameOrId(name);

    private NetworkAdapterInfo ResolveRequired(string? id, string? name, string label)
    {
        var adapter = Resolve(id, name);
        if (adapter is null)
        {
            var configured = string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)
                ? "nothing is saved yet"
                : $"configured as name='{name ?? "(none)"}', id='{id ?? "(none)"}'";
            throw new InvalidOperationException(
                $"{label} adapter was not found ({configured}). " +
                "Open Choose adapters… and pick your NIC from the full list " +
                "(Wi‑Fi chips like Realtek 8922AE / MediaTek appear with a [Wi‑Fi] hint).");
        }

        return adapter;
    }

    private static bool TryWmiSetEnabled(NetworkAdapterInfo adapter, bool enabled, out string? error)
    {
        error = null;
        var method = enabled ? "Enable" : "Disable";
        uint? lastResult = null;

        try
        {
            // Match by NetConnectionID (friendly name shown in ncpa.cpl) first.
            if (InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID = '" + EscapeWmi(adapter.Name) + "'",
                    method,
                    out lastResult))
            {
                return true;
            }

            // Retry by DeviceID.
            if (InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE DeviceID = '" + EscapeWmi(adapter.DeviceId) + "'",
                    method,
                    out lastResult))
            {
                return true;
            }

            // Retry by InterfaceIndex when available (stable across renames on some builds).
            if (adapter.InterfaceIndex is uint idx &&
                InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE InterfaceIndex = " + idx,
                    method,
                    out lastResult))
            {
                return true;
            }

            error = lastResult is null
                ? "WMI: adapter object not found by name/id/index."
                : $"WMI {method} returned code {lastResult} (0=ok, 5=already).";
        }
        catch (Exception ex)
        {
            error = $"WMI error: {ex.Message}";
            return false;
        }

        return false;
    }

    private static bool InvokeWmiByQuery(string query, string method, out uint? lastResult)
    {
        lastResult = null;
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var result = (uint)mo.InvokeMethod(method, null!);
                lastResult = result;
                // 0 = success, 5 = already in requested state on some drivers.
                if (result is 0 or 5)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RunNetsh(string interfaceName, bool enabled)
    {
        var admin = enabled ? "ENABLED" : "DISABLED";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface set interface name=\"{interfaceName}\" admin={admin}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start netsh.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"netsh failed (exit {process.ExitCode}) while {(enabled ? "enabling" : "disabling")} '{interfaceName}'. {(stderr + " " + stdout).Trim()}");
        }
    }

    private static string EscapeWmi(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static Dictionary<uint, NetworkInterface> BuildNetworkInterfaceMap()
    {
        var map = new Dictionary<uint, NetworkInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var props = nic.GetIPProperties()?.GetIPv4Properties();
                    if (props is not null)
                    {
                        map[(uint)props.Index] = nic;
                    }
                }
                catch
                {
                    // Some NICs throw on property access when disabled; ignore.
                }
            }
        }
        catch
        {
            // NetworkInterface enumeration can fail in locked-down environments.
        }

        return map;
    }

    private static int KindSortOrder(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => 0,
        AdapterKind.Wireless => 1,
        AdapterKind.Other => 2,
        _ => 3
    };

    internal static AdapterKind Classify(
        string connectionName,
        string? description,
        string name,
        string? manufacturer,
        string? pnpDeviceId,
        ushort? adapterTypeId,
        NetworkInterfaceType? networkInterfaceType)
    {
        // 1) Authoritative .NET interface type when available.
        var fromNic = ClassifyFromNetworkInterfaceType(networkInterfaceType);
        if (fromNic is AdapterKind.Wireless or AdapterKind.Ethernet)
        {
            return fromNic.Value;
        }

        var haystack = BuildHaystack(connectionName, description, name, manufacturer, pnpDeviceId);

        // 2) Virtual / non-LAN first so VPN/Hyper-V do not look like Ethernet.
        if (LooksVirtualOrNonPhysical(haystack))
        {
            return AdapterKind.Other;
        }

        // 3) Wi‑Fi signals — MUST run before any Realtek/ethernet vendor heuristics.
        //    Realtek 8922AE / RTL8852BE / MediaTek MT7922 are Wi‑Fi, not LAN.
        if (LooksWireless(haystack))
        {
            return AdapterKind.Wireless;
        }

        // 4) WMI AdapterTypeId: 9 = Wireless, 0 = Ethernet 802.3
        if (adapterTypeId == 9)
        {
            return AdapterKind.Wireless;
        }

        if (adapterTypeId == 0 && !LooksWireless(haystack))
        {
            return AdapterKind.Ethernet;
        }

        // 5) Ethernet name/description (avoid bare vendor strings like "realtek" / "asus").
        if (LooksEthernet(haystack))
        {
            return AdapterKind.Ethernet;
        }

        return fromNic ?? AdapterKind.Other;
    }

    private static AdapterKind? ClassifyFromNetworkInterfaceType(NetworkInterfaceType? type) =>
        type switch
        {
            null => null,
            NetworkInterfaceType.Wireless80211 => AdapterKind.Wireless,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.GigabitEthernet => AdapterKind.Ethernet,
            NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel => AdapterKind.Other,
            _ => null
        };

    private static string BuildHaystack(NetworkAdapterInfo adapter) =>
        BuildHaystack(adapter.Name, adapter.Description, adapter.Name, null, adapter.PnpDeviceId);

    private static string BuildHaystack(
        string connectionName,
        string? description,
        string name,
        string? manufacturer,
        string? pnpDeviceId)
    {
        var sb = new StringBuilder();
        sb.Append(connectionName).Append(' ')
          .Append(description).Append(' ')
          .Append(name).Append(' ')
          .Append(manufacturer).Append(' ')
          .Append(pnpDeviceId);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Detect Wi‑Fi NICs including oddly named ASUS / MediaTek / Realtek wireless chips
    /// (e.g. "Realtek 8922AE Wi‑Fi 7 PCIe", "Wi-Fi 6", "WLAN", "Wireless").
    /// </summary>
    internal static bool LooksWireless(string haystackLower)
    {
        // Explicit wireless vocabulary (covers "Wi-Fi 7", "WiFi", "WLAN", …).
        ReadOnlySpan<string> tokens =
        [
            "wi-fi", "wifi", "wi‑fi", "wireless", "wlan", "802.11", "80211",
            "wi fi",
            // Generational labels often appear alone on connection names.
            "wi-fi 6", "wi-fi 6e", "wi-fi 7", "wifi 6", "wifi 6e", "wifi 7",
            // Common PCIe Wi‑Fi chipset families (ASUS B850I etc.).
            "8922ae", "rtl8922", "8852", "rtl8852", "8822", "rtl8822", "8821",
            "mt792", "mt76", "mediatek", "media tek",
            "ax201", "ax210", "ax211", "ax411", "be200", "be202",
            "killer wi", "killer(r) wi", "qualcomm", "atheros", "qca6", "qcn",
            "broadcom 802", "bcm43", "intel(r) wi", "intel® wi", "intel wifi",
            "wireless-ac", "wireless-ax", "wireless ae", "pcie wifi", "wifi 7 pcie"
        ];

        foreach (var token in tokens)
        {
            if (haystackLower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // PNP class hint: USB\Class_E0 is Bluetooth; {4d36e972-...} is net class (too broad).
        // PCI\VEN_* alone is not enough; chipset tokens above cover Realtek/MediaTek Wi‑Fi.
        return false;
    }

    private static bool LooksEthernet(string haystackLower)
    {
        ReadOnlySpan<string> tokens =
        [
            "ethernet", "gigabit", "2.5g", "2.5 gigabit", "5g ethernet", "10gbe", "10 gigabit",
            "local area connection", "realtek pcie gb", "realtek gaming",
            "realtek usb gb", "intel(r) ethernet", "intel® ethernet",
            "killer ethernet", "marvell", "broadcom netxtreme", "nvidia nforce",
            "lan port", "rj45"
        ];

        foreach (var token in tokens)
        {
            if (haystackLower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksVirtualOrNonPhysical(string haystackLower)
    {
        ReadOnlySpan<string> tokens =
        [
            "bluetooth", "virtual", "hyper-v", "vethernet", "vpn", "tap-", "tap adapter",
            "loopback", "pseudo", "wsl", "docker", "npcap", "wireguard", "nordlynx",
            "cisco anyconnect", "virtualbox", "vmware", "host-only", "kernel debug"
        ];

        foreach (var token in tokens)
        {
            if (haystackLower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
