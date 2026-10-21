using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PcPulse
{
    // ADAMO visual assets; original sources and logo preparation are recorded in assets/brand-source.md.
    public static class BrandAssets
    {
        public const string LogoResourceName = "PcPulse.BrandLogo.png";

        public static void Apply(Window window)
        {
            BitmapSource logo = LoadLogo();
            if (logo != null)
            {
                // The window uses the executable's multi-size Win32 ICO. The PNG
                // is only the larger dashboard mark, not a replacement shell icon.
                var image = window.FindName("AppLogo") as Image;
                if (image != null)
                {
                    image.Source = logo;
                    AutomationProperties.SetName(image, "ADAMO");
                }
            }

            SetButton(window, "SaveReportButton", "task", "Отчёт", 16);
            SetButton(window, "RefreshButton", "refresh", "Обновить", 16);
            SetButton(window, "SensorsButton", "settings", "Датчики", 16);
            SetButton(window, "PeripheralTestsButton", "scan", "Устройства", 16);
            SetButton(window, "StopTestButton", "close", "Остановить", 16);
            SetButton(window, "RunTestButton", "refresh", "Повторить SSD", 16);
            SetButton(window, "RunGpuTestButton", "scan", "Тест GPU", 14);
            SetButton(window, "PortsSourceButton", "view", "Источник", 14);
            SetButton(window, "PortsRefreshButton", "refresh", "", 14);
            SetButton(window, "PortsSearchButton", "search", "Поиск", 14);
        }

        public static BitmapSource LoadLogo()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LogoResourceName))
            {
                if (stream == null) { return null; }
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private static void SetButton(Window window, string name, string icon, string label, double size)
        {
            var button = window.FindName(name) as Button;
            if (button == null) { return; }
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var path = new System.Windows.Shapes.Path
            {
                Data = IconGeometry(icon), StrokeThickness = 1.75,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = false
            };
            path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { Source = button });
            var canvas = new Canvas { Width = 24, Height = 24, Children = { path } };
            content.Children.Add(new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center });
            if (label.Length > 0)
            {
                var text = new TextBlock { Text = label, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = button });
                text.SetBinding(TextBlock.FontSizeProperty, new Binding("FontSize") { Source = button });
                text.SetBinding(TextBlock.FontWeightProperty, new Binding("FontWeight") { Source = button });
                content.Children.Add(text);
            }
            button.Content = content;
            AutomationProperties.SetName(button, label.Length > 0 ? label : "Обновить спецификацию");
        }

        private static Geometry IconGeometry(string icon)
        {
            // Same 24 px outline geometry as icon() in the ADAMO APK's src/main.js.
            string data;
            switch (icon)
            {
                case "task": data = "M8 5h11v15H5V8l3-3Z M8 5v3H5 M9 12h6 M9 16h6"; break;
                case "refresh": data = "M20 11a8 8 0 0 0-14.8-3L4 10 M4 5v5h5 M4 13a8 8 0 0 0 14.8 3L20 14 M20 19v-5h-5"; break;
                case "settings": data = "M12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7Z M19 13l.1-1-.1-1 2-1.5-2-3.4-2.4 1a8 8 0 0 0-1.7-1L14.5 3h-4l-.4 3.1a8 8 0 0 0-1.7 1l-2.4-1-2 3.4L6 11a8 8 0 0 0 0 2l-2 1.5 2 3.4 2.4-1a8 8 0 0 0 1.7 1l.4 3.1h4l.4-3.1a8 8 0 0 0 1.7-1l2.4 1 2-3.4L19 13Z"; break;
                case "scan": data = "M4 8V5a1 1 0 0 1 1-1h3 M16 4h3a1 1 0 0 1 1 1v3 M20 16v3a1 1 0 0 1-1 1h-3 M8 20H5a1 1 0 0 1-1-1v-3 M7 12h10"; break;
                case "close": data = "M7 7l10 10 M17 7L7 17"; break;
                case "view": return OutlineWithCircle("M3.5 12c0 0 3.2-5 8.5-5s8.5 5 8.5 5-3.2 5-8.5 5-8.5-5-8.5-5Z", 12, 12, 2.3);
                case "search": return OutlineWithCircle("M15.5 15.5l4.5 4.5", 10.5, 10.5, 6.5);
                default: throw new ArgumentOutOfRangeException("icon");
            }
            Geometry geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }

        private static Geometry OutlineWithCircle(string data, double x, double y, double radius)
        {
            var geometry = new GeometryGroup();
            geometry.Children.Add(Geometry.Parse(data));
            geometry.Children.Add(new EllipseGeometry(new Point(x, y), radius, radius));
            geometry.Freeze();
            return geometry;
        }
    }
}
