using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IsoWriter.InternalBoot;

internal sealed record BootPlan(string Id, string Source, long SourceWriteTime, LinuxImage Image, BootPartition Target, BootPartition Esp)
{
    internal string DirectoryName => "IsoWriter-" + Id;
    internal string Staging => Path.Combine(Target.Volume, DirectoryName);
    internal string IsoPath => "/" + DirectoryName + "/installer.iso";
    internal string EfiDirectory => Path.Combine(Esp.Volume, "EFI", DirectoryName);
    internal string Summary => Target.LinuxDevice.Length > 0 ? $"ISO: {Source}\n{Image.Family} • {Units.Size(Image.Length)}\n\nISO → {Staging}\nGRUB → {EfiDirectory}\n\nA separate UEFI entry will be appended to the boot order. Your current OS will remain the default.\nThis operation does not change partitions.\nDo not delete partition {Target.LinuxDevice} containing the ISO during installation. Use another partition or unallocated space for the new OS.\nAfter preparation, schedule the next boot separately and restart." : $"ISO: {Source}\r\nProfile: {Image.Family} • {Units.Size(Image.Length)}\r\n\r\n"
            + $"Storage: {Target.Root} — disk {Target.DiskNumber}, partition {Target.Number}\r\nFolder: {Path.Combine(Target.Root, DirectoryName)}\r\n"
        + $"EFI: disk {Esp.DiskNumber}, partition {Esp.Number}, directory EFI\\{DirectoryName}\r\n\r\n"
        + "ISO and GRUB will be copied and a separate UEFI entry added. Windows will remain the default OS.\r\n"
        + "No partitions will be formatted or resized.\r\n\r\n"
        + $"DO NOT FORMAT {Target.Letter}: or select 'erase entire disk' in the installer: this partition contains the ISO. "
        + "Use another partition or unallocated space for Linux.\r\n"
        + (Target.IsBoot ? "The ISO will be on the Windows partition. To replace Windows, first select another partition for the ISO.\r\n" : "")
        + "After preparation, click 'Boot Linux next', then use Restart in Windows.";
}

internal sealed record BootReceipt(string Id, BootPartition Target, BootPartition Esp, ushort BootNumber, string IsoHash, string LoaderHash, string ConfigHash)
{
    public string State { get; set; } = "Preparing";
}

internal static class GrubConfig
{
    internal static string Build(string id, LinuxImage image)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !new[] { "/casper/vmlinuz", "/live/vmlinuz" }.Contains(image.Kernel)
            || !new[] { "/casper/initrd", "/casper/initrd.lz", "/casper/initrd.img", "/casper/initrd.gz", "/live/initrd", "/live/initrd.img", "/live/initrd.lz", "/live/initrd.gz" }.Contains(image.Initrd))
            throw new InvalidDataException("Unsupported boot profile.");
        string path = $"/IsoWriter-{id}/installer.iso";
        return $$"""
            set timeout=30
            set default=1
            menuentry 'Start Linux live installer' {
                insmod part_gpt
                insmod ntfs
                insmod ext2
                insmod iso9660
                insmod loopback
                unset isopart
                search --no-floppy --file --set=isopart {{path}}
                if [ -z "$isopart" ]; then
                    echo 'ISO not found. Return to your OS and prepare it again.'
                    sleep 15
                else
                    loopback loop ($isopart){{path}}
                    linux (loop){{image.Kernel}} {{image.Arguments(path)}}
                    initrd (loop){{image.Initrd}}
                fi
            }
            menuentry 'Return to firmware / existing OS' {
                exit
            }
            """ + "\n";
    }
}

