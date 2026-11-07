# Bootable Ubuntu USB on Windows

Rewriting an install stick is a five-minute job that reliably takes an hour,
because three of the steps look optional and aren't: working out what is
actually on the stick, working out what the current release actually is, and
picking a write method that can cope with the image.

The worked example below is a SanDisk Ultra rewritten from Ubuntu 24.04.4 to
26.04.1 on Windows 11.

## Read the stick before you wipe it

A stick labelled `UBUNTU 24_0` tells you almost nothing — the volume label is
whatever the last writer set, and it goes stale the moment a point release
lands. The image itself carries the truth:

```bash
cat /e/.disk/info
# Ubuntu 24.04.4 LTS "Noble Numbat" - Release amd64 (20260210)

cat /e/autorun.inf
# ; Created by Rufus 4.14.2377
# label = Ubuntu 24.04.4 LTS amd64
```

`.disk/info` gives the exact point release and build date. `autorun.inf`, if
the stick was written by Rufus, records the Rufus version — useful when you
want to reproduce how it was made. `casper/install-sources.yaml` tells you the
edition (desktop vs live-server) and which squashfs variants are present.

Check for anything that isn't installer content before you commit. A live USB
with leftover free space is often carrying a persistence file or someone's
rescued data:

```powershell
Get-ChildItem E:\ -Force | Select-Object Mode,Length,LastWriteTime,Name
```

Identify the physical disk too — drive letters get reassigned, disk numbers
are what the imaging tools actually target:

```powershell
Get-Disk | Where-Object BusType -eq 'USB' |
  Select-Object Number,FriendlyName,Size,PartitionStyle
# 2  SanDisk Ultra  28.6 GB  MBR
```

## Don't trust your memory of the version numbers

"It's currently 24, I think" is the normal state of knowledge here, and it was
wrong by a full LTS cycle. Ubuntu ships an LTS every even April, so the answer
changes twice as often as people expect. Read the directory listing rather
than recalling it:

```bash
curl -s https://releases.ubuntu.com/ | grep -oE '[0-9]{2}\.[0-9]{2}(\.[0-9])? LTS'
```

Two version numbers matter and they are not the same thing. **26.04** is the
release; **26.04.1** is the point release, which folds in several months of
kernel and security updates. Always take the highest point release available —
same installer, far less to patch on first boot.

## Download and verify

```bash
cd ~/Downloads
curl -L -C - --retry 5 -O \
  https://releases.ubuntu.com/26.04/ubuntu-26.04.1-desktop-amd64.iso
curl -s https://releases.ubuntu.com/26.04/SHA256SUMS
```

`-C -` matters more than it looks: these are 6 GB images, and a resumable
download turns a dropped connection into an annoyance rather than a restart.

Verify before writing, not after. A corrupt ISO produces an installer that
boots happily and then fails partway through with a checksum error, by which
point the old stick is gone:

```powershell
(Get-FileHash .\ubuntu-26.04.1-desktop-amd64.iso -Algorithm SHA256).Hash.ToLower()
# 601e30fbf5d97759367c632e2c33630665039b7e2158fd068403da3ccf1bda1f
```

`SHA256SUMS` is signed; `SHA256SUMS.gpg` alongside it is worth checking if the
stick is going anywhere near a machine you care about.

## The trap: you cannot format and copy

The obvious approach — format the stick FAT32, copy the ISO contents onto it,
done — is what most "make a bootable USB" instructions describe, and it has
been broken for Ubuntu desktop images for several releases.

The desktop ISO contains a single squashfs larger than 4 GB:

```bash
# casper/install-sources.yaml, 26.04.1 desktop
#   minimal.standard.squashfs   6,097,338,368 bytes
```

FAT32 caps individual files at 4 GB. The copy fails on that one file, and it
fails *late*, after several minutes of apparently fine progress. Formatting
NTFS instead fixes the file size limit and breaks UEFI boot, because most
firmware will only read a FAT32 EFI system partition.

Real tools work around this by splitting the layout — a small FAT32 partition
carrying the bootloader plus a second partition for the payload, or a shim
like Rufus's UEFI:NTFS. This is why "just use the tool" is the right answer
even though the manual route looks simpler.

## Picking the write method

**Rufus** is the default. It handles the partition split and Secure Boot shim,
writes MBR+GPT hybrid layouts that boot on both BIOS and UEFI, and the
portable build needs no install:

```bash
curl -sL -o rufus-4.15p.exe \
  https://github.com/pbatard/rufus/releases/download/v4.15/rufus-4.15p.exe
powershell -c "Get-AuthenticodeSignature .\rufus-4.15p.exe | Select Status,SignerCertificate"
# Valid — CN=Akeo Consulting, O=Akeo Consulting, C=IE
```

Check that signature. A bootable-USB writer is a program you hand raw disk
access and administrator rights to, downloaded from a release page.

`-i` preselects the image so the GUI opens ready to go:

```powershell
.\rufus-4.15p.exe -i C:\Users\<you>\Downloads\ubuntu-26.04.1-desktop-amd64.iso
```

