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
    // The desktop collects only quantity and condition; the server enforces office access.
    // Product mapping, warehouse and the sticker printer are server decisions.
    internal sealed class CrmQuickIntakeWindow : Window
    {
        private readonly CrmQuickIntakeClient client;
        private CrmQuickSubmission submission;
        private readonly TextBox quantity = new TextBox { Text = "1", MaxLength = 3, MinHeight = 36, Padding = new Thickness(8) };
        private readonly ComboBox condition = new ComboBox { MinHeight = 36, Padding = new Thickness(8, 5, 8, 5), SelectedIndex = -1 };
        private readonly ListBox association = new ListBox { MinHeight = 72, MaxHeight = 200, Padding = new Thickness(2), SelectedIndex = -1,
            Background = Brushes.White, BorderBrush = Brush("#AEB8B2"), BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private FrameworkElement associationField;
        private readonly StackPanel selectionPanel = new StackPanel();
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), MinHeight = 24, FontSize = 13 };
        private readonly Button primary = ActionButton("Continuă"), close = ActionButton("Anulează");
        private CancellationTokenSource cancellation;
        private bool busy, closed, completed, attempted;
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
            AutomationProperties.SetName(quantity, "Cantitate");
            AutomationProperties.SetName(condition, "Stare");
            quantity.Background = condition.Background = Brush("#FFFFFF");
            quantity.Foreground = condition.Foreground = Foreground;
            quantity.BorderBrush = condition.BorderBrush = Brush("#AEB8B2");
            quantity.BorderThickness = condition.BorderThickness = new Thickness(1);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "Trimite în CRM", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

            var choices = new Grid();
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            choices.Children.Add(Field("Cantitate", quantity));
            var conditionField = Field("Stare", condition); Grid.SetColumn(conditionField, 2); choices.Children.Add(conditionField);
            selectionPanel.Children.Add(choices);
            associationField = Field("Produs existent sau produs nou", association);
            associationField.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(association, "Asociere produs CRM");
            ScrollViewer.SetHorizontalScrollBarVisibility(association, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(association, ScrollBarVisibility.Auto);
            association.SelectionChanged += delegate { UpdateState(); };
            condition.Items.Add(new ConditionChoice { Code = "new", Label = "Nou" });
            condition.Items.Add(new ConditionChoice { Code = "like_new", Label = "Ca nou" });
            condition.Items.Add(new ConditionChoice { Code = "used", Label = "Folosit" });
            var body = new Grid { VerticalAlignment = VerticalAlignment.Top };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(selectionPanel);
            Grid.SetRow(associationField, 1); body.Children.Add(associationField);
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

            RestorePending(); UpdateState();
            primary.Click += async delegate { await ContinueAsync(); };
            close.Click += delegate { Close(); };
            Loaded += delegate
            {
                DialogViewport.Position(this, DialogViewport.WorkArea(Owner));
                quantity.Focus();
            };
            Closing += delegate
            {
                if (!busy && !completed && associationField.Visibility == Visibility.Visible)
                { try { client.CancelUncommittedAssociation(); } catch { } }
                closed = true; generation++;
                if (cancellation != null) { cancellation.Cancel(); }
            };
            Closed += delegate { if (!busy) { client.Dispose(); } };
        }

        private async Task ContinueAsync()
        {
            if (closed || busy || completed) { return; }
            await SubmitAsync();
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
            if (associationField.Visibility == Visibility.Visible)
            {
                var chosenItem = association.SelectedItem as ListBoxItem;
                var chosenProduct = chosenItem == null ? null : chosenItem.Tag as CrmQuickAssociationChoice;
                if (chosenProduct == null || String.IsNullOrEmpty(chosenProduct.Token))
                { status.Text = "Alege un produs compatibil sau «Creează produs nou»."; return; }
                try { client.SelectAssociation(chosenProduct.Token); }
                catch (Exception error) { status.Text = error.Message; return; }
                associationField.Visibility = Visibility.Collapsed;
            }
            int request = BeginOperation("Se trimite în CRM…");
            CancellationToken token = cancellation.Token;
            try
            {
                if (!client.IsAuthenticated) { await client.LoginAsync(token); }
                if (!Current(request)) { return; }
                CrmQuickIntakeResult result = await client.SubmitAsync(client.Pending ?? submission, token);
                if (!Current(request)) { return; }
                if (result != null && result.Completed)
                {
                    completed = true;
                    status.Text = (result.ProductCreated ? "Produs creat." : "Unități asociate produsului existent.") + " Stickerele sunt în coada de imprimare.";
                }
                else { status.Text = "Trimiterea nu este completă. Încearcă din nou."; }
            }
            catch (CrmQuickAssociationException choice)
            {
                if (Current(request))
                {
                    association.Items.Clear();
                    foreach (var option in choice.Choices)
                    {
                        bool eligible = !String.IsNullOrEmpty(option.Token);
                        var text = new StackPanel();
                        text.Children.Add(new TextBlock { Text = option.Name, TextWrapping = TextWrapping.Wrap, MaxHeight = 48,
                            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = eligible ? Foreground : Brush("#59615D") });
                        if (!String.IsNullOrEmpty(option.Reason))
                        { text.Children.Add(new TextBlock { Text = option.Reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#59615D"), Margin = new Thickness(0, 2, 0, 0) }); }
                        association.Items.Add(new ListBoxItem {
                            Content = text,
                            Tag = option, ToolTip = option.ToString(), IsEnabled = eligible,
                            Padding = new Thickness(8, 6, 8, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch
                        });
                    }
                    association.SelectedIndex = -1;
                    associationField.Visibility = Visibility.Visible;
                    Height = Math.Max(Height, 520);
                    status.Text = "CRM a găsit produse asemănătoare. Confirmă asocierea; variantele cu diferențe nu pot fi alese.";
                    if (IsLoaded) { DialogViewport.Fit(this, DialogViewport.WorkArea(Owner)); }
                    associationField.InvalidateMeasure(); associationField.InvalidateArrange();
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(delegate {
                        if (closed || completed || associationField.Visibility != Visibility.Visible) { return; }
                        associationField.UpdateLayout(); associationField.BringIntoView(); association.Focus();
                    }));
                }
            }
            catch (OperationCanceledException) { if (Current(request)) { status.Text = "Trimiterea a fost întreruptă. Încearcă din nou."; } }
            catch (Exception error)
            {
                if (Current(request))
                {
                    var crmError = error as CrmException;
                    if (crmError != null && crmError.StatusCode == 401)
                    { status.Text = "Sesiunea a expirat. Încearcă din nou pentru a continua."; }
                    else { status.Text = ErrorText(error, false); }
                }
            }
            finally { EndOperation(request); }
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
            selectionPanel.Visibility = Visibility.Visible;
            association.IsEnabled = !busy && !completed;
            quantity.IsEnabled = condition.IsEnabled = !busy && !attempted && !completed;
            primary.Content = "Trimite și imprimă";
            primary.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
            var chosenItem = association.SelectedItem as ListBoxItem;
            var chosen = chosenItem == null ? null : chosenItem.Tag as CrmQuickAssociationChoice;
            primary.IsEnabled = !busy && !closed && !completed && (associationField.Visibility != Visibility.Visible
                || (chosenItem != null && chosenItem.IsEnabled && chosen != null && !String.IsNullOrEmpty(chosen.Token)));
            close.Content = completed ? "Închide" : "Anulează";
        }
        private static string ErrorText(Exception error, bool login)
        {
            var crm = error as CrmException;
            int code = crm == null ? 0 : crm.StatusCode;
            if (code == 401) { return "Sesiunea a expirat. Încearcă din nou."; }
            if (code == 403) { return "CRM este disponibil doar din oficiu."; }
            if (code == 409) { return "Verifică trimiterea în CRM."; }
            if (code == 404 || code == 503) { return "Conectarea CRM nu este încă disponibilă."; }
            if (code == 426) { return "Versiunile ADAMO GT și CRM nu sunt compatibile. Actualizează integrarea."; }
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
