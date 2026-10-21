using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmDashboardCaptureTests
    {
        public static void Run()
        {
            RejectsUnreadyAndWrongWindow();
            ExistingDashboardPixelsAndDialogIsolation();
            PreservesViewportAndScroll();
            BoundsLargeAndHighEntropyImages();
            RequiresDispatcherThread();
        }

        private static void RejectsUnreadyAndWrongWindow()
        {
            var dashboard = Program.LoadWindow();
            try { Reject(delegate { CrmDashboardCapture.Capture(dashboard); }, "Unopened dashboard cannot be captured"); }
            finally { dashboard.Close(); }
            var unready = new Grid();
            Reject(delegate { CrmDashboardCapture.CaptureElement(unready, Brushes.White); }, "Unarranged visual cannot be exported as an empty image");
            Window dialog = ShowOffscreen(new Window { Content = new PasswordBox { Password = "fixture-only" } }, 400, 200);
            try { Reject(delegate { CrmDashboardCapture.Capture(dialog); }, "Password dialog cannot be mistaken for the first page"); }
            finally { dialog.Close(); }
        }

        private static void ExistingDashboardPixelsAndDialogIsolation()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            dashboard.Render(new Snapshot
            {
                ComputerManufacturer = "ADAMO", ComputerModel = "Capture fixture", ComputerName = "PRIVATE-TOOLTIP-ONLY",
                CpuName = "Pixel fixture CPU", WindowsName = "Windows fixture", SensorStatus = "Fixture sensors"
            });
            ShowOffscreen(window, 1200, 760);
            Window dialog = null;
            try
            {
                var page = (FrameworkElement)window.Content;
                var cpuCard = (Border)window.FindName("CpuCard");
                cpuCard.Background = Brushes.Lime;
                window.UpdateLayout();
                BitmapSource initial = Decode(CrmDashboardCapture.Capture(window));
                Require(initial.PixelWidth > 0 && initial.PixelHeight > 0, "Existing first page is rendered");
                Point location = cpuCard.TranslatePoint(new Point(cpuCard.ActualWidth / 2, 5), page);
                Color marker = Pixel(initial, (int)(location.X / page.ActualWidth * initial.PixelWidth), (int)(location.Y / page.ActualHeight * initial.PixelHeight));
                Require(marker.G > 220 && marker.R < 35 && marker.B < 35, "Screenshot contains the live WPF card pixels, not a regenerated specification sheet");

                byte[] before = Pixels(initial);
                var password = new PasswordBox { Password = "fixture-only-password", Background = Brushes.Magenta, Foreground = Brushes.Magenta };
                dialog = new Window { Owner = window, Background = Brushes.Magenta, Content = password };
                ShowOffscreen(dialog, 1200, 760);
                BitmapSource during = Decode(CrmDashboardCapture.Capture(window));
                Require(Equal(before, Pixels(during)), "An owned password window does not enter the captured page");
                dialog.Close(); dialog = null;

                ((TextBlock)window.FindName("CpuName")).Text = "The visible CPU label changed";
                window.UpdateLayout();
                byte[] after = Pixels(Decode(CrmDashboardCapture.Capture(window)));
                Require(!Equal(before, after), "Changing the displayed first page changes the captured pixels");
                Require(ReferenceEquals(window.Content, page) && cpuCard.Background == Brushes.Lime, "Capture does not replace the visual tree or alter its contents");
                window.Hide();
                Reject(delegate { CrmDashboardCapture.Capture(window); }, "A hidden first page cannot be submitted");
            }
            finally { if (dialog != null) { dialog.Close(); } window.Close(); }
        }

        private static void PreservesViewportAndScroll()
        {
            var content = new StackPanel();
            content.Children.Add(new Border { Background = Brushes.Red, Height = 200 });
            content.Children.Add(new Border { Background = Brushes.Blue, Height = 200 });
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var root = new Grid { Background = Brushes.White };
            root.Children.Add(scroll);
            Arrange(root, 320, 150);
            Require(root.IsMeasureValid && root.IsArrangeValid, "Initial scroll fixture has a completed layout");
            BitmapSource first = Decode(CrmDashboardCapture.CaptureElement(root, Brushes.White));
            Require(Pixel(first, first.PixelWidth / 2, first.PixelHeight / 2).R > 240, "First viewport contains the visible red region");
            scroll.ScrollToVerticalOffset(200); root.UpdateLayout();
            Arrange(root, 320, 150);
            Require(root.IsMeasureValid && root.IsArrangeValid, "Scrolled fixture has a completed layout");
            double offset = scroll.VerticalOffset;
            BitmapSource second = Decode(CrmDashboardCapture.CaptureElement(root, Brushes.White));
            Require(Pixel(second, second.PixelWidth / 2, second.PixelHeight / 2).B > 240, "Current scroll position is faithfully captured");
            Require(scroll.VerticalOffset == offset, "Capture does not scroll or rearrange the source");
            Require(first.PixelWidth == second.PixelWidth && first.PixelHeight == second.PixelHeight, "Capture exports the viewport, not hidden scrolling content");
        }

        private static void BoundsLargeAndHighEntropyImages()
        {
            var page = new Grid { Background = Brushes.White };
            page.Children.Add(new Border { Width = 200, Height = 200, Background = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
            page.Children.Add(new Border { Width = 200, Height = 200, Background = Brushes.Blue, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            Arrange(page, 6000, 9000);
            byte[] large = CrmDashboardCapture.CaptureElement(page, Brushes.White);
            BitmapSource bounded = Decode(large);
            Require(bounded.PixelWidth <= 2400 && bounded.PixelHeight <= 4800 && large.Length <= 2 * 1024 * 1024, "Oversize visual fits all server limits");
            Require(Math.Abs((double)bounded.PixelWidth / bounded.PixelHeight - 6000.0 / 9000.0) < 0.001, "Large visual keeps its aspect ratio");
            Require(Pixel(bounded, 10, 10).R > 240 && Pixel(bounded, bounded.PixelWidth - 11, bounded.PixelHeight - 11).B > 240, "Both distant corners survive uniform downscale without cropping");

            const int width = 2200, height = 1200;
            byte[] noise = new byte[width * height * 4];
            new Random(271).NextBytes(noise);
            for (int i = 3; i < noise.Length; i += 4) { noise[i] = 255; }
            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, noise, width * 4);
            image.Freeze();
            var noisy = new Image { Source = image, Stretch = Stretch.Fill };
            Arrange(noisy, width, height);
            byte[] compressed = CrmDashboardCapture.CaptureElement(noisy, Brushes.White);
            BitmapSource downscaled = Decode(compressed);
            Require(compressed.Length <= 2 * 1024 * 1024 && downscaled.PixelWidth < width && downscaled.PixelHeight < height, "High-entropy PNG reduces resolution to meet the byte limit");
            Require(downscaled.PixelWidth > 100 && downscaled.PixelHeight > 100, "Byte-limit fallback keeps a useful full-page image");
        }

        private static void RequiresDispatcherThread()
        {
            var page = new Grid(); Arrange(page, 100, 100);
            Exception failure = null;
            var worker = new Thread(delegate()
            {
                try { CrmDashboardCapture.CaptureElement(page, Brushes.White); }
                catch (Exception error) { failure = error; }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            Require(worker.Join(5000), "Wrong-thread check terminates promptly");
            Require(failure is InvalidOperationException, "Capture requires the owning UI dispatcher thread");
        }

        private static Window ShowOffscreen(Window window, double width, double height)
        {
            window.Width = width; window.Height = height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000; window.Top = -20000;
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Show(); window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            return window;
        }

        private static void Arrange(FrameworkElement element, double width, double height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }

        private static BitmapSource Decode(byte[] png)
        {
            using (var stream = new MemoryStream(png))
            {
                var image = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                return converted;
            }
        }

        private static byte[] Pixels(BitmapSource image)
        {
            byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(bytes, image.PixelWidth * 4, 0);
            return bytes;
        }

        private static Color Pixel(BitmapSource image, int x, int y)
        {
            byte[] value = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), value, 4, 0);
            return Color.FromArgb(value[3], value[2], value[1], value[0]);
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) { return false; }
            for (int i = 0; i < left.Length; i++) { if (left[i] != right[i]) { return false; } }
            return true;
        }

        private static void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
