using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class StressSsdTests
    {
        public static void Run()
        {
            PatternIntegrity();
            OwnedTemporaryFile();
            CancellationBeforeDiskAccess();
        }

        private static void PatternIntegrity()
        {
            byte[] pattern = StressWorkloads.CreateSsdPattern(65536);
            Require(pattern.SequenceEqual(StressWorkloads.CreateSsdPattern(65536)), "SSD pattern must be deterministic.");
            Require(!pattern.SequenceEqual(StressWorkloads.CreateSsdPattern(65536, 2)), "Rewriting must change the full payload, not only its header.");
            Require(pattern.Distinct().Count() > 240, "SSD pattern must not be zero-filled or trivially compressible.");
            IntPtr expected = Marshal.AllocHGlobal(pattern.Length);
            IntPtr actual = Marshal.AllocHGlobal(pattern.Length);
            try
            {
                Marshal.Copy(pattern, 0, expected, pattern.Length); Marshal.Copy(pattern, 0, actual, pattern.Length);
                StressWorkloads.SetSsdPatternIdentity(expected, pattern.Length, 17, 3); StressWorkloads.SetSsdPatternIdentity(actual, pattern.Length, 17, 3);
                StressWorkloads.VerifySsdPattern(expected, actual, pattern.Length, 17);
                StressWorkloads.SetSsdPatternIdentity(actual, pattern.Length, 18, 3);
                Throws<InvalidDataException>(delegate { StressWorkloads.VerifySsdPattern(expected, actual, pattern.Length, 17); }, "Misplaced block must fail integrity verification.");
                StressWorkloads.SetSsdPatternIdentity(actual, pattern.Length, 17, 2);
                Throws<InvalidDataException>(delegate { StressWorkloads.VerifySsdPattern(expected, actual, pattern.Length, 17); }, "Stale generation must fail integrity verification.");
                StressWorkloads.SetSsdPatternIdentity(actual, pattern.Length, 17, 3);
                Marshal.WriteInt32(actual, 512 + 4, 18);
                Throws<InvalidDataException>(delegate { StressWorkloads.VerifySsdPattern(expected, actual, pattern.Length, 17); }, "A misplaced inner sector must fail independently of the block's first header.");
                StressWorkloads.SetSsdPatternIdentity(actual, pattern.Length, 17, 3);
                Marshal.WriteByte(actual, pattern.Length - 1, (byte)(pattern[pattern.Length - 1] ^ 1));
                Throws<InvalidDataException>(delegate { StressWorkloads.VerifySsdPattern(expected, actual, pattern.Length, 17); }, "Tail-byte corruption must fail; verification covers the full block.");
            }
            finally { Marshal.FreeHGlobal(actual); Marshal.FreeHGlobal(expected); }
            Require(StressWorkloads.StressSsdFileBytes == 268435456L && StressWorkloads.StressSsdWriteLimitBytes == 8589934592L,
                "SSD limits must remain 256 MB scratch and 8 GB total writes.");
        }

        private static void OwnedTemporaryFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ADAMO-GT-ssd-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string unrelated = Path.Combine(directory, "keep.txt");
            File.WriteAllText(unrelated, "existing user data");
            string path = null;
            try
            {
                using (var scratch = StressWorkloads.SsdScratch.Create(directory, false))
                {
                    path = scratch.FilePath;
                    Require(File.Exists(path), "Scratch must own a newly created file.");
                    Require(new FileInfo(path).Length == 0, "Lifecycle tests must not execute an SSD workload.");
                    Require(Path.GetDirectoryName(path) == directory, "Injected scratch path must stay in its test directory.");
                    Throws<IOException>(delegate { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None)) { } }, "Owned handle must exclude file replacement or external writes.");
                }
                Require(!File.Exists(path), "Closing the owned handle must remove only the scratch file.");
                try
                {
                    using (var scratch = StressWorkloads.SsdScratch.Create(directory, false))
                    { path = scratch.FilePath; throw new OperationCanceledException(); }
                }
                catch (OperationCanceledException) { }
                Require(!File.Exists(path), "Cancellation cleanup must remove the owned scratch file.");
                Require(File.ReadAllText(unrelated) == "existing user data", "Unrelated data must remain untouched.");
                Require(Directory.GetFiles(directory).Length == 1, "No orphan scratch files may remain.");
                Throws<IOException>(delegate { StressWorkloads.SsdScratch.Create(Path.Combine(directory, "missing"), false); }, "Missing directory must be rejected rather than created.");
            }
            finally
            {
                if (File.Exists(unrelated)) { File.Delete(unrelated); }
                Directory.Delete(directory, false);
            }
        }

        private static void CancellationBeforeDiskAccess()
        {
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                Throws<OperationCanceledException>(delegate { StressWorkloads.RunSsd(new StressOptions { DurationSeconds = 1 }, null, source.Token); },
                    "Canceled stress must stop before disk probing or file creation.");
            }
            Throws<ArgumentOutOfRangeException>(delegate { StressWorkloads.RunSsd(new StressOptions { DurationSeconds = 901 }, null, CancellationToken.None); },
                "Unbounded stress duration must be rejected.");
        }

        private static void Require(bool value, string message) { if (!value) { throw new Exception(message); } }
        private static void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception(message);
        }
    }
}
