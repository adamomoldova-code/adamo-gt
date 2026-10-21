using System;
using System.Windows;
using System.Windows.Controls;
using PcPulse;

namespace PcPulseTests
{
    public static class DialogViewportTests
    {
        public static void Run()
        {
            foreach (Rect area in new[] { new Rect(0, 0, 1280, 752), new Rect(0, 0, 640, 464),
                new Rect(-683, 30, 683, 336), new Rect(100, 40, 480, 320), new Rect(0, 0, 320, 240) })
            {
                var scroll = new ScrollViewer { Content = new Border { Height = 1800 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var window = new Window { Width = 700, SizeToContent = SizeToContent.Height,
                    Left = 9000, Top = 9000, Content = scroll };
                DialogViewport.Fit(window, area);
                Require(window.Width <= area.Width && window.MaxHeight <= area.Height, "Dialog fits the available work area " + area);
                DialogViewport.Position(window, area);
                Require(area.Contains(new Rect(window.Left, window.Top, window.Width, window.MaxHeight)), "Dialog is repositioned inside the selected monitor");
                // Long help content must scroll inside the bounded dialog instead of
                // forcing its window beyond the screen's bottom edge.
                var size = new Size(window.Width - 16, window.MaxHeight - 39);
                scroll.Measure(size);
                scroll.Arrange(new Rect(new Point(), size));
                scroll.UpdateLayout();
                Require(scroll.ScrollableHeight > 0, "Long help text has a scrollable viewport");
                scroll.ScrollToEnd();
                scroll.UpdateLayout();
                Require(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "Bottom actions remain reachable by scrolling");
                window.Close();
            }
            var unchanged = new Window { Width = 700, MaxHeight = 800 };
            DialogViewport.Fit(unchanged, Rect.Empty);
            Require(unchanged.Width == 700 && unchanged.MaxHeight == 800, "Unavailable work area does not corrupt dialog sizing");
            unchanged.Close();
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
