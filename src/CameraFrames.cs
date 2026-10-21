using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcPulse
{
    // Frames live only in two bounded RAM buffers. Nothing is recorded or transmitted.
    // GDI avoids DirectDraw overlays used by the default legacy video renderer, which
    // can leave a running graph black with older display/Boot Camp drivers.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class CameraFrameReceiver : ICameraSampleCallback
    {
        private readonly object gate = new object();
        private readonly CameraBitmapInfo bitmap;
        private readonly int requiredBytes;
        private byte[] latest, display;
        private bool stopped, hasFrame, dark;
        private long lastFrameTick, count;
        private string failure;
        internal long PaintedCount { get; private set; }
        internal string Failure { get { lock (gate) { return failure; } } }

        internal CameraFrameReceiver(CameraBitmapInfo format)
        {
            requiredBytes = RequiredBytes(format);
            bitmap = format;
            bitmap.Size = 40; bitmap.SizeImage = requiredBytes;
            latest = new byte[requiredBytes]; display = new byte[requiredBytes];
        }

        internal static int RequiredBytes(CameraBitmapInfo format)
        {
            if (format.Width < 1 || format.Width > 4096 || format.Height == 0 || format.Height < -2160 || format.Height > 2160
                || format.Planes != 1 || (format.BitCount != 24 && format.BitCount != 32) || format.Compression != 0)
            { throw new InvalidOperationException("Драйвер камеры вернул неподдерживаемый формат изображения."); }
            return checked(((format.Width * format.BitCount + 31) / 32) * 4 * Math.Abs(format.Height));
        }

        public int SampleCB(double time, IntPtr sample) { return 0; }
        public int BufferCB(double time, IntPtr buffer, int length)
        {
            // Streaming thread: no UI, COM, waits, file I/O or allocation per frame.
            if (buffer == IntPtr.Zero || length < requiredBytes) { return 0; }
            try
            {
                lock (gate)
                {
                    if (stopped) { return 0; }
                    Marshal.Copy(buffer, latest, 0, requiredBytes);
                    dark = IsDark(latest, bitmap.Width, Math.Abs(bitmap.Height), bitmap.BitCount);
                    hasFrame = true; count++; lastFrameTick = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception) { lock (gate) { failure = "Не удалось обработать кадр камеры."; } return unchecked((int)0x80004005); }
            return 0;
        }

        internal bool HasFrame { get { lock (gate) { return hasFrame; } } }
        internal long Count { get { lock (gate) { return count; } } }
        internal bool IsDarkFrame { get { lock (gate) { return hasFrame && dark; } } }
        internal bool Stalled(double seconds)
        {
            lock (gate) { return hasFrame && (Stopwatch.GetTimestamp() - lastFrameTick) / (double)Stopwatch.Frequency > seconds; }
        }
        internal void Stop()
        {
            lock (gate)
            {
                stopped = true; hasFrame = false;
                Array.Clear(latest, 0, latest.Length); Array.Clear(display, 0, display.Length);
            }
        }
        internal byte[] CopyFrame()
        {
            lock (gate)
            {
                if (!hasFrame || stopped) { return null; }
                Buffer.BlockCopy(latest, 0, display, 0, requiredBytes);
                return display;
            }
        }
        internal static bool IsDark(byte[] pixels, int width, int height, int bitCount)
        {
            int bytesPerPixel = bitCount / 8;
            int stride = ((width * bitCount + 31) / 32) * 4;
            // Sparse grid ignores padding and alpha, and does not mistake a few bright
            // pixels in an otherwise black image for a usable camera picture.
            int bright = 0, samples = 0;
            for (int y = 0; y < height; y += Math.Max(1, height / 16))
            for (int x = 0; x < width; x += Math.Max(1, width / 16))
            {
                int index = y * stride + x * bytesPerPixel;
                if (pixels[index] > 12 || pixels[index + 1] > 12 || pixels[index + 2] > 12) { bright++; }
                samples++;
            }
            return bright <= samples / 100;
        }

        internal void Paint(IntPtr window, int width, int height)
        {
            byte[] frame = CopyFrame();
            if (frame == null || width < 1 || height < 1 || !IsWindowVisible(window)) { return; }
            int x, y, outputWidth, outputHeight;
            DirectShowCameraSession.FitImage(width, height, bitmap.Width, Math.Abs(bitmap.Height), out x, out y, out outputWidth, out outputHeight);
            IntPtr dc = GetDC(window);
            if (dc == IntPtr.Zero) { throw new InvalidOperationException("Не удалось получить область изображения камеры."); }
            try
            {
                // Preserve the signed height: positive DIBs are bottom-up, negative top-down.
                var info = bitmap;
                // Clear only letterbox bars, so each redraw does not flash black.
                if (x > 0) { PatBlt(dc, 0, 0, x, height, 0x00000042); }
                if (x + outputWidth < width) { PatBlt(dc, x + outputWidth, 0, width - x - outputWidth, height, 0x00000042); }
                if (y > 0) { PatBlt(dc, x, 0, outputWidth, y, 0x00000042); }
                if (y + outputHeight < height) { PatBlt(dc, x, y + outputHeight, outputWidth, height - y - outputHeight, 0x00000042); }
                SetStretchBltMode(dc, 3); // COLORONCOLOR; no GPU overlay required.
                int result = StretchDIBits(dc, x, y, outputWidth, outputHeight, 0, 0, bitmap.Width, Math.Abs(bitmap.Height), frame, ref info, 0, 0x00CC0020);
                if (result == 0 || result == -1) { throw new InvalidOperationException("Windows не смогла вывести полученное изображение камеры."); }
                PaintedCount++;
            }
            finally { ReleaseDC(window, dc); }
        }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint operation);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, [In] byte[] pixels, ref CameraBitmapInfo bitmap, uint usage, uint operation);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraBitmapInfo
    {
        internal int Size, Width, Height;
        internal short Planes, BitCount;
        internal int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraVideoInfo
    {
        internal int SourceLeft, SourceTop, SourceRight, SourceBottom, TargetLeft, TargetTop, TargetRight, TargetBottom;
        internal int BitRate, BitErrorRate;
        internal long AverageTimePerFrame;
        internal CameraBitmapInfo Bitmap;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraMediaType
    {
        internal Guid MajorType, SubType;
        [MarshalAs(UnmanagedType.Bool)] internal bool FixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] internal bool TemporalCompression;
        internal int SampleSize;
        internal Guid FormatType;
        internal IntPtr Unknown;
        internal int FormatSize;
        internal IntPtr Format;
        internal void Free()
        {
            if (Format != IntPtr.Zero) { Marshal.FreeCoTaskMem(Format); Format = IntPtr.Zero; }
            if (Unknown != IntPtr.Zero) { Marshal.Release(Unknown); Unknown = IntPtr.Zero; }
        }
    }
    [ComVisible(true), Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICameraSampleCallback
    {
        [PreserveSig] int SampleCB(double time, IntPtr sample);
        [PreserveSig] int BufferCB(double time, IntPtr buffer, int length);
    }
    [ComImport, Guid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraSampleGrabber
    {
        [PreserveSig] int SetOneShot([MarshalAs(UnmanagedType.Bool)] bool oneShot);
        [PreserveSig] int SetMediaType([In] ref CameraMediaType mediaType);
        [PreserveSig] int GetConnectedMediaType(out CameraMediaType mediaType);
        [PreserveSig] int SetBufferSamples([MarshalAs(UnmanagedType.Bool)] bool bufferSamples);
        [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
        [PreserveSig] int GetCurrentSample(out IntPtr sample);
        [PreserveSig] int SetCallback([MarshalAs(UnmanagedType.Interface)] ICameraSampleCallback callback, int whichMethod);
    }
    [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraStreamConfig
    {
        [PreserveSig] int SetFormat([In] ref CameraMediaType mediaType);
        [PreserveSig] int GetFormat(out IntPtr mediaType);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mediaType, IntPtr capabilities);
    }
}
