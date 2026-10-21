using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PcPulse
{
    public static class DiskBenchmark
    {
        private const int BlockBytes = 4 * 1024 * 1024;
        private const long MaximumTestBytes = 1024L * 1024 * 1024;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint CreateNew = 1;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileFlagWriteThrough = 0x80000000;
        private const uint FileFlagNoBuffering = 0x20000000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint FileFlagDeleteOnClose = 0x04000000;

        public static void Run(DiskSnapshot disk, Action<BenchmarkProgress> report, CancellationToken token)
        {
            if (BenchmarkSettings.Current.Profile == "quick-v1")
            {
                RunWithSize(disk, report, token, BenchmarkSettings.Current.DiskMiB * 1024L * 1024L);
            }
            else { DiskSpdBenchmark.Run(disk, report, token); }
        }

        internal static void RunWithSize(DiskSnapshot disk, Action<BenchmarkProgress> report, CancellationToken token, long totalBytes)
        {
            BenchmarkProgress progress = new BenchmarkProgress
            {
                DiskIndex = disk == null ? 0 : disk.Index,
                TestSizeBytes = totalBytes,
                BlockSizeBytes = BlockBytes,
                Engine = "ADAMO GT",
                EngineVersion = "1",
                Profile = "native-seq-v1",
                QueueDepth = 1,
                ThreadCount = 1,
                CacheMode = "Без кэша Windows; write-through",
                Method = "Последовательный · один поток · очередь 1 · без кэша Windows",
                Stage = "Подготовка последовательного теста",
                IsRunning = true
            };
            SafeFileHandle handle = null;
            StorageTopology.BenchmarkTarget target = null;
            IntPtr allocation = IntPtr.Zero;
            string testPath = null;
            bool created = false;
            Publish(report, progress);
            try
            {
                token.ThrowIfCancellationRequested();
                if (disk == null) { throw new InvalidOperationException("Накопитель не выбран."); }
                if (totalBytes <= 0 || totalBytes > MaximumTestBytes || totalBytes % BlockBytes != 0)
                {
                    throw new InvalidOperationException("Недопустимый объём тестового файла.");
                }
                target = StorageTopology.OpenBenchmarkTarget(disk, totalBytes);
                string volumeRoot = target.VolumeRoot;
                string directory = SelectDirectory(volumeRoot);
                testPath = Path.Combine(directory, "PcPulse-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                uint flags = FileAttributeNormal | FileFlagNoBuffering | FileFlagWriteThrough | FileFlagSequentialScan | FileFlagDeleteOnClose;
                handle = CreateFile(testPath, GenericRead | GenericWrite | DeleteAccess, 0, IntPtr.Zero, CreateNew, flags, IntPtr.Zero);
                if (handle.IsInvalid) { throw NativeError("Создать временный файл для теста"); }
                created = true;

                FileAttributes attributes = File.GetAttributes(testPath);
                if ((attributes & (FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0)
                {
                    throw new InvalidOperationException("Тест без кэша недоступен для сжатых, разреженных или EFS-зашифрованных файлов.");
                }

                FileStorageInfo storage;
                if (!GetFileInformationByHandleEx(handle, 16, out storage, (uint)Marshal.SizeOf(typeof(FileStorageInfo))))
                {
                    throw NativeError("Определить выравнивание для теста без кэша");
                }
                uint alignment = Math.Max(4096u, Math.Max(storage.LogicalBytesPerSector,
                    Math.Max(storage.PhysicalBytesPerSectorForAtomicity, Math.Max(storage.PhysicalBytesPerSectorForPerformance,
                    storage.FileSystemEffectivePhysicalBytesPerSectorForAtomicity))));
                if (storage.LogicalBytesPerSector == 0 || alignment > BlockBytes || BlockBytes % alignment != 0 || BlockBytes % storage.LogicalBytesPerSector != 0)
                {
                    throw new InvalidOperationException("Размер сектора не поддерживает выбранный тест без кэша.");
                }
                allocation = VirtualAlloc(IntPtr.Zero, new UIntPtr((uint)BlockBytes + alignment), 0x3000, 0x04);
                if (allocation == IntPtr.Zero) { throw NativeError("Выделить выровненный буфер"); }
                long address = allocation.ToInt64();
                long alignedAddress = checked(address + ((alignment - address % alignment) % alignment));
                IntPtr buffer = new IntPtr(alignedAddress);
                byte[] randomBlock = new byte[BlockBytes];
                long writeTicks = 0;
                long readTicks = 0;
                long lastReport = 0;

                progress.Stage = "Последовательная запись · без кэша Windows";
                Publish(report, progress);
                using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                {
                    for (long completed = 0; completed < totalBytes; completed += BlockBytes)
                    {
                        token.ThrowIfCancellationRequested();
                        random.GetBytes(randomBlock);
                        Marshal.Copy(randomBlock, 0, buffer, BlockBytes);
                        token.ThrowIfCancellationRequested();
                        uint written;
                        long started = Stopwatch.GetTimestamp();
                        bool success = WriteFile(handle, buffer, BlockBytes, out written, IntPtr.Zero);
                        int error = success ? 0 : Marshal.GetLastWin32Error();
                        writeTicks += Stopwatch.GetTimestamp() - started;
                        if (!success) { throw NativeError("Записать тестовый файл без кэша", error); }
                        if (written != BlockBytes) { throw new IOException("Накопитель записал неполный блок; результат теста не принят."); }
                        progress.Percent = 50d * (completed + BlockBytes) / totalBytes;
                        PublishOccasionally(report, progress, ref lastReport, completed + BlockBytes == totalBytes);
                    }
                }
                token.ThrowIfCancellationRequested();
                long flushStarted = Stopwatch.GetTimestamp();
                bool flushed = FlushFileBuffers(handle);
                int flushError = flushed ? 0 : Marshal.GetLastWin32Error();
                writeTicks += Stopwatch.GetTimestamp() - flushStarted;
                if (!flushed) { throw NativeError("Завершить запись тестового файла", flushError); }
                progress.WriteBytesPerSecond = Rate(totalBytes, writeTicks);
                token.ThrowIfCancellationRequested();
                long position;
                if (!SetFilePointerEx(handle, 0, out position, 0)) { throw NativeError("Перейти к началу тестового файла"); }

                progress.Stage = "Последовательное чтение · без кэша Windows";
                Publish(report, progress);
                for (long completed = 0; completed < totalBytes; completed += BlockBytes)
                {
                    token.ThrowIfCancellationRequested();
                    uint read;
                    long started = Stopwatch.GetTimestamp();
                    bool success = ReadFile(handle, buffer, BlockBytes, out read, IntPtr.Zero);
                    int error = success ? 0 : Marshal.GetLastWin32Error();
                    readTicks += Stopwatch.GetTimestamp() - started;
                    if (!success) { throw NativeError("Прочитать тестовый файл без кэша", error); }
                    if (read != BlockBytes) { throw new IOException("Накопитель вернул неполный блок; результат теста не принят."); }
                    progress.Percent = 50d + 50d * (completed + BlockBytes) / totalBytes;
                    PublishOccasionally(report, progress, ref lastReport, completed + BlockBytes == totalBytes);
                }
                token.ThrowIfCancellationRequested();
                progress.ReadBytesPerSecond = Rate(totalBytes, readTicks);
                progress.Percent = 100;
                progress.Stage = "Готово · последовательный тест " + (totalBytes / 1048576) + " MB";
            }
            catch (OperationCanceledException)
            {
                progress.Stage = "Тест остановлен";
                progress.ReadBytesPerSecond = null;
                progress.WriteBytesPerSecond = null;
            }
            catch (Exception exception)
            {
                progress.Stage = "Тест недоступен";
                progress.Error = exception.Message;
                progress.ReadBytesPerSecond = null;
                progress.WriteBytesPerSecond = null;
            }
            finally
            {
                if (handle != null) { handle.Dispose(); }
                if (target != null) { target.Dispose(); }
                if (allocation != IntPtr.Zero) { VirtualFree(allocation, UIntPtr.Zero, 0x8000); }
                if (created && testPath != null && File.Exists(testPath))
                {
                    progress.Stage = "Ожидание удаления временного файла";
                    progress.Error = "Windows ещё не удалила тестовый файл: " + testPath;
                }
                progress.IsRunning = false;
                Publish(report, progress);
            }
        }

        private static string SelectDirectory(string volumeRoot)
        {
            string temporary = Path.GetFullPath(Path.GetTempPath());
            if (string.Equals(Path.GetPathRoot(temporary), volumeRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(temporary))
            {
                return temporary;
            }
            return volumeRoot;
        }

        private static double Rate(long bytes, long ticks)
        {
            if (ticks <= 0) { throw new InvalidOperationException("Недостаточная длительность измерения скорости."); }
            return bytes * (double)Stopwatch.Frequency / ticks;
        }

        private static void PublishOccasionally(Action<BenchmarkProgress> report, BenchmarkProgress progress, ref long lastReport, bool force)
        {
            long now = Stopwatch.GetTimestamp();
            if (force || now - lastReport >= Stopwatch.Frequency / 5)
            {
                lastReport = now;
                Publish(report, progress);
            }
        }

        private static void Publish(Action<BenchmarkProgress> report, BenchmarkProgress progress)
        {
            if (report == null) { return; }
            BenchmarkProgress copy = progress.Copy();
            try { report(copy); }
            catch (Exception) { }
        }

        private static Exception NativeError(string operation)
        {
            return NativeError(operation, Marshal.GetLastWin32Error());
        }

        private static Exception NativeError(string operation, int error)
        {
            if (error == 5 || error == 19)
            {
                return new IOException(operation + ": том защищён от записи или недостаточно прав.", new Win32Exception(error));
            }
            return new IOException(operation + ": " + new Win32Exception(error).Message + " (код " + error + ").");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileStorageInfo
        {
            public uint LogicalBytesPerSector;
            public uint PhysicalBytesPerSectorForAtomicity;
            public uint PhysicalBytesPerSectorForPerformance;
            public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
            public uint Flags;
            public uint ByteOffsetForSectorAlignment;
            public uint ByteOffsetForPartitionAlignment;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileStorageInfo information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint bytes, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint bytes, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushFileBuffers(SafeFileHandle file);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint method);
    }
}
