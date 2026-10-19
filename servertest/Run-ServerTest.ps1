#Requires -RunAsAdministrator
<#
  DiskImager — Windows Server 2022 test.
  Run on this PC in PowerShell as administrator (Hyper-V must be turned on):

      powershell -ExecutionPolicy Bypass -File "<path to DiskImager>\servertest\Run-ServerTest.ps1"

  It downloads Microsoft's free Windows Server 2022 evaluation (about 5 GB, once), builds a VM named
  "DiskImager-Test" in D:\DiskImagerServerTest, installs DiskImager inside it with the real installer,
  runs the tests in InVm-Tests.ps1 and copies the report back to D:\DiskImagerServerTest\results.
  The tests include a backup to a password-protected NAS share and, last, a backup with
  "When done: Shut down": this script waits for the VM to turn itself off, then starts it again.
  Run it again any time to re-test (the VM is reused; add -Rebuild to start from a fresh VM,
  -IncludeSystemDisk to also do the slow live backup of the server's C: drive).
  Remove everything afterwards with Remove-ServerTest.ps1.
#>
param(
    [string]$WorkDir = "D:\DiskImagerServerTest",
    [string]$IsoPath = "",
    [string]$Installer = "",
    [switch]$Rebuild,
    [switch]$IncludeSystemDisk
)
$ErrorActionPreference = 'Stop'
$VmName = 'DiskImager-Test'
$IsoUrl = 'https://go.microsoft.com/fwlink/p/?LinkID=2195280&clcid=0x409&culture=en-us&country=US'

New-Item -ItemType Directory -Force $WorkDir | Out-Null
$HostLog = Join-Path $WorkDir 'host-log.txt'
function Step([string]$m) { $line = "$(Get-Date -Format 'HH:mm:ss')  $m"; Write-Host "`n== $line" -ForegroundColor Cyan; Add-Content $HostLog $line }

# ---------------------------------------------------------------- 0. Checks
if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) {
    throw "Hyper-V isn't available. Turn it on (Turn Windows features on or off > Hyper-V), restart, then run this again."
}
if (-not $Installer) {
    # The newest released installer (by version number).
    $Installer = Get-ChildItem (Join-Path $PSScriptRoot '..\releases') -Filter 'DiskImagerSetup-*.exe' |
        Sort-Object { [version]($_.BaseName -replace '^DiskImagerSetup-', '') } -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Installer -or -not (Test-Path $Installer)) { throw "Couldn't find DiskImagerSetup-*.exe in the releases folder." }
Step "Using installer $Installer"

# ---------------------------------------------------------------- 1. Windows Server 2022 evaluation ISO
if (-not $IsoPath) { $IsoPath = Join-Path $WorkDir 'WindowsServer2022-Evaluation.iso' }
if (-not (Test-Path $IsoPath) -or (Get-Item $IsoPath).Length -lt 4GB) {
    Step "Downloading the Windows Server 2022 evaluation from Microsoft (about 5 GB, one time only)..."
    try {
        Start-BitsTransfer -Source $IsoUrl -Destination $IsoPath -DisplayName 'Windows Server 2022 evaluation' -Priority Foreground
    } catch {
        throw "The download didn't work ($($_.Exception.Message)). Download the ISO yourself from " +
              "https://www.microsoft.com/evalcenter/download-windows-server-2022 (64-bit ISO, English), save it as $IsoPath, and run this again."
    }
    if ((Get-Item $IsoPath).Length -lt 4GB) { throw "The downloaded file is too small to be the Server ISO. Delete $IsoPath and try again." }
}

# ---------------------------------------------------------------- 2. VM (built once, reused)
$OsVhd = Join-Path $WorkDir 'DITest-OS.vhdx'
$PwFile = Join-Path $WorkDir 'vm-admin-password.txt'   # test VM only
$existing = Get-VM -Name $VmName -ErrorAction SilentlyContinue
if ($existing -and $Rebuild) {
    Step "Removing the old test VM (-Rebuild)"
    if ($existing.State -ne 'Off') { Stop-VM $VmName -TurnOff -Force }
    Remove-VM $VmName -Force
    Get-ChildItem $WorkDir -Filter 'DITest-*.vhdx' | Remove-Item -Force
    $existing = $null
}

