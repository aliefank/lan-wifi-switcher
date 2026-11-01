using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanWifiSwitcher;

/// <summary>
/// Persisted user settings under %AppData%\LanWifiSwitcher\config.json.
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? EthernetAdapterId { get; set; }
    public string? EthernetAdapterName { get; set; }
    public string? WifiAdapterId { get; set; }
    public string? WifiAdapterName { get; set; }
    public bool ShowToastOnSwitch { get; set; } = true;
    public bool RunAtStartup { get; set; }

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanWifiSwitcher");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall through to defaults.
        }

        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
