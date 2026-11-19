namespace LanWifiSwitcher;

/// <summary>
/// Tray-only application context: no main window, no taskbar button.
/// Left-click toggles LAN ↔ Wi‑Fi; right-click opens the menu.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly NetworkAdapterService _network = new();
    private readonly System.Windows.Forms.Timer _animTimer;
    private AppConfig _config;
    private Icon? _currentIcon;
    private bool _ownsCurrentIcon;
    private readonly ToolStripLabel _modeStatus = new() { TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripMenuItem _switchItem = new();
    private readonly ToolStripMenuItem _startupItem = new("Run at Windows startup");
    private bool _busy;
    private bool _dialogOpen;
    private bool _disposed;
    private IReadOnlyList<Icon>? _animFrames;
    private int _animIndex;
    private ConnectionMode _animSettleMode;
    private bool _toggleTransitionActive;
    private bool _animationsEnabled;
    private const int LoopIntervalMs = 72;
    private const int ToggleIntervalMs = 32;

    public TrayAppContext()
    {
        _config = AppConfig.Load();
        // Repair stale TAP/VPN/Bluetooth picks; prefer ncpa.cpl "Ethernet" + "Wi-Fi".
        _network.AutoSelectDefaults(_config, repairUnsuitable: true);
        _config.RunAtStartup = StartupManager.IsEnabled() || _config.RunAtStartup;
        StartupManager.SyncFromConfig(_config);
        _config.Save();

        _animTimer = new System.Windows.Forms.Timer { Interval = LoopIntervalMs };
        _animTimer.Tick += OnAnimTick;

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "LAN Wi‑Fi Switcher",
            ContextMenuStrip = BuildMenu()
        };

        _tray.MouseClick += OnTrayMouseClick;
        RefreshTrayPresentation(animateFrom: null);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        UiTheme.StyleTrayMenu(menu);
        _modeStatus.Font = UiTheme.SectionFont;
        _modeStatus.Padding = new Padding(4, 5, 10, 5);
        _switchItem.Click += async (_, _) => await ToggleNowAsync(showToast: _config.ShowToastOnSwitch);

        menu.Items.Add(_modeStatus);
        menu.Items.Add(_switchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Choose adapters…", null, (_, _) => OpenAdapterDialog(showPreferences: false));
        menu.Items.Add("Settings…", null, (_, _) => OpenAdapterDialog(showPreferences: true));

        _startupItem.Checked = _config.RunAtStartup;
        _startupItem.Click += (_, _) =>
        {
            var enabled = !_startupItem.Checked;
            try
            {
                StartupManager.SetEnabled(enabled);
                _config.RunAtStartup = enabled;
                _config.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not update startup registration:\n{ex.Message}",
                    "LAN Wi-Fi Switcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _config.RunAtStartup = StartupManager.IsEnabled();
            }

            _startupItem.Checked = _config.RunAtStartup;
        };
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        menu.Opening += (_, e) =>
        {
            if (_busy || _dialogOpen)
            {
                e.Cancel = true;
                return;
            }

            var mode = _network.DetectCurrentMode(_config);
            if (_animSettleMode != mode || _animationsEnabled != IconFactory.AnimationsEnabled)
                RefreshTrayPresentation(animateFrom: null, settleMode: mode);
            else
                UpdateMenuStatus(mode);
        };
        return menu;
    }

    private async void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            await ToggleNowAsync(showToast: _config.ShowToastOnSwitch);
        }
    }

    private async Task ToggleNowAsync(bool showToast)
    {
        if (_busy || _dialogOpen)
        {
            return;
        }

        _busy = true;
        _tray.Text = "LAN/Wi-Fi Switcher — switching adapters…";
        ConnectionMode? previous = null;
        ConnectionMode intended = ConnectionMode.Unknown;
        try
        {
            previous = await Task.Run(() => _network.DetectCurrentMode(_config));
            intended = SwitchTarget(previous.Value);
            var next = await Task.Run(() => _network.Toggle(_config));
            RefreshTrayPresentation(animateFrom: previous, settleMode: next);

            if (showToast)
            {
                ShowSuccessToast(previous.Value, next);
            }
        }
        catch (Exception ex)
        {
            // Race: Ethernet may already be up while Wi‑Fi Disable / route update finishes ~1s later.
            // Don't show a hard error if the intended path landed.
            await Task.Delay(1000);
            var landed = ConnectionMode.Unknown;
            try
            {
                landed = await Task.Run(() => _network.DetectCurrentMode(_config));
            }
            catch
            {
                // Keep the original switch error if the follow-up status check also fails.
            }
            if (previous is ConnectionMode from &&
                (landed == intended ||
                 (intended == ConnectionMode.Lan && landed is ConnectionMode.Lan or ConnectionMode.Both) ||
                 (intended == ConnectionMode.Wifi && landed is ConnectionMode.Wifi or ConnectionMode.Both)))
            {
                var settle = intended is ConnectionMode.Lan or ConnectionMode.Wifi ? intended : landed;
                RefreshTrayPresentation(animateFrom: from, settleMode: settle);
                if (showToast)
                {
                    ShowSuccessToast(from, settle);
                }

                return;
            }

            RefreshTrayPresentation(animateFrom: null, settleMode: landed);
            ShowToggleError(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowSuccessToast(ConnectionMode previous, ConnectionMode next)
    {
        _tray.BalloonTipTitle = "LAN Wi‑Fi Switcher";
        _tray.BalloonTipText = $"Switched to {ModeLabel(next)} (was {ModeLabel(previous)}).";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(2500);
    }

    private void OpenAdapterDialog(bool showPreferences)
    {
        if (_busy || _dialogOpen) return;

        _dialogOpen = true;
        try
        {
            using Form form = showPreferences
                ? new SettingsForm(_config, _network)
                : new AdapterChooserForm(_config, _network);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _config = AppConfig.Load();
                _startupItem.Checked = _config.RunAtStartup;
                RefreshTrayPresentation(animateFrom: null);
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void UpdateMenuStatus(ConnectionMode mode)
    {
        _modeStatus.Text = mode switch
        {
            ConnectionMode.Lan => "Ethernet enabled",
            ConnectionMode.Wifi => "Wi-Fi enabled",
            ConnectionMode.Both => "Both adapters enabled",
            ConnectionMode.Neither => "No active adapter",
            _ => "Adapter status unavailable"
        };
        _switchItem.Text = SwitchTarget(mode) == ConnectionMode.Lan ? "Switch to &Ethernet" : "Switch to &Wi-Fi";
    }

    private static ConnectionMode SwitchTarget(ConnectionMode mode) =>
        mode == ConnectionMode.Wifi ? ConnectionMode.Lan : ConnectionMode.Wifi;

    private void RefreshTrayPresentation(ConnectionMode? animateFrom, ConnectionMode? settleMode = null)
    {
        var mode = settleMode ?? _network.DetectCurrentMode(_config);
        UpdateMenuStatus(mode);
        _tray.Text = TruncateTooltip($"LAN/Wi-Fi Switcher — {_modeStatus.Text}");

        StopAnimation(disposeFrames: true);
        _animationsEnabled = IconFactory.AnimationsEnabled;

        if (_animationsEnabled && animateFrom is ConnectionMode from && from != mode)
        {
            _animSettleMode = mode;
            _toggleTransitionActive = true;
            _animFrames = IconFactory.CreateToggleAnimation(from, mode);
            _animIndex = 0;
            _animTimer.Interval = ToggleIntervalMs;
            ApplyIcon(_animFrames[0], ownsIcon: false);
            _animTimer.Start();
            return;
        }

        StartContinuousLoop(mode);
    }

    private void StartContinuousLoop(ConnectionMode mode)
    {
        _animSettleMode = mode;
        _toggleTransitionActive = false;

        if (!_animationsEnabled || !IconFactory.SupportsContinuousLoop(mode))
        {
            _animTimer.Stop();
            ApplyIcon(IconFactory.CreateForMode(mode), ownsIcon: true);
            return;
        }

        _animFrames = IconFactory.CreateContinuousLoop(mode);
        if (_animFrames.Count == 0)
        {
            ApplyIcon(IconFactory.CreateForMode(mode), ownsIcon: true);
            return;
        }

        _animIndex = 0;
        _animTimer.Interval = LoopIntervalMs;
        ApplyIcon(_animFrames[0], ownsIcon: false);
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_animFrames is null || _animFrames.Count == 0)
        {
            _animTimer.Stop();
            return;
        }

        if (_toggleTransitionActive)
        {
            if (_animIndex >= _animFrames.Count - 1)
            {
                StopAnimation(disposeFrames: true);
                StartContinuousLoop(_animSettleMode);
                return;
            }

            _animIndex++;
            ApplyIcon(_animFrames[_animIndex], ownsIcon: false);
            return;
        }

        _animIndex = (_animIndex + 1) % _animFrames.Count;
        ApplyIcon(_animFrames[_animIndex], ownsIcon: false);
    }

    private void StopAnimation(bool disposeFrames)
    {
        _animTimer.Stop();
        _toggleTransitionActive = false;
        if (disposeFrames && _animFrames is not null)
        {
            // Detach before disposing so NotifyIcon is not left holding a freed HICON.
            if (!_ownsCurrentIcon)
            {
                _tray.Icon = SystemIcons.Application;
                _currentIcon = null;
            }

            foreach (var frame in _animFrames)
            {
                frame.Dispose();
            }
        }

        _animFrames = null;
        _animIndex = 0;
    }

    private void ApplyIcon(Icon nextIcon, bool ownsIcon)
    {
        _tray.Icon = nextIcon;
        if (_ownsCurrentIcon)
        {
            _currentIcon?.Dispose();
        }

        _currentIcon = nextIcon;
        _ownsCurrentIcon = ownsIcon;
    }

    private void ExitApp() => ExitThread();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopAnimation(disposeFrames: true);
            _animTimer.Dispose();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            if (_ownsCurrentIcon)
            {
                _currentIcon?.Dispose();
            }

            _currentIcon = null;
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
            message + "\n\nOpen Choose adapters from the tray to check your Ethernet and Wi-Fi selections.",
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
