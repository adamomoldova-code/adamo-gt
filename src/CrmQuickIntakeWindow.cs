using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcPulse
{
    // The desktop collects only the operator's password, quantity and condition.
    // Product mapping, warehouse and the sticker printer are server decisions.
    internal sealed class CrmQuickIntakeWindow : Window
    {
        private readonly CrmQuickIntakeClient client;
        private CrmQuickSubmission submission;
        private readonly PasswordBox password = new PasswordBox { MinHeight = 36, Padding = new Thickness(8), MaxLength = 256 };
        private readonly TextBox quantity = new TextBox { Text = "1", MaxLength = 3, MinHeight = 36, Padding = new Thickness(8) };
        private readonly ComboBox condition = new ComboBox { MinHeight = 36, Padding = new Thickness(8, 5, 8, 5), SelectedIndex = -1 };
        private readonly StackPanel loginPanel = new StackPanel(), selectionPanel = new StackPanel();
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), MinHeight = 24, FontSize = 13 };
        private readonly Button primary = ActionButton("Continuă"), close = ActionButton("Anulează");
        private CancellationTokenSource cancellation;
        private bool signedIn, busy, closed, completed, attempted;
        private int generation;

        private sealed class ConditionChoice
        {
            internal string Code, Label;
            public override string ToString() { return Label; }
        }

        internal CrmQuickIntakeWindow(CrmQuickSubmission prepared, CrmQuickIntakeClient client)
        {
            if (prepared == null || client == null) { throw new ArgumentNullException(prepared == null ? "prepared" : "client"); }
            this.submission = prepared; this.client = client;
            Title = "CRM"; Width = 440; Height = 300; MinWidth = 320; MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            Background = Brush("#FFFFFF"); Foreground = Brush("#202723"); UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            AutomationProperties.SetName(password, "Parola CRM");
            AutomationProperties.SetName(quantity, "Cantitate");
            AutomationProperties.SetName(condition, "Stare");
            password.Background = quantity.Background = condition.Background = Brush("#FFFFFF");
            password.Foreground = quantity.Foreground = condition.Foreground = Foreground;
            password.BorderBrush = quantity.BorderBrush = condition.BorderBrush = Brush("#AEB8B2");
            password.BorderThickness = quantity.BorderThickness = condition.BorderThickness = new Thickness(1);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "Trimite în CRM", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

            loginPanel.Children.Add(Field("Parola", password));
            var choices = new Grid();
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            choices.Children.Add(Field("Cantitate", quantity));
            var conditionField = Field("Stare", condition); Grid.SetColumn(conditionField, 2); choices.Children.Add(conditionField);
            selectionPanel.Children.Add(choices);
            condition.Items.Add(new ConditionChoice { Code = "new", Label = "Nou" });
            condition.Items.Add(new ConditionChoice { Code = "like_new", Label = "Ca nou" });
            condition.Items.Add(new ConditionChoice { Code = "used", Label = "Folosit" });
            var body = new Grid { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(loginPanel); body.Children.Add(selectionPanel);
            // Scrolling is only a fallback for unusually small work areas or large system text.
            var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroller, 1); root.Children.Add(scroller);
            Grid.SetRow(status, 2); root.Children.Add(status);
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            close.Margin = new Thickness(0, 0, 8, 0); close.IsCancel = true;
            primary.IsDefault = true; primary.Background = Brush("#244B3D"); primary.Foreground = Brushes.White; primary.BorderBrush = primary.Background;
            Grid.SetColumn(close, 1); Grid.SetColumn(primary, 2); actions.Children.Add(close); actions.Children.Add(primary);
            Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;

            signedIn = client.IsAuthenticated;
            RestorePending(); UpdateState();
            primary.Click += async delegate { await ContinueAsync(); };
            close.Click += delegate { Close(); };
            Loaded += delegate
            {
                DialogViewport.Position(this, DialogViewport.WorkArea(Owner));
                if (signedIn) { quantity.Focus(); } else { password.Focus(); }
            };
            Closing += delegate
            {
                closed = true; generation++;
                if (cancellation != null) { cancellation.Cancel(); }
                password.Clear();
            };
            Closed += delegate { if (!busy) { client.Dispose(); } };
        }

        private async Task ContinueAsync()
        {
            if (closed || busy || completed) { return; }
            if (!signedIn) { await SignInAsync(); }
            else { await SubmitAsync(); }
        }

        private async Task SignInAsync()
        {
            string secret = password.Password;
            if (String.IsNullOrWhiteSpace(secret)) { status.Text = "Introdu parola."; password.Focus(); return; }
            int request = BeginOperation("Se verifică parola…");
            CancellationToken token = cancellation.Token;
            password.Clear();
            try
            {
                await client.LoginAsync(secret, token);
                if (!Current(request)) { return; }
                signedIn = true; RestorePending();
                status.Text = attempted ? "Continuă trimiterea precedentă." : "";
            }
            catch (OperationCanceledException) { if (Current(request)) { status.Text = "Conectarea a fost anulată."; } }
            catch (Exception error) { if (Current(request)) { status.Text = ErrorText(error, true); } }
            finally { secret = null; password.Clear(); EndOperation(request); }
            if (!closed && signedIn) { quantity.Focus(); }
        }

        private async Task SubmitAsync()
        {
            RestorePending();
            if (!attempted)
            {
                int count;
                if (!Int32.TryParse(quantity.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > 500)
                { status.Text = "Introdu o cantitate între 1 și 500."; quantity.Focus(); return; }
                var chosen = condition.SelectedItem as ConditionChoice;
                if (chosen == null) { status.Text = "Alege starea produsului."; condition.Focus(); return; }
                submission.Quantity = count; submission.Condition = chosen.Code;
            }
            attempted = true;
            int request = BeginOperation("Se trimite în CRM…");
            CancellationToken token = cancellation.Token;
            try
            {
                CrmQuickIntakeResult result = await client.SubmitAsync(client.Pending ?? submission, token);
                if (!Current(request)) { return; }
                if (result != null && result.Completed)
                {
                    completed = true;
                    status.Text = "Produs creat. Stickerele sunt în coada de imprimare.";
                }
                else { status.Text = "Trimiterea nu este completă. Încearcă din nou."; }
            }
            catch (OperationCanceledException) { if (Current(request)) { status.Text = "Trimiterea a fost întreruptă. Încearcă din nou."; } }
            catch (Exception error)
            {
                if (Current(request))
                {
                    var crmError = error as CrmException;
                    if (crmError != null && crmError.StatusCode == 401)
                    { signedIn = false; password.Clear(); status.Text = "Introdu parola din nou pentru a continua."; }
                    else { status.Text = ErrorText(error, false); }
                }
            }
            finally { EndOperation(request); }
            if (!closed && !signedIn) { password.Focus(); }
        }

        private void RestorePending()
        {
            CrmQuickSubmission pending = client.Pending;
            if (pending == null) { return; }
            submission = pending; attempted = true;
            quantity.Text = pending.Quantity.ToString(CultureInfo.InvariantCulture);
            condition.SelectedIndex = -1;
            for (int i = 0; i < condition.Items.Count; i++)
            { if (((ConditionChoice)condition.Items[i]).Code == pending.Condition) { condition.SelectedIndex = i; break; } }
        }

        private int BeginOperation(string message)
        {
            busy = true; cancellation = new CancellationTokenSource(); status.Text = message;
            int request = ++generation; UpdateState(); return request;
        }
        private bool Current(int request) { return !closed && generation == request; }
        private void EndOperation(int request)
        {
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            busy = false;
            if (closed) { client.Dispose(); return; }
            if (generation == request) { UpdateState(); }
        }
        private void UpdateState()
        {
            loginPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            selectionPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            password.IsEnabled = !busy && !completed;
            quantity.IsEnabled = condition.IsEnabled = !busy && !attempted && !completed;
            primary.Content = signedIn ? "Trimite și imprimă" : "Continuă";
            primary.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
            primary.IsEnabled = !busy && !closed && !completed;
            close.Content = completed ? "Închide" : "Anulează";
        }
        private static string ErrorText(Exception error, bool login)
        {
            var crm = error as CrmException;
            int code = crm == null ? 0 : crm.StatusCode;
            if (code == 401) { return "Parola nu este corectă."; }
            if (code == 403) { return "CRM este disponibil doar din oficiu."; }
            if (code == 409) { return "Verifică trimiterea în CRM."; }
            if (code == 404 || code == 503) { return "Conectarea CRM nu este încă disponibilă."; }
            if (code == 429) { return "Prea multe încercări. Încearcă din nou mai târziu."; }
            return login ? "Conectarea nu a reușit. Încearcă din nou." : "Trimiterea nu a reușit. Încearcă din nou.";
        }
        private static FrameworkElement Field(string label, Control control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5), FontWeight = FontWeights.SemiBold });
            panel.Children.Add(control); return panel;
        }
        private static Button ActionButton(string text)
        { return new Button { Content = text, MinHeight = 36, MinWidth = 76, Padding = new Thickness(12, 5, 12, 5), Background = Brush("#F2F4F3"), Foreground = Brush("#202723"), BorderBrush = Brush("#CAD1CC") }; }
        private static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
    }
}
