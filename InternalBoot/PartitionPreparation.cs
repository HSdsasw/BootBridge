using System.Text.Json;

namespace IsoWriter.InternalBoot;

internal sealed record ShrinkPlan(BootPartition Source, long NewSize, long StagingSize)
{
    internal long Unallocated => Source.Size - NewSize - StagingSize;
    internal string Summary => Source.LinuxDevice.Length > 0 ? $"{Source.LinuxDevice} • {Source.Model}\nShrink unmounted ext4: {Units.Size(Source.Size)} → {Units.Size(NewSize)}\nNew ext4 ISO_INSTALL: {Units.Size(StagingSize)}\nUnallocated space for Linux: {Units.Size(Unallocated)}\n\nThis will run e2fsck and resize2fs, then modify GPT. Only the new partition will be formatted. Back up your data and ensure stable power. Cancellation and automatic resize rollback are unavailable." : $"Disk {Source.DiskNumber} • {Source.Model}\r\n"
        + $"Shrink {Source.Letter}: from {Units.Size(Source.Size)} to {Units.Size(NewSize)}.\r\n"
        + $"Create a NEW NTFS partition ISO_INSTALL: {Units.Size(StagingSize)}.\r\n"
        + $"Leave {Units.Size(Unallocated)} of unallocated space for Linux.\r\n\r\n"
        + "Windows tools preserve existing files. Back up your data and ensure stable power before resizing a partition.\r\n"
        + "Cancellation is unavailable while changing partitions. A completed shrink is not automatically reversed after a failure.";
}

internal static class PartitionPreparation
{
    internal static ShrinkPlan Calculate(BootPartition source, long minimumSize, int stagingGiB, int linuxGiB)
    {
        if (source.BlockReason is { } reason) throw new IOException(reason);
        if (stagingGiB is < 4 or > 128 || linuxGiB is < 16 or > 4096) throw new IOException("Specify 4–128 GiB for the ISO and 16–4096 GiB for Linux.");
        long staging = (long)stagingGiB << 30;
        long reduce = staging + ((long)linuxGiB << 30);
        long next = (source.Size - reduce) / (1024 * 1024) * (1024 * 1024);
        if (next < minimumSize || next < (32L << 30) || reduce > source.Free - (8L << 30))
            throw new IOException("Windows cannot free this much space. Request smaller sizes or free up space. At least 8 GiB of free space is retained on the source partition.");
        return new(source, next, staging);
    }
    internal static async Task<ShrinkPlan> PlanAsync(BootPartition selected, int stagingGiB, int linuxGiB, CancellationToken token)
    {
        if (OperatingSystem.IsLinux()) return await Platform.LinuxPartitions.PlanAsync(selected, stagingGiB, linuxGiB, token);
        var env = await BootStorage.ReadAsync(token); env.Validate();
        var current = env.Partitions.SingleOrDefault(p => p.Identity == selected.Identity && p.Letter == selected.Letter) ?? throw new IOException("The partition changed.");
        string output = await SystemProcess.PowerShell($"(Get-PartitionSupportedSize -DiskNumber {current.DiskNumber} -PartitionNumber {current.Number}).SizeMin", token);
        if (!long.TryParse(output.Trim(), out long minimum)) throw new IOException("Could not retrieve the minimum partition size.");
        return Calculate(current, minimum, stagingGiB, linuxGiB);
    }
    internal static async Task<string> ApplyAsync(ShrinkPlan plan)
    {
        if (OperatingSystem.IsLinux()) return await Platform.LinuxPartitions.ApplyAsync(plan);
        var env = await BootStorage.ReadAsync(); env.Validate();
        var current = env.Partitions.SingleOrDefault(p => p.Identity == plan.Source.Identity) ?? throw new IOException("The source partition changed.");
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
        catch (Exception ex) { throw new IOException($"Partition changes did not complete: {ex.Message}\nThe partition may already have been shrunk or a new partition created. Refresh the list and check Disk Management. Plan saved to: {journal}", ex); }
    }
}
