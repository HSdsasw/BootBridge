namespace IsoWriter;

public sealed record Partition
{
    public int Number { get; init; }
    public string Letter { get; init; } = "";
    public long Size { get; init; }
    public long Offset { get; init; }
    public string Type { get; init; } = "";
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public string[] Paths { get; init; } = [];
    public string Caption => $"Partition {Number}{(string.IsNullOrWhiteSpace(Letter) ? "" : $" ({Letter}:)")} • {Units.Size(Size)} • {Type}";
}

public sealed record Disk
{
    public int Number { get; init; }
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string UniqueId { get; init; } = "";
    public string Bus { get; init; } = "";
    public long Size { get; init; }
    public int SectorSize { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsClustered { get; init; }
    public Partition[] Partitions { get; init; } = [];
    public string LinuxDevice { get; init; } = "";
    public string? PlatformBlockReason { get; init; }
    public string DevicePath => LinuxDevice.Length > 0 ? LinuxDevice : $@"\\.\PhysicalDrive{Number}";
    public string? BlockReason => PlatformBlockReason ?? (IsBoot || IsSystem || Partitions.Any(p => p.IsBoot || p.IsSystem)
        ? "Windows system / boot disk"
        : IsReadOnly ? "The disk is write-protected"
        : IsOffline ? "The disk is offline"
        : IsClustered ? "Clustered disk"
        : !new[] { "USB", "SATA", "ATA", "SCSI", "NVMe", "SAS", "SD", "MMC" }.Contains(Bus)
            ? $"Unsupported disk type: {Bus} (RAID, virtual and composite disks are blocked)"
        : SectorSize != 512 ? "A 512-byte logical sector is required; 4Kn ISO is not supported"
        : string.IsNullOrWhiteSpace(UniqueId) ? "Could not retrieve the disk identity"
        : null);
    public string Identity => $"{Number}|{UniqueId}|{Serial}|{Size}|{SectorSize}";
    public string Layout => string.Join(";", Partitions.Select(p => $"{p.Number}:{p.Offset}:{p.Size}:{string.Join(',', p.Paths)}"));
}

public sealed record Target(Disk Disk, Partition? Partition)
{
    public override string ToString() => $"{(Partition is null ? "Entire disk" : Partition.Caption)} → Disk {Disk.Number} • {Disk.Model} • {Units.Size(Disk.Size)}{(Disk.BlockReason is null ? "" : " [BLOCKED]")}";
}

public static class Units
{
    public static string Size(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GiB" : $"{value / (double)(1L << 20):0.0} MiB";
}

public static class Safety
{
    public static void Validate(Disk selected, Disk current, long imageSize, IEnumerable<int> sourceDisks)
    {
        if (selected.Identity != current.Identity || selected.Layout != current.Layout)
            throw new InvalidOperationException("The disk or its layout changed. Refresh the list and confirm the selection again.");
        if (current.BlockReason is { } reason) throw new InvalidOperationException(reason);
        if (imageSize <= 0 || imageSize > current.Size) throw new InvalidOperationException("The image does not fit on the disk.");
        if (sourceDisks.Contains(current.Number)) throw new InvalidOperationException("The ISO is on the selected physical disk. Move the ISO to another disk.");
    }
}
