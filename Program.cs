using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace UsbMonitorWmi {
    class Program {
        // File paths
        static readonly string logDir = @"C:\ProgramData\UsbMonitor";
        static readonly string whitelistPath = Path.Combine(logDir, "whitelist.txt");
        static readonly string eventLogPath = Path.Combine(logDir, "usb_events.txt");
        static readonly string alertLogPath = Path.Combine(logDir, "usb_alerts.log");

        // Kept alive for the life of the process so the watchers aren't garbage collected
        static ManagementEventWatcher arrivalWatcher;
        static ManagementEventWatcher removalWatcher;

        static readonly object logLock = new object();

        static void Main(string[] args) {
            // 1. Must run elevated: WMI Disable() and some PnP queries require admin rights
            if (!IsRunAsAdmin()) {
                Console.WriteLine("[-] Error: This program must be run as Administrator.");
                Console.WriteLine("Right-click the executable (or Visual Studio) and choose 'Run as Administrator'.");
                return;
            }

            // 2. Init directories / whitelist
            Directory.CreateDirectory(logDir);
            if (!File.Exists(whitelistPath)) {
                File.WriteAllText(whitelistPath, "VID_80EE&PID_CAFE" + Environment.NewLine);
                Console.WriteLine("[*] Created default whitelist.txt. Add allowed VID_PID combinations here (one per line).");
            }

            Console.WriteLine("[*] Starting USB monitor via WMI device-change events...");
            Console.WriteLine("[*] Press Ctrl+C to stop.\n");

            // 3. Subscribe to WMI instance creation/deletion for USB PnP entities.
            //    This fires on EVERY physical arrival/removal, unlike the ETW
            //    Kernel-PnP 2003/2004 "driver install" events, which only fire
            //    the first time Windows needs to install/stage a driver for a
            //    given hardware ID. Polling interval below is WQL's own
            //    "WITHIN n" clause (seconds), not a busy loop.
            try {
                var arrivalQuery = new WqlEventQuery(
                    "SELECT * FROM __InstanceCreationEvent WITHIN 1 " +
                    "WHERE TargetInstance ISA 'Win32_PnPEntity'");
                arrivalWatcher = new ManagementEventWatcher(arrivalQuery);
                arrivalWatcher.EventArrived += (s, e) => OnDeviceEvent(e, "ADD");
                arrivalWatcher.Start();

                var removalQuery = new WqlEventQuery(
                    "SELECT * FROM __InstanceDeletionEvent WITHIN 1 " +
                    "WHERE TargetInstance ISA 'Win32_PnPEntity'");
                removalWatcher = new ManagementEventWatcher(removalQuery);
                removalWatcher.EventArrived += (s, e) => OnDeviceEvent(e, "REMOVE");
                removalWatcher.Start();
            }
            catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to start WMI watchers: {ex.Message}");
                return;
            }

            // Block main thread; watchers deliver events on background threads.
            var exitSignal = new System.Threading.ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                exitSignal.Set();
            };
            exitSignal.WaitOne();

            arrivalWatcher?.Stop();
            removalWatcher?.Stop();
            Console.WriteLine("[*] Stopped.");
        }

        static void OnDeviceEvent(EventArrivedEventArgs e, string action) {
            try {
                var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];

                string deviceId = target["DeviceID"] as string;
                string description = target["Description"] as string ?? target["Name"] as string ?? "(unknown)";

                if (string.IsNullOrEmpty(deviceId))
                    return;

                // Only care about USB devices; Win32_PnPEntity also reports non-USB PnP hardware.
                if (deviceId.IndexOf("USB", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                ProcessUsbEvent(deviceId, description, action);
            }
            catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to process device event: {ex.Message}");
            }
        }

        static void ProcessUsbEvent(string deviceId, string description, string action) {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            Match match = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
            if (!match.Success) {
                // Not every USB PnP entity (e.g. composite child nodes, hubs) carries VID/PID
                // in this exact form; log it but don't try to whitelist-match it.
                LogLine(eventLogPath, $"[{timestamp}] {action} | (no VID/PID) | {description} | ID: {deviceId}");
                return;
            }

            string vidPid = $"VID_{match.Groups[1].Value.ToUpperInvariant()}&PID_{match.Groups[2].Value.ToUpperInvariant()}";
            string logEntry = $"[{timestamp}] {action} | {vidPid} | {description} | ID: {deviceId}";

            LogLine(eventLogPath, logEntry);
            Console.WriteLine($"[LOG] {action} detected: {vidPid} ({description})");

            if (action != "ADD")
                return;

            var whitelist = LoadWhitelist();

            if (whitelist.Contains(vidPid)) {
                Console.WriteLine($"[OK] Allowed: {vidPid} is whitelisted");
                return;
            }

            Console.WriteLine($"[!] ALERT: Unauthorized device {vidPid} detected! Blocking...");
            LogLine(alertLogPath, $"[{timestamp}] BLOCKED: {vidPid} ({description}) | ID: {deviceId}");

            BlockDevice(deviceId);
        }

        static string[] LoadWhitelist() {
            try {
                return File.ReadAllLines(whitelistPath)
                    .Select(l => l.Trim().ToUpperInvariant())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .ToArray();
            }
            catch (Exception ex) {
                Console.WriteLine($"[ERROR] Could not read whitelist: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        static void BlockDevice(string deviceId) {
            try {
                // Escape single quotes too, not just backslashes, since WQL string
                // literals use ' as the delimiter.
                string escapedId = deviceId.Replace("\\", "\\\\").Replace("'", "\\'");
                string query = $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{escapedId}'";

                using (var searcher = new ManagementObjectSearcher(query)) {
                    var matches = searcher.Get().Cast<ManagementObject>().ToList();

                    if (matches.Count == 0) {
                        Console.WriteLine($"[WARNING] No WMI PnP entity found matching DeviceID '{deviceId}'; cannot block.");
                        return;
                    }

                    foreach (var device in matches) {
                        using (device) {
                            var outParams = (ManagementBaseObject)device.InvokeMethod("Disable", null);
                            uint returnValue = (uint)outParams["ReturnValue"];

                            if (returnValue == 0) {
                                Console.WriteLine($"[SUCCESS] Device {deviceId} has been disabled.");
                            }
                            else {
                                // Common non-zero codes: 5 = Access Denied (need admin),
                                // 15 = Dependent services running, 22 = Not implemented for this device.
                                Console.WriteLine($"[WARNING] Disable() returned code {returnValue} for {deviceId}.");
                            }
                        }
                    }
                }
            }
            catch (ManagementException mex) {
                Console.WriteLine($"[ERROR] WMI error while blocking device: {mex.Message}");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to block device: {ex.Message}");
            }
        }

        static void LogLine(string path, string line) {
            lock (logLock) {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        static bool IsRunAsAdmin() {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent()) {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
    }
}