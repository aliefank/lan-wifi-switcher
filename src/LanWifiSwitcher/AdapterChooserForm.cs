namespace LanWifiSwitcher;

/// <summary>
/// Focused dialog to pick Ethernet and Wi‑Fi adapters (also reachable from Settings).
/// </summary>
public sealed class AdapterChooserForm : Form
{
    private readonly AppConfig _config;
    private readonly NetworkAdapterService _network;
    private readonly ListBox _ethernetList;
    private readonly ListBox _wifiList;

    public AdapterChooserForm(AppConfig config, NetworkAdapterService network)
    {
        _config = config;
        _network = network;

        Text = "Choose adapters";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 360);
        Font = new Font("Segoe UI", 9f);

        var ethernetLabel = new Label
        {
            Text = "Ethernet / LAN",
            Location = new Point(16, 12),
            AutoSize = true
        };

        _ethernetList = new ListBox
        {
            Location = new Point(16, 36),
            Size = new Size(236, 260),
            DisplayMember = nameof(NetworkAdapterInfo.Name)
        };

        var wifiLabel = new Label
        {
            Text = "Wi‑Fi",
            Location = new Point(268, 12),
            AutoSize = true
        };

        _wifiList = new ListBox
        {
            Location = new Point(268, 36),
            Size = new Size(236, 260),
            DisplayMember = nameof(NetworkAdapterInfo.Name)
        };

        var ok = new Button
        {
            Text = "OK",
            Location = new Point(320, 316),
            Size = new Size(88, 28)
        };
        ok.Click += (_, _) => SaveAndClose();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(416, 316),
            Size = new Size(88, 28)
        };

        var auto = new Button
        {
            Text = "Auto-detect",
            Location = new Point(16, 316),
            Size = new Size(100, 28)
        };
        auto.Click += (_, _) => AutoDetect();

        Controls.AddRange([ethernetLabel, _ethernetList, wifiLabel, _wifiList, auto, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        Load += (_, _) => Populate();
    }

    private void Populate()
    {
        var adapters = _network.GetAdapters();
        _ethernetList.Items.Clear();
        _wifiList.Items.Clear();

        foreach (var adapter in adapters)
        {
            _ethernetList.Items.Add(adapter);
            _wifiList.Items.Add(adapter);
        }

        SelectMatching(_ethernetList, _config.EthernetAdapterId, _config.EthernetAdapterName, AdapterKind.Ethernet);
        SelectMatching(_wifiList, _config.WifiAdapterId, _config.WifiAdapterName, AdapterKind.Wireless);
    }

    private static void SelectMatching(
        ListBox list,
        string? id,
        string? name,
        AdapterKind preferredKind)
    {
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is NetworkAdapterInfo a &&
                (string.Equals(a.DeviceId, id, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                list.SelectedIndex = i;
                return;
            }
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is NetworkAdapterInfo a && a.Kind == preferredKind)
            {
                list.SelectedIndex = i;
                return;
            }
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    private void AutoDetect()
    {
        var temp = new AppConfig();
        _network.AutoSelectDefaults(temp);
        SelectMatching(_ethernetList, temp.EthernetAdapterId, temp.EthernetAdapterName, AdapterKind.Ethernet);
        SelectMatching(_wifiList, temp.WifiAdapterId, temp.WifiAdapterName, AdapterKind.Wireless);
    }

    private void SaveAndClose()
    {
        if (_ethernetList.SelectedItem is not NetworkAdapterInfo ethernet ||
            _wifiList.SelectedItem is not NetworkAdapterInfo wifi)
        {
            MessageBox.Show(
                "Select both adapters.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(ethernet.Name, wifi.Name, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Pick two different adapters.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _config.EthernetAdapterId = ethernet.DeviceId;
        _config.EthernetAdapterName = ethernet.Name;
        _config.WifiAdapterId = wifi.DeviceId;
        _config.WifiAdapterName = wifi.Name;
        _config.Save();
        DialogResult = DialogResult.OK;
        Close();
    }
}
