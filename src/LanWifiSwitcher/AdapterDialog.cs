namespace LanWifiSwitcher;

internal sealed class AdapterDialog
{
    private readonly Form _form;
    private readonly AppConfig _config;
    private readonly NetworkAdapterService _network;
    private readonly bool _showPreferences;
    private readonly ListBox _ethernet = new() { AccessibleName = "Ethernet adapter", Dock = DockStyle.Fill };
    private readonly ListBox _wifi = new() { AccessibleName = "Wi-Fi adapter", Dock = DockStyle.Fill };
    private readonly Label _ethernetSelection = UiTheme.Label("");
    private readonly Label _wifiSelection = UiTheme.Label("");
    private readonly Label _feedback = UiTheme.Label("");
    private readonly Button _save = UiTheme.Button("&Save", primary: true);
    private readonly Button _auto = UiTheme.Button("&Auto-detect", width: 112);
    private readonly Button _refresh = UiTheme.Button("&Refresh");
    private readonly CheckBox _notifications = new() { Text = "Show a &notification after switching", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Run at &Windows startup", AutoSize = true };
    private readonly ToolTip _details = new();
    private bool _loading;
    private string? _scanMessage;

    public AdapterDialog(Form form, AppConfig config, NetworkAdapterService network, bool showPreferences)
    {
        _form = form;
        _config = config;
        _network = network;
        _showPreferences = showPreferences;

        var title = showPreferences ? "Settings" : "Choose adapters";
        var size = new Size(752, showPreferences ? 580 : 488);
        UiTheme.ApplyWindow(form, $"LAN Wi-Fi Switcher — {title}", size);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 18),
            ColumnCount = 1,
            RowCount = showPreferences ? 5 : 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Width = size.Width - 48 };
        var heading = UiTheme.Label(title, UiTheme.TitleFont, UiTheme.TextPrimary);
        heading.Dock = DockStyle.None;
        heading.SetBounds(48, 3, header.Width - 48, 34);
        heading.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var subtitle = UiTheme.Label("Choose the two network adapters to switch between from the tray.");
        subtitle.Dock = DockStyle.None;
        subtitle.SetBounds(49, 39, header.Width - 49, 23);
        subtitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var brand = new PictureBox { Image = IconFactory.AppIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom };
        brand.SetBounds(0, 13, 36, 36);
        header.Controls.AddRange([brand, heading, subtitle]);
        form.Disposed += (_, _) =>
        {
            brand.Image?.Dispose();
            _details.Dispose();
        };
        layout.Controls.Add(header, 0, 0);

        var adapterColumns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        adapterColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        adapterColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        adapterColumns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        adapterColumns.Controls.Add(CreateAdapterCard("Ethernet (LAN)", UiTheme.AccentLan, _ethernet, _ethernetSelection, new Padding(0, 0, 7, 0)), 0, 0);
        adapterColumns.Controls.Add(CreateAdapterCard("Wi-Fi", UiTheme.AccentWifi, _wifi, _wifiSelection, new Padding(7, 0, 0, 0)), 1, 0);
        layout.Controls.Add(adapterColumns, 0, 1);

        var footerRow = 2;
        if (showPreferences)
        {
            var preferences = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 12, 0, 0),
                Padding = new Padding(14, 8, 14, 8),
                BackColor = UiTheme.Surface,
                ColumnCount = 2,
                RowCount = 2
            };
            preferences.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            preferences.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            preferences.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            preferences.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var label = UiTheme.Label("Preferences", UiTheme.SectionFont, UiTheme.TextPrimary);
            preferences.Controls.Add(label, 0, 0);
            _notifications.Checked = config.ShowToastOnSwitch;
            _startup.Checked = config.RunAtStartup || StartupManager.IsEnabled();
            _notifications.Anchor = AnchorStyles.Left;
            _startup.Anchor = AnchorStyles.Left;
            preferences.Controls.Add(_notifications, 0, 1);
            preferences.Controls.Add(_startup, 1, 1);

            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            layout.Controls.Add(preferences, 0, footerRow++);
        }

        _feedback.AccessibleName = "Adapter selection status";
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(_feedback, 0, footerRow++);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(CreateFooter(), 0, footerRow);
        form.Controls.Add(layout);

        _ethernet.SelectedIndexChanged += OnSelectionChanged;
        _wifi.SelectedIndexChanged += OnSelectionChanged;
        _save.Enabled = false;
        form.Shown += async (_, _) => await LoadAdaptersAsync(useSaved: true, autoDetect: false);
    }

    private static TableLayoutPanel CreateAdapterCard(string title, Color accent, ListBox list, Label selection, Padding margin)
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = margin,
            Padding = new Padding(12, 10, 12, 12),
            BackColor = UiTheme.Surface,
            ColumnCount = 1,
            RowCount = 3
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(UiTheme.Label(title, UiTheme.SectionFont, accent), 0, 0);
        card.Controls.Add(selection, 0, 1);
        UiTheme.StyleAdapterList(list);
        card.Controls.Add(list, 0, 2);
        return card;
    }

    private TableLayoutPanel CreateFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _auto.Click += async (_, _) => await LoadAdaptersAsync(useSaved: false, autoDetect: true);
        _refresh.Click += async (_, _) => await LoadAdaptersAsync(useSaved: false, autoDetect: false);
        tools.Controls.AddRange([_auto, _refresh]);
        footer.Controls.Add(tools, 0, 0);

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var cancel = UiTheme.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        _save.Click += (_, _) => SaveAndClose();
        _save.Margin = Padding.Empty;
        actions.Controls.AddRange([cancel, _save]);
        footer.Controls.Add(actions, 2, 0);

        _form.AcceptButton = _save;
        _form.CancelButton = cancel;
        return footer;
    }

    private async Task LoadAdaptersAsync(bool useSaved, bool autoDetect)
    {
        if (_loading) return;

        var pickedEthernet = _ethernet.SelectedItem as NetworkAdapterInfo;
        var pickedWifi = _wifi.SelectedItem as NetworkAdapterInfo;
        _loading = true;
        _auto.Enabled = _refresh.Enabled = _save.Enabled = _ethernet.Enabled = _wifi.Enabled = false;
        _feedback.ForeColor = UiTheme.TextMuted;
        _feedback.Text = "Scanning network adapters…";

        string? scanError = null;
        try
        {
            var adapters = await Task.Run(_network.GetAdapters);
            if (_form.IsDisposed) return;

            var ethernet = FindAdapter(adapters,
                useSaved ? _config.EthernetAdapterId : pickedEthernet?.DeviceId,
                useSaved ? _config.EthernetAdapterName : pickedEthernet?.Name);
            var wifi = FindAdapter(adapters,
                useSaved ? _config.WifiAdapterId : pickedWifi?.DeviceId,
                useSaved ? _config.WifiAdapterName : pickedWifi?.Name);

            if (useSaved || autoDetect)
            {
                var suggestions = new AppConfig();
                _network.AutoSelectDefaults(suggestions, adapters);
                var suggestedEthernet = FindAdapter(adapters, suggestions.EthernetAdapterId, suggestions.EthernetAdapterName);
                var suggestedWifi = FindAdapter(adapters, suggestions.WifiAdapterId, suggestions.WifiAdapterName);
                if (autoDetect)
                {
                    (ethernet, wifi) = (suggestedEthernet, suggestedWifi);
                }
                else
                {
                    if (ethernet is null && suggestedEthernet != wifi) ethernet = suggestedEthernet;
                    if (wifi is null && suggestedWifi != ethernet) wifi = suggestedWifi;
                }
            }

            BindList(_ethernet, adapters, AdapterKind.Ethernet, ethernet);
            BindList(_wifi, adapters, AdapterKind.Wireless, wifi);
            _scanMessage = autoDetect
                ? "Adapters detected. Review the selections, then save."
                : useSaved ? null : "Adapter list refreshed.";
        }
        catch (Exception ex)
        {
            scanError = $"Could not scan adapters. Select Refresh to try again. {ex.Message}";
        }
        finally
        {
            _loading = false;
            if (!_form.IsDisposed)
            {
                _auto.Enabled = _refresh.Enabled = true;
                _ethernet.Enabled = _ethernet.Items.Count > 0;
                _wifi.Enabled = _wifi.Items.Count > 0;
                UpdateSelection();
                if (scanError is not null)
                {
                    _feedback.ForeColor = UiTheme.Warning;
                    _feedback.Text = scanError;
                }
            }
        }
    }

    private static NetworkAdapterInfo? FindAdapter(IReadOnlyList<NetworkAdapterInfo> adapters, string? id, string? name) =>
        adapters.FirstOrDefault(adapter => NetworkAdapterService.NamesEqual(adapter.Name, name)) ??
        adapters.FirstOrDefault(adapter => string.Equals(adapter.DeviceId.Trim(), id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void BindList(ListBox list, IReadOnlyList<NetworkAdapterInfo> adapters, AdapterKind kind, NetworkAdapterInfo? selected)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            var ordered = adapters.OrderBy(adapter => adapter.IsVirtualOrIgnored)
                .ThenByDescending(adapter => adapter.Kind == kind);
            foreach (var adapter in ordered)
            {
                list.Items.Add(adapter);
            }

            list.SelectedIndex = selected is null ? -1 : list.Items.IndexOf(selected);
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _scanMessage = null;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var ethernet = _ethernet.SelectedItem as NetworkAdapterInfo;
        var wifi = _wifi.SelectedItem as NetworkAdapterInfo;
        UpdateSelectionLabel(_ethernetSelection, ethernet);
        UpdateSelectionLabel(_wifiSelection, wifi);

        var duplicate = ethernet is not null && wifi is not null && NetworkAdapterService.NamesEqual(ethernet.Name, wifi.Name);
        _save.Enabled = ethernet is not null && wifi is not null && !duplicate;
        var virtualSelection = ethernet?.IsVirtualOrIgnored == true || wifi?.IsVirtualOrIgnored == true;
        _feedback.ForeColor = duplicate || virtualSelection ? UiTheme.Warning : UiTheme.TextMuted;
        _feedback.Text = (_ethernet.Items.Count, ethernet, wifi) switch
        {
            (0, _, _) => "No network adapters found. Select Refresh to scan again.",
            _ when duplicate => "Choose a different adapter for each connection.",
            (_, null, null) => "Select an Ethernet adapter and a Wi-Fi adapter, or try Auto-detect.",
            (_, null, _) => "Select your Ethernet adapter to continue.",
            (_, _, null) => "Select your Wi-Fi adapter to continue.",
            _ when virtualSelection => "A virtual, VPN, or Bluetooth adapter is selected. Review before saving.",
            _ => _scanMessage ?? "Your selections take effect the next time you switch from the tray."
        };
    }

    private void UpdateSelectionLabel(Label label, NetworkAdapterInfo? adapter)
    {
        label.Text = adapter is null ? "No adapter selected" : $"Selected: {adapter.Name}";
        _details.SetToolTip(label, adapter is null ? null : $"{adapter.Name}\n{adapter.Description}");
    }

    private void SaveAndClose()
    {
        if (!_save.Enabled || _ethernet.SelectedItem is not NetworkAdapterInfo ethernet ||
            _wifi.SelectedItem is not NetworkAdapterInfo wifi)
        {
            return;
        }

        if ((ethernet.IsVirtualOrIgnored || wifi.IsVirtualOrIgnored) &&
            MessageBox.Show(_form, "A selected adapter appears to be virtual, VPN, or Bluetooth. Save this pair?",
                _form.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        var updated = new AppConfig
        {
            EthernetAdapterId = ethernet.DeviceId,
            EthernetAdapterName = ethernet.Name,
            WifiAdapterId = wifi.DeviceId,
            WifiAdapterName = wifi.Name,
            ShowToastOnSwitch = _showPreferences ? _notifications.Checked : _config.ShowToastOnSwitch,
            RunAtStartup = _showPreferences ? _startup.Checked : _config.RunAtStartup
        };

        try
        {
            var wasRegistered = _showPreferences && StartupManager.IsEnabled();
            var registrationChanged = _showPreferences && wasRegistered != updated.RunAtStartup;
            if (registrationChanged)
            {
                StartupManager.SetEnabled(updated.RunAtStartup);
            }

            try
            {
                updated.Save();
            }
            catch (Exception saveError) when (registrationChanged)
            {
                try
                {
                    StartupManager.SetEnabled(wasRegistered);
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        $"{saveError.Message}\nStartup could not be restored: {rollbackError.Message}", saveError);
                }

                throw;
            }

            _config.EthernetAdapterId = updated.EthernetAdapterId;
            _config.EthernetAdapterName = updated.EthernetAdapterName;
            _config.WifiAdapterId = updated.WifiAdapterId;
            _config.WifiAdapterName = updated.WifiAdapterName;
            _config.ShowToastOnSwitch = updated.ShowToastOnSwitch;
            _config.RunAtStartup = updated.RunAtStartup;
            _form.DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_form, $"Could not save settings.\n\n{ex.Message}",
                _form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
