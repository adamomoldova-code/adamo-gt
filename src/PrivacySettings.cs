using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcPulse
{
    internal sealed class PrivacySettings
    {
        internal const string PolicyVersion = "2026-10-21-v1";
        private readonly string path;
        internal bool HasChoice { get; private set; }
        internal bool AutomaticCrmChecks { get; private set; }

        internal PrivacySettings(string path)
        {
            this.path = path;
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 4096) { return; }
                var value = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as System.Collections.Generic.Dictionary<string, object>;
                object version, enabled;
                if (value == null || !value.TryGetValue("policy_version", out version) || !(version is string)
                    || (string)version != PolicyVersion || !value.TryGetValue("automatic_crm_checks", out enabled) || !(enabled is bool)) { return; }
                AutomaticCrmChecks = (bool)enabled; HasChoice = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        internal static PrivacySettings OpenDefault()
        {
            return new PrivacySettings(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADAMO GT", "privacy.json"));
        }

        internal void Save(bool enabled)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "privacy." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string json = new JavaScriptSerializer().Serialize(new { policy_version = PolicyVersion, automatic_crm_checks = enabled });
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) { File.Replace(temporary, path, null); } else { File.Move(temporary, path); }
                AutomaticCrmChecks = enabled; HasChoice = true;
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }

        internal static string ReadPolicy()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PcPulse.Privacy.md"))
            {
                if (stream == null) { throw new InvalidOperationException("Politica de confidențialitate lipsește din aplicație."); }
                using (var reader = new StreamReader(stream, Encoding.UTF8)) { return reader.ReadToEnd(); }
            }
        }
    }

    internal sealed class PrivacySettingsWindow : Window
    {
        internal readonly CheckBox CrmChoice;
        internal readonly Button SaveButton;
        private readonly TextBlock status;

        internal PrivacySettingsWindow(PrivacySettings settings)
        {
            Title = "Confidențialitate — ADAMO GT"; Width = 650; Height = 560; MinWidth = 340; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = Brushes.White; Foreground = new SolidColorBrush(Color.FromRgb(29, 29, 31));
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14; UseLayoutRounding = true;
            var root = new DockPanel { Margin = new Thickness(20) };
            System.Windows.Documents.TextElement.SetFontFamily(root, FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(root, FontSize);
            var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(151, 48, 35)), Margin = new Thickness(0, 8, 0, 8) };
            bottom.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Închide", IsCancel = true, MinHeight = 36, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            SaveButton = new Button { Content = "Salvează și continuă", IsDefault = true, MinWidth = 168, MinHeight = 36, Padding = new Thickness(14, 5, 14, 5) };
            actions.Children.Add(cancel); actions.Children.Add(SaveButton); bottom.Children.Add(actions); root.Children.Add(bottom);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "Datele și conexiunile aplicației", FontWeight = FontWeights.SemiBold, FontSize = 20, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Margin = new Thickness(0, 12, 0, 14), TextWrapping = TextWrapping.Wrap,
                Text = "Specificațiile și testele rulează pe acest calculator. Imaginile camerei, sunetul și tastele testate nu sunt încărcate pe internet. Căutarea online a porturilor și descărcarea driverului PawnIO pornesc numai la cererea ta.\n\nCRM primește specificațiile și captura primei pagini numai când apeși «Trimite și imprimă»." });
            CrmChoice = new CheckBox { Content = new TextBlock { Text = "Permite verificarea automată a accesului CRM", TextWrapping = TextWrapping.Wrap }, IsChecked = settings.AutomaticCrmChecks, Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetName(CrmChoice, "Verificarea automată CRM");
            content.Children.Add(CrmChoice);
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16),
                Text = "Opțional: aplicația contactează crm.adamo.md la pornire și periodic pentru a verifica accesul din oficiu. Serviciul vede adresa IP, fără raport hardware sau parolă. Dacă opțiunea este oprită, butonul CRM rămâne ascuns. Poți schimba alegerea din «Датчики» → «Confidențialitate»." });
            content.Children.Add(new TextBlock { Text = "Politica integrală de confidențialitate și ștergere a datelor", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            content.Children.Add(new TextBox { Text = PrivacySettings.ReadPolicy(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 200, Padding = new Thickness(8), FontSize = 12 });
            root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            cancel.Click += delegate { Close(); };
            SaveButton.Click += delegate
            {
                try { settings.Save(CrmChoice.IsChecked == true); Close(); }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException)) { throw; }
                    status.Text = "Alegerea nu s-a putut salva. Închide fereastra pentru a continua cu setările precedente.";
                }
            };
            Loaded += delegate { DialogViewport.Position(this, DialogViewport.WorkArea(Owner)); };
        }
    }
}
