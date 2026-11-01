namespace LanWifiSwitcher;

/// <summary>
/// Focused dialog to pick Ethernet and Wi‑Fi adapters (also reachable from Settings).
/// Lists every adapter with type hints so oddly named Wi‑Fi NICs (Realtek 8922AE, MediaTek, …)
/// remain selectable even if auto-classification is uncertain.
/// </summary>
public sealed class AdapterChooserForm : Form
{
    private readonly AppConfig _config;
    private readonly NetworkAdapterService _network;
    private readonly ListBox _ethernetList;
    private readonly ListBox _wifiList;
    private readonly Label _hintLabel;

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
        ClientSize = new Size(640, 420);
        Font = new Font("Segoe UI", 9f);

        var ethernetLabel = new Label
        {
            Text = "Ethernet / LAN (pick one)",
            Location = new Point(16, 12),
            AutoSize = true
        };

        _ethernetList = new ListBox
        {
            Location = new Point(16, 36),
            Size = new Size(298, 280),
            IntegralHeight = false,
            HorizontalScrollbar = true
        };

        var wifiLabel = new Label
        {
            Text = "Wi‑Fi (pick one — includes Realtek / MediaTek / ASUS)",
            Location = new Point(326, 12),
            AutoSize = true
        };

        _wifiList = new ListBox
        {
            Location = new Point(326, 36),
            Size = new Size(298, 280),
            IntegralHeight = false,
            HorizontalScrollbar = true
        };

        _hintLabel = new Label
        {
            Location = new Point(16, 324),
            Size = new Size(608, 36),
            ForeColor = Color.FromArgb(70, 70, 70),
            Text = "All adapters are listed with [type] hints. Prefer entries marked [Wi‑Fi] " +
                   "for wireless (e.g. Realtek 8922AE Wi‑Fi 7). Use Auto-detect if unsure."
        };

        var ok = new Button
        {
            Text = "OK",
            Location = new Point(440, 372),
            Size = new Size(88, 28)
        };
        ok.Click += (_, _) => SaveAndClose();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(536, 372),
            Size = new Size(88, 28)
        };

        var auto = new Button
        {
            Text = "Auto-detect",
            Location = new Point(16, 372),
            Size = new Size(100, 28)
        };
        auto.Click += (_, _) => AutoDetect();

        var refresh = new Button
        {
            Text = "Refresh",
            Location = new Point(124, 372),
            Size = new Size(88, 28)
        };
        refresh.Click += (_, _) => Populate();

        Controls.AddRange(
        [
            ethernetLabel, _ethernetList, wifiLabel, _wifiList,
            _hintLabel, auto, refresh, ok, cancel
        ]);
        AcceptButton = ok;
        CancelButton = cancel;

        Load += (_, _) => Populate();
    }

    private void Populate()
    {
        var adapters = _network.GetAdapters();
        _ethernetList.BeginUpdate();
        _wifiList.BeginUpdate();
        _ethernetList.Items.Clear();
        _wifiList.Items.Clear();

        // Always show the full list so misclassified NICs remain pickable.
        foreach (var adapter in adapters)
        {
            _ethernetList.Items.Add(adapter);
            _wifiList.Items.Add(adapter);
        }

        _ethernetList.EndUpdate();
        _wifiList.EndUpdate();

        var wifiCount = adapters.Count(a => a.Kind == AdapterKind.Wireless);
        var ethCount = adapters.Count(a => a.Kind == AdapterKind.Ethernet);
        _hintLabel.Text =
            $"Found {adapters.Count} adapter(s): {ethCount} Ethernet, {wifiCount} Wi‑Fi, " +
            $"{adapters.Count - ethCount - wifiCount} Other. " +
            "Look for [Wi‑Fi] next to Realtek 8922AE / MediaTek / WLAN names.";

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

        if (string.IsNullOrWhiteSpace(temp.WifiAdapterName))
        {
            MessageBox.Show(
                "No Wi‑Fi adapter was auto-detected. Pick it manually from the right-hand list " +
                "(look for [Wi‑Fi] or a Realtek / MediaTek / WLAN description).",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
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

        if (string.Equals(ethernet.DeviceId, wifi.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ethernet.Name, wifi.Name, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Pick two different adapters.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (wifi.Kind != AdapterKind.Wireless)
        {
            var confirm = MessageBox.Show(
                $"“{wifi.Name}” is classified as {wifi.KindLabel}, not Wi‑Fi.\n\n" +
                "Use it as the Wi‑Fi adapter anyway?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }
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