if (-not $existing) {
    $pw = 'Di-' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '!a9'
    Set-Content $PwFile $pw

    Step "Creating the system disk and applying Windows Server 2022 (10-15 minutes)..."
    if (Test-Path $OsVhd) { Remove-Item $OsVhd -Force }
    New-VHD -Path $OsVhd -SizeBytes 60GB -Dynamic | Out-Null
    $disk = Mount-VHD -Path $OsVhd -Passthru | Get-Disk
    try {
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        $efi = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
        $efi | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'System' -Force | Out-Null
        $efi | Add-PartitionAccessPath -AssignDriveLetter
        New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
        $win = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        $win | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Windows' -Force | Out-Null
        $efiL = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber).DriveLetter
        $winL = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $win.PartitionNumber).DriveLetter

        $iso = Mount-DiskImage -ImagePath $IsoPath -PassThru
        try {
            $isoL = ($iso | Get-Volume).DriveLetter
            $wim = "${isoL}:\sources\install.wim"
            $image = Get-WindowsImage -ImagePath $wim | Where-Object { $_.ImageName -match 'Standard' -and $_.ImageName -match 'Desktop' } | Select-Object -First 1
            if (-not $image) { throw "Couldn't find 'Standard (Desktop Experience)' in the ISO." }
            Step "Applying '$($image.ImageName)'..."
            Expand-WindowsImage -ImagePath $wim -Index $image.ImageIndex -ApplyPath "${winL}:\" | Out-Null
        } finally { Dismount-DiskImage -ImagePath $IsoPath | Out-Null }

        # Use the image's own bcdboot (not this PC's newer one): newer Windows 11 boot files can be signed with
        # the 2023 Secure Boot certificate, which a standard Hyper-V VM rejects ("boot loader did not load an OS").
        & "${winL}:\Windows\System32\bcdboot.exe" "${winL}:\Windows" /s "${efiL}:" /f UEFI | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "bcdboot failed (exit code $LASTEXITCODE)." }

        # First boot without questions: computer name, administrator password, no welcome screens.
        $unattend = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>DITEST</ComputerName>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE><HideEULAPage>true</HideEULAPage><SkipMachineOOBE>true</SkipMachineOOBE><SkipUserOOBE>true</SkipUserOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>
      <UserAccounts><AdministratorPassword><Value>$pw</Value><PlainText>true</PlainText></AdministratorPassword></UserAccounts>
      <TimeZone>UTC</TimeZone>
    </component>
  </settings>
</unattend>
"@
        New-Item -ItemType Directory -Force "${winL}:\Windows\Panther" | Out-Null
        Set-Content "${winL}:\Windows\Panther\unattend.xml" $unattend -Encoding UTF8
    } finally { Dismount-VHD -Path $OsVhd }

    Step "Creating test disks and the VM..."
    $data = [ordered]@{ 'DITest-Source.vhdx' = 8GB; 'DITest-TargetSame.vhdx' = 8GB; 'DITest-TargetBig.vhdx' = 16GB; 'DITest-Backups.vhdx' = 40GB }
    foreach ($k in $data.Keys) {
        $p = Join-Path $WorkDir $k
        if (Test-Path $p) { Remove-Item $p -Force }
        New-VHD -Path $p -SizeBytes $data[$k] -Dynamic | Out-Null
    }
    New-VM -Name $VmName -Generation 2 -MemoryStartupBytes 4GB -VHDPath $OsVhd -Path $WorkDir | Out-Null
    Set-VMProcessor $VmName -Count 2
    Set-VMMemory $VmName -DynamicMemoryEnabled $false
    Set-VM $VmName -AutomaticCheckpointsEnabled $false
    # Test VM only: Secure Boot off avoids boot-certificate mismatches; it doesn't affect what's being tested.
    Set-VMFirmware $VmName -EnableSecureBoot Off
    Set-VMFirmware $VmName -FirstBootDevice (Get-VMHardDiskDrive -VMName $VmName | Where-Object Path -eq $OsVhd)
    foreach ($k in $data.Keys) { Add-VMHardDiskDrive -VMName $VmName -Path (Join-Path $WorkDir $k) }
}

$pw = Get-Content $PwFile
$cred = New-Object System.Management.Automation.PSCredential('Administrator', (ConvertTo-SecureString $pw -AsPlainText -Force))
if ((Get-VM $VmName).State -ne 'Running') { Step "Starting the VM"; Start-VM $VmName }

# ---------------------------------------------------------------- 3. Wait for Windows to be ready
function Connect-Vm([int]$minutes) {
    $s = $null
    $until = (Get-Date).AddMinutes($minutes)
    while (-not $s -and (Get-Date) -lt $until) {
        try { $s = New-PSSession -VMName $VmName -Credential $cred -ErrorAction Stop }
        catch { Start-Sleep -Seconds 15 }
    }
    if (-not $s) { throw "The VM didn't become reachable within $minutes minutes. Open it in Hyper-V Manager to see what it's doing." }
    return $s
}
Step "Waiting for Windows Server to finish starting (first start takes 5-15 minutes)..."
$session = Connect-Vm 40
Step "Connected to the VM"

# ---------------------------------------------------------------- 4. Copy in and run the tests
Invoke-Command -Session $session -ScriptBlock { Remove-Item C:\DITest -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory C:\DITest | Out-Null }
Copy-Item -ToSession $session -Path $Installer -Destination 'C:\DITest\DiskImagerSetup.exe'
Copy-Item -ToSession $session -Path (Join-Path $PSScriptRoot 'InVm-Tests.ps1') -Destination 'C:\DITest\InVm-Tests.ps1'

