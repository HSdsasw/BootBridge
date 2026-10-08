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
    public string Caption => $"Раздел {Number}{(string.IsNullOrWhiteSpace(Letter) ? "" : $" ({Letter}:)")} • {Units.Size(Size)} • {Type}";
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
    public string DevicePath => $@"\\.\PhysicalDrive{Number}";
    public string? BlockReason => IsBoot || IsSystem || Partitions.Any(p => p.IsBoot || p.IsSystem)
        ? "Системный / загрузочный диск Windows"
        : IsReadOnly ? "Диск защищён от записи"
        : IsOffline ? "Диск отключён (Offline)"
        : IsClustered ? "Кластерный диск"
        : !new[] { "USB", "SATA", "ATA", "SCSI", "NVMe", "SAS", "SD", "MMC" }.Contains(Bus)
            ? $"Неподдерживаемый тип диска: {Bus} (RAID, виртуальные и составные диски запрещены)"
        : SectorSize != 512 ? "Требуется логический сектор 512 байт; 4Kn ISO не поддерживается"
        : string.IsNullOrWhiteSpace(UniqueId) ? "Не удалось получить идентификатор диска"
        : null;
    public string Identity => $"{Number}|{UniqueId}|{Serial}|{Size}|{SectorSize}";
    public string Layout => string.Join(";", Partitions.Select(p => $"{p.Number}:{p.Offset}:{p.Size}:{string.Join(',', p.Paths)}"));
}

public sealed record Target(Disk Disk, Partition? Partition)
{
    public override string ToString() => $"{(Partition is null ? "Весь диск" : Partition.Caption)} → Диск {Disk.Number} • {Disk.Model} • {Units.Size(Disk.Size)}{(Disk.BlockReason is null ? "" : " [ЗАБЛОКИРОВАН]")}";
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
            throw new InvalidOperationException("Диск или его разметка изменились. Обновите список и подтвердите выбор заново.");
        if (current.BlockReason is { } reason) throw new InvalidOperationException(reason);
        if (imageSize <= 0 || imageSize > current.Size) throw new InvalidOperationException("Образ не помещается на диск.");
        if (sourceDisks.Contains(current.Number)) throw new InvalidOperationException("ISO находится на выбранном физическом диске. Перенесите ISO на другой диск.");
    }
}
