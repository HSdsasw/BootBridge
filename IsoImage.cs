using System.Buffers.Binary;
using System.Text;

namespace IsoWriter;

public static class IsoImage
{
    public static void Validate(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 34 * 2048 || stream.Length % 512 != 0)
            throw new InvalidDataException("Invalid ISO size: a hybrid ISO aligned to 512 bytes is required.");
        stream.Position = 0;
        byte[] mbr = new byte[512];
        stream.ReadExactly(mbr);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
            throw new InvalidDataException("The ISO has no MBR boot signature. Standard CD/DVD ISOs are not supported; select a hybrid Linux ISO.");
        bool hasPartition = false;
        for (int i = 446; i < 510; i += 16)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(i + 8, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(i + 12, 4));
            if (mbr[i + 4] == 0 || count == 0) continue;
            if (mbr[i] is not (0 or 0x80)) throw new InvalidDataException("Invalid MBR partition flag.");
            if (((ulong)start + count) * 512 > (ulong)stream.Length)
                throw new InvalidDataException("An MBR partition extends beyond the ISO bounds; this image is not supported.");
            hasPartition = true;
        }
        if (!hasPartition) throw new InvalidDataException("The ISO does not contain a hybrid image partition table.");
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
        if (!primary || !boot) throw new InvalidDataException("Bootable ISO9660 / El Torito was not found. This image type is not supported.");
        stream.Position = 0;
    }
}
