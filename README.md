# USB Monitor (WMI-based)

A Windows background utility that runs in the system tray, watches USB device arrivals and removals in real time, raises a Windows notification for every event, and automatically disables any USB device whose Vendor ID / Product ID (VID/PID) is not on an administrator-defined whitelist. Every event is logged with the port it was plugged into.

---

## Features

- **Runs in the background** — no console window. Starts into the system tray and stays out of the way until it has something to report.
- **Desktop notifications** — a Windows notification (balloon/toast, shown in the Action Center) for each event: allowed device connected, unauthorized device blocked, device removed, and block/startup failures.
- **Tray icon with quick actions** — open the event log, alert log or whitelist, or exit, from the right-click menu.
- **Whitelist enforcement** — anything not listed in `whitelist.txt` is disabled through WMI as soon as it is plugged in.
- **Port logging** — every event records the USB hub port the device attached to (`Port_#0002.Hub_#0001`) and the COM port it registered (`COM3`), not just its VID/PID.
- **Two audit logs** — a full event stream and a separate log of blocked devices plus the result of each block attempt.
- **Notification de-duplication** — a single physical plug generates several PnP events (parent node plus composite children); they are collapsed into one notification per VID/PID and event type within 5 seconds so the desktop isn't flooded, while every node is still written to the log.
- **Safe failure** — disk/logging errors never take the monitor down, and elevation/WMI startup failures are reported in a dialog or notification instead of silently dying.

---

## How to run

### 1. Build

```bash
dotnet build
```

or open `UsbMonitorETW.csproj` in Visual Studio and build it.

The executable is produced at:

```
bin\Debug\net10.0-windows\UsbMonitorETW.exe
```

### 2. Run it as Administrator

The app must be elevated — it disables PnP devices and reads PnP state, both of which require administrator rights. Pick whichever is convenient:

- **Explorer**: right-click `UsbMonitorETW.exe` → **Run as administrator**.
- **Terminal (already elevated)**: `.\bin\Debug\net10.0-windows\UsbMonitorETW.exe`
- **Visual Studio**: start Visual Studio itself *as administrator*, then F5 — the debugged process inherits its elevation. Running `dotnet run` from a normal prompt is *not* elevated and will only show the admin dialog.

If it is started without elevation, it shows an error dialog and exits — this is expected behavior, not a bug.

### 3. Confirm it is running

There is no console output. Success looks like:

- a shield icon in the notification area (system tray), and
- a "USB Monitor running" notification.

Plug a device in or out to see notifications appear. If nothing happens when you plug a device in, check `usb_events.txt` for `[ERROR]` lines — see Troubleshooting.

### 4. Stop it

Right-click the tray icon → **Exit**. Watchers are stopped and the icon is removed.

### Starting automatically at login (optional)

It does not install itself as a startup item. To have it start with Windows *without* a UAC prompt each time, create a Scheduled Task:

1. Task Scheduler → **Create Task**.
2. **General** → tick **Run with highest privileges**.
3. **Triggers** → New → **At log on** (your account).
4. **Actions** → New → Start a program → point it at `UsbMonitorETW.exe`.
5. Save.

Placing a shortcut in `shell:startup` also works, but the shortcut must be configured to *Run as administrator* and will prompt for elevation at each login.

---

## How it works

### Startup sequence

1. Verify the process is elevated; otherwise show an error dialog and exit.
2. Create `C:\ProgramData\UsbMonitor` and a default `whitelist.txt` if they do not exist.
3. Create the tray icon and menu.
4. Subscribe to WMI device-change events (below).
5. Run a GUI message loop with no visible window — it keeps the tray icon alive and delivers notifications until **Exit** is chosen.

### Detecting devices

The app subscribes to WMI's `__InstanceCreationEvent` and `__InstanceDeletionEvent` against `Win32_PnPEntity`. These fire on **every** physical USB arrival and removal, not only the first time Windows installs a driver for a new hardware ID (which is all the ETW Kernel-PnP "driver install" events would report). The `WITHIN 1` clause in the WQL query is WMI's own polling interval in seconds, not a busy loop.

Events are delivered on WMI's background threads. Because the tray icon may only be touched from the UI thread, notifications are marshalled there through a hidden window handle, and the 5-second de-duplication check runs before anything is shown.

### Per-event processing

For each event, the device's `DeviceID` (e.g. `USB\VID_0781&PID_5567\4C530001...`) is examined:

1. Non-USB PnP entities are ignored (Windows reports lots of unrelated hardware through that same class).
2. `DeviceID` is parsed for a `VID_xxxx&PID_xxxx` pattern.
3. Port information is resolved from the device's own `HKLM\SYSTEM\CurrentControlSet\Enum\<DeviceID>` registry key — the hub port from `LocationInformation`, the COM port from `Device Parameters\PortName`. (`Win32_PnPEntity` exposes neither, so WMI cannot supply this.)
4. The event is written to `usb_events.txt`.

On **ADD**:

