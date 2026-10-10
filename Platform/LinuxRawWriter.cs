using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IsoWriter.Platform;

internal static class LinuxRawWriter
{
    internal static async Task<WriteResult> WriteAsync(Disk selected, string iso, IProgress<WriteProgress> progress, CancellationToken token)
    {
        LinuxNative.RequireRoot(); LinuxStorage.CheckDevice(selected.LinuxDevice);
        using var source = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
        IsoImage.Validate(source);
        var current = (await LinuxStorage.ReadDisksAsync(token)).SingleOrDefault(d => d.LinuxDevice == selected.LinuxDevice) ?? throw new IOException("The disk was disconnected.");
        Safety.Validate(selected, current, source.Length, []); // all mounted filesystems, including ISO source, prohibit a write
        int fd = LinuxNative.open(current.LinuxDevice, 2 | 0x80 | 0x4000 | 0x20000 | 0x80000); // RDWR EXCL DIRECT NOFOLLOW CLOEXEC
        if (fd < 0) throw LinuxNative.Error("Could not open the disk exclusively. Unmount its partitions");
        using var handle = new SafeFileHandle((IntPtr)fd, true);
        var locked = (await LinuxStorage.ReadDisksAsync(token)).SingleOrDefault(d => d.LinuxDevice == current.LinuxDevice) ?? throw new IOException("The disk was disconnected after opening.");
        Safety.Validate(current, locked, source.Length, []);
        if (LinuxNative.ioctl(handle, 0x80081272, out ulong size) != 0 || size != (ulong)current.Size) throw new IOException("The size of the opened device changed.");
        bool touched = false;
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); using var device = new LinuxImageDevice(handle);
                touched = true;
                string hash = ImageTransfer.WriteAndVerify(source, device, current.Size, progress, token);
                string? warning = LinuxNative.ioctl(handle, 0x125f, out _) == 0 ? null : "Reconnect the disk: the kernel could not refresh the partition table.";
                return new WriteResult(hash, warning);
            }, token);
        }
        catch (Exception ex) when (touched) { throw new IOException(ex.Message + " The disk has already been modified. After cancellation or failure, write the entire image again.", ex); }
    }
    private sealed class LinuxImageDevice : IImageDevice, IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly IntPtr buffer;
        internal LinuxImageDevice(SafeFileHandle handle)
        {
            this.handle = handle;
            if (LinuxNative.posix_memalign(out buffer, 4096, 4 * 1024 * 1024) != 0) throw new IOException("Could not allocate a buffer.");
        }
        public void Seek(long position) { if (position % 512 != 0 || LinuxNative.lseek(handle, position, 0) != position) throw LinuxNative.Error("Disk position"); }
        public void Read(byte[] data, int count)
        {
            if (count > 4 * 1024 * 1024 || count % 512 != 0) throw new IOException("Invalid block size");
            if (LinuxNative.read(handle, buffer, (nuint)count) != count) throw LinuxNative.Error("Reading disk"); Marshal.Copy(buffer, data, 0, count);
        }
        public void Write(byte[] data, int count)
        {
            if (count > 4 * 1024 * 1024 || count % 512 != 0) throw new IOException("Invalid block size");
            Marshal.Copy(data, 0, buffer, count); if (LinuxNative.write(handle, buffer, (nuint)count) != count) throw LinuxNative.Error("Writing disk");
        }
        public void Flush() { if (LinuxNative.fsync(handle) != 0) throw LinuxNative.Error("Flushing cache"); }
        public void Dispose() => LinuxNative.free(buffer);
    }
}
