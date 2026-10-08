using System.Runtime.InteropServices;
using System.Text.Json;

namespace IsoWriter.InternalBoot;

internal sealed record BootPartition
{
    public int DiskNumber { get; init; }
    public int Number { get; init; }
    public string DiskId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Guid { get; init; } = "";
    public string Volume { get; init; } = "";
    public string Letter { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long Offset { get; init; }
    public long Size { get; init; }
    public long Free { get; init; }
    public int Sector { get; init; }
    public bool IsEsp { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool Available { get; init; }
    public bool Gpt { get; init; }
    public int Encryption { get; init; } = -1;
    public int Protection { get; init; } = -1;
    public string Identity => $"{DiskId}|{Guid}|{Volume}|{Offset}|{Size}|{Sector}";
    public string Root => Letter + @":\";
    public override string ToString() => $"{Letter}: • диск {DiskNumber}, раздел {Number} • {Model} • свободно {Units.Size(Free)}{(IsBoot ? " • Windows" : "")}";
    internal string? BlockReason => !Available || !Gpt ? "Нужен доступный обычный GPT-диск (не RAID/Storage Spaces)."
        : IsEsp || IsSystem ? "EFI-раздел нельзя использовать для хранения ISO."
        : FileSystem != "NTFS" ? "Для ISO нужен NTFS-раздел."
        : Letter.Length != 1 || Letter[0] is < 'A' or > 'Z' ? "Раздел должен иметь букву диска."
        : Encryption != 0 ? "Раздел зашифрован BitLocker или состояние шифрования неизвестно. Выберите незашифрованный раздел."
        : !System.Guid.TryParse(Guid, out _) || !Volume.StartsWith(@"\\?\Volume{") ? "Не удалось идентифицировать раздел."
        : null;
}

internal sealed record BootEnvironment
{
    public int SecureBoot { get; init; } = -1;
    public string DetectionWarning { get; init; } = "";
    public BootPartition[] Partitions { get; init; } = [];
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFirmwareType(out uint type);
    internal BootPartition Esp => Partitions.SingleOrDefault(p => p.IsEsp && p.IsSystem) ?? throw new IOException("Не найден единственный системный EFI-раздел.");
    internal void Validate()
    {
        if (!string.IsNullOrWhiteSpace(DetectionWarning)) throw new IOException("Не удалось проверить BitLocker. Запустите IsoWriter.exe от имени администратора. " + DetectionWarning);
        if (!Environment.Is64BitOperatingSystem || RuntimeInformation.OSArchitecture != Architecture.X64 || !GetFirmwareType(out uint type) || type != 2)
            throw new IOException("Этот режим поддерживает Windows x64 с загрузкой UEFI. Legacy BIOS не поддерживается.");
        if (SecureBoot != 0) throw new IOException("Этот GRUB не подписан для Secure Boot. Отключите Secure Boot в настройках UEFI и повторите проверку; приложение не меняет эту настройку.");
        var os = Partitions.SingleOrDefault(p => p.IsBoot) ?? throw new IOException("Не удалось определить раздел Windows.");
        if (os.Protection != 0) throw new IOException("Защита BitLocker на Windows активна или неизвестна. Сохраните ключ восстановления и приостановите защиту перед изменением загрузки.");
        if (!Esp.Available || !Esp.Gpt || Esp.FileSystem != "FAT32" || Esp.Sector <= 0 || Esp.Free < 32L * 1024 * 1024)
            throw new IOException("EFI-раздел должен быть доступным FAT32/GPT со свободными 32 MiB.");
    }
}

internal static class BootStorage
{
    private const string Query = """
        $enc = @{}
        $warning = ''
        try {
        $encrypted = @(Get-CimInstance -Namespace 'root/CIMV2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume)
        foreach ($v in $encrypted) {
          $conversion = Invoke-CimMethod -InputObject $v -MethodName GetConversionStatus
          $protection = Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus
          if ($conversion.ReturnValue -ne 0 -or $protection.ReturnValue -ne 0) { throw 'Cannot query BitLocker status.' }
          $enc[$v.DeviceID.TrimEnd('\').ToLowerInvariant()] = @([int]$conversion.ConversionStatus,[int]$protection.ProtectionStatus)
        }
        } catch { $warning = $_.Exception.Message; $enc = @{} }
        $parts = @(foreach ($d in Get-Disk) {
          if ($d.PartitionStyle -eq 'RAW') { continue }
          foreach ($p in Get-Partition -DiskNumber $d.Number) {
            $v = $p | Get-Volume -ErrorAction SilentlyContinue
            if (-not $v) { continue }
            $volume = [string]$v.Path
            $state = $enc[$volume.TrimEnd('\').ToLowerInvariant()]
            # FAT32 ESPs are not BitLocker volumes; NTFS must have an explicit query result.
            $conversion = -1; $protection = -1
            if ($state) { $conversion = $state[0]; $protection = $state[1] }
            if ($v.FileSystemType -eq 'FAT32') { $conversion = 0; $protection = 0 }
            [pscustomobject]@{
              DiskNumber=[int]$d.Number; Number=[int]$p.PartitionNumber; DiskId=[string]$d.UniqueId
              Model=[string]$d.FriendlyName; Guid=[string]$p.Guid; Volume=$volume
              Letter=([string]$p.DriveLetter).Trim([char]0); FileSystem=[string]$v.FileSystemType
              Offset=[long]$p.Offset; Size=[long]$p.Size; Free=[long]$v.SizeRemaining; Sector=[int]$d.LogicalSectorSize
              IsEsp=(([string]$p.GptType).Trim('{}') -eq 'c12a7328-f81f-11d2-ba4b-00a0c93ec93b'); IsSystem=[bool]$p.IsSystem; IsBoot=[bool]$p.IsBoot
              Available=(!$d.IsOffline -and !$d.IsReadOnly -and !$d.IsClustered -and ([string]$d.BusType -in @('USB','SATA','ATA','SCSI','NVMe','SAS','SD','MMC')))
              Gpt=([string]$d.PartitionStyle -eq 'GPT'); Encryption=[int]$conversion; Protection=[int]$protection
            }
          }
        })
        $secure = -1
        try { $secure = [int](Confirm-SecureBootUEFI) } catch { }
        [pscustomobject]@{ SecureBoot=$secure; DetectionWarning=$warning; Partitions=$parts } | ConvertTo-Json -Depth 5 -Compress
        """;
    internal static async Task<BootEnvironment> ReadAsync(CancellationToken token = default) =>
        JsonSerializer.Deserialize<BootEnvironment>(await SystemProcess.PowerShell(Query, token)) ?? throw new IOException("Пустые сведения о дисках.");
}
