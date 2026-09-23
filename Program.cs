using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing;
using System.Reflection.Metadata;

namespace UsbMonitorETW
{
    class Program
    {
        // File paths
        static string logDir = @"C:\ProgramData\UsbMonitor";
        static string whitelistPath = Path.Combine(logDir, "whitelist.txt");
        static string eventLogPath = Path.Combine(logDir, "usb_events.txt");
        static string alertLogPath = Path.Combine(logDir, "usb_alerts.log");

        static void main(String[] args)
        {
            // 1. Ensuring running as administrator (reuires for ETW and Device Disabling)
            if(!IsRunAsAdmin())
            {
                Console.WriteLine("[-] Error: This program must be run as Admistrator.");
                return;
            }

            // 2. Initialize the directories and whitelist
            Directory.CreateDirectory(logDir);
            if (!File.Exists(whitelistPath))
            {
                File.WriteAllText(whitelistPath, "VID_80EE&PID_CAFE\n");
                Console.WriteLine("[*] Created default whitelist.txt. Add allowed VID_PID combinations here.");
            }

            Console.WriteLine("[*] Starting Kernel-Level USB monitor via ETW...");
            Console.WriteLine("[*] Press Ctrl+C to stop.\n");

            // 3. Set up ETW session
            string sessionName = "UsbKernelMonitorSession";

            if (TraceEventSession.GetActiveSessionNames().Contains(sessionName)) // Check for any leftover session which may be crashed
            {
                TraceEventSession.GetActiveSession(sessionName).Stop();
            }

            using (var session = new TraceEventSession(sessionName))
            {
                // Enable the windows kernel pnp provider
                session.EnableProvider("Microsoft-Windows-Kernel-PnP");

                // Subscribe to all dynamic events from this provider
                session.Source.Dynamic.All += data =>
                {
                    if (data.ID == 2003 || data.ID == 2004)
                    {
                        string deviceId = data.PayloadByName("DeviceInstanceId") as string;
                        string description = data.PayloadByName("DeviceDescription") as string;

                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            ProcessUsbEvents(deviceId, description, data.ID == 2003 ? "ADD" : "REMOVE");
                        }
                    }
                };

                // Start processing events (blocks the main thread)
                session.Source.Process();
            }
        }

        static void ProcessUsbEvent(string deviceId, string description, string action)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Extract VID and PID
            Match match = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4}&PID_([0-9A-Fa-f]{4})");

            if(match.Success)
            {
                string vidPid = $"VID_{match.Groups[1].Value.ToUpper()}&PID_{match.Groups[2].Value.ToUpper()}";
                string logEntry = $"[{timestamp}] {action} | {vidPid} | {description} | ID: {deviceId}";

                // Log all events
                File.AppendAllText(eventLogPath, logEntry + Environment.NewLine);
                Console.WriteLine($"[LOG] {action} detected: {vidPid} ({description})");

                // Only check whitelist on ADD events
                if (action == "ADD")
                {
                    string[] whitelist = File.ReadAllLines(whitelistPath);

                    if(whitelist.Contains(vidPid))
                    {
                        Console.WriteLine($"[OK] Allowed: {vidPid} is whitelisted");
                    }
                    else
                    {
                        Console.WriteLine($"[!] ALERT: Unauthorized device {vidPid} detected! Blocking...");
                        File.AppendAllText(alertLogPath, $"[{timestamp}] BLOCKED: {vidPid} ({description}){Environment.NewLine}");

                        BlockBuilder(deviceId);

                    }
                }
            }
        }

        static void BlockDevice(string deviceId)
        {
            try
            {
                // Use WMI to find the PnP entity and disable it
                string query = $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{deviceId.Replace("\\", "\\\\")}'";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        // Invoke the Disable method (Equivalent to right-click -> Disable in Device Manager)
                        var outParams = device.InvokeMethod("Disable", null);
                        uint returnValue = (uint)(outParams?["ReturnValue"] ?? 1);

                        if (returnValue == 0)
                        {
                            Console.WriteLine($"[SUCCESS] Device {deviceId} has been disabled at the kernel level.");
                        }
                        else
                        {
                            Console.WriteLine($"[WARNING] Disable method returned code: {returnValue}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to block device: {ex.Message}");
            }
        }

        static bool IsRunAsAdmin()
        {

        }
    }
}