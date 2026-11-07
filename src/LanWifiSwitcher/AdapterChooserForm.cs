namespace LanWifiSwitcher;

/// <summary>
/// Modern adapter picker: full list with type hints; prefers ncpa.cpl aliases.
/// </summary>
public sealed class AdapterChooserForm : Form
{
    private readonly AppConfig _config;
    private readonly NetworkAdapterService _network;
    private readonly ListBox _ethernetList;
    private readonly ListBox _wifiList;
    private readonly Label _hintLabel;
    private readonly Label _etherMeta;
    private readonly Label _wifiMeta;

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
        ClientSize = new Size(720, 520);
        UiTheme.ApplyWindow(this);

        var title = new Label
        {
            Text = "Choose adapters",
            Location = new Point(24, 18),
            AutoSize = true,
            Font = UiTheme.TitleFont,
            ForeColor = UiTheme.TextPrimary
        };

        var subtitle = UiTheme.MakeMutedLabel(
            "Pick the ncpa.cpl aliases Ethernet + Wi‑Fi. Bluetooth / TAP / OpenVPN are listed but ignored by Auto-detect.",
            new Point(24, 50),
            new Size(672, 28));

        var etherCard = UiTheme.MakeCard(new Point(24, 88), new Size(328, 340));
        var wifiCard = UiTheme.MakeCard(new Point(368, 88), new Size(328, 340));

        var etherTitle = new Label
        {
            Text = "LAN · Ethernet",
            Location = new Point(14, 12),
            AutoSize = true,
            Font = UiTheme.SectionFont,
            ForeColor = UiTheme.AccentLan,
            BackColor = Color.Transparent
        };
        _etherMeta = UiTheme.MakeMutedLabel(" ", new Point(14, 34), new Size(300, 18));
        _ethernetList = new ListBox
        {
            Location = new Point(14, 58),
            Size = new Size(300, 266)
        };
        UiTheme.StyleOwnerDrawList(_ethernetList);
        etherCard.Controls.AddRange([etherTitle, _etherMeta, _ethernetList]);

        var wifiTitle = new Label
        {
            Text = "Wi‑Fi",
            Location = new Point(14, 12),
            AutoSize = true,
            Font = UiTheme.SectionFont,
            ForeColor = UiTheme.AccentWifi,
            BackColor = Color.Transparent
        };
        _wifiMeta = UiTheme.MakeMutedLabel(" ", new Point(14, 34), new Size(300, 18));
        _wifiList = new ListBox
        {
            Location = new Point(14, 58),
            Size = new Size(300, 266)
        };
        UiTheme.StyleOwnerDrawList(_wifiList);
        wifiCard.Controls.AddRange([wifiTitle, _wifiMeta, _wifiList]);

        _hintLabel = UiTheme.MakeMutedLabel("", new Point(24, 438), new Size(480, 32));

        var auto = UiTheme.MakeGhostButton("Auto-detect", new Point(24, 478), new Size(110, 30));
        auto.Click += (_, _) => AutoDetect();

        var refresh = UiTheme.MakeGhostButton("Refresh", new Point(142, 478), new Size(90, 30));
        refresh.Click += (_, _) => Populate();

        var cancel = UiTheme.MakeGhostButton("Cancel", new Point(516, 478), new Size(88, 30));
        cancel.DialogResult = DialogResult.Cancel;

        var ok = UiTheme.MakePrimaryButton("Save", new Point(612, 478), new Size(84, 30));
        ok.Click += (_, _) => SaveAndClose();

