using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace LanWifiSwitcher;

/// <summary>
/// Discovers adapters and enables/disables them via WMI (Win32_NetworkAdapter).
/// Falls back to netsh when WMI methods are unavailable.
/// Prefers ncpa.cpl aliases ("Wi-Fi", "Ethernet"); ignores TAP/VPN/Bluetooth for auto-pick.
/// Toggle only flips admin enable/disable — cable link is NOT required.
/// </summary>
public sealed class NetworkAdapterService
{
    private static readonly Regex EthernetAliasRegex = new(
        @"^ethernet(\s+\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WifiAliasRegex = new(
        @"^(wi[-\s]?fi|wifi|wlan)(\s+\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var results = new List<NetworkAdapterInfo>();
        var nicByIndex = BuildNetworkInterfaceMap();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, NetConnectionID, NetEnabled, NetConnectionStatus, AdapterTypeId, InterfaceIndex, Description, PNPDeviceID, ProductName, Manufacturer, PhysicalAdapter " +
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
                    var enabled = ReadNetEnabled(mo);
                    var status = Convert.ToUInt16(mo["NetConnectionStatus"] ?? 0);
                    // 2 = Connected (media/link). 7 = Media disconnected (cable unplugged) — still "enabled".
                    var connected = status == 2;
                    uint? ifIndex = mo["InterfaceIndex"] is null ? null : Convert.ToUInt32(mo["InterfaceIndex"]);
                    var pnp = mo["PNPDeviceID"]?.ToString();
                    ushort? adapterTypeId = mo["AdapterTypeId"] is null
                        ? null
                        : Convert.ToUInt16(mo["AdapterTypeId"]);
                    bool? physical = mo["PhysicalAdapter"] is bool pb ? pb : null;

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

                    var haystack = BuildHaystack(connectionName, description, name, manufacturer, pnp);
                    var ignored = LooksVirtualOrNonPhysical(haystack) || physical == false;
                    var kind = Classify(
                        connectionName,
                        description,
                        name,
                        manufacturer,
                        pnp,
                        adapterTypeId,
                        matchedNic?.NetworkInterfaceType,
                        ignored);

                    results.Add(new NetworkAdapterInfo
                    {
                        DeviceId = deviceId,
                        Name = connectionName,
                        Description = description ?? name,
                        Kind = kind,
                        IsEnabled = enabled,
                        IsConnected = connected,
                        InterfaceIndex = ifIndex,
                        PnpDeviceId = pnp,
                        IsVirtualOrIgnored = ignored || kind == AdapterKind.Other && LooksVirtualOrNonPhysical(haystack)
                    });
                }
            }
        }
        catch
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var haystack = BuildHaystack(nic.Name, nic.Description, nic.Name, null, null);
                var ignored = LooksVirtualOrNonPhysical(haystack);
                var kind = Classify(
                    nic.Name,
                    nic.Description,
                    nic.Name,
                    manufacturer: null,
                    pnpDeviceId: null,
                    adapterTypeId: null,
                    networkInterfaceType: nic.NetworkInterfaceType,
                    forceIgnored: ignored);

                results.Add(new NetworkAdapterInfo
                {
                    DeviceId = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    Kind = kind,
                    IsEnabled = nic.OperationalStatus != OperationalStatus.NotPresent,
                    IsConnected = nic.OperationalStatus == OperationalStatus.Up,
                    InterfaceIndex = null,
                    IsVirtualOrIgnored = ignored
                });
            }
        }

        return results
            .OrderBy(a => KindSortOrder(a.Kind))
            .ThenBy(a => a.IsVirtualOrIgnored)
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
        // Prefer NetConnectionID / alias match over DeviceID (indices can reshuffle).
        return adapters.FirstOrDefault(a =>
                   string.Equals(a.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? adapters.FirstOrDefault(a =>
                   string.Equals(a.DeviceId, nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? adapters.FirstOrDefault(a =>
                   a.Description is not null &&
                   string.Equals(a.Description, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Auto-pick or repair LAN/Wi‑Fi roles. Prefers aliases "Ethernet" / "Wi-Fi".
    /// Ignores Bluetooth, TAP, OpenVPN, and other virtual NICs. Does not require a cable link.
    /// </summary>
    public void AutoSelectDefaults(AppConfig config, bool repairUnsuitable = true)
    {
        var adapters = GetAdapters();

        var ethernetOk = IsSuitableLan(Resolve(config.EthernetAdapterId, config.EthernetAdapterName));
        if (!ethernetOk)
        {
            if (repairUnsuitable)
            {
                config.EthernetAdapterId = null;
                config.EthernetAdapterName = null;
            }

            if (string.IsNullOrWhiteSpace(config.EthernetAdapterName) &&
                string.IsNullOrWhiteSpace(config.EthernetAdapterId))
            {
                var ethernet = PickBest(adapters, AdapterKind.Ethernet, preferWifiAlias: false);
                if (ethernet is not null)
                {
                    config.EthernetAdapterId = ethernet.DeviceId;
                    config.EthernetAdapterName = ethernet.Name;
                }
            }
        }
        else
        {
            // Refresh stored DeviceId while keeping the preferred NetConnectionID alias.
            var current = Resolve(config.EthernetAdapterId, config.EthernetAdapterName)!;
            config.EthernetAdapterId = current.DeviceId;
            config.EthernetAdapterName = current.Name;
        }

        var wifiOk = IsSuitableWifi(Resolve(config.WifiAdapterId, config.WifiAdapterName));
        if (!wifiOk)
        {
            if (repairUnsuitable)
            {
                config.WifiAdapterId = null;
                config.WifiAdapterName = null;
            }

            if (string.IsNullOrWhiteSpace(config.WifiAdapterName) &&
                string.IsNullOrWhiteSpace(config.WifiAdapterId))
            {
                var wifi = PickBest(adapters, AdapterKind.Wireless, preferWifiAlias: true);
                if (wifi is not null)
                {
                    config.WifiAdapterId = wifi.DeviceId;
                    config.WifiAdapterName = wifi.Name;
                }
            }
        }
        else
        {
            var current = Resolve(config.WifiAdapterId, config.WifiAdapterName)!;
            config.WifiAdapterId = current.DeviceId;
            config.WifiAdapterName = current.Name;
        }
    }

    public ConnectionMode DetectCurrentMode(AppConfig config)
    {
        var ethernet = Resolve(config.EthernetAdapterId, config.EthernetAdapterName);
        var wifi = Resolve(config.WifiAdapterId, config.WifiAdapterName);

        // Mode is based on admin enable/disable — NOT cable/media connected.
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
        // Repair stale TAP/VPN picks before toggling.
        AutoSelectDefaults(config, repairUnsuitable: true);
        config.Save();

        var ethernet = ResolveRequired(config.EthernetAdapterId, config.EthernetAdapterName, "Ethernet / LAN");
        var wifi = ResolveRequired(config.WifiAdapterId, config.WifiAdapterName, "Wi‑Fi");

        var mode = DetectCurrentMode(config);
        var target = mode == ConnectionMode.Wifi ? ConnectionMode.Lan : ConnectionMode.Wifi;

        ApplyTarget(ethernet, wifi, target);
        Thread.Sleep(500);
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
        ApplyTarget(ethernet, wifi, target);
        Thread.Sleep(500);
    }

    private void ApplyTarget(NetworkAdapterInfo ethernet, NetworkAdapterInfo wifi, ConnectionMode target)
    {
        // Order: enable target first so the machine is never left with both down mid-toggle.
        // Cable link is irrelevant — Enable/Disable is admin state only.
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
    }

    public void SetEnabled(NetworkAdapterInfo adapter, bool enabled)
    {
        // Already in desired admin state — success even if cable is unplugged.
        if (adapter.IsEnabled == enabled)
        {
            return;
        }

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

        // Re-check: some drivers report odd WMI codes but still flip NetEnabled.
        var refreshed = FindByNameOrId(adapter.Name);
        if (refreshed is not null && refreshed.IsEnabled == enabled)
        {
            return;
        }

        var action = enabled ? "enable" : "disable";
        var detail = errors.Count == 0
            ? "WMI and netsh both failed with no detail."
            : string.Join(" | ", errors);
        throw new InvalidOperationException(
            $"Could not {action} adapter '{adapter.Name}' (id {adapter.DeviceId}). {detail} " +
            "Confirm the app is elevated (UAC) and the name matches Control Panel → Network Connections (ncpa.cpl). " +
            "A disconnected Ethernet cable is OK — only the adapter itself must enable/disable.");
    }

    private NetworkAdapterInfo? Resolve(string? id, string? name)
    {
        // Alias / NetConnectionID first, then DeviceID.
        return FindByNameOrId(name) ?? FindByNameOrId(id);
    }

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
                "Open Choose adapters… and pick the ncpa.cpl aliases \"Ethernet\" and \"Wi-Fi\" " +
                "(ignore Bluetooth / TAP / OpenVPN).");
        }

        return adapter;
    }

    private static NetworkAdapterInfo? PickBest(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        AdapterKind kind,
        bool preferWifiAlias)
    {
        return adapters
            .Where(a => !a.IsVirtualOrIgnored && a.Kind == kind)
            .OrderByDescending(a => AliasScore(a.Name, preferWifiAlias))
            .ThenByDescending(a => a.IsEnabled)   // prefer currently enabled; cable optional
            .ThenByDescending(a => a.IsConnected) // nice-to-have only
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsSuitableLan(NetworkAdapterInfo? adapter) =>
        adapter is { Kind: AdapterKind.Ethernet, IsVirtualOrIgnored: false };

    private static bool IsSuitableWifi(NetworkAdapterInfo? adapter) =>
        adapter is { Kind: AdapterKind.Wireless, IsVirtualOrIgnored: false };

    /// <summary>
    /// Higher = better match for canonical Windows connection names.
    /// Exact "Ethernet" / "Wi-Fi" beat hardware description strings.
    /// </summary>
    internal static int AliasScore(string connectionName, bool wifi)
    {
        var n = connectionName.Trim();
        if (wifi)
        {
            if (n.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("WiFi", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("WLAN", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (WifiAliasRegex.IsMatch(n))
            {
                return 90;
            }

            return 0;
        }

        if (n.Equals("Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (EthernetAliasRegex.IsMatch(n))
        {
            return 90;
        }

        return 0;
    }

    private static bool ReadNetEnabled(ManagementObject mo)
    {
        try
        {
            if (mo["NetEnabled"] is bool b)
            {
                return b;
            }
        }
        catch
        {
            // Some adapters throw on NetEnabled when media is disconnected; fall through.
        }

        // Fallback: status codes where adapter is administratively up.
        // 0=Disconnected, 1=Connecting, 2=Connected, 3=Disconnecting, 4=Hardware not present,
        // 5=Hardware disabled, 6=Hardware malfunction, 7=Media disconnected, 8=Authenticating, …
        try
        {
            var status = Convert.ToUInt16(mo["NetConnectionStatus"] ?? 0);
            // 4/5 typically mean disabled / not present.
            if (status is 4 or 5)
            {
                return false;
            }

            // 2 connected, 7 media disconnected (unplugged cable), 1/3/8 transitional — treat as enabled.
            if (status is 1 or 2 or 3 or 7 or 8 or 9 or 10)
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TryWmiSetEnabled(NetworkAdapterInfo adapter, bool enabled, out string? error)
    {
        error = null;
        var method = enabled ? "Enable" : "Disable";
        uint? lastResult = null;

        try
        {
            // Prefer NetConnectionID (ncpa.cpl alias) — "Wi-Fi" / "Ethernet".
            if (InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID = '" + EscapeWmi(adapter.Name) + "'",
                    method,
                    out lastResult))
            {
                return true;
            }

            if (InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE DeviceID = '" + EscapeWmi(adapter.DeviceId) + "'",
                    method,
                    out lastResult))
            {
                return true;
            }

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
                // 0 = success. 5 = often "already in requested state" on consumer NICs.
                // 84 / others may still succeed for media-disconnected adapters — caller re-checks NetEnabled.
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
        // Quote the ncpa.cpl alias exactly (e.g. name="Wi-Fi" / name="Ethernet").
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
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException($"netsh timed out while {(enabled ? "enabling" : "disabling")} '{interfaceName}'.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
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
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
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
        NetworkInterfaceType? networkInterfaceType,
        bool forceIgnored = false)
    {
        var haystack = BuildHaystack(connectionName, description, name, manufacturer, pnpDeviceId);

        // 1) Virtual / VPN / Bluetooth / TAP ALWAYS win — never call them Ethernet via NIC type.
        if (forceIgnored || LooksVirtualOrNonPhysical(haystack))
        {
            return AdapterKind.Other;
        }

        // 2) Canonical Windows aliases are authoritative.
        if (WifiAliasRegex.IsMatch(connectionName.Trim()) || LooksWireless(haystack))
        {
            return AdapterKind.Wireless;
        }

        if (EthernetAliasRegex.IsMatch(connectionName.Trim()))
        {
            return AdapterKind.Ethernet;
        }

        // 3) .NET interface type (only after virtual + alias checks).
        var fromNic = ClassifyFromNetworkInterfaceType(networkInterfaceType);
        if (fromNic is AdapterKind.Wireless)
        {
            return AdapterKind.Wireless;
        }

        if (LooksWireless(haystack))
        {
            return AdapterKind.Wireless;
        }

        if (adapterTypeId == 9)
        {
            return AdapterKind.Wireless;
        }

        if (fromNic is AdapterKind.Ethernet)
        {
            return AdapterKind.Ethernet;
        }

        if (adapterTypeId == 0)
        {
            return AdapterKind.Ethernet;
        }

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

    internal static bool LooksWireless(string haystackLower)
    {
        ReadOnlySpan<string> tokens =
        [
            "wi-fi", "wifi", "wi‑fi", "wireless", "wlan", "802.11", "80211",
            "wi fi", "wi-fi 6", "wi-fi 6e", "wi-fi 7", "wifi 6", "wifi 6e", "wifi 7",
            "8922ae", "rtl8922", "8852", "rtl8852", "8822", "rtl8822", "8821",
            "mt792", "mt76", "mediatek", "media tek",
            "ax201", "ax210", "ax211", "ax411", "be200", "be202",
            "killer wi", "killer(r) wi", "qualcomm", "atheros", "qca6", "qcn",
            "broadcom 802", "bcm43", "intel(r) wi", "intel® wi", "intel wifi",
            "wireless-ac", "wireless-ax", "wireless ae", "pcie wifi", "wifi 7 pcie",
            "wifi 7 pci-e", "wifi 7 pci"
        ];

        foreach (var token in tokens)
        {
            if (haystackLower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Realtek 8922AE WiFi 7 PCI-E NIC (user's exact description spelling "WiFi" without hyphen).
        if (haystackLower.Contains("8922", StringComparison.Ordinal) &&
            (haystackLower.Contains("wifi", StringComparison.Ordinal) ||
             haystackLower.Contains("wi-fi", StringComparison.Ordinal) ||
             haystackLower.Contains("wireless", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool LooksEthernet(string haystackLower)
    {
        // Do NOT match bare "local area connection" — OpenVPN TAP often uses that name.
        ReadOnlySpan<string> tokens =
        [
            "ethernet", "gigabit", "2.5gbe", "2.5gb", "2.5 gbe", "5gbe", "10gbe", "10 gigabit",
            "realtek pcie gb", "realtek gaming", "realtek usb gb", "realtek pcie 2.5",
            "intel(r) ethernet", "intel® ethernet",
            "killer ethernet", "marvell", "broadcom netxtreme", "nvidia nforce",
            "lan port", "rj45", "family controller"
        ];

        foreach (var token in tokens)
        {
            if (haystackLower.Contains(token, StringComparison.Ordinal))
            {
                // "family controller" only if ethernet-ish context
                if (token is "family controller" &&
                    !haystackLower.Contains("ethernet", StringComparison.Ordinal) &&
                    !haystackLower.Contains("gbe", StringComparison.Ordinal) &&
                    !haystackLower.Contains("gigabit", StringComparison.Ordinal))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    internal static bool LooksVirtualOrNonPhysical(string haystackLower)
    {
        ReadOnlySpan<string> tokens =
        [
            "bluetooth", "virtual", "hyper-v", "vethernet", "vpn", "tap-", "tap adapter",
            "tap-windows", "tapwindows", "openvpn", "open vpn", "dco adapter", "wireguard",
            "nordlynx", "loopback", "pseudo", "wsl", "docker", "npcap",
            "cisco anyconnect", "virtualbox", "vmware", "host-only", "kernel debug",
            "isatap", "teredo", "microsoft wi-fi direct", "hosted network"
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
