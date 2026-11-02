namespace LanWifiSwitcher;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly NetworkAdapterService _network;
    private readonly ListBox _ethernetList;
    private readonly ListBox _wifiList;
    private readonly CheckBox _toastCheck;
    private readonly CheckBox _startupCheck;
    private readonly Label _statusLabel;
    private readonly Label _hintLabel;

    public SettingsForm(AppConfig config, NetworkAdapterService network)
    {
        _config = config;
        _network = network;

        Text = "LAN Wi‑Fi Switcher — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 560);
        UiTheme.ApplyWindow(this);

        var title = new Label
        {
            Text = "Settings",
            Location = new Point(24, 18),
            AutoSize = true,
            Font = UiTheme.TitleFont,
            ForeColor = UiTheme.TextPrimary
        };

        var subtitle = UiTheme.MakeMutedLabel(
            "Tray-only switcher · adapters use ncpa.cpl names · cable not required to toggle",
            new Point(24, 50),
            new Size(592, 22));

        var etherCard = UiTheme.MakeCard(new Point(24, 84), new Size(592, 160));
        var etherTitle = new Label
        {
            Text = "LAN · Ethernet",
            Location = new Point(14, 10),
            AutoSize = true,
            Font = UiTheme.SectionFont,
            ForeColor = UiTheme.AccentLan,
            BackColor = Color.Transparent
        };
        _ethernetList = new ListBox
        {
            Location = new Point(14, 36),
            Size = new Size(564, 110)
        };
        UiTheme.StyleOwnerDrawList(_ethernetList);
        etherCard.Controls.AddRange([etherTitle, _ethernetList]);

        var wifiCard = UiTheme.MakeCard(new Point(24, 256), new Size(592, 160));
        var wifiTitle = new Label
        {
            Text = "Wi‑Fi",
            Location = new Point(14, 10),
            AutoSize = true,
            Font = UiTheme.SectionFont,
            ForeColor = UiTheme.AccentWifi,
            BackColor = Color.Transparent
        };
        _wifiList = new ListBox
        {
            Location = new Point(14, 36),
            Size = new Size(564, 110)
        };
        UiTheme.StyleOwnerDrawList(_wifiList);
        wifiCard.Controls.AddRange([wifiTitle, _wifiList]);

        _hintLabel = UiTheme.MakeMutedLabel("", new Point(24, 424), new Size(592, 20));

        _toastCheck = new CheckBox
        {
            Text = "Show a notification when switching",
            Location = new Point(24, 450),
            AutoSize = true,
            Checked = _config.ShowToastOnSwitch,
            ForeColor = UiTheme.TextPrimary,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat
        };

        _startupCheck = new CheckBox
        {
            Text = "Run at Windows startup",
            Location = new Point(24, 476),
            AutoSize = true,
            Checked = _config.RunAtStartup || StartupManager.IsEnabled(),
            ForeColor = UiTheme.TextPrimary,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat
        };

        _statusLabel = UiTheme.MakeMutedLabel("", new Point(280, 450), new Size(336, 44));
        _statusLabel.Font = UiTheme.SmallFont;

        var refresh = UiTheme.MakeGhostButton("Refresh", new Point(24, 516), new Size(90, 30));
        refresh.Click += (_, _) =>
        {
            LoadAdapters();
            UpdateStatus();
        };

        var cancel = UiTheme.MakeGhostButton("Cancel", new Point(436, 516), new Size(88, 30));
        cancel.DialogResult = DialogResult.Cancel;

        var save = UiTheme.MakePrimaryButton("Save", new Point(532, 516), new Size(84, 30));
        save.Click += (_, _) => SaveAndClose();

        Controls.AddRange(
        [
            title, subtitle, etherCard, wifiCard, _hintLabel,
            _toastCheck, _startupCheck, _statusLabel,
            refresh, cancel, save
        ]);

        AcceptButton = save;
        CancelButton = cancel;

        Load += (_, _) =>
        {
            LoadAdapters();
            UpdateStatus();
        };
    }

    private void LoadAdapters()
    {
        var adapters = _network.GetAdapters();
        BindList(_ethernetList, adapters, _config.EthernetAdapterId, _config.EthernetAdapterName, AdapterKind.Ethernet);
        BindList(_wifiList, adapters, _config.WifiAdapterId, _config.WifiAdapterName, AdapterKind.Wireless);

        var wifi = adapters.Count(a => a.Kind == AdapterKind.Wireless && !a.IsVirtualOrIgnored);
        var ignored = adapters.Count(a => a.IsVirtualOrIgnored);
        _hintLabel.Text = wifi == 0
            ? "No Wi‑Fi classified yet — pick the Wi‑Fi row manually (Realtek 8922AE)."
            : $"{wifi} Wi‑Fi · {ignored} ignored virtual (Bluetooth / TAP / OpenVPN).";
    }

    private static void BindList(
        ListBox list,
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? selectedId,
        string? selectedName,
        AdapterKind preferredKind)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var adapter in adapters)
        {
            list.Items.Add(adapter);
        }

        var matchIndex = -1;
        for (var i = 0; i < adapters.Count; i++)
        {
            var a = adapters[i];
            if (string.Equals(a.Name, selectedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
        {
            var bestScore = -1;
            for (var i = 0; i < adapters.Count; i++)
            {
                var a = adapters[i];
                if (a.Kind != preferredKind || a.IsVirtualOrIgnored) continue;
                var score = NetworkAdapterService.AliasScore(a.Name, preferredKind == AdapterKind.Wireless);
                if (score > bestScore)
                {
                    bestScore = score;
                    matchIndex = i;
                }
            }
        }

        if (matchIndex >= 0)
        {
            list.SelectedIndex = matchIndex;
        }
        else if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        list.EndUpdate();
    }

    private void UpdateStatus()
    {
        var mode = _network.DetectCurrentMode(_config);
        _statusLabel.Text = mode switch
        {
            ConnectionMode.Lan => "Mode: LAN (Ethernet enabled — cable optional)",
            ConnectionMode.Wifi => "Mode: Wi‑Fi (Ethernet disabled)",
            ConnectionMode.Both => "Mode: both enabled",
            ConnectionMode.Neither => "Mode: both disabled",
            _ => "Mode: unknown — save adapters, then toggle from the tray."
        };
    }

    private void SaveAndClose()
    {
        if (_ethernetList.SelectedItem is not NetworkAdapterInfo ethernet ||
            _wifiList.SelectedItem is not NetworkAdapterInfo wifi)
        {
            MessageBox.Show(
                "Select both an Ethernet and a Wi‑Fi adapter.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(ethernet.Name, wifi.Name, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "Ethernet and Wi‑Fi must be different adapters.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _config.EthernetAdapterId = ethernet.DeviceId;
        _config.EthernetAdapterName = ethernet.Name;
        _config.WifiAdapterId = wifi.DeviceId;
        _config.WifiAdapterName = wifi.Name;
        _config.ShowToastOnSwitch = _toastCheck.Checked;
        _config.RunAtStartup = _startupCheck.Checked;

        try
        {
            StartupManager.SetEnabled(_config.RunAtStartup);
            _config.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save settings:\n{ex.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