        Controls.AddRange(
        [
            title, subtitle, etherCard, wifiCard,
            _hintLabel, auto, refresh, cancel, ok
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

        foreach (var adapter in adapters)
        {
            _ethernetList.Items.Add(adapter);
            _wifiList.Items.Add(adapter);
        }

        _ethernetList.EndUpdate();
        _wifiList.EndUpdate();

        var wifiCount = adapters.Count(a => a.Kind == AdapterKind.Wireless && !a.IsVirtualOrIgnored);
        var ethCount = adapters.Count(a => a.Kind == AdapterKind.Ethernet && !a.IsVirtualOrIgnored);
        var ignored = adapters.Count(a => a.IsVirtualOrIgnored);
        _etherMeta.Text = $"{ethCount} physical Ethernet · cable optional";
        _wifiMeta.Text = $"{wifiCount} physical Wi‑Fi";
        _hintLabel.Text =
            $"{adapters.Count} adapters · {ignored} ignored for auto-pick (Bluetooth / TAP / OpenVPN). " +
            "Prefer rows named exactly Ethernet and Wi‑Fi.";

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
                (NetworkAdapterService.NamesEqual(a.Name, name) ||
                 string.Equals(a.DeviceId?.Trim(), id?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                list.SelectedIndex = i;
                return;
            }
        }

        // Prefer canonical alias among the preferred kind.
        var bestAlias = -1;
        var bestScore = -1;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is NetworkAdapterInfo a &&
                a.Kind == preferredKind &&
                !a.IsVirtualOrIgnored)
            {
                var score = NetworkAdapterService.AliasScore(a.Name, preferredKind == AdapterKind.Wireless);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAlias = i;
                }
            }
        }

        if (bestAlias >= 0)
        {
            list.SelectedIndex = bestAlias;
            return;
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    private void AutoDetect()
    {
        // Refresh the on-screen list first so WMI + NetworkInterface merge is current.
        Populate();

        var temp = new AppConfig();
        _network.AutoSelectDefaults(temp, repairUnsuitable: true);

        // If service still missed one role, pick exact aliases directly from the list items.
        var listed = _ethernetList.Items.Cast<NetworkAdapterInfo>().ToList();
        temp.EthernetAdapterName ??= listed
            .Where(a => NetworkAdapterService.AliasScore(a.Name, wifi: false) >= 90)
            .OrderByDescending(a => NetworkAdapterService.AliasScore(a.Name, wifi: false))
            .Select(a => a.Name)
            .FirstOrDefault();
        temp.EthernetAdapterId ??= listed
            .FirstOrDefault(a => NetworkAdapterService.NamesEqual(a.Name, temp.EthernetAdapterName))
            ?.DeviceId;
        temp.WifiAdapterName ??= listed
            .Where(a => NetworkAdapterService.AliasScore(a.Name, wifi: true) >= 90)
            .OrderByDescending(a => NetworkAdapterService.AliasScore(a.Name, wifi: true))
            .Select(a => a.Name)
            .FirstOrDefault();
        temp.WifiAdapterId ??= listed
            .FirstOrDefault(a => NetworkAdapterService.NamesEqual(a.Name, temp.WifiAdapterName))
            ?.DeviceId;

        SelectMatching(_ethernetList, temp.EthernetAdapterId, temp.EthernetAdapterName, AdapterKind.Ethernet);
        SelectMatching(_wifiList, temp.WifiAdapterId, temp.WifiAdapterName, AdapterKind.Wireless);

        if (string.IsNullOrWhiteSpace(temp.WifiAdapterName) || string.IsNullOrWhiteSpace(temp.EthernetAdapterName))
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(temp.EthernetAdapterName)) missing.Add("Ethernet");
            if (string.IsNullOrWhiteSpace(temp.WifiAdapterName)) missing.Add("Wi-Fi");

            MessageBox.Show(
                $"Could not auto-detect: {string.Join(" + ", missing)}.\n\n" +
                "Looked for ncpa.cpl aliases \"Ethernet\" and \"Wi-Fi\" " +
                "(and Realtek 2.5GbE / 8922AE descriptions). Skip Bluetooth / TAP / OpenVPN.\n\n" +
                "Adapters seen:\n" + _network.FormatAdapterDiagnostics(listed),
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
            MessageBox.Show("Select both adapters.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(ethernet.Name, wifi.Name, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Pick two different adapters.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (ethernet.IsVirtualOrIgnored || wifi.IsVirtualOrIgnored)
        {
            var confirm = MessageBox.Show(
                "One of the selected adapters looks like Bluetooth / TAP / VPN.\n\nUse it anyway?",
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
