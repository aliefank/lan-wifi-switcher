namespace LanWifiSwitcher;

public sealed class SettingsForm : Form
{
    public SettingsForm(AppConfig config, NetworkAdapterService network)
    {
        _ = new AdapterDialog(this, config, network, showPreferences: true);
    }
}
