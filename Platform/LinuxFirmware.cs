using System.Text.RegularExpressions;
using IsoWriter.InternalBoot;

namespace IsoWriter.Platform;

internal sealed class LinuxFirmware : IFirmwareStore
{
    private const string DirectoryPath = "/sys/firmware/efi/efivars";
    private static string PathFor(string name)
    {
        if (!Regex.IsMatch(name, "^(BootOrder|BootNext|Boot[0-9A-F]{4})$", RegexOptions.CultureInvariant)) throw new IOException("Invalid UEFI variable.");
        return Path.Combine(DirectoryPath, name + "-8be4df61-93ca-11d2-aa0d-00e098032b8c");
    }
    public byte[]? Read(string name)
    {
        string path = PathFor(name);
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 4 || data.Length > 131076) throw new IOException("Malformed EFI variable.");
            return data[4..];
        }
        catch (FileNotFoundException) { return null; }
    }
    public void Write(string name, byte[]? bytes)
    {
        LinuxNative.RequireRoot(); string path = PathFor(name);
        if (name is not ("BootOrder" or "BootNext") && !Regex.IsMatch(name, "^Boot20[0-9A-F]{2}$")) throw new IOException("Only ISO Writer entries may be modified.");
        if (bytes is null) File.Delete(path);
        else
        {
            byte[] data = new byte[bytes.Length + 4]; BitConverter.GetBytes(7u).CopyTo(data, 0); bytes.CopyTo(data, 4);
            // efivarfs expects attributes + payload in one write(2), not multiple buffered writes.
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
            file.Write(data); file.Flush();
        }
        if (!(Read(name) ?? []).AsSpan().SequenceEqual(bytes ?? [])) throw new IOException("UEFI did not save " + name);
    }
}
