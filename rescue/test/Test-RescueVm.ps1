<#
.SYNOPSIS
  Boot test for the TechSource Rescue USB image in Hyper-V (run as administrator).

.DESCRIPTION
  Setup (default): creates two test VMs that boot the rescue ISO:
    TSR-Test-UEFI  Generation 2, Secure Boot on (Microsoft UEFI Certificate Authority), like a modern PC
    TSR-Test-BIOS  Generation 1, legacy BIOS, like an older PC
  Each VM gets a small "BACKUPS" disk holding a copy of -BackupFolder and an empty 4 GB "target" disk to
  restore onto. The VMs use the "Default Switch" for internet, so Firefox and Tailscale can be tried.

  -Check: after a restore inside a VM (and the VM shut down), mounts that VM's target disk read-only and
  compares its files with -Manifest (the JSON list of the original files: path, size, SHA-256).

  -Remove: deletes the test VMs and their disks.

.EXAMPLE
  .\Test-RescueVm.ps1 -Iso C:\Users\me\Downloads\TechSource-Rescue-dev-1234567.iso -BackupFolder C:\Temp\rescue-demo\backups
  .\Test-RescueVm.ps1 -Check -Vm TSR-Test-UEFI -Manifest ..\..\tests\fixtures\ntfs-fixture-a.json
  .\Test-RescueVm.ps1 -Remove
#>
param(
    [string]$Iso,
    [string]$BackupFolder,
    [switch]$Check,
    [string]$Vm = "TSR-Test-UEFI",
    [string]$Manifest,
    [switch]$Remove,
    [string]$Work = "C:\DITest-Rescue"
)
$ErrorActionPreference = "Stop"
$names = "TSR-Test-UEFI", "TSR-Test-BIOS"

function Say($m) { Write-Host $m -ForegroundColor Cyan }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an administrator PowerShell."
}

if ($Remove) {
    foreach ($n in $names) {
        if (Get-VM $n -ErrorAction SilentlyContinue) { Stop-VM $n -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM $n -Force; Say "Removed $n" }
    }
    if (Test-Path $Work) { Remove-Item $Work -Recurse -Force; Say "Removed $Work" }
    return
}

if ($Check) {
    if (-not $Manifest) { throw "Give -Manifest (the JSON list of the original files)." }
    if ((Get-VM $Vm).State -ne "Off") { throw "Shut $Vm down first (Power > Shut down in the rescue system)." }
    $vhd = Join-Path $Work "$Vm-target.vhdx"
    $files = Get-Content $Manifest -Raw | ConvertFrom-Json
    $disk = Mount-VHD $vhd -ReadOnly -Passthru | Get-Disk
    try {
        $part = Get-Partition -DiskNumber $disk.Number | Where-Object { (Get-Volume -Partition $_ -ErrorAction SilentlyContinue).FileSystem -eq "NTFS" } | Sort-Object Size -Descending | Select-Object -First 1
        if (-not $part) { throw "No NTFS partition on the target disk: the restore didn't happen or failed." }
        if (-not $part.DriveLetter -or $part.DriveLetter -eq [char]0) { $part | Add-PartitionAccessPath -AssignDriveLetter; $part = Get-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber }
        $root = "$($part.DriveLetter):"
        Say "Restored partition: $root ($([math]::Round($part.Size / 1MB)) MB, layout $($disk.PartitionStyle))"
        $ok = 0; $bad = @()
        foreach ($f in $files) {
            $p = $root + $f.Path
            if (-not (Test-Path -LiteralPath $p)) { $bad += "missing  $($f.Path)"; continue }
            if ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $f.Sha256) { $bad += "differs  $($f.Path)"; continue }
            $ok++
        }
        Say "$ok of $($files.Count) files identical to the original."
        $bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        if ($bad.Count -eq 0) { Write-Host "RESTORE CHECK PASSED" -ForegroundColor Green } else { Write-Host "RESTORE CHECK: $($bad.Count) file(s) not identical (some may be in folders the backup leaves out on purpose)" -ForegroundColor Yellow }
    }
    finally { Dismount-VHD $vhd }
    return
}