internal static class BootInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static async Task<BootPlan> PlanAsync(string path, BootPartition selected, CancellationToken token)
    {
        var env = await BootStorage.ReadAsync(token); env.Validate();
        var target = env.Partitions.SingleOrDefault(p => p.Identity == selected.Identity && p.Letter == selected.Letter)
            ?? throw new IOException("The selected partition changed. Refresh the list.");
        if (target.BlockReason is { } reason) throw new IOException(reason);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var profile = await Task.Run(() => LinuxImage.Inspect(file), token);
        if (target.Free < profile.Length + 256L * 1024 * 1024) throw new IOException("Not enough free space: the ISO size plus 256 MiB is required.");
        VerifyLoader();
        return new(Guid.NewGuid().ToString("N"), Path.GetFullPath(path), File.GetLastWriteTimeUtc(path).Ticks, profile, target, env.Esp);
    }
    internal static string VerifyLoader()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "grubx64.efi");
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "grubx64.sha256")).Trim();
        string actual = HashFile(path);
        if (expected.Length != 64 || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("The GRUB file is missing or corrupted. Restore the application package.");
        return actual;
    }
    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file));
    }
    internal static void NoLinks(string path)
    {
        for (var part = new DirectoryInfo(path); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked directories are not allowed: " + part.FullName);
    }
    private static void Save(string path, BootReceipt receipt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, JsonOptions));
        using (var file = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    internal static async Task<string> InstallAsync(BootPlan plan, IProgress<string> progress, CancellationToken token)
    {
        if (!Guid.TryParseExact(plan.Id, "N", out _)) throw new IOException("Invalid installation ID.");
        var env = await BootStorage.ReadAsync(token); env.Validate();
        var current = env.Partitions.SingleOrDefault(p => p.Identity == plan.Target.Identity && p.Letter == plan.Target.Letter);
        if (current is null || current.BlockReason is not null || env.Esp.Identity != plan.Esp.Identity || current.Free < plan.Image.Length + 256L * 1024 * 1024)
            throw new IOException("Partitions or free space changed. Generate a new plan.");
        plan = plan with { Target = current, Esp = env.Esp };
        if (OperatingSystem.IsLinux()) { Platform.LinuxNative.RequireRoot(); Platform.LinuxNative.AssertSafeDirectory(plan.Target.Volume); Platform.LinuxNative.AssertSafeDirectory(Path.Combine(plan.Esp.Volume, "EFI")); }
        using var source = new FileStream(plan.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != plan.Image.Length || File.GetLastWriteTimeUtc(plan.Source).Ticks != plan.SourceWriteTime || LinuxImage.Inspect(source) != plan.Image)
            throw new IOException("The ISO changed after validation. Select it again.");
        IFirmwareStore store = OperatingSystem.IsLinux() ? new Platform.LinuxFirmware() : new WindowsFirmware(); var firmware = new FirmwareManager(store);
        ushort number = firmware.ReserveNumber();
        byte[] option = EfiOption.Build(plan.Esp, plan.Id);
        string loaderHash = VerifyLoader();
        if (Directory.Exists(plan.Staging) || Directory.Exists(plan.EfiDirectory)) throw new IOException("The installation directory already exists.");
        NoLinks(plan.Target.Volume); NoLinks(Path.Combine(plan.Esp.Volume, "EFI"));
        bool stagingCreated = false, efiCreated = false, registrationAttempted = false;
        BootReceipt? receipt = null;
        string receiptPath = Path.Combine(plan.Staging, "installation.json");
        try
        {
            Directory.CreateDirectory(plan.Staging); stagingCreated = true;
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(plan.Staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // Hold the directory without FILE_SHARE_DELETE to prevent replacement by a junction mid-copy.
            using var directoryLock = DirectoryGuard.Open(plan.Staging);
            progress.Report("Copying ISO…");
            string iso = Path.Combine(plan.Staging, "installer.iso");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(iso, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous))
            {
                NoLinks(plan.Staging);
                byte[] buffer = new byte[4 * 1024 * 1024]; long copied = 0;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, token); if (count == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, count), token); hash.AppendData(buffer, 0, count); copied += count;
                    progress.Report($"Copying ISO: {copied * 100 / source.Length}% • {Units.Size(copied)} / {Units.Size(source.Length)}");
                }
                destination.Flush(true);
            }
            string isoHash = Convert.ToHexString(hash.GetHashAndReset());
            progress.Report("Verifying the copied ISO with SHA-256…");
            using (var copied = File.OpenRead(iso))
            {
                if (Convert.ToHexString(await SHA256.HashDataAsync(copied, token)) != isoHash) throw new IOException("ISO copy verification failed.");
                if (LinuxImage.Inspect(copied) != plan.Image) throw new IOException("The ISO contents changed during copying.");
            }
            token.ThrowIfCancellationRequested();
            string config = GrubConfig.Build(plan.Id, plan.Image);
            string configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
            receipt = new(plan.Id, plan.Target, plan.Esp, number, isoHash, loaderHash, configHash);
            Save(receiptPath, receipt); // persisted before firmware changes; recovery works after a crash
            File.WriteAllText(Path.Combine(plan.Staging, "PLAN.txt"), plan.Summary, new UTF8Encoding(false));
            byte[] backup = store.Read("BootOrder") ?? throw new IOException("BootOrder is missing.");
            File.WriteAllBytes(Path.Combine(plan.Staging, "BootOrder.before.bin"), backup);
            progress.Report("Copying GRUB to a separate EFI directory…");
            Directory.CreateDirectory(plan.EfiDirectory); efiCreated = true;
            using var efiLock = DirectoryGuard.Open(plan.EfiDirectory);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "assets", "grubx64.efi"), Path.Combine(plan.EfiDirectory, "grubx64.efi"), false);
            File.WriteAllText(Path.Combine(plan.EfiDirectory, "grub.cfg"), config, new UTF8Encoding(false));
            foreach (string name in new[] { "grubx64.efi", "grub.cfg" })
            {
                using var persisted = new FileStream(Path.Combine(plan.EfiDirectory, name), FileMode.Open, FileAccess.Write, FileShare.Read);
                persisted.Flush(true);
            }
            if (HashFile(Path.Combine(plan.EfiDirectory, "grubx64.efi")) != loaderHash || HashFile(Path.Combine(plan.EfiDirectory, "grub.cfg")) != configHash)
                throw new IOException("EFI file verification failed.");
            token.ThrowIfCancellationRequested();
            // A short non-cancellable commit. No reboot and no change to Windows' boot entry.
            progress.Report("Adding a separate UEFI entry…");
            registrationAttempted = true;
            firmware.Add(number, option);
            receipt.State = "Installed"; Save(receiptPath, receipt);
            progress.Report("Done. The ISO is prepared; your current OS remains the default.");
            return receiptPath;
        }
        catch (Exception failure)
        {
            try
            {
                if (registrationAttempted) firmware.Remove(number, option);
                if (efiCreated) DeleteKnown(plan.EfiDirectory, ["grub.cfg", "grubx64.efi"]);
                if (stagingCreated) DeleteKnown(plan.Staging, ["installer.iso", "installation.json", "installation.json.tmp", "PLAN.txt", "BootOrder.before.bin"]);
            }
            catch (Exception rollback)
            {
                throw new IOException($"{failure.Message}\nRollback did not complete: {rollback.Message}\nSaved installation record: {receiptPath}. Use 'Open installation' to open it and remove the boot entry.", failure);
            }
            throw;
        }
    }
    private static void DeleteKnown(string directory, string[] names)
    {
        NoLinks(directory);
        foreach (string name in names)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A link was found in place of an installation file.");
                File.Delete(path);
            }
        }
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
    internal static BootReceipt LoadReceipt(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new IOException("Invalid installation record.");
        NoLinks(Path.GetDirectoryName(path)!);
        var receipt = JsonSerializer.Deserialize<BootReceipt>(File.ReadAllText(path)) ?? throw new IOException("The installation record is empty.");
        if (!Guid.TryParseExact(receipt.Id, "N", out _) || receipt.BootNumber is < 0x2000 or >= 0x2100
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "IsoWriter-" + receipt.Id, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) != "installation.json") throw new IOException("Invalid installation record.");
        return receipt;
    }
    internal static async Task ManageAsync(string receiptPath, bool schedule)
    {
        var receipt = LoadReceipt(receiptPath);
        var env = await BootStorage.ReadAsync();
        if (schedule) env.Validate();
        if (env.Esp.Identity != receipt.Esp.Identity) throw new IOException("The EFI partition changed.");
        var currentTarget = env.Partitions.SingleOrDefault(p => p.Identity == receipt.Target.Identity)
            ?? throw new IOException("The partition containing the ISO was not found.");
        string directory = Path.Combine(env.Esp.Volume, "EFI", "IsoWriter-" + receipt.Id);
        string expectedReceipt = Path.Combine(currentTarget.Volume, "IsoWriter-" + receipt.Id, "installation.json");
        if (!string.Equals(Path.GetFullPath(receiptPath), Path.GetFullPath(expectedReceipt), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The installation record must be in its original installation directory on the selected partition.");
        NoLinks(directory);
        byte[] option = EfiOption.Build(env.Esp, receipt.Id);
        if (OperatingSystem.IsLinux()) { Platform.LinuxNative.RequireRoot(); Platform.LinuxNative.AssertSafeDirectory(Path.GetDirectoryName(receiptPath)!); Platform.LinuxNative.AssertSafeDirectory(Path.Combine(env.Esp.Volume, "EFI")); }
        var firmware = new FirmwareManager(OperatingSystem.IsLinux() ? new Platform.LinuxFirmware() : new WindowsFirmware());
        if (schedule)
        {
            if (receipt.State != "Installed" || currentTarget.BlockReason is not null) throw new IOException("The installation is not ready to boot.");
            string iso = Path.Combine(currentTarget.Volume, "IsoWriter-" + receipt.Id, "installer.iso");
            NoLinks(Path.GetDirectoryName(iso)!);
            if (HashFile(Path.Combine(directory, "grubx64.efi")) != receipt.LoaderHash || HashFile(Path.Combine(directory, "grub.cfg")) != receipt.ConfigHash
                || HashFile(iso) != receipt.IsoHash) throw new IOException("The installation files changed. Prepare the ISO again.");
            firmware.Schedule(receipt.BootNumber, option);
        }
        else
        {
            firmware.Remove(receipt.BootNumber, option);
            // Keep ISO and receipts intentionally. Never recursively delete user-controlled directories.
            foreach (var item in new[] { ("grubx64.efi", receipt.LoaderHash), ("grub.cfg", receipt.ConfigHash) })
            {
                string file = Path.Combine(directory, item.Item1);
                if (File.Exists(file) && HashFile(file) == item.Item2) File.Delete(file);
            }
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            receipt.State = "Removed"; Save(receiptPath, receipt);
        }
    }
}
