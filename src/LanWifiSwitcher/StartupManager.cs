using Microsoft.Win32;

namespace LanWifiSwitcher;

/// <summary>
/// Registers/unregisters the app in HKCU Run so it starts with Windows.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanWifiSwitcher";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open HKCU Run key.");

        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine process path.");
            // Quote the path for safety with spaces.
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Keeps the registry entry in sync with config, and refreshes the stored path
    /// when the executable has moved.
    /// </summary>
    public static void SyncFromConfig(AppConfig config)
    {
        if (config.RunAtStartup)
        {
            SetEnabled(true);
        }
        else if (IsEnabled())
        {
            SetEnabled(false);
        }
    }
}
