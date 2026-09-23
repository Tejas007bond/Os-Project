using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing;

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
        }
    }
}