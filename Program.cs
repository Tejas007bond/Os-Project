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

    }
}