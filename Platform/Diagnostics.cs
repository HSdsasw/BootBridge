using System.Text.Json;
using IsoWriter.InternalBoot;

namespace IsoWriter.Platform;

internal static class Diagnostics
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            var disks = await DiskCatalog.LoadAsync(); var boot = await BootStorage.ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { Platform = Environment.OSVersion.ToString(), Disks = disks.Select(d => new { d.DevicePath, d.Size, d.BlockReason }), Boot = boot.Partitions.Select(p => new { p.Root, p.FileSystem, p.IsEsp, p.BlockReason }), boot.SecureBoot }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
