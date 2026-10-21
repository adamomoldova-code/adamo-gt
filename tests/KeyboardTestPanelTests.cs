using System;
using System.Windows;
using System.Windows.Input;
using PcPulse;

namespace PcPulseTests
{
    public static class KeyboardTestPanelTests
    {
        public static void Run()
        {
            var panel = new KeyboardTestPanel();
            int changes = 0;
            panel.ProgressChanged += delegate { changes++; };
            Require(!panel.ProcessKey(Key.A, true) && panel.SeenCount == 0, "Input is ignored before explicit Start");
            panel.Start();
            Require(panel.IsRunning && panel.ProcessKey(Key.A, true), "Explicit Start enables input");
            Require(panel.HasSeen("A") && panel.IsPressed("A") && panel.SeenCount == 1, "A key is remembered and highlighted");
            panel.Start();
            Require(panel.IsRunning && panel.HasSeen("A") && panel.SeenCount == 1, "Returning to capture resumes without resetting checked keys");
            int firstPressChanges = changes;
            Require(!panel.ProcessKey(Key.A, true) && changes == firstPressChanges, "Auto-repeat does not inflate counts or notifications");
            panel.ProcessKey(Key.A, false);
            Require(!panel.IsPressed("A") && panel.HasSeen("A") && panel.SeenCount == 1, "Release retains the checked marker");
            panel.ProcessKey(Key.LeftCtrl, true);
            panel.ProcessKey(Key.RightCtrl, true);
            Require(panel.IsPressed("LeftCtrl") && panel.IsPressed("RightCtrl"), "Left and right modifiers are independent");
            panel.ReleasePressedKeys();
            Require(panel.PressedCount == 0 && panel.SeenCount == 3, "Focus loss releases keys but retains observed history");
            panel.Stop();
            Require(!panel.IsRunning && !panel.ProcessKey(Key.B, true) && !panel.HasSeen("B"), "Stop ignores later input");
            Require(panel.SeenCount == 3, "Stop preserves manual-test results");
            panel.Start();
            Require(panel.SeenCount == 3 && panel.IsRunning, "Resuming after leaving the tab preserves its session history");
            panel.Stop();
            panel.Reset();
            Require(panel.SeenCount == 0 && panel.PressedCount == 0 && !panel.IsRunning, "Reset clears results without silently starting");
            panel.Start();
            panel.ProcessKey(Key.Return, true, false, 0x1c);
            panel.ProcessKey(Key.Return, true, true, 0x1c);
            Require(panel.HasSeen("Enter") && panel.HasSeen("NumEnter") && panel.SeenCount == 2, "Main Enter and extended numpad Enter are distinct");
            panel.ProcessKey(Key.End, true, false, 0x4f);
            Require(panel.HasSeen("NumPad1") && !panel.HasSeen("End"), "NumLock-off navigation maps to the physical number pad");
            panel.ProcessKey(Key.End, true, true, 0x4f);
            Require(panel.HasSeen("End"), "Extended navigation End remains independent of NumPad1");
            panel.ProcessKey(Key.LeftCtrl, true, true, 0x1d);
            Require(panel.HasSeen("RightCtrl"), "Native extended modifier information selects right Ctrl");
            panel.Reset();
            Require(panel.IsRunning && panel.SeenCount == 0 && panel.PressedCount == 0, "Reset while running starts a clean observation set");
            Require(!panel.ProcessKey(Key.None, true) && !panel.ProcessKey(Key.F24, true), "Unknown/unrepresented keys do not claim keyboard coverage");
            panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Require(panel.SeenCount == 0, "Mouse clicks never count as physical keyboard checks");
            Require(KeyboardTestPanel.NormalizeKey(Key.System, Key.F4, Key.None) == Key.F4, "SystemKey is unwrapped for Alt combinations");
            Require(KeyboardTestPanel.NormalizeKey(Key.ImeProcessed, Key.None, Key.A) == Key.A, "IME-processed keys are unwrapped");
            Require(KeyboardTestPanel.NormalizeKey(Key.LeftShift, Key.None, Key.None) == Key.LeftShift, "Ordinary keys remain unchanged");
            Require(KeyboardTestPanel.CapturesNavigation(Key.Tab, ModifierKeys.None) && KeyboardTestPanel.CapturesNavigation(Key.Tab, ModifierKeys.Shift), "Tab and Shift+Tab stay in the explicit capture surface");
            Require(KeyboardTestPanel.CapturesNavigation(Key.Return, ModifierKeys.None) && KeyboardTestPanel.CapturesNavigation(Key.Space, ModifierKeys.None), "Plain Enter and Space cannot activate outside buttons while captured");
            Require(!KeyboardTestPanel.CapturesNavigation(Key.F4, ModifierKeys.Alt) && !KeyboardTestPanel.CapturesNavigation(Key.Tab, ModifierKeys.Alt)
                && !KeyboardTestPanel.CapturesNavigation(Key.LWin, ModifierKeys.Windows) && !KeyboardTestPanel.CapturesNavigation(Key.Escape, ModifierKeys.Control), "OS shortcuts remain unhandled");
            Require(!panel.Summary.Contains("исправна") && !panel.Summary.Contains("100"), "A seen count never declares an automatic hardware pass");
            panel.Stop();
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
