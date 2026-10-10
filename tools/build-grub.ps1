$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$grubDirectory = Join-Path $projectRoot 'tools\grub-2.14\grub-2.14-for-windows'
$modules = 'part_gpt part_msdos fat ntfs ext2 iso9660 loopback search search_fs_file search_fs_uuid normal configfile linux chain reboot halt echo sleep test'
Push-Location $projectRoot
try {
    & (Join-Path $grubDirectory 'grub-mkstandalone.exe') -O x86_64-efi -d (Join-Path $grubDirectory 'x86_64-efi') --modules=$modules --locales= --fonts= -o assets/grubx64.efi 'boot/grub/grub.cfg=assets/bootstrap.cfg'
    if ($LASTEXITCODE -ne 0) { throw 'grub-mkstandalone failed' }
    & (Join-Path $grubDirectory 'grub-file.exe') --is-x86_64-efi assets/grubx64.efi
    if ($LASTEXITCODE -ne 0) { throw 'Invalid EFI output' }
    (Get-FileHash assets/grubx64.efi -Algorithm SHA256).Hash | Set-Content -Encoding ascii assets/grubx64.sha256
} finally { Pop-Location }
