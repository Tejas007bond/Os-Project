# USB Monitor (WMI-based)

A Windows console application that watches for USB device arrivals/removals in real time and automatically disables any USB device whose Vendor ID / Product ID (VID/PID) is not on an administrator-defined whitelist.

## How it works

The application subscribes to WMI's `__InstanceCreationEvent` and `__InstanceDeletionEvent` against `Win32_PnPEntity`, which fire on every physical USB device arrival and removal, regardless of whether a driver needs to be installed. For each arrival:

1. The device's `DeviceID` is parsed for a `VID_xxxx&PID_xxxx` pattern.
2. The VID/PID is checked against `whitelist.txt`.
3. If it matches, the event is logged and nothing else happens.
4. If it does not match, the event is logged as an alert and the device is disabled via WMI's `Disable()` method on the matching `Win32_PnPEntity`.

Removal events are logged only; they do not trigger any whitelist check.

## Requirements

- Windows (WMI and `Win32_PnPEntity` are Windows-only; there is no cross-platform equivalent).
- .NET Framework or .NET 6+ (either works).
- `System.Management` reference (built in on .NET Framework; add the `System.Management` NuGet package on .NET Core/.NET 5+).
- Administrator privileges at runtime — required both to disable devices via WMI and to reliably query all `Win32_PnPEntity` instances.

## Build

```
dotnet build
```

or open/build the project in Visual Studio. No external dependencies beyond `System.Management`.

## Run

Launch the compiled executable **as Administrator**. Running without elevation causes the program to print an error and exit immediately — this is expected behavior, not a bug.

```
UsbMonitor.exe
```

The console prints a startup message once the WMI watchers are active, then logs every USB `ADD`/`REMOVE` event as it happens. Press `Ctrl+C` to stop.

## Configuration

All files live under `C:\ProgramData\UsbMonitor`, created automatically on first run:

| File | Purpose |
|---|---|
| `whitelist.txt` | One `VID_xxxx&PID_xxxx` entry per line. Case-insensitive; blank lines and lines starting with `#` are ignored. A default entry (`VID_80EE&PID_CAFE`) is created on first run — replace or extend it with your organization's approved devices. |
| `usb_events.txt` | Full log of every ADD/REMOVE event detected, timestamped. |
| `usb_alerts.log` | Log of only the devices that were blocked (unauthorized ADD events). |

To find a device's VID/PID: Device Manager → right-click the device → Properties → Details tab → **Hardware Ids**.

## Testing

1. Run as Administrator and confirm the startup message appears with no WMI watcher errors.
2. Plug in a whitelisted device — expect `[OK] Allowed` in the console and a corresponding line in `usb_events.txt`.
3. Plug in a non-whitelisted device — expect `[!] ALERT` followed by either `[SUCCESS]` (device disabled) or a `[WARNING] Disable() returned code N`. Check Device Manager for the disabled-device icon.
4. Unplug a device — expect a `REMOVE` log line; no whitelist check or blocking occurs on removal.

If no events appear at all when plugging in a device, add a temporary catch-all log at the top of `OnDeviceEvent` to confirm whether WMI events are reaching the app before assuming a logic bug — see the Troubleshooting section.

## Troubleshooting

- **No events fire at all**: confirm WMI itself is healthy (`winmgmt /verifyrepository` from an admin prompt), and check whether another security/EDR product is holding an exclusive WMI event subscription.
- **`Disable()` returns a non-zero code**: common codes are `5` (access denied — some composite USB devices only allow disabling their parent node, not a child function), `15` (dependent services running), and `22` (not supported for this device class).
- **A device you need gets disabled during testing**: re-enable it via Device Manager → right-click the device → Enable device, or unplug/replug it.

## Known limitations

- Only devices exposing a standard `VID_xxxx&PID_xxxx` pattern in their `DeviceID` are matched against the whitelist; other USB PnP entities (e.g. some composite child nodes or hubs) are logged but not whitelist-checked.
- The whitelist is VID/PID based only — it does not distinguish between two physically different devices that share the same VID/PID (e.g. two identical flash drive models).
- This tool blocks device operation at the OS level (`Disable()`); it does not prevent a device from being physically connected, and a technically sophisticated user with local admin rights could re-enable a blocked device manually.
