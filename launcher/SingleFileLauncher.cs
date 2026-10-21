using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace AdamoGT.SingleFile
{
    internal sealed class PackageFile
    {
        internal string Name;
        internal long Length;
        internal string Sha256;
    }

    internal sealed class PackageManifest
    {
        internal const long MaximumFileBytes = 64L * 1024 * 1024;
        internal const long MaximumPackageBytes = 256L * 1024 * 1024;
        internal string PayloadHash;
        internal readonly Dictionary<string, PackageFile> Files = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase);

        internal static PackageManifest Read(Stream input)
        {
            if (input == null) { throw new InvalidDataException("Не найден список компонентов программы."); }
            if (input.CanSeek && input.Length > 1024 * 1024) { throw new InvalidDataException("Список компонентов слишком большой."); }
            var result = new PackageManifest();
            using (var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 4096, true))
            {
                if (reader.ReadLine() != "ADAMO-GT-PACKAGE-1") { throw new InvalidDataException("Неверный формат пакета."); }
                result.PayloadHash = reader.ReadLine();
                if (!IsHash(result.PayloadHash)) { throw new InvalidDataException("Неверная контрольная сумма пакета."); }
                result.PayloadHash = result.PayloadHash.ToLowerInvariant();
                long total = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split('\t');
                    long length;
                    if (parts.Length != 3 || !Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out length)
                        || length < 0 || length > MaximumFileBytes || !IsHash(parts[2]))
                    { throw new InvalidDataException("Повреждён список компонентов."); }
                    ValidateName(parts[0]);
                    if (result.Files.Count >= 1024 || result.Files.ContainsKey(parts[0])) { throw new InvalidDataException("Повторяющийся компонент пакета."); }
                    result.Files.Add(parts[0], new PackageFile { Name = parts[0], Length = length, Sha256 = parts[2].ToLowerInvariant() });
                    total += length;
                    if (total > MaximumPackageBytes) { throw new InvalidDataException("Пакет слишком большой."); }
                }
            }
            foreach (string name in result.Files.Keys)
            {
                string parent = name;
                while (parent.LastIndexOf('/') >= 0)
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (result.Files.ContainsKey(parent)) { throw new InvalidDataException("Конфликт файла и папки пакета."); }
                }
            }
            if (!result.Files.ContainsKey("ADAMO-GT.exe") || !result.Files.ContainsKey("ADAMO-GT.exe.config"))
            { throw new InvalidDataException("В пакете отсутствует основная программа."); }
            return result;
        }

        internal static bool IsHash(string value)
        {
            return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        internal static void ValidateName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 180 || name.IndexOf('\\') >= 0 || name.StartsWith("/", StringComparison.Ordinal))
            { throw new InvalidDataException("Недопустимый путь компонента."); }
            foreach (string part in name.Split('/'))
            {
                if (part.Length == 0 || part.Length > 120 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal)
                    || part.EndsWith(" ", StringComparison.Ordinal) || part.Any(c => c < 32 || c == 127 || "<>:\"|?*".IndexOf(c) >= 0))
                { throw new InvalidDataException("Недопустимое имя компонента."); }
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$"
                    || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                        && "123456789\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0))
                { throw new InvalidDataException("Зарезервированное Windows имя компонента."); }
            }
        }
    }

    internal static class RuntimePackage
    {
        internal static string Hash(Stream input)
        {
            using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        }

        internal static string Prepare(Stream payload, PackageManifest manifest, string cacheBase)
        {
            if (payload == null || manifest == null) { throw new InvalidDataException("Не найдены встроенные компоненты программы."); }
            if (!payload.CanSeek || payload.Length > PackageManifest.MaximumPackageBytes) { throw new InvalidDataException("Неверный встроенный архив."); }
            payload.Position = 0;
            if (Hash(payload) != manifest.PayloadHash) { throw new InvalidDataException("Встроенный архив повреждён. Скопируйте ADAMO GT заново."); }
            payload.Position = 0;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                ValidateArchive(archive, manifest);
                string versions = Path.Combine(Path.GetFullPath(cacheBase), manifest.PayloadHash);
                EnsurePlainDirectory(versions);
                // Different processes may extract concurrently. Unique immutable directories
                // avoid replacing files used by another instance, including a RunAs child.
                foreach (string candidate in Directory.EnumerateDirectories(versions).Take(128))
                {
                    if (VerifyDirectory(candidate, manifest)) { return candidate; }
                }
                string destination = Path.Combine(versions, Guid.NewGuid().ToString("N"));
                EnsurePlainDirectory(destination);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    PackageFile expected = manifest.Files[entry.FullName];
                    string path = EntryPath(destination, expected.Name);
                    EnsurePlainDirectory(Path.GetDirectoryName(path));
                    using (Stream source = entry.Open())
                    using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        CopyExactly(source, target, expected.Length);
                    }
                    using (var actual = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (Hash(actual) != expected.Sha256) { throw new InvalidDataException("Контрольная сумма компонента не совпала: " + expected.Name); }
                    }
                }
                if (!VerifyDirectory(destination, manifest)) { throw new InvalidDataException("Не удалось проверить распакованные компоненты."); }
                return destination;
            }
        }

        private static void ValidateArchive(ZipArchive archive, PackageManifest manifest)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (archive.Entries.Count != manifest.Files.Count) { throw new InvalidDataException("Состав архива не совпадает со списком компонентов."); }
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                PackageManifest.ValidateName(entry.FullName);
                PackageFile expected;
                int unixKind = (entry.ExternalAttributes >> 16) & 0xf000;
                if (!seen.Add(entry.FullName) || !manifest.Files.TryGetValue(entry.FullName, out expected)
                    || !String.Equals(entry.FullName, expected.Name, StringComparison.Ordinal) || entry.Length != expected.Length
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                    || (unixKind != 0 && unixKind != 0x8000))
                { throw new InvalidDataException("Неверный компонент архива: " + entry.FullName); }
            }
        }

        private static void CopyExactly(Stream source, Stream target, long length)
        {
            byte[] buffer = new byte[65536];
            long copied = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                copied += read;
                if (copied > length) { throw new InvalidDataException("Компонент превысил заявленный размер."); }
                target.Write(buffer, 0, read);
            }
            if (copied != length) { throw new InvalidDataException("Компонент извлечён не полностью."); }
        }

        internal static string EntryPath(string directory, string name)
        {
            PackageManifest.ValidateName(name);
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("Компонент находится вне папки программы."); }
            return result;
        }

        internal static void EnsurePlainDirectory(string directory)
        {
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full);
            CheckDirectory(root);
            string current = root;
            foreach (string part in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileAttributes? attributes = TryAttributes(current);
                if (!attributes.HasValue) { Directory.CreateDirectory(current); }
                CheckDirectory(current);
            }
        }

        private static FileAttributes? TryAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void CheckDirectory(string path)
        {
            FileAttributes? attributes = TryAttributes(path);
            if (!attributes.HasValue || (attributes.Value & FileAttributes.Directory) == 0 || (attributes.Value & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Папка компонентов недоступна или является ссылкой: " + path); }
        }

        internal static bool VerifyDirectory(string directory, PackageManifest manifest)
        {
            try
            {
                // Also inspect ancestors; validating only the final directory misses junctions.
                string current = Path.GetFullPath(directory);
                while (!String.IsNullOrEmpty(current))
                {
                    CheckDirectory(current);
                    string parent = Path.GetDirectoryName(current);
                    if (parent == current) { break; }
                    current = parent;
                }
                var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in manifest.Files.Keys)
                {
                    string parent = name;
                    while (parent.LastIndexOf('/') >= 0)
                    {
                        parent = parent.Substring(0, parent.LastIndexOf('/'));
                        expectedDirectories.Add(parent);
                    }
                }
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new Stack<string>();
                pending.Push(Path.GetFullPath(directory));
                string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                while (pending.Count != 0)
                {
                    string folder = pending.Pop();
                    CheckDirectory(folder);
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { return false; }
                        string relative = path.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!expectedDirectories.Contains(relative)) { return false; }
                            pending.Push(path);
                            continue;
                        }
                        PackageFile expected;
                        if (!manifest.Files.TryGetValue(relative, out expected) || !found.Add(relative)) { return false; }
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            if (file.Length != expected.Length || Hash(file) != expected.Sha256) { return false; }
                        }
                    }
                }
                return found.Count == manifest.Files.Count;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        internal static string QuoteArgument(string argument)
        {
            if (argument == null || argument.IndexOf('\0') >= 0) { throw new ArgumentException("Invalid process argument."); }
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char value in argument)
            {
                if (value == '\\') { slashes++; continue; }
                if (value == '"') { quoted.Append('\\', slashes * 2 + 1); }
                else { quoted.Append('\\', slashes); }
                quoted.Append(value);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        internal static int RunChild(string executable, string[] args, string workingDirectory, string launcherExecutable = null)
        {
            // Keep the Windows environment inherited verbatim. Accessing the .NET
            // Framework EnvironmentVariables property can fail for valid inherited
            // blocks containing differently cased duplicates such as Path/PATH.
            // A reserved argument carries routing only to this child process.
            string[] childArguments = args.Where(arg => arg == null || !arg.StartsWith(PcPulse.SensorStartup.LauncherArgumentPrefix, StringComparison.Ordinal)).ToArray();
            if (!String.IsNullOrEmpty(launcherExecutable))
            {
                childArguments = childArguments.Concat(new[] { PcPulse.SensorStartup.LauncherArgumentPrefix + Path.GetFullPath(launcherExecutable) }).ToArray();
            }
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = String.Join(" ", childArguments.Select(QuoteArgument)),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var child = Process.Start(start))
            {
                if (child == null) { throw new IOException("Windows не запустила ADAMO GT."); }
                child.WaitForExit();
                return child.ExitCode;
            }
        }
    }

    internal static class Launcher
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (!Environment.Is64BitOperatingSystem) { throw new PlatformNotSupportedException("Эта версия ADAMO GT предназначена для 64-разрядной Windows."); }
                string workingDirectory = Environment.CurrentDirectory;
                Assembly assembly = Assembly.GetExecutingAssembly();
                int relaunchedExitCode;
                // Ask Windows before entering a user-specific extraction directory.
                // An inherited routing variable must never redirect the outer launcher.
                if (PcPulse.SensorStartup.TryRelaunch(ref args, out relaunchedExitCode, assembly.Location)) { return relaunchedExitCode; }
                string[] childArguments = args.Concat(new[] { "--no-elevation" }).ToArray();
                string cacheBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADAMO GT", "Runtime");
                using (Stream metadata = assembly.GetManifestResourceStream("AdamoGT.Manifest.txt"))
                using (Stream payload = assembly.GetManifestResourceStream("AdamoGT.Payload.zip"))
                {
                    PackageManifest manifest = PackageManifest.Read(metadata);
                    string directory = RuntimePackage.Prepare(payload, manifest, cacheBase);
                    return RuntimePackage.RunChild(Path.Combine(directory, "ADAMO-GT.exe"), childArguments, workingDirectory, assembly.Location);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show("Не удалось запустить ADAMO GT.\n\n" + exception.Message, "ADAMO GT", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
