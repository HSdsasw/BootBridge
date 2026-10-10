using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IsoWriter.InternalBoot;

internal static class DirectoryGuard
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    internal static IDisposable Open(string directory)
    {
        if (OperatingSystem.IsLinux()) return Platform.LinuxNative.GuardDirectory(directory);
        BootInstaller.NoLinks(directory);
        var handle = CreateFile(directory, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose(); throw new IOException("Could not protect the installation directory against changes: " + directory);
    }
}