**Ventoy** is the better choice if you rewrite sticks often. You install it to
the drive once, then ISOs are plain files you copy on — a 28 GB stick holds
four or five releases and you pick one from a boot menu. The cost is a
one-time layout change and a slightly less standard stick.

**Raw `dd`** is the only fully headless option, because Ubuntu ISOs are
isohybrid images — a byte-for-byte copy to the device boots on both UEFI and
BIOS, and it is the documented method on Linux and macOS. On Windows it means
writing to `\\.\PhysicalDriveN` with administrator rights, where a wrong disk
number is unrecoverable and there is no confirmation prompt. It also has a
Windows-specific failure, covered in the next section, which is the whole
reason tools like Rufus exist.

Target the disk by *identity*, never by a bare number — numbers are reassigned
on replug — and refuse to run rather than guess:

```powershell
$cands = Get-Disk | Where-Object {
    $_.BusType -eq 'USB' -and -not $_.IsBoot -and -not $_.IsSystem -and
    $_.FriendlyName -like '*SanDisk Ultra*' -and
    [math]::Abs(($_.Size / 1GB) - 28.6) -lt 1.0
}
if (@($cands).Count -ne 1) { throw 'Refusing to guess the target disk.' }
```

## The raw write dies at about 20%

Nothing warns you about this one. The write starts fine, runs at full speed for
a gigabyte or so, then every subsequent write fails:

```
Exception calling "Write" with "3" argument(s):
  "Incorrect function."      # ERROR_INVALID_FUNCTION
```

It is a race with Windows itself. The ISO's GPT lands in the first sectors,
Windows notices a new partition table on the device, auto-mounts the volumes
that table describes, and takes ownership of those regions — after which the
handle you are writing through is no longer allowed to touch them. Nothing is
wrong with your buffer, your alignment, or your handle flags.

Two things close the race, and you need both. First, stop Windows mounting what
it discovers:

```powershell
mountvol.exe /N   # ALWAYS restore with /E in a finally block:
                  # this is a persistent, machine-wide setting
```

Second, lock any volume already present and **hold the handles open for the
whole write**, so nothing can remount underneath you:

```
FSCTL_LOCK_VOLUME      0x00090018
FSCTL_DISMOUNT_VOLUME  0x00090020
```

opened with `CreateFileW` on `\\.\X:` using `FILE_SHARE_READ | FILE_SHARE_WRITE`.
Dismounting and then closing the handle is not enough — the lock has to outlive
the write.

Two smaller traps in the same area:

- **`Set-Disk -IsOffline $true` fails on a USB stick** — "Removable media cannot
  be set to offline". Taking a disk offline is a reasonable way to get exclusive
  access to a fixed disk; on removable media it simply is not available, so
  treat that failure as non-fatal instead of letting it abort the run.
- **`Clear-Disk` fails on a disk with no partitions**, which is exactly what a
  previous failed attempt leaves behind. Guard it with a partition count.

Verify by reading back off the raw device and comparing against the ISO. Do not
verify by looking at the drive in Explorer: a correctly written stick shows up
as an **unreadable volume with no filesystem**, and Windows will offer to format
it, because Windows cannot mount ISO9660 from a physical disk. Declining that
prompt is part of the procedure.

A good final check is the partition table the write produced:

```powershell
Get-Partition -DiskNumber 2 | Select-Object PartitionNumber,Size,GptType
# 1  6176 MB  {ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}  basic data (ISO9660)
# 2     5 MB  {c12a7328-f81f-11d2-ba4b-00a0c93ec93b}  EFI System Partition
```

That ESP GUID is the one that matters — it is what firmware looks for.

## Administrator rights

All three methods need elevation — raw disk access is not available to a
standard user. Worth checking up front rather than discovering it at the write
step:

```powershell
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
(New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
  [Security.Principal.WindowsBuiltInRole]::Administrator)
```

Rufus self-elevates with a UAC prompt, so a non-admin shell is fine as long as
someone can approve it. Fully unattended writes need an already-elevated
session.

## Checklist

1. `cat /e/.disk/info` — what is actually on the stick
2. `Get-Disk` — identify the target by model and size, not by number alone
3. Confirm there is no non-installer data on it
4. Check `releases.ubuntu.com` for the current LTS *point* release
5. Download with `-C -`, verify SHA256 against `SHA256SUMS`
6. Verify the writer's Authenticode signature (Rufus) — or, for a raw write,
   confirm the ISO is isohybrid: `55 aa` at offset 510, `EFI PART` at 512
7. For a raw write: `mountvol /N`, lock and dismount the volumes, and keep
   those handles open for the duration
8. Write
9. `mountvol /E` — always, even on failure
10. Verify off the raw device: byte count equal to the ISO, a readback that
    matches it, and an ESP at GPT type `c12a7328-f81f-11d2-ba4b-00a0c93ec93b`.
    Explorer showing an unreadable drive is the expected result, not a failure
