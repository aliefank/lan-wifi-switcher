namespace LanWifiSwitcher;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly NetworkAdapterService _network;
    private readonly ComboBox _ethernetCombo;
    private readonly ComboBox _wifiCombo;
    private readonly CheckBox _toastCheck;
    private readonly CheckBox _startupCheck;
    private readonly Label _statusLabel;

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
        ClientSize = new Size(460, 300);
        Font = new Font("Segoe UI", 9f);

        var ethernetLabel = new Label
        {
            Text = "Ethernet / LAN adapter:",
            Location = new Point(16, 18),
            AutoSize = true
        };

        _ethernetCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(16, 40),
            Width = 428
        };

        var wifiLabel = new Label
        {
            Text = "Wi‑Fi adapter:",
            Location = new Point(16, 78),
            AutoSize = true
        };

        _wifiCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(16, 100),
            Width = 428
        };

        _toastCheck = new CheckBox
        {
            Text = "Show a notification when switching",
            Location = new Point(16, 148),
            AutoSize = true,
            Checked = _config.ShowToastOnSwitch
        };

        _startupCheck = new CheckBox
        {
            Text = "Run at Windows startup",
            Location = new Point(16, 176),
            AutoSize = true,
            Checked = _config.RunAtStartup || StartupManager.IsEnabled()
        };

        _statusLabel = new Label
        {
            Location = new Point(16, 210),
            Size = new Size(428, 36),
            ForeColor = Color.FromArgb(70, 70, 70)
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.None,
            Location = new Point(268, 256),
            Size = new Size(88, 28)
        };
        saveButton.Click += (_, _) => SaveAndClose();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(360, 256),
            Size = new Size(88, 28)
        };

        var refreshButton = new Button
        {
            Text = "Refresh list",
            Location = new Point(16, 256),
            Size = new Size(100, 28)
        };
        refreshButton.Click += (_, _) => LoadAdapters();

        Controls.AddRange(
        [
            ethernetLabel, _ethernetCombo,
            wifiLabel, _wifiCombo,
            _toastCheck, _startupCheck, _statusLabel,
            refreshButton, saveButton, cancelButton
        ]);

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Load += (_, _) =>
        {
            LoadAdapters();
            UpdateStatus();
        };
    }

    private void LoadAdapters()
    {
        var adapters = _network.GetAdapters();
        var ethernet = adapters.Where(a => a.Kind is AdapterKind.Ethernet or AdapterKind.Other).ToList();
        var wifi = adapters.Where(a => a.Kind is AdapterKind.Wireless or AdapterKind.Other).ToList();

        // Always allow picking from the full list in case classification missed something.
        if (ethernet.Count == 0) ethernet = adapters.ToList();
        if (wifi.Count == 0) wifi = adapters.ToList();

        BindCombo(_ethernetCombo, ethernet, _config.EthernetAdapterId, _config.EthernetAdapterName);
        BindCombo(_wifiCombo, wifi, _config.WifiAdapterId, _config.WifiAdapterName);
    }

    private static void BindCombo(
        ComboBox combo,
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? selectedId,
        string? selectedName)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (var adapter in adapters)
        {
            combo.Items.Add(adapter);
        }

        NetworkAdapterInfo? match = null;
        if (!string.IsNullOrWhiteSpace(selectedId) || !string.IsNullOrWhiteSpace(selectedName))
        {
            match = adapters.FirstOrDefault(a =>
                string.Equals(a.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        }

        if (match is not null)
        {
            combo.SelectedItem = match;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        combo.EndUpdate();
    }

    private void UpdateStatus()
    {
        var mode = _network.DetectCurrentMode(_config);
        _statusLabel.Text = mode switch
        {
            ConnectionMode.Lan => "Current mode: LAN (Ethernet enabled, Wi‑Fi disabled)",
            ConnectionMode.Wifi => "Current mode: Wi‑Fi (Ethernet disabled, Wi‑Fi enabled)",
            ConnectionMode.Both => "Current mode: both adapters enabled",
            ConnectionMode.Neither => "Current mode: both adapters disabled",
            _ => "Current mode: unknown — pick both adapters and toggle."
        };
    }

    private void SaveAndClose()
    {
        if (_ethernetCombo.SelectedItem is not NetworkAdapterInfo ethernet ||
            _wifiCombo.SelectedItem is not NetworkAdapterInfo wifi)
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
