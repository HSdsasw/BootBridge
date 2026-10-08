using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace IsoWriter;

internal static class NativeDisk
{
    internal const uint LockVolume = 0x00090018, DismountVolume = 0x00090020;
    private const uint Extents = 0x00560000;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolume(StringBuilder name, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolume(IntPtr find, StringBuilder name, int length);
    [DllImport("kernel32.dll")]
    private static extern bool FindVolumeClose(IntPtr find);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int length, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, int length, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    internal static IOException Error(string action) => new($"{action}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    internal static SafeFileHandle Open(string path, bool write, bool raw = false)
    {
        var handle = CreateFile(path.TrimEnd('\\'), write ? 0xC0000000u : 0u, 3, IntPtr.Zero, 3,
            raw ? 0xA0000000u : 0u, IntPtr.Zero); // NO_BUFFERING + WRITE_THROUGH
        if (!handle.IsInvalid) return handle;
        var error = Error("Не удалось открыть " + path);
        handle.Dispose();
        throw error;
    }

    internal static byte[] Control(SafeFileHandle handle, uint code, int outputSize = 0)
    {
        byte[]? bytes = outputSize == 0 ? null : new byte[outputSize];
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, bytes, outputSize, out _, IntPtr.Zero))
            throw Error($"Ошибка операции с диском 0x{code:X}");
        return bytes ?? [];
    }

    internal static int[] DiskNumbers(SafeFileHandle volume)
    {
        byte[] bytes = Control(volume, Extents, 65536);
        int count = BitConverter.ToInt32(bytes, 0);
        if (count < 1 || count > (bytes.Length - 8) / 24) throw new IOException("Не удалось определить физические диски тома.");
        return Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(bytes, 8 + i * 24)).ToArray();
    }

    internal static int[] SourceDisks(FileStream source)
    {
        var path = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(source.SafeFileHandle, path, (uint)path.Capacity, 1);
        if (size == 0 || size >= path.Capacity) throw Error("Не удалось определить том ISO");
        string finalPath = path.ToString();
        int end = finalPath.IndexOf('}');
        if (!finalPath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
            throw new IOException("ISO должен находиться на локальном томе с определяемым физическим диском.");
        using var volume = Open(finalPath[..(end + 1)], false);
        return DiskNumbers(volume);
    }

    internal static List<string> VolumesOnDisk(int diskNumber)
    {
        var result = new List<string>();
        var name = new StringBuilder(1024);
        IntPtr search = FindFirstVolume(name, name.Capacity);
        if (search == new IntPtr(-1)) throw Error("Не удалось перечислить тома");
        try
        {
            do
            {
                string path = name.ToString();
                using var volume = Open(path, false);
                var bytes = new byte[65536];
                if (!DeviceIoControl(volume, Extents, IntPtr.Zero, 0, bytes, bytes.Length, out _, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    // Optical drives / empty removable readers have no disk extents.
                    if (error is not (1 or 21 or 50)) throw Error("Не удалось проверить том " + path);
                }
                else
                {
                    int count = BitConverter.ToInt32(bytes, 0);
                    if (count < 1 || count > (bytes.Length - 8) / 24) throw new IOException("Некорректная карта тома.");
                    var disks = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(bytes, 8 + 24 * i)).ToArray();
                    if (disks.Contains(diskNumber))
                    {
                        if (count != 1) throw new IOException("Составные и динамические тома не поддерживаются.");
                        result.Add(path);
                    }
                }
                name.Clear();
                if (FindNextVolume(search, name, name.Capacity)) continue;
                if (Marshal.GetLastWin32Error() != 18) throw Error("Ошибка перечисления томов");
                break;
            } while (true);
        }
        finally { FindVolumeClose(search); }
        return result;
    }

    internal static long Length(SafeFileHandle disk) => BitConverter.ToInt64(Control(disk, 0x0007405C, 8));
    internal static int Number(SafeFileHandle disk) => BitConverter.ToInt32(Control(disk, 0x002D1080, 12), 4);
}

// A page-aligned buffer is required by unbuffered raw disk I/O. All transfers are sector-aligned.
internal sealed class RawDisk : IDisposable, IImageDevice
{
    private readonly SafeFileHandle handle;
    private readonly IntPtr buffer;
    public const int BufferSize = 4 * 1024 * 1024;
    internal RawDisk(SafeFileHandle handle)
    {
        this.handle = handle;
        buffer = NativeDisk.VirtualAlloc(IntPtr.Zero, BufferSize, 0x3000, 4);
        if (buffer == IntPtr.Zero) throw NativeDisk.Error("Не удалось выделить буфер");
    }
    public void Seek(long position)
    {
        if (position < 0 || position % 512 != 0) throw new ArgumentOutOfRangeException(nameof(position));
        if (!NativeDisk.SetFilePointerEx(handle, position, out long actual, 0) || actual != position)
            throw NativeDisk.Error("Не удалось установить позицию диска");
    }
    public void Write(byte[] bytes, int count)
    {
        CheckCount(count);
        Marshal.Copy(bytes, 0, buffer, count);
        if (!NativeDisk.WriteFile(handle, buffer, count, out int written, IntPtr.Zero) || written != count)
            throw NativeDisk.Error("Ошибка записи диска");
    }
    public void Read(byte[] bytes, int count)
    {
        CheckCount(count);
        if (!NativeDisk.ReadFile(handle, buffer, count, out int read, IntPtr.Zero) || read != count)
            throw NativeDisk.Error("Ошибка чтения диска");
        Marshal.Copy(buffer, bytes, 0, count);
    }
    private static void CheckCount(int count)
    {
        if (count < 1 || count > BufferSize || count % 512 != 0) throw new ArgumentOutOfRangeException(nameof(count));
    }
    public void Flush()
    {
        if (!NativeDisk.FlushFileBuffers(handle)) throw NativeDisk.Error("Не удалось сбросить кеш записи");
    }
    public void Dispose() => NativeDisk.VirtualFree(buffer, 0, 0x8000);
}
