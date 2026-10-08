using System.Buffers.Binary;
using System.Text;
using IsoWriter;

if (args.Length == 2 && args[0] == "--inspect-remote")
{
    using var remote = new HttpRangeStream(args[1]);
    var profile = IsoWriter.InternalBoot.LinuxImage.Inspect(remote);
    Console.WriteLine($"PASS remote ISO: {profile}; read {remote.BytesRead} bytes only.");
    return;
}

int passed = 0;
void Check(string name, Action action)
{
    action(); passed++; Console.WriteLine("PASS " + name);
}
void Reject<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
byte[] Image()
{
    byte[] bytes = new byte[128 * 2048];
    bytes[510] = 0x55; bytes[511] = 0xAA;
    bytes[446] = 0x80; bytes[450] = 0x17;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(458, 4), (uint)bytes.Length / 512);
    foreach (var (block, type) in new[] { (16, 1), (17, 0), (18, 255) })
    {
        bytes[block * 2048] = (byte)type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, block * 2048 + 1);
        bytes[block * 2048 + 6] = 1;
    }
    Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(bytes, 17 * 2048 + 7);
    return bytes;
}
void Validate(byte[] bytes) { using var stream = new MemoryStream(bytes); IsoImage.Validate(stream); if (stream.Position != 0) throw new Exception("Position was not reset"); }
Check("hybrid ISO headers and stream rewind", () => Validate(Image()));
Check("reject truncated ISO", () => Reject<InvalidDataException>(() => Validate(new byte[200])));
Check("reject non aligned ISO", () => Reject<InvalidDataException>(() => Validate(new byte[128 * 2048 + 1])));
Check("reject missing MBR signature", () => { var b = Image(); b[511] = 0; Reject<InvalidDataException>(() => Validate(b)); });
Check("reject missing partition", () => { var b = Image(); b[450] = 0; Reject<InvalidDataException>(() => Validate(b)); });
Check("reject out of bounds partition", () => { var b = Image(); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(458, 4), uint.MaxValue); Reject<InvalidDataException>(() => Validate(b)); });
Check("reject invalid boot flag", () => { var b = Image(); b[446] = 42; Reject<InvalidDataException>(() => Validate(b)); });
Check("reject optical ISO without boot record", () => { var b = Image(); b[17 * 2048 + 7] = 0; Reject<InvalidDataException>(() => Validate(b)); });
Check("reject missing primary descriptor", () => { var b = Image(); b[16 * 2048] = 2; Reject<InvalidDataException>(() => Validate(b)); });

var disk = new Disk { Number = 3, UniqueId = "disk-id", Serial = "serial", Bus = "USB", Size = 64L << 30, SectorSize = 512 };
Check("allow ordinary separate target", () => Safety.Validate(disk, disk, 1L << 30, [0]));
void Block(string name, Disk changed) => Check(name, () => Reject<InvalidOperationException>(() => Safety.Validate(changed, changed, 1L << 30, [0])));
Block("block boot disk", disk with { IsBoot = true });
Block("block system disk", disk with { IsSystem = true });
Block("block system partition", disk with { Partitions = [new Partition { IsSystem = true }] });
Block("block readonly", disk with { IsReadOnly = true });
Block("block offline", disk with { IsOffline = true });
Block("block clustered", disk with { IsClustered = true });
Block("block RAID", disk with { Bus = "RAID" });
Block("block virtual disk", disk with { Bus = "File Backed Virtual" });
Block("block 4Kn", disk with { SectorSize = 4096 });
Block("block missing identity", disk with { UniqueId = "" });
Check("reject source on same disk", () => Reject<InvalidOperationException>(() => Safety.Validate(disk, disk, 1L << 30, [3])));
Check("reject spanned source touching target", () => Reject<InvalidOperationException>(() => Safety.Validate(disk, disk, 1L << 30, [0, 3])));
Check("reject oversized ISO", () => Reject<InvalidOperationException>(() => Safety.Validate(disk, disk, disk.Size + 512, [0])));
Check("reject empty ISO", () => Reject<InvalidOperationException>(() => Safety.Validate(disk, disk, 0, [0])));
Check("reject replaced disk", () => Reject<InvalidOperationException>(() => Safety.Validate(disk, disk with { UniqueId = "replacement" }, 1024, [0])));
Check("reject changed layout", () => Reject<InvalidOperationException>(() => Safety.Validate(disk, disk with { Partitions = [new Partition { Number = 1, Size = 1024 }] }, 1024, [0])));

