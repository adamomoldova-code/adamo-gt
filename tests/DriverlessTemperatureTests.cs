using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using PcPulse;

namespace PcPulseTests
{
    public static class DriverlessTemperatureTests
    {
        public static int Main(string[] args)
        {
            try { Run(); Console.WriteLine("PASS: driverless temperature providers (7 groups)"); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void Run()
        {
            CpuUnits(); CpuAttribution(); Availability(); ProviderFailuresAndFreshness(); WddmLayoutAndIdentity(); WddmFailuresAndFreshness(); ExistingSources();
        }

        private static void CpuUnits()
        {
            Equal(DriverlessTemperatureReader.ConvertNumericTemperature(625, 2, -1), 62.5, "OEM deci-Celsius");
            Equal(DriverlessTemperatureReader.ConvertNumericTemperature(140, 3, 0), 60, "OEM Fahrenheit");
            Equal(DriverlessTemperatureReader.ConvertNumericTemperature(33315, 4, -2), 60, "OEM Kelvin");
            Require(!DriverlessTemperatureReader.ConvertNumericTemperature(50, 65, 0).HasValue, "CPU thermal index percentages are not temperatures");
            Require(!DriverlessTemperatureReader.ConvertNumericTemperature(50, 2, null).HasValue, "Missing units exponent is not guessed");
            Require(!DriverlessTemperatureReader.ConvertNumericTemperature(uint.MaxValue, 2, -6).HasValue, "OEM sentinel is rejected before scaling");
            Require(!DriverlessTemperatureReader.ConvertNumericTemperature(double.NaN, 2, 0).HasValue, "NaN rejected");
            Require(!DriverlessTemperatureReader.ConvertNumericTemperature(50, 2, double.NaN).HasValue, "NaN exponent rejected");
            Require(!DriverlessTemperatureReader.ConvertNumericTemperature(0, 2, 0).HasValue, "OEM unsupported zero is not presented as temperature");
            var win32 = CpuRow("CPU temperature", 627);
            Equal(DriverlessTemperatureReader.SelectCpuProbe(new[] { win32 }, true).Value, 62.7, "Win32 documented tenths Celsius");
            win32["CurrentReading"] = null;
            win32["NominalReading"] = 500;
            Require(!DriverlessTemperatureReader.SelectCpuProbe(new[] { win32 }, true).Value.HasValue, "Win32 nominal value cannot replace normally empty CurrentReading");
        }

        private static void CpuAttribution()
        {
            foreach (string label in new[] { "ACPI Thermal Zone", "CPU ACPI Thermal Zone", "TZ00", "CPU Thermal Index", "Core temperature", "CPU ambient", "CPU proximity", "CPU socket", "CPU motherboard", "CPU fan", "CPU margin", "CPU Distance to TjMax", "GPU processor", "CPU critical limit" })
            {
                Require(!DriverlessTemperatureReader.SelectCpuProbe(new[] { CpuRow(label, 55) }, false).Value.HasValue, label + " is not CPU die/probe telemetry");
            }
            Equal(DriverlessTemperatureReader.SelectCpuProbe(new[] { CpuRow("CPU0 temperature", 52), CpuRow("CPU1 temperature", 61), CpuRow("GPU", 90) }, false).Value, 61,
                "Hottest explicitly attributed CPU probe selected");
            var generic = CpuRow("Thermal_01", 48);
            generic["Description"] = "Processor temperature";
            Equal(DriverlessTemperatureReader.SelectCpuProbe(new[] { generic }, false).Value, 48, "Explicit OEM description attributes anonymous sensor");
        }

        private static void Availability()
        {
            var row = CpuRow("CPU temperature", 55);
            row["OperationalStatus"] = new ushort[] { 2, 13 };
            Require(!DriverlessTemperatureReader.SelectCpuProbe(new[] { row }, false).Value.HasValue, "Lost communication never produces a current value");
            row["OperationalStatus"] = 2;
            row["EnabledState"] = 3;
            Require(!DriverlessTemperatureReader.SelectCpuProbe(new[] { row }, false).Value.HasValue, "Disabled sensor rejected");
            row["EnabledState"] = 2;
            row["RateUnits"] = 3;
            Require(!DriverlessTemperatureReader.SelectCpuProbe(new[] { row }, false).Value.HasValue, "Temperature rate is not an absolute temperature");
            row["RateUnits"] = 0;
            row["Availability"] = 8;
            Require(!DriverlessTemperatureReader.SelectCpuProbe(new[] { row }, false).Value.HasValue, "Offline probe rejected");
            row["Availability"] = 3;
            Equal(DriverlessTemperatureReader.SelectCpuProbe(new[] { row }, false).Value, 55, "Available operational probe accepted");
        }

        private static void ProviderFailuresAndFreshness()
        {
            DateTime now = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);
            int hpCalls = 0;
            var reader = new DriverlessTemperatureReader(delegate(string scope, string query)
            {
                if (scope == "root\\HP\\InstrumentedBIOS")
                {
                    hpCalls++;
                    if (hpCalls == 1) { return new List<Dictionary<string, object>> { CpuRow("CPU temperature", 62) }; }
                    throw new UnauthorizedAccessException();
                }
                return new List<Dictionary<string, object>>();
            });
            var first = new Snapshot { ComputerManufacturer = "HP" };
            reader.ApplyCpu(first, now);
            Equal(first.CpuTemperature, 62, "Fresh OEM CPU read");
            Require(first.CpuTemperatureTrustedForStress && !first.CpuTemperatureExperimental, "Established OEM reading retains thermal-stop eligibility");
            Require(first.CpuTemperatureStatus.Contains("HP CMI") && first.CpuTemperatureStatus.Contains("может отличаться"), "Source and limit remain visible");
            var second = new Snapshot { ComputerManufacturer = "HP" };
            reader.ApplyCpu(second, now.AddSeconds(2));
            Require(!second.CpuTemperature.HasValue, "Access failure does not replay stale OEM value");
            Require(!second.CpuTemperatureTrustedForStress && !second.CpuTemperatureExperimental, "Fresh unavailable snapshots do not inherit trust metadata");
            reader.ApplyCpu(new Snapshot { ComputerManufacturer = "HP" }, now.AddSeconds(3));
            Require(hpCalls == 2, "Unavailable provider is backed off without storing readings");
            reader.ApplyCpu(new Snapshot { ComputerManufacturer = "HP" }, now.AddSeconds(63));
            Require(hpCalls == 3, "Unavailable provider is retried after bounded delay");
            int calls = 0;
            var missing = new DriverlessTemperatureReader(delegate(string scope, string query) { calls++; throw new ManagementException(); });
            var absent = new Snapshot { ComputerManufacturer = "Apple Inc." };
            missing.ApplyCpu(absent, now);
            Require(calls == 1 && !absent.CpuTemperature.HasValue, "Unknown manufacturer queries only Windows; missing WMI class stays n/a");
            var dell = new DriverlessTemperatureReader(delegate(string scope, string query)
            {
                Require(scope == "root\\dcim\\sysman" && query.Contains("DCIM_NumericSensor"), "Only documented Dell namespace used");
                return new List<Dictionary<string, object>> { CpuRow("CPU temperature", 59) };
            });
            var dellSnapshot = new Snapshot { ComputerManufacturer = "Dell Inc." };
            dell.ApplyCpu(dellSnapshot, now);
            Equal(dellSnapshot.CpuTemperature, 59, "Existing Dell provider supported");
            Require(dellSnapshot.CpuTemperatureTrustedForStress && !dellSnapshot.CpuTemperatureExperimental, "Dell success has explicit non-experimental trust metadata");
        }

        private static void WddmLayoutAndIdentity()
        {
            Require(Marshal.SizeOf(typeof(WindowsGpuTemperatureReader.AdapterPerformanceData)) == 64, "WDDM ABI data size");
            Require(Marshal.OffsetOf(typeof(WindowsGpuTemperatureReader.AdapterPerformanceData), "Temperature").ToInt32() == 56, "WDDM temperature at correct aligned offset");
            Require(Marshal.SizeOf(typeof(WindowsGpuTemperatureReader.OpenAdapter)) == 12 && Marshal.SizeOf(typeof(WindowsGpuTemperatureReader.CloseAdapter)) == 4, "WDDM uint handles are not pointer-sized");
            Require(Marshal.SizeOf(typeof(WindowsGpuTemperatureReader.QueryAdapter)) == (IntPtr.Size == 8 ? 24 : 16), "Query ABI honors pointer size");
            var api = new FakeApi { Temperature = 654 };
            var gpu = new GpuSnapshot { AdapterIndex = 7, AdapterLuidLow = 0xf1234567, AdapterLuidHigh = -18 };
            Equal(WindowsGpuTemperatureReader.Read(gpu, api), 65.4, "WDDM deci-Celsius conversion");
            Require(api.LuidLow == gpu.AdapterLuidLow && api.LuidHigh == gpu.AdapterLuidHigh && api.Closes == 1, "Exact DXGI LUID, not enumeration index/name, selects the GPU and closes handle");
            Require(!WindowsGpuTemperatureReader.Read(new GpuSnapshot { Name = gpu.Name }, api).HasValue && api.Opens == 1, "A name-only inventory cannot assign another adapter temperature");
            Require(!WindowsGpuTemperatureReader.Read(new GpuSnapshot { AdapterIndex = 0 }, api).HasValue && api.Opens == 1, "Uninitialized LUID rejected");
        }

        private static void WddmFailuresAndFreshness()
        {
            var gpu = new GpuSnapshot { AdapterIndex = 0, AdapterLuidLow = 9 };
            var api = new FakeApi { Temperature = 610 };
            Equal(WindowsGpuTemperatureReader.Read(gpu, api), 61, "First GPU sample");
            api.Temperature = 0;
            Require(!WindowsGpuTemperatureReader.Read(gpu, api).HasValue, "Unsupported next sample clears prior GPU temperature");
            api.Temperature = uint.MaxValue;
            Require(!WindowsGpuTemperatureReader.Read(gpu, api).HasValue, "GPU sentinel rejected");
            api.Temperature = 700;
            api.Count = 2;
            Require(!WindowsGpuTemperatureReader.Read(gpu, api).HasValue, "Linked physical GPUs never collapsed to first GPU");
            api.Count = 1;
            api.QueryFailure = true;
            Require(!WindowsGpuTemperatureReader.Read(gpu, api).HasValue && api.Closes == api.Opens, "Unsupported WDDM query closes all opened handles");
            api.QueryFailure = false;
            api.ThrowQuery = true;
            Require(!WindowsGpuTemperatureReader.Read(gpu, api).HasValue && api.Closes == api.Opens, "Provider exception does not leak handles");
            api.ThrowQuery = false;
            api.OpenFailure = true;
            int closed = api.Closes;
            Require(!WindowsGpuTemperatureReader.Read(gpu, api).HasValue && api.Closes == closed, "Failed open is not used or closed as a valid handle");
            Require(!WindowsGpuTemperatureReader.DecodeTemperature(1501).HasValue, "Implausible WDDM unit/value rejected");
        }

        private static void ExistingSources()
        {
            var existing = new Snapshot { ComputerManufacturer = "HP", CpuTemperature = 68, CpuTemperatureStatus = "CPU Package" };
            var reader = new DriverlessTemperatureReader(delegate(string scope, string query) { throw new InvalidOperationException("Must not override CPU Package"); });
            reader.ApplyCpu(existing, DateTime.UtcNow);
            Require(existing.CpuTemperature == 68 && existing.CpuTemperatureStatus == "CPU Package", "Precise direct CPU package reading takes precedence over OEM");
        }

        private static Dictionary<string, object> CpuRow(string name, object temperature)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { "Name", name }, { "CurrentReading", temperature }, { "SensorType", 2 }, { "BaseUnits", 2 }, { "UnitModifier", 0 } };
        }
        private static void Equal(double? actual, double expected, string message) { Require(actual.HasValue && Math.Abs(actual.Value - expected) < .0001, message); }
        private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }

        private sealed class FakeApi : WindowsGpuTemperatureReader.IApi
        {
            public uint Temperature;
            public int Count = 1;
            public int Opens, Closes;
            public uint LuidLow;
            public int LuidHigh;
            public bool OpenFailure, QueryFailure, ThrowQuery;
            public int Open(ref WindowsGpuTemperatureReader.OpenAdapter request)
            {
                Opens++; LuidLow = request.LuidLow; LuidHigh = request.LuidHigh;
                if (OpenFailure) { return unchecked((int)0xc0000001); }
                request.Handle = 0xa1234567; return 0;
            }
            public int Query(ref WindowsGpuTemperatureReader.QueryAdapter request)
            {
                Require(request.Handle == 0xa1234567, "Query uses native handle");
                if (ThrowQuery) { throw new EntryPointNotFoundException(); }
                if (QueryFailure) { return unchecked((int)0xc0000001); }
                if (request.Type == 30) { Require(request.Size == 4, "Count request size"); Marshal.WriteInt32(request.Data, Count); }
                else
                {
                    Require(request.Type == 62 && request.Size == 64, "Performance request type and size");
                    Require(Marshal.ReadInt32(request.Data) == 0 && Marshal.ReadInt32(request.Data, 56) == 0, "Physical index and output reset each query");
                    Marshal.StructureToPtr(new WindowsGpuTemperatureReader.AdapterPerformanceData { Temperature = Temperature }, request.Data, false);
                }
                return 0;
            }
            public int Close(ref WindowsGpuTemperatureReader.CloseAdapter request) { Require(request.Handle == 0xa1234567, "Close uses native handle"); Closes++; return 0; }
        }
    }
}
