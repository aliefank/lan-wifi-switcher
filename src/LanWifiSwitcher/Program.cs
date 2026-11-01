using System.Security.Principal;

namespace LanWifiSwitcher;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Single-instance guard (tray apps should not stack).
        const string mutexName = @"Local\LanWifiSwitcher_SingleInstance";
        _mutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "LAN Wi‑Fi Switcher is already running (check the system tray).",
                "LAN Wi‑Fi Switcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (!IsRunningElevated())
        {
            MessageBox.Show(
                "Administrator elevation is required to enable or disable network adapters.\n\n" +
                "Rebuild with the embedded app.manifest (requireAdministrator), then run again and accept the UAC prompt.",
                "LAN Wi‑Fi Switcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            // Continue anyway so Settings remains usable; toggle will fail with a clear error.
        }

        Application.Run(new TrayAppContext());
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
