using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class BenchmarkHistoryTests
    {
        public static int Main(string[] args)
        {
            try
            {
                FirstAttempt();
                SuccessfulRestore();
                FailurePreservesResults();
                HardwareChanges();
                InvalidHistory();
                ConcurrentResults();
                SecurityPolicies();
                SharedMutexAccess();
                SharedStorageRejectsUnsafePaths();
                Console.WriteLine("PASS: persistent benchmark history (9 groups)");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void FirstAttempt()
        {
            string path = NewPath();
            Snapshot snapshot = Hardware();
            var store = new BenchmarkHistory(path);
            Require(!store.Load(snapshot).HasPreviousAttempt, "Only a missing history permits first-launch automatic tests.");
            Task<bool>[] claims = Enumerable.Range(0, 6).Select(index => Task.Run(delegate { return new BenchmarkHistory(path).TryBeginAutomaticAttempt(snapshot); })).ToArray();
            Task.WaitAll(claims);
            Require(claims.Count(task => task.Result) == 1, "Concurrent launches must claim automatic testing exactly once.");
            BenchmarkHistoryResult saved = new BenchmarkHistory(path).Load(snapshot);
            Require(saved.HasPreviousAttempt && !saved.LastTestUtc.HasValue && saved.Disks.Count == 0 && saved.Gpus.Count == 0,
                "An interrupted or ineligible first run is remembered without inventing measurements.");
            Require(saved.Status.Contains("завершённых тестов нет"), "Attempt-only history must not claim completed test results exist.");
            Require(!store.TryBeginAutomaticAttempt(snapshot), "Another launch cannot repeat automatic stress tests.");
        }

        public static void SuccessfulRestore()
        {
            var store = new BenchmarkHistory(NewPath());
            Snapshot snapshot = Hardware();
            BenchmarkProgress diskResult = Disk(0);
            GpuBenchmarkProgress gpuResult = Gpu(0);
            DateTime measured = DateTime.UtcNow.AddMinutes(-5);
            diskResult.TestedAtUtc = gpuResult.TestedAtUtc = measured;
            Require(store.RecordDisk(snapshot, snapshot.Disks[0], diskResult), "Disk result is written.");
            Require(store.RecordGpu(snapshot, snapshot.Gpus[0], gpuResult), "GPU result is written.");
            snapshot.Disks[0].Index = 7;
            snapshot.Gpus[0].AdapterIndex = 3;
            snapshot.Gpus[0].AdapterLuidLow = 9876;
            snapshot.Gpus[0].MemoryBytes = 268435456;
            snapshot.CpuTemperature = 70;
            snapshot.RamUsedBytes = 8UL * 1073741824;
            BenchmarkHistoryResult result = store.Load(snapshot);
            Require(result.HasPreviousAttempt && !result.ConfigurationChanged && result.LastTestUtc.HasValue
                && result.Disks.Single().DiskIndex == 7 && result.Gpus.Single().AdapterIndex == 3,
                "Stable device identities restore across disk/adapter reorder and reboot LUID changes; live usage is irrelevant.");
            Require(result.Disks[0].FromHistory && result.Gpus[0].FromHistory && result.Disks[0].TestedAtUtc.HasValue
                && result.Gpus[0].TestedAtUtc.HasValue && result.Disks[0].Stage.StartsWith("Сохранённый тест") && result.Status.Contains("серийный номер GPU недоступен"),
                "Restored results carry their date and disclose the weaker GPU identity match.");
            Require(result.Disks[0].TestedAtUtc == measured && result.Gpus[0].TestedAtUtc == measured,
                "Measurement completion timestamps must not change to the later persistence time.");
            result.Disks[0].ReadBytesPerSecond = 1;
            Require(store.Load(snapshot).Disks[0].ReadBytesPerSecond == 3000000000,
                "The UI receives copies rather than mutating saved state.");
        }

        public static void FailurePreservesResults()
        {
            var store = new BenchmarkHistory(NewPath());
            Snapshot snapshot = Hardware();
            store.RecordDisk(snapshot, snapshot.Disks[0], Disk(0));
            store.RecordGpu(snapshot, snapshot.Gpus[0], Gpu(0));
            DateTime? before = store.Load(snapshot).LastTestUtc;
            BenchmarkProgress partial = Disk(0);
            partial.WriteBytesPerSecond = null;
            partial.Percent = 50;
            partial.Error = "Cancelled";
            GpuBenchmarkProgress failed = Gpu(0);
            failed.Completed = false;
            failed.Error = "Driver unavailable";
            Require(store.MarkAttempt(snapshot) && !store.RecordDisk(snapshot, snapshot.Disks[0], partial) && !store.RecordGpu(snapshot, snapshot.Gpus[0], failed),
                "Cancelled/error attempts are remembered while the result return value rejects unsuccessful measurements.");
            BenchmarkHistoryResult result = store.Load(snapshot);
            Require(result.LastTestUtc == before && result.Disks.Single().WriteBytesPerSecond == 2000000000 && result.Gpus.Single().FramesPerSecond == 100,
                "A failed manual retest preserves prior completed measurements and their date.");
            BenchmarkProgress invalid = Disk(0);
            invalid.ReadBytesPerSecond = double.NaN;
            Require(!store.RecordDisk(snapshot, snapshot.Disks[0], invalid), "Non-finite readings cannot be recorded as successful speed.");
            invalid = Disk(0);
            invalid.TestedAtUtc = DateTime.UtcNow.AddHours(1);
            Require(!store.RecordDisk(snapshot, snapshot.Disks[0], invalid), "Future measurement timestamps are rejected.");
        }

        public static void HardwareChanges()
        {
            var store = new BenchmarkHistory(NewPath());
            Snapshot snapshot = Hardware();
            store.RecordDisk(snapshot, snapshot.Disks[0], Disk(0));
            store.RecordGpu(snapshot, snapshot.Gpus[0], Gpu(0));
            snapshot.Disks[0].PersistentHardwareId = "another serial of exactly the same model";
            BenchmarkHistoryResult changed = store.Load(snapshot);
            Require(changed.HasPreviousAttempt && changed.ConfigurationChanged && changed.Disks.Count == 0 && changed.Gpus.Count == 1,
                "Replacing an identical disk drops its old speed without enabling automatic tests.");
            snapshot.RamInstalledBytes *= 2;
            changed = store.Load(snapshot);
            Require(changed.ConfigurationChanged && changed.Disks.Count == 0 && changed.Gpus.Count == 0,
                "Changed CPU/RAM configuration cannot silently inherit old benchmark figures.");
            snapshot = Hardware();
            snapshot.Gpus.Add(new GpuSnapshot { Name = snapshot.Gpus[0].Name, PersistentHardwareId = snapshot.Gpus[0].PersistentHardwareId, MemoryBytes = snapshot.Gpus[0].MemoryBytes, IsIntegrated = true, AdapterIndex = 1 });
            Require(store.Load(snapshot).Gpus.Count == 0, "Two identical GPUs cannot receive an ambiguous cached result.");
            var weak = new BenchmarkHistory(NewPath());
            snapshot.Disks[0].PersistentHardwareId = "";
            weak.RecordDisk(snapshot, snapshot.Disks[0], Disk(0));
            Require(weak.Load(snapshot).HasPreviousAttempt && weak.Load(snapshot).Disks.Count == 0,
                "A disk with a PnP identity but no serial records the attempt without assigning speed by model/capacity alone.");
        }

        public static void InvalidHistory()
        {
            Snapshot snapshot = Hardware();
            foreach (string invalid in new[] { "{", new string('x', 262145), "{\"SchemaVersion\":999}" })
            {
                string path = NewPath();
                File.WriteAllText(path, invalid);
                var store = new BenchmarkHistory(path);
                BenchmarkHistoryResult result = store.Load(snapshot);
                Require(result.HasPreviousAttempt && result.Disks.Count == 0 && !store.TryBeginAutomaticAttempt(snapshot),
                    "Corrupt, oversized and unknown-schema history must suppress automatic retesting.");
            }
            foreach (bool future in new[] { true, false })
            {
                string path = NewPath();
                var store = new BenchmarkHistory(path);
                store.RecordDisk(snapshot, snapshot.Disks[0], Disk(0));
                string json = File.ReadAllText(path);
                var serializer = new JavaScriptSerializer();
                var document = serializer.Deserialize<Dictionary<string, object>>(json);
                if (future) { document["LastAttemptUtc"] = DateTime.UtcNow.AddDays(1).ToString("o"); }
                else
                {
                    var records = (System.Collections.ArrayList)document["Disks"];
                    var record = (Dictionary<string, object>)records[0];
                    ((Dictionary<string, object>)record["Progress"])["ReadBytesPerSecond"] = 0;
                }
                File.WriteAllText(path, serializer.Serialize(document));
                BenchmarkHistoryResult result = store.Load(snapshot);
                Require(result.HasPreviousAttempt && result.Disks.Count == 0 && !result.LastTestUtc.HasValue,
                    "Future timestamps and invalid speeds must not become valid saved measurements.");
            }
            string directoryPath = NewPath();
            Directory.CreateDirectory(directoryPath);
            Require(new BenchmarkHistory(directoryPath).Load(snapshot).HasPreviousAttempt,
                "An unreadable/non-file history target is not treated as a first run.");
            string noSchemaPath = NewPath();
            var noSchemaStore = new BenchmarkHistory(noSchemaPath);
            noSchemaStore.MarkAttempt(snapshot);
            File.WriteAllText(noSchemaPath, File.ReadAllText(noSchemaPath).Replace("\"SchemaVersion\":1,", ""));
            Require(noSchemaStore.Load(snapshot).HasPreviousAttempt && !noSchemaStore.TryBeginAutomaticAttempt(snapshot),
                "A missing explicit schema is corrupt history, not an invitation to repeat automatic tests.");
        }

        public static void ConcurrentResults()
        {
            string path = NewPath();
            Snapshot snapshot = Hardware();
            snapshot.Disks.Add(new DiskSnapshot { Index = 1, Name = "Second SSD", PersistentHardwareId = "stable-second-serial", StorageIdentity = "stable-second-id", SizeBytes = 1024000000000 });
            Task<bool>[] writes = new[]
            {
                Task.Run(delegate { return new BenchmarkHistory(path).RecordDisk(snapshot, snapshot.Disks[0], Disk(0)); }),
                Task.Run(delegate { return new BenchmarkHistory(path).RecordDisk(snapshot, snapshot.Disks[1], Disk(1)); }),
                Task.Run(delegate { return new BenchmarkHistory(path).RecordGpu(snapshot, snapshot.Gpus[0], Gpu(0)); })
            };
            Task.WaitAll(writes);
            BenchmarkHistoryResult result = new BenchmarkHistory(path).Load(snapshot);
            Require(writes.All(task => task.Result) && result.Disks.Count == 2 && result.Gpus.Count == 1,
                "Serialized atomic writes retain independent results from concurrent instances.");
            Require(Directory.GetFiles(Path.GetDirectoryName(path), ".history-*.tmp").Length == 0,
                "Successful atomic replacement leaves no staging files.");
        }

        public static void SecurityPolicies()
        {
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            foreach (bool directory in new[] { false, true })
            {
                FileSystemSecurity security = BenchmarkHistory.StorageSecurity(directory);
                Require(BenchmarkHistory.IsSecureStorageAcl(security, directory), "New storage policy permits writes only to Administrators and SYSTEM.");
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (users.Equals(rule.IdentityReference))
                    {
                        Require((rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read && (rule.FileSystemRights & FileSystemRights.Write) == 0,
                            "Regular users can read shared history but cannot modify it.");
                    }
                }
                security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
                Require(!BenchmarkHistory.IsSecureStorageAcl(security, directory), "Existing user-writable storage must fail closed.");
                security = BenchmarkHistory.StorageSecurity(directory);
                security.SetOwner(users);
                Require(!BenchmarkHistory.IsSecureStorageAcl(security, directory), "A user-owned object can change its ACL and must not be trusted for elevated writes.");
            }
            DirectorySecurity inherited = (DirectorySecurity)BenchmarkHistory.StorageSecurity(true);
            inherited.SetAccessRuleProtection(false, true);
            Require(!BenchmarkHistory.IsSecureStorageAcl(inherited, true), "Storage directories cannot inherit future write permissions from their parent.");
            var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            MutexAccessRule sharedRule = BenchmarkHistory.SharedMutexSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<MutexAccessRule>().Single(rule => authenticated.Equals(rule.IdentityReference));
            Require(sharedRule.MutexRights == (MutexRights.Synchronize | MutexRights.Modify), "Shared mutex grants regular users only waiting and release rights.");
        }

        public static void SharedMutexAccess()
        {
            string name = @"Global\ADAMO-GT-History-Test-" + Guid.NewGuid().ToString("N");
            bool created;
            using (var owner = new Mutex(false, name, out created, BenchmarkHistory.SharedMutexSecurity()))
            {
                Require(created, "The mutex test owns a unique fixture.");
                string before = owner.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                using (Mutex opened = BenchmarkHistory.OpenSharedMutex(name))
                {
                    Require(opened.WaitOne(0), "Existing global mutex can be used through a minimal-rights handle.");
                    opened.ReleaseMutex();
                }
                Require(owner.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) == before,
                    "Opening an existing shared mutex cannot alter its permissions.");
            }
            string unknown = @"Global\ADAMO-GT-History-Test-Unknown-" + Guid.NewGuid().ToString("N");
            using (var owner = new Mutex(false, unknown))
            {
                string before = owner.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                using (Mutex opened = BenchmarkHistory.OpenSharedMutex(unknown)) { Require(opened.WaitOne(0), "An unknown existing mutex can be opened with minimal rights."); opened.ReleaseMutex(); }
                Require(owner.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) == before,
                    "Existing unknown mutex ACLs must not be rewritten.");
            }
        }

        public static void SharedStorageRejectsUnsafePaths()
        {
            string unsafePath = NewPath();
            string directory = Path.GetDirectoryName(unsafePath);
            string before = Directory.GetAccessControl(directory).GetSecurityDescriptorSddlForm(AccessControlSections.All);
            var shared = new BenchmarkHistory(unsafePath, true);
            Require(shared.Load(Hardware()).HasPreviousAttempt && !shared.TryBeginAutomaticAttempt(Hardware()) && !shared.MarkAttempt(Hardware()),
                "Unsafe pre-existing shared directories must suppress automatic work and reject writes.");
            Require(!File.Exists(unsafePath) && Directory.GetAccessControl(directory).GetSecurityDescriptorSddlForm(AccessControlSections.All) == before,
                "Rejected directories and their ACLs must remain untouched.");
            string target = Path.Combine(directory, "target");
            string link = Path.Combine(directory, "redirect");
            Directory.CreateDirectory(target);
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) { throw new IOException("Cannot create history junction fixture: " + output + error); }
            }
            bool rejected = false;
            try { BenchmarkHistory.RejectReparseAncestors(Path.Combine(link, "History", "test.json")); }
            catch (IOException) { rejected = true; }
            Require(rejected, "A missing history file below an existing junction ancestor must be rejected before elevated creation.");
            var redirected = new BenchmarkHistory(Path.Combine(link, "History", "test.json"), true);
            Require(redirected.Load(Hardware()).HasPreviousAttempt && !redirected.MarkAttempt(Hardware()) && Directory.GetFileSystemEntries(target).Length == 0,
                "Reads fail closed and elevated-style writes cannot traverse the redirected ancestor.");
        }

        private static Snapshot Hardware()
        {
            var snapshot = new Snapshot { CpuName = "Example CPU", CpuCores = 8, CpuThreads = 16, RamInstalledBytes = 16UL * 1073741824, RamType = "DDR5", RamRate = "4800" };
            snapshot.Disks.Add(new DiskSnapshot { Index = 0, Name = "Example SSD", PersistentHardwareId = "stable-ssd-serial", StorageIdentity = "stable-ssd-pnp-id", SizeBytes = 512000000000 });
            snapshot.Gpus.Add(new GpuSnapshot { AdapterIndex = 0, Name = "Example GPU", PersistentHardwareId = "8086:1234:5678:01", MemoryBytes = 134217728, IsIntegrated = true });
            return snapshot;
        }
        private static BenchmarkProgress Disk(uint index)
        {
            return new BenchmarkProgress { DiskIndex = index, Percent = 100, ReadBytesPerSecond = 3000000000, WriteBytesPerSecond = 2000000000,
                Engine = "Microsoft DiskSpd", EngineVersion = "2.3.0", Profile = "diskspd-seq-v1", Method = "SEQ 1 MB Q8T1",
                TestSizeBytes = 1073741824, BlockSizeBytes = 1048576, QueueDepth = 8, ThreadCount = 1, Stage = "Done" };
        }
        private static GpuBenchmarkProgress Gpu(uint index)
        {
            return new GpuBenchmarkProgress { AdapterIndex = index, AdapterName = "Example GPU", Completed = true, Percent = 100,
                FramesPerSecond = 100, FrameTimeMs = 10, DurationSeconds = 15, Stage = "Done" };
        }
        private static string NewPath()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "history.json");
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
