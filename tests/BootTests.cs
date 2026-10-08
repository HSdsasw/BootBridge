using System.Buffers.Binary;
using System.Text;
using IsoWriter.InternalBoot;

internal static class BootTests
{
    internal static async Task<int> RunAsync(string[] args)
    {
        int passed = 0;
        void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        LinuxImage Inspect(byte[] image) { using var memory = new MemoryStream(image); return LinuxImage.Inspect(memory); }
        Test("Ubuntu profile from ISO directory contents", () => Assert(Inspect(SyntheticIso()).Kernel == "/casper/vmlinuz"));
        Test("Debian Live profile", () => Assert(Inspect(SyntheticIso(true)).Arguments("/image.iso").Contains("findiso=")));
        Test("Rock Ridge .disk name", () => Assert(Inspect(SyntheticIso(rockRidge: true)).Family.Contains("Ubuntu")));
        Test("reject 32-bit kernel", () => { var data = SyntheticIso(); data[25 * 2048 + 0x236] = 0; Throws<InvalidDataException>(() => Inspect(data)); });
        Test("reject non-Linux kernel", () => { var data = SyntheticIso(); data[25 * 2048 + 0x202] = 0; Throws<InvalidDataException>(() => Inspect(data)); });
        Test("reject unknown distribution", () => { var data = SyntheticIso(); Array.Clear(data, 27 * 2048, 64); Throws<InvalidDataException>(() => Inspect(data)); });
        Test("reject out-of-bounds ISO directory", () => { var data = SyntheticIso(); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16 * 2048 + 158), uint.MaxValue); Throws<InvalidDataException>(() => Inspect(data)); });
        Test("reject truncated ISO directory", () => Throws<InvalidDataException>(() => Inspect(SyntheticIso()[..(24 * 2048)])));
        string id = "0123456789abcdef0123456789abcdef";
        var ubuntu = Inspect(SyntheticIso());
        string config = GrubConfig.Build(id, ubuntu);
        Test("GRUB uses unique absolute file path", () => Assert(config.Contains($"/IsoWriter-{id}/installer.iso") && config.Contains("loopback loop") && config.Contains("iso-scan/filename=")));
        Test("GRUB default returns without launching installer", () => Assert(config.Contains("set default=1") && config.Contains("exit")));
        Test("GRUB rejects command injection through id", () => Throws<InvalidDataException>(() => GrubConfig.Build("x; reboot", ubuntu)));
        Test("GRUB rejects arbitrary kernel commands", () => Throws<InvalidDataException>(() => GrubConfig.Build(id, ubuntu with { Kernel = "/vmlinuz; reboot" })));
        var esp = new BootPartition { Guid = "00112233-4455-6677-8899-aabbccddeeff", IsEsp = true, Gpt = true, Number = 2, Sector = 512, Offset = 1L << 20, Size = 100L << 20 };
        byte[] option = EfiOption.Build(esp, id);
        Test("EFI load option has correct device path lengths and GPT signature", () =>
        {
            int pathStart = 6 + Encoding.Unicode.GetByteCount("ISO Writer Linux " + id[..8] + '\0');
            Assert(BitConverter.ToUInt16(option, 4) == option.Length - pathStart);
            Assert(BitConverter.ToUInt32(option, pathStart + 4) == 2 && BitConverter.ToUInt64(option, pathStart + 8) == 2048);
            Assert(option.AsSpan(pathStart + 24, 16).SequenceEqual(Guid.Parse(esp.Guid).ToByteArray()));
            Assert(option[pathStart + 40] == 2 && option[pathStart + 41] == 2 && option[^4] == 0x7f);
        });
        Test("EFI rejects invalid installation id", () => Throws<InvalidDataException>(() => EfiOption.Build(esp, "../../Microsoft")));
        Test("EFI rejects non-GPT ESP", () => Throws<InvalidDataException>(() => EfiOption.Build(esp with { Gpt = false }, id)));
        Test("firmware append preserves existing default and order", () =>
        {
            var store = new FakeFirmware(); var manager = new FirmwareManager(store); ushort number = manager.ReserveNumber(); manager.Add(number, option);
            Assert(FirmwareManager.Decode(store.Read("BootOrder")).SequenceEqual(new ushort[] { 1, 7, number }));
            manager.Schedule(number, option); Assert(BitConverter.ToUInt16(store.Read("BootNext")!) == number);
            manager.Remove(number, option); Assert(FirmwareManager.Decode(store.Read("BootOrder")).SequenceEqual(new ushort[] { 1, 7 }));
            Assert(store.Read("BootNext") is null && store.Read(FirmwareManager.Name(number)) is null);
        });
        Test("refuse to overwrite another BootNext", () =>
        {
            var store = new FakeFirmware(); var manager = new FirmwareManager(store); manager.Add(0x2000, option); store.Write("BootNext", BitConverter.GetBytes((ushort)7));
            Throws<IOException>(() => manager.Schedule(0x2000, option)); Assert(BitConverter.ToUInt16(store.Read("BootNext")!) == 7);
        });
        Test("refuse to delete foreign EFI option", () =>
        {
            var store = new FakeFirmware(); var manager = new FirmwareManager(store); manager.Add(0x2000, option); store.Write("Boot2000", [3, 4]);
            Throws<IOException>(() => manager.Remove(0x2000, option)); Assert(store.Read("Boot2000")!.SequenceEqual(new byte[] { 3, 4 }));
        });
        Test("rollback orphan option when BootOrder write fails", () =>
        {
            var store = new FakeFirmware { FailOrderOnce = true }; var manager = new FirmwareManager(store);
            Throws<IOException>(() => manager.Add(0x2000, option)); manager.Remove(0x2000, option);
            Assert(store.Read("Boot2000") is null && FirmwareManager.Decode(store.Read("BootOrder")).SequenceEqual(new ushort[] { 1, 7 }));
        });
        Test("rollback preserves concurrently added foreign boot entry", () =>
        {
            var store = new FakeFirmware(); var manager = new FirmwareManager(store); manager.Add(0x2000, option);
            store.Write("BootOrder", new ushort[] { 1, 7, 0x2000, 9 }.SelectMany(BitConverter.GetBytes).ToArray()); manager.Remove(0x2000, option);
            Assert(FirmwareManager.Decode(store.Read("BootOrder")).SequenceEqual(new ushort[] { 1, 7, 9 }));
        });
        Test("reject malformed firmware list", () => Throws<IOException>(() => FirmwareManager.Decode([1])));
        var target = new BootPartition { Letter = "C", Volume = @"\\?\Volume{00112233-4455-6677-8899-aabbccddeeff}\", Guid = esp.Guid, DiskId = "test", Available = true, Gpt = true, FileSystem = "NTFS", Encryption = 0, Protection = 0, Size = 256L << 30, Free = 150L << 30, IsBoot = true };
        Test("system Windows partition allowed for file staging", () => Assert(target.BlockReason is null));
        Test("reject encrypted target even when protection suspended", () => Assert((target with { Encryption = 1, Protection = 0 }).BlockReason is not null));
        Test("reject unknown encryption status", () => Assert((target with { Encryption = -1 }).BlockReason is not null));
        Test("reject storing ISO on ESP", () => Assert((target with { IsEsp = true }).BlockReason is not null));
        Test("partition plan leaves staging and Linux space", () =>
        {
            var plan = PartitionPreparation.Calculate(target, 100L << 30, 12, 40);
            Assert(plan.NewSize == 204L << 30 && plan.StagingSize == 12L << 30 && plan.Unallocated == 40L << 30);
        });
        Test("reject shrink below Windows supported minimum", () => Throws<IOException>(() => PartitionPreparation.Calculate(target, 230L << 30, 12, 40)));
        Test("reject shrink exhausting free space", () => Throws<IOException>(() => PartitionPreparation.Calculate(target with { Free = 54L << 30 }, 100L << 30, 12, 40)));
        Test("reject invalid partition sizes", () => Throws<IOException>(() => PartitionPreparation.Calculate(target, 0, 0, 40)));
        if (args.Contains("--bootcatalog"))
        {
            var environment = await BootStorage.ReadAsync();
            Test("internal boot storage query (read only)", () => Assert(environment.Partitions.Length > 0));
            Console.WriteLine($"Boot storage: {environment.Partitions.Length} partitions; SecureBoot={environment.SecureBoot}.");
            foreach (var p in environment.Partitions) Console.WriteLine($"Disk {p.DiskNumber}, partition {p.Number}: {p.FileSystem}, ESP={p.IsEsp}, system={p.IsSystem}, conversion={p.Encryption}, protection={p.Protection}");
        }
        if (args.Contains("--grub-test-config"))
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "grub-test.cfg"), config, new UTF8Encoding(false));
        return passed;
    }

    internal static byte[] SyntheticIso(bool debian = false, bool rockRidge = false)
    {
        byte[] image = new byte[64 * 2048];
        void Record(int at, string name, uint block, uint size, bool directory, string? alternate = null)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(name);
            int length = 33 + encoded.Length + (encoded.Length % 2 == 0 ? 1 : 0);
            int baseLength = length;
            if (alternate is not null) length += 5 + Encoding.UTF8.GetByteCount(alternate);
            image[at] = (byte)length;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 2), block); BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 6), block);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 10), size); BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 14), size);
            image[at + 25] = directory ? (byte)2 : (byte)0; image[at + 28] = 1; image[at + 31] = 1;
            image[at + 32] = (byte)encoded.Length; encoded.CopyTo(image, at + 33);
            if (alternate is not null) { int start = at + baseLength; image[start] = (byte)'N'; image[start + 1] = (byte)'M'; image[start + 2] = (byte)(5 + Encoding.UTF8.GetByteCount(alternate)); image[start + 3] = 1; Encoding.UTF8.GetBytes(alternate).CopyTo(image, start + 5); }
        }
        image[16 * 2048] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(image, 16 * 2048 + 1); image[16 * 2048 + 6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16 * 2048 + 128), 2048);
        Record(16 * 2048 + 156, "\0", 20, 2048, true);
        Record(20 * 2048, rockRidge ? "_DISK" : ".DISK", 21, 2048, true, rockRidge ? ".disk" : null);
        Record(20 * 2048 + image[20 * 2048], debian ? "LIVE" : "CASPER", 22, 2048, true);
        int third = 20 * 2048 + image[20 * 2048]; third += image[third]; Record(third, "EFI", 23, 2048, true);
        Record(21 * 2048, "INFO;1", 27, 64, false);
        Record(22 * 2048, "VMLINUZ;1", 25, 2048, false);
        Record(22 * 2048 + image[22 * 2048], debian ? "INITRD.IMG;1" : "INITRD;1", 26, 2048, false);
        Record(23 * 2048, "BOOT", 24, 2048, true); Record(24 * 2048, "BOOTX64.EFI;1", 28, 2048, false);
        Encoding.ASCII.GetBytes("HdrS").CopyTo(image, 25 * 2048 + 0x202); image[25 * 2048 + 0x236] = 1;
        Encoding.ASCII.GetBytes(debian ? "Debian GNU/Linux Live amd64" : "Ubuntu 24.04 LTS amd64").CopyTo(image, 27 * 2048);
        return image;
    }
    private sealed class FakeFirmware : IFirmwareStore
    {
        private readonly Dictionary<string, byte[]> values = new() { ["BootOrder"] = [1, 0, 7, 0] };
        internal bool FailOrderOnce;
        public byte[]? Read(string name) => values.TryGetValue(name, out var value) ? value.ToArray() : null;
        public void Write(string name, byte[]? bytes)
        {
            if (name == "BootOrder" && FailOrderOnce) { FailOrderOnce = false; throw new IOException("Simulated NVRAM failure"); }
            if (bytes is null) values.Remove(name); else values[name] = bytes.ToArray();
        }
    }
}
