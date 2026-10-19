# DiskImager

A simple Windows disk imaging tool, loosely modelled on Macrium Reflect. Powered by [TechSource Digital](https://e-techsource.com).

**Install:** download `DiskImagerSetup-<version>.exe` from this repository's **Releases** page and run it. It needs nothing else installed (works on Windows 10/11 and Windows Server 2022) and asks for admin rights when it starts. For unattended installs: `DiskImagerSetup-<version>.exe /silent`.

## What it does

| Tab | Features |
|---|---|
| **Disks** | Shows every attached disk with a partition map. **Image selected disk** makes a one-off Full or Incremental image. **Clone selected disk** copies it directly to another disk. |
| **Scheduled backups** | Jobs with separate **Full** and **Incremental** schedules (daily, weekly or monthly). Each job also has an **after-completion action** (shut down, restart, sleep or hibernate), **wake to run**, and a **retention** setting that keeps the newest N full sets and deletes older ones after a successful backup. |
| **Restore** | Point it at a local folder, another drive, or a NAS/SMB share (with a username and password). Pick a backup point at the top and a target drive from the connected-drives list below, then choose **Restore to selected drive**. **Verify image** re-checks every block's checksum. |
| **Resize (restore and clone)** | The target drive can be smaller or larger than the source. **Resize automatically** grows or shrinks the main NTFS partition (usually C:) to fill the drive and moves the partitions after it, such as Recovery. **Choose sizes myself** lets you set each NTFS partition's size. Before and after bars preview the result. |
| **Settings** | SMTP email settings: server, port, security, login, from and to addresses. Includes a **Send test email** button. Each job chooses when to email: never, only if it fails (the default), or every time. Failure emails include the error and the last 40 log lines. |
| **Log** | Live log. Scheduled runs also write to `C:\ProgramData\DiskImager\Logs`. |

## How it works

- **Live imaging:** Windows volumes are imaged from a VSS snapshot, so C: can be backed up while it's in use. Free NTFS space is skipped.
- **Image files (`.dimg`):** Each disk is stored as compressed 1 MB blocks, each with a SHA-256 checksum. An incremental stores only the blocks that changed and points back to the earlier files in its set. That means every incremental needs the full backup and all the incrementals before it.
- **File names:** `<Job>_<yyyyMMdd-HHmmss>_F.dimg` for a full and `..._I001.dimg`, `..._I002.dimg` for incrementals. Keep all files of a set in the same folder.
- **Growing a partition:** Data is copied to the partitions' new positions and the partition table (GPT or MBR) is rewritten. Windows then extends the NTFS/ReFS file system to fill the partition. If Windows doesn't mount the new drive, the log gives the `diskpart` → `extend filesystem` steps to finish it.
- **Shrinking a partition:** A temporary virtual disk (`.vhdx`) is created in a working folder you choose, on a drive other than the target. The source is copied into it, Windows' own **Resize-Partition** shrinks it there (moving files as needed), and the result is written to the target. The source is never modified. This needs free space about equal to the data size and takes roughly twice as long. Windows may refuse to shrink below a certain size because of unmovable files; the app then reports the smallest size possible.
- **Partitions that can't be resized:** EFI, FAT and MSR partitions keep their size but can be moved. Only NTFS can shrink; NTFS and ReFS can grow.
- **Scheduling:** Jobs are stored in `C:\ProgramData\DiskImager\jobs.json` and run by Windows Task Scheduler as SYSTEM, under the `DiskImager` folder in Task Scheduler. The program copies itself to `C:\Program Files\DiskImager` so schedules keep working if you move the downloaded exe.
- **NAS passwords:** Encrypted with Windows DPAPI at machine level, because the scheduled task runs as SYSTEM and must be able to read them.

## Limitations

- **The disk Windows is running from can't be restored onto from inside Windows.** To replace your C: drive:
  - attach the new drive and restore or clone onto it, then swap the drives; or
  - restore from another PC.
  There's no bootable rescue USB yet.
- **Incremental backups still read the whole disk.** Only changed blocks are written, but every used block is read to find them. Backups are small but don't run much faster than a full.
- **Some disk types aren't supported:**
  - resizing MBR disks that have extended/logical partitions (they can only be copied as-is);
  - different sector sizes (512 vs 4K), and shrinking 4K-sector disks;
  - dynamic disks and Storage Spaces.
- **Not yet tested on real disks:** The resize logic is tested on simulated disks only. The Windows-side steps (extending the file system, the temporary working copy, shrinking) need admin rights and haven't been run on real hardware yet.
- **BitLocker:** Unlocked BitLocker volumes are imaged as decrypted data, and the restored copy is unencrypted.
- **Cloning or restoring while both disks stay attached:** Unless you tick "new disk IDs", Windows may take one of the two disks offline because they look identical.

## Building

Needs the .NET 10 SDK.

```
dotnet build -c Release
dotnet run --project tests\SmokeTest -- <scratch folder>    # end-to-end engine test on fake disks
.\build-release.ps1 -Version 1.2.0                          # app + installer into releases\
```

**Windows Server test:** `servertest\Run-ServerTest.ps1` (run as administrator, Hyper-V required) builds a Windows Server 2022 VM and tests:
- the silent install
- scheduled full and incremental backups
- verify, and restores to same-size and bigger disks
- a password-protected NAS share
- shut down after backup

Add `-IncludeSystemDisk` to also back up the running C: drive.
