using System.Text.Json;
using System.Text.RegularExpressions;
using IsoWriter.InternalBoot;

namespace IsoWriter.Platform;

internal static class LinuxPartitions
{
    internal static ShrinkPlan Calculate(BootPartition p, long minimum, long free, int stagingGiB, int linuxGiB)
    {
        if (p.LinuxDevice.Length == 0 || !p.Available || !p.Gpt || p.FileSystem != "ext4" || p.Volume.Length != 0 || p.IsEsp || p.IsBoot)
            throw new IOException("On Linux, only an unmounted basic ext4 partition can be shrunk. Boot a live environment to resize a system disk. This wizard cannot shrink XFS/Btrfs/NTFS/LUKS/LVM.");
        if (stagingGiB is < 4 or > 128 || linuxGiB is < 16 or > 4096) throw new IOException("Specify 4–128 GiB for the ISO and 16–4096 GiB for Linux.");
        long staging = (long)stagingGiB << 30, reduction = staging + ((long)linuxGiB << 30);
        long next = (p.Size - reduction) / (1024 * 1024) * (1024 * 1024);
        if (next < minimum + (1L << 30) || next < (32L << 30) || reduction > free - (8L << 30) || p.Sector is not (512 or 4096))
            throw new IOException("Not enough space to safely shrink ext4. Request smaller sizes.");
        return new(p, next, staging);
    }
    private static long Field(string output, string field)
    {
        var match = Regex.Match(output, "^" + Regex.Escape(field) + @":\s*(\d+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success ? long.Parse(match.Groups[1].Value) : throw new IOException("Could not read ext4: " + field);
    }
    private static async Task<BootPartition> CurrentAsync(BootPartition selected, CancellationToken token = default)
    {
        var env = await LinuxStorage.ReadBootAsync(token);
        var current = env.Partitions.SingleOrDefault(p => p.Identity == selected.Identity && p.LinuxDevice == selected.LinuxDevice) ?? throw new IOException("The partition changed.");
        LinuxStorage.CheckDevice(current.LinuxDevice); LinuxStorage.CheckDevice(current.ParentDevice);
        var disk = (await LinuxStorage.ReadAsync(token)).SingleOrDefault(d => d.Path == current.ParentDevice) ?? throw new IOException("Disk not found.");
        if (LinuxStorage.RawBlockReason(disk) is { } reason) throw new IOException(reason + " The entire disk must be unmounted to modify GPT.");
        return current;
    }
    internal static async Task<ShrinkPlan> PlanAsync(BootPartition selected, int stagingGiB, int linuxGiB, CancellationToken token)
    {
        LinuxNative.RequireRoot(); var p = await CurrentAsync(selected, token);
        if (p.FileSystem != "ext4" || p.Volume.Length > 0) throw new IOException("Select an unmounted ext4 partition.");
        foreach (string tool in new[] { "resize2fs", "e2fsck", "dumpe2fs", "sfdisk", "parted", "mkfs.ext4", "mount", "udevadm" }) LinuxCommand.Find(tool);
        string info = await LinuxCommand.RunAsync("dumpe2fs", ["-h", p.LinuxDevice], token);
        string minimum = await LinuxCommand.RunAsync("resize2fs", ["-P", p.LinuxDevice], token);
        var match = Regex.Match(minimum, @"Estimated minimum size of the filesystem:\s*(\d+)");
        if (!match.Success) throw new IOException("Could not determine the minimum ext4 size.");
        long blockSize = Field(info, "Block size");
        return Calculate(p, checked(long.Parse(match.Groups[1].Value) * blockSize), checked(Field(info, "Free blocks") * blockSize), stagingGiB, linuxGiB);
    }
    internal static async Task<string> ApplyAsync(ShrinkPlan plan)
    {
        LinuxNative.RequireRoot(); var current = await CurrentAsync(plan.Source);
        // Recalculate instead of trusting a GUI/helper request containing a new partition size.
        int stagingGiB = checked((int)(plan.StagingSize >> 30)), linuxGiB = checked((int)(plan.Unallocated >> 30));
        var recalculated = await PlanAsync(current, stagingGiB, linuxGiB, default);
        if (recalculated.NewSize != plan.NewSize || recalculated.StagingSize != plan.StagingSize) throw new IOException("The partition plan no longer matches the disk state.");
        string journalRoot = "/var/lib/bootbridge";
        LinuxNative.AssertSafeDirectory("/var/lib"); Directory.CreateDirectory(journalRoot); LinuxNative.AssertSafeDirectory(journalRoot);
        string prefix = Path.Combine(journalRoot, Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(plan));
        await File.WriteAllTextAsync(prefix + ".sfdisk", await LinuxCommand.RunAsync("sfdisk", ["--dump", current.ParentDevice]));
        string partitionInput = "size=" + (plan.NewSize / current.Sector).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        // -N modifies only this partition; unspecified start, type and UUID are retained.
        await SystemProcess.Run(LinuxCommand.Find("sfdisk"), ["--no-act", "--lock=yes", "-N", current.Number.ToString(), current.ParentDevice], standardInput: partitionInput);
        try
        {
            await SystemProcess.Run(LinuxCommand.Find("e2fsck"), ["-f", "-p", current.LinuxDevice], timeoutSeconds: 0, acceptedExitCodes: [0, 1]);
            // Filesystem is shrunk FIRST; partition boundary is changed only after success.
            await LinuxCommand.RunAsync("resize2fs", [current.LinuxDevice, ((plan.NewSize - 16 * 1024 * 1024) / 1024) + "K"], timeout: 0);
            await CurrentAsync(current);
            await SystemProcess.Run(LinuxCommand.Find("sfdisk"), ["--lock=yes", "-N", current.Number.ToString(), current.ParentDevice], timeoutSeconds: 0, standardInput: partitionInput);
            await LinuxCommand.RunAsync("udevadm", ["settle"], timeout: 0);
            var resizedDisk = (await LinuxStorage.ReadAsync()).Single(d => d.Path == current.ParentDevice);
            var resized = resizedDisk.Children.SingleOrDefault(p => p.Path == current.LinuxDevice);
            if (resizedDisk.Identity != current.DiskId || resized is null || resized.PartUuid != current.Guid || resized.Start * 512 != current.Offset || resized.Size != plan.NewSize || LinuxStorage.RawBlockReason(resizedDisk) is not null)
                throw new IOException("Unexpected partition shrink result. Creation of the new partition was canceled.");
            long start = current.Offset + plan.NewSize, end = start + plan.StagingSize - 1;
            await LinuxCommand.RunAsync("parted", ["--script", current.ParentDevice, "unit", "B", "mkpart", "ISO_INSTALL", "ext4", start + "B", end + "B"], timeout: 0);
            await LinuxCommand.RunAsync("udevadm", ["settle"], timeout: 0);
            var disk = (await LinuxStorage.ReadAsync()).Single(d => d.Path == current.ParentDevice);
            var created = disk.Children.SingleOrDefault(p => p.Start * 512 == start && p.Size == plan.StagingSize && p.Kind == "part") ?? throw new IOException("The newly created partition was not found. Formatting was canceled.");
            if (created.Path == current.LinuxDevice || created.Mounts.Length > 0 || created.Children.Length > 0 || !Guid.TryParse(created.PartUuid, out var uuid))
                throw new IOException("Unexpected new partition state. Formatting was canceled.");
            LinuxStorage.CheckDevice(created.Path);
            await LinuxCommand.RunAsync("mkfs.ext4", ["-F", "-L", "ISO_INSTALL", created.Path], timeout: 0);
            string mount = "/mnt/bootbridge-" + uuid.ToString("N"); LinuxNative.AssertSafeDirectory("/mnt");
            if (Directory.Exists(mount)) throw new IOException("The mount point already exists.");
            Directory.CreateDirectory(mount);
            await LinuxCommand.RunAsync("mount", ["-t", "ext4", created.Path, mount], timeout: 0);
            return created.Path;
        }
        catch (Exception ex) { throw new IOException($"{ex.Message}\nThe filesystem size or GPT may already have changed. There is no automatic rollback. Plan and original partition table: {prefix}.json / .sfdisk. Check the disk before retrying.", ex); }
    }
}
