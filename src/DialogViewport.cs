using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PcPulse
{
    internal static class DialogViewport
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        internal static Rect WorkArea(Window owner)
        {
            if (owner != null)
            {
                IntPtr handle = new WindowInteropHelper(owner).Handle;
                if (handle != IntPtr.Zero)
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    var source = HwndSource.FromHwnd(handle);
                    if (source != null && source.CompositionTarget != null
                        && GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
                    {
                        var pixels = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                        Rect logical = Rect.Transform(pixels, source.CompositionTarget.TransformFromDevice);
                        if (Valid(logical)) { return logical; }
                    }
                }
            }
            return SystemParameters.WorkArea;
        }

        internal static void Fit(Window dialog, Rect workArea)
        {
            if (!Valid(workArea)) { return; }
            // Keep a margin when space allows; never impose a minimum larger than
            // the available monitor. Long help text remains in the ScrollViewer.
            double width = Math.Max(1, workArea.Width - Math.Min(32, workArea.Width / 10));
            double height = Math.Max(1, workArea.Height - Math.Min(32, workArea.Height / 10));
            dialog.MinWidth = Math.Min(dialog.MinWidth, width);
            dialog.MinHeight = Math.Min(dialog.MinHeight, height);
            dialog.MaxWidth = Math.Min(dialog.MaxWidth, width);
            dialog.MaxHeight = Math.Min(dialog.MaxHeight, height);
            dialog.Width = Math.Min(double.IsNaN(dialog.Width) ? 700 : dialog.Width, dialog.MaxWidth);
        }

        internal static void Position(Window dialog, Rect workArea)
        {
            if (!Valid(workArea)) { return; }
            Fit(dialog, workArea);
            double width = Math.Min(dialog.ActualWidth > 0 ? dialog.ActualWidth : dialog.Width, workArea.Width);
            double height = Math.Min(dialog.ActualHeight > 0 ? dialog.ActualHeight : dialog.MaxHeight, workArea.Height);
            double left = double.IsNaN(dialog.Left) ? workArea.Left + (workArea.Width - width) / 2 : dialog.Left;
            double top = double.IsNaN(dialog.Top) ? workArea.Top + (workArea.Height - height) / 2 : dialog.Top;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - width));
            dialog.Top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - height));
        }

        private static bool Valid(Rect area)
        {
            return !area.IsEmpty && area.Width > 0 && area.Height > 0
                && Finite(area.X) && Finite(area.Y) && Finite(area.Width) && Finite(area.Height);
        }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
