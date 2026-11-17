# Devices Battery

Battery level of your wireless mouse and headset, right in the Windows taskbar — next to the clock, with the native Windows 11 icons.

- One small, dependency-free `.exe` (C# / .NET Framework 4, which ships with Windows 10 and 11).
- Read-only: it only sends battery queries to your devices, never lighting, DPI or configuration commands.
- A device that goes to sleep stays listed with its last reading (dimmed) and refreshes once it wakes up.
- Low-battery notification at 15%.
- Updates itself from this repository's releases.

## Install

1. Download `DevicesBattery.exe` from the [latest release](https://github.com/blockdev2/devices-battery/releases/latest).
2. Put it in a folder of your choice (for example `%LOCALAPPDATA%\DevicesBattery`) and run it.
3. Right-click the widget and enable **Start with Windows**.

Windows SmartScreen may warn about an unsigned app the first time; choose **More info → Run anyway**.

## Using it

| Action | Result |
| --- | --- |
| Hover | Device names, and how old a reading is |
| Left click | Refresh now (it also refreshes every 60 s and after waking from sleep) |
| Right click | Refresh, Start with Windows, Check for updates, Exit |

Colors only carry meaning: red at 15% or less, a blue bolt while charging, dimmed for a sleeping device's last reading.
If the widget cannot attach to the taskbar (for example a vertical taskbar), the app falls back to tray icons.

## Updates

The app checks this repository's latest release 30 seconds after starting and then once a day. When a newer version exists it shows a notification; you can also use **Check for updates** in the menu.
The update downloads `DevicesBattery.exe` from the release over HTTPS, verifies the SHA-256 digest GitHub publishes for the file, replaces the running executable and restarts.

## Supported devices

| Vendor | Devices |
| --- | --- |
| Logitech | HID++ mice, keyboards and headsets on Unifying / Bolt / LIGHTSPEED receivers or cable; PRO X 2 LIGHTSPEED headset |
| Razer | Wireless mice and keyboards; BlackShark V2 HyperSpeed, V2 Pro (2023), V3, V3 Pro, V3 X; Barracuda X (2022) |
| SteelSeries | Arctis 1 / 7 / 9 / Pro / Nova family; Aerox 3 / 5 / 9, Prime, Rival 3 / 650 / Pro |
| Corsair | Void / HS70 / HS80; Virtuoso, Void V2, HS80 MAX and Slipstream devices |
| HyperX | Cloud III, III S, II, II Core, Alpha, Alpha 2, Flight, Stinger Core wireless |
| Glorious, Hitscan, G-Wolves | Wireless mice |
| Pulsar, Kysona, ATK / VXE, Redragon, HECATE | Wireless mice |
| LAMZU, Endgame Gear 4K, VAXEE, ZYGEN, WLMouse, Ninjutso | Wireless mice |
| ASUS ROG | Wireless mice and keyboards, Omni Receiver, Pelta headset |
| BenQ ZOWIE | EC-CW, DW series, U2 |
| Cooler Master | MM712, MM731 |
| Sony | INZONE H9 II, H5, Buds |
| Others | Motospeed X6, Delux M800 / M900 Pro, Attack Shark, Ajazz, Incott |
| Any brand | Devices exposing the standard HID battery usage, and Bluetooth devices that show a battery in Windows Settings |

Only the Logitech protocols have been verified on hardware by this project so far. The other protocols follow public community documentation; if a device shows a wrong value, open an issue with the output of `DevicesBattery.exe --test`.

## Command line

| Command | What it does |
| --- | --- |
| `DevicesBattery.exe --test` | Runs the self-test and prints every device found, then exits |
| `DevicesBattery.exe --update` | Checks for and installs an update without the UI |

## Build

No SDK needed; the C# compiler ships with Windows:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /win32manifest:src\app.manifest /r:System.Web.Extensions.dll /out:DevicesBattery.exe src\*.cs
```

## Project layout

| File | Purpose |
| --- | --- |
| `src/Hid.cs` | Win32 HID layer: enumeration, timed reads/writes, feature reports |
| `src/Logitech.cs` | Logitech HID++ 1.0 / 2.0 and the PRO X 2 headset |
| `src/Vendors.cs` | Every other vendor protocol |
| `src/Generic.cs` | Standard HID battery usage and Bluetooth battery |
| `src/Widget.cs` | The taskbar widget |
| `src/Updater.cs` | Self-update from GitHub Releases |
| `src/App.cs` | App lifetime, device list, menu, tray fallback |

## Adding a device

Each vendor driver finds its device by USB vendor and product ID, sends its read-only battery query and adds a `Reading` to the scan. Add the protocol to `src/Vendors.cs` (or a new file), register it in `Vendors.PollAll`, and publish a new release: running copies pick it up through the updater.
