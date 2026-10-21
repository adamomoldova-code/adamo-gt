using System;
using System.IO;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class SensorSetupTests
    {
        public static void Run()
        {
            OnlyPinnedHttpsReleaseAndItsDeliveryHostAreAllowed();
            DownloadIsBoundedAndRejectsTruncation();
            SameLengthTamperingIsRejected();
            CancellationPreventsAnyDownloadOrInstaller();
            InstallerExitAloneCannotClaimSuccess();
        }

        private static void OnlyPinnedHttpsReleaseAndItsDeliveryHostAreAllowed()
        {
            Require(SensorSetup.IsAllowedDownloadUri(new Uri(SensorSetup.InstallerUrl)), "pinned official release accepted");
            Require(SensorSetup.IsAllowedDownloadUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/123/abc?token=value")), "GitHub delivery host accepted");
            foreach (string uri in new[]
            {
                "http://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe",
                "https://github.com/attacker/PawnIO/releases/download/2.2.0/PawnIO_setup.exe",
                "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe",
                "https://release-assets.githubusercontent.com.evil.example/github-production-release-asset/test",
                "https://release-assets.githubusercontent.com:444/github-production-release-asset/test",
                "https://attacker@release-assets.githubusercontent.com/github-production-release-asset/test",
                "https://release-assets.githubusercontent.com/other/test",
                "file:///C:/Windows/system32/cmd.exe"
            })
            {
                Require(!SensorSetup.IsAllowedDownloadUri(new Uri(uri)), "untrusted or mutable address rejected: " + uri);
            }
            Require(!SensorSetup.IsAllowedDownloadUri(new Uri("relative", UriKind.Relative)), "relative address rejected");
        }

        private static void DownloadIsBoundedAndRejectsTruncation()
        {
            foreach (int length in new[] { 0, 17, (int)SensorSetup.InstallerLength - 1, (int)SensorSetup.InstallerLength + 1 })
            {
                using (var input = new MemoryStream(new byte[length]))
                using (var output = new MemoryStream())
                {
                    Throws<InvalidDataException>(delegate { SensorSetup.CopyDownload(input, output, CancellationToken.None); }, "incorrect transfer length rejected");
                    Require(output.Length <= SensorSetup.InstallerLength, "oversize response never written beyond cap");
                }
            }
            using (var input = new MemoryStream(new byte[SensorSetup.InstallerLength]))
            using (var output = new MemoryStream())
            {
                SensorSetup.CopyDownload(input, output, CancellationToken.None);
                Require(output.Length == SensorSetup.InstallerLength, "complete file copied before cryptographic verification");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var input = new MemoryStream(new byte[32]))
            using (var output = new MemoryStream())
            {
                cancellation.Cancel();
                Throws<OperationCanceledException>(delegate { SensorSetup.CopyDownload(input, output, cancellation.Token); }, "cancelled transfer rejected before read");
                Require(input.Position == 0 && output.Length == 0, "cancelled copy performs no I/O");
            }
        }

        private static void SameLengthTamperingIsRejected()
        {
            using (var input = new MemoryStream(new byte[SensorSetup.InstallerLength]))
            {
                Throws<InvalidDataException>(delegate { SensorSetup.VerifyPinnedContent(input); }, "same-length replacement cannot pass the SHA256 pin");
            }
        }

        private static void CancellationPreventsAnyDownloadOrInstaller()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                int progress = 0;
                SensorSetupResult result = SensorSetup.InstallAsync(delegate { progress++; }, cancellation.Token).GetAwaiter().GetResult();
                Require(result.State == SensorSetupState.Cancelled && progress == 0, "pre-cancelled setup exits before registry, network and UAC");
            }
        }

        private static void InstallerExitAloneCannotClaimSuccess()
        {
            Require(SensorSetup.ResultAfterExit(0, null).State == SensorSetupState.Failed, "GUI closed without installed version is not success");
            Require(SensorSetup.ResultAfterExit(0, new Version(2, 1, 0)).State == SensorSetupState.Failed, "unchanged older version is not success");
            Require(SensorSetup.ResultAfterExit(3010, null).State == SensorSetupState.Failed, "reboot exit without installation evidence is not success");
            Require(SensorSetup.ResultAfterExit(0, new Version(2, 2, 0)).State == SensorSetupState.Installed, "matching fresh registry and successful exit confirm install");
            Require(SensorSetup.ResultAfterExit(3010, new Version(2, 2, 0)).State == SensorSetupState.RebootRequired, "reboot explicitly reported");
            Require(SensorSetup.ResultAfterExit(1602, new Version(2, 2, 0)).State == SensorSetupState.Cancelled, "user cancellation takes precedence over registry");
            Require(SensorSetup.ResultAfterExit(5, new Version(2, 2, 0)).State == SensorSetupState.Failed, "installer error cannot become success");
        }

        private static void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
