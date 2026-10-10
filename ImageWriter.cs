using Microsoft.Win32.SafeHandles;

namespace IsoWriter;

public sealed record WriteProgress(string Stage, int Percent, string Detail, bool Destructive = false);
public sealed record WriteResult(string Sha256, string? Warning);

public static class ImageWriter
{
    public static async Task<WriteResult> WriteAsync(Disk selected, string imagePath, IProgress<WriteProgress> progress, CancellationToken token)
    {
        if (OperatingSystem.IsLinux()) return await Platform.LinuxRawWriter.WriteAsync(selected, imagePath, progress, token);
        progress.Report(new("Checking disk and ISO", 0, "Identifying the device again"));
        using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, RawDisk.BufferSize, FileOptions.SequentialScan);
        IsoImage.Validate(source);
        int[] sourceDisks = NativeDisk.SourceDisks(source);
        Safety.Validate(selected, selected, source.Length, sourceDisks);
        // Keep the device handle open across refresh and writing: a reused PhysicalDrive number
        // after unplugging cannot silently redirect writes to a newly attached disk.
        using var diskHandle = NativeDisk.Open(selected.DevicePath, true, true);
        var disks = await DiskCatalog.LoadAsync(token);
        var current = disks.SingleOrDefault(d => d.Number == selected.Number) ?? throw new IOException("The disk was disconnected.");
        Safety.Validate(selected, current, source.Length, sourceDisks);
        if (NativeDisk.Length(diskHandle) != selected.Size || NativeDisk.Number(diskHandle) != selected.Number)
            throw new IOException("The opened disk identity or size does not match the selection.");

        byte[] layout = NativeDisk.Control(diskHandle, 0x00070050, 1024 * 1024);
        return await Task.Run(() => WriteLocked(selected, source, diskHandle, layout, progress, token), token);
    }

    private static WriteResult WriteLocked(Disk disk, FileStream source, SafeFileHandle handle, byte[] layout, IProgress<WriteProgress> progress, CancellationToken token)
    {
        var locks = new List<SafeFileHandle>();
        bool touched = false;
        try
        {
            var volumes = NativeDisk.VolumesOnDisk(disk.Number);
            var expected = disk.Partitions.SelectMany(p => p.Paths).Where(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
            if (expected.Any(p => !volumes.Contains(p, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("The volume list changed. Refresh the disk list.");
            foreach (string volumePath in volumes)
            {
                token.ThrowIfCancellationRequested();
                var volume = NativeDisk.Open(volumePath, true);
                locks.Add(volume);
                if (!NativeDisk.DiskNumbers(volume).SequenceEqual(new[] { disk.Number }))
                    throw new IOException("The volume no longer belongs to the selected disk.");
                NativeDisk.Control(volume, NativeDisk.LockVolume);
            }
            // Nothing has been written if even one volume cannot be locked (pagefile, open files, etc.).
            foreach (var volume in locks) NativeDisk.Control(volume, NativeDisk.DismountVolume);
            if (!layout.AsSpan().SequenceEqual(NativeDisk.Control(handle, 0x00070050, 1024 * 1024)))
                throw new IOException("The disk layout changed during preparation. Writing was canceled.");
            token.ThrowIfCancellationRequested();
            using var raw = new RawDisk(handle);
            touched = true;
            progress.Report(new("Writing ISO", 0, "Physical disk overwrite started", true));
            string sourceHash = ImageTransfer.WriteAndVerify(source, raw, disk.Size, progress, token);
            foreach (var volume in locks) volume.Dispose();
            locks.Clear();
            string? warning = null;
            try { NativeDisk.Control(handle, 0x00070140); }
            catch (IOException) { warning = "The image was verified, but Windows could not refresh the partition table. Reconnect the drive or restart the computer."; }
            return new(sourceHash, warning);
        }
        catch (Exception ex) when (touched)
        {
            throw new IOException((ex is OperationCanceledException ? "Writing or verification was stopped." : ex.Message)
                + " The disk has already been modified and may not boot. Write the entire image again.", ex);
        }
        finally { foreach (var volume in locks) volume.Dispose(); }
    }
}