- If the VID/PID is in `whitelist.txt` → an "allowed" notification, nothing else happens.
- If it is not → a "blocked" warning notification, a line in `usb_alerts.log`, and the device is disabled by invoking WMI's `Disable()` on the matching `Win32_PnPEntity`. Success (`DISABLED`) or the returned error code is recorded in the alert log, and a failure is also notified.

On **REMOVE**: a removal notification and a log line only — no whitelist check and no blocking.

Entities without a parseable VID/PID (some composite child nodes and hubs) are logged with `(no VID/PID)` but are not whitelist-checked.

---

## Configuration

All files live under `C:\ProgramData\UsbMonitor`, created automatically on first run:

| File | Purpose |
|---|---|
| `whitelist.txt` | One `VID_xxxx&PID_xxxx` entry per line. Case-insensitive; blank lines and lines starting with `#` are ignored. A default entry (`VID_80EE&PID_CAFE`) is created on first run — replace or extend it with your organization's approved devices. |
| `usb_events.txt` | Full log of every ADD/REMOVE event detected, timestamped, with port info and `[ERROR]`/`[INFO]` diagnostics. |
| `usb_alerts.log` | Log of only the devices that were blocked (unauthorized ADD events), plus the result of the disable attempt. |

### Tray menu

| Action | What it does |
|---|---|
| Double-click the icon | Open `usb_events.txt` |
| Open event log | Open `usb_events.txt` |
| Open alert log | Open `usb_alerts.log` |
| Open whitelist | Open `whitelist.txt` for editing (changes are picked up on the next device event — no restart needed) |
| Exit | Stop the watchers and quit |

### Log format

```
[2026-10-22 22:52:11] ADD | VID_0781&PID_5567 | Port_#0003.Hub_#0001 | COM n/a | SanDisk USB Device | ID: USB\VID_0781&PID_5567\4C530001...
```

The two port columns are:

- **USB hub port** — from the device's `LocationInformation`, e.g. `Port_#0003.Hub_#0001` (port number on that hub). `Port n/a` when Windows does not report a location.
- **COM port** — the COM port the device registered (`COM3`). `COM n/a` for devices that are not serial ports, and for removals (the port is already gone by then).

To find a device's VID/PID: Device Manager → right-click the device → Properties → Details tab → **Hardware Ids**.

---

## Requirements

- Windows (WMI and `Win32_PnPEntity` are Windows-only; there is no cross-platform equivalent).
- .NET 10 SDK (the project targets `net10.0-windows` and uses Windows Forms for the tray icon).
- `System.Management` NuGet package.
- Administrator privileges at runtime — required both to disable devices via WMI and to reliably query all `Win32_PnPEntity` instances.

---

## Testing

1. Run as Administrator and confirm the tray icon appears along with the "USB Monitor running" notification.
2. Plug in a whitelisted device — expect an info notification and an `ADD` line with the port info in `usb_events.txt`.
3. Plug in a non-whitelisted device — expect a "blocked" warning notification, a line in `usb_alerts.log`, and either `DISABLED` (device disabled) or `WARNING: Disable() returned code N`. Check Device Manager for the disabled-device icon.
4. Unplug a device — expect a removal notification and a `REMOVE` log line with its port info.
5. Right-click → **Open event log** / **Open alert log** / **Open whitelist** to confirm the menu works, then **Exit** and confirm the icon disappears.

If no events appear at all when plugging in a device, check `usb_events.txt` for any `[ERROR]` lines before assuming a logic bug — see the Troubleshooting section.

---

## Troubleshooting

- **Admin dialog appears and the app exits**: it was launched without elevation. Use *Run as administrator* (or the elevated Scheduled Task described above).
- **No events fire at all**: confirm WMI itself is healthy (`winmgmt /verifyrepository` from an admin prompt), and check whether another security/EDR product is holding an exclusive WMI event subscription.
- **`Disable()` returns a non-zero code**: common codes are `5` (access denied — some composite USB devices only allow disabling their parent node, not a child function), `15` (dependent services running), and `22` (not supported for this device class).
- **A device you need gets disabled during testing**: re-enable it via Device Manager → right-click the device → Enable device, or unplug/replug it.
- **Tray icon missing**: it may be hidden behind the notification-area overflow arrow; drag it out to pin it.
- **Notifications don't show up**: check Windows Settings → System → Notifications, and make sure notifications and *Do not disturb / Focus assist* aren't suppressing them.

---

## Known limitations

- Only devices exposing a standard `VID_xxxx&PID_xxxx` pattern in their `DeviceID` are matched against the whitelist; other USB PnP entities (e.g. some composite child nodes or hubs) are logged but not whitelist-checked.
- The whitelist is VID/PID based only — it does not distinguish between two physically different devices that share the same VID/PID (e.g. two identical flash drive models).
- Port info is what Windows reports. Composite devices can report a different location for their child nodes, and removals may show `Port n/a` once the device's registry information is gone.
- This tool blocks device operation at the OS level (`Disable()`); it does not prevent a device from being physically connected, and a technically sophisticated user with local admin rights could re-enable a blocked device manually.
- Because it needs a tray icon and notifications, it runs as a per-user desktop app, not a Windows service — a service cannot show tray icons or user notifications.
