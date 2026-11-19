namespace LanWifiSwitcher;

public sealed class AdapterChooserForm : Form
{
    public AdapterChooserForm(AppConfig config, NetworkAdapterService network)
    {
        _ = new AdapterDialog(this, config, network, showPreferences: false);
    }
}
