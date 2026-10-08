using System.Diagnostics;
using System.Security.Cryptography;

namespace IsoWriter;

internal interface IImageDevice
{
    void Seek(long position);
    void Write(byte[] bytes, int count);
    void Read(byte[] bytes, int count);
    void Flush();
}

internal static class ImageTransfer
{
    internal static string WriteAndVerify(Stream source, IImageDevice device, long diskSize,
        IProgress<WriteProgress> progress, CancellationToken token)
    {
        long length = source.Length;
        if (length <= 0 || length % 512 != 0 || diskSize < length || diskSize % 512 != 0)
            throw new InvalidDataException("Недопустимые размеры образа или диска.");
        token.ThrowIfCancellationRequested();
        var bytes = new byte[4 * 1024 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long tail = Math.Min(1024 * 1024, diskSize - length);
        if (tail > 0)
        {
            device.Seek(diskSize - tail);
            device.Write(bytes, (int)tail);
        }
        device.Seek(0);
        source.Position = 0;
        long done = 0;
        var watch = Stopwatch.StartNew();
        while (done < length)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(bytes.Length, length - done);
            source.ReadExactly(bytes.AsSpan(0, count));
            device.Write(bytes, count);
            hash.AppendData(bytes, 0, count);
            done += count;
            progress.Report(new("Запись ISO", (int)(done * 50 / length), $"{Units.Size(done)} / {Units.Size(length)} • {done / 1048576.0 / Math.Max(watch.Elapsed.TotalSeconds, .1):0.0} MiB/с", true));
        }
        device.Flush();
        byte[] sourceHash = hash.GetHashAndReset();
        device.Seek(0);
        done = 0;
        while (done < length)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(bytes.Length, length - done);
            device.Read(bytes, count);
            hash.AppendData(bytes, 0, count);
            done += count;
            progress.Report(new("Проверка SHA-256", 50 + (int)(done * 50 / length), $"Прочитано {Units.Size(done)} / {Units.Size(length)}", true));
        }
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, hash.GetHashAndReset()))
            throw new IOException("SHA-256 записанных данных не совпадает с ISO. Не используйте этот носитель для загрузки.");
        return Convert.ToHexString(sourceHash);
    }
}
