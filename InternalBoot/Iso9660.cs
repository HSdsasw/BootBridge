using System.Buffers.Binary;
using System.Text;

namespace IsoWriter.InternalBoot;

// Read only: never mount an ISO or execute files from it. Bounds and directory sizes are limited.
internal sealed class Iso9660
{
    internal sealed record Entry(long Offset, long Length, bool Directory);
    private readonly Stream stream;
    private readonly Entry root;
    internal Iso9660(Stream stream)
    {
        this.stream = stream;
        byte[] sector = new byte[2048];
        for (int block = 16; block < 80; block++)
        {
            Read(block * 2048L, sector);
            if (Encoding.ASCII.GetString(sector, 1, 5) != "CD001" || sector[6] != 1) break;
            if (sector[0] == 1)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(128)) != 2048)
                    throw new InvalidDataException("Unsupported ISO block size.");
                root = Parse(sector.AsSpan(156));
                if (!root.Directory) throw new InvalidDataException("Invalid ISO root directory.");
                return;
            }
            if (sector[0] == 255) break;
        }
        throw new InvalidDataException("ISO9660 was not found. Select an Ubuntu/Mint or Debian Live installation ISO.");
    }
    private Entry Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 34 || record[0] < 34 || record[0] > record.Length || record[1] != 0 || (record[25] & 0x80) != 0)
            throw new InvalidDataException("Unsupported ISO record (multi-extent / extended attributes).");
        long start = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]) * 2048L;
        long size = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
        if (start > stream.Length || size > stream.Length - start) throw new InvalidDataException("The file extends beyond the ISO bounds.");
        return new(start, size, (record[25] & 2) != 0);
    }
    private void Read(long offset, Span<byte> bytes)
    {
        if (offset < 0 || offset > stream.Length || bytes.Length > stream.Length - offset) throw new InvalidDataException("The ISO is truncated.");
        stream.Position = offset;
        stream.ReadExactly(bytes);
    }
    internal Entry? Find(string path)
    {
        Entry current = root;
        foreach (string component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!current.Directory || current.Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid ISO directory.");
            byte[] directory = new byte[(int)current.Length];
            Read(current.Offset, directory);
            Entry? next = null;
            for (int position = 0; position < directory.Length;)
            {
                int length = directory[position];
                if (length == 0) { position = (position / 2048 + 1) * 2048; continue; }
                if (length < 34 || position + length > directory.Length || position % 2048 + length > 2048)
                    throw new InvalidDataException("Invalid ISO directory record.");
                var record = directory.AsSpan(position, length);
                int nameLength = record[32];
                if (33 + nameLength > length) throw new InvalidDataException("Invalid ISO file name.");
                string name = Encoding.ASCII.GetString(record.Slice(33, nameLength)).Split(';')[0].TrimEnd('.');
                // Rock Ridge NM preserves names such as .disk that ISO9660 may encode as _DISK.
                int systemUse = 33 + nameLength + (nameLength % 2 == 0 ? 1 : 0);
                var alternate = new StringBuilder();
                for (int s = systemUse; s + 4 <= length;)
                {
                    int entryLength = record[s + 2];
                    if (entryLength < 4 || s + entryLength > length) break;
                    if (record[s] == (byte)'N' && record[s + 1] == (byte)'M' && entryLength >= 5 && (record[s + 4] & 6) == 0)
                        alternate.Append(Encoding.UTF8.GetString(record.Slice(s + 5, entryLength - 5)));
                    s += entryLength;
                }
                if (alternate.Length > 0) name = alternate.ToString();
                if (name.Equals(component, StringComparison.OrdinalIgnoreCase)) { next = Parse(record); break; }
                position += length;
            }
            if (next is null) return null;
            current = next;
        }
        return current;
    }
    internal byte[] Prefix(Entry entry, int maximum)
    {
        byte[] data = new byte[(int)Math.Min(entry.Length, maximum)];
        Read(entry.Offset, data); return data;
    }
}

internal sealed record LinuxImage(string Family, string Kernel, string Initrd, long Length)
{
    internal static LinuxImage Inspect(Stream stream)
    {
        var iso = new Iso9660(stream);
        string info = iso.Find("/.disk/info") is { Directory: false } metadata
            ? Encoding.UTF8.GetString(iso.Prefix(metadata, 4096)) : "";
        bool casper = info.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) || info.Contains("Linux Mint", StringComparison.OrdinalIgnoreCase);
        bool debian = info.Contains("Debian", StringComparison.OrdinalIgnoreCase);
        string folder = casper ? "/casper" : debian ? "/live" : throw new InvalidDataException("Ubuntu/Linux Mint and Debian Live ISOs with a .disk/info file are supported.");
        string kernel = folder + "/vmlinuz";
        var kernelEntry = iso.Find(kernel) ?? throw new InvalidDataException("The image has no supported live kernel. Debian netinst is not supported.");
        byte[] header = iso.Prefix(kernelEntry, 4096);
        if (header.Length < 0x238 || Encoding.ASCII.GetString(header, 0x202, 4) != "HdrS" || (header[0x236] & 1) == 0)
            throw new InvalidDataException("A Linux x86-64 kernel using the bzImage boot protocol is required.");
        string initrd = new[] { "/initrd", "/initrd.lz", "/initrd.img", "/initrd.gz" }.Select(s => folder + s)
            .FirstOrDefault(p => iso.Find(p) is { Directory: false, Length: > 0 }) ?? throw new InvalidDataException("No supported initrd was found.");
        if (iso.Find("/EFI/BOOT/BOOTX64.EFI") is not { Directory: false }) throw new InvalidDataException("The ISO has no UEFI x64 bootloader.");
        stream.Position = 0;
        return new(casper ? "Ubuntu / Linux Mint (casper)" : "Debian Live", kernel, initrd, stream.Length);
    }
    internal string Arguments(string isoPath) => Kernel.StartsWith("/casper/")
        ? $"boot=casper iso-scan/filename={isoPath} noeject noprompt ---"
        : $"boot=live components findiso={isoPath}";
}