# --- Setup ---
if (-not $Iso -or -not (Test-Path $Iso)) { throw "Give -Iso (the TechSource-Rescue-*.iso file)." }
if (-not $BackupFolder -or -not (Test-Path $BackupFolder)) { throw "Give -BackupFolder (a folder of DiskImager backups to restore from)." }
$Iso = (Resolve-Path $Iso).Path
foreach ($n in $names) { if (Get-VM $n -ErrorAction SilentlyContinue) { throw "$n already exists. Run with -Remove first." } }
New-Item -ItemType Directory -Force $Work | Out-Null
$switch = Get-VMSwitch -Name "Default Switch" -ErrorAction SilentlyContinue

foreach ($n in $names) {
    $gen = if ($n -like "*UEFI") { 2 } else { 1 }

    # The backups disk: NTFS, labelled BACKUPS, with a copy of the backup folder.
    $bk = Join-Path $Work "$n-backups.vhdx"
    $size = [math]::Max(1GB, [math]::Ceiling(((Get-ChildItem $BackupFolder -Recurse -File | Measure-Object Length -Sum).Sum * 1.3 + 256MB) / 1GB) * 1GB)
    New-VHD -Path $bk -SizeBytes $size -Dynamic | Out-Null
    $d = Mount-VHD $bk -Passthru | Get-Disk
    Initialize-Disk -Number $d.Number -PartitionStyle GPT
    $p = New-Partition -DiskNumber $d.Number -UseMaximumSize -AssignDriveLetter
    Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel BACKUPS -Confirm:$false | Out-Null
    $p = Get-Partition -DiskNumber $d.Number -PartitionNumber $p.PartitionNumber
    Copy-Item $BackupFolder "$($p.DriveLetter):\DiskImager Backups" -Recurse
    Dismount-VHD $bk

    # The empty disk to restore onto.
    $tg = Join-Path $Work "$n-target.vhdx"
    New-VHD -Path $tg -SizeBytes 4GB -Dynamic | Out-Null

    New-VM -Name $n -Generation $gen -MemoryStartupBytes 4GB -NoVHD -Path $Work | Out-Null
    Set-VMProcessor $n -Count 2
    Set-VM $n -AutomaticCheckpointsEnabled $false -CheckpointType Disabled
    if ($switch) { Get-VMNetworkAdapter $n | Connect-VMNetworkAdapter -SwitchName $switch.Name }
    if ($gen -eq 2) {
        Add-VMScsiController $n -ErrorAction SilentlyContinue
        $dvd = Add-VMDvdDrive $n -Path $Iso -Passthru
        Add-VMHardDiskDrive $n -Path $bk
        Add-VMHardDiskDrive $n -Path $tg
        Set-VMFirmware $n -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority -FirstBootDevice $dvd
    } else {
        if (Get-VMDvdDrive $n) { Get-VMDvdDrive $n | Set-VMDvdDrive -Path $Iso } else { Add-VMDvdDrive $n -ControllerNumber 1 -ControllerLocation 0 -Path $Iso }
        Add-VMHardDiskDrive $n -ControllerType IDE -ControllerNumber 0 -ControllerLocation 0 -Path $bk
        Add-VMHardDiskDrive $n -ControllerType IDE -ControllerNumber 0 -ControllerLocation 1 -Path $tg
        Set-VMBios $n -StartupOrder @("CD", "IDE", "LegacyNetworkAdapter", "Floppy")
    }
    Say "Created $n (generation $gen)"
}

Start-VM $names[0]
Start-Process vmconnect.exe -ArgumentList "localhost", $names[0]
Say ""
Say "TSR-Test-UEFI is starting (Secure Boot on). In the rescue system:"
Say "  1. Check the desktop, taskbar, Start menu, Firefox, network."
Say "  2. DiskImager: Restore a backup > pick the backup on BACKUPS > the empty 4 GB disk > Erase and restore."
Say "  3. Power > Shut down, then run:  .\Test-RescueVm.ps1 -Check -Vm TSR-Test-UEFI -Manifest <manifest.json>"
Say "Then the same on the older-PC VM:  Start-VM TSR-Test-BIOS; vmconnect localhost TSR-Test-BIOS"
Say "Remove everything afterwards with:  .\Test-RescueVm.ps1 -Remove"
