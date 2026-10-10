using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using IsoWriter.InternalBoot;

namespace IsoWriter.Platform;

internal static class LinuxNative
{
    [DllImport("libc", SetLastError = true)] internal static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int flock(SafeFileHandle fd, int operation);
    [DllImport("libc", SetLastError = true)] internal static extern int fsync(SafeFileHandle fd);
    [DllImport("libc", SetLastError = true)] internal static extern long lseek(SafeFileHandle fd, long offset, int whence);
    [DllImport("libc", SetLastError = true)] internal static extern long read(SafeFileHandle fd, IntPtr buffer, nuint count);
    [DllImport("libc", SetLastError = true)] internal static extern long write(SafeFileHandle fd, IntPtr buffer, nuint count);
    [DllImport("libc", SetLastError = true)] internal static extern int ioctl(SafeFileHandle fd, ulong request, out ulong result);
    [DllImport("libc")] internal static extern int posix_memalign(out IntPtr buffer, nuint alignment, nuint size);
    [DllImport("libc")] internal static extern void free(IntPtr buffer);
    internal static IOException Error(string action) => new(action + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    internal static void RequireRoot()
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0) throw new IOException("This operation requires root privileges. Use the polkit authorization dialog in the application.");
    }
    internal static void AssertSafeDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        BootInstaller.NoLinks(path);
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            if (!dir.Exists) continue;
            string owner = LinuxCommand.RunAsync("stat", ["-c", "%u", "--", dir.FullName]).GetAwaiter().GetResult();
            var mode = File.GetUnixFileMode(dir.FullName);
            if (owner.Trim() != "0" || (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new IOException("To prepare boot files, mount the partition in a root-owned directory without write access for other users (for example /mnt/installer). Unsafe directory: " + dir.FullName);
        }
    }
    internal static IDisposable GuardDirectory(string path)
    {
        AssertSafeDirectory(path);
        int fd = open(path, 0x10000 | 0x20000 | 0x80000); // DIRECTORY, NOFOLLOW, CLOEXEC
        if (fd < 0) throw Error("Opening directory");
        var handle = new SafeFileHandle((IntPtr)fd, true);
        if (flock(handle, 2 | 4) == 0) return handle; // advisory serialization, ownership is checked above
        handle.Dispose(); throw Error("The directory is in use by another operation");
    }
}

internal static class LinuxCommand
{
    internal static string Find(string name)
    {
        if (name.IndexOfAny(['/', '\\']) >= 0) throw new ArgumentException("Only a tool name is allowed");
        foreach (string dir in new[] { "/usr/sbin", "/usr/bin", "/sbin", "/bin" })
        { string path = Path.Combine(dir, name); if (File.Exists(path)) return path; }
        throw new IOException($"Missing system utility: {name}. Install the dependencies listed in README.");
    }
    internal static Task<string> RunAsync(string name, string[] args, CancellationToken token = default, int timeout = 90) =>
        SystemProcess.Run(Find(name), args, token, timeout);
}