Step "Running the tests inside the VM (about $(if ($IncludeSystemDisk) { '25' } else { '10' }) minutes)..."
# Error text printed by programs inside the VM must not stop this script: the report still has to be collected.
$ErrorActionPreference = 'Continue'
$script:ShutdownExpected = $false
Invoke-Command -Session $session -ArgumentList $IncludeSystemDisk.IsPresent -ScriptBlock {
    param($sys)
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\DITest\InVm-Tests.ps1')
    if ($sys) { $a += '-IncludeSystemDisk' }
    & powershell @a 2>&1
} 2>&1 | ForEach-Object {
    if ("$_" -match 'SHUTDOWN-EXPECTED') { $script:ShutdownExpected = $true }
    Write-Host "   $_"; Add-Content $HostLog "   $_"
}
Remove-PSSession $session -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 5. "When done: Shut down" - the VM should turn itself off
$hostChecks = @()
if ($script:ShutdownExpected) {
    Step "Waiting for the VM to shut itself down (DiskImager gives a 60-second warning first)..."
    $t0 = Get-Date
    while ((Get-VM $VmName).State -ne 'Off' -and (Get-Date) -lt $t0.AddMinutes(6)) { Start-Sleep -Seconds 5 }
    $off = (Get-VM $VmName).State -eq 'Off'
    $secs = [int]((Get-Date) - $t0).TotalSeconds
    $hostChecks += "$(if ($off) { 'PASS' } else { 'FAIL' })  the server shut itself down after the backup (VM off $secs s after the tests finished)"
    if (-not $off) { Stop-VM $VmName -Force -ErrorAction SilentlyContinue }   # tidy restart either way
    Step "Starting the VM again to collect the results"
    Start-VM $VmName
} else {
    $hostChecks += "FAIL  the shutdown test didn't run (the tests inside the VM stopped early)"
}
$ErrorActionPreference = 'Stop'
$session = Connect-Vm 15
if ($script:ShutdownExpected) {
    $ev = Invoke-Command -Session $session -ScriptBlock {
        Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1074; StartTime = (Get-Date).AddHours(-2) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'DiskImager has finished' } | Select-Object -First 1 -ExpandProperty TimeCreated
    }
    $hostChecks += "$(if ($ev) { 'PASS' } else { 'FAIL' })  Windows logged the shutdown as requested by DiskImager$(if ($ev) { " ($($ev.ToString('HH:mm:ss')) VM time)" })"
    $nas = Invoke-Command -Session $session -ScriptBlock {
        (Get-Content C:\ProgramData\DiskImager\jobs.json -Raw | ConvertFrom-Json) | Where-Object Name -eq 'SrvNas' | Select-Object -ExpandProperty LastResult
    }
    $hostChecks += "$(if ($nas -like 'OK*') { 'PASS' } else { 'FAIL' })  after restarting, the NAS backup is still recorded as successful (result: $nas)"
}

# ---------------------------------------------------------------- 6. Collect results
$results = Join-Path $WorkDir ("results-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $results | Out-Null
$ErrorActionPreference = 'Continue'
Copy-Item -FromSession $session -Path 'C:\DITest\report.txt' -Destination $results -ErrorAction SilentlyContinue
Copy-Item -FromSession $session -Path 'C:\ProgramData\DiskImager\Logs' -Destination $results -Recurse -ErrorAction SilentlyContinue
Copy-Item -FromSession $session -Path 'C:\DITest\cli-*.txt' -Destination $results -ErrorAction SilentlyContinue
Remove-PSSession $session
$ErrorActionPreference = 'Stop'

# Add this PC's checks and recount the summary.
$reportFile = Join-Path $results 'report.txt'
$body = @(Get-Content $reportFile -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -notlike 'SUMMARY*' }) + $hostChecks
$passed = @($body | Where-Object { $_ -like 'PASS*' }).Count
$failed = @($body | Where-Object { $_ -like 'FAIL*' }).Count
$body += "SUMMARY  $passed passed, $failed failed"
Set-Content $reportFile $body -Encoding UTF8
$hostChecks | ForEach-Object { Add-Content $HostLog "   $_" }
Copy-Item $reportFile (Join-Path $WorkDir 'latest-report.txt') -Force

Step "Done. Report: $reportFile"
$body | Where-Object { $_ -match '^(PASS|FAIL|SUMMARY)' } | ForEach-Object {
    $color = if ($_ -like 'FAIL*') { 'Red' } elseif ($_ -like 'PASS*') { 'Green' } else { 'White' }
    Write-Host $_ -ForegroundColor $color
}
Write-Host "`nThe VM is left running so it can be inspected. Stop it with:  Stop-VM -Name $VmName"

