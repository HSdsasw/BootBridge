# GNU GRUB 2.14

`grubx64.efi` is a standalone x86_64-efi image generated using the official GNU GRUB 2.14 Windows tools. It is not signed for UEFI Secure Boot.

BootBridge 0.2 embeds the `ext2` module (which also reads supported ext4 filesystems), alongside NTFS/FAT and ISO9660, for staging ISOs from Linux. See the exact module list in `build-grub.ps1`.

Upstream release: https://lists.gnu.org/archive/html/grub-devel/2026-01/msg00029.html

Downloads:

- https://ftp.gnu.org/gnu/grub/grub-2.14-for-windows.zip
- https://ftp.gnu.org/gnu/grub/grub-2.14-for-windows.zip.sig
- https://ftp.gnu.org/gnu/grub/grub-2.14.tar.xz
- https://ftp.gnu.org/gnu/grub/grub-2.14.tar.xz.sig

Both release archive signatures were verified with the release signing fingerprint announced by GNU:

`BE5C23209ACDDACEB20DB0A28C8189F1988C2166` (Daniel Kiper).

SHA-256 of the Windows tools archive:

`243B2130CF93C3167822DA3C6043324D0D5BF4E107C610EAD4637D245F2ABE2B`

SHA-256 of the included corresponding source archive `grub-2.14.tar.xz`:

`BC8D3C73535B8838D8C8E2654D73EDC4E6AE8C8ACDB45D5DF5DC9A1547446D43`

GRUB is licensed under GPLv3 or later; see `GRUB-COPYING.txt` and individual source file notices. Corresponding source, including upstream build files, is provided in `grub-2.14.tar.xz`. No upstream source was modified. The custom embedded configuration is `bootstrap.cfg`; its generation script is `build-grub.ps1` (also in the project's tools directory).

To regenerate the EFI binary on Windows, download and verify the official Windows archive, extract it into `tools/grub-2.14/grub-2.14-for-windows`, then run `tools/build-grub.ps1` from the project. It builds the standalone image and checks its EFI format with `grub-file`. Generated timestamps may change the output hash; the script updates `grubx64.sha256` accordingly. Rebuild/publish the .NET project after regeneration.

The local hash file detects an incomplete/corrupt application distribution; it is not an independent signature of this application. Distribute the whole assets directory, including these license/source files, with the application.
