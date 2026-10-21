using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace PcPulse
{
    internal sealed class BenchmarkHistoryResult
    {
        internal bool HasPreviousAttempt;
        internal bool ConfigurationChanged;
        internal DateTime? LastTestUtc;
        internal string Status = "";
        internal List<BenchmarkProgress> Disks = new List<BenchmarkProgress>();
        internal List<GpuBenchmarkProgress> Gpus = new List<GpuBenchmarkProgress>();
    }

    internal sealed partial class BenchmarkHistory
    {
        private const int MaximumBytes = 262144;
        private readonly string path;
        private readonly string mutexName;
        private readonly bool machineWide;

        internal BenchmarkHistory(string historyFilePath)
            : this(historyFilePath, false)
        {
        }

        internal BenchmarkHistory(string historyFilePath, bool machineWide)
        {
            path = Path.GetFullPath(historyFilePath);
            this.machineWide = machineWide;
            mutexName = (machineWide ? @"Global\" : @"Local\") + "ADAMO-GT-History-" + Hash(path.ToUpperInvariant());
        }

        internal static BenchmarkHistory OpenDefault()
        {
            string machine = null;
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (key != null) { machine = key.GetValue("MachineGuid") as string; }
                }
            }
            catch (Exception) { }
            if (string.IsNullOrWhiteSpace(machine)) { machine = "computer-name:" + Environment.MachineName; }
            return new BenchmarkHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ADAMO GT", "History", Hash(machine) + ".json"), true);
        }

        internal BenchmarkHistoryResult Load(Snapshot snapshot)
        {
            var result = new BenchmarkHistoryResult();
            try
            {
                WithLock(delegate
                {
                    Document document = Read();
                    if (document == null) { return; }
                    result.HasPreviousAttempt = true;
                    string configuration = Configuration(snapshot);
                    result.ConfigurationChanged = document.Configuration != configuration;
                    foreach (DiskRecord record in document.Disks)
                    {
                        DateTime tested = Timestamp(record.TestedAtUtc);
                        SetLastTest(result, tested);
                        List<DiskSnapshot> matches = snapshot.Disks.Where(disk => DiskKey(disk) == record.Key).ToList();
                        if (matches.Count != 1) { continue; }
                        if (record.Configuration != configuration) { result.ConfigurationChanged = true; continue; }
                        BenchmarkProgress progress = Copy(record.Progress);
                        progress.DiskIndex = matches[0].Index;
                        progress.FromHistory = true;
                        progress.TestedAtUtc = tested;
                        progress.Stage = SavedStage(tested);
                        result.Disks.Add(progress);
                    }
                    foreach (GpuRecord record in document.Gpus)
                    {
                        DateTime tested = Timestamp(record.TestedAtUtc);
                        SetLastTest(result, tested);
                        List<GpuSnapshot> matches = snapshot.Gpus.Where(gpu => GpuKey(gpu) == record.Key).ToList();
                        if (matches.Count != 1 || !matches[0].AdapterIndex.HasValue) { continue; }
                        if (record.Configuration != configuration) { result.ConfigurationChanged = true; continue; }
                        GpuBenchmarkProgress progress = Copy(record.Progress);
                        progress.AdapterIndex = matches[0].AdapterIndex;
                        progress.AdapterName = matches[0].Name;
                        progress.FromHistory = true;
                        progress.TestedAtUtc = tested;
                        progress.Stage = SavedStage(tested);
                        result.Gpus.Add(progress);
                    }
                    if (document.Devices != Devices(snapshot)) { result.ConfigurationChanged = true; }
                    result.Status = result.ConfigurationChanged
                        ? "Комплектующие или конфигурация изменились. Повторный тест запускается кнопкой."
                        : result.Disks.Count + result.Gpus.Count > 0
                            ? "Сохранённые результаты. Повторный тест запускается кнопкой."
                            : "Сохранённых завершённых тестов нет. Тест запускается кнопкой.";
                    if (result.Gpus.Count > 0) { result.Status += " GPU сопоставлен по аппаратной модели; серийный номер GPU недоступен."; }
                });
            }
            catch (Exception)
            {
                // Unreadable/corrupt history must never silently trigger another stress test.
                result.HasPreviousAttempt = true;
                result.Disks.Clear();
                result.Gpus.Clear();
                result.LastTestUtc = null;
                result.Status = "История тестов недоступна. При необходимости запустите тест вручную.";
            }
            return result;
        }

        internal bool TryBeginAutomaticAttempt(Snapshot snapshot)
        {
            bool claimed = false;
            try
            {
                WithLock(delegate
                {
                    if (Read() != null) { return; }
                    Write(NewDocument(snapshot));
                    claimed = true;
                });
            }
            catch (Exception) { return false; }
            return claimed;
        }

        internal bool MarkAttempt(Snapshot snapshot)
        {
            return Update(snapshot, delegate(Document document) { });
        }

        internal bool RecordDisk(Snapshot snapshot, DiskSnapshot disk, BenchmarkProgress progress)
        {
            bool recorded = false;
            bool persisted = Update(snapshot, delegate(Document document)
            {
                string key = DiskKey(disk);
                if (key == null || snapshot.Disks.Count(item => DiskKey(item) == key) != 1 || !ValidDisk(progress) || disk.Index != progress.DiskIndex || progress.FromHistory) { return; }
                string testedAt;
                if (!ResultTimestamp(progress.TestedAtUtc, out testedAt)) { return; }
                document.Configuration = Configuration(snapshot);
                document.Devices = Devices(snapshot);
                document.Disks.RemoveAll(record => record.Key == key);
                document.Disks.Add(new DiskRecord { Key = key, Configuration = Configuration(snapshot), TestedAtUtc = testedAt, Progress = Copy(progress) });
                while (document.Disks.Count > 32) { document.Disks.RemoveAt(0); }
                recorded = true;
            });
            return persisted && recorded;
        }

        internal bool RecordGpu(Snapshot snapshot, GpuSnapshot gpu, GpuBenchmarkProgress progress)
        {
            bool recorded = false;
            bool persisted = Update(snapshot, delegate(Document document)
            {
                string key = GpuKey(gpu);
                if (key == null || snapshot.Gpus.Count(item => GpuKey(item) == key) != 1 || !ValidGpu(progress)
                    || !gpu.AdapterIndex.HasValue || gpu.AdapterIndex != progress.AdapterIndex || progress.FromHistory) { return; }
                string testedAt;
                if (!ResultTimestamp(progress.TestedAtUtc, out testedAt)) { return; }
                document.Configuration = Configuration(snapshot);
                document.Devices = Devices(snapshot);
                document.Gpus.RemoveAll(record => record.Key == key);
                document.Gpus.Add(new GpuRecord { Key = key, Configuration = Configuration(snapshot), TestedAtUtc = testedAt, Progress = Copy(progress) });
                while (document.Gpus.Count > 32) { document.Gpus.RemoveAt(0); }
                recorded = true;
            });
            return persisted && recorded;
        }

        private bool Update(Snapshot snapshot, Action<Document> mutation)
        {
            try
            {
                WithLock(delegate
                {
                    Document document;
                    try { document = Read() ?? NewDocument(snapshot); }
                    catch (InvalidDataException) { document = NewDocument(snapshot); }
                    document.LastAttemptUtc = Now();
                    mutation(document);
                    Write(document);
                });
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Document NewDocument(Snapshot snapshot)
        {
            return new Document { SchemaVersion = 1, Configuration = Configuration(snapshot), Devices = Devices(snapshot), LastAttemptUtc = Now() };
        }

        private void WithLock(Action action)
        {
            using (Mutex mutex = machineWide ? OpenSharedMutex(mutexName) : new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(1000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { throw new IOException("History is busy."); }
                    action();
                }
                finally { if (acquired) { mutex.ReleaseMutex(); } }
            }
        }

        private Document Read()
        {
            if (machineWide) { PrepareSharedStorage(false); }
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { throw new InvalidDataException(); }
            if (machineWide && !IsSecureStorageAcl(File.GetAccessControl(path), false)) { throw new UnauthorizedAccessException("History file permissions are unsafe."); }
            try
            {
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumBytes) { throw new InvalidDataException(); }
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true)) { json = reader.ReadToEnd(); }
                }
                Document document = Serializer().Deserialize<Document>(json);
                if (document == null || document.SchemaVersion != 1 || !Digest(document.Configuration) || !Digest(document.Devices)
                    || document.Disks == null || document.Gpus == null || document.Disks.Count > 32 || document.Gpus.Count > 32) { throw new InvalidDataException(); }
                Timestamp(document.LastAttemptUtc);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (DiskRecord record in document.Disks)
                {
                    if (record == null || !Digest(record.Key) || !Digest(record.Configuration) || !keys.Add("d" + record.Key) || !ValidDisk(record.Progress)) { throw new InvalidDataException(); }
                    Timestamp(record.TestedAtUtc);
                }
                foreach (GpuRecord record in document.Gpus)
                {
                    if (record == null || !Digest(record.Key) || !Digest(record.Configuration) || !keys.Add("g" + record.Key) || !ValidGpu(record.Progress)) { throw new InvalidDataException(); }
                    Timestamp(record.TestedAtUtc);
                }
                return document;
            }
            catch (ArgumentException exception) { throw new InvalidDataException("Invalid history.", exception); }
            catch (InvalidOperationException exception) { throw new InvalidDataException("Invalid history.", exception); }
        }

        private void Write(Document document)
        {
            WriteJson(Serializer().Serialize(document));
        }

        private void WriteJson(string json)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            if (bytes.Length > MaximumBytes) { throw new InvalidDataException(); }
            string directory = Path.GetDirectoryName(path);
            if (machineWide) { PrepareSharedStorage(true); }
            else { Directory.CreateDirectory(directory); }
            if (machineWide && File.Exists(path) && !IsSecureStorageAcl(File.GetAccessControl(path), false)) { throw new UnauthorizedAccessException("History file permissions are unsafe."); }
            string temporary = Path.Combine(directory, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = machineWide
                    ? new FileStream(temporary, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, (FileSecurity)StorageSecurity(false))
                    : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (machineWide) { PrepareSharedStorage(false); }
                if (File.Exists(path)) { File.Replace(temporary, path, null); }
                else { File.Move(temporary, path); }
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private void PrepareSharedStorage(bool create)
        {
            RejectReparseAncestors(path);
            string directory = Path.GetDirectoryName(path);
            foreach (string target in new[] { Path.GetDirectoryName(directory), directory })
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(target); }
                catch (FileNotFoundException) { if (!create) { continue; } Directory.CreateDirectory(target, (DirectorySecurity)StorageSecurity(true)); attributes = File.GetAttributes(target); }
                catch (DirectoryNotFoundException) { if (!create) { continue; } Directory.CreateDirectory(target, (DirectorySecurity)StorageSecurity(true)); attributes = File.GetAttributes(target); }
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0
                    || !IsSecureStorageAcl(Directory.GetAccessControl(target), true)) { throw new UnauthorizedAccessException("History directory permissions are unsafe."); }
            }
            RejectReparseAncestors(path);
        }

        internal static void RejectReparseAncestors(string target)
        {
            for (string current = Path.GetFullPath(target); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new IOException("History cannot traverse a reparse point."); }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static FileSystemSecurity StorageSecurity(bool directory)
        {
            FileSystemSecurity security = directory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            InheritanceFlags inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, (directory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read) | FileSystemRights.Synchronize,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        internal static bool IsSecureStorageAcl(FileSystemSecurity security, bool directory)
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            if ((!administrators.Equals(owner) && !system.Equals(owner)) || (directory && !security.AreAccessRulesProtected)) { return false; }
            const FileSystemRights mutations = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            bool administratorsFull = false, systemFull = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) { continue; }
                bool admin = administrators.Equals(rule.IdentityReference), localSystem = system.Equals(rule.IdentityReference);
                if (!admin && !localSystem && (rule.FileSystemRights & mutations) != 0) { return false; }
                bool full = (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                if (admin && full) { administratorsFull = true; }
                if (localSystem && full) { systemFull = true; }
            }
            return administratorsFull && systemFull;
        }

        internal static MutexSecurity SharedMutexSecurity()
        {
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            return security;
        }

        internal static Mutex OpenSharedMutex(string name)
        {
            try { return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); }
            catch (WaitHandleCannotBeOpenedException)
            {
                bool created;
                try { return new Mutex(false, name, out created, SharedMutexSecurity()); }
                catch (UnauthorizedAccessException) { return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); }
            }
        }

        private static bool ValidDisk(BenchmarkProgress progress)
        {
            return progress != null && !progress.IsRunning && string.IsNullOrEmpty(progress.Error) && progress.Percent == 100
                && Positive(progress.ReadBytesPerSecond) && Positive(progress.WriteBytesPerSecond)
                && progress.TestSizeBytes > 0 && progress.TestSizeBytes <= 1073741824L && progress.BlockSizeBytes > 0 && progress.BlockSizeBytes <= 4194304
                && progress.QueueDepth > 0 && progress.QueueDepth <= 128 && progress.ThreadCount > 0 && progress.ThreadCount <= 64
                && (!progress.ReadSeconds.HasValue || Positive(progress.ReadSeconds)) && (!progress.WriteSeconds.HasValue || Positive(progress.WriteSeconds))
                && (!progress.ReadBytes.HasValue || progress.ReadBytes > 0) && (!progress.WriteBytes.HasValue || progress.WriteBytes > 0)
                && (!progress.WarmupSeconds.HasValue || (progress.WarmupSeconds >= 0 && progress.WarmupSeconds <= 60))
                && (!progress.MeasurementSeconds.HasValue || (progress.MeasurementSeconds > 0 && progress.MeasurementSeconds <= 3600))
                && (progress.Profile == "diskspd-seq-v1" || progress.Profile == "native-seq-v1")
                && Text(progress.Engine, true) && Text(progress.EngineVersion, true) && Text(progress.Method, true)
                && Text(progress.CacheMode, false) && Text(progress.Stage, false);
        }

        private static bool ValidGpu(GpuBenchmarkProgress progress)
        {
            return progress != null && !progress.IsRunning && progress.Completed && string.IsNullOrEmpty(progress.Error)
                && progress.Percent == 100 && Positive(progress.FramesPerSecond) && Positive(progress.FrameTimeMs)
                && Positive(progress.DurationSeconds) && progress.DurationSeconds <= 3600
                && Math.Abs(progress.FramesPerSecond.Value * progress.FrameTimeMs.Value - 1000) <= 1
                && (!progress.MaxTemperature.HasValue || Finite(progress.MaxTemperature.Value))
                && (!progress.WarmupSeconds.HasValue || (progress.WarmupSeconds >= 0 && progress.WarmupSeconds <= 60))
                && (!progress.PlannedMeasurementSeconds.HasValue || (progress.PlannedMeasurementSeconds > 0 && progress.PlannedMeasurementSeconds <= 3600))
                && Text(progress.Preset, true) && Text(progress.AdapterName, false) && Text(progress.Stage, false);
        }

        private static bool Positive(double? number) { return number.HasValue && Finite(number.Value) && number.Value > 0; }
        private static bool Finite(double number) { return !double.IsNaN(number) && !double.IsInfinity(number); }
        private static bool Text(string value, bool required) { return (!required || !string.IsNullOrWhiteSpace(value)) && (value == null || value.Length <= 2048); }
        private static bool Digest(string value) { return value != null && value.Length == 64 && value.All(character => (character >= '0' && character <= '9') || (character >= 'A' && character <= 'F')); }
        private static string Now() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
        private static bool ResultTimestamp(DateTime? value, out string result)
        {
            result = value.HasValue ? value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : Now();
            try { Timestamp(result); return true; }
            catch (InvalidDataException) { return false; }
        }
        private static DateTime Timestamp(string value)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)
                || parsed.Kind != DateTimeKind.Utc || parsed < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) || parsed > DateTime.UtcNow) { throw new InvalidDataException(); }
            return parsed;
        }
        private static string SavedStage(DateTime time) { return "Сохранённый тест · " + time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")); }
        private static void SetLastTest(BenchmarkHistoryResult result, DateTime time) { if (!result.LastTestUtc.HasValue || time > result.LastTestUtc.Value) { result.LastTestUtc = time; } }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 16 }; }
        private static T Copy<T>(T source) { return Serializer().Deserialize<T>(Serializer().Serialize(source)); }
        private static string DiskKey(DiskSnapshot disk)
        {
            // Model/capacity/drive letters cannot distinguish an identical replacement disk.
            return disk == null || string.IsNullOrWhiteSpace(disk.PersistentHardwareId) ? null : Hash("disk:" + disk.PersistentHardwareId);
        }
        private static string GpuKey(GpuSnapshot gpu)
        {
            if (gpu != null && !string.IsNullOrWhiteSpace(gpu.PersistentHardwareId)) { return Hash("gpu-hardware:" + Normalize(gpu.PersistentHardwareId)); }
            if (gpu == null || string.IsNullOrWhiteSpace(gpu.Name) || !gpu.MemoryBytes.HasValue || !gpu.IsIntegrated.HasValue) { return null; }
            return Hash("gpu:" + Normalize(gpu.Name) + "|" + gpu.MemoryBytes.Value.ToString(CultureInfo.InvariantCulture) + "|" + gpu.IsIntegrated.Value);
        }
        private static string Configuration(Snapshot snapshot)
        {
            if (snapshot == null) { throw new ArgumentNullException("snapshot"); }
            return Hash(Normalize(snapshot.CpuName) + "|" + snapshot.CpuCores + "|" + snapshot.CpuThreads + "|"
                + (snapshot.RamInstalledBytes ?? snapshot.RamTotalBytes).ToString() + "|" + Normalize(snapshot.RamType) + "|" + Normalize(snapshot.RamRate));
        }
        private static string Devices(Snapshot snapshot)
        {
            return Hash(string.Join("|", snapshot.Disks.Select(disk => "d:" + (DiskKey(disk) ?? Hash(Normalize(disk.Name) + "|" + disk.SizeBytes + "|" + Normalize(disk.Volumes))))
                .Concat(snapshot.Gpus.Select(gpu => "g:" + (GpuKey(gpu) ?? Hash(Normalize(gpu.Name))))).OrderBy(value => value, StringComparer.Ordinal)));
        }
        private static string Normalize(string value) { return (value ?? "").Trim().ToUpperInvariant(); }
        private static string Hash(string value)
        {
            using (SHA256 hash = SHA256.Create()) { return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
        }

        private sealed class Document
        {
            public int SchemaVersion;
            public string Configuration;
            public string Devices;
            public string LastAttemptUtc;
            public List<DiskRecord> Disks = new List<DiskRecord>();
            public List<GpuRecord> Gpus = new List<GpuRecord>();
        }
        private sealed class DiskRecord { public string Key; public string Configuration; public string TestedAtUtc; public BenchmarkProgress Progress; }
        private sealed class GpuRecord { public string Key; public string Configuration; public string TestedAtUtc; public GpuBenchmarkProgress Progress; }
    }
}
