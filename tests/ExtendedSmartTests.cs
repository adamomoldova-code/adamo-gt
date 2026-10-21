using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class ExtendedSmartTests
    {
        public static void Run()
        {
            QueriesOnlyTheRequestedPhysicalDrive();
            StartupDoesNotStartTheExtendedReader();
            CacheClearsStaleDataAndTracksDiskIdentity();
            DirectNvmeDoesNotNeedAHelper();
            NativeEnduranceAndZeroCountersRemainExplicit();
            AtaPercentRequiresSpecificEvidence();
            WarningsDoNotReplaceRemainingEndurance();
            UntrustedAndIncompleteResponsesRemainUnknown();
            MissingHelperIsRecoverable();
            Console.WriteLine("PASS: extended SMART (read-only command, deferred fallback/cache, identity, endurance evidence, warning flags, bounded/error responses)");
        }

        private static void QueriesOnlyTheRequestedPhysicalDrive()
        {
            string arguments = SmartctlSmartReader.BuildArguments(12, @"C:\ADAMO GT\tools\drivedb.h");
            Require(arguments == "-a -j -b exit -q noserial -l ssd -B \"C:\\ADAMO GT\\tools\\drivedb.h\" /dev/pd12", "fixed allowlist of read-only arguments, strict checksum validation and quoted database path");
            bool rejected = false;
            try { SmartctlSmartReader.BuildArguments(256, "db"); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Windows physical disk index must fit the documented range");
            rejected = false;
            try { SmartctlSmartReader.BuildArguments(0, "db\" -s on"); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "database path cannot inject arguments");
        }

        private static void StartupDoesNotStartTheExtendedReader()
        {
            int directCalls = 0, extendedCalls = 0;
            SmartReader reader = new SmartReader(delegate(uint index) { directCalls++; return new SmartReading(); }, delegate(DiskSnapshot disk)
            {
                extendedCalls++;
                return new SmartReading { HealthPercent = 83, PowerOnHours = 200 };
            });
            DiskSnapshot target = new DiskSnapshot { Index = 2, Name = "SATA SSD", SizeBytes = 500000000000 };
            DateTime now = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
            reader.Apply(target, now, false);
            Require(directCalls == 1 && extendedCalls == 0 && target.SmartStatus.Contains("после открытия окна"), "initial inventory defers helper and says pending");
            reader.Apply(target, now.AddSeconds(1), true);
            Require(directCalls == 1 && extendedCalls == 1 && target.HealthPercent == 83 && target.PowerOnHours == 200, "first sensor read can extend an already-cached direct failure");
            reader.Apply(target, now.AddSeconds(29), true);
            Require(extendedCalls == 1, "fallback result cached between sensor ticks");
        }

        private static void CacheClearsStaleDataAndTracksDiskIdentity()
        {
            int calls = 0;
            SmartReader reader = new SmartReader(delegate(uint index) { return new SmartReading(); }, delegate(DiskSnapshot target)
            {
                calls++;
                if (calls == 2) { throw new InvalidOperationException("removed"); }
                return new SmartReading { HealthPercent = 72, PowerOnHours = 900 };
            });
            DateTime now = DateTime.UtcNow;
            DiskSnapshot disk = new DiskSnapshot { Index = 0, Name = "old" };
            reader.Apply(disk, now, true);
            reader.Apply(disk, now.AddSeconds(30), true);
            Require(calls == 2 && !disk.HealthPercent.HasValue && !disk.PowerOnHours.HasValue, "failed refresh clears stale health and hours");
            reader.Apply(disk, now.AddSeconds(31), true);
            Require(calls == 2, "unknown/error fallback cached too");
            disk.Name = "replacement";
            reader.Apply(disk, now.AddSeconds(32), true);
            Require(calls == 3 && disk.HealthPercent == 72, "a replacement model on same physical index invalidates cache");
        }

        private static void DirectNvmeDoesNotNeedAHelper()
        {
            int extendedCalls = 0;
            SmartReader reader = new SmartReader(delegate(uint index) { return new SmartReading { HealthPercent = 95, PowerOnHours = 5738, CriticalWarning = 0 }; }, delegate(DiskSnapshot disk)
            {
                extendedCalls++;
                return new SmartReading { HealthPercent = 10 };
            });
            DiskSnapshot target = new DiskSnapshot();
            reader.Apply(target, DateTime.UtcNow, true);
            Require(extendedCalls == 0 && target.HealthPercent == 95 && target.PowerOnHours == 5738, "working read-only native NVMe path keeps priority");
        }

        private static void NativeEnduranceAndZeroCountersRemainExplicit()
        {
            Dictionary<string, object> nvme = Response("NVMe", 0);
            nvme["nvme_smart_health_information_log"] = Map("percentage_used", 5, "critical_warning", 0);
            nvme["power_on_time"] = Map("hours", 0);
            nvme["temperature"] = Map("current", 32);
            SmartReading result = Parse(nvme, 0);
            Require(result.HealthPercent == 95 && result.PowerOnHours == 0 && result.Temperature == 32 && result.CriticalWarning == 0, "explicit NVMe readings include valid zero counters");
            nvme["nvme_smart_health_information_log"] = Map("percentage_used", 120, "critical_warning", 0);
            Require(Parse(nvme, 0).HealthPercent == 0, "exhausted endurance is zero, separate from critical warning");
            Dictionary<string, object> scsi = Response("SCSI", 0);
            scsi["scsi_percentage_used_endurance_indicator"] = 12;
            Require(Parse(scsi, 0).HealthPercent == 88, "SCSI endurance indicator interpreted as percentage used");
        }

        private static void AtaPercentRequiresSpecificEvidence()
        {
            Dictionary<string, object> root = Response("ATA", 0);
            root["endurance_used"] = Map("current_percent", 0);
            root["ata_smart_attributes"] = Map("table", new object[] { Map("id", 231, "name", "Unknown_Attribute", "value", 100, "raw", Map("value", 0)) });
            Require(!Parse(root, 0).HealthPercent.HasValue, "generic normalized value and inferred top-level default cannot produce 100 percent");
            root["ata_device_statistics"] = Statistics(9, false);
            Require(!Parse(root, 0).HealthPercent.HasValue, "invalid device-statistics entry rejected");
            root["ata_device_statistics"] = Statistics(9, true);
            Require(Parse(root, 0).HealthPercent == 91, "valid page 7 offset 8 is percentage used endurance");
            root["ata_device_statistics"] = Statistics(110, true);
            Require(Parse(root, 0).HealthPercent == 0, "used endurance above 100 percent clamps to zero");
            root.Remove("ata_device_statistics");
            root["in_smartctl_database"] = true;
            root["rotation_rate"] = 0;
            root["ata_smart_attributes"] = Map("table", new object[] { Map("id", 177, "name", "Wear_Leveling_Count", "value", 84) });
            Require(!Parse(root, 0).HealthPercent.HasValue, "a wear count does not automatically mean percentage even for a known SSD");
            root["ata_smart_attributes"] = Map("table", new object[] { Map("id", 202, "name", "Percent_Lifetime_Remain", "value", 84) });
            Require(Parse(root, 0).HealthPercent == 84, "known explicit percent remaining attribute can supply normalized remaining endurance");
            root["in_smartctl_database"] = false;
            Require(!Parse(root, 0).HealthPercent.HasValue, "unrecognized model does not inherit database semantics");
            root["in_smartctl_database"] = true;
            root["ata_smart_attributes"] = Map("table", new object[] { Map("name", "SSD_Life_Left", "value", 200) });
            Require(!Parse(root, 0).HealthPercent.HasValue, "normalized 200 does not turn into perfect health");
            root["ata_smart_attributes"] = Map("table", new object[] { Map("name", "SSD_Life_Left", "value", 80), Map("name", "Percent_Lifetime_Remain", "value", 50) });
            Require(!Parse(root, 0).HealthPercent.HasValue, "conflicting endurance indicators remain unknown");
            root["ata_smart_attributes"] = Map("table", new object[] { Map("name", "SSD_Life_Left", "value", 0) });
            Require(Parse(root, 0).HealthPercent == 0, "explicit named zero resource is not mistaken for a missing field");
        }

        private static void WarningsDoNotReplaceRemainingEndurance()
        {
            Dictionary<string, object> root = Response("ATA", 24);
            root["smart_status"] = Map("passed", false);
            root["ata_device_statistics"] = Statistics(5, true);
            SmartReading result = Parse(root, 24);
            Require(result.HealthPercent == 95 && result.SmartPassed == false && result.Status.Contains("неисправности") && !result.CriticalWarning.HasValue, "ATA failure separate from remaining endurance and NVMe warning byte");
            root = Response("ATA", 16);
            root["smart_status"] = Map("passed", true);
            Require(Parse(root, 16).SmartPassed == false, "current threshold failure overrides generic passed flag");
            Require(Parse(Response("ATA", 8), 8).SmartPassed == false, "explicit failing exit bit is surfaced even if passed flag is absent");
            root = Response("ATA", 196);
            root["power_on_time"] = Map("hours", 100);
            result = Parse(root, 196);
            Require(result.PowerOnHours == 100 && result.Status.Contains("журнале ошибок") && result.Status.Contains("прошлых самотестов") && result.Status.Contains("часть команд"), "history and partial reads retain useful data while surfacing warnings");
        }

        private static void UntrustedAndIncompleteResponsesRemainUnknown()
        {
            Dictionary<string, object> root = Response("ATA", 0);
            Require(!Parse(root, 0).HealthPercent.HasValue && !Parse(root, 0).PowerOnHours.HasValue, "missing fields remain unknown");
            root["device"] = Map("name", "/dev/pd10", "protocol", "ATA");
            root["power_on_time"] = Map("hours", 1234);
            Require(!Parse(root, 0).PowerOnHours.HasValue, "exact physical-drive identity required, no model/order matching");
            root = Response("ATA", 0);
            root["power_on_time"] = Map("hours", -1);
            root["temperature"] = Map("current", 65535);
            Require(!Parse(root, 0).PowerOnHours.HasValue && !Parse(root, 0).Temperature.HasValue, "sentinels and negative counters rejected");
            root["power_on_time"] = Map("hours", "9007199254740993");
            Require(!Parse(root, 0).PowerOnHours.HasValue, "unexpected numeric strings are not silently coerced");
            root["smart_support"] = Map("enabled", false);
            Require(Parse(root, 0).Status.Contains("не меняет"), "disabled SMART is reported without enabling");
            Require(!Parse(Response("ATA", 2), 2).HealthPercent.HasValue, "open failure rejected");
            Require(SmartctlSmartReader.Parse(0, new SmartctlProcessResult { TimedOut = true }).Status.Contains("12 секунд"), "timeout is recoverable unavailable result");
            Require(SmartctlSmartReader.Parse(0, new SmartctlProcessResult { OutputTooLarge = true }).Status.Contains("размер"), "oversized output rejected");
            Require(!SmartctlSmartReader.Parse(0, new SmartctlProcessResult { Output = "not-json" }).HealthPercent.HasValue, "malformed helper output rejected");
            Require(Parse(Response("ATA", 0), 8).Status.Contains("не подтверждён"), "process and JSON exit-status mismatch rejected");
        }

        private static void MissingHelperIsRecoverable()
        {
            SmartReading result = new SmartctlSmartReader(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Read(new DiskSnapshot());
            Require(!result.HealthPercent.HasValue && result.Status.Contains("распакуйте"), "incomplete portable bundle remains usable and explains missing helper");
        }

        private static Dictionary<string, object> Statistics(int used, bool valid)
        {
            return Map("pages", new object[] { Map("number", 7, "table", new object[] { Map("offset", 8, "value", used, "flags", Map("valid", valid)) }) });
        }
        private static Dictionary<string, object> Response(string protocol, int exitCode)
        {
            return Map("smartctl", Map("exit_status", exitCode), "device", Map("name", "/dev/pd0", "protocol", protocol));
        }
        private static SmartReading Parse(Dictionary<string, object> root, int exitCode)
        {
            return SmartctlSmartReader.Parse(0, new SmartctlProcessResult { Output = new JavaScriptSerializer().Serialize(root), ExitCode = exitCode });
        }
        private static Dictionary<string, object> Map(params object[] pairs)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            for (int index = 0; index < pairs.Length; index += 2) { result[(string)pairs[index]] = pairs[index + 1]; }
            return result;
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException("Extended SMART: " + message); } }
    }
}
