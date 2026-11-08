using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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

    // After NormalizeConnectionName, Unicode hyphens become ASCII '-' so "Wi‑Fi" matches.
    private static readonly Regex WifiAliasRegex = new(
        @"^(wi[-\s]?fi|wifi|wlan)(\s+\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        // Always merge WMI + NetworkInterface. Some Realtek Wi‑Fi 7 / 2.5GbE stacks omit a
        // NetConnectionID in one API while ncpa.cpl still shows "Wi-Fi" / "Ethernet".
        var byName = new Dictionary<string, NetworkAdapterInfo>(StringComparer.OrdinalIgnoreCase);
        var nicByIndex = BuildNetworkInterfaceMap();

        foreach (var adapter in EnumerateFromWmi(nicByIndex))
        {
            UpsertAdapter(byName, adapter);
        }

        foreach (var adapter in EnumerateFromNetworkInterfaces())
        {
            UpsertAdapter(byName, adapter);
        }

        return byName.Values
            .OrderBy(a => KindSortOrder(a.Kind))
            .ThenBy(a => a.IsVirtualOrIgnored)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void UpsertAdapter(Dictionary<string, NetworkAdapterInfo> byName, NetworkAdapterInfo adapter)
    {
        var key = NormalizeConnectionName(adapter.Name);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!byName.TryGetValue(key, out var existing))
        {
            byName[key] = adapter;
            return;
        }

        byName[key] = PreferRicher(existing, adapter);
    }

    private static NetworkAdapterInfo PreferRicher(NetworkAdapterInfo a, NetworkAdapterInfo b)
    {
        // Prefer the richer classification, but always keep a numeric WMI DeviceID when either side has one.
        // NetworkInterface.Id is a NetCfg GUID and must not replace WMI DeviceID for Enable/Disable.
        var primary = RichnessScore(b) > RichnessScore(a) ? b : a;
        var secondary = ReferenceEquals(primary, a) ? b : a;

        return new NetworkAdapterInfo
        {
            DeviceId = PreferNumericDeviceId(primary.DeviceId, secondary.DeviceId),
            Name = primary.Name,
            Description = primary.Description ?? secondary.Description,
            Kind = primary.Kind != AdapterKind.Other && primary.Kind != AdapterKind.Unknown
                ? primary.Kind
                : secondary.Kind,
            IsEnabled = primary.IsEnabled || secondary.IsEnabled,
            IsConnected = primary.IsConnected || secondary.IsConnected,
            InterfaceIndex = primary.InterfaceIndex ?? secondary.InterfaceIndex,
            PnpDeviceId = primary.PnpDeviceId ?? secondary.PnpDeviceId,
            NetCfgInstanceId = primary.NetCfgInstanceId ?? secondary.NetCfgInstanceId
                               ?? AsNetCfgGuid(primary.DeviceId) ?? AsNetCfgGuid(secondary.DeviceId),
            IsVirtualOrIgnored = primary.IsVirtualOrIgnored && secondary.IsVirtualOrIgnored
        };
    }

    private static int RichnessScore(NetworkAdapterInfo a)
    {
        var score = 0;
        if (!a.IsVirtualOrIgnored) score += 8;
        if (a.Kind is AdapterKind.Ethernet or AdapterKind.Wireless) score += 4;
        if (a.InterfaceIndex is not null) score += 2;
        // Strongly prefer WMI numeric DeviceID over NetworkInterface GUID.
        if (uint.TryParse(a.DeviceId, out _)) score += 10;
        if (!string.IsNullOrWhiteSpace(a.NetCfgInstanceId)) score += 1;
        if (a.IsEnabled) score += 1;
        if (a.IsConnected) score += 1;
        return score;
    }

    private static string PreferNumericDeviceId(string primary, string secondary) =>
        uint.TryParse(primary, out _) ? primary
        : uint.TryParse(secondary, out _) ? secondary
        : primary;

    private static string? AsNetCfgGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Trim('{', '}');
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("B") : null;
    }

    private static IEnumerable<NetworkAdapterInfo> EnumerateFromWmi(Dictionary<uint, NetworkInterface> nicByIndex)
    {
        IEnumerable<ManagementObject> rows;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, NetConnectionID, NetEnabled, NetConnectionStatus, AdapterTypeId, InterfaceIndex, Description, PNPDeviceID, ProductName, Manufacturer, PhysicalAdapter, GUID " +
                "FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
            // Materialize inside try so a late enumerator failure still falls through cleanly.
            rows = searcher.Get().Cast<ManagementObject>().ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var mo in rows)
        {
            using (mo)
            {
                // Keep the raw NetConnectionID for WMI/netsh; normalize only when comparing.
                var connectionName = mo["NetConnectionID"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(connectionName))
                {
                    continue;
                }

                var deviceId = mo["DeviceID"]?.ToString()?.Trim() ?? connectionName;
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
                var guid = AsNetCfgGuid(mo["GUID"]?.ToString());
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
                    matchedNic = nicByIndex.Values.FirstOrDefault(n => NamesEqual(n.Name, connectionName));
                }

                guid ??= AsNetCfgGuid(matchedNic?.Id);

                yield return BuildAdapterInfo(
                    connectionName,
                    deviceId,
                    description ?? name,
                    name,
                    manufacturer,
                    pnp,
                    enabled,
                    connected,
                    ifIndex,
                    adapterTypeId,
                    physical,
                    matchedNic?.NetworkInterfaceType,
                    guid);
            }
        }
    }

    private static IEnumerable<NetworkAdapterInfo> EnumerateFromNetworkInterfaces()
    {
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            yield break;
        }

        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            uint? ifIndex = null;
            try
            {
                var props = nic.GetIPProperties()?.GetIPv4Properties();
                if (props is not null)
                {
                    ifIndex = (uint)props.Index;
                }
            }
            catch
            {
                // ignore
            }

            var guid = AsNetCfgGuid(nic.Id);
            yield return BuildAdapterInfo(
                connectionName: nic.Name,
                deviceId: nic.Id,
                description: nic.Description,
                hardwareName: nic.Name,
                manufacturer: null,
                pnp: null,
                enabled: nic.OperationalStatus != OperationalStatus.NotPresent,
                connected: nic.OperationalStatus == OperationalStatus.Up,
                ifIndex: ifIndex,
                adapterTypeId: null,
                physical: null,
                networkInterfaceType: nic.NetworkInterfaceType,
                netCfgInstanceId: guid);
        }
    }

    private static NetworkAdapterInfo BuildAdapterInfo(
        string connectionName,
        string deviceId,
        string? description,
        string hardwareName,
        string? manufacturer,
        string? pnp,
        bool enabled,
        bool connected,
        uint? ifIndex,
        ushort? adapterTypeId,
        bool? physical,
        NetworkInterfaceType? networkInterfaceType,
        string? netCfgInstanceId)
    {
        var haystack = BuildHaystack(connectionName, description, hardwareName, manufacturer, pnp);
        var clearWifi = AliasScore(connectionName, wifi: true) >= 90;
        var clearEth = AliasScore(connectionName, wifi: false) >= 90;
        var clearAlias = clearWifi || clearEth;
        // Exact ncpa.cpl aliases are never auto-ignored unless the alias itself is Bluetooth/TAP/VPN.
        var nameLooksVirtual = LooksVirtualOrNonPhysical(NormalizeConnectionName(connectionName).ToLowerInvariant());
        var haystackLooksVirtual = LooksVirtualOrNonPhysical(haystack);
        var ignored = clearAlias
            ? nameLooksVirtual
            : haystackLooksVirtual || physical == false;

        var kind = Classify(
            connectionName,
            description,
            hardwareName,
            manufacturer,
            pnp,
            adapterTypeId,
            networkInterfaceType,
            forceIgnored: ignored && !clearAlias);

        // Re-assert kind for clear aliases even if a description token looked "virtual".
        if (clearWifi)
        {
            kind = AdapterKind.Wireless;
        }
        else if (clearEth)
        {
            kind = AdapterKind.Ethernet;
        }

        return new NetworkAdapterInfo
        {
            DeviceId = deviceId,
            Name = connectionName,
            Description = description ?? hardwareName,
            Kind = kind,
            IsEnabled = enabled,
            IsConnected = connected,
            InterfaceIndex = ifIndex,
            PnpDeviceId = pnp,
            NetCfgInstanceId = netCfgInstanceId ?? AsNetCfgGuid(deviceId),
            IsVirtualOrIgnored = ignored
        };
    }

    public NetworkAdapterInfo? FindByNameOrId(string? nameOrId) =>
        FindByNameOrId(GetAdapters(), nameOrId);

    public NetworkAdapterInfo? FindByNameOrId(IReadOnlyList<NetworkAdapterInfo> adapters, string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId) || adapters.Count == 0)
        {
            return null;
        }

        var needle = NormalizeConnectionName(nameOrId);
        if (string.IsNullOrWhiteSpace(needle))
        {
            return null;
        }

        // Prefer NetConnectionID / alias match over DeviceID (indices can reshuffle after reboot/driver update).
        return adapters.FirstOrDefault(a => NamesEqual(a.Name, needle))
               ?? adapters.FirstOrDefault(a =>
                   string.Equals(a.DeviceId?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
               ?? adapters.FirstOrDefault(a =>
                   a.Description is not null && NamesEqual(a.Description, needle))
               ?? adapters.FirstOrDefault(a =>
                   a.Description is not null &&
                   a.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
                   needle.Length >= 5);
    }

    /// <summary>
    /// Auto-pick or repair LAN/Wi‑Fi roles. Prefers aliases "Ethernet" / "Wi-Fi".
    /// Ignores Bluetooth, TAP, OpenVPN, and other virtual NICs. Does not require a cable link.
    /// </summary>
    public void AutoSelectDefaults(AppConfig config, bool repairUnsuitable = true)
    {
        var adapters = GetAdapters();

        // 1) Strongest signal: exact ncpa.cpl aliases, even if Kind was previously wrong.
        var ethernet = FindCanonicalAlias(adapters, wifi: false);
        var wifi = FindCanonicalAlias(adapters, wifi: true);

        // 2) Honor / repair saved config when aliases were missing.
        ethernet ??= ResolveRole(adapters, config.EthernetAdapterId, config.EthernetAdapterName, AdapterKind.Ethernet);
        wifi ??= ResolveRole(adapters, config.WifiAdapterId, config.WifiAdapterName, AdapterKind.Wireless);

        // 3) Description / chipset fallbacks (Realtek 8922AE, Realtek 2.5GbE, …).
        ethernet ??= PickBest(adapters, AdapterKind.Ethernet, preferWifiAlias: false)
                     ?? ResolveByEthernetDescription(adapters);
        wifi ??= PickBest(adapters, AdapterKind.Wireless, preferWifiAlias: true)
                 ?? ResolveByWifiChipset(adapters);

        if (IsSuitableLan(ethernet) && (wifi is null || !NamesEqual(ethernet!.Name, wifi.Name)))
        {
            config.EthernetAdapterId = ethernet!.DeviceId;
            config.EthernetAdapterName = ethernet.Name;
        }
        else if (repairUnsuitable)
        {
            config.EthernetAdapterId = null;
            config.EthernetAdapterName = null;
        }

        if (IsSuitableWifi(wifi) && (ethernet is null || !NamesEqual(wifi!.Name, ethernet.Name)))
        {
            config.WifiAdapterId = wifi!.DeviceId;
            config.WifiAdapterName = wifi.Name;
        }
        else if (repairUnsuitable)
        {
            config.WifiAdapterId = null;
            config.WifiAdapterName = null;
        }
    }

    /// <summary>Human-readable snapshot used by Auto-detect failure dialogs.</summary>
    public string FormatAdapterDiagnostics(IReadOnlyList<NetworkAdapterInfo>? adapters = null)
    {
        adapters ??= GetAdapters();
        if (adapters.Count == 0)
        {
            return "No adapters returned by WMI/NetworkInterface.";
        }

        var lines = adapters.Select(a =>
        {
            var flags = a.IsVirtualOrIgnored ? "ignore" : "ok";
            return $"- {a.Name} | {a.KindLabel} | {a.StatusLabel} | {flags} | {TruncateDiag(a.Description, 48)}";
        });
        return string.Join(Environment.NewLine, lines);
    }

    private static string TruncateDiag(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(no description)";
        }

        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    public ConnectionMode DetectCurrentMode(AppConfig config)
    {
        var adapters = GetAdapters();
        var ethernet = ResolveRole(adapters, config.EthernetAdapterId, config.EthernetAdapterName, AdapterKind.Ethernet);
        var wifi = ResolveRole(adapters, config.WifiAdapterId, config.WifiAdapterName, AdapterKind.Wireless);

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
        // Repair stale TAP/VPN picks / DeviceID reshuffles before toggling.
        AutoSelectDefaults(config, repairUnsuitable: true);
        config.Save();

        var adapters = GetAdapters();
        var ethernet = ResolveRequired(adapters, config.EthernetAdapterId, config.EthernetAdapterName, "Ethernet / LAN", AdapterKind.Ethernet);
        var wifi = ResolveRequired(adapters, config.WifiAdapterId, config.WifiAdapterName, "Wi‑Fi", AdapterKind.Wireless);

        var mode = DetectCurrentMode(config);
        var target = mode == ConnectionMode.Wifi ? ConnectionMode.Lan : ConnectionMode.Wifi;

        ApplyTarget(ethernet, wifi, target);
        Thread.Sleep(400);

        // Prefer the intended target when disable was best-effort but traffic already moved.
        var detected = DetectCurrentMode(config);
        return PreferTargetIfTrafficMoved(target, detected, ethernet.Name, wifi.Name);
    }

    public void SwitchTo(AppConfig config, ConnectionMode target)
    {
        if (target is not (ConnectionMode.Lan or ConnectionMode.Wifi))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        AutoSelectDefaults(config, repairUnsuitable: true);
        config.Save();

        var adapters = GetAdapters();
        var ethernet = ResolveRequired(adapters, config.EthernetAdapterId, config.EthernetAdapterName, "Ethernet / LAN", AdapterKind.Ethernet);
        var wifi = ResolveRequired(adapters, config.WifiAdapterId, config.WifiAdapterName, "Wi‑Fi", AdapterKind.Wireless);
        ApplyTarget(ethernet, wifi, target);
        Thread.Sleep(400);
    }

    private void ApplyTarget(NetworkAdapterInfo ethernet, NetworkAdapterInfo wifi, ConnectionMode target)
    {
        // Enable the destination first so the machine is never left with both down mid-toggle.
        // Then best-effort disable the source (Realtek Wi‑Fi Disable often returns non-zero even after success).
        var targetAdapter = target == ConnectionMode.Lan ? ethernet : wifi;
        var otherAdapter = target == ConnectionMode.Lan ? wifi : ethernet;

        SetEnabled(targetAdapter, enabled: true, bestEffort: false);
        Thread.Sleep(300);

        // Fire disable, but do not trust an immediate "still active" read — Windows route
        // updates (and Realtek admin state) often settle ~0.5–1.5s later.
        _ = TryDisableWithRetry(otherAdapter, retries: 2);

        if (WaitForSwitchSuccess(target, targetAdapter.Name, otherAdapter.Name, timeoutMs: 2000))
        {
            return;
        }

        var targetFresh = FindByNameOrId(targetAdapter.Name) ?? targetAdapter;
        var otherFresh = FindByNameOrId(otherAdapter.Name) ?? otherAdapter;

        if (!targetFresh.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Could not enable '{targetAdapter.Name}' (id {targetAdapter.DeviceId}). " +
                "Confirm the app is elevated (UAC) and the name matches ncpa.cpl.");
        }

        // Last chance: target up and carrying / preferred over the old path.
        if (IsTrafficOnTarget(target, targetFresh, otherFresh))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Switched toward {(target == ConnectionMode.Lan ? "LAN" : "Wi‑Fi")}, but '{otherAdapter.Name}' " +
            $"(id {otherAdapter.DeviceId}) is still the active path. " +
            "Try Choose adapters… or disable it once in ncpa.cpl.");
    }

    /// <summary>
    /// Poll for up to <paramref name="timeoutMs"/> while Windows finishes Disable + route update.
    /// Success when the target is enabled and is (or becomes) the preferred / default path.
    /// </summary>
    private bool WaitForSwitchSuccess(
        ConnectionMode target,
        string targetName,
        string otherName,
        int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var targetFresh = FindByNameOrId(targetName);
            var otherFresh = FindByNameOrId(otherName);
            if (targetFresh is not null &&
                otherFresh is not null &&
                targetFresh.IsEnabled &&
                IsTrafficOnTarget(target, targetFresh, otherFresh))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            // Keep nudging disable while we wait — slow Realtek Wi‑Fi Disable is common.
            if (otherFresh is { IsEnabled: true })
            {
                _ = SetEnabled(otherFresh, enabled: false, bestEffort: true);
            }

            Thread.Sleep(250);
        }
    }

    private bool TryDisableWithRetry(NetworkAdapterInfo adapter, int retries)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            if (SetEnabled(adapter, enabled: false, bestEffort: true))
            {
                return true;
            }

            Thread.Sleep(300);
            var refreshed = FindByNameOrId(adapter.Name);
            if (refreshed is { IsEnabled: false })
            {
                return true;
            }

            // Wi‑Fi may stay "enabled" briefly while NetConnectionStatus already shows disconnected.
            if (refreshed is { IsConnected: false, Kind: AdapterKind.Wireless })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the destination adapter is up and the old one is no longer carrying traffic
    /// (disabled, Wi‑Fi disconnected, Ethernet connected, or default route moved).
    /// </summary>
    private static bool IsTrafficOnTarget(
        ConnectionMode target,
        NetworkAdapterInfo targetFresh,
        NetworkAdapterInfo otherFresh)
    {
        if (!targetFresh.IsEnabled)
        {
            return false;
        }

        if (!otherFresh.IsEnabled)
        {
            return true;
        }

        // Strongest signal: Windows best interface / default route already prefers the target.
        if (IsLikelyDefaultRoute(targetFresh) && !IsLikelyDefaultRoute(otherFresh))
        {
            return true;
        }

        if (IsBestInterface(targetFresh))
        {
            return true;
        }

        if (target == ConnectionMode.Lan)
        {
            // LAN up (cable optional). Success if Wi‑Fi is disconnected / not associated.
            if (!otherFresh.IsConnected)
            {
                return true;
            }

            if (targetFresh.IsConnected && !otherFresh.IsConnected)
            {
                return true;
            }

            // Ethernet has link and a gateway even while Wi‑Fi is still draining.
            if (targetFresh.IsConnected && HasIpv4Gateway(targetFresh))
            {
                return true;
            }

            return false;
        }

        // Target Wi‑Fi: Ethernet disabled, or unplugged while Wi‑Fi is associated.
        if (!otherFresh.IsConnected && targetFresh.IsConnected)
        {
            return true;
        }

        if (targetFresh.IsConnected && !otherFresh.IsEnabled)
        {
            return true;
        }

        if (targetFresh.IsConnected && HasIpv4Gateway(targetFresh) && !HasIpv4Gateway(otherFresh))
        {
            return true;
        }

        return false;
    }

    private static bool IsLikelyDefaultRoute(NetworkAdapterInfo adapter) =>
        HasIpv4Gateway(adapter) && adapter.IsEnabled &&
        (adapter.IsConnected || adapter.Kind == AdapterKind.Ethernet);

    private static bool HasIpv4Gateway(NetworkAdapterInfo adapter)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!NamesEqual(nic.Name, adapter.Name) &&
                    (adapter.NetCfgInstanceId is null ||
                     !string.Equals(AsNetCfgGuid(nic.Id), AsNetCfgGuid(adapter.NetCfgInstanceId), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (nic.OperationalStatus is OperationalStatus.Down or OperationalStatus.NotPresent)
                {
                    return false;
                }

                return nic.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.Any.Equals(g.Address) &&
                    !IPAddress.None.Equals(g.Address));
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool IsBestInterface(NetworkAdapterInfo adapter)
    {
        if (adapter.InterfaceIndex is not uint ifIndex)
        {
            return false;
        }

        try
        {
            // 1.1.1.1 — ask the stack which interface would carry general Internet traffic.
            var dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
            if (GetBestInterface(dest, out var best) == 0 && best == ifIndex)
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

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    private ConnectionMode PreferTargetIfTrafficMoved(
        ConnectionMode target,
        ConnectionMode detected,
        string ethernetName,
        string wifiName)
    {
        if (detected == target || detected is ConnectionMode.Unknown or ConnectionMode.Neither)
        {
            return detected == ConnectionMode.Unknown ? target : detected;
        }

        var ethernet = FindByNameOrId(ethernetName);
        var wifi = FindByNameOrId(wifiName);
        if (ethernet is null || wifi is null)
        {
            return detected;
        }

        if (target == ConnectionMode.Lan &&
            IsTrafficOnTarget(ConnectionMode.Lan, ethernet, wifi))
        {
            return ConnectionMode.Lan;
        }

        if (target == ConnectionMode.Wifi &&
            IsTrafficOnTarget(ConnectionMode.Wifi, wifi, ethernet))
        {
            return ConnectionMode.Wifi;
        }

        return detected;
    }

    public void SetEnabled(NetworkAdapterInfo adapter, bool enabled) =>
        SetEnabled(adapter, enabled, bestEffort: false);

    /// <summary>
    /// Enable/disable an adapter. When <paramref name="bestEffort"/> is true, failures return false
    /// instead of throwing (used when disabling the previous path after the target is already up).
    /// </summary>
    public bool SetEnabled(NetworkAdapterInfo adapter, bool enabled, bool bestEffort)
    {
        // Already in desired admin state — success even if cable is unplugged.
        if (adapter.IsEnabled == enabled)
        {
            return true;
        }

        var errors = new List<string>();

        if (TryWmiSetEnabled(adapter, enabled, out var wmiError))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(wmiError))
        {
            errors.Add(wmiError);
        }

        try
        {
            RunNetsh(adapter.Name, enabled);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        // Re-check: some drivers report odd WMI codes but still flip NetEnabled / disconnect media.
        var refreshed = FindByNameOrId(adapter.Name);
        if (refreshed is not null && refreshed.IsEnabled == enabled)
        {
            return true;
        }

        if (!enabled && refreshed is { IsConnected: false })
        {
            // Disable "failed" but the NIC is already media-disconnected — treat as OK.
            return true;
        }

        if (bestEffort)
        {
            return false;
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
        var adapters = GetAdapters();
        return ResolveRole(adapters, id, name, preferredKind: null);
    }

    /// <summary>
    /// Resolve saved config by NetConnectionID first, then DeviceID.
    /// If that fails (or points at the wrong kind / a virtual NIC), re-resolve by
    /// canonical alias ("Wi-Fi" / "Ethernet") and Wi‑Fi chipset hints.
    /// </summary>
    private NetworkAdapterInfo? ResolveRole(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? id,
        string? name,
        AdapterKind? preferredKind)
    {
        // Alias / NetConnectionID first, then DeviceID (indices reshuffle across boots).
        NetworkAdapterInfo? byConfig = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            byConfig = FindByNameOrId(adapters, name);
        }

        byConfig ??= FindByNameOrId(adapters, id);

        if (byConfig is not null && IsSuitableForRole(byConfig, preferredKind))
        {
            return byConfig;
        }

        // Saved config missed or unsuitable — re-resolve by role.
        if (preferredKind == AdapterKind.Wireless)
        {
            return ResolveByCanonicalAlias(adapters, wifi: true)
                   ?? ResolveByWifiChipset(adapters)
                   ?? PickBest(adapters, AdapterKind.Wireless, preferWifiAlias: true);
        }

        if (preferredKind == AdapterKind.Ethernet)
        {
            return ResolveByCanonicalAlias(adapters, wifi: false)
                   ?? PickBest(adapters, AdapterKind.Ethernet, preferWifiAlias: false);
        }

        return byConfig;
    }

    private static bool IsSuitableForRole(NetworkAdapterInfo adapter, AdapterKind? preferredKind) =>
        preferredKind switch
        {
            AdapterKind.Wireless => IsSuitableWifi(adapter),
            AdapterKind.Ethernet => IsSuitableLan(adapter),
            _ => !adapter.IsVirtualOrIgnored
        };

    private NetworkAdapterInfo ResolveRequired(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? id,
        string? name,
        string label,
        AdapterKind preferredKind)
    {
        var adapter = ResolveRole(adapters, id, name, preferredKind);
        if (adapter is null)
        {
            throw new InvalidOperationException(BuildNotFoundMessage(adapters, id, name, label, preferredKind));
        }

        return adapter;
    }

    private static string BuildNotFoundMessage(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? id,
        string? name,
        string label,
        AdapterKind preferredKind)
    {
        var searched = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            searched.Add($"saved name '{NormalizeConnectionName(name)}'");
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            searched.Add($"saved id '{id.Trim()}'");
        }

        if (preferredKind == AdapterKind.Wireless)
        {
            searched.Add("alias 'Wi-Fi' / 'WiFi' / 'WLAN'");
            searched.Add("description hints (Realtek 8922AE, WiFi 7, 802.11, …)");
        }
        else if (preferredKind == AdapterKind.Ethernet)
        {
            searched.Add("alias 'Ethernet'");
        }

        var searchedText = searched.Count == 0
            ? "nothing configured yet"
            : string.Join("; ", searched);

        var visible = adapters
            .Where(a => !a.IsVirtualOrIgnored)
            .Select(a => $"{a.Name} [{a.KindLabel}, id={a.DeviceId}]")
            .ToList();
        var ignored = adapters
            .Where(a => a.IsVirtualOrIgnored)
            .Select(a => a.Name)
            .ToList();

        var foundText = visible.Count == 0
            ? "no physical adapters were returned by WMI/NetworkInterface"
            : "found: " + string.Join(", ", visible);
        if (ignored.Count > 0)
        {
            foundText += "; ignored: " + string.Join(", ", ignored);
        }

        return
            $"{label} adapter was not found. Searched {searchedText}. {foundText}. " +
            "Open Choose adapters… and pick the ncpa.cpl aliases \"Ethernet\" and \"Wi-Fi\" " +
            "(ignore Bluetooth / TAP / OpenVPN).";
    }

    private static NetworkAdapterInfo? ResolveByCanonicalAlias(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        bool wifi) =>
        FindCanonicalAlias(adapters, wifi);

    /// <summary>
    /// Prefer exact ncpa.cpl aliases ("Wi-Fi", "Ethernet") even when Kind/ignore flags are wrong.
    /// Only skips aliases whose own name looks like Bluetooth/TAP/VPN.
    /// </summary>
    private static NetworkAdapterInfo? FindCanonicalAlias(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        bool wifi)
    {
        return adapters
            .Where(a => AliasScore(a.Name, wifi) >= 90)
            .Where(a => !LooksVirtualOrNonPhysical(NormalizeConnectionName(a.Name).ToLowerInvariant()))
            .OrderByDescending(a => AliasScore(a.Name, wifi))
            .ThenByDescending(a => !a.IsVirtualOrIgnored)
            .ThenByDescending(a => a.IsEnabled)
            .ThenByDescending(a => a.IsConnected)
            .FirstOrDefault();
    }

    private static NetworkAdapterInfo? ResolveByWifiChipset(IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        return adapters
            .Where(a => !a.IsVirtualOrIgnored || AliasScore(a.Name, wifi: true) >= 90)
            .Where(a => !LooksVirtualOrNonPhysical(NormalizeConnectionName(a.Name).ToLowerInvariant()))
            .Where(a =>
            {
                var hay = BuildHaystack(a);
                return LooksWireless(hay);
            })
            .OrderByDescending(a => AliasScore(a.Name, wifi: true))
            .ThenByDescending(a => a.Kind == AdapterKind.Wireless)
            .ThenByDescending(a => a.IsEnabled)
            .ThenByDescending(a => a.IsConnected)
            .FirstOrDefault();
    }

    private static NetworkAdapterInfo? ResolveByEthernetDescription(IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        return adapters
            .Where(a => !a.IsVirtualOrIgnored || AliasScore(a.Name, wifi: false) >= 90)
            .Where(a => !LooksVirtualOrNonPhysical(NormalizeConnectionName(a.Name).ToLowerInvariant()))
            .Where(a => LooksEthernet(BuildHaystack(a)) && !LooksWireless(BuildHaystack(a)))
            .OrderByDescending(a => AliasScore(a.Name, wifi: false))
            .ThenByDescending(a => a.Kind == AdapterKind.Ethernet)
            .ThenByDescending(a => a.IsEnabled)
            .ThenByDescending(a => a.IsConnected)
            .FirstOrDefault();
    }

    private static NetworkAdapterInfo? PickBest(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        AdapterKind kind,
        bool preferWifiAlias)
    {
        return adapters
            .Where(a =>
                (!a.IsVirtualOrIgnored || AliasScore(a.Name, preferWifiAlias) >= 90) &&
                (a.Kind == kind || AliasScore(a.Name, preferWifiAlias) >= 90))
            .Where(a => !LooksVirtualOrNonPhysical(NormalizeConnectionName(a.Name).ToLowerInvariant()))
            .OrderByDescending(a => AliasScore(a.Name, preferWifiAlias))
            .ThenByDescending(a => a.IsEnabled)   // prefer currently enabled; cable optional
            .ThenByDescending(a => a.IsConnected) // nice-to-have only
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsSuitableLan(NetworkAdapterInfo? adapter) =>
        adapter is not null
        && !LooksVirtualOrNonPhysical(NormalizeConnectionName(adapter.Name).ToLowerInvariant())
        && (adapter.Kind == AdapterKind.Ethernet || AliasScore(adapter.Name, wifi: false) >= 90)
        && (adapter is { IsVirtualOrIgnored: false } || AliasScore(adapter.Name, wifi: false) >= 90);

    private static bool IsSuitableWifi(NetworkAdapterInfo? adapter) =>
        adapter is not null
        && !LooksVirtualOrNonPhysical(NormalizeConnectionName(adapter.Name).ToLowerInvariant())
        && (adapter.Kind == AdapterKind.Wireless || AliasScore(adapter.Name, wifi: true) >= 90)
        && (adapter is { IsVirtualOrIgnored: false } || AliasScore(adapter.Name, wifi: true) >= 90);

    /// <summary>
    /// Higher = better match for canonical Windows connection names.
    /// Exact "Ethernet" / "Wi-Fi" beat hardware description strings.
    /// </summary>
    internal static int AliasScore(string connectionName, bool wifi)
    {
        var n = NormalizeConnectionName(connectionName);
        if (string.IsNullOrEmpty(n))
        {
            return 0;
        }

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

    /// <summary>
    /// Normalize ncpa.cpl / WMI NetConnectionID quirks: Unicode hyphens, NBSP, ZWSP, trim, collapse spaces.
    /// Windows 11 often shows "Wi-Fi"; some APIs return "Wi‑Fi" (U+2011) which breaks OrdinalIgnoreCase.
    /// </summary>
    internal static string NormalizeConnectionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim().Trim('\uFEFF'))
        {
            // Drop zero-width / format chars that break ^Wi-Fi$ alias matching.
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD')
            {
                continue;
            }

            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            var mapped = ch switch
            {
                '\u00A0' or '\u202F' or '\u2007' => ' ', // NBSP / NNBSP / figure space
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => '-', // hyphens/dashes/minus
                '\t' or '\r' or '\n' => ' ',
                _ => ch
            };

            if (char.IsWhiteSpace(mapped))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(mapped);
        }

        return sb.ToString();
    }

    internal static bool NamesEqual(string? left, string? right) =>
        string.Equals(
            NormalizeConnectionName(left),
            NormalizeConnectionName(right),
            StringComparison.OrdinalIgnoreCase);

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
        var invoked = false;

        try
        {
            // 1) NetConnectionID (normalized) — most reliable for ncpa.cpl "Wi-Fi" / "Ethernet".
            if (InvokeWmiByConnectionName(adapter.Name, method, out lastResult, out var byName))
            {
                return true;
            }

            invoked |= byName;

            // 2) Numeric WMI DeviceID only (GUID DeviceId would miss Win32_NetworkAdapter.DeviceID).
            var byDeviceId = false;
            if (uint.TryParse(adapter.DeviceId, out _) &&
                InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE DeviceID = '" + EscapeWmi(adapter.DeviceId) + "'",
                    method,
                    out lastResult,
                    out byDeviceId))
            {
                return true;
            }

            invoked |= byDeviceId;

            // 3) NetCfgInstanceId / Win32_NetworkAdapter.GUID (braced).
            var byGuid = false;
            var guid = AsNetCfgGuid(adapter.NetCfgInstanceId) ?? AsNetCfgGuid(adapter.DeviceId);
            if (guid is not null &&
                InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE GUID = '" + EscapeWmi(guid) + "'",
                    method,
                    out lastResult,
                    out byGuid))
            {
                return true;
            }

            invoked |= byGuid;

            // 4) InterfaceIndex
            var byIndex = false;
            if (adapter.InterfaceIndex is uint idx &&
                InvokeWmiByQuery(
                    "SELECT * FROM Win32_NetworkAdapter WHERE InterfaceIndex = " + idx,
                    method,
                    out lastResult,
                    out byIndex))
            {
                return true;
            }

            invoked |= byIndex;

            // Realtek often returns non-0/5 while the flip is still in flight — recheck admin state.
            if (invoked)
            {
                Thread.Sleep(250);
                if (IsAdapterInDesiredState(adapter.Name, enabled))
                {
                    return true;
                }
            }

            error = lastResult is null
                ? "WMI: adapter object not found by name/id/guid/index."
                : $"WMI {method} returned code {lastResult} (0=ok, 5=already).";
        }
        catch (Exception ex)
        {
            error = $"WMI error: {ex.Message}";
            return false;
        }

        return false;
    }

    private static bool IsAdapterInDesiredState(string connectionName, bool enabled)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT NetConnectionID, NetEnabled, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (!NamesEqual(mo["NetConnectionID"]?.ToString(), connectionName))
                    {
                        continue;
                    }

                    return ReadNetEnabled(mo) == enabled ||
                           (!enabled && Convert.ToUInt16(mo["NetConnectionStatus"] ?? 0) is not (1 or 2 or 8 or 9));
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool InvokeWmiByConnectionName(
        string connectionName,
        string method,
        out uint? lastResult,
        out bool invoked)
    {
        lastResult = null;
        invoked = false;
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var id = mo["NetConnectionID"]?.ToString();
                if (!NamesEqual(id, connectionName))
                {
                    continue;
                }

                invoked = true;
                var result = (uint)mo.InvokeMethod(method, null!);
                lastResult = result;
                // 0 = success. 5 = already in requested state. Other codes may still flip — caller rechecks.
                if (result is 0 or 5)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool InvokeWmiByQuery(
        string query,
        string method,
        out uint? lastResult,
        out bool invoked)
    {
        lastResult = null;
        invoked = false;
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                invoked = true;
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
        var alias = NormalizeConnectionName(connectionName);

        // 1) Canonical Windows aliases beat description false-positives (e.g. "virtual" in a PNP string).
        if (WifiAliasRegex.IsMatch(alias))
        {
            return AdapterKind.Wireless;
        }

        if (EthernetAliasRegex.IsMatch(alias))
        {
            return AdapterKind.Ethernet;
        }

        // 2) Virtual / VPN / Bluetooth / TAP — never call them Ethernet via NIC type.
        if (forceIgnored || LooksVirtualOrNonPhysical(haystack))
        {
            return AdapterKind.Other;
        }

        if (LooksWireless(haystack))
        {
            return AdapterKind.Wireless;
        }

        // 3) .NET interface type (only after virtual + alias checks).
        var fromNic = ClassifyFromNetworkInterfaceType(networkInterfaceType);
        if (fromNic is AdapterKind.Wireless)
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
