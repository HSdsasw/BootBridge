using System.Text.Json;
using System.Runtime.InteropServices;
using IsoWriter.InternalBoot;

namespace IsoWriter.Platform;

internal sealed record LinuxBlock
{
    public string Path { get; init; } = "";
    public string Kernel { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Parent { get; init; } = "";
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Wwn { get; init; } = "";
    public string DeviceNumber { get; init; } = "";
    public string Table { get; init; } = "";
    public string PartType { get; init; } = "";
    public string PartUuid { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string Fs { get; init; } = "";
    public string Transport { get; init; } = "";
    public long Size { get; init; }
    public long Start { get; init; }
    public long Free { get; init; }
    public int Sector { get; init; }
    public bool ReadOnly { get; init; }
    public string[] Mounts { get; init; } = [];
    public LinuxBlock[] Children { get; init; } = [];
    internal IEnumerable<LinuxBlock> All => new[] { this }.Concat(Children.SelectMany(c => c.All));
    internal string Identity => $"{Path}|{DeviceNumber}|{Wwn}|{Serial}";
}

internal static class LinuxStorage
{
    private const string EspType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    internal static LinuxBlock[] Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        LinuxBlock ParseNode(JsonElement node, string parent)
        {
            string Text(string key) => node.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
            long Num(string key) => long.TryParse(Text(key), out long value) ? value : 0;
            string path = Text("name");
            return new LinuxBlock
            {
                Path = path, Kernel = System.IO.Path.GetFileName(Text("kname")), Kind = Text("type"), Parent = parent,
                Model = Text("model").Trim(), Serial = Text("serial"), Wwn = Text("wwn"), DeviceNumber = Text("maj:min"),
                Table = Text("pttype"), PartType = Text("parttype"), PartUuid = Text("partuuid"), Uuid = Text("uuid"),
                Fs = Text("fstype"), Transport = Text("tran"), Size = Num("size"), Start = Num("start"), Free = Num("fsavail"),
                Sector = (int)Num("log-sec"), ReadOnly = Text("ro").Equals("true", StringComparison.OrdinalIgnoreCase) || Text("ro") == "1",
                Mounts = node.TryGetProperty("mountpoints", out var mounts) && mounts.ValueKind == JsonValueKind.Array ? mounts.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(v => v.Length > 0).ToArray() : [],
                Children = node.TryGetProperty("children", out var children) ? children.EnumerateArray().Select(c => ParseNode(c, path)).ToArray() : []
            };
        }
        return doc.RootElement.GetProperty("blockdevices").EnumerateArray().Select(n => ParseNode(n, "")).ToArray();
    }
    internal static async Task<LinuxBlock[]> ReadAsync(CancellationToken token = default) => Parse(await LinuxCommand.RunAsync("lsblk",
        ["--json", "--bytes", "--paths", "--tree", "--output", "NAME,KNAME,TYPE,SIZE,RO,MODEL,SERIAL,WWN,MAJ:MIN,TRAN,LOG-SEC,PTTYPE,PARTTYPE,PARTUUID,UUID,FSTYPE,MOUNTPOINTS,START,FSAVAIL"], token));
    internal static void CheckDevice(string path)
    {
        if (!path.StartsWith("/dev/", StringComparison.Ordinal) || path.Length <= 5 || path.Contains("..") || path.Any(char.IsWhiteSpace) || path[5..].Contains('/'))
            throw new IOException("Expected a basic block device under /dev/…");
    }
    internal static bool HasHolders(LinuxBlock block) => block.All.Any(n =>
        Directory.Exists($"/sys/class/block/{n.Kernel}/holders") && Directory.EnumerateFileSystemEntries($"/sys/class/block/{n.Kernel}/holders").Any());
    internal static string? RawBlockReason(LinuxBlock disk)
    {
        if (disk.Kind != "disk" || disk.ReadOnly) return "An accessible basic disk is required";
        if (disk.All.Any(n => n.Mounts.Length > 0)) return "The disk has mounted partitions or active swap. Unmount them before writing; the running system disk cannot be overwritten.";
        if (disk.Children.Any(p => p.Kind != "part" || p.Children.Length != 0)) return "LVM, LUKS, RAID and composite devices are not supported";
        if (HasHolders(disk)) return "The disk is in use by another block device";
        return null;
    }
    internal static async Task<Disk[]> ReadDisksAsync(CancellationToken token = default)
    {
        var blocks = await ReadAsync(token);
        return blocks.Where(b => b.Kind == "disk").OrderBy(b => b.Path, StringComparer.Ordinal).Select((b, i) => new Disk
        {
            Number = i, LinuxDevice = b.Path, UniqueId = b.Identity, Serial = b.Serial, Model = b.Model, Size = b.Size, SectorSize = b.Sector,
            Bus = b.Transport.ToUpperInvariant() switch { "NVME" => "NVMe", "" => "SCSI", var bus => bus },
            IsReadOnly = b.ReadOnly, PlatformBlockReason = RawBlockReason(b),
            Partitions = b.Children.Where(p => p.Kind == "part").Select(p => new Partition { Number = PartitionNumber(p), Offset = p.Start * 512, Size = p.Size, Type = p.Fs, Paths = p.Mounts }).ToArray()
        }).ToArray();
    }
    private static int PartitionNumber(LinuxBlock p) => int.Parse(File.ReadAllText($"/sys/class/block/{p.Kernel}/partition").Trim());
    internal static string? StagingBlockReason(BootPartition p) => !p.Available || !p.Gpt ? "A basic GPT partition without LVM/LUKS/RAID is required."
        : p.IsEsp ? "The EFI partition cannot be used to store the ISO."
        : p.FileSystem is not ("ext4" or "NTFS") ? "ext4 and NTFS are supported. Btrfs/XFS/LUKS are not supported."
        : !p.Volume.StartsWith('/') ? "Mount the partition first, for example at /mnt/installer."
        : !Guid.TryParse(p.Guid, out _) ? "The partition PARTUUID is unknown."
        : p.Encryption != 0 ? "Encrypted partitions are not supported." : null;
    internal static void ValidateEnvironment(BootEnvironment env)
    {
        if (!env.Uefi || RuntimeInformation.OSArchitecture != Architecture.X64) throw new IOException("Booting an ISO requires Linux x64 running in UEFI mode.");
        if (env.SecureBoot != 0) throw new IOException("Secure Boot is enabled or its status is unavailable. Disable Secure Boot in UEFI; the bundled GRUB is unsigned.");
        if (!env.Esp.Available || !env.Esp.Gpt || env.Esp.FileSystem != "FAT32" || env.Esp.Free < 32L * 1024 * 1024)
            throw new IOException("Mount a single system FAT32 ESP at /boot/efi, /efi or /boot; 32 MiB of free space is required.");
    }
    internal static async Task<BootEnvironment> ReadBootAsync(CancellationToken token = default)
    {
        var blocks = await ReadAsync(token); var partitions = new List<BootPartition>();
        foreach (var disk in blocks.Where(b => b.Kind == "disk"))
        foreach (var p in disk.Children.Where(p => p.Kind == "part"))
        {
            bool esp = p.PartType.Equals(EspType, StringComparison.OrdinalIgnoreCase);
            string mount = p.Mounts.FirstOrDefault(m => m.StartsWith('/')) ?? "";
            bool available = !disk.ReadOnly && !p.ReadOnly && p.Children.Length == 0 && !HasHolders(p);
            long free = p.Free;
            if (mount.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await LinuxCommand.RunAsync("findmnt", ["--json", "--target", mount, "--output", "TARGET,FSROOT,OPTIONS"], token));
                    var record = doc.RootElement.GetProperty("filesystems")[0];
                    available &= record.GetProperty("target").GetString() == mount && record.GetProperty("fsroot").GetString() == "/"
                        && !record.GetProperty("options").GetString()!.Split(',').Contains("ro");
                    free = new DriveInfo(mount).AvailableFreeSpace;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { available = false; }
            }
            partitions.Add(new BootPartition
            {
                LinuxDevice = p.Path, ParentDevice = disk.Path, DiskId = disk.Identity, Number = PartitionNumber(p), Model = disk.Model,
                Guid = p.PartUuid, FileSystemUuid = p.Uuid, Volume = mount, FileSystem = p.Fs switch { "vfat" => "FAT32", "ntfs" or "ntfs3" => "NTFS", var fs => fs },
                Offset = p.Start * 512, Size = p.Size, Sector = disk.Sector, Free = free, Gpt = disk.Table == "gpt", Available = available,
                IsEsp = esp, IsSystem = esp && mount is "/boot/efi" or "/efi" or "/boot", IsBoot = mount == "/", Encryption = p.Fs == "crypto_LUKS" ? 1 : 0, Protection = 0
            });
        }
        int secure = -1;
        try { byte[] data = File.ReadAllBytes("/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c"); if (data.Length == 5) secure = data[4]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new BootEnvironment { Linux = true, Uefi = Directory.Exists("/sys/firmware/efi"), SecureBoot = secure, Partitions = partitions.ToArray() };
    }
}
