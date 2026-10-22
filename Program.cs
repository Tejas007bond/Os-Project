using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsbMonitorWmi {
    static class Program {
        // File paths
        static readonly string logDir = @"C:\ProgramData\UsbMonitor";              // Root folder for config + logs
        static readonly string whitelistPath = Path.Combine(logDir, "whitelist.txt"); // Approved VID/PID entries
        static readonly string eventLogPath = Path.Combine(logDir, "usb_events.txt");  // Every event + diagnostics
        static readonly string alertLogPath = Path.Combine(logDir, "usb_alerts.log");  // Blocked devices only

        // Kept alive for the life of the process so the watchers aren't garbage collected
        static ManagementEventWatcher? arrivalWatcher;  // Fires when a PnP entity appears (ADD)
        static ManagementEventWatcher? removalWatcher;  // Fires when a PnP entity disappears (REMOVE)

        // Serializes file writes: WMI threads can log while the UI thread is busy
        static readonly object logLock = new object();

        // Tray UI (lives on the UI thread)
        static NotifyIcon? trayIcon;                                        // Tray icon; null after Exit
        static readonly ApplicationContext appContext = new ApplicationContext(); // Keeps the loop alive, no main window

        // Never shown. It exists only to own a window handle so the WMI watcher
        // threads can hop back onto the UI thread before touching the tray icon.
        static readonly Form dispatcher = new Form();

        // Coalesces the burst of PnP events one physical plug produces (parent node
        // plus composite children) down to a single balloon notification.
        static readonly Dictionary<string, DateTime> recentlyNotified = new Dictionary<string, DateTime>(); // "ACTION|VID&PID" -> last shown (UTC)
        static readonly TimeSpan notifyCoalesceWindow = TimeSpan.FromSeconds(5); // Duplicates inside this window are suppressed

        // Entry point: elevation check, one-time setup, then the tray message loop.
        [STAThread]
        static void Main() {
            // 1. Must run elevated: WMI Disable() and some PnP queries require admin rights
            if (!IsRunAsAdmin()) {
                MessageBox.Show(
                    "This program must be run as Administrator.\r\n\r\n" +
                    "Right-click the executable and choose 'Run as administrator'.",
                    "USB Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 2. Init directories / whitelist
            Directory.CreateDirectory(logDir);
            // First run only: seed a placeholder whitelist so the file exists to edit.
            if (!File.Exists(whitelistPath)) {
                File.WriteAllText(whitelistPath, "VID_80EE&PID_CAFE" + Environment.NewLine);
                LogLine(eventLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] Created default whitelist.txt.");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Force the marshalling handle onto the UI thread before any WMI event can fire.
            _ = dispatcher.Handle;

            CreateTrayIcon();

            // 3. Subscribe to WMI instance creation/deletion for USB PnP entities.
            //    If the watchers fail there is nothing to monitor, so clean up and quit.
            if (!StartWatchers()) {
                trayIcon?.Dispose();
                return;
            }

            // Runs with no visible window: this message loop keeps the tray icon
            // alive and delivers notifications until "Exit" is chosen.
            Application.Run(appContext);
        }

        // Builds the tray icon and its right-click menu (UI thread only).
        static void CreateTrayIcon() {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open event log", null, (s, e) => OpenInEditor(eventLogPath));
            menu.Items.Add("Open alert log", null, (s, e) => OpenInEditor(alertLogPath));
            menu.Items.Add("Open whitelist", null, (s, e) => OpenInEditor(whitelistPath));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

            trayIcon = new NotifyIcon {
                Icon = System.Drawing.SystemIcons.Shield,
                Text = "USB Monitor - watching for USB devices",
                ContextMenuStrip = menu,
                Visible = true
            };

            trayIcon.DoubleClick += (s, e) => OpenInEditor(eventLogPath);
            trayIcon.ShowBalloonTip(3000, "USB Monitor running",
                "Watching for USB arrivals and removals in the background.", ToolTipIcon.Info);
        }

        // Starts both WMI subscriptions; returns false when WMI refused to start them.
        static bool StartWatchers() {
            // This fires on EVERY physical arrival/removal, unlike the ETW
            // Kernel-PnP 2003/2004 "driver install" events, which only fire
            // the first time Windows needs to install/stage a driver for a
            // given hardware ID. Polling interval below is WQL's own
            // "WITHIN n" clause (seconds), not a busy loop.
            try {
                // Arrival query: a new PnP entity instance appeared.
                var arrivalQuery = new WqlEventQuery(
                    "SELECT * FROM __InstanceCreationEvent WITHIN 1 " +
                    "WHERE TargetInstance ISA 'Win32_PnPEntity'");
                arrivalWatcher = new ManagementEventWatcher(arrivalQuery);
                arrivalWatcher.EventArrived += (s, e) => OnDeviceEvent(e, "ADD");
                arrivalWatcher.Start();

                // Removal query: an existing PnP entity instance went away.
                var removalQuery = new WqlEventQuery(
                    "SELECT * FROM __InstanceDeletionEvent WITHIN 1 " +
                    "WHERE TargetInstance ISA 'Win32_PnPEntity'");
                removalWatcher = new ManagementEventWatcher(removalQuery);
                removalWatcher.EventArrived += (s, e) => OnDeviceEvent(e, "REMOVE");
                removalWatcher.Start();

                return true;
            }
            catch (Exception ex) {
                LogLine(eventLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Failed to start WMI watchers: {ex.Message}");
                Notify("USB Monitor failed to start",
                    $"WMI watchers could not be started: {ex.Message}", ToolTipIcon.Error);
                return false;
            }
        }

        // Tray "Exit": stop the watchers, remove the icon, end the message loop.
        static void ExitApp() {
            try { arrivalWatcher?.Stop(); } catch { }
            try { removalWatcher?.Stop(); } catch { }

            arrivalWatcher?.Dispose();
            removalWatcher?.Dispose();

            if (trayIcon != null) {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }

            appContext.ExitThread();
        }

        // WMI callback: unwraps the device from the event and drops non-USB entities.
        // Runs on a WMI background thread, so only thread-safe work happens here.
        static void OnDeviceEvent(EventArrivedEventArgs e, string action) {
            try {
                // The PnP entity instance the event was raised for.
                var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];

                string? deviceId = target["DeviceID"] as string;  // e.g. USB\VID_0781&PID_5567\...
                string description = target["Description"] as string ?? target["Name"] as string ?? "(unknown)";

                // No DeviceID means nothing can be identified or blocked.
                if (string.IsNullOrEmpty(deviceId))
                    return;

                // Only care about USB devices; Win32_PnPEntity also reports non-USB PnP hardware.
                if (deviceId.IndexOf("USB", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                ProcessUsbEvent(deviceId, description, action);
            }
            catch (Exception ex) {
                LogLine(eventLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Failed to process device event: {ex.Message}");
            }
        }

        // Logs one USB event, then enforces the whitelist for arrivals only.
        static void ProcessUsbEvent(string deviceId, string description, string action) {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); // Local time used in both logs
            string portInfo = GetPortInfo(deviceId);                          // "Port_#0002.Hub_#0001 | COM3"

            // Groups 1 + 2 are the 4-hex-digit VID and PID from the DeviceID.
            Match match = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
            if (!match.Success) {
                // Not every USB PnP entity (e.g. composite child nodes, hubs) carries VID/PID
                // in this exact form; log it but don't try to whitelist-match it.
                LogLine(eventLogPath, $"[{timestamp}] {action} | (no VID/PID) | {portInfo} | {description} | ID: {deviceId}");
                return;
            }

            // Normalized key as written in whitelist.txt, e.g. VID_0781&PID_5567.
            string vidPid = $"VID_{match.Groups[1].Value.ToUpperInvariant()}&PID_{match.Groups[2].Value.ToUpperInvariant()}";
            LogLine(eventLogPath, $"[{timestamp}] {action} | {vidPid} | {portInfo} | {description} | ID: {deviceId}");

            // Removals are reported only: there is nothing to block anymore.
            if (action != "ADD") {
                NotifyThrottled($"{action}|{vidPid}", "USB device removed",
                    $"{vidPid}\n{portInfo}\n{description}", ToolTipIcon.Info);
                return;
            }

            // Re-read on every arrival, so edits take effect without a restart.
            var whitelist = LoadWhitelist();

            // Trusted device: report it and leave it enabled.
            if (whitelist.Contains(vidPid)) {
                NotifyThrottled($"{action}|{vidPid}", "USB device connected",
                    $"Allowed (whitelisted): {vidPid}\n{portInfo}", ToolTipIcon.Info);
                return;
            }

            // Unknown device: warn first, then record and block it.
            NotifyThrottled($"{action}|{vidPid}", "Unauthorized USB device blocked!",
                $"{vidPid}\n{portInfo}\n{description}", ToolTipIcon.Warning);

            LogLine(alertLogPath, $"[{timestamp}] BLOCKED: {vidPid} | {portInfo} | ({description}) | ID: {deviceId}");

            BlockDevice(deviceId, vidPid);
        }

        /// <summary>
        /// Builds the port columns for a log line: the USB hub port the device is
        /// attached to (e.g. "Port_#0002.Hub_#0001") and the COM port the device
        /// registered, if it is a serial device. Win32_PnPEntity exposes neither,
        /// so both come from the device's own Enum registry key.
        /// </summary>
        static string GetPortInfo(string deviceId) {
            string? location = ReadEnumValue(deviceId, "LocationInformation"); // e.g. "Port_#0002.Hub_#0001"
            // Windows reports no location for some nodes (composite children, hubs).
            string hubPort = string.IsNullOrWhiteSpace(location) ? "Port n/a" : location.Trim();
            return $"{hubPort} | {GetComPort(deviceId)}"; // Both columns for the log line
        }

        static string GetComPort(string deviceId) {
            // Serial devices record their assigned port name under Device Parameters.
            string? portName = ReadEnumValue(deviceId, "PortName", "Device Parameters");
            // Only accept a literal COMn name; anything else means "not a serial port".
            return portName != null && Regex.IsMatch(portName, @"^COM\d+$") ? portName : "COM n/a";
        }

        /// <summary>
        /// Reads a string value from HKLM\SYSTEM\CurrentControlSet\Enum\{deviceId}\[{subKey}].
        /// Returns null when the key or value does not exist.
        /// </summary>
        static string? ReadEnumValue(string deviceId, string valueName, string? subKey = null) {
            try {
                // subKey is optional: LocationInformation sits on the device key itself,
                // PortName lives one level deeper in "Device Parameters".
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}" +
                              (subKey == null ? string.Empty : "\\" + subKey);
                using var key = Registry.LocalMachine.OpenSubKey(path);
                return key?.GetValue(valueName) as string; // null when key/value is absent
            }
            catch {
                // Key missing / access denied: caller falls back to "n/a".
                return null;
            }
        }

        // Shows a notification unless the same key was already shown within the window.
        static void NotifyThrottled(string key, string title, string text, ToolTipIcon icon) {
            if (!ShouldNotify(key))
                return;

            Notify(title, text, icon);
        }

        // Records the key and returns true only if it has not been seen recently.
        static bool ShouldNotify(string key) {
            lock (recentlyNotified) {
                var now = DateTime.UtcNow;

                // Duplicate inside the coalesce window: log only, no balloon.
                if (recentlyNotified.TryGetValue(key, out var last) && now - last < notifyCoalesceWindow)
                    return false;

                recentlyNotified[key] = now;

                // Housekeeping so the dictionary cannot grow forever on a long run.
                if (recentlyNotified.Count > 100) {
                    foreach (var stale in recentlyNotified
                                 .Where(kv => now - kv.Value > notifyCoalesceWindow)
                                 .Select(kv => kv.Key)
                                 .ToList()) {
                        recentlyNotified.Remove(stale);
                    }
                }

                return true;
            }
        }

        // Thread-safe notification: marshals the balloon onto the UI thread.
        static void Notify(string title, string text, ToolTipIcon severity) {
            var trayHost = trayIcon; // Snapshot: the field can be nulled by Exit
            // Nothing to notify with once the icon is gone or the loop isn't up yet.
            if (trayHost == null || !dispatcher.IsHandleCreated)
                return;

            try {
                dispatcher.BeginInvoke(new Action(() => {
                    try {
                        trayHost.ShowBalloonTip(5000, title, text, severity);
                    }
                    catch {
                        // Tray icon already disposed while shutting down.
                    }
                }));
            }
            catch {
                // The UI thread is gone (shutting down); nothing to do.
            }
        }

        // Opens a log or whitelist file in whatever the user has associated.
        static void OpenInEditor(string path) {
            try {
                // Create it first so "Open" never fails on a log with no entries yet.
                if (!File.Exists(path))
                    File.WriteAllText(path, string.Empty);

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) {
                MessageBox.Show($"Could not open {path}:\r\n{ex.Message}",
                    "USB Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Reads the whitelist: trimmed, upper-cased, comments and blanks dropped.
        static string[] LoadWhitelist() {
            try {
                return File.ReadAllLines(whitelistPath)
                    .Select(l => l.Trim().ToUpperInvariant())      // match the VID/PID casing used elsewhere
                    .Where(l => l.Length > 0 && !l.StartsWith("#")) // skip comments / blank lines
                    .ToArray();
            }
            catch (Exception ex) {
                // Unreadable whitelist = empty whitelist = everything gets blocked; log why.
                LogLine(eventLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Could not read whitelist: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        // Disables the device(s) whose DeviceID matches, via WMI's Disable() method.
        static void BlockDevice(string deviceId, string vidPid) {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            try {
                // Escape single quotes too, not just backslashes, since WQL string
                // literals use ' as the delimiter.
                string escapedId = EscapeWql(deviceId);
                string query = $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{escapedId}'";

                using (var searcher = new ManagementObjectSearcher(query)) {
                    // Usually one instance, but a DeviceID can surface on more than one node.
                    var matches = searcher.Get().Cast<ManagementObject>().ToList();

                    // Nothing to disable: report instead of claiming success.
                    if (matches.Count == 0) {
                        string msg = $"No WMI PnP entity found matching DeviceID '{deviceId}'; cannot block.";
                        LogLine(alertLogPath, $"[{timestamp}] WARNING: {vidPid} | {msg}");
                        Notify("Block failed", $"{vidPid}: {msg}", ToolTipIcon.Warning);
                        return;
                    }

                    foreach (var device in matches) {
                        using (device) {
                            var outParams = (ManagementBaseObject)device.InvokeMethod("Disable", null);
                            uint returnValue = (uint)outParams["ReturnValue"]; // 0 = success

                            // 0 means the device was disabled successfully.
                            if (returnValue == 0) {
                                LogLine(alertLogPath, $"[{timestamp}] DISABLED: {vidPid} | ID: {deviceId}");
                            }
                            else {
                                // Common non-zero codes: 5 = Access Denied (need admin),
                                // 15 = Dependent services running, 22 = Not implemented for this device.
                                LogLine(alertLogPath, $"[{timestamp}] WARNING: Disable() returned code {returnValue} for {vidPid} | ID: {deviceId}");
                                Notify("Block failed",
                                    $"{vidPid}: Disable() returned code {returnValue}.", ToolTipIcon.Warning);
                            }
                        }
                    }
                }
            }
            catch (ManagementException mex) {
                LogLine(alertLogPath, $"[{timestamp}] ERROR: WMI error while blocking {vidPid}: {mex.Message}");
            }
            catch (Exception ex) {
                LogLine(alertLogPath, $"[{timestamp}] ERROR: Failed to block {vidPid}: {ex.Message}");
            }
        }

        // Escapes backslashes and quotes so the value is safe inside a WQL string literal.
        static string EscapeWql(string value) {
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        // Appends one line to a log file; serialized across threads and never throws.
        static void LogLine(string path, string line) {
            lock (logLock) {
                try {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch {
                    // Logging must never take the monitor down.
                }
            }
        }

        // True when this process is elevated (needed for Disable() and full PnP queries).
        static bool IsRunAsAdmin() {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent()) {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
    }
}
