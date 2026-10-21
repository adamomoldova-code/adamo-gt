using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PcPulse
{
    // Observes only this window's input while explicitly started. No global hook,
    // typed text, key sequence or background keyboard monitoring is retained.
    public sealed class KeyboardTestPanel : UserControl
    {
        private const double KeyUnit = 36;
        private const double KeyHeight = 38;
        private readonly Dictionary<string, Border> keys = new Dictionary<string, Border>();
        private readonly Dictionary<Key, string> keyNames = new Dictionary<Key, string>();
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly HashSet<string> pressed = new HashSet<string>();
        private readonly TextBlock status;
        private readonly TextBlock focusStatus;
        private readonly Border captureSurface;
        private IInputElement previousFocus;
        private Window owner;
        private HwndSource source;
        private int nativeTimestamp = Int32.MinValue;
        private int nativeScanCode;
        private bool nativeExtended;
        private bool nativeKeyDown;

        public event EventHandler ProgressChanged;
        public bool IsRunning { get; private set; }
        public int SeenCount { get { return seen.Count; } }
        public int PressedCount { get { return pressed.Count; } }
        public string Summary { get; private set; }

        public KeyboardTestPanel()
        {
            Focusable = false;
            IsTabStop = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            Foreground = Brush("#1D1D1F");
            AutomationProperties.SetName(this, "Проверка физических клавиш клавиатуры");
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(panel, Text("Нажимайте физические клавиши. Тест включается при выборе вкладки и возвращении в окно. Тёмная клавиша — нажата, зелёная — уже проверена."), 0);
            focusStatus = Text("Тест ещё не запущен.");
            focusStatus.Margin = new Thickness(0, 7, 0, 8);
            Add(panel, focusStatus, 1);

            var keyboard = new Grid { Width = KeyUnit * 22 + 24, Height = KeyHeight * 6, HorizontalAlignment = HorizontalAlignment.Left };
            keyboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyUnit * 15) });
            keyboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            keyboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyUnit * 3) });
            keyboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            keyboard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyUnit * 4) });
            keyboard.Children.Add(MainKeyboard());
            var navigation = NavigationKeys(); Grid.SetColumn(navigation, 2); keyboard.Children.Add(navigation);
            var numberPad = NumberPad(); Grid.SetColumn(numberPad, 4); keyboard.Children.Add(numberPad);
            var scroll = new ScrollViewer
            {
                Content = keyboard, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false,
                Margin = new Thickness(0, 0, 0, 8)
            };
            captureSurface = new Border { Child = scroll, Focusable = true, Background = Brushes.Transparent };
            KeyboardNavigation.SetIsTabStop(captureSurface, true);
            AutomationProperties.SetName(captureSurface, "Область проверки клавиатуры. Escape завершает захват клавиш.");
            captureSurface.GotKeyboardFocus += delegate { UpdateStatus(false); };
            Add(panel, captureSurface, 2);
            status = Text(""); status.FontWeight = FontWeights.SemiBold;
            Add(panel, status, 3);
            var note = Text("Esc завершает захват и возвращает обычную навигацию Tab. Alt+F4 и сочетания Win работают как обычно. Схема типовая: на ноутбуке может не быть NumPad, правого Win или ISO. Fn, питание и часть специальных клавиш Windows не передаёт. Счётчик не означает, что вся клавиатура исправна — результат подтвердите вручную.");
            note.FontSize = 13; note.Foreground = Brush("#4B5158"); note.Margin = new Thickness(0, 8, 0, 0);
            Add(panel, note, 4);
            Content = panel;
            Loaded += delegate { if (IsRunning) { AttachWindow(); captureSurface.Focus(); } };
            Unloaded += delegate { Stop(false); };
            captureSurface.PreviewMouseLeftButtonDown += delegate { if (IsRunning) { captureSurface.Focus(); } };
            UpdateStatus(false);
        }

        public void Start()
        {
            bool starting = !IsRunning;
            if (!InsideCapture(Keyboard.FocusedElement as DependencyObject)) { previousFocus = Keyboard.FocusedElement; }
            IsRunning = true;
            AttachWindow();
            captureSurface.Focus();
            UpdateStatus(starting);
        }

        public void Stop(bool restorePreviousFocus = true)
        {
            bool changed = IsRunning || pressed.Count > 0;
            bool restoreFocus = restorePreviousFocus && captureSurface.IsKeyboardFocusWithin;
            IsRunning = false;
            DetachWindow();
            ClearPressed();
            var restoreTarget = previousFocus as UIElement;
            previousFocus = null;
            // The owning dialog re-enables its Start button in ProgressChanged.
            // Notify first so Esc can return focus to that previously disabled button.
            UpdateStatus(changed);
            if (restoreFocus && restoreTarget != null && restoreTarget.IsVisible && restoreTarget.IsEnabled && restoreTarget.Focusable) { restoreTarget.Focus(); }
        }

        public void Reset()
        {
            seen.Clear();
            pressed.Clear();
            nativeTimestamp = Int32.MinValue;
            foreach (string id in keys.Keys) { PaintKey(id); }
            UpdateStatus(true);
        }

        // Shared by preview input and deterministic tests. scanCode comes only
        // from the owning HWND's matching message, never a global keyboard hook.
        public bool ProcessKey(Key key, bool isDown, bool isExtended = false, int scanCode = 0)
        {
            if (!IsRunning) { return false; }
            string id = ResolveKey(key, isExtended, scanCode);
            if (id == null || !keys.ContainsKey(id)) { return false; }
            bool changed;
            if (isDown)
            {
                changed = seen.Add(id);
                changed = pressed.Add(id) || changed;
            }
            else { changed = pressed.Remove(id); }
            if (changed) { PaintKey(id); UpdateStatus(true); }
            return changed;
        }

        public bool HasSeen(string keyId) { return keyId != null && seen.Contains(keyId); }
        public bool IsPressed(string keyId) { return keyId != null && pressed.Contains(keyId); }

        public static Key NormalizeKey(Key key, Key systemKey, Key imeProcessedKey)
        {
            return key == Key.System ? systemKey : key == Key.ImeProcessed ? imeProcessedKey : key;
        }

        internal void ReleasePressedKeys()
        {
            bool changed = pressed.Count > 0;
            ClearPressed();
            UpdateStatus(changed);
        }

        private void ClearPressed()
        {
            var old = new List<string>(pressed);
            pressed.Clear();
            nativeTimestamp = Int32.MinValue;
            foreach (string id in old) { PaintKey(id); }
        }

        private void AttachWindow()
        {
            Window window = Window.GetWindow(this);
            if (window == owner) { AttachSource(); return; }
            DetachWindow();
            owner = window;
            if (owner == null) { return; }
            owner.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDownObserved), true);
            owner.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(KeyUpObserved), true);
            owner.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(FocusLost), true);
            owner.Deactivated += OwnerDeactivated;
            owner.Activated += OwnerActivated;
            owner.SourceInitialized += SourceInitialized;
            owner.Closed += OwnerClosed;
            AttachSource();
        }

        private void AttachSource()
        {
            if (source != null || owner == null) { return; }
            source = PresentationSource.FromVisual(owner) as HwndSource;
            if (source != null) { source.AddHook(ObserveWindowMessage); }
        }

        private void DetachWindow()
        {
            if (source != null)
            {
                if (!source.IsDisposed) { source.RemoveHook(ObserveWindowMessage); }
                source = null;
            }
            if (owner == null) { return; }
            owner.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDownObserved));
            owner.RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(KeyUpObserved));
            owner.RemoveHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(FocusLost));
            owner.Deactivated -= OwnerDeactivated;
            owner.Activated -= OwnerActivated;
            owner.SourceInitialized -= SourceInitialized;
            owner.Closed -= OwnerClosed;
            owner = null;
        }

        private void SourceInitialized(object sender, EventArgs args) { AttachSource(); }
        private void OwnerClosed(object sender, EventArgs args) { Stop(); }
        private void OwnerDeactivated(object sender, EventArgs args) { ReleasePressedKeys(); }
        private void OwnerActivated(object sender, EventArgs args) { UpdateStatus(false); }
        private void FocusLost(object sender, KeyboardFocusChangedEventArgs args)
        {
            var destination = args.NewFocus as DependencyObject;
            if (!InsideCapture(destination)) { ReleasePressedKeys(); }
        }
        private void KeyDownObserved(object sender, KeyEventArgs args) { ObserveKey(args, true); }
        private void KeyUpObserved(object sender, KeyEventArgs args) { ObserveKey(args, false); }

        private void ObserveKey(KeyEventArgs args, bool isDown)
        {
            if (!IsRunning || owner == null || !owner.IsActive || !captureSurface.IsKeyboardFocusWithin) { return; }
            Key key = NormalizeKey(args.Key, args.SystemKey, args.ImeProcessedKey);
            bool matchingMessage = args.Timestamp == nativeTimestamp && isDown == nativeKeyDown;
            ProcessKey(key, isDown, matchingMessage && nativeExtended, matchingMessage ? nativeScanCode : 0);
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (key == Key.Escape && modifiers == ModifierKeys.None && isDown)
            {
                args.Handled = true;
                Stop();
            }
            else if (CapturesNavigation(key, modifiers)) { args.Handled = true; }
            // Only plain navigation/activation keys stay inside capture. Alt+F4,
            // Alt+Tab, Ctrl+Esc, Win and other OS shortcuts are not swallowed.
        }

        internal static bool CapturesNavigation(Key key, ModifierKeys modifiers)
        {
            return key == Key.Tab && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift)
                || (key == Key.Space || key == Key.Return) && modifiers == ModifierKeys.None;
        }

        private bool InsideCapture(DependencyObject element)
        {
            for (DependencyObject current = element; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            { if (ReferenceEquals(current, captureSurface)) { return true; } }
            return false;
        }

        private IntPtr ObserveWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (IsRunning && (message == 0x100 || message == 0x101 || message == 0x104 || message == 0x105))
            {
                long bits = lParam.ToInt64();
                nativeScanCode = (int)((bits >> 16) & 0xff);
                nativeExtended = (bits & 0x01000000) != 0;
                nativeKeyDown = message == 0x100 || message == 0x104;
                nativeTimestamp = GetMessageTime();
            }
            return IntPtr.Zero;
        }

        private string ResolveKey(Key key, bool extended, int scan)
        {
            if (scan == 0x1c || key == Key.Return) { return extended ? "NumEnter" : "Enter"; }
            if (scan == 0x1d) { return extended ? "RightCtrl" : "LeftCtrl"; }
            if (scan == 0x38) { return extended ? "RightAlt" : "LeftAlt"; }
            if (scan == 0x2a) { return "LeftShift"; }
            if (scan == 0x36) { return "RightShift"; }
            if (!extended)
            {
                switch (scan)
                {
                    case 0x47: return "NumPad7"; case 0x48: return "NumPad8"; case 0x49: return "NumPad9";
                    case 0x4b: return "NumPad4"; case 0x4c: return "NumPad5"; case 0x4d: return "NumPad6";
                    case 0x4f: return "NumPad1"; case 0x50: return "NumPad2"; case 0x51: return "NumPad3";
                    case 0x52: return "NumPad0"; case 0x53: return "Decimal";
                }
            }
            string name;
            return keyNames.TryGetValue(key, out name) ? name : null;
        }

        private void UpdateStatus(bool notify)
        {
            string state = IsRunning ? "Тест идёт" : seen.Count > 0 ? "Тест остановлен" : "Тест ещё не запущен";
            Summary = state + " · Получено нажатий разных клавиш: " + seen.Count + " · Сейчас нажато: " + pressed.Count;
            status.Text = Summary;
            focusStatus.Text = !IsRunning ? "Учёт остановлен. Нажмите «Продолжить», выберите вкладку заново или вернитесь в окно проверки."
                : owner != null && !owner.IsActive ? "Окно неактивно. Вернитесь в окно проверки; новые нажатия вне него не учитываются."
                : !captureSurface.IsKeyboardFocusWithin ? "Выберите область клавиатуры мышью или клавишей Tab, чтобы продолжить учёт."
                : "Нажимайте клавиши. Esc — закончить; Tab, Enter и пробел проверяются здесь. Щелчки по рисунку не учитываются.";
            if (notify && ProgressChanged != null) { ProgressChanged(this, EventArgs.Empty); }
        }

        private Grid MainKeyboard()
        {
            var panel = Rows(6);
            AddRow(panel, 0, Spec(Key.Escape, "Esc"), Gap(1), Spec(Key.F1, "F1"), Spec(Key.F2, "F2"), Spec(Key.F3, "F3"), Spec(Key.F4, "F4"), Gap(.5), Spec(Key.F5, "F5"), Spec(Key.F6, "F6"), Spec(Key.F7, "F7"), Spec(Key.F8, "F8"), Gap(.5), Spec(Key.F9, "F9"), Spec(Key.F10, "F10"), Spec(Key.F11, "F11"), Spec(Key.F12, "F12"));
            AddRow(panel, 1, Spec(Key.Oem3, "` ~"), Spec(Key.D1, "1"), Spec(Key.D2, "2"), Spec(Key.D3, "3"), Spec(Key.D4, "4"), Spec(Key.D5, "5"), Spec(Key.D6, "6"), Spec(Key.D7, "7"), Spec(Key.D8, "8"), Spec(Key.D9, "9"), Spec(Key.D0, "0"), Spec(Key.OemMinus, "−"), Spec(Key.OemPlus, "+ ="), Spec(Key.Back, "Backspace", 2));
            AddRow(panel, 2, Spec(Key.Tab, "Tab", 1.5), Spec(Key.Q, "Q"), Spec(Key.W, "W"), Spec(Key.E, "E"), Spec(Key.R, "R"), Spec(Key.T, "T"), Spec(Key.Y, "Y"), Spec(Key.U, "U"), Spec(Key.I, "I"), Spec(Key.O, "O"), Spec(Key.P, "P"), Spec(Key.Oem4, "["), Spec(Key.Oem6, "]"), Spec(Key.Oem5, "\\", 1.5));
            AddRow(panel, 3, Spec(Key.CapsLock, "Caps", 1.75), Spec(Key.A, "A"), Spec(Key.S, "S"), Spec(Key.D, "D"), Spec(Key.F, "F"), Spec(Key.G, "G"), Spec(Key.H, "H"), Spec(Key.J, "J"), Spec(Key.K, "K"), Spec(Key.L, "L"), Spec(Key.Oem1, ";"), Spec(Key.Oem7, "'"), Spec(Key.Return, "Enter", 2.25, "Enter"));
            AddRow(panel, 4, Spec(Key.LeftShift, "Shift", 1.75), Spec(Key.Oem102, "ISO"), Spec(Key.Z, "Z"), Spec(Key.X, "X"), Spec(Key.C, "C"), Spec(Key.V, "V"), Spec(Key.B, "B"), Spec(Key.N, "N"), Spec(Key.M, "M"), Spec(Key.OemComma, ","), Spec(Key.OemPeriod, "."), Spec(Key.Oem2, "/"), Spec(Key.RightShift, "Shift", 2.25));
            AddRow(panel, 5, Spec(Key.LeftCtrl, "Ctrl", 1.25), Spec(Key.LWin, "Win", 1.25), Spec(Key.LeftAlt, "Alt", 1.25), Spec(Key.Space, "Пробел", 6.25), Spec(Key.RightAlt, "Alt", 1.25), Spec(Key.RWin, "Win", 1.25), Spec(Key.Apps, "Menu", 1.25), Spec(Key.RightCtrl, "Ctrl", 1.25));
            return panel;
        }

        private Grid NavigationKeys()
        {
            var grid = Rows(6);
            AddRow(grid, 0, Spec(Key.PrintScreen, "PrtSc"), Spec(Key.Scroll, "ScrLk"), Spec(Key.Pause, "Pause"));
            AddRow(grid, 1, Spec(Key.Insert, "Ins"), Spec(Key.Home, "Home"), Spec(Key.PageUp, "PgUp"));
            AddRow(grid, 2, Spec(Key.Delete, "Del"), Spec(Key.End, "End"), Spec(Key.PageDown, "PgDn"));
            AddRow(grid, 4, Gap(1), Spec(Key.Up, "↑"), Gap(1));
            AddRow(grid, 5, Spec(Key.Left, "←"), Spec(Key.Down, "↓"), Spec(Key.Right, "→"));
            return grid;
        }

        private Grid NumberPad()
        {
            var grid = Rows(6);
            for (int i = 0; i < 4; i++) { grid.ColumnDefinitions.Add(new ColumnDefinition()); }
            Put(grid, Spec(Key.NumLock, "Num"), 1, 0); Put(grid, Spec(Key.Divide, "/"), 1, 1); Put(grid, Spec(Key.Multiply, "×"), 1, 2); Put(grid, Spec(Key.Subtract, "−"), 1, 3);
            Put(grid, Spec(Key.NumPad7, "7"), 2, 0); Put(grid, Spec(Key.NumPad8, "8"), 2, 1); Put(grid, Spec(Key.NumPad9, "9"), 2, 2); Put(grid, Spec(Key.Add, "+"), 2, 3, 2, 1);
            Put(grid, Spec(Key.NumPad4, "4"), 3, 0); Put(grid, Spec(Key.NumPad5, "5"), 3, 1); Put(grid, Spec(Key.NumPad6, "6"), 3, 2);
            Put(grid, Spec(Key.NumPad1, "1"), 4, 0); Put(grid, Spec(Key.NumPad2, "2"), 4, 1); Put(grid, Spec(Key.NumPad3, "3"), 4, 2); Put(grid, Spec(Key.None, "Enter", 1, "NumEnter"), 4, 3, 2, 1);
            Put(grid, Spec(Key.NumPad0, "0"), 5, 0, 1, 2); Put(grid, Spec(Key.Decimal, "."), 5, 2);
            return grid;
        }

        private void AddRow(Grid parent, int index, params KeySpec[] row)
        {
            var grid = new Grid();
            for (int i = 0; i < row.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(row[i].Units, GridUnitType.Star) });
                if (row[i].Id == null) { continue; }
                var key = CreateKey(row[i]); Grid.SetColumn(key, i); grid.Children.Add(key);
            }
            Add(parent, grid, index);
        }

        private void Put(Grid grid, KeySpec spec, int row, int column, int rows = 1, int columns = 1)
        {
            var key = CreateKey(spec);
            Grid.SetRow(key, row); Grid.SetColumn(key, column); Grid.SetRowSpan(key, rows); Grid.SetColumnSpan(key, columns); grid.Children.Add(key);
        }

        private Border CreateKey(KeySpec spec)
        {
            var label = new TextBlock { Text = spec.Label, FontSize = 12, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var border = new Border { Child = label, Margin = new Thickness(2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), ToolTip = spec.Id, Focusable = false };
            keys.Add(spec.Id, border);
            if (spec.Key != Key.None) { keyNames[spec.Key] = spec.Id; }
            AutomationProperties.SetName(border, spec.Id);
            PaintKey(spec.Id);
            return border;
        }

        private void PaintKey(string id)
        {
            Border border = keys[id];
            bool down = pressed.Contains(id), checkedKey = seen.Contains(id);
            border.Background = Brush(down ? "#2C3136" : checkedKey ? "#E8EFEA" : "#FFFFFF");
            border.BorderBrush = Brush(down ? "#2C3136" : checkedKey ? "#52785B" : "#CAD0D5");
            ((TextBlock)border.Child).Foreground = Brush(down ? "#FFFFFF" : "#1D1D1F");
            AutomationProperties.SetHelpText(border, down ? "Нажата" : checkedKey ? "Нажатие получено" : "Ещё не проверена");
        }

        private static Grid Rows(int count)
        {
            var grid = new Grid();
            for (int i = 0; i < count; i++) { grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyHeight) }); }
            return grid;
        }
        private static void Add(Grid grid, UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
        private static TextBlock Text(string value) { return new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap }; }
        private static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromInvariantString(color); }
        private static KeySpec Gap(double units) { return new KeySpec { Units = units }; }
        private static KeySpec Spec(Key key, string label, double units = 1, string id = null) { return new KeySpec { Key = key, Label = label, Units = units, Id = id ?? key.ToString() }; }
        private sealed class KeySpec { public Key Key; public string Id; public string Label; public double Units; }

        [DllImport("user32.dll")] private static extern int GetMessageTime();
    }
}
