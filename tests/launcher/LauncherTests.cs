using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AdamoGT.SingleFile.Tests
{
    internal static class LauncherTests
    {
        private static string artifacts;
        private static string probe;
        private static int passed;

        public static int Main(string[] args)
        {
            try
            {
                probe = Path.GetFullPath(args[0]);
                artifacts = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(artifacts);
                Run("Safe Windows archive paths", Paths);
                Run("Malformed and missing manifests", Manifests);
                Run("Missing, corrupt and inconsistent payloads", InvalidPayloads);
                Run("Extract all components and reuse verified cache", Extraction);
                Run("Tampered, missing and extra cached files get a fresh directory", CacheRepair);
                Run("Concurrent extraction preserves complete immutable directories", ConcurrentExtraction);
                Run("Windows argument forwarding, caller directory and exit code", Arguments);
                Run("Cached junctions and redirected ancestors are rejected", ReparsePoints);
                Console.WriteLine("PASS: " + passed + " single-file launcher test groups.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void Run(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }

        private static void Fails(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("Expected rejection: " + message);
        }

        private static void Paths()
        {
            foreach (string good in new[] { "ADAMO-GT.exe", "tools/smartmontools/drivedb.h", "data/экран.json", "com10.txt", "Licenses/A B.txt" })
            { PackageManifest.ValidateName(good); }
            foreach (string bad in new[] { "", "/a", "../a", "x/../../a", "./a", "x//a", "x/", "C:/a", "a:stream", "x\\a", "\\server\\a", "x/CON", "nul.txt", "Lpt9.dll", "com1.txt", "COM\u00b2", "CONIN$", "x/COM1 .exe", "x/..", "x.", "x ", "x/fi\tle", "x/a?", "x/a*", "x/a\"", "x/a|" })
            { string captured = bad; Fails(delegate { PackageManifest.ValidateName(captured); }, "unsafe path " + captured); }
            string normal = RuntimePackage.EntryPath(artifacts, "data/models.json");
            Check(normal.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Entry path left extraction root.");
        }

        private static PackageManifest ReadManifest(string text)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text))) { return PackageManifest.Read(stream); }
        }

        private static void Manifests()
        {
            string hash = new string('a', 64);
            string header = "ADAMO-GT-PACKAGE-1\n" + hash + "\n";
            Fails(delegate { PackageManifest.Read(null); }, "missing manifest");
            foreach (string bad in new[] { "", "bad\n", "ADAMO-GT-PACKAGE-1\ninvalid\n", header, header + "a\t-1\t" + hash,
                header + "a\t67108865\t" + hash, header + "a\t1\tbad", header + "../outside\t0\t" + hash,
                header + "ADAMO-GT.exe\t0\t" + hash + "\nadamo-gt.EXE\t0\t" + hash,
                header + "data\t0\t" + hash + "\ndata/a.txt\t0\t" + hash })
            { string captured = bad; Fails(delegate { ReadManifest(captured); }, "malformed manifest"); }
        }

        private sealed class Fixture
        {
            internal byte[] Payload;
            internal string Metadata;
            internal PackageManifest Manifest { get { return ReadManifest(Metadata); } }
            internal string Prepare(string cache)
            {
                using (var stream = new MemoryStream(Payload)) { return RuntimePackage.Prepare(stream, Manifest, cache); }
            }
        }

        private static Fixture NewFixture(Action<ZipArchive> modify = null)
        {
            var files = new Dictionary<string, byte[]>
            {
                { "ADAMO-GT.exe", File.ReadAllBytes(probe) },
                { "ADAMO-GT.exe.config", Encoding.UTF8.GetBytes("<configuration><startup><supportedRuntime version=\"v4.0\" /></startup></configuration>") },
                { "data/models.json", Encoding.UTF8.GetBytes("{\"fixture\":\"тест\"}") },
                { "tools/sample.bin", new byte[] { 0, 1, 2, 3, 255 } }
            };
            byte[] payload;
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var item in files)
                    {
                        ZipArchiveEntry entry = archive.CreateEntry(item.Key, CompressionLevel.Optimal);
                        using (Stream content = entry.Open()) { content.Write(item.Value, 0, item.Value.Length); }
                    }
                    if (modify != null) { modify(archive); }
                }
                payload = output.ToArray();
            }
            var lines = new List<string> { "ADAMO-GT-PACKAGE-1", HashBytes(payload) };
            lines.AddRange(files.Select(item => item.Key + "\t" + item.Value.Length + "\t" + HashBytes(item.Value)));
            return new Fixture { Payload = payload, Metadata = String.Join("\n", lines) };
        }

        private static string HashBytes(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes)) { return RuntimePackage.Hash(stream); }
        }

        private static void InvalidPayloads()
        {
            Fixture normal = NewFixture();
            Fails(delegate { RuntimePackage.Prepare(null, normal.Manifest, Path.Combine(artifacts, "missing")); }, "missing embedded ZIP");
            byte[] damaged = (byte[])normal.Payload.Clone();
            damaged[0] ^= 1;
            Fails(delegate { using (var stream = new MemoryStream(damaged)) { RuntimePackage.Prepare(stream, normal.Manifest, Path.Combine(artifacts, "bad-hash")); } }, "payload hash mismatch");
            Fixture extra = NewFixture(archive => archive.CreateEntry("extra.dll"));
            Fails(delegate { extra.Prepare(Path.Combine(artifacts, "extra")); }, "unlisted archive file");
            Fixture traversal = NewFixture(archive => archive.CreateEntry("../outside.exe"));
            Fails(delegate { traversal.Prepare(Path.Combine(artifacts, "traversal")); }, "archive traversal");
            Fixture duplicate = NewFixture(archive => archive.CreateEntry("adamo-gt.EXE"));
            Fails(delegate { duplicate.Prepare(Path.Combine(artifacts, "duplicate")); }, "archive case-insensitive duplicate");
            Fixture wrongFileHash = NewFixture();
            string modelHash = HashBytes(Encoding.UTF8.GetBytes("{\"fixture\":\"тест\"}"));
            wrongFileHash.Metadata = wrongFileHash.Metadata.Replace(modelHash, new string('0', 64));
            Fails(delegate { wrongFileHash.Prepare(Path.Combine(artifacts, "file-hash")); }, "uncompressed component hash mismatch");
            Check(!File.Exists(Path.Combine(artifacts, "outside.exe")), "Archive traversal wrote outside cache.");
        }

        private static void Extraction()
        {
            Fixture fixture = NewFixture();
            string cache = Path.Combine(artifacts, "normal");
            string first = fixture.Prepare(cache);
            Check(RuntimePackage.VerifyDirectory(first, fixture.Manifest), "Fresh extraction did not verify.");
            string second = fixture.Prepare(cache);
            Check(first == second, "Identical intact cache was not reused.");
            Check(File.Exists(Path.Combine(first, "ADAMO-GT.exe.config")) && File.Exists(Path.Combine(first, "tools", "sample.bin")), "Nested component or configuration missing.");
        }

        private static void CacheRepair()
        {
            Fixture fixture = NewFixture();
            string cache = Path.Combine(artifacts, "repair");
            string first = fixture.Prepare(cache);
            string corrupted = Path.Combine(first, "data", "models.json");
            File.WriteAllText(corrupted, "tampered");
            string second = fixture.Prepare(cache);
            Check(first != second && RuntimePackage.VerifyDirectory(second, fixture.Manifest), "Tampered cache was not repaired independently.");
            Check(File.ReadAllText(corrupted) == "tampered", "An existing extraction was overwritten.");
            File.WriteAllText(Path.Combine(second, "unexpected.dll"), "extra");
            string third = fixture.Prepare(cache);
            Check(third != second && third != first, "Unexpected cached DLL was accepted.");
            File.Delete(Path.Combine(third, "ADAMO-GT.exe.config"));
            string fourth = fixture.Prepare(cache);
            Check(fourth != third && RuntimePackage.VerifyDirectory(fourth, fixture.Manifest), "Missing cached component was accepted.");
            Check(Directory.Exists(first) && Directory.Exists(second) && Directory.Exists(third), "Repair deleted an existing cache.");
        }

        private static void ConcurrentExtraction()
        {
            Fixture fixture = NewFixture();
            string cache = Path.Combine(artifacts, "concurrent");
            Task<string>[] tasks = Enumerable.Range(0, 8).Select(number => Task.Run(() => fixture.Prepare(cache))).ToArray();
            Task.WaitAll(tasks);
            foreach (Task<string> task in tasks)
            { Check(RuntimePackage.VerifyDirectory(task.Result, fixture.Manifest), "Concurrent launch received partial runtime files."); }
            Check(tasks.Select(task => task.Result).Distinct().Count() <= 8, "Unexpected concurrent extraction count.");
        }

        private static void Arguments()
        {
            string[] originalEnvironment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(entry => (string)entry.Key + "\0" + (string)entry.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Fixture fixture = NewFixture();
            string runtime = fixture.Prepare(Path.Combine(artifacts, "arguments-cache"));
            string working = Path.Combine(artifacts, "caller with spaces — Проверка");
            Directory.CreateDirectory(working);
            string[] values = { "", "simple", "two words", "a\tb", "a\"b", "C:\\trailing\\", "C:\\space path\\", "back\\\\\"quote", "line\nbreak", "русский română", "--anything" };
            string[] forwarded = new[] { "relative probe.txt" }.Concat(values).ToArray();
            int exit = RuntimePackage.RunChild(Path.Combine(runtime, "ADAMO-GT.exe"), forwarded, working);
            Check(exit == 37, "Child exit code was not propagated.");
            string[] result = File.ReadAllLines(Path.Combine(working, "relative probe.txt")).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
            Check(result[0] == working, "Caller working directory changed.");
            Check(result.Skip(1).Take(values.Length).SequenceEqual(values), "Quoted arguments changed in transit.");
            Check(result.Last() == Path.Combine(runtime, "ADAMO-GT.exe"), "A plain child inherited an unrelated launcher route.");
            string parentRoute = Environment.GetEnvironmentVariable("ADAMO_GT_LAUNCHER");
            exit = RuntimePackage.RunChild(Path.Combine(runtime, "ADAMO-GT.exe"), forwarded, working, probe);
            result = File.ReadAllLines(Path.Combine(working, "relative probe.txt")).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
            Check(exit == 37 && result.Last() == probe, "The cached child did not receive its exact original launcher path.");
            Check(Environment.GetEnvironmentVariable("ADAMO_GT_LAUNCHER") == parentRoute, "Child routing changed the parent process environment.");
            // Neither a spoofed internal argument nor an inherited legacy variable
            // may override the exact launcher chosen by this process.
            try
            {
                Environment.SetEnvironmentVariable("ADAMO_GT_LAUNCHER", "C:\\unrelated-launcher.exe");
                string[] spoofed = forwarded.Concat(new[] { PcPulse.SensorStartup.LauncherArgumentPrefix + "C:\\unrelated-launcher.exe" }).ToArray();
                exit = RuntimePackage.RunChild(Path.Combine(runtime, "ADAMO-GT.exe"), spoofed, working, probe);
                result = File.ReadAllLines(Path.Combine(working, "relative probe.txt")).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
                Check(exit == 37 && result.Last() == probe && result.Skip(1).Take(values.Length).SequenceEqual(values), "Spoofed routing changed the launcher or application arguments.");
                Check(Environment.GetEnvironmentVariable("ADAMO_GT_LAUNCHER") == "C:\\unrelated-launcher.exe", "Child routing mutated its parent environment.");
                exit = RuntimePackage.RunChild(Path.Combine(runtime, "ADAMO-GT.exe"), spoofed, working);
                result = File.ReadAllLines(Path.Combine(working, "relative probe.txt")).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
                Check(exit == 37 && result.Last() == Path.Combine(runtime, "ADAMO-GT.exe"), "A plain child accepted untrusted routing.");
            }
            finally { Environment.SetEnvironmentVariable("ADAMO_GT_LAUNCHER", parentRoute); }
            string[] finalEnvironment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(entry => (string)entry.Key + "\0" + (string)entry.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Check(originalEnvironment.SequenceEqual(finalEnvironment), "Launching children changed the parent process environment block.");
        }

        private static void Junction(string link, string target)
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) { throw new IOException("Cannot create junction fixture: " + output + error); }
            }
        }

        private static void ReparsePoints()
        {
            Fixture fixture = NewFixture();
            string runtime = fixture.Prepare(Path.Combine(artifacts, "junction-cache"));
            string target = Path.Combine(artifacts, "junction-target");
            Directory.CreateDirectory(target);
            Junction(Path.Combine(runtime, "redirect"), target);
            Check(!RuntimePackage.VerifyDirectory(runtime, fixture.Manifest), "Cached junction was accepted.");
            string redirectedBase = Path.Combine(artifacts, "redirected-cache-root");
            Junction(redirectedBase, target);
            Fails(delegate { fixture.Prepare(redirectedBase); }, "junction ancestor");
            Check(Directory.GetFileSystemEntries(target).Length == 0, "Extraction wrote through a junction.");
        }
    }
}
