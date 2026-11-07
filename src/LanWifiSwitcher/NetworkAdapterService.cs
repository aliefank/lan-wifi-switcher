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

    // After NormalizeConnectionName, Unicode hyphens become ASCII '-' so "Wi‑Fi" matches.
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
                    // Keep the raw NetConnectionID for WMI/netsh; normalize only when comparing.
                    // Some stacks return "Wi‑Fi" (U+2011) while ncpa.cpl displays "Wi-Fi".
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
                            NamesEqual(n.Name, connectionName));
                    }

                    var haystack = BuildHaystack(connectionName, description, name, manufacturer, pnp);
                    var looksVirtual = LooksVirtualOrNonPhysical(haystack);
                    // Canonical ncpa.cpl aliases win over a false PhysicalAdapter flag from some drivers.
                    var clearAlias = AliasScore(connectionName, wifi: true) > 0
                                     || AliasScore(connectionName, wifi: false) > 0;
                    var ignored = looksVirtual || (physical == false && !clearAlias);
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
                        IsVirtualOrIgnored = ignored || (kind == AdapterKind.Other && looksVirtual)
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

        var ethernet = ResolveRole(adapters, config.EthernetAdapterId, config.EthernetAdapterName, AdapterKind.Ethernet);
        if (IsSuitableLan(ethernet))
        {
            // Refresh stored DeviceId while keeping the preferred NetConnectionID alias.
            config.EthernetAdapterId = ethernet!.DeviceId;
            config.EthernetAdapterName = ethernet.Name;
        }
        else
        {
            if (repairUnsuitable)
            {
                config.EthernetAdapterId = null;
                config.EthernetAdapterName = null;
            }

            if (string.IsNullOrWhiteSpace(config.EthernetAdapterName) &&
                string.IsNullOrWhiteSpace(config.EthernetAdapterId))
            {
                ethernet = PickBest(adapters, AdapterKind.Ethernet, preferWifiAlias: false)
                           ?? ResolveByCanonicalAlias(adapters, wifi: false);
                if (ethernet is not null)
                {
                    config.EthernetAdapterId = ethernet.DeviceId;
                    config.EthernetAdapterName = ethernet.Name;
                }
            }
        }

        var wifi = ResolveRole(adapters, config.WifiAdapterId, config.WifiAdapterName, AdapterKind.Wireless);
        if (IsSuitableWifi(wifi))
        {
            config.WifiAdapterId = wifi!.DeviceId;
            config.WifiAdapterName = wifi.Name;
        }
        else
        {
            if (repairUnsuitable)
            {
                config.WifiAdapterId = null;
                config.WifiAdapterName = null;
            }

            if (string.IsNullOrWhiteSpace(config.WifiAdapterName) &&
                string.IsNullOrWhiteSpace(config.WifiAdapterId))
            {
                wifi = PickBest(adapters, AdapterKind.Wireless, preferWifiAlias: true)
                       ?? ResolveByCanonicalAlias(adapters, wifi: true)
                       ?? ResolveByWifiChipset(adapters);
                if (wifi is not null)
                {
                    config.WifiAdapterId = wifi.DeviceId;
                    config.WifiAdapterName = wifi.Name;
                }
            }
        }
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
        Thread.Sleep(500);
        return DetectCurrentMode(config);
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
        bool wifi)
    {
        return adapters
            .Where(a => !a.IsVirtualOrIgnored)
            .Where(a => wifi
                ? a.Kind is AdapterKind.Wireless or AdapterKind.Unknown || AliasScore(a.Name, wifi: true) > 0
                : a.Kind is AdapterKind.Ethernet or AdapterKind.Unknown || AliasScore(a.Name, wifi: false) > 0)
            .OrderByDescending(a => AliasScore(a.Name, wifi))
            .ThenByDescending(a => a.IsEnabled)
            .ThenByDescending(a => a.IsConnected)
            .FirstOrDefault(a => AliasScore(a.Name, wifi) >= 90);
    }

    private static NetworkAdapterInfo? ResolveByWifiChipset(IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        return adapters
            .Where(a => !a.IsVirtualOrIgnored)
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
    /// Normalize ncpa.cpl / WMI NetConnectionID quirks: Unicode hyphens, NBSP, trim, collapse spaces.
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
        foreach (var ch in value.Trim())
        {
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

        try
        {
            // Match NetConnectionID with normalized equality (ASCII vs Unicode hyphen).
            if (InvokeWmiByConnectionName(adapter.Name, method, out lastResult))
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

    private static bool InvokeWmiByConnectionName(string connectionName, string method, out uint? lastResult)
    {
        lastResult = null;
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

                var result = (uint)mo.InvokeMethod(method, null!);
                lastResult = result;
                if (result is 0 or 5)
                {
                    return true;
                }
            }
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

        // 2) Canonical Windows aliases are authoritative (after Unicode hyphen normalize).
        var alias = NormalizeConnectionName(connectionName);
        if (WifiAliasRegex.IsMatch(alias) || LooksWireless(haystack))
        {
            return AdapterKind.Wireless;
        }

        if (EthernetAliasRegex.IsMatch(alias))
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
