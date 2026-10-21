using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace PcPulse
{
    public static class DiskSpdBenchmark
    {
        public const string ProfileName = "diskspd-seq-v1";
        public const string EngineVersion = "2.3.0";
        public const long FileSizeBytes = 1024L * 1024 * 1024;
        public const int BlockSizeBytes = 1024 * 1024;
        public const int QueueDepth = 8;
        public const int ThreadCount = 1;
        public const int WarmupSeconds = 1;
        public const int MeasurementSeconds = 5;
        internal const string TargetName = "workload.tmp";
        internal const string EngineSha256 = "E0C1070A8481EE0C7D1C05DE4158F50019E75F20B42D1083E98870E52C02DEFF";
        private const int OutputLimit = 4 * 1024 * 1024;
        private const int ProcessTimeoutMilliseconds = 20000;
        private const int PreparationTimeoutMilliseconds = 60000;

        public static BenchmarkProgress DescribeProfile(uint diskIndex)
        {
            return new BenchmarkProgress
            {
                DiskIndex = diskIndex,
                Engine = "Microsoft DiskSpd (MIT source build)",
                EngineVersion = EngineVersion,
                Profile = ProfileName,
                TestSizeBytes = FileSizeBytes,
                BlockSizeBytes = BlockSizeBytes,
                QueueDepth = QueueDepth,
                ThreadCount = ThreadCount,
                WarmupSeconds = WarmupSeconds,
                MeasurementSeconds = MeasurementSeconds,
                CacheMode = "Кэш Windows отключён; write-through не запрошен",
                Method = "DiskSpd 2.3 · SEQ 1 MB · Q8T1 · файл 1 GB · 5 с на чтение/запись · без кэша Windows"
            };
        }

        public static void Run(DiskSnapshot disk, Action<BenchmarkProgress> report, CancellationToken token)
        {
            BenchmarkProgress progress = DescribeProfile(disk == null ? 0 : disk.Index);
            progress.IsRunning = true;
            progress.Stage = "Подготовка DiskSpd";
            Publish(report, progress);
            ScratchFile scratch = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (disk == null) { throw new InvalidOperationException("Накопитель не выбран."); }
                string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "diskspd", "diskspd.exe");
                using (FileStream engine = OpenVerifiedEngine(executable))
                using (StorageTopology.BenchmarkTarget target = StorageTopology.OpenBenchmarkTarget(disk, FileSizeBytes))
                {
                    scratch = ScratchFile.Create(target.VolumeRoot);
                    progress.Stage = "Подготовка 1 GB случайных данных";
                    Publish(report, progress);
                    PrepareFile(scratch.Stream, FileSizeBytes, token, delegate(double fraction)
                    {
                        progress.Percent = fraction * 15;
                        Publish(report, progress);
                    }, PreparationTimeoutMilliseconds);

                    for (int direction = 0; direction < 2; direction++)
                    {
                        token.ThrowIfCancellationRequested();
                        bool write = direction == 1;
                        progress.Stage = write ? "DiskSpd · запись SEQ 1 MB Q8T1" : "DiskSpd · чтение SEQ 1 MB Q8T1";
                        progress.Percent = write ? 55 : 15;
                        Publish(report, progress);
                        string xml = RunProcess(executable, BuildArguments(write), scratch.DirectoryPath, token,
                            delegate(double elapsed)
                            {
                                progress.Percent = (write ? 55 : 15) + Math.Min(1, elapsed / (WarmupSeconds + MeasurementSeconds)) * 39;
                                Publish(report, progress);
                            }, delegate { return new DiskSpdProcess(); }, ProcessTimeoutMilliseconds);
                        DiskSpdMeasurement measurement = ParseResult(xml, write);
                        if (write)
                        {
                            progress.WriteBytes = measurement.Bytes;
                            progress.WriteSeconds = measurement.Seconds;
                            progress.WriteBytesPerSecond = measurement.Bytes / measurement.Seconds;
                        }
                        else
                        {
                            progress.ReadBytes = measurement.Bytes;
                            progress.ReadSeconds = measurement.Seconds;
                            progress.ReadBytesPerSecond = measurement.Bytes / measurement.Seconds;
                        }
                        Publish(report, progress);
                    }
                }
                token.ThrowIfCancellationRequested();
                progress.Percent = 100;
                progress.Stage = "Готово · DiskSpd SEQ 1 MB Q8T1";
            }
            catch (OperationCanceledException)
            {
                ClearMeasurements(progress);
                progress.Stage = "Тест остановлен";
            }
            catch (Exception exception)
            {
                ClearMeasurements(progress);
                progress.Stage = "Тест недоступен";
                progress.Error = exception.Message;
            }
            finally
            {
                if (scratch != null)
                {
                    try { scratch.Dispose(); }
                    catch (Exception exception)
                    {
                        progress.Error = (progress.Error.Length == 0 ? "" : progress.Error + " ") + exception.Message;
                    }
                }
                progress.IsRunning = false;
                Publish(report, progress);
            }
        }

        internal static string BuildArguments(bool write)
        {
            // A fixed relative target avoids both command injection and DiskSpd's ANSI path/XML escaping limitations.
            return "-b1M -s1M -o8 -t1 -d5 -W1 -C0 -Su -n -bsp -Z64M -Rxml "
                + (write ? "-w100" : "-w0") + " \"" + TargetName + "\"";
        }

        internal static FileStream OpenVerifiedEngine(string executable)
        {
            if (!File.Exists(executable)) { throw new FileNotFoundException("В комплекте отсутствует DiskSpd 2.3. Распакуйте архив ADAMO GT полностью."); }
            FileStream stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                string hash;
                using (SHA256 algorithm = SHA256.Create()) { hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", ""); }
                if (!string.Equals(hash, EngineSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Файл DiskSpd не соответствует проверенной сборке ADAMO GT. Повторно распакуйте официальный комплект.");
                }
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        internal static void PrepareFile(FileStream stream, long bytes, CancellationToken token, Action<double> report, int timeoutMilliseconds)
        {
            if (bytes <= 0 || bytes > FileSizeBytes || bytes % 4096 != 0) { throw new ArgumentOutOfRangeException("bytes"); }
            Stopwatch clock = Stopwatch.StartNew();
            byte[] buffer = new byte[Math.Min(BlockSizeBytes, bytes)];
            long lastReport = -1000;
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                for (long written = 0; written < bytes; written += buffer.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (clock.ElapsedMilliseconds >= timeoutMilliseconds) { throw new TimeoutException("Подготовка тестового файла заняла слишком много времени."); }
                    random.GetBytes(buffer);
                    int count = (int)Math.Min(buffer.Length, bytes - written);
                    Task write = stream.WriteAsync(buffer, 0, count, token);
                    WaitPreparation(write, stream, clock, timeoutMilliseconds, token);
                    if (report != null && (clock.ElapsedMilliseconds - lastReport >= 200 || written + count == bytes))
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        report((written + count) / (double)bytes);
                    }
                }
            }
            WaitPreparation(stream.FlushAsync(token), stream, clock, timeoutMilliseconds, token);
            if (stream.Length != bytes) { throw new IOException("Подготовлен неполный тестовый файл."); }
        }

        private static void WaitPreparation(Task task, FileStream stream, Stopwatch clock, int timeoutMilliseconds, CancellationToken token)
        {
            while (!task.IsCompleted)
            {
                if (token.IsCancellationRequested || clock.ElapsedMilliseconds >= timeoutMilliseconds)
                {
                    CancelIoEx(stream.SafeFileHandle.DangerousGetHandle(), IntPtr.Zero);
                    try { task.Wait(3000); } catch (AggregateException) { }
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Подготовка тестового файла заняла слишком много времени.");
                }
                try { task.Wait(50); } catch (AggregateException) { break; }
            }
            token.ThrowIfCancellationRequested();
            task.GetAwaiter().GetResult();
        }

        internal static string RunProcess(string executable, string arguments, string directory, CancellationToken token,
            Action<double> report, Func<IDiskSpdProcess> createProcess, int timeoutMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            using (IDiskSpdProcess process = createProcess())
            {
                bool started = false;
                try
                {
                    process.Start(executable, arguments, directory);
                    started = true;
                    Stopwatch clock = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (process.OutputLimitExceeded) { throw new InvalidDataException("DiskSpd вернул слишком большой отчёт."); }
                        if (clock.ElapsedMilliseconds >= timeoutMilliseconds) { throw new TimeoutException("DiskSpd не завершился вовремя; тест остановлен."); }
                        if (report != null) { report(clock.Elapsed.TotalSeconds); }
                    }
                    token.ThrowIfCancellationRequested();
                    if (process.OutputLimitExceeded) { throw new InvalidDataException("DiskSpd вернул слишком большой отчёт."); }
                    if (process.ExitCode != 0) { throw new InvalidOperationException("DiskSpd завершился с ошибкой (код " + process.ExitCode + "). Проверьте доступ к диску и свободное место."); }
                    return process.StandardOutput;
                }
                finally
                {
                    if (started && !process.HasExited)
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000)) { throw new IOException("Windows ещё завершает DiskSpd. Тестовый файл может оставаться занят."); }
                    }
                }
            }
        }

        internal static DiskSpdMeasurement ParseResult(string xml, bool write)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > OutputLimit) { throw InvalidResult(); }
            XmlDocument document = new XmlDocument { XmlResolver = null };
            try
            {
                using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = OutputLimit
                })) { document.Load(reader); }
            }
            catch (XmlException) { throw InvalidResult(); }
            if (document.DocumentElement == null || document.DocumentElement.Name != "Results" || document.SelectNodes("//Error").Count != 0) { throw InvalidResult(); }
            RequireText(document, "/Results/System/Tool/Version", EngineVersion);
            XmlNode profile = Single(document, "/Results/Profile/TimeSpans/TimeSpan");
            RequireInteger(profile, "Duration", MeasurementSeconds);
            RequireInteger(profile, "Warmup", WarmupSeconds);
            RequireInteger(profile, "Cooldown", 0);
            RequireText(profile, "DisableAffinity", "true");
            XmlNode targetProfile = Single(profile, "Targets/Target");
            RequireText(targetProfile, "Path", TargetName);
            RequireInteger(targetProfile, "BlockSize", BlockSizeBytes);
            RequireInteger(targetProfile, "RequestCount", QueueDepth);
            RequireInteger(targetProfile, "ThreadsPerFile", ThreadCount);
            RequireInteger(targetProfile, "WriteRatio", write ? 100 : 0);
            RequireInteger(targetProfile, "StrideSize", BlockSizeBytes);
            RequireText(targetProfile, "DisableOSCache", "true");
            if (targetProfile.SelectNodes("Random|RandomRatio|MemoryMappedIo|BypassIO|FileSize").Count != 0) { throw InvalidResult(); }
            if (targetProfile.SelectSingleNode("WriteThrough") != null) { RequireText(targetProfile, "WriteThrough", "false"); }
            RequireText(targetProfile, "WriteBufferContent/Pattern", "random");
            RequireInteger(targetProfile, "WriteBufferContent/RandomDataSource/SizeInBytes", 64 * 1024 * 1024);

            XmlNode result = Single(document, "/Results/TimeSpan");
            RequireInteger(result, "ThreadCount", ThreadCount);
            XmlNode thread = Single(result, "Thread");
            XmlNode target = Single(thread, "Target");
            RequireText(target, "Path", TargetName);
            RequireInteger(target, "FileSize", FileSizeBytes);
            long readBytes = Integer(target, "ReadBytes");
            long writeBytes = Integer(target, "WriteBytes");
            long bytes = write ? writeBytes : readBytes;
            long count = Integer(target, write ? "WriteCount" : "ReadCount");
            if ((write ? readBytes : writeBytes) != 0 || Integer(target, write ? "ReadCount" : "WriteCount") != 0 || bytes <= 0 || bytes % BlockSizeBytes != 0
                || count != bytes / BlockSizeBytes || Integer(target, "BytesCount") != bytes || Integer(target, "IOCount") != count)
            {
                throw InvalidResult();
            }
            double seconds;
            if (!double.TryParse(Single(result, "TestTimeSeconds").InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < MeasurementSeconds - 0.1 || seconds > MeasurementSeconds + 5)
            {
                throw InvalidResult();
            }
            return new DiskSpdMeasurement { Bytes = bytes, Seconds = seconds };
        }

        private static XmlNode Single(XmlNode node, string path)
        {
            XmlNodeList matches = node.SelectNodes(path);
            if (matches == null || matches.Count != 1) { throw InvalidResult(); }
            return matches[0];
        }

        private static long Integer(XmlNode node, string path)
        {
            long number;
            if (!long.TryParse(Single(node, path).InnerText, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0) { throw InvalidResult(); }
            return number;
        }

        private static void RequireInteger(XmlNode node, string path, long value)
        {
            if (Integer(node, path) != value) { throw InvalidResult(); }
        }

        private static void RequireText(XmlNode node, string path, string value)
        {
            if (!string.Equals(Single(node, path).InnerText, value, StringComparison.Ordinal)) { throw InvalidResult(); }
        }

        private static InvalidDataException InvalidResult()
        {
            return new InvalidDataException("DiskSpd вернул неполный отчёт или параметры измерения не совпали с выбранным профилем. Скорость не рассчитана.");
        }

        internal static string ValidateVolume(string path)
        {
            return StorageTopology.ValidateVolume(path, FileSizeBytes);
        }

        private static void ClearMeasurements(BenchmarkProgress progress)
        {
            progress.ReadBytesPerSecond = null;
            progress.WriteBytesPerSecond = null;
            progress.ReadBytes = null;
            progress.WriteBytes = null;
            progress.ReadSeconds = null;
            progress.WriteSeconds = null;
        }

        private static void Publish(Action<BenchmarkProgress> report, BenchmarkProgress progress)
        {
            if (report == null) { return; }
            try { report(progress.Copy()); } catch (Exception) { }
        }

        internal sealed class DiskSpdMeasurement
        {
            public long Bytes;
            public double Seconds;
        }

        internal interface IDiskSpdProcess : IDisposable
        {
            void Start(string executable, string arguments, string directory);
            bool WaitForExit(int milliseconds);
            void Kill();
            bool HasExited { get; }
            int ExitCode { get; }
            string StandardOutput { get; }
            bool OutputLimitExceeded { get; }
        }

        private sealed class DiskSpdProcess : IDiskSpdProcess
        {
            private readonly Process process = new Process();
            private readonly StringBuilder output = new StringBuilder();
            private readonly object outputLock = new object();
            private readonly ManualResetEvent outputEnded = new ManualResetEvent(false);
            private readonly ManualResetEvent errorEnded = new ManualResetEvent(false);
            private volatile bool exceeded;
            private int errorCharacters;
            public bool HasExited { get { return process.HasExited; } }
            public int ExitCode { get { return process.ExitCode; } }
            public string StandardOutput { get { lock (outputLock) { return output.ToString(); } } }
            public bool OutputLimitExceeded { get { return exceeded; } }

            public void Start(string executable, string arguments, string directory)
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null) { outputEnded.Set(); return; }
                    lock (outputLock)
                    {
                        if (output.Length + args.Data.Length + 2 > OutputLimit) { exceeded = true; }
                        else if (!exceeded) { output.AppendLine(args.Data); }
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null) { errorEnded.Set(); return; }
                    if (args.Data != null && Interlocked.Add(ref errorCharacters, args.Data.Length + 2) > 65536) { exceeded = true; }
                };
                if (!process.Start()) { throw new IOException("Не удалось запустить DiskSpd."); }
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch
                {
                    Kill();
                    process.WaitForExit(5000);
                    throw;
                }
            }

            public bool WaitForExit(int milliseconds)
            {
                if (!process.WaitForExit(milliseconds)) { return false; }
                if (!outputEnded.WaitOne(2000) || !errorEnded.WaitOne(2000))
                {
                    throw new IOException("DiskSpd завершился без полного отчёта.");
                }
                return true;
            }

            public void Kill()
            {
                try { if (!process.HasExited) { process.Kill(); } }
                catch (InvalidOperationException) { if (!process.HasExited) { throw; } }
            }

            public void Dispose() { process.Dispose(); outputEnded.Dispose(); errorEnded.Dispose(); }
        }

        private sealed class ScratchFile : IDisposable
        {
            public string DirectoryPath;
            public FileStream Stream;
            private string filePath;
            private bool owned;
            private bool fileOwned;

            public static ScratchFile Create(string volumeRoot)
            {
                string temporary = Path.GetFullPath(Path.GetTempPath());
                string parent = string.Equals(Path.GetPathRoot(temporary), volumeRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(temporary) ? temporary : volumeRoot;
                for (DirectoryInfo item = new DirectoryInfo(parent); item != null; item = item.Parent)
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("Каталог теста не может проходить через ссылку на другой том."); }
                }
                ScratchFile scratch = new ScratchFile();
                scratch.DirectoryPath = Path.Combine(parent, "ADAMO-GT-diskspd-" + Guid.NewGuid().ToString("N"));
                if (!CreateDirectory(scratch.DirectoryPath, IntPtr.Zero)) { throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать отдельный каталог теста."); }
                scratch.owned = true;
                scratch.filePath = Path.Combine(scratch.DirectoryPath, TargetName);
                try
                {
                    scratch.Stream = new FileStream(scratch.filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
                        4096, FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
                    scratch.fileOwned = true;
                    if ((File.GetAttributes(scratch.filePath) & (FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0)
                    {
                        throw new IOException("Тест недоступен для сжатого, разреженного, EFS-зашифрованного файла или ссылки.");
                    }
                    return scratch;
                }
                catch { scratch.Dispose(); throw; }
            }

            public void Dispose()
            {
                if (!owned) { return; }
                if (Stream != null) { Stream.Dispose(); Stream = null; }
                if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) { throw new IOException("Каталог теста изменился; автоматическое удаление отменено."); }
                if (fileOwned && File.Exists(filePath)) { File.Delete(filePath); }
                Directory.Delete(DirectoryPath, false);
                owned = false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
    }
}
