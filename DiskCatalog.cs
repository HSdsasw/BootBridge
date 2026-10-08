using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IsoWriter;

public static class DiskCatalog
{
    // Fixed script: no user supplied paths or strings are interpolated into PowerShell.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        Import-Module Storage
        $result = @(Get-Disk | ForEach-Object {
          $d = $_
          $parts = @()
          if ($d.PartitionStyle -ne 'RAW') {
            $parts = @(Get-Partition -DiskNumber $d.Number | ForEach-Object {
              [pscustomobject]@{
                Number = [int]$_.PartitionNumber; Letter = ([string]$_.DriveLetter).Trim([char]0)
                Size = [long]$_.Size; Offset = [long]$_.Offset; Type = [string]$_.Type
                IsBoot = [bool]$_.IsBoot; IsSystem = [bool]$_.IsSystem
                Paths = @($_.AccessPaths | Where-Object { $_ })
              }
            })
          }
          [pscustomobject]@{
            Number = [int]$d.Number; Model = [string]$d.FriendlyName
            Serial = ([string]$d.SerialNumber).Trim(); UniqueId = [string]$d.UniqueId
            Bus = [string]$d.BusType; Size = [long]$d.Size
            SectorSize = [int]$d.LogicalSectorSize
            IsBoot = [bool]$d.IsBoot; IsSystem = [bool]$d.IsSystem
            IsOffline = [bool]$d.IsOffline; IsReadOnly = [bool]$d.IsReadOnly
            IsClustered = [bool]$d.IsClustered; Partitions = $parts
          }
        })
        ConvertTo-Json -InputObject $result -Depth 6 -Compress
        """;

    public static async Task<Disk[]> LoadAsync(CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить службу обнаружения дисков.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new IOException("Не удалось получить список дисков: " + await error);
        return JsonSerializer.Deserialize<Disk[]>(await output) ?? [];
    }
}
