using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class TemperatureDiagnosticsTests
    {
        public static int Main(string[] args)
        {
            try
            {
                CpuSelection();
                GpuSelection();
                Availability();
                Console.WriteLine("PASS: temperature acquisition and availability (3 groups)");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void CpuSelection()
        {
            Require(HardwareMonitor.SelectCpuTemperature(new[] { R("CPU Package", 58), R("Core Max", 62) }).Value == 58,
                "Intel package remains the primary CPU reading, as in the live elevated HP probe.");
            Require(HardwareMonitor.SelectCpuTemperature(new[] { R("CPU Package", null), R("P-Core #1", 54), R("E-Core #1", 49) }).Value == 54,
                "Unavailable package temperature falls back to the hottest reported CPU core.");
            Require(HardwareMonitor.SelectCpuTemperature(new[] { R("CPU (Tctl/Tdie)", 85), R("Core (Tdie)", 65) }).Value == 65,
                "Physical AMD die temperature has priority over an offset-bearing control reading.");
            Require(HardwareMonitor.SelectCpuTemperature(new[] { R("CPU (Tctl/Tdie)", 68) }).Value == 68,
                "Combined AMD Tctl/Tdie remains supported when no separate die reading exists.");
            Require(!HardwareMonitor.SelectCpuTemperature(new[] { R("P-Core #1 Distance to TjMax", 42), R("GPU Core", 51), R("ACPI Thermal Zone", 31) }).Value.HasValue,
                "Distance-to-limit, GPU and ACPI zone readings cannot masquerade as CPU temperature.");
            Require(!HardwareMonitor.SelectCpuTemperature(new[] { R("CPU Package", double.NaN), R("Core Max", double.PositiveInfinity), R("Core #1", null) }).Value.HasValue,
                "Invalid or unavailable sensors stay unknown.");
            Require(HardwareMonitor.SelectCpuTemperature(new[] { R("CPU Package", 0) }).Value == 0,
                "A real zero temperature is not treated as missing.");
        }

        public static void GpuSelection()
        {
            var gpu = new GpuSnapshot { IsIntegrated = true };
            HardwareMonitor.ApplyGpuTemperature(gpu, new KeyValuePair<string, double?>[0], true);
            Require(!gpu.Temperature.HasValue && gpu.TemperatureStatus == "Драйвер Intel не предоставляет отдельную температуру GPU.",
                "Working IGCL without a temperature sensor is explicitly unsupported, not a missing CPU privilege.");
            HardwareMonitor.ApplyGpuTemperature(gpu, new[] { R("GPU Core", 44) }, true);
            Require(gpu.Temperature == 44 && gpu.TemperatureStatus.Contains("GPU Core"),
                "An Intel driver that does expose its GPU sensor is supported.");
            HardwareMonitor.ApplyGpuTemperature(gpu, new[] { R("GPU Core", null), R("GPU Memory", 77), R("GPU Hot Spot", 82) }, false);
            Require(!gpu.Temperature.HasValue && gpu.TemperatureStatus.Contains("временно"),
                "A sleeping or temporarily unavailable GPU clears its old value; VRAM/hotspot cannot replace core temperature.");
            HardwareMonitor.ApplyGpuTemperature(gpu, new[] { R("GPU", 51) }, false);
            Require(gpu.Temperature == 51, "Legacy GPU sensor name remains supported.");
            HardwareMonitor.ApplyGpuTemperature(gpu, new[] { R("GPU Core", double.NaN), R("GPU", double.NegativeInfinity) }, false);
            Require(!gpu.Temperature.HasValue, "NaN and infinity must never be shown as GPU temperatures.");
        }

        public static void Availability()
        {
            var snapshot = new Snapshot();
            HardwareMonitor.ApplyTemperatureStatus(snapshot, false, "");
            Require(snapshot.CpuTemperatureStatus.StartsWith("Нет доступного датчика CPU") && snapshot.CpuTemperatureStatus.Contains("дополнительный драйвер"), "Missing direct CPU access is explained without requiring an installation.");
            HardwareMonitor.ApplyTemperatureStatus(snapshot, true, "");
            Require(snapshot.CpuTemperatureStatus.StartsWith("Нужны права администратора"), "Installed driver without elevation gets the access action.");
            snapshot.IsAdmin = true;
            HardwareMonitor.ApplyTemperatureStatus(snapshot, true, "");
            Require(snapshot.CpuTemperatureStatus == "Датчик CPU не предоставил температуру.", "An elevated unsupported CPU must not loop on an elevation instruction.");
            var gpu = new GpuSnapshot { IsIntegrated = true };
            HardwareMonitor.ApplyGpuTemperature(gpu, new KeyValuePair<string, double?>[0], true);
            snapshot.Gpus.Add(gpu);
            snapshot.CpuTemperature = 58;
            snapshot.CpuTemperatureStatus = "Датчик CPU: CPU Package.";
            HardwareMonitor.ApplyTemperatureStatus(snapshot, true, "");
            Require(snapshot.CpuTemperature == 58 && !gpu.Temperature.HasValue && gpu.TemperatureStatus.Contains("Intel"),
                "The actual HP topology reports CPU temperature without fabricating an Iris Xe temperature.");
            snapshot.IsAdmin = false;
            snapshot.CpuTemperature = null;
            HardwareMonitor.ApplyTemperatureStatus(snapshot, true, "");
            Require(snapshot.CpuTemperatureStatus.StartsWith("Нужны права") && gpu.TemperatureStatus.Contains("Intel"),
                "An unrelated CPU privilege requirement must not erase a known GPU driver limitation.");
            var failed = new Snapshot();
            failed.Gpus.Add(new GpuSnapshot());
            HardwareMonitor.ApplyTemperatureStatus(failed, true, "Ошибка подключения датчиков.");
            Require(failed.CpuTemperatureStatus == "Ошибка подключения датчиков." && failed.Gpus[0].TemperatureStatus == failed.CpuTemperatureStatus,
                "Initialization failure is preserved for both unmatched sensors.");
            var independent = new Snapshot();
            independent.Gpus.Add(new GpuSnapshot { Temperature = 56, TemperatureStatus = "Windows WDDM" });
            HardwareMonitor.ApplyTemperatureStatus(independent, false, "", "CPU provider failed");
            Require(independent.CpuTemperatureStatus == "CPU provider failed" && independent.Gpus[0].Temperature == 56
                && independent.Gpus[0].TemperatureStatus == "Windows WDDM", "CPU initialization failure does not erase independent GPU telemetry.");
            var firmware = new Snapshot { CpuTemperature = 60, CpuTemperatureStatus = "CPU OEM via WMI" };
            firmware.Gpus.Add(new GpuSnapshot { Temperature = 56, TemperatureStatus = "Windows WDDM" });
            HardwareMonitor.ApplyTemperatureStatus(firmware, false, "LHM initialization failed", "");
            Require(firmware.SensorStatus == "Датчики CPU и GPU доступны." && firmware.CpuTemperatureStatus == "CPU OEM via WMI",
                "Working Windows/OEM sensors remain usable even when the optional provider fails.");
            string json = new JavaScriptSerializer().Serialize(snapshot);
            Snapshot roundTrip = new JavaScriptSerializer().Deserialize<Snapshot>(json);
            Require(roundTrip.CpuTemperatureStatus == snapshot.CpuTemperatureStatus && roundTrip.Gpus[0].TemperatureStatus == gpu.TemperatureStatus,
                "Exported reports retain the precise availability reasons.");
        }

        private static KeyValuePair<string, double?> R(string name, double? value) { return new KeyValuePair<string, double?>(name, value); }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
