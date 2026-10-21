using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class PeripheralHistoryTests
    {
        public static void Run()
        {
            // Keep atomic replace/move fixtures in the test workspace. Some Windows
            // sandbox temp redirects permit writes but deny File.Move there.
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "peripheral-history-fixtures", Guid.NewGuid().ToString("N"), "history.json");
            var history = new BenchmarkHistory(path);
            string error;
            Require(history.LoadPeripherals(out error).Count == 0 && error == "", "New history is empty");
            var snapshot = new Snapshot();
            var results = new[] { "camera", "microphone", "keyboard", "speakers" }.Select(Result).ToArray();
            Task.WaitAll(results.Select(item => Task.Run(() => Require(new BenchmarkHistory(path).RecordPeripheral(item), "Concurrent result saved"))).ToArray());
            var loaded = new BenchmarkHistory(path).LoadPeripherals(out error);
            Require(loaded.Count == 4 && loaded.All(item => item.ConfirmedBy == "user") && error == "", "Four independent results and user confirmation persist");
            Require(!File.Exists(path) && !history.Load(snapshot).HasPreviousAttempt, "Device checks cannot suppress a first automatic benchmark");
            Require(history.TryBeginAutomaticAttempt(snapshot), "Automatic benchmark still claims first attempt");
            byte[] benchmarkBytes = File.ReadAllBytes(path);
            var changed = Result("camera"); changed.Outcome = "problem"; changed.DeviceId = "replacement-camera";
            Require(history.RecordPeripheral(changed), "Replacement-device verdict saved");
            loaded = history.LoadPeripherals(out error);
            Require(loaded.Count == 4 && loaded.Single(item => item.Kind == "camera").DeviceId == "replacement-camera", "Latest verdict replaces only same test kind");
            Require(benchmarkBytes.SequenceEqual(File.ReadAllBytes(path)), "Manual saves leave benchmarks byte-for-byte unchanged");
            var bad = Result("camera"); bad.TestedAtUtc = DateTime.UtcNow.AddDays(1).ToString("o");
            Require(!history.RecordPeripheral(bad), "Future verdict rejected");
            bad = Result("camera"); bad.ConfirmedBy = "automatic";
            Require(!history.RecordPeripheral(bad), "No automated healthy verdict accepted");
            bad = Result("camera"); bad.DeviceName = "unsafe\nname";
            Require(!history.RecordPeripheral(bad), "Control characters rejected");
            bad = Result("unknown"); Require(!history.RecordPeripheral(bad), "Unknown test kind rejected");
            string json = DiagnosticReport.Serialize(snapshot, new BenchmarkProgress[0], new GpuBenchmarkProgress[0], null, false, loaded);
            var report = new JavaScriptSerializer().Deserialize<DiagnosticReport>(json);
            Require(report.PeripheralTests.Count == 4 && report.PeripheralTestsNotice.Contains("пользователем"), "Report retains explicit manual verdict provenance");
            File.WriteAllText(path + ".peripherals", "{broken");
            Require(history.LoadPeripherals(out error).Count == 0 && error.Length > 0, "Corrupt manual history cannot certify a device");
            Require(history.Load(snapshot).HasPreviousAttempt && benchmarkBytes.SequenceEqual(File.ReadAllBytes(path)), "Corrupt manual history does not change startup benchmark decisions");
            Require(history.RecordPeripheral(Result("keyboard")), "A new manual verdict repairs corrupt manual history");
            Require(history.LoadPeripherals(out error).Count == 1 && error == "", "Only fresh verified result survives corruption recovery");
            ByteBudget(path + ".unicode", '\u4e00', true);
            ByteBudget(path + ".escaped", '"', false);
        }

        private static void ByteBudget(string path, char character, bool exceedsLimit)
        {
            var history = new BenchmarkHistory(path);
            byte[] previous = null;
            string[] kinds = { "camera", "microphone", "keyboard", "speakers" };
            for (int i = 0; i < kinds.Length; i++)
            {
                var result = Result(kinds[i]);
                result.DeviceId = new string(character, 4096);
                result.DeviceName = new string(character, 512);
                result.Detail = new string(character, 1024);
                Require(PeripheralTestResult.Valid(result), "Long fixture stays inside each field's supported character limit");
                bool saved = history.RecordPeripheral(result);
                if (exceedsLimit && i == 3)
                {
                    Require(!saved && previous.SequenceEqual(File.ReadAllBytes(path + ".peripherals")),
                        "Oversized UTF-8 document is rejected before replacing three valid verdicts");
                }
                else
                {
                    Require(saved, "A document inside the encoded byte limit is saved");
                    previous = File.ReadAllBytes(path + ".peripherals");
                    Require(previous.Length <= 65536, "Writer never creates a document that exceeds the reader's byte budget");
                }
            }
            string error;
            var loaded = history.LoadPeripherals(out error);
            Require(error == "" && loaded.Count == (exceedsLimit ? 3 : 4)
                && loaded.All(item => item.DeviceId == new string(character, 4096)),
                "UTF-8 and escaped JSON round-trip within the shared byte budget without discarding prior results");
        }

        private static PeripheralTestResult Result(string kind)
        {
            return new PeripheralTestResult { Kind = kind, Outcome = "passed", DeviceId = "test-device-" + kind,
                DeviceName = "Synthetic test device", Detail = "User-confirmed synthetic fixture", TestedAtUtc = DateTime.UtcNow.ToString("o") };
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    }
}
