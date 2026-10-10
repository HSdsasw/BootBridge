# BootBridge 0.2

**Prepare a Linux installer on an internal drive without a USB stick.** BootBridge is a C# / .NET 10 / Avalonia UI 12.1 application for **Windows x64 and Linux x64**. It can also write a hybrid ISO to a separate physical drive. Both platforms share the interface, with separate implementations for disk access, privileges, and UEFI.

## Run a packaged build

### Windows

Extract the **entire** `win-x64` folder, run `BootBridge.exe`, and accept the UAC prompt. No separate .NET installation is required. Keep the `assets` folder and all other package files alongside the executable.

### Linux

Extract the `linux-x64` folder and open a terminal inside it:

```sh
sh ./start-bootbridge.sh
```

Alternatively:

```sh
chmod +x ./BootBridge
./BootBridge
```

The package requires a desktop Linux x86_64 system with glibc and X11 or XWayland. Alpine/musl, ARM, and macOS are not supported by this build. The .NET runtime is included, but native system libraries are still required. On Ubuntu 24.04:

```sh
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libfreetype6 libicu74 libssl3t64 libstdc++6 zlib1g libgssapi-krb5-2 libgcc-s1 ca-certificates polkitd pkexec util-linux fdisk e2fsprogs parted udev
```

ICU, OpenSSL, and polkit package names differ between distributions. Your desktop session must have a running polkit authentication agent. Start the GUI as a regular user: BootBridge invokes a separate privileged helper through `pkexec` when needed. An operation stops if authorization is denied. System utilities are invoked by absolute path, without executing shell commands from user input.

