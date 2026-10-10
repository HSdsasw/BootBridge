using System.Text.Json;
using IsoWriter;
using IsoWriter.InternalBoot;
using IsoWriter.Platform;

internal static class LinuxTests
{
    internal static int Run()
    {
        int count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
        void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected rejection"); }
        var disk = LinuxStorage.Parse("""
            {"blockdevices":[{"name":"/dev/nvme0n1","kname":"/dev/nvme0n1","type":"disk","size":274877906944,"log-sec":512,"ro":false,"pttype":"gpt","maj:min":"259:0","mountpoints":[null],"children":[{"name":"/dev/nvme0n1p1","kname":"/dev/nvme0n1p1","type":"part","size":"137438953472","start":2048,"fstype":"ext4","mountpoints":[null]}]}]}
            """)[0];
        Test("lsblk numeric and string sizes, paths and null mounts", () => Assert(disk.Size == 256L << 30 && disk.Children[0].Size == 128L << 30 && disk.Children[0].Kernel == "nvme0n1p1" && disk.Mounts.Length == 0));
        Test("Linux refuses mounted root disk", () => Assert(LinuxStorage.RawBlockReason(disk with { Children = [disk.Children[0] with { Mounts = ["/"] }] }) is not null));
        Test("Linux refuses active swap", () => Assert(LinuxStorage.RawBlockReason(disk with { Children = [disk.Children[0] with { Mounts = ["[SWAP]"] }] }) is not null));
        Test("Linux refuses layered encrypted device", () => Assert(LinuxStorage.RawBlockReason(disk with { Children = [disk.Children[0] with { Children = [new LinuxBlock { Kind = "crypt" }] }] }) is not null));
        Test("Linux refuses read-only disk", () => Assert(LinuxStorage.RawBlockReason(disk with { ReadOnly = true }) is not null));
        Test("reject unsafe device paths", () => { foreach (string path in new[] { "/dev/", "/dev/../sda", "/tmp/sda", "/dev/mapper/root", "/dev/sda x" }) Reject(() => LinuxStorage.CheckDevice(path)); });
        var p = new BootPartition { LinuxDevice = "/dev/sda2", Available = true, Gpt = true, FileSystem = "ext4", Guid = "00112233-4455-6677-8899-aabbccddeeff", Volume = "/mnt/installer", Size = 256L << 30, Free = 150L << 30, Sector = 512, Encryption = 0 };
        Test("Linux ext4 file staging accepted", () => Assert(p.BlockReason is null));
        Test("Linux unmounted file staging rejected", () => Assert((p with { Volume = "" }).BlockReason is not null));
        Test("Linux Btrfs and LUKS staging rejected", () => Assert((p with { FileSystem = "btrfs" }).BlockReason is not null && (p with { Encryption = 1 }).BlockReason is not null));
        Test("Linux ESP cannot hold ISO", () => Assert((p with { IsEsp = true }).BlockReason is not null));
        Test("offline ext4 partition plan", () => { var plan = LinuxPartitions.Calculate(p with { Volume = "" }, 100L << 30, p.Free, 12, 40); Assert(plan.NewSize == 204L << 30 && plan.Unallocated == 40L << 30); });
        Test("mounted ext4 shrink rejected", () => Reject(() => LinuxPartitions.Calculate(p, 100L << 30, p.Free, 12, 40)));
        Test("ext4 minimum and free space enforced", () => { Reject(() => LinuxPartitions.Calculate(p with { Volume = "" }, 220L << 30, p.Free, 12, 40)); Reject(() => LinuxPartitions.Calculate(p with { Volume = "" }, 100L << 30, 54L << 30, 12, 40)); });
        Test("Linux NTFS shrink rejected", () => Reject(() => LinuxPartitions.Calculate(p with { Volume = "", FileSystem = "NTFS" }, 100L << 30, p.Free, 12, 40)));
        Test("GRUB loads ext4 reader", () => { using var iso = new MemoryStream(BootTests.SyntheticIso()); Assert(GrubConfig.Build("0123456789abcdef0123456789abcdef", LinuxImage.Inspect(iso)).Contains("insmod ext2")); });
        Test("helper request retains partition identity", () => { var request = new WorkRequest("boot-plan", JsonSerializer.SerializeToElement(new BootRequest("/tmp/a.iso", p))); var roundTrip = JsonSerializer.Deserialize<WorkRequest>(JsonSerializer.Serialize(request))!; Assert(roundTrip.Payload.Deserialize<BootRequest>()!.Target.Identity == p.Identity); });
        Test("helper refuses unknown operations", () => Reject(() => HelperHost.DispatchAsync(new WorkRequest("shell", JsonSerializer.SerializeToElement("echo unsafe")), new ActionProgress<string>(_ => { }), default).GetAwaiter().GetResult()));
        return count;
    }
}
