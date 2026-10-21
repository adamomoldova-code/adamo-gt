using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcPulse
{
    internal sealed class CrmIntakeWindow : Window
    {
        private readonly CrmProductDraft draft;
        private readonly CrmIntakeClient client;
        private readonly TextBox email = Entry("Логин CRM");
        private readonly PasswordBox password = new PasswordBox { MinHeight = 32, Padding = new Thickness(6), Margin = new Thickness(0, 3, 8, 4) };
        private readonly Button login = Button("Войти"), send = Button("Создать в CRM"), preview = Button("Предпросмотр фото"), open = Button("Открыть в CRM");
        private readonly TextBox name = Entry("Название товара"), price = Entry("Цена продажи, MDL"), quantity = Entry("Количество"), notes = Entry("Комплект, дефекты и гарантия");
        private readonly ComboBox warehouse = Choice("Склад"), condition = Choice("Состояние"), printer = Choice("Принтер наклеек");
        private readonly CheckBox reviewed = Check("Комплектация проверена: выбранные компоненты входят в товар"), print = Check("Напечатать существующую наклейку CRM с AD-кодом");
        private readonly TextBlock status = Text("Войдите в CRM, проверьте комплектацию и создайте товар для подготовки к публикации."), account = Text(""), printReason = Text("");
        private readonly TextBox summary = Entry("Характеристики, которые будут отправлены");
        private readonly StackPanel loginPanel = new StackPanel(), components = new StackPanel();
        private readonly List<Control> editors = new List<Control>();
        private CrmContext context;
        private CrmIntakeResult result;
        private CancellationTokenSource cancellation;
        private bool busy, closed, completed, updating;
        private int generation;
        private string reviewedImageFingerprint;

        private sealed class ConditionChoice
        {
            internal string Code, Label;
            public override string ToString() { return Label; }
        }

        internal CrmIntakeWindow(CrmProductDraft draft, CrmIntakeClient client)
        {
            if (draft == null || client == null) { throw new ArgumentNullException(draft == null ? "draft" : "client"); }
            this.draft = draft; this.client = client;
            Title = "ADAMO GT — товар в CRM"; Width = 850; Height = 780; MinWidth = 500; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            Background = Brush("#F3F4F5"); Foreground = Brush("#1D1D1F"); UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            AutomationProperties.SetName(password, "Пароль CRM");
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            heading.Children.Add(new TextBlock { Text = "Подготовить товар в CRM", FontSize = 22, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(Text("Характеристики и фото-лист попадут в карточку товара. Публикацию выполняйте отдельно в CRM."));
            root.Children.Add(heading);

            var content = new StackPanel();
            loginPanel.Children.Add(Text("Вход сотрудника CRM"));
            var credentials = new Grid(); credentials.ColumnDefinitions.Add(new ColumnDefinition()); credentials.ColumnDefinitions.Add(new ColumnDefinition()); credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            credentials.Children.Add(Field("Логин CRM", email));
            var passwordField = Field("Пароль", password); Grid.SetColumn(passwordField, 1); credentials.Children.Add(passwordField);
            Grid.SetColumn(login, 2); login.VerticalAlignment = VerticalAlignment.Bottom; credentials.Children.Add(login);
            loginPanel.Children.Add(credentials); content.Children.Add(loginPanel); account.Visibility = Visibility.Collapsed; content.Children.Add(account);

            name.Text = draft.Title; name.MaxLength = 200; content.Children.Add(Field("Название товара", name));
            condition.Items.Add(new ConditionChoice { Code = "new", Label = "Новое" });
            condition.Items.Add(new ConditionChoice { Code = "like_new", Label = "Как новое" });
            condition.Items.Add(new ConditionChoice { Code = "used", Label = "Б/у" });
            condition.SelectedIndex = -1;
            content.Children.Add(TwoColumns(Field("Склад", warehouse), Field("Состояние — выберите вручную", condition)));
            quantity.Text = "1"; quantity.MaxLength = 3; price.MaxLength = 16;
            content.Children.Add(TwoColumns(Field("Количество (1–500)", quantity), Field("Цена продажи, MDL (можно оставить пустой)", price)));
            content.Children.Add(Text("При количестве больше 1 одинаковая конфигурация применяется ко всем единицам. Диагностика и фото-лист относятся только к этому компьютеру."));
            components.Children.Add(new TextBlock { Text = "Комплект продаваемого компьютера", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            components.Children.Add(Text("Отметьте накопители, которые входят в товар. Подключённые USB-диски не добавляются автоматически. Если продаётся компьютер без накопителя, оставьте список пустым."));
            foreach (CrmDiskDraft disk in draft.Disks)
            {
                CrmDiskDraft selected = disk;
                var check = Check("Диск: " + disk.Caption); check.IsChecked = disk.Include;
                check.Checked += delegate { selected.Include = true; SelectionChanged(); };
                check.Unchecked += delegate { selected.Include = false; SelectionChanged(); };
                components.Children.Add(check); editors.Add(check);
            }
            foreach (CrmGpuDraft gpu in draft.Gpus)
            {
                CrmGpuDraft selected = gpu;
                var check = Check("GPU: " + gpu.Caption); check.IsChecked = gpu.Include;
                check.Checked += delegate { selected.Include = true; SelectionChanged(); };
                check.Unchecked += delegate { selected.Include = false; SelectionChanged(); };
                components.Children.Add(check); editors.Add(check);
            }
            if (draft.Disks.Count == 0) { components.Children.Add(Text("Накопители не определены. Допишите комплектацию вручную при необходимости.")); }
            content.Children.Add(components); content.Children.Add(reviewed);
            summary.IsReadOnly = true; summary.AcceptsReturn = true; summary.TextWrapping = TextWrapping.Wrap;
            summary.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; summary.Height = 150;
            summary.Background = Brush("#F8F9FA"); summary.Text = draft.BuildDescription();
            content.Children.Add(Field("Характеристики — будут отправлены в CRM", summary));
            notes.AcceptsReturn = true; notes.TextWrapping = TextWrapping.Wrap; notes.Height = 72; notes.MaxLength = 3000;
            notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            content.Children.Add(Field("Дополните комплект, дефекты, раскладку клавиатуры и гарантию", notes));
            content.Children.Add(preview);
            content.Children.Add(Text("Перед отправкой откройте предпросмотр фото. Это лист с выбранными характеристиками и логотипом ADAMO; рабочий стол, пароль и другие окна в изображение не попадают."));
            content.Children.Add(print); content.Children.Add(printer); content.Children.Add(printReason);
            var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroller, 1); root.Children.Add(scroller);

            var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            status.Margin = new Thickness(0, 0, 0, 7); status.MaxHeight = 105; footer.Children.Add(status);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var close = Button("Закрыть"); close.Click += delegate { Close(); };
            actions.Children.Add(open); actions.Children.Add(close); actions.Children.Add(send); footer.Children.Add(actions);
            Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;

            editors.AddRange(new Control[] { name, price, quantity, notes, warehouse, condition, reviewed });
            login.Click += async delegate { await SignIn(); };
            send.Click += async delegate { await Submit(); };
            preview.Click += delegate { ShowPreview(); };
            open.Click += delegate { OpenProduct(); };
            print.Checked += delegate { UpdateState(); }; print.Unchecked += delegate { UpdateState(); };
            Loaded += delegate { DialogViewport.Position(this, DialogViewport.WorkArea(Owner)); };
            Closing += delegate
            {
                closed = true; generation++;
                if (cancellation != null) { cancellation.Cancel(); }
                password.Clear();
            };
            Closed += delegate { if (!busy) { client.Dispose(); } };
            UpdateState();
        }

        private void SelectionChanged()
        {
            if (updating) { return; }
            reviewed.IsChecked = false; summary.Text = draft.BuildDescription();
        }

        private async Task SignIn()
        {
            if (busy || closed || context != null) { return; }
            string address = email.Text.Trim(), secret = password.Password;
            if (address.Length == 0 || secret.Length == 0) { status.Text = "Введите логин и пароль CRM."; return; }
            int request = BeginOperation("Входим в CRM…");
            try
            {
                CrmContext found = await client.LoginAsync(address, secret, cancellation.Token);
                if (!Current(request)) { return; }
                context = found;
                warehouse.ItemsSource = found.Warehouses;
                printer.ItemsSource = found.Printers;
                warehouse.SelectedIndex = found.Warehouses != null && found.Warehouses.Count == 1 ? 0 : -1;
                printer.SelectedIndex = found.Printers != null && found.Printers.Count == 1 ? 0 : -1;
                account.Text = "Вход выполнен: " + address;
                account.Visibility = Visibility.Visible;
                loginPanel.Visibility = Visibility.Collapsed;
                if (client.Pending != null) { RestorePending(); }
                else { status.Text = "Проверьте комплектацию, склад, состояние и количество. Товар будет создан без публикации."; }
            }
            catch (OperationCanceledException) { if (Current(request)) { status.Text = "Вход отменён."; } }
            catch (Exception error) { if (Current(request)) { status.Text = SafeError(error, "Не удалось войти в CRM. Проверьте подключение и попробуйте ещё раз."); } }
            finally { secret = null; password.Clear(); EndOperation(request); }
        }

        private async Task Submit()
        {
            if (busy || closed || completed || context == null) { return; }
            CrmSubmission submission = client.Pending;
            if (submission == null)
            {
                string validation;
                if (!TryCreateSubmission(out submission, out validation)) { status.Text = validation; return; }
            }
            int request = BeginOperation(client.Pending != null ? "Проверяем предыдущую отправку и повторяем незавершённые действия…" : "Создаём товар и прикладываем фото характеристик…");
            try
            {
                CrmIntakeResult response = await client.SubmitAsync(submission, cancellation.Token);
                if (!Current(request)) { return; }
                result = response;
                completed = client.Pending == null;
                if (!completed) { RestorePending(); }
                status.Text = ResultMessage(response);
            }
            catch (OperationCanceledException)
            {
                if (Current(request)) { status.Text = "Отправка прервана. Если запрос уже получен CRM, повторная отправка продолжит его без создания копии."; }
            }
            catch (Exception error)
            {
                if (Current(request))
                {
                    if (client.Pending != null) { RestorePending(); }
                    status.Text = SafeError(error, "Не удалось получить ответ CRM. Повторите отправку: сохранённый запрос будет использован без изменений.");
                }
            }
            finally { EndOperation(request); }
        }

        private bool TryCreateSubmission(out CrmSubmission submission, out string error)
        {
            submission = null; error = "";
            string title = name.Text.Trim();
            if (title.Length == 0) { error = "Укажите название товара."; return false; }
            var selectedWarehouse = warehouse.SelectedItem as CrmChoice;
            long warehouseId;
            if (selectedWarehouse == null || !Int64.TryParse(selectedWarehouse.Id, out warehouseId) || warehouseId <= 0) { error = "Выберите склад."; return false; }
            var selectedCondition = condition.SelectedItem as ConditionChoice;
            if (selectedCondition == null) { error = "Выберите состояние товара вручную."; return false; }
            int count;
            if (!Int32.TryParse(quantity.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > 500)
            { error = "Количество должно быть целым числом от 1 до 500."; return false; }
            decimal? amount;
            if (!ParsePrice(price.Text, out amount)) { error = "Введите цену в MDL, например 4500 или 4500,50; либо оставьте поле пустым."; return false; }
            if (reviewed.IsChecked != true) { error = "Проверьте выбранные накопители и GPU и подтвердите комплектацию. Компьютер без накопителя тоже допустим."; return false; }
            bool printing = print.IsChecked == true;
            var selectedPrinter = printer.SelectedItem as CrmChoice;
            if (printing && (context == null || !context.CanPrint || selectedPrinter == null)) { error = "Печать доступна только из офиса; выберите разрешённый принтер."; return false; }
            string description = BuildDescription();
            if (description.Length > 12000) { error = "Описание слишком длинное. Сократите дополнения или выбранную комплектацию."; return false; }
            if (reviewedImageFingerprint != title + "\0" + description)
            { error = "Откройте предпросмотр фото с текущими характеристиками перед отправкой."; return false; }
            byte[] png;
            try { png = CrmProductImage.RenderPng(title, description); }
            catch (Exception) { error = "Не удалось подготовить фото характеристик. Попробуйте сократить описание."; return false; }
            submission = new CrmSubmission
            {
                IdempotencyKey = Guid.NewGuid().ToString("D"), Name = title, Description = description,
                Condition = selectedCondition.Code, WarehouseId = warehouseId, SalePrice = amount, Quantity = count,
                Print = printing, PrinterId = printing ? selectedPrinter.Id : null,
                SpecificationImageBase64 = Convert.ToBase64String(png)
            };
            return true;
        }

        private string BuildDescription()
        {
            string extra = notes.Text.Trim();
            return draft.BuildDescription() + (extra.Length == 0 ? "" : Environment.NewLine + Environment.NewLine + "Описание продавца:" + Environment.NewLine + extra);
        }

        private void RestorePending()
        {
            CrmSubmission pending = client.Pending;
            if (pending == null) { return; }
            updating = true;
            try
            {
                name.Text = pending.Name; summary.Text = pending.Description;
                price.Text = pending.SalePrice.HasValue ? pending.SalePrice.Value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) : "";
                quantity.Text = pending.Quantity.ToString(CultureInfo.InvariantCulture);
                foreach (ConditionChoice item in condition.Items) { if (item.Code == pending.Condition) { condition.SelectedItem = item; break; } }
                SelectById(warehouse, pending.WarehouseId.ToString(CultureInfo.InvariantCulture));
                SelectById(printer, pending.PrinterId);
                print.IsChecked = pending.Print; reviewed.IsChecked = true;
                components.Visibility = Visibility.Collapsed; notes.Visibility = Visibility.Collapsed;
                status.Text = "Восстановлена незавершённая отправка. Данные зафиксированы; повторный запрос продолжит её без создания копии товара.";
            }
            finally { updating = false; }
        }

        private void ShowPreview()
        {
            if (busy || closed) { return; }
            try
            {
                CrmSubmission pending = client.Pending;
                byte[] bytes = pending != null ? Convert.FromBase64String(pending.SpecificationImageBase64)
                    : CrmProductImage.RenderPng(name.Text.Trim(), BuildDescription());
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); }
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 1050 };
                AutomationProperties.SetName(image, "Фото характеристик, которое будет прикреплено к товару в CRM");
                var dialog = new Window
                {
                    Owner = this, Title = "Фото характеристик для CRM", Width = 760, Height = 720, MinWidth = 360, MinHeight = 300,
                    Background = Brushes.White, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new ScrollViewer { Content = image, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
                };
                DialogViewport.Fit(dialog, DialogViewport.WorkArea(this)); dialog.ShowDialog();
                if (pending == null) { reviewedImageFingerprint = name.Text.Trim() + "\0" + BuildDescription(); }
            }
            catch (Exception) { status.Text = "Не удалось показать фото характеристик. Сократите описание и попробуйте снова."; }
        }

        private void OpenProduct()
        {
            Uri target, origin;
            if (result == null || !Uri.TryCreate(result.ProductUrl, UriKind.Absolute, out target)
                || !Uri.TryCreate(CrmIntakeClient.DefaultBaseUrl, UriKind.Absolute, out origin)
                || target.Scheme != Uri.UriSchemeHttps || !String.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                || target.Port != origin.Port || target.UserInfo.Length != 0) { status.Text = "Откройте созданный товар в обычном интерфейсе CRM."; return; }
            try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception) { status.Text = "Не удалось открыть браузер. Товар доступен в CRM: " + result.ProductId; }
        }

        private int BeginOperation(string message)
        {
            if (cancellation != null) { cancellation.Dispose(); }
            cancellation = new CancellationTokenSource(); busy = true; status.Text = message; UpdateState(); return ++generation;
        }
        private bool Current(int request) { return !closed && request == generation; }
        private void EndOperation(int request)
        {
            if (!Current(request))
            {
                if (closed)
                {
                    if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                    busy = false; client.Dispose();
                }
                return;
            }
            busy = false;
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            UpdateState();
        }

        private void UpdateState()
        {
            bool pending = client.Pending != null;
            bool editable = !busy && !closed && !completed && !pending;
            foreach (Control editor in editors) { editor.IsEnabled = editable; }
            login.IsEnabled = !busy && !closed && context == null;
            email.IsEnabled = password.IsEnabled = login.IsEnabled;
            bool hasPrinters = context != null && context.Printers != null && context.Printers.Count > 0;
            print.IsEnabled = editable && context != null && context.CanPrint && hasPrinters;
            printer.IsEnabled = print.IsEnabled && print.IsChecked == true;
            printReason.Text = context == null ? "Доступность офисной печати проверяется CRM после входа."
                : !context.CanPrint ? (String.IsNullOrWhiteSpace(context.PrintReason) ? "Печать разрешена только из офиса ADAMO." : context.PrintReason)
                : !hasPrinters ? "В CRM не найден доступный принтер наклеек."
                : "Офисная печать доступна. Для каждой единицы CRM использует её AD-код и штатный макет наклейки.";
            send.IsEnabled = !busy && !closed && !completed && context != null;
            send.Content = pending ? result != null && result.ImageStatus != "attached" ? "Повторить загрузку фото" : "Продолжить отправку" : "Создать в CRM";
            AutomationProperties.SetName(send, Convert.ToString(send.Content));
            preview.IsEnabled = !busy && !closed;
            open.Visibility = result == null || String.IsNullOrWhiteSpace(result.ProductUrl) ? Visibility.Collapsed : Visibility.Visible;
            open.IsEnabled = !busy && !closed;
        }

        internal static bool ParsePrice(string input, out decimal? price)
        {
            price = null;
            string text = (input ?? "").Trim().Replace(" ", "").Replace("\u00a0", "");
            if (text.Length == 0) { return true; }
            decimal amount;
            if (text.Length > 16 || !Regex.IsMatch(text, @"^\d+(?:[.,]\d{1,2})?$", RegexOptions.CultureInvariant)
                || !Decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
                || amount < 0 || amount > 999999999) { return false; }
            price = amount; return true;
        }
        internal static string ResultMessage(CrmIntakeResult result)
        {
            string message = "Товар создан в CRM: " + result.ProductId + ". Публикация не выполнялась.";
            message += result.ImageStatus == "attached" ? " Фото характеристик прикреплено." : " Загрузка фото не подтверждена; повторите загрузку.";
            if (result.PrintRequested)
            {
                message += !String.IsNullOrWhiteSpace(result.PrintMessage) ? " " + result.PrintMessage
                    : result.PrintStatus == "queued" ? " Наклейка передана в очередь печати."
                    : result.PrintStatus == "failed" ? " Печать не запущена. Повторите её в карточке товара CRM."
                    : result.PrintStatus == "paused" ? " Печать приостановлена; проверьте очередь CRM."
                    : result.PrintStatus == "cancelled" ? " Задание печати отменено; проверьте карточку товара CRM."
                    : result.PrintStatus == "completed" ? " CRM сообщает о завершении задания печати."
                    : " Статус печати: " + result.PrintStatus + ".";
            }
            return message;
        }
        private static string SafeError(Exception error, string fallback) { return error is CrmException ? error.Message : fallback; }
        private static void SelectById(ComboBox box, string id)
        { foreach (CrmChoice item in box.Items) { if (item.Id == id) { box.SelectedItem = item; return; } } box.SelectedIndex = -1; }
        private static TextBox Entry(string accessibleName)
        {
            var field = new TextBox { MinHeight = 32, Padding = new Thickness(6), Margin = new Thickness(0, 3, 8, 4), Foreground = Brush("#1D1D1F"), BorderBrush = Brush("#BAC2C9") };
            AutomationProperties.SetName(field, accessibleName); return field;
        }
        private static ComboBox Choice(string accessibleName)
        {
            var field = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 3, 8, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(field, accessibleName); return field;
        }
        private static CheckBox Check(string text)
        { return new CheckBox { Content = Text(text), Margin = new Thickness(0, 3, 0, 4), VerticalContentAlignment = VerticalAlignment.Center }; }
        private static Button Button(string text)
        {
            var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 3, 8, 4), Background = Brushes.White, Foreground = Brush("#1D1D1F"), BorderBrush = Brush("#BAC2C9") };
            AutomationProperties.SetName(button, text); return button;
        }
        private static TextBlock Text(string text) { return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 4), Foreground = Brush("#343A40") }; }
        private static StackPanel Field(string label, UIElement input) { var panel = new StackPanel(); panel.Children.Add(Text(label)); panel.Children.Add(input); return panel; }
        private static Grid TwoColumns(UIElement first, UIElement second)
        { var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.Children.Add(first); Grid.SetColumn(second, 1); grid.Children.Add(second); return grid; }
        private static SolidColorBrush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
    }
}
