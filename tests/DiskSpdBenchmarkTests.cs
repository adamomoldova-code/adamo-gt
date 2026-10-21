using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class DiskSpdBenchmarkTests
    {
        public static void Results()
        {
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
                var read = DiskSpdBenchmark.ParseResult(ResultXml(false), false);
                var write = DiskSpdBenchmark.ParseResult(ResultXml(true), true);
                Equal(10L * 1024 * 1024 * 1024, read.Bytes, "Reads use total measured bytes, including repeated file passes.");
                Equal(5.02, read.Seconds, "The XML duration is parsed independently of Russian decimal formatting.");
                Equal(read.Bytes, write.Bytes, "Read and write counters use the same units.");
                string valid = ResultXml(false);
                string[] invalid =
                {
                    "", "<Results>", valid.Replace("<Version>2.3.0</Version>", "<Version>2.2.0</Version>"),
                    valid.Replace("<TestTimeSeconds>5.02</TestTimeSeconds>", "<TestTimeSeconds>0.01</TestTimeSeconds>"),
                    valid.Replace("<TestTimeSeconds>5.02</TestTimeSeconds>", "<TestTimeSeconds>NaN</TestTimeSeconds>"),
                    valid.Replace("<TestTimeSeconds>5.02</TestTimeSeconds>", "<TestTimeSeconds>5,02</TestTimeSeconds>"),
                    valid.Replace("<ReadBytes>10737418240</ReadBytes>", "<ReadBytes>9223372036854775808</ReadBytes>"),
                    valid.Replace("<WriteBytes>0</WriteBytes>", "<WriteBytes>1048576</WriteBytes>"),
                    valid.Replace("<ReadCount>10240</ReadCount>", "<ReadCount>100</ReadCount>"),
                    valid.Replace("<FileSize>1073741824</FileSize>", "<FileSize>0</FileSize>"),
                    valid.Replace("<DisableOSCache>true</DisableOSCache>", "<DisableOSCache>false</DisableOSCache>"),
                    valid.Replace("<RequestCount>8</RequestCount>", "<RequestCount>1</RequestCount>"),
                    valid.Replace("<Path>workload.tmp</Path>", "<Path>C:\\user-file.txt</Path>"),
                    valid.Replace("<Duration>5</Duration>", "<Duration>3</Duration>"),
                    valid.Replace("<ThreadsPerFile>1</ThreadsPerFile>", "<ThreadsPerFile>2</ThreadsPerFile>"),
                    valid.Replace("<StrideSize>1048576</StrideSize>", "<Random>1048576</Random>"),
                    valid.Replace("<WriteRatio>0</WriteRatio>", "<WriteRatio>100</WriteRatio>"),
                    valid.Replace("<BytesCount>10737418240</BytesCount>", "<BytesCount>0</BytesCount>"),
                    valid.Replace("</Results>", "<Error>Partial run</Error></Results>"),
                    valid.Replace("</Thread>", "</Thread><Thread><Target /></Thread>"),
                    "<!DOCTYPE Results [<!ENTITY sample SYSTEM 'file:///does-not-exist'>]>" + valid
                };
                foreach (string xml in invalid)
                {
                    Throws<InvalidDataException>(delegate { DiskSpdBenchmark.ParseResult(xml, false); }, "Incomplete or mismatched XML must not produce a speed.");
                }
                Throws<InvalidDataException>(delegate { DiskSpdBenchmark.ParseResult(valid, true); }, "Read data cannot become a write result.");
            }
            finally { Thread.CurrentThread.CurrentCulture = original; }
        }

        public static void CancellationAndTimeout()
        {
            var success = new FakeProcess { Output = ResultXml(false), CompleteAfter = 2 };
            string result = Invoke(success, CancellationToken.None, 1000);
            Equal(success.Output, result, "Successful child output is returned without rate conversion.");
            Require(!success.Killed && success.Disposed, "Successful child is disposed without killing.");

            using (var source = new CancellationTokenSource())
            {
                var canceled = new FakeProcess { OnWait = delegate { source.Cancel(); } };
                Throws<OperationCanceledException>(delegate { Invoke(canceled, source.Token, 1000); }, "Cancel propagates.");
                Require(canceled.Killed && canceled.Disposed, "Cancel kills and disposes the owned child.");
            }
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                var canceled = new FakeProcess();
                Throws<OperationCanceledException>(delegate { Invoke(canceled, source.Token, 1000); }, "Pre-canceled runs stop before launch.");
                Require(!canceled.Started, "Pre-canceled runs never launch a process.");
            }
            var timedOut = new FakeProcess { OnWait = delegate { Thread.Sleep(2); } };
            Throws<TimeoutException>(delegate { Invoke(timedOut, CancellationToken.None, 1); }, "Timeout propagates.");
            Require(timedOut.Killed && timedOut.Disposed, "Timeout kills the child before cleanup.");

            var tooLarge = new FakeProcess { OutputLimitExceededValue = true };
            Throws<InvalidDataException>(delegate { Invoke(tooLarge, CancellationToken.None, 1000); }, "Unbounded output is rejected.");
            Require(tooLarge.Killed, "Excessive output stops the child.");
            var failed = new FakeProcess { CompleteAfter = 1, ExitCodeValue = 9, Output = ResultXml(false) };
            Throws<InvalidOperationException>(delegate { Invoke(failed, CancellationToken.None, 1000); }, "Nonzero exit never becomes a successful measurement.");
        }

        public static void PreparationAndBoundaries()
        {
            foreach (string invalid in new[] { "", @"\\server\share\", @"\\.\PhysicalDrive0", @"C:\Windows\", "#0" })
            {
                Throws<InvalidOperationException>(delegate { DiskSpdBenchmark.ValidateVolume(invalid); }, "Only a local volume root is accepted.");
            }
            BenchmarkProgress description = DiskSpdBenchmark.DescribeProfile(7);
            Require(description.TestSizeBytes == 1073741824 && description.BlockSizeBytes == 1048576
                && description.QueueDepth == 8 && description.ThreadCount == 1 && description.WarmupSeconds == 1
                && description.MeasurementSeconds == 5 && description.DiskIndex == 7, "Published metadata matches the fixed workload.");
            Require(description.ReadBytesPerSecond == null && description.WriteBytesPerSecond == null, "Describing a planned test cannot fabricate measured rates.");
            Require(!DiskSpdBenchmark.BuildArguments(false).Contains("-c") && !DiskSpdBenchmark.BuildArguments(true).Contains("-c"), "Engine never creates or truncates an arbitrary target.");

            string path = Path.Combine(Path.GetTempPath(), "ADAMO-GT-prepare-unit-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    var fractions = new List<double>();
                    DiskSpdBenchmark.PrepareFile(stream, 65536, CancellationToken.None, fractions.Add, 10000);
                    Equal(65536L, stream.Length, "Preparation actually writes the whole file.");
                    byte[] bytes = new byte[65536];
                    stream.Position = 0;
                    Equal(bytes.Length, stream.Read(bytes, 0, bytes.Length), "Prepared file is readable.");
                    Require(bytes.Take(4096).Any(value => value != 0) && bytes.Skip(61440).Any(value => value != 0), "Beginning and end contain initialized data.");
                    Require(bytes.Distinct().Count() > 200, "Preparation does not leave a zero-filled or trivially compressible target.");
                    Equal(1d, fractions.Last(), "Preparation completion is reported after all writes.");
                }
                Throws<InvalidDataException>(delegate { using (DiskSpdBenchmark.OpenVerifiedEngine(path)) { } }, "A substituted engine is rejected before launch.");
            }
            finally { if (File.Exists(path)) { File.Delete(path); } }

            path = Path.Combine(Path.GetTempPath(), "ADAMO-GT-prepare-unit-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous))
                using (var source = new CancellationTokenSource())
                {
                    source.Cancel();
                    Throws<OperationCanceledException>(delegate { DiskSpdBenchmark.PrepareFile(stream, 65536, source.Token, null, 10000); }, "Canceled preparation stops before writing.");
                    Equal(0L, stream.Length, "Canceled preparation leaves no unwritten file extents pretending to be initialized data.");
                    Throws<TimeoutException>(delegate { DiskSpdBenchmark.PrepareFile(stream, 65536, CancellationToken.None, null, 0); }, "Preparation has its own time limit.");
                }
            }
            finally { if (File.Exists(path)) { File.Delete(path); } }
        }

        private static string Invoke(FakeProcess process, CancellationToken token, int timeout)
        {
            return DiskSpdBenchmark.RunProcess("unused.exe", DiskSpdBenchmark.BuildArguments(false), "unused", token, null, delegate { return process; }, timeout);
        }

        private static string ResultXml(bool write)
        {
            string readBytes = write ? "0" : "10737418240";
            string writeBytes = write ? "10737418240" : "0";
            return "<Results><System><Tool><Version>2.3.0</Version></Tool></System><Profile><TimeSpans><TimeSpan>"
                + "<Duration>5</Duration><Warmup>1</Warmup><Cooldown>0</Cooldown><DisableAffinity>true</DisableAffinity><Targets><Target>"
                + "<Path>workload.tmp</Path><BlockSize>1048576</BlockSize><RequestCount>8</RequestCount><ThreadsPerFile>1</ThreadsPerFile>"
                + "<WriteRatio>" + (write ? "100" : "0") + "</WriteRatio><StrideSize>1048576</StrideSize><DisableOSCache>true</DisableOSCache>"
                + "<WriteBufferContent><Pattern>random</Pattern><RandomDataSource><SizeInBytes>67108864</SizeInBytes></RandomDataSource></WriteBufferContent>"
                + "</Target></Targets></TimeSpan></TimeSpans></Profile><TimeSpan><TestTimeSeconds>5.02</TestTimeSeconds><ThreadCount>1</ThreadCount>"
                + "<Thread><Id>0</Id><Target><Path>workload.tmp</Path><FileSize>1073741824</FileSize><BytesCount>10737418240</BytesCount><IOCount>10240</IOCount>"
                + "<ReadBytes>" + readBytes + "</ReadBytes><ReadCount>" + (write ? "0" : "10240") + "</ReadCount>"
                + "<WriteBytes>" + writeBytes + "</WriteBytes><WriteCount>" + (write ? "10240" : "0") + "</WriteCount></Target></Thread></TimeSpan></Results>";
        }

        private sealed class FakeProcess : DiskSpdBenchmark.IDiskSpdProcess
        {
            public string Output = "";
            public int CompleteAfter = int.MaxValue;
            public int ExitCodeValue;
            public bool OutputLimitExceededValue;
            public Action OnWait;
            public bool Started;
            public bool Killed;
            public bool Disposed;
            private int waits;
            public bool HasExited { get { return Killed || waits >= CompleteAfter; } }
            public int ExitCode { get { return ExitCodeValue; } }
            public string StandardOutput { get { return Output; } }
            public bool OutputLimitExceeded { get { return OutputLimitExceededValue; } }
            public void Start(string executable, string arguments, string directory) { Started = true; }
            public bool WaitForExit(int milliseconds) { waits++; if (OnWait != null && !Killed) { OnWait(); } return HasExited; }
            public void Kill() { Killed = true; }
            public void Dispose() { Disposed = true; }
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new Exception(message); }
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!object.Equals(expected, actual)) { throw new Exception(message + " Expected=" + expected + "; actual=" + actual); }
        }

        private static void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception(message);
        }
    }
}
