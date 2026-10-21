using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PcPulse
{
    internal enum SensorSetupState { Installed, AlreadyInstalled, RebootRequired, InstallerRunning, Cancelled, Failed }

    internal sealed class SensorSetupResult
    {
        internal SensorSetupState State;
        internal string Message;
    }

    internal static class SensorSetup
    {
        internal const string OfficialWebsiteUrl = "https://pawnio.eu/";
        internal const string InstallerUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        internal const string InstallerSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
        internal const string SignerThumbprint = "F380DCC9F706E2756A5047B832FFE719E1BC35F5";
        internal const long InstallerLength = 3410960;
        internal static readonly Version InstallerVersion = new Version(2, 2, 0);
        private static int installationActive;

        internal static Task<SensorSetupResult> InstallAsync(Action<string> reportProgress, CancellationToken cancellation)
        {
            return Task.Run(delegate { return Install(reportProgress, cancellation); });
        }

        private static SensorSetupResult Install(Action<string> reportProgress, CancellationToken cancellation)
        {
            if (Interlocked.CompareExchange(ref installationActive, 1, 0) != 0)
            {
                return Result(SensorSetupState.InstallerRunning, "Установщик PawnIO уже открыт. Завершите установку в его окне.");
            }
            string directory = null;
            FileStream lockedInstaller = null;
            Process installerProcess = null;
            bool cleanupTransferred = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Version installed = ReadInstalledVersion();
                if (installed != null && installed >= InstallerVersion)
                {
                    return Result(SensorSetupState.AlreadyInstalled, "PawnIO " + installed + " уже установлен. Перезапустите ADAMO GT от имени администратора для чтения датчиков.");
                }
                Report(reportProgress, "Загрузка официального PawnIO 2.2.0 (3,3 MB)…");
                directory = Path.Combine(Path.GetTempPath(), "PCPulse-PawnIO-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "PawnIO_setup.exe");
                Download(path, cancellation);
                cancellation.ThrowIfCancellationRequested();
                Report(reportProgress, "Проверка целостности файла и цифровой подписи Windows…");
                lockedInstaller = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                VerifyInstaller(lockedInstaller, path);
                cancellation.ThrowIfCancellationRequested();
                Report(reportProgress, "Подтвердите запрос Windows и установите обычную подписанную версию PawnIO в открывшемся окне.");
                installerProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = directory,
                    Arguments = "",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                });
                if (installerProcess == null) { throw new IOException("Windows не вернула процесс установщика."); }
                var waiting = Stopwatch.StartNew();
                while (!installerProcess.WaitForExit(200))
                {
                    if (cancellation.IsCancellationRequested || waiting.Elapsed > TimeSpan.FromMinutes(15))
                    {
                        Process continuingProcess = installerProcess;
                        FileStream continuingLock = lockedInstaller;
                        string continuingDirectory = directory;
                        Task.Run(delegate
                        {
                            try { continuingProcess.WaitForExit(); }
                            finally
                            {
                                continuingProcess.Dispose();
                                continuingLock.Dispose();
                                CleanDownload(continuingDirectory);
                                Interlocked.Exchange(ref installationActive, 0);
                            }
                        });
                        cleanupTransferred = true;
                        return Result(SensorSetupState.InstallerRunning, "Установщик PawnIO остаётся открытым. Завершите или отмените установку в его окне, затем перезапустите ADAMO GT.");
                    }
                }
                return ResultAfterExit(installerProcess.ExitCode, ReadInstalledVersion());
            }
            catch (OperationCanceledException)
            {
                return Result(SensorSetupState.Cancelled, "Загрузка или проверка отменена. Установщик не запускался.");
            }
            catch (Win32Exception exception)
            {
                return exception.NativeErrorCode == 1223
                    ? Result(SensorSetupState.Cancelled, "Запрос администратора отменён. PawnIO не устанавливался.")
                    : Result(SensorSetupState.Failed, "Не удалось открыть установщик PawnIO: " + exception.Message);
            }
            catch (Exception exception)
            {
                return Result(SensorSetupState.Failed, "Установка PawnIO не выполнена: " + exception.Message + " Можно открыть официальный сайт и установить драйвер самостоятельно.");
            }
            finally
            {
                if (!cleanupTransferred)
                {
                    if (installerProcess != null) { installerProcess.Dispose(); }
                    if (lockedInstaller != null) { lockedInstaller.Dispose(); }
                    CleanDownload(directory);
                    Interlocked.Exchange(ref installationActive, 0);
                }
            }
        }

        internal static Version ReadInstalledVersion()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                {
                    Version version;
                    if (key != null && Version.TryParse(key.GetValue("DisplayVersion") as string, out version)) { return version; }
                }
            }
            return null;
        }

        internal static SensorSetupResult ResultAfterExit(int exitCode, Version installedVersion)
        {
            if (exitCode == 1223 || exitCode == 1602)
            {
                return Result(SensorSetupState.Cancelled, "Установка отменена в окне PawnIO.");
            }
            if (exitCode != 0 && exitCode != 3010 && exitCode != 1641)
            {
                return Result(SensorSetupState.Failed, "Установщик PawnIO завершился с кодом " + exitCode + ". Проверьте сообщение в его окне.");
            }
            if (installedVersion == null || installedVersion < InstallerVersion)
            {
                return Result(SensorSetupState.Failed, "Установщик закрыт, но установка PawnIO 2.2.0 не подтверждена. Проверьте результат в установщике или на официальном сайте.");
            }
            if (exitCode == 3010 || exitCode == 1641)
            {
                return Result(SensorSetupState.RebootRequired, "PawnIO установлен. Установщик сообщает о необходимости перезагрузки Windows. После неё запустите ADAMO GT от имени администратора.");
            }
            return Result(SensorSetupState.Installed, "PawnIO установлен. Перезапустите ADAMO GT от имени администратора для чтения датчиков. Если установщик просил перезагрузку Windows, выполните её сначала.");
        }

        private static void Download(string path, CancellationToken cancellation)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    Uri uri = new Uri(InstallerUrl);
                    for (int redirect = 0; redirect <= 3; redirect++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!IsAllowedDownloadUri(uri)) { throw new InvalidDataException("Сервер перенаправил загрузку на неподтверждённый адрес."); }
                        var request = (HttpWebRequest)WebRequest.Create(uri);
                        request.AllowAutoRedirect = false;
                        request.Timeout = 15000;
                        request.ReadWriteTimeout = 15000;
                        request.UserAgent = "ADAMO-GT-SensorSetup/2.2";
                        request.AutomaticDecompression = DecompressionMethods.None;
                        using (deadline.Token.Register(delegate { request.Abort(); }))
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                            {
                                Uri redirected;
                                if (!Uri.TryCreate(uri, response.Headers[HttpResponseHeader.Location], out redirected))
                                {
                                    throw new InvalidDataException("Сервер вернул некорректный адрес загрузки.");
                                }
                                uri = redirected;
                                continue;
                            }
                            if (response.StatusCode != HttpStatusCode.OK) { throw new IOException("Сервер не вернул файл установщика."); }
                            if (response.ContentLength != -1 && response.ContentLength != InstallerLength)
                            {
                                throw new InvalidDataException("Размер установщика отличается от проверенной версии.");
                            }
                            using (Stream input = response.GetResponseStream())
                            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                CopyDownload(input, output, deadline.Token);
                            }
                            return;
                        }
                    }
                    throw new IOException("Слишком много перенаправлений при загрузке PawnIO.");
                }
                catch (Exception exception)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) { throw new TimeoutException("Время загрузки PawnIO истекло.", exception); }
                    throw;
                }
            }
        }

        internal static bool IsAllowedDownloadUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) { return false; }
            if (string.Equals(uri.AbsoluteUri, InstallerUrl, StringComparison.Ordinal)) { return true; }
            return string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal);
        }

        internal static void CopyDownload(Stream input, Stream output, CancellationToken cancellation)
        {
            byte[] buffer = new byte[32768];
            long total = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) { break; }
                total += read;
                if (total > InstallerLength) { throw new InvalidDataException("Загрузка превышает размер проверенного установщика."); }
                output.Write(buffer, 0, read);
            }
            if (total != InstallerLength) { throw new InvalidDataException("Установщик загружен не полностью."); }
        }

        internal static void VerifyInstaller(FileStream file, string path)
        {
            VerifyPinnedContent(file);
            int trust = VerifyWindowsTrust(file, path);
            if (trust != 0) { throw new InvalidDataException("Windows не подтвердила цифровую подпись установщика (0x" + unchecked((uint)trust).ToString("X8") + ")."); }
            using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
            {
                if (!string.Equals(certificate.Thumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Подписант установщика не совпадает с проверенным издателем namazso.");
                }
            }
        }

        internal static void VerifyPinnedContent(Stream file)
        {
            if (file.Length != InstallerLength) { throw new InvalidDataException("Размер установщика отличается от проверенной версии."); }
            file.Position = 0;
            using (SHA256 hash = SHA256.Create())
            {
                string value = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
                if (!string.Equals(value, InstallerSha256, StringComparison.Ordinal)) { throw new InvalidDataException("Контрольная сумма установщика не совпадает с официальной версией."); }
            }
            file.Position = 0;
        }

        private static int VerifyWindowsTrust(FileStream file, string path)
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                FilePath = path,
                FileHandle = file.SafeFileHandle.DangerousGetHandle()
            };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            Marshal.StructureToPtr(fileInfo, pointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = pointer,
                StateAction = 1,
                ProviderFlags = 0x80 | 0x2000,
                UiContext = 1
            };
            Guid policy = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try { return WinVerifyTrust(new IntPtr(-1), ref policy, ref data); }
            finally
            {
                data.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
                Marshal.DestroyStructure(pointer, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pointer);
                GC.KeepAlive(file);
            }
        }

        private static SensorSetupResult Result(SensorSetupState state, string message)
        {
            return new SensorSetupResult { State = state, Message = message };
        }

        private static void Report(Action<string> report, string message)
        {
            if (report != null) { report(message); }
        }

        private static void CleanDownload(string directory)
        {
            if (string.IsNullOrEmpty(directory)) { return; }
            try
            {
                File.Delete(Path.Combine(directory, "PawnIO_setup.exe"));
                Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            internal uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] internal string FilePath;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            internal uint Size;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr FileInfo;
            internal uint StateAction;
            internal IntPtr StateData;
            internal IntPtr UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
            internal IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    }
}
