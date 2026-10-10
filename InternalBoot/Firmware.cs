using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IsoWriter.InternalBoot;

internal interface IFirmwareStore
{
    byte[]? Read(string name);
    void Write(string name, byte[]? bytes);
}

internal sealed class WindowsFirmware : IFirmwareStore
{
    private const string Global = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid, byte[] data, uint size, out uint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFirmwareEnvironmentVariableEx(string name, string guid, byte[]? data, uint size, uint attributes);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Privilege { public uint Count; public long Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disable, ref Privilege state, uint size, IntPtr previous, IntPtr length);
    internal WindowsFirmware()
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x28, out var token)) throw Error();
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out long luid)) throw Error();
            var state = new Privilege { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0) throw Error();
        }
    }
    private static IOException Error() => new("UEFI: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    public byte[]? Read(string name)
    {
        byte[] data = new byte[131072];
        uint length = GetFirmwareEnvironmentVariableEx(name, Global, data, (uint)data.Length, out _);
        if (length != 0) return data[..(int)length];
        if (Marshal.GetLastWin32Error() == 203) return null;
        throw Error();
    }
    public void Write(string name, byte[]? bytes)
    {
        if (!SetFirmwareEnvironmentVariableEx(name, Global, bytes, (uint)(bytes?.Length ?? 0), bytes is null ? 0u : 7u)) throw Error();
        if (!(Read(name) ?? []).AsSpan().SequenceEqual(bytes ?? [])) throw new IOException("UEFI did not save variable " + name);
    }
}

internal static class EfiOption
{
    internal static byte[] Build(BootPartition esp, string installationId)
    {
        if (!Guid.TryParseExact(installationId, "N", out _) || !Guid.TryParse(esp.Guid, out Guid partitionId)
            || !esp.IsEsp || !esp.Gpt || esp.Sector <= 0 || esp.Offset % esp.Sector != 0 || esp.Size % esp.Sector != 0)
            throw new InvalidDataException("Invalid EFI partition parameters.");
        string path = $@"\EFI\IsoWriter-{installationId}\grubx64.efi";
        byte[] file = Encoding.Unicode.GetBytes(path + '\0');
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(1u); // LOAD_OPTION_ACTIVE
        writer.Write(checked((ushort)(42 + 4 + file.Length + 4)));
        writer.Write(Encoding.Unicode.GetBytes("ISO Writer Linux " + installationId[..8] + '\0'));
        writer.Write((byte)4); writer.Write((byte)1); writer.Write((ushort)42);
        writer.Write((uint)esp.Number); writer.Write((ulong)(esp.Offset / esp.Sector)); writer.Write((ulong)(esp.Size / esp.Sector));
        writer.Write(partitionId.ToByteArray()); writer.Write((byte)2); writer.Write((byte)2); // GPT + GUID signature
        writer.Write((byte)4); writer.Write((byte)4); writer.Write(checked((ushort)(4 + file.Length))); writer.Write(file);
        writer.Write((byte)0x7f); writer.Write((byte)0xff); writer.Write((ushort)4);
        return memory.ToArray();
    }
}

internal sealed class FirmwareManager(IFirmwareStore store)
{
    internal static string Name(ushort number) => $"Boot{number:X4}";
    internal static ushort[] Decode(byte[]? value)
    {
        if (value is null) return [];
        if (value.Length % 2 != 0) throw new IOException("Invalid UEFI BootOrder.");
        return Enumerable.Range(0, value.Length / 2).Select(i => BitConverter.ToUInt16(value, i * 2)).ToArray();
    }
    private static byte[] Encode(IEnumerable<ushort> numbers) => numbers.SelectMany(BitConverter.GetBytes).ToArray();
    internal ushort ReserveNumber()
    {
        var order = Decode(store.Read("BootOrder"));
        if (order.Length == 0) throw new IOException("BootOrder is empty: automatic bootloader registration is blocked.");
        for (ushort id = 0x2000; id < 0x2100; id++)
            if (!order.Contains(id) && store.Read(Name(id)) is null) return id;
        throw new IOException("No unused UEFI entry number is available for ISO Writer.");
    }
    internal void Add(ushort number, byte[] option)
    {
        if (store.Read(Name(number)) is not null) throw new IOException("The UEFI entry number is already in use. Prepare the installation again.");
        store.Write(Name(number), option);
        var order = Decode(store.Read("BootOrder"));
        if (order.Length == 0 || order.Contains(number)) throw new IOException("BootOrder changed during installation.");
        store.Write("BootOrder", Encode(order.Append(number))); // preserve existing default
    }
    internal void Schedule(ushort number, byte[] option)
    {
        VerifyOwner(number, option);
        if (store.Read("BootNext") is not null) throw new IOException("Another next boot is already scheduled. Complete it first or cancel it in system settings.");
        store.Write("BootNext", BitConverter.GetBytes(number));
    }
    internal void Remove(ushort number, byte[] option)
    {
        byte[]? existing = store.Read(Name(number));
        if (existing is not null) VerifyOwner(number, option);
        if ((store.Read("BootNext") ?? []).AsSpan().SequenceEqual(BitConverter.GetBytes(number))) store.Write("BootNext", null);
        var order = Decode(store.Read("BootOrder"));
        if (order.Contains(number))
        {
            var next = order.Where(n => n != number).ToArray();
            if (next.Length == 0) throw new IOException("Refusing to remove the last UEFI entry.");
            store.Write("BootOrder", Encode(next));
        }
        if (existing is not null) store.Write(Name(number), null);
    }
    private void VerifyOwner(ushort number, byte[] option)
    {
        if (!(store.Read(Name(number)) ?? []).AsSpan().SequenceEqual(option))
            throw new IOException("This UEFI entry was changed or belongs to another application. Automatic modification is blocked.");
    }
}