See the [Avalonia Linux deployment documentation](https://docs.avaloniaui.net/docs/deployment/linux) for the interface's native dependencies.

## Install Linux from an internal drive

Requirements: **UEFI x64, GPT, Secure Boot disabled**, and an existing FAT32 EFI System Partition (ESP) with at least 32 MiB free. The package includes an unsigned GNU GRUB 2.14 build. BootBridge does not disable Secure Boot itself.

| Feature | Windows | Linux |
|---|---|---|
| ISO storage | Unencrypted NTFS | Mounted ext4 or NTFS |
| System ESP | Detected through Windows | Mounted at `/boot/efi`, `/efi`, or `/boot` |
| Create space for installation | Shrink NTFS using Windows tools | Shrink ext4 on a fully unmounted disk only |
| Privileges | UAC when starting the EXE | Separate helper through polkit |
| Supported installers | Ubuntu/Mint with casper; Debian Live amd64 | Same profiles |

Boot profiles are detected from `.disk/info`, the ISO directory structure, the x86-64 kernel header, initrd, and the EFI loader. Debian netinst, Windows ISO, Fedora, and arbitrary ISO images are **not supported for booting from an ISO file**. A `.iso` extension alone does not establish compatibility.

1. On the first tab, select a partition to store the ISO. You may use the current OS partition, but you must not delete or format it while the installer is using the ISO.
2. If needed, click **Create partition…** and specify the ISO partition size and unallocated space for Linux. Review the separate partitioning plan. The wizard formats only the newly created `ISO_INSTALL` partition.
3. Select the ISO and click **Validate and show plan**. Validation does not modify partitions, EFI files, or NVRAM.
4. Confirm the plan and click **Prepare ISO boot**. The ISO is copied to `IsoWriter-<id>/installer.iso` on the selected partition and verified using SHA-256. GRUB and its configuration are placed in a separate `EFI/IsoWriter-<id>` directory on the ESP.
5. A new UEFI entry is appended to `BootOrder`, preserving the existing default OS. Click **Boot Linux next** to verify the files and set `BootNext`. Restart the computer yourself.
6. In GRUB, select **Start Linux live installer**. On timeout, GRUB exits to the firmware to allow the normal boot sequence to continue.
7. In the installer, select another partition or unallocated space. **Do not select “erase entire disk” or format the partition containing the ISO.** To replace your current OS completely, first store the ISO on a separate partition.

### Windows notes

The ISO storage partition must be fully decrypted: suspending BitLocker is not sufficient for GRUB to read it. If Windows itself is on another encrypted partition, save its recovery key and suspend protection before changing boot settings. An unknown BitLocker status blocks preparation.

The partition wizard uses `Get-PartitionSupportedSize` and `Resize-Partition`. Its plan is saved under `%LOCALAPPDATA%/IsoWriter`.

### Linux notes

To write boot files, the mount point and its parent directories must be owned by root and must not allow group or other-user writes. Suitable locations include `/`, `/mnt/installer`, and the standard system ESP. User-managed automounts under `/media/<user>` may fail this check. Mount the selected partition separately after verifying its device name with `lsblk -f`. Subdirectory mounts, LUKS, LVM, RAID, Btrfs, and XFS are not supported as ISO storage.

The ext4 shrink sequence runs `e2fsck`, then `resize2fs`, then changes GPT. The entire physical disk must be unmounted, including active swap. **A running root partition cannot be shrunk; use a live environment.** An existing mounted ext4/NTFS partition can store an ISO without being resized. The new partition is temporarily mounted at `/mnt/bootbridge-<uuid>`; no fstab entry is added. After restarting Linux, mount it at the same location again to manage the installation. GRUB locates the ISO independently using its unique path.

Back up your data before changing partitions. Cancellation is unavailable during resizing. If an operation fails, the filesystem or GPT may already have been shrunk; sizes are not automatically rolled back. The original GPT layout and plan are saved under `/var/lib/bootbridge`. Check the disk state before retrying.

## Manage a prepared installation

The ISO directory contains an `installation.json` record. Use **Open installation…** to reopen it after restarting BootBridge. **Remove boot entry** removes that installation's UEFI entry and unchanged GRUB files, while preserving the ISO and record. The `IsoWriter-*` directory names and `ISO Writer Linux` EFI labels are retained for compatibility with the previous format.

If copying or registration fails, BootBridge attempts to remove its newly created files and entry. If rollback is incomplete, it displays the installation record path. Existing third-party UEFI entries, BCD, and bootloaders are not replaced. Modified entries are rejected, and modified loader files are preserved rather than deleted.

## Overwrite a separate disk

The second tab writes a hybrid ISO byte-for-byte to the **entire physical disk**, even if you selected one of its partitions. It requires a separate drive, 512-byte logical sectors, and the exact confirmation text `DISK N`.

Windows blocks system, boot, unavailable, and composite disks, as well as the disk containing the source ISO. Linux blocks disks with mounted partitions, active swap, or active composite devices, and opens the target exclusively before writing. The selected disk's identity and layout are rechecked. Writing finishes with a cache flush and SHA-256 verification by reading the data back. Canceling does not restore the original disk contents.

The Ubuntu/Mint/Debian Live profile restrictions apply to the first tab. The second tab validates hybrid ISO structure; a successful write does not itself guarantee that the image will boot on your firmware.

## Build from source

Install .NET SDK 10 and ensure NuGet is reachable:

```sh
dotnet build IsoWriter.csproj -c Release
dotnet run --project tests/IsoWriter.Tests.csproj -c Release
```

To publish both self-contained builds from Windows PowerShell:

```powershell
./tools/publish.ps1
```

To publish the Linux build and archive from Linux:

```sh
sh tools/publish.sh
```

Outputs are `dist/win-x64/BootBridge.exe` and `dist/linux-x64/BootBridge`. Distribute every file in the corresponding folder together. The project file remains `IsoWriter.csproj`; the application executable is named `BootBridge`.

The workflow in `.github/workflows/build.yml` builds both platforms, runs tests, and runs the Linux GUI preview under Xvfb. After pushing the changes, packages are available under **Actions → Build Windows and Linux → Artifacts**. They are not automatically published to Releases.

## Validation and known limits

The 78 automated tests cover ISO validation, copying and readback using an in-memory device, safety restrictions, shrink plans, GRUB configuration, UEFI transactions using a fake store, Linux device parsing, and the helper protocol. They do not modify real disks or EFI variables.

`BootBridge --diagnose` reads disk and boot information without modifying it. `BootBridge --preview` opens an interface with operations disabled, saves `preview-avalonia.png` beside the application, and exits. On Windows, you can run the diagnostic DLL through `dotnet`; this does not elevate privileges.

The Windows GUI has been checked locally. The Linux package has been cross-compiled; the Linux GUI and actual ISO boot on a Linux machine have not yet been verified locally. CI validation starts after the changes are pushed. Validate the complete workflow in a UEFI VM with disposable disks before using it on your primary drive.

## GRUB and licenses

The `assets` directory includes GRUB 2.14, GPLv3, the corresponding source archive, and provenance information. See `assets/GRUB-NOTICE.md`. The EFI binary includes the `ext2` module for reading supported ext4 filesystems. Its checksum checks package integrity; it is not an application signature. Rebuild it using `tools/build-grub.ps1`.
