using System.Management;
using System.Net.NetworkInformation;

namespace LanWifiSwitcher;

/// <summary>
/// Discovers adapters and enables/disables them via WMI (Win32_NetworkAdapter).
/// Falls back to netsh when WMI methods are unavailable.
/// </summary>
public sealed class NetworkAdapterService
{
    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var results = new List<NetworkAdapterInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, NetConnectionID, NetEnabled, NetConnectionStatus, AdapterTypeId, InterfaceIndex, Description, PNPDeviceID " +
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
                    var description = mo["Description"]?.ToString();
                    var name = mo["Name"]?.ToString() ?? connectionName;
                    var enabled = mo["NetEnabled"] is bool b && b;
                    var status = Convert.ToUInt16(mo["NetConnectionStatus"] ?? 0);
                    // 2 = Connected
                    var connected = status == 2;
                    uint? ifIndex = mo["InterfaceIndex"] is null ? null : Convert.ToUInt32(mo["InterfaceIndex"]);
                    var kind = Classify(connectionName, description, name, mo["PNPDeviceID"]?.ToString(), mo["AdapterTypeId"]);

                    results.Add(new NetworkAdapterInfo
                    {
                        DeviceId = deviceId,
                        Name = connectionName,
                        Description = description ?? name,
                        Kind = kind,
                        IsEnabled = enabled,
                        IsConnected = connected,
                        InterfaceIndex = ifIndex
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

                var kind = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
                        or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
                        or NetworkInterfaceType.GigabitEthernet => AdapterKind.Ethernet,
                    NetworkInterfaceType.Wireless80211 => AdapterKind.Wireless,
                    _ => AdapterKind.Other
                };

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
            .OrderBy(a => a.Kind)
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
        // Prefer WMI Enable/Disable on Win32_NetworkAdapter.
        if (TryWmiSetEnabled(adapter, enabled))
        {
            return;
        }

        // Fallback: netsh interface set interface name="..." admin=enabled|disabled
        RunNetsh(adapter.Name, enabled);
    }

    private NetworkAdapterInfo? Resolve(string? id, string? name) =>
        FindByNameOrId(id) ?? FindByNameOrId(name);

    private NetworkAdapterInfo ResolveRequired(string? id, string? name, string label)
    {
        var adapter = Resolve(id, name);
        if (adapter is null)
        {
            throw new InvalidOperationException(
                $"{label} adapter is not configured or was not found. Open Settings or Choose adapters.");
        }

        return adapter;
    }

    private static bool TryWmiSetEnabled(NetworkAdapterInfo adapter, bool enabled)
    {
        try
        {
            // Match by NetConnectionID (friendly name shown in ncpa.cpl) first.
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID = '" + EscapeWmi(adapter.Name) + "'");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var result = (uint)mo.InvokeMethod(enabled ? "Enable" : "Disable", null!);
                    // 0 = success, 5 = already in requested state on some drivers.
                    if (result is 0 or 5)
                    {
                        return true;
                    }
                }
            }

            // Retry by DeviceID if name match failed.
            using var byId = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapter WHERE DeviceID = '" + EscapeWmi(adapter.DeviceId) + "'");

            foreach (ManagementObject mo in byId.Get())
            {
                using (mo)
                {
                    var result = (uint)mo.InvokeMethod(enabled ? "Enable" : "Disable", null!);
                    if (result is 0 or 5)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
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
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            var output = process.StandardOutput.ReadToEnd();
            throw new InvalidOperationException(
                $"netsh failed (exit {process.ExitCode}) while {(enabled ? "enabling" : "disabling")} '{interfaceName}'. {err} {output}".Trim());
        }
    }

    private static string EscapeWmi(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static AdapterKind Classify(
        string connectionName,
        string? description,
        string name,
        string? pnpDeviceId,
        object? adapterTypeId)
    {
        var haystack = $"{connectionName} {description} {name} {pnpDeviceId}".ToLowerInvariant();

        if (haystack.Contains("wi-fi", StringComparison.Ordinal) ||
            haystack.Contains("wifi", StringComparison.Ordinal) ||
            haystack.Contains("wireless", StringComparison.Ordinal) ||
            haystack.Contains("wlan", StringComparison.Ordinal) ||
            haystack.Contains("802.11", StringComparison.Ordinal))
        {
            return AdapterKind.Wireless;
        }

        if (haystack.Contains("bluetooth", StringComparison.Ordinal) ||
            haystack.Contains("virtual", StringComparison.Ordinal) ||
            haystack.Contains("hyper-v", StringComparison.Ordinal) ||
            haystack.Contains("vethernet", StringComparison.Ordinal) ||
            haystack.Contains("vpn", StringComparison.Ordinal) ||
            haystack.Contains("tap-", StringComparison.Ordinal) ||
            haystack.Contains("loopback", StringComparison.Ordinal))
        {
            return AdapterKind.Other;
        }

        // AdapterTypeId 0 = Ethernet 802.3
        if (adapterTypeId is not null && Convert.ToUInt16(adapterTypeId) == 0)
        {
            return AdapterKind.Ethernet;
        }

        if (haystack.Contains("ethernet", StringComparison.Ordinal) ||
            haystack.Contains("realtek", StringComparison.Ordinal) ||
            haystack.Contains("intel(r) ethernet", StringComparison.Ordinal) ||
            haystack.Contains("local area connection", StringComparison.Ordinal))
        {
            return AdapterKind.Ethernet;
        }

        return AdapterKind.Other;
    }
}
