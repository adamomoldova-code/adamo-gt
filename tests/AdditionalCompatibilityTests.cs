using System;
using System.Collections.Generic;
using System.Linq;
using PcPulse;

namespace PcPulseTests
{
    public static class AdditionalCompatibilityTests
    {
        public static int Main(string[] args)
        {
            int failures = 0;
            foreach (Action test in new Action[] { CompoundPortClauses, LegacyConfigurationCasing, ExternalDisplayConnector })
            {
                try { test(); Console.WriteLine("PASS: " + test.Method.Name); }
                catch (Exception exception) { failures++; Console.WriteLine("FAIL: " + test.Method.Name + ": " + exception.Message); }
            }
            return failures == 0 ? 0 : 1;
        }

        public static void CompoundPortClauses()
        {
            foreach (string separator in new[] { ", ", " and ", ", and ", " &amp; " })
            {
                List<PortEntry> ports = Ports("2 USB Type-A ports" + separator + "1 USB Type-C port" + separator + "1 HDMI 2.0 port");
                Require(ports.Count == 3 && Count(ports, "USB-A") == 2 && Count(ports, "USB-C") == 1 && Count(ports, "HDMI") == 1,
                    "A compound list must preserve each connector/count; separator [" + separator + "] produced " + Describe(ports));
            }
            List<PortEntry> reverse = Ports("1 HDMI 2.0 port and 2 USB Type-C ports");
            Require(reverse.Count == 2 && Count(reverse, "HDMI") == 1 && Count(reverse, "USB-C") == 2,
                "A later connector cannot disappear behind the first one.");
            List<PortEntry> capabilities = Ports("2 USB Type-C ports with DisplayPort 1.4 and Power Delivery 3.0; 1 HDMI 2.0 port");
            Require(capabilities.Count == 2 && Count(capabilities, "USB-C") == 2 && Count(capabilities, "HDMI") == 1,
                "USB-C alternate-mode and power capabilities are not separate physical connectors.");
            List<PortEntry> option = Ports("2 USB Type-A ports, 1 Smart Card reader (optional), 1 HDMI 2.0 port");
            Require(option.Count == 3 && option.Single(p => p.Name == "Smart Card").Optional
                && option.Where(p => p.Name != "Smart Card").All(p => !p.Optional),
                "An optional connector must not mark earlier or later mandatory connectors optional.");
            Require(Ports("2 USB Type-A and USB Type-C ports").Count == 0
                && Ports("2 USB Type-A ports, USB Type-C port; 1 HDMI 2.0 port").Count == 0,
                "A shared/missing count for physically different USB sockets remains ambiguous.");
            List<PortEntry> nested = Ports("2 USB Type-A ports (1 USB port supports charging, 1 USB port supports wake); 1 HDMI 2.0 port");
            Require(nested.Count == 2 && Count(nested, "USB-A") == 2 && Count(nested, "HDMI") == 1,
                "Parenthetical descriptions cannot create additional physical connectors.");
        }

        public static void LegacyConfigurationCasing()
        {
            foreach (string field in new[] { "DiskMiB", "diskmib", "DISKMIB" })
            {
                BenchmarkSettings value = BenchmarkSettings.Parse("{\"SchemaVersion\":1,\"Profile\":\"quick-v1\",\"" + field
                    + "\":256,\"GpuWarmupSeconds\":2,\"GpuMeasurementSeconds\":10}");
                Require(value.Note.Length == 0 && value.Profile == "quick-v1" && value.DiskMiB == 256,
                    "A recognized legacy file-size field cannot be silently replaced with the default; " + field + " yielded " + value.DiskMiB + ".");
            }
            Require(BenchmarkSettings.Parse("{\"Profile\":\"quick-v1\"}").DiskMiB == 512,
                "A genuinely omitted legacy file size retains the historical 512 MB default.");
        }

        public static void ExternalDisplayConnector()
        {
            // Microsoft D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY: SDTVDONGLE = 14 is external.
            Require(ScreenInventory.InternalConnection(14) == false, "A known SDTV dongle connector is external, not unknown.");
            Require(ScreenInventory.InternalConnection(11) == true && ScreenInventory.InternalConnection(13) == true,
                "Embedded DisplayPort and UDI remain internal.");
            Require(!ScreenInventory.InternalConnection(7).HasValue && !ScreenInventory.InternalConnection(uint.MaxValue).HasValue,
                "Reserved/unknown connection codes remain unknown.");
        }

        private static List<PortEntry> Ports(string content)
        {
            return PortLookup.ExtractPorts("<table><tr><th>Ports</th><td>" + content + "</td></tr></table>");
        }

        private static int Count(List<PortEntry> ports, string name)
        {
            return ports.Where(port => port.Name == name).Sum(port => port.Count);
        }

        private static string Describe(List<PortEntry> ports)
        {
            return String.Join(", ", ports.Select(port => port.Count + " " + port.Name));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