Check("copy multiple blocks and final sector; readback SHA-256; clear stale tail", () =>
{
    var data = new byte[4 * 1024 * 1024 + 512];
    new Random(42).NextBytes(data);
    var device = new MemoryDevice(data.Length + 2 * 1024 * 1024);
    using var source = new MemoryStream(data);
    var reporter = new Reporter();
    string hash = ImageTransfer.WriteAndVerify(source, device, device.Data.Length, reporter, default);
    if (!device.Data.AsSpan(0, data.Length).SequenceEqual(data)) throw new Exception("Copy differs");
    if (device.Data[^1] != 0 || device.Data[data.Length] != 0xCC) throw new Exception("Wrong tail clearing bounds");
    if (hash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))) throw new Exception("Hash differs");
    if (reporter.Last != 100 || !device.Flushed) throw new Exception("Missing flush or progress");
});
Check("exact fit disk", () =>
{
    using var source = new MemoryStream(Image()); var device = new MemoryDevice((int)source.Length);
    ImageTransfer.WriteAndVerify(source, device, device.Data.Length, new Reporter(), default);
});
Check("detect corrupt readback", () =>
{
    using var source = new MemoryStream(Image()); var device = new MemoryDevice((int)source.Length) { Corrupt = true };
    Reject<IOException>(() => ImageTransfer.WriteAndVerify(source, device, device.Data.Length, new Reporter(), default));
});
Check("pre-cancel performs no writes", () =>
{
    using var source = new MemoryStream(Image()); var device = new MemoryDevice((int)source.Length);
    Reject<OperationCanceledException>(() => ImageTransfer.WriteAndVerify(source, device, device.Data.Length, new Reporter(), new CancellationToken(true)));
    if (device.Data.Any(b => b != 0xCC)) throw new Exception("Cancelled operation wrote data");
});
Check("cancel between write and verification", () =>
{
    using var source = new MemoryStream(Image()); var device = new MemoryDevice((int)source.Length);
    using var cts = new CancellationTokenSource();
    Reject<OperationCanceledException>(() => ImageTransfer.WriteAndVerify(source, device, device.Data.Length, new Reporter { Callback = () => cts.Cancel() }, cts.Token));
});
Check("flush failure is not success", () =>
{
    using var source = new MemoryStream(Image()); var device = new MemoryDevice((int)source.Length) { FailFlush = true };
    Reject<IOException>(() => ImageTransfer.WriteAndVerify(source, device, device.Data.Length, new Reporter(), default));
});

if (args.Contains("--catalog"))
{
    var disks = await DiskCatalog.LoadAsync();
    foreach (var d in disks)
        Console.WriteLine($"Disk {d.Number}: {d.Bus}, {Units.Size(d.Size)}, {d.Partitions.Length} partitions, protection: {d.BlockReason ?? "none"}");
    Check("catalog read-only integration", () => { if (disks.Length == 0) throw new Exception("No disks returned"); });
    using var source = File.OpenRead(typeof(Disk).Assembly.Location);
    int[] sourceDisks = NativeDisk.SourceDisks(source);
    Check("resolve actual source volume extents", () => { if (sourceDisks.Length == 0 || sourceDisks.Any(n => !disks.Any(d => d.Number == n))) throw new Exception("Unknown source disk"); });
    Check("enumerate source disk volumes without writes", () => { if (NativeDisk.VolumesOnDisk(sourceDisks[0]).Count == 0) throw new Exception("No volumes found"); });
}
Console.WriteLine($"{passed} tests passed. No physical disks were written.");
passed += await BootTests.RunAsync(args);
Console.WriteLine($"TOTAL: {passed} tests passed. Physical disk contents, partitions and firmware were not modified.");

sealed class Reporter : IProgress<WriteProgress>
{
    public int Last;
    public Action? Callback;
    public void Report(WriteProgress value) { if (value.Percent < Last) throw new Exception("Non-monotonic progress"); Last = value.Percent; Callback?.Invoke(); }
}
sealed class MemoryDevice(int size) : IImageDevice
{
    public byte[] Data = Enumerable.Repeat((byte)0xCC, size).ToArray();
    public bool Corrupt, Flushed, FailFlush;
    private int position;
    public void Seek(long value) => position = checked((int)value);
    public void Write(byte[] bytes, int count) { bytes.AsSpan(0, count).CopyTo(Data.AsSpan(position, count)); position += count; }
    public void Read(byte[] bytes, int count) { if (!Flushed) throw new Exception("Read before flush"); Data.AsSpan(position, count).CopyTo(bytes); if (Corrupt) bytes[0] ^= 1; position += count; }
    public void Flush() { if (FailFlush) throw new IOException("Simulated flush failure"); Flushed = true; }
}
