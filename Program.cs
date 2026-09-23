using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing;

namespace UsbMonitorETW {
    class Program {
        // File paths
        static string logDir = @"C:\ProgramData\UsbMonitor";
        static string whitelistPath = Path.Combine(logDir, "whitelist.txt");
        static string eventLogPath = Path.Combine(logDir, "usb_events.txt");
        static string alertLogPath = Path.Combine(logDir, "usb_alerts.log");

        // FIX 1: Capitalized 'Main' (C# is case-sensitive)
        static void Main(string[] args) {
            // 1. Ensuring running as administrator (required for ETW and Device Disabling)
            if (!IsRunAsAdmin()) {
                Console.WriteLine("[-] Error: This program must be run as Administrator.");
                Console.WriteLine("Please right-click Visual Studio and select 'Run as Administrator'.");
                return;
            }

            // 2. Initialize the directories and whitelist
            Directory