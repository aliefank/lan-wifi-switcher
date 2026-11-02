namespace LanWifiSwitcher;

/// <summary>
/// Tray-only application context: no main window, no taskbar button.
/// Left-click toggles LAN ↔ Wi‑Fi; right-click opens the menu.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly NetworkAdapterService _network = new();
    private AppConfig _config;
    private Icon? _currentIcon;
    private ToolStripMenuItem? _startupItem;
    private bool _busy;

    public TrayAppContext()
    {
        _config = AppConfig.Load();
        // Repair stale TAP/VPN/Bluetooth picks; prefer ncpa.cpl "Ethernet" + "Wi-Fi".
        _network.AutoSelectDefaults(_config, repairUnsuitable: true);
        _config.RunAtStartup = StartupManager.IsEnabled() || _config.RunAtStartup;
        StartupManager.SyncFromConfig(_config);
        _config.Save();

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "LAN Wi‑Fi Switcher",
            ContextMenuStrip = BuildMenu()
        };

        _tray.MouseClick += OnTrayMouseClick;
        RefreshTrayPresentation();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Choose adapters…", null, (_, _) => OpenAdapterChooser());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Toggle now", null, (_, _) => ToggleNow(showToast: true));
        menu.Items.Add(new ToolStripSeparator());

        _startupItem = new ToolStripMenuItem("Run at startup")
        {
            Checked = _config.RunAtStartup,
            CheckOnClick = true
        };
        _startupItem.CheckedChanged += (_, _) =>
        {
            _config.RunAtStartup = _startupItem.Checked;
            try
            {
                StartupManager.SetEnabled(_config.RunAtStartup);
                _config.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not update startup registration:\n{ex.Message}",
                    "LAN Wi‑Fi Switcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _startupItem.Checked = StartupManager.IsEnabled();
                _config.RunAtStartup = _startupItem.Checked;
            }
        };
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        return menu;
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ToggleNow(showToast: _config.ShowToastOnSwitch);
        }
    }

    private void ToggleNow(bool showToast)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            var previous = _network.DetectCurrentMode(_config);
            var next = _network.Toggle(_config);
            RefreshTrayPresentation();

            if (showToast)
            {
                var text = ModeLabel(next);
                _tray.BalloonTipTitle = "LAN Wi‑Fi Switcher";
                _tray.BalloonTipText = $"Switched to {text} (was {ModeLabel(previous)}).";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(2500);
            }
        }
        catch (Exception ex)
        {
            ShowToggleError(ex);
            RefreshTrayPresentation();
        }
        finally
        {
            Cursor.Current = Cursors.Default;
            _busy = false;
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config, _network);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _config = AppConfig.Load();
            if (_startupItem is not null)
            {
                _startupItem.Checked = _config.RunAtStartup;
            }

            RefreshTrayPresentation();
        }
    }

    private void OpenAdapterChooser()
    {
        using var form = new AdapterChooserForm(_config, _network);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _config = AppConfig.Load();
            RefreshTrayPresentation();
        }
    }

    private void RefreshTrayPresentation()
    {
        var mode = _network.DetectCurrentMode(_config);
        var nextIcon = IconFactory.CreateForMode(mode);
        _tray.Icon = nextIcon;
        _currentIcon?.Dispose();
        _currentIcon = nextIcon;

        // NotifyIcon.Text is capped at 63 chars and should stay on one line.
        _tray.Text = TruncateTooltip($"LAN/Wi‑Fi Switcher: {ModeLabel(mode)}");
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _currentIcon?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowToggleError(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? "Toggle failed for an unknown reason."
            : ex.Message;

        // Balloon toast for immediate visibility from the tray…
        _tray.BalloonTipTitle = "LAN Wi‑Fi Switcher — toggle failed";
        _tray.BalloonTipText = TruncateBalloon(message);
        _tray.BalloonTipIcon = ToolTipIcon.Error;
        _tray.ShowBalloonTip(6000);

        // …and a dialog with the full detail + recovery hint.
        MessageBox.Show(
            message + "\n\nTip: Right-click the tray icon → Choose adapters… and select your " +
            "Realtek / MediaTek / WLAN NIC (look for the [Wi‑Fi] hint).",
            "LAN Wi‑Fi Switcher — toggle failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static string ModeLabel(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Lan => "LAN",
        ConnectionMode.Wifi => "Wi‑Fi",
        ConnectionMode.Both => "Both enabled",
        ConnectionMode.Neither => "Both disabled",
        _ => "Unknown"
    };

    private static string TruncateTooltip(string text)
    {
        // NotifyIcon.Text max length is 63 characters on Windows.
        const int max = 63;
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 1)] + "…";
    }

    private static string TruncateBalloon(string text)
    {
        // Balloon tip body is larger than tooltip text; keep it readable.
        const int max = 250;
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 1)] + "…";
    }
}
