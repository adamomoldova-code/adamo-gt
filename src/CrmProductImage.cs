using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcPulse
{
    public static class CrmProductImage
    {
        // Render the reviewed product data, never capture the user's desktop.
        public static byte[] RenderPng(string name, string description)
        {
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(description))
                throw new ArgumentException("Для фото нужны название и характеристики.");
            if (name.Length > 255 || description.Length > 16000)
                throw new ArgumentException("Сократите текст характеристик для фото (до 16 000 символов).");
            var body = new StackPanel { Margin = new Thickness(60, 44, 60, 38) };
            BitmapSource logo = BrandAssets.LoadLogo();
            if (logo != null) { body.Children.Add(new Image { Source = logo, Width = 52, Height = 52,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), Stretch = Stretch.Uniform }); }
            body.Children.Add(new TextBlock { Text = "ADAMO.MD  /  ХАРАКТЕРИСТИКИ", FontSize = 21,
                FontWeight = FontWeights.SemiBold, Foreground = Brush("#34744F"), Margin = new Thickness(0, 0, 0, 24) });
            body.Children.Add(new TextBlock { Text = name, FontSize = 34, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24), Foreground = Brush("#16232C") });
            body.Children.Add(new Border { Height = 2, Background = Brush("#DFE6E1"), Margin = new Thickness(0, 0, 0, 22) });
            body.Children.Add(new TextBlock { Text = description, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 21, LineHeight = 30, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#24333D") });
            body.Children.Add(new Border { Height = 1, Background = Brush("#DFE6E1"), Margin = new Thickness(0, 26, 0, 18) });
            body.Children.Add(new TextBlock { Text = "Данные ADAMO GT · Проверено оператором перед отправкой\nCreat cu ❤️ în Moldova | ADAMO.MD",
                FontSize = 17, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#52616B") });
            var page = new Border { Width = 1200, Background = Brushes.White, Child = body };
            page.Measure(new Size(1200, Double.PositiveInfinity));
            int height = (int)Math.Ceiling(page.DesiredSize.Height);
            if (height > 4800) { throw new ArgumentException("Текст не помещается на фото. Сократите характеристики."); }
            page.Arrange(new Rect(0, 0, 1200, height));
            page.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1200, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                byte[] bytes = output.ToArray();
                if (bytes.Length > 2 * 1024 * 1024) { throw new ArgumentException("Фото превышает 2 MB. Сократите текст."); }
                return bytes;
            }
        }

        private static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
    }
}
