using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PcPulse;

namespace PcPulseTests
{
    public static class DashboardViewportTests
    {
        public static void Run()
        {
            PreservesContentAndNames();
            FitsContentAtSmallAndNormalSizes();
            FitsWindowToWorkArea();
            HandlesUnavailableWorkArea();
            Console.WriteLine("PASS dashboard viewport: scaled bounds, normal layout, names and work area");
        }

        private static void PreservesContentAndNames()
        {
            var window = (Window)XamlReader.Parse("<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Grid x:Name='DashboardRoot'><Button x:Name='ActionButton'>Test</Button></Grid></Window>");
            var content = (Grid)window.Content;
            var button = (Button)window.FindName("ActionButton");
            int clicks = 0;
            button.Click += delegate { clicks++; };

            DashboardViewport.Attach(window);
            var viewbox = window.Content as Viewbox;
            Require(viewbox != null, "Dashboard is wrapped in a Viewbox");
            Require(viewbox.Stretch == Stretch.Uniform && viewbox.StretchDirection == StretchDirection.DownOnly, "Uniform down-only scaling");
            Require(ReferenceEquals(window.FindName("DashboardRoot"), content), "Root name registration survives reparenting");
            Require(ReferenceEquals(window.FindName("ActionButton"), button), "Child name registration survives reparenting");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(clicks == 1, "Existing control event handlers survive reparenting");
            DashboardViewport.Attach(window);
            Require(ReferenceEquals(window.Content, viewbox), "Attach is idempotent");
        }

        private static void FitsContentAtSmallAndNormalSizes()
        {
            var content = new Grid { MinWidth = 960, MinHeight = 680 };
            var topLeft = new Border { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var bottomRight = new Border { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            content.Children.Add(topLeft);
            content.Children.Add(bottomRight);
            var window = new Window { Content = content };
            DashboardViewport.Attach(window);
            var viewport = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = viewport };

            VerifyLayout(host, content, topLeft, bottomRight, 960, 680);
            VerifyLayout(host, content, topLeft, bottomRight, 832, 500);
            VerifyLayout(host, content, topLeft, bottomRight, 1200, 760);
            VerifyLayout(host, content, topLeft, bottomRight, 640, 400);
            VerifyLayout(host, content, topLeft, bottomRight, 1200, 760);
        }

        private static void VerifyLayout(Border host, Grid content, Border topLeft, Border bottomRight, double width, double height)
        {
            host.Width = width;
            host.Height = height;
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            string context = width + "x" + height;
            double expectedWidth = Math.Max(960, width);
            double expectedHeight = Math.Max(680, height);
            Require(Close(content.ActualWidth, expectedWidth) && Close(content.ActualHeight, expectedHeight), context + " has finite responsive layout dimensions");
            Rect bounds = BoundsInHost(content, host);
            Require(Inside(bounds, width, height), context + " contains the entire dashboard");
            Require(Inside(BoundsInHost(topLeft, host), width, height) && Inside(BoundsInHost(bottomRight, host), width, height), context + " includes both corner controls");
            double scaleX = bounds.Width / content.ActualWidth;
            double scaleY = bounds.Height / content.ActualHeight;
            double expectedScale = Math.Min(1, Math.Min(width / expectedWidth, height / expectedHeight));
            Require(Close(scaleX, scaleY) && Close(scaleX, expectedScale), context + " preserves proportions without enlarging");
            if (width >= 960 && height >= 680)
            {
                Require(Close(bounds.Width, width) && Close(bounds.Height, height), context + " fills normal viewport");
            }
        }

        private static void FitsWindowToWorkArea()
        {
            VerifyWindowFit(new Rect(0, 0, 1092.8, 582.4));
            VerifyWindowFit(new Rect(0, 0, 910.67, 485.33));
            VerifyWindowFit(new Rect(-832, 40, 832, 500));
            VerifyWindowFit(new Rect(10, 20, 480, 320));
            var normal = new Window { Width = 1200, Height = 760, MinWidth = 960, MinHeight = 720 };
            DashboardViewport.FitToWorkArea(normal, new Rect(0, 0, 1920, 1040));
            Require(Close(normal.Width, 1200) && Close(normal.Height, 760), "Normal window dimensions remain unchanged");
            Require(normal.MinWidth <= 640 && normal.MinHeight <= 440, "Window can be resized on smaller screens");
        }

        private static void VerifyWindowFit(Rect workArea)
        {
            var window = new Window { Width = 1200, Height = 760, MinWidth = 960, MinHeight = 720, Left = 3000, Top = 2000 };
            DashboardViewport.FitToWorkArea(window, workArea);
            var bounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            Require(workArea.Contains(bounds), "Initial window fits work area " + workArea);
            Require(window.MinWidth <= window.Width && window.MinHeight <= window.Height, "Minimum window constraints permit fitting");
            Require(window.WindowStartupLocation == WindowStartupLocation.Manual, "Position is retained at startup");
        }

        private static void HandlesUnavailableWorkArea()
        {
            var window = new Window { Width = 1200, Height = 760, MinWidth = 960, MinHeight = 720 };
            DashboardViewport.FitToWorkArea(window, Rect.Empty);
            DashboardViewport.FitToWorkArea(window, new Rect(0, 0, 0, 0));
            Require(window.Width == 1200 && window.Height == 760 && window.MinWidth == 960, "Unavailable work area does not alter the window");
            var auto = new Window();
            DashboardViewport.FitToWorkArea(auto, new Rect(-800, 0, 800, 600));
            Require(new Rect(-800, 0, 800, 600).Contains(new Rect(auto.Left, auto.Top, auto.Width, auto.Height)), "Unspecified initial size uses a fitting fallback");
        }

        private static Rect BoundsInHost(FrameworkElement element, FrameworkElement host)
        {
            return element.TransformToAncestor(host).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        private static bool Inside(Rect bounds, double width, double height)
        {
            return bounds.Left >= -0.1 && bounds.Top >= -0.1 && bounds.Right <= width + 0.1 && bounds.Bottom <= height + 0.1;
        }

        private static bool Close(double value, double expected)
        {
            return Math.Abs(value - expected) < 0.1;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
