using System.Text.Json;

namespace IsoWriter.InternalBoot;

internal sealed record ShrinkPlan(BootPartition Source, long NewSize, long StagingSize)
{
    internal long Unallocated => Source.Size - NewSize - StagingSize;
    internal string Summary => $"Диск {Source.DiskNumber} • {Source.Model}\r\n"
        + $"Уменьшить {Source.Letter}: с {Units.Size(Source.Size)} до {Units.Size(NewSize)}.\r\n"
        + $"Создать НОВЫЙ раздел NTFS ISO_INSTALL: {Units.Size(StagingSize)}.\r\n"
        + $"Оставить {Units.Size(Unallocated)} нераспределённого места для Linux.\r\n\r\n"
        + "Существующие файлы сохраняются средствами Windows. Перед изменением размера раздела сделайте резервную копию и обеспечьте стабильное питание.\r\n"
        + "Во время изменения разметки отмена недоступна. При сбое уже выполненное уменьшение автоматически не откатывается.";
}

internal static class PartitionPreparation
{
    internal static ShrinkPlan Calculate(BootPartition source, long minimumSize, int stagingGiB, int linuxGiB)
    {
        if (source.BlockReason is { } reason) throw new IOException(reason);
        if (stagingGiB is < 4 or > 128 || linuxGiB is < 16 or > 4096) throw new IOException("Укажите 4–128 GiB для ISO и 16–4096 GiB для Linux.");
        long staging = (long)stagingGiB << 30;
        long reduce = staging + ((long)linuxGiB << 30);
        long next = (source.Size - reduce) / (1024 * 1024) * (1024 * 1024);
        if (next < minimumSize || next < (32L << 30) || reduce > source.Free - (8L << 30))
            throw new IOException("Windows не может освободить столько места. Уменьшите размеры или освободите место. На исходном разделе оставляется минимум 8 GiB свободного пространства.");
        return new(source, next, staging);
    }
    internal static async Task<ShrinkPlan> PlanAsync(BootPartition selected, int stagingGiB, int linuxGiB, CancellationToken token)
    {
        var env = await BootStorage.ReadAsync(token); env.Validate();
        var current = env.Partitions.SingleOrDefault(p => p.Identity == selected.Identity && p.Letter == selected.Letter) ?? throw new IOException("Раздел изменился.");
        string output = await SystemProcess.PowerShell($"(Get-PartitionSupportedSize -DiskNumber {current.DiskNumber} -PartitionNumber {current.Number}).SizeMin", token);
        if (!long.TryParse(output.Trim(), out long minimum)) throw new IOException("Не удалось получить минимальный размер раздела.");
        return Calculate(current, minimum, stagingGiB, linuxGiB);
    }
    internal static async Task<string> ApplyAsync(ShrinkPlan plan)
    {
        var env = await BootStorage.ReadAsync(); env.Validate();
        var current = env.Partitions.SingleOrDefault(p => p.Identity == plan.Source.Identity) ?? throw new IOException("Исходный раздел изменился.");
        if (current.BlockReason is { } reason) throw new IOException(reason);
        string guid = Guid.Parse(current.Guid).ToString("B");
        // Only validated GUIDs and numeric values enter this script. No paths or user shell text.
        string script = $$"""
            $p = Get-Partition -DiskNumber {{current.DiskNumber}} -PartitionNumber {{current.Number}}
            if ([string]$p.Guid -ne '{{guid}}' -or [long]$p.Size -ne {{current.Size}} -or [long]$p.Offset -ne {{current.Offset}}) { throw 'Partition identity changed.' }
            $limits = Get-PartitionSupportedSize -DiskNumber {{current.DiskNumber}} -PartitionNumber {{current.Number}}
            if ([long]$limits.SizeMin -gt {{plan.NewSize}}) { throw 'Partition can no longer be shrunk to the planned size.' }
            $p | Resize-Partition -Size {{plan.NewSize}} -Confirm:$false
            $changed = Get-Partition -DiskNumber {{current.DiskNumber}} -PartitionNumber {{current.Number}}
            if ([long]$changed.Size -ne {{plan.NewSize}} -or [string]$changed.Guid -ne '{{guid}}') { throw 'Unexpected resize result.' }
            $created = New-Partition -DiskNumber {{current.DiskNumber}} -Offset {{current.Offset + plan.NewSize}} -Size {{plan.StagingSize}} -AssignDriveLetter
            if ([long]$created.Offset -ne {{current.Offset + plan.NewSize}} -or [long]$created.Size -ne {{plan.StagingSize}} -or $created.IsBoot -or $created.IsSystem) { throw 'Unexpected new partition. Formatting refused.' }
            $created | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'ISO_INSTALL' -Confirm:$false | Out-Null
            [string]$created.DriveLetter
            """;
        string journalDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoWriter");
        Directory.CreateDirectory(journalDirectory);
        string journal = Path.Combine(journalDirectory, "partition-plan-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(plan));
        try { return (await SystemProcess.PowerShell(script, timeoutSeconds: 0)).Trim(); }
        catch (Exception ex) { throw new IOException($"Изменение разметки не завершено: {ex.Message}\nДиск мог быть уже уменьшен или новый раздел уже создан. Обновите список и проверьте Управление дисками. План сохранён: {journal}", ex); }
    }
}
