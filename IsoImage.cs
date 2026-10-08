using System.Buffers.Binary;
using System.Text;

namespace IsoWriter;

public static class IsoImage
{
    public static void Validate(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 34 * 2048 || stream.Length % 512 != 0)
            throw new InvalidDataException("Некорректный размер ISO: необходим гибридный ISO с выравниванием 512 байт.");
        stream.Position = 0;
        byte[] mbr = new byte[512];
        stream.ReadExactly(mbr);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            throw new InvalidDataException("В ISO нет загрузочной подписи MBR. Обычные CD/DVD ISO не поддерживаются; выберите hybrid ISO Linux.");
        bool hasPartition = false;
        for (int i = 446; i < 510; i += 16)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(i + 8, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(i + 12, 4));
            if (mbr[i + 4] == 0 || count == 0) continue;
            if (mbr[i] is not (0 or 0x80)) throw new InvalidDataException("Некорректный флаг раздела MBR.");
            if (((ulong)start + count) * 512 > (ulong)stream.Length)
                throw new InvalidDataException("Раздел MBR выходит за границы ISO; этот образ не поддерживается.");
            hasPartition = true;
        }
        if (!hasPartition) throw new InvalidDataException("ISO не содержит таблицу разделов гибридного образа.");
        bool primary = false, boot = false;
        byte[] sector = new byte[2048];
        for (int i = 16; i < 80 && (long)(i + 1) * 2048 <= stream.Length; i++)
        {
            stream.Position = i * 2048L;
            stream.ReadExactly(sector);
            if (Encoding.ASCII.GetString(sector, 1, 5) != "CD001" || sector[6] != 1) break;
            primary |= sector[0] == 1;
            boot |= sector[0] == 0 && Encoding.ASCII.GetString(sector, 7, 23).TrimEnd('\0', ' ') == "EL TORITO SPECIFICATION";
            if (sector[0] == 255) break;
        }
        if (!primary || !boot) throw new InvalidDataException("Не найден загрузочный ISO9660 / El Torito. Этот тип образа не поддерживается.");
        stream.Position = 0;
    }
}
