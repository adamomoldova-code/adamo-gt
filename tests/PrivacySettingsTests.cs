using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcPulse;

namespace PcPulseTests
{
    internal static class PrivacySettingsTests
    {
        internal static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ADAMO-PrivacyTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "privacy.json");
                var settings = new PrivacySettings(path);
                Require(!settings.HasChoice && !settings.AutomaticCrmChecks, "Fresh or absent preferences never authorize network access");
                settings.Save(false);
                settings = new PrivacySettings(path);
                Require(settings.HasChoice && !settings.AutomaticCrmChecks, "Choosing local use is remembered without enabling CRM");
                settings.Save(true);
                settings = new PrivacySettings(path);
                Require(settings.HasChoice && settings.AutomaticCrmChecks, "An explicit choice can enable the anonymous office check");
                string valid = File.ReadAllText(path);
                File.WriteAllText(path, valid.Replace(PrivacySettings.PolicyVersion, "obsolete-policy"));
                Require(!new PrivacySettings(path).AutomaticCrmChecks && !new PrivacySettings(path).HasChoice, "A changed policy needs a fresh choice");
                foreach (string invalid in new[] { "{", "null", "{}", "[]", valid.Replace("true", "\"true\""), new string('x', 4097) })
                {
                    File.WriteAllText(path, invalid);
                    var denied = new PrivacySettings(path);
                    Require(!denied.HasChoice && !denied.AutomaticCrmChecks, "Corrupt, oversized or mistyped settings fail closed");
                }
                Require(PrivacySettings.ReadPolicy().Contains("crm.adamo.md") && PrivacySettings.ReadPolicy().Contains("Removal"), "The full embedded policy includes CRM disclosure and removal instructions");
            }
            finally { Directory.Delete(directory, true); }
        }

        internal static void RunUi()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ADAMO-PrivacyUiTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "privacy.json");
                var settings = new PrivacySettings(path);
                VerifyLayout(settings, 650, 520);
                VerifyLayout(settings, 324, 300);
                var first = new PrivacySettingsWindow(settings);
                Require(first.CrmChoice.IsChecked == false, "CRM is unchecked on first use");
                first.CrmChoice.IsChecked = true; first.Close();
                Require(!File.Exists(path) && !settings.HasChoice, "Closing the policy without saving does not grant permission");
                var accepted = new PrivacySettingsWindow(settings);
                accepted.CrmChoice.IsChecked = true;
                accepted.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(settings.HasChoice && new PrivacySettings(path).AutomaticCrmChecks, "The actual save action persists explicit opt-in");
                var revoked = new PrivacySettingsWindow(settings);
                revoked.CrmChoice.IsChecked = false;
                revoked.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(!settings.AutomaticCrmChecks && !new PrivacySettings(path).AutomaticCrmChecks, "The same dialog can revoke the preference");
                string invalidTarget = Path.Combine(directory, "directory-not-file"); Directory.CreateDirectory(invalidTarget);
                var failing = new PrivacySettings(invalidTarget);
                var errorDialog = new PrivacySettingsWindow(failing); errorDialog.CrmChoice.IsChecked = true;
                errorDialog.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(!failing.HasChoice && !failing.AutomaticCrmChecks, "A storage failure never grants consent");
                errorDialog.Close();
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void VerifyLayout(PrivacySettings settings, int width, int height)
        {
            var dialog = new PrivacySettingsWindow(settings);
            var content = (FrameworkElement)dialog.Content; dialog.Content = null;
            var host = new Grid { Width = width, Height = height, Background = Brushes.White };
            host.Children.Add(content); host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            Rect save = dialog.SaveButton.TransformToAncestor(host).TransformBounds(new Rect(dialog.SaveButton.RenderSize));
            Require(save.Width >= 150 && save.Height >= 36 && save.Left >= 0 && save.Top >= 0 && save.Right <= width + 1 && save.Bottom <= height + 1,
                "The save action remains visible and reachable in client area " + width + "x" + height + "; actual bounds " + save);
            string folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "privacy-272"));
            Directory.CreateDirectory(folder);
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(folder, "privacy-" + width + "x" + height + ".png"))) { encoder.Save(output); }
            dialog.Close();
        }

        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
