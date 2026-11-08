# LAN Wi‑Fi Switcher

Windows 11 system-tray app that instantly toggles between your Ethernet (LAN) and Wi‑Fi adapters so traffic switches immediately.

Tray-only: no main window and no taskbar button on launch. Requires Administrator elevation (UAC) because enabling/disabling adapters needs it.

## Features

- **Left-click** the tray icon → toggle LAN ↔ Wi‑Fi (enables the target adapter, disables the other).
- **Right-click** tray menu:
  - **Settings…** — pick adapters, toast on switch, startup toggle
  - **Choose adapters…** — focused adapter picker with auto-detect
  - **Toggle now** — same as left-click
  - **Run at startup** — checkable; writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  - **Exit**
- Tray **tooltip** and **icon** reflect the current mode (LAN vs Wi‑Fi), with a short animation on toggle.
- Switching enables the target adapter first; disabling the previous one is **best-effort** with a short settle/poll (no hard error if Ethernet becomes the path even while Wi‑Fi Disable is slow).
- Config persisted in `%AppData%\LanWifiSwitcher\config.json`.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- Administrator rights at runtime (embedded `app.manifest` requests `requireAdministrator`)

## Build

```powershell
git clone https://github.com/aliefank/lan-wifi-switcher.git
cd lan-wifi-switcher
dotnet restore LanWifiSwitcher.sln
dotnet build LanWifiSwitcher.sln -c Release
```

The executable is written to:

```text
src\LanWifiSwitcher\bin\Release\net8.0-windows\LanWifiSwitcher.exe
```

### Publish a single-folder (or single-file) build

```powershell
dotnet publish src\LanWifiSwitcher\LanWifiSwitcher.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

Self-contained (no machine-wide .NET runtime required):

```powershell
dotnet publish src\LanWifiSwitcher\LanWifiSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

## Run

1. Double-click `LanWifiSwitcher.exe` (or run from Terminal).
2. Accept the UAC prompt.
3. The app appears only in the **system tray** (you may need to show hidden icons).
4. On first run it auto-detects a sensible Ethernet and Wi‑Fi pair — confirm via **Choose adapters…** or **Settings…**.

### Left-click / right-click behavior

| Action | Result |
|--------|--------|
| Left-click tray icon | Instantly toggles: if currently on Wi‑Fi → enable LAN & disable Wi‑Fi; otherwise → enable Wi‑Fi & disable LAN. Optional balloon toast if enabled in Settings. |
| Right-click tray icon | Opens the context menu (Settings, Choose adapters, Toggle now, Run at startup, Exit). |

Adapter enable/disable uses WMI `Win32_NetworkAdapter` `Enable`/`Disable`, with a `netsh interface set interface … admin=` fallback.

## Install as startup

### From the app (recommended)

1. Right-click the tray icon → check **Run at startup**, **or**
2. Open **Settings…** → check **Run at Windows startup** → Save.

This creates a value named `LanWifiSwitcher` under:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
```

pointing at the current `.exe` path. Uncheck to remove it.

### Manually (Startup folder)

1. Press `Win+R`, run `shell:startup`.
2. Create a shortcut to `LanWifiSwitcher.exe`.

### Manually (Registry)

```powershell
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v LanWifiSwitcher /t REG_SZ /d "\"C:\Path\To\LanWifiSwitcher.exe\"" /f
```

## Config file

```text
%AppData%\LanWifiSwitcher\config.json
```

Example:

```json
{
  "ethernetAdapterId": "2",
  "ethernetAdapterName": "Ethernet",
  "wifiAdapterId": "5",
  "wifiAdapterName": "Wi-Fi",
  "showToastOnSwitch": true,
  "runAtStartup": true
}
```

## Project layout

```text
LanWifiSwitcher.sln
src/LanWifiSwitcher/
  LanWifiSwitcher.csproj
  Program.cs                 # Single-instance + elevation check
  app.manifest               # requireAdministrator
  TrayAppContext.cs          # Tray icon, clicks, menu
  NetworkAdapterService.cs   # WMI / netsh enable-disable
  AppConfig.cs               # %AppData% persistence
  StartupManager.cs          # HKCU Run
  SettingsForm.cs
  AdapterChooserForm.cs
  IconFactory.cs             # Mode-colored tray icons
```

## Notes

- Only one instance runs at a time.
- Auto-detect prefers ncpa.cpl aliases **`Ethernet`** and **`Wi‑Fi`**, and ignores Bluetooth / TAP-Windows / OpenVPN / other virtual NICs.
- Toggle flips admin enable/disable only — an **unplugged Ethernet cable is fine**; link state is not required.
- Adapter resolve matches by **normalized** connection alias first (Unicode hyphen / NBSP / ZWSP safe), then DeviceID; Auto-detect prefers exact `"Wi-Fi"` / `"Ethernet"` aliases (merged from WMI + NetworkInterface) and Realtek 8922AE / 2.5GbE description hints.
- Adapter classification uses `.NET` interface type, WMI `AdapterTypeId`, and Wi‑Fi chipset signals (Realtek 8922AE / RTL88xx, MediaTek, “Wi‑Fi 6/7”). Virtual adapters are classified before Ethernet NIC-type heuristics.
- Settings / Choose adapters use a modern dark panel UI and list every NIC with type/status hints.
- If a toggle fails, the error lists what was searched and which adapters were found; confirm elevation and that names match `ncpa.cpl`.

## License

MIT
