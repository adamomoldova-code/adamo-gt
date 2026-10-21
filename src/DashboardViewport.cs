using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcPulse
{
    internal static class DashboardViewport
    {
        private const double MinimumLayoutWidth = 960;
        private const double MinimumLayoutHeight = 680;

        internal static void Attach(Window window)
        {
            if (window == null) { throw new ArgumentNullException("window"); }
            if (window.Content == null || window.Content is DashboardViewbox) { return; }

            object content = window.Content;
            window.Content = null;
            var element = content as UIElement;
            if (element == null) { element = new ContentPresenter { Content = content }; }
            window.Content = new DashboardViewbox(element);
        }

        internal static void FitToWorkArea(Window window, Rect workArea)
        {
            if (window == null) { throw new ArgumentNullException("window"); }
            if (workArea.IsEmpty || !PositiveFinite(workArea.Width) || !PositiveFinite(workArea.Height)
                || !Finite(workArea.Left) || !Finite(workArea.Top)) { return; }

            window.MinWidth = Math.Min(window.MinWidth, Math.Min(640, workArea.Width));
            window.MinHeight = Math.Min(window.MinHeight, Math.Min(440, workArea.Height));
            double width = PositiveFinite(window.Width) ? window.Width : MinimumLayoutWidth;
            double height = PositiveFinite(window.Height) ? window.Height : MinimumLayoutHeight;
            window.Width = Math.Max(window.MinWidth, Math.Min(Math.Min(width, workArea.Width), window.MaxWidth));
            window.Height = Math.Max(window.MinHeight, Math.Min(Math.Min(height, workArea.Height), window.MaxHeight));

            double left = Finite(window.Left) ? window.Left : workArea.Left + (workArea.Width - window.Width) / 2;
            double top = Finite(window.Top) ? window.Top : workArea.Top + (workArea.Height - window.Height) / 2;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - window.Width));
            window.Top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - window.Height));
        }

        private static bool PositiveFinite(double value)
        {
            return value > 0 && Finite(value);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class DashboardViewbox : Viewbox
        {
            private readonly Grid layoutHost;

            internal DashboardViewbox(UIElement content)
            {
                Stretch = Stretch.Uniform;
                StretchDirection = StretchDirection.DownOnly;
                HorizontalAlignment = HorizontalAlignment.Stretch;
                VerticalAlignment = VerticalAlignment.Stretch;
                layoutHost = new Grid();
                layoutHost.Children.Add(content);
                Child = layoutHost;
            }

            protected override Size MeasureOverride(Size constraint)
            {
                // Viewbox measures its child without bounds. Give the dashboard a finite
                // responsive layout first, then scale that whole layout for smaller screens.
                layoutHost.Width = Math.Max(MinimumLayoutWidth, PositiveFinite(constraint.Width) ? constraint.Width : MinimumLayoutWidth);
                layoutHost.Height = Math.Max(MinimumLayoutHeight, PositiveFinite(constraint.Height) ? constraint.Height : MinimumLayoutHeight);
                return base.MeasureOverride(constraint);
            }
        }
    }
}
