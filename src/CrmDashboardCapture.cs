using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcPulse
{
    public static class CrmDashboardCapture
    {
        internal const int MaximumBytes = 2 * 1024 * 1024;
        internal const int MaximumWidth = 2400;
        internal const int MaximumHeight = 4800;

        // Capture only the existing first-page WPF visual. Owned windows, tooltips,
        // popup menus, window chrome and the desktop are outside this visual tree.
        // Selections and scroll offsets intentionally match what the operator sees.
        public static byte[] Capture(Window window)
        {
            if (window == null) { throw new ArgumentNullException("window"); }
            window.Dispatcher.VerifyAccess();
            if (!window.IsLoaded || !window.IsVisible || window.WindowState == WindowState.Minimized)
            {
                throw new InvalidOperationException("Откройте главную страницу ADAMO GT перед отправкой в CRM.");
            }

            var page = window.Content as FrameworkElement;
            var dashboard = window.FindName("DashboardRoot") as FrameworkElement;
            if (page == null || dashboard == null || (!ReferenceEquals(page, dashboard) && !page.IsAncestorOf(dashboard)))
            {
                throw new InvalidOperationException("Для фото CRM нужна главная страница ADAMO GT.");
            }
            if (PresentationSource.FromVisual(page) == null)
            {
                throw new InvalidOperationException("Главная страница ещё не готова. Дождитесь её отображения.");
            }
            page.UpdateLayout();
            return CaptureElement(page, window.Background);
        }

        internal static byte[] CaptureElement(FrameworkElement page, Brush background)
        {
            if (page == null) { throw new ArgumentNullException("page"); }
            page.Dispatcher.VerifyAccess();
            double width = page.ActualWidth, height = page.ActualHeight;
            if (!page.IsMeasureValid || !page.IsArrangeValid || page.Visibility != Visibility.Visible
                || !PositiveFinite(width) || !PositiveFinite(height))
            {
                throw new InvalidOperationException("Главная страница ещё не готова. Дождитесь её отображения.");
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(page);
            double scale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
            if (!PositiveFinite(scale)) { scale = 1; }
            scale = Math.Min(scale, Math.Min(MaximumWidth / width, MaximumHeight / height));

            // The absolute viewbox preserves the complete arranged viewport, including
            // its background/margins; descendant bounds would crop or stretch the page.
            var pageBrush = new VisualBrush(page)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                AutoLayoutContent = false
            };
            RenderOptions.SetBitmapScalingMode(pageBrush, BitmapScalingMode.HighQuality);

            int previousWidth = -1, previousHeight = -1;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                int pixelWidth = Math.Max(1, Math.Min(MaximumWidth, (int)Math.Floor(width * scale)));
                int pixelHeight = Math.Max(1, Math.Min(MaximumHeight, (int)Math.Floor(height * scale)));
                if (pixelWidth == previousWidth && pixelHeight == previousHeight) { break; }
                previousWidth = pixelWidth;
                previousHeight = pixelHeight;

                var drawing = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.HighQuality);
                var bounds = new Rect(0, 0, pixelWidth, pixelHeight);
                using (DrawingContext context = drawing.RenderOpen())
                {
                    // Keep transparent areas readable, without altering the live UI.
                    context.DrawRectangle(Brushes.White, null, bounds);
                    if (background != null) { context.DrawRectangle(background, null, bounds); }
                    context.DrawRectangle(pageBrush, null, bounds);
                }
                var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = new MemoryStream())
                {
                    encoder.Save(output);
                    if (output.Length <= MaximumBytes) { return output.ToArray(); }
                }
                // PNG is lossless. Reduce the entire page uniformly when necessary;
                // never crop text, cards or the footer to meet the server limit.
                scale *= 0.75;
            }
            throw new InvalidOperationException("Не удалось сохранить фото главной страницы размером до 2 MB.");
        }

        private static bool PositiveFinite(double value)
        {
            return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }
}
