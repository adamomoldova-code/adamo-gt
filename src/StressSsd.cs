using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PcPulse
{
    internal static partial class StressWorkloads
    {
        internal const int SsdStressBlockBytes = 1024 * 1024;
        internal const long StressSsdFileBytes = 256L * 1024 * 1024;
        internal const long StressSsdWriteLimitBytes = 8L * 1024 * 1024 * 1024;

        internal static void RunSsd(StressOptions options, Action<StressProgress> report, CancellationToken token)
        {
            if (options == null) { throw new ArgumentNullException("options"); }
            if (options.DurationSeconds < 1 || options.DurationSeconds > 900) { throw new ArgumentOutOfRangeException("options.DurationSeconds"); }
            token.ThrowIfCancellationRequested();
            if (options.Disk == null) { throw new InvalidOperationException("Накопитель не выбран."); }
            // This validates free space (including the 1 GB reserve), disk identity and
            // the volume's physical extents before any temporary file is created.
            long writes = 0, transferred = 0, verified = 0;
            using (StorageTopology.BenchmarkTarget target = StorageTopology.OpenBenchmarkTarget(options.Disk, StressSsdFileBytes))
            using (SsdScratch scratch = SsdScratch.Create(target.VolumeRoot, true))
            using (SsdIo io = new SsdIo(scratch, delegate
            {
                if (report != null) { report(new StressProgress { Component = StressComponent.Ssd, Iterations = verified,
                    BytesProcessed = transferred, WrittenBytes = writes,
                    Detail = "Остановка SSD: ожидание завершения операции драйвером Windows…" }); }
            }))
            {
                int blockCount = (int)(StressSsdFileBytes / SsdStressBlockBytes);
                uint[] generations = new uint[blockCount];
                int block = 0;
                Stopwatch clock = Stopwatch.StartNew();
                long lastReport = -1000;
                do
                {
                    token.ThrowIfCancellationRequested();
                    bool write = writes < StressSsdWriteLimitBytes;
                    if (write)
                    {
                        generations[block]++;
                        io.PrepareExpected(block, generations[block]);
                        io.Transfer(true, (long)block * SsdStressBlockBytes, token);
                        writes += SsdStressBlockBytes;
                        transferred += SsdStressBlockBytes;
                    }
                    else { io.PrepareExpected(block, generations[block]); }
                    // Always verify the block just written, even when the requested
                    // duration expires between writing and reading that one block.
                    io.Transfer(false, (long)block * SsdStressBlockBytes, token);
                    transferred += SsdStressBlockBytes;
                    io.Verify(block);
                    verified++;
                    block = (block + 1) % blockCount;
                    if (clock.ElapsedMilliseconds - lastReport >= 250)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        ReportSsd(report, verified, transferred, writes, write);
                    }
                }
                while (clock.Elapsed.TotalSeconds < options.DurationSeconds);
                token.ThrowIfCancellationRequested();
                ReportSsd(report, verified, transferred, writes, writes < StressSsdWriteLimitBytes);
            }
        }

        private static void ReportSsd(Action<StressProgress> report, long verified, long bytes, long written, bool writing)
        {
            if (report == null) { return; }
            report(new StressProgress
            {
                Component = StressComponent.Ssd,
                Iterations = verified,
                BytesProcessed = bytes,
                WrittenBytes = written,
                Detail = (writing ? "Запись + проверка чтением" : "Лимит записи достигнут · проверка чтением")
                    + " · без кэша Windows · записано " + (written / 1048576).ToString(CultureInfo.InvariantCulture)
                    + " MB · проверено блоков: " + verified.ToString(CultureInfo.InvariantCulture)
            });
        }

        // The base pattern is deterministic and not a zero-filled/compressible file.
        // Every block also contains its address and rewrite generation, so a stale
        // or misplaced block cannot pass merely because payload bytes are equal.
        internal static byte[] CreateSsdPattern(int bytes)
        { return CreateSsdPattern(bytes, 1); }

        internal static byte[] CreateSsdPattern(int bytes, uint generation)
        {
            if (bytes < 16 || bytes > SsdStressBlockBytes || generation == 0) { throw new ArgumentOutOfRangeException("bytes"); }
            byte[] result = new byte[bytes];
            uint state = 0xA3D19F47u ^ unchecked(generation * 0x9E3779B9u);
            if (state == 0) { state = 1; }
            for (int index = 0; index < result.Length; index++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                result[index] = (byte)state;
            }
            return result;
        }

        internal static void SetSsdPatternIdentity(IntPtr buffer, int bytes, int block, uint generation)
        {
            if (buffer == IntPtr.Zero || bytes < 512 || bytes > SsdStressBlockBytes || bytes % 512 != 0 || block < 0 || generation == 0)
            { throw new ArgumentException("Invalid SSD pattern identity."); }
            for (int offset = 0; offset < bytes; offset += 512)
            {
                Marshal.WriteInt32(buffer, offset, unchecked((int)0xADAD0F17));
                Marshal.WriteInt32(buffer, offset + 4, block);
                Marshal.WriteInt32(buffer, offset + 8, unchecked((int)generation));
                Marshal.WriteInt32(buffer, offset + 12, offset);
            }
        }

        internal static void VerifySsdPattern(IntPtr expected, IntPtr actual, int bytes, int block)
        {
            if (expected == IntPtr.Zero || actual == IntPtr.Zero || bytes < 16 || bytes > SsdStressBlockBytes)
            { throw new ArgumentException("Invalid SSD verification buffer."); }
            if (SsdMemcmp(expected, actual, new UIntPtr((uint)bytes)) != 0)
            { throw new InvalidDataException("Ошибка целостности SSD: прочитанные данные отличаются от записанных, блок " + block.ToString(CultureInfo.InvariantCulture) + "."); }
        }

        // A random CREATE_NEW file is the only owned filesystem object. An exclusive
        // handle prevents replacement while open. DELETE_ON_CLOSE deletes by handle,
        // including on cancellation/error; no recursive deletion or path fallback.
        internal sealed class SsdScratch : IDisposable
        {
            internal readonly string FilePath;
            internal readonly SafeFileHandle Handle;
            private SsdScratch(string path, SafeFileHandle handle) { FilePath = path; Handle = handle; }

            internal static SsdScratch Create(string directory, bool unbuffered)
            {
                if (string.IsNullOrWhiteSpace(directory)) { throw new ArgumentException("Scratch directory is required."); }
                string parent = Path.GetFullPath(directory);
                if (parent.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(parent))
                { throw new IOException("Для тестового файла нужен существующий локальный каталог."); }
                for (DirectoryInfo item = new DirectoryInfo(parent); item != null; item = item.Parent)
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    { throw new IOException("Путь тестового файла не может проходить через ссылку."); }
                }
                string path = Path.Combine(parent, "ADAMO-GT-stress-" + Guid.NewGuid().ToString("N") + ".tmp");
                uint flags = 0x80 | 0x40000000 | 0x04000000 | 0x00200000; // Normal, overlapped, delete-on-close, open-reparse-point.
                if (unbuffered) { flags |= 0x20000000 | 0x80000000; }
                SafeFileHandle handle = SsdCreateFile(path, 0xC0010000, 0, IntPtr.Zero, 1, flags, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error(); handle.Dispose();
                    throw SsdNativeError("Создать отдельный временный файл SSD", error);
                }
                var scratch = new SsdScratch(path, handle);
                try
                {
                    SsdBasicInformation information;
                    if (!SsdGetBasicInformation(handle, 0, out information, (uint)Marshal.SizeOf(typeof(SsdBasicInformation))))
                    { throw SsdNativeError("Проверить свойства временного файла", Marshal.GetLastWin32Error()); }
                    if ((information.Attributes & (uint)(FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0)
                    { throw new IOException("Тест SSD недоступен для сжатого, разреженного, EFS-зашифрованного файла или ссылки."); }
                    return scratch;
                }
                catch { scratch.Dispose(); throw; }
            }

            public void Dispose() { Handle.Dispose(); }
        }

        private sealed class SsdIo : IDisposable
        {
            private readonly SsdScratch scratch;
            private readonly Action waitingForDriver;
            private IntPtr allocation;
            private IntPtr expected;
            private IntPtr actual;
            private IntPtr overlapped;
            private byte[] pattern;
            private uint preparedGeneration;
            private readonly ManualResetEvent completed = new ManualResetEvent(false);

            internal SsdIo(SsdScratch value, Action waiting)
            {
                scratch = value;
                waitingForDriver = waiting;
                try
                {
                    SsdStorageInformation storage;
                    if (!SsdGetStorageInformation(scratch.Handle, 16, out storage, (uint)Marshal.SizeOf(typeof(SsdStorageInformation))))
                    { throw SsdNativeError("Определить выравнивание секторов SSD", Marshal.GetLastWin32Error()); }
                    uint alignment = Math.Max(4096u, Math.Max(storage.LogicalBytesPerSector,
                        Math.Max(storage.PhysicalBytesPerSectorForAtomicity, Math.Max(storage.PhysicalBytesPerSectorForPerformance,
                        storage.FileSystemEffectivePhysicalBytesPerSectorForAtomicity))));
                    if (storage.LogicalBytesPerSector == 0 || alignment > SsdStressBlockBytes
                        || SsdStressBlockBytes % alignment != 0 || SsdStressBlockBytes % storage.LogicalBytesPerSector != 0)
                    { throw new IOException("Выравнивание секторов не поддерживает тест SSD без кэша."); }
                    allocation = SsdVirtualAlloc(IntPtr.Zero, new UIntPtr((uint)(2 * SsdStressBlockBytes) + alignment), 0x3000, 4);
                    if (allocation == IntPtr.Zero) { throw SsdNativeError("Выделить буферы SSD", Marshal.GetLastWin32Error()); }
                    long address = allocation.ToInt64();
                    expected = new IntPtr(checked(address + ((alignment - address % alignment) % alignment)));
                    actual = new IntPtr(expected.ToInt64() + SsdStressBlockBytes);
                    overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SsdOverlapped)));
                }
                catch { Dispose(); throw; }
            }

            internal void PrepareExpected(int block, uint generation)
            {
                if (pattern == null || preparedGeneration != generation)
                { pattern = CreateSsdPattern(SsdStressBlockBytes, generation); preparedGeneration = generation; }
                Marshal.Copy(pattern, 0, expected, pattern.Length);
                SetSsdPatternIdentity(expected, pattern.Length, block, generation);
            }
            internal void Verify(int block) { VerifySsdPattern(expected, actual, SsdStressBlockBytes, block); }

            internal void Transfer(bool write, long offset, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                WaitHandle[] waitHandles = new WaitHandle[] { completed, token.WaitHandle };
                completed.Reset();
                var operation = new SsdOverlapped
                {
                    Offset = unchecked((uint)offset), OffsetHigh = unchecked((uint)(offset >> 32)),
                    Event = completed.SafeWaitHandle.DangerousGetHandle()
                };
                Marshal.StructureToPtr(operation, overlapped, false);
                uint bytes;
                bool success = write
                    ? SsdWriteFile(scratch.Handle, expected, SsdStressBlockBytes, out bytes, overlapped)
                    : SsdReadFile(scratch.Handle, actual, SsdStressBlockBytes, out bytes, overlapped);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                if (!success && error != 997) { throw SsdNativeError(write ? "Записать блок SSD" : "Прочитать блок SSD", error); }
                if (!success)
                {
                    int selected = WaitHandle.WaitAny(waitHandles, 10000);
                    if (selected != 0)
                    {
                        SsdCancelIoEx(scratch.Handle, overlapped);
                        if (!completed.WaitOne(2000))
                        {
                            // A faulty driver may ignore CancelIoEx. The worker keeps
                            // the stress admission and all native resources until the
                            // kernel completes; the UI remains responsive and reports
                            // that it is stopping. Starting another test is prohibited.
                            try { if (waitingForDriver != null) { waitingForDriver(); } } catch { }
                            completed.WaitOne();
                        }
                        SsdGetOverlappedResult(scratch.Handle, overlapped, out bytes, false);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("Накопитель не ответил за 10 секунд; тест SSD остановлен.");
                    }
                }
                if (!SsdGetOverlappedResult(scratch.Handle, overlapped, out bytes, false))
                { throw SsdNativeError(write ? "Завершить запись SSD" : "Завершить чтение SSD", Marshal.GetLastWin32Error()); }
                if (bytes != SsdStressBlockBytes) { throw new IOException("SSD вернул неполный блок; тест остановлен."); }
                token.ThrowIfCancellationRequested();
            }

            private void ReleaseBuffers()
            {
                if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
                if (allocation != IntPtr.Zero) { SsdVirtualFree(allocation, UIntPtr.Zero, 0x8000); allocation = IntPtr.Zero; }
                completed.Dispose();
            }
            public void Dispose() { ReleaseBuffers(); }
        }

        private static IOException SsdNativeError(string operation, int error)
        { return new IOException(operation + ": " + new Win32Exception(error).Message + " (код " + error.ToString(CultureInfo.InvariantCulture) + ")."); }

        [StructLayout(LayoutKind.Sequential)]
        private struct SsdBasicInformation
        {
            public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
            public uint Attributes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SsdStorageInformation
        {
            public uint LogicalBytesPerSector, PhysicalBytesPerSectorForAtomicity, PhysicalBytesPerSectorForPerformance;
            public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity, Flags, ByteOffsetForSectorAlignment, ByteOffsetForPartitionAlignment;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SsdOverlapped
        {
            public UIntPtr Internal, InternalHigh;
            public uint Offset, OffsetHigh;
            public IntPtr Event;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle SsdCreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool SsdGetBasicInformation(SafeFileHandle handle, int informationClass, out SsdBasicInformation information, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool SsdGetStorageInformation(SafeFileHandle handle, int informationClass, out SsdStorageInformation information, uint size);
        [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc", SetLastError = true)]
        private static extern IntPtr SsdVirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protection);
        [DllImport("kernel32.dll", EntryPoint = "VirtualFree", SetLastError = true)]
        private static extern bool SsdVirtualFree(IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
        private static extern bool SsdWriteFile(SafeFileHandle handle, IntPtr buffer, uint bytes, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
        private static extern bool SsdReadFile(SafeFileHandle handle, IntPtr buffer, uint bytes, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "GetOverlappedResult", SetLastError = true)]
        private static extern bool SsdGetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", EntryPoint = "CancelIoEx", SetLastError = true)]
        private static extern bool SsdCancelIoEx(SafeFileHandle handle, IntPtr overlapped);
        [DllImport("msvcrt.dll", EntryPoint = "memcmp", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SsdMemcmp(IntPtr first, IntPtr second, UIntPtr length);
    }
}
