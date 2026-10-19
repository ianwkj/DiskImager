<#
  Runs INSIDE the Windows Server 2022 test VM (copied there and started by Run-ServerTest.ps1).
  Disks in the VM: 0 = Windows (60 GB), plus four test disks: source 8 GB, same-size target 8 GB,
  bigger target 16 GB, backups 40 GB. Writes C:\DITest\report.txt.

  The "NAS" is a password-protected SMB share on this server (\\<name>\NasBackups) that only a separate
  local account (nasuser) may open, so DiskImager has to log on to it exactly as it would to a real NAS.
  The last test runs a backup with "When done: Shut down", so this VM turns itself off at the end;
  Run-ServerTest.ps1 watches for that, starts the VM again and collects the results.

  -IncludeSystemDisk adds the slow live backup of the running C: drive (about 5 GB).
  -IncludeRescue also backs up C: and builds the rescue USB on the small "rescue" disk, with an unattended
   restore of that backup; Run-ServerTest.ps1 then boots it in a second VM and boots the restored disk in a third.
#>
param([switch]$IncludeSystemDisk, [switch]$IncludeRescue)
if ($IncludeRescue) { $IncludeSystemDisk = [switch]$true }
$ErrorActionPreference = 'Continue'
$Report = 'C:\DITest\report.txt'
Remove-Item $Report -ErrorAction SilentlyContinue
$script:Pass = 0; $script:Fail = 0
# Write-Host (not Write-Output) so these never leak into a function's return value.
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:Pass++ } else { $script:Fail++ }
    $line = "$(if ($ok) { 'PASS' } else { 'FAIL' })  $what"
    Add-Content $Report $line; Write-Host $line
}
function Info([string]$m) { Add-Content $Report "INFO  $m"; Write-Host "INFO  $m" }

# After a failure: DiskImager's latest log lines and Windows' crash records for it (full detail).
$script:EventsShown = @{}
function Diagnose([string]$what) {
    Info "---- diagnostics for: $what ----"
    Get-ChildItem 'C:\ProgramData\DiskImager\Logs\*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 |
        ForEach-Object { Get-Content $_.FullName -Encoding UTF8 -Tail 25 } | ForEach-Object { Info "log: $_" }
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-30) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Windows Error Reporting' -and $_.Message -match 'DiskImager' }
    foreach ($ev in $events | Select-Object -First 4) {
        if ($script:EventsShown.ContainsKey($ev.RecordId)) { continue }
        $script:EventsShown[$ev.RecordId] = $true
        Info "event $($ev.ProviderName) (id $($ev.Id), $($ev.TimeCreated.ToString('HH:mm:ss'))):"
        $ev.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 40 | ForEach-Object { Info "   | $_" }
    }
    Info "---- end diagnostics ----"
}

$Exe = 'C:\Program Files\DiskImager\DiskImager.exe'
$Jobs = 'C:\ProgramData\DiskImager\jobs.json'
function AllLogs { (Get-ChildItem "C:\ProgramData\DiskImager\Logs\*.log" -ErrorAction SilentlyContinue | Get-Content -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n" }

# Runs a program and waits for THAT program only. (Start-Process -Wait also waits for anything it starts,
# such as DiskImager's tray helper, which keeps running by design - that would hang the test.)
function Run-Exe([string]$path, [string]$arguments, [int]$minutes = 30) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo($path, $arguments)
    $psi.UseShellExecute = $false
    # Capture what the program prints (e.g. a .NET "Fatal error" crash report) to files for the report.
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $errTask = $proc.StandardError.ReadToEndAsync()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    if (-not $proc.WaitForExit($minutes * 60000)) { try { $proc.Kill() } catch {}; return -999 }
    # A program it started (e.g. DiskImager's tray helper) can keep the output pipe open forever,
    # so don't wait for "end of output" more than a few seconds after the program itself has exited.
    $outDone = $outTask.Wait(5000); $errDone = $errTask.Wait(5000)
    $text = ($(if ($outDone) { $outTask.Result } else { '' }) + "`n" + $(if ($errDone) { $errTask.Result } else { '' })).Trim()
    if ($text) {
        Info "output of $(Split-Path $path -Leaf) $arguments (exit code $($proc.ExitCode)):"
        $text -split "`r?`n" | Select-Object -First 60 | ForEach-Object { Info "   | $_" }
    }
    return $proc.ExitCode
}

# ---------------------------------------------------------------- environment
$os = Get-CimInstance Win32_OperatingSystem
Info "$($os.Caption) build $($os.BuildNumber)"
$net = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
Info ".NET Framework release $net (4.8 = 528040 or higher)"
Check ($net -ge 528040) ".NET Framework 4.8 present (needed by setup)"
# A shutdown left pending by an earlier run must not cut this one short.
shutdown /a 2>$null | Out-Null

# ---------------------------------------------------------------- 1. install with the real installer
$code = Run-Exe 'C:\DITest\DiskImagerSetup.exe' '/silent' 10
Check ($code -eq 0) "silent install finished (exit code $code)"
# Setup logs every copy step; show each message once.
Get-Content "$env:TEMP\DiskImagerSetup.log" -Encoding UTF8 -ErrorAction SilentlyContinue |
    ForEach-Object { if ($_.Length -gt 20) { $_.Substring(20) } else { $_ } } | Select-Object -Unique | ForEach-Object { Info "setup: $_" }
Check (Test-Path $Exe) "DiskImager.exe installed in Program Files"
if (Test-Path $Exe) { Info "Installed version $((Get-Item $Exe).VersionInfo.FileVersion)" }
Check (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DiskImager') "listed in Installed apps"
Check (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\DiskImager.lnk") "Start menu shortcut"

# Start-up probe: can DiskImager start at all here? (verify of a missing file should fail cleanly with exit code 1)
$code = Run-Exe $Exe '--cli verify C:\DITest\does-not-exist.dimg --out C:\DITest\cli-probe.txt' 2
Check ($code -eq 1 -and (Test-Path C:\DITest\cli-probe.txt)) "DiskImager starts on Windows Server (probe exit code $code)"
if ($code -ne 1) { Diagnose 'start-up probe' }

# ---------------------------------------------------------------- 2. prepare the test disks
$NasShare = 'NasBackups'
Remove-SmbShare -Name $NasShare -Force -ErrorAction SilentlyContinue   # it lives on the backups disk, which is wiped below

# Windows Server leaves new disks offline: bring the test disks online and writable.
$dataDisks = Get-Disk | Where-Object { $_.Number -ne 0 } | Sort-Object Number
foreach ($d in $dataDisks) {
    if ($d.IsOffline) { Set-Disk -Number $d.Number -IsOffline $false }
    if ($d.IsReadOnly) { Set-Disk -Number $d.Number -IsReadOnly $false }
}
$dataDisks = Get-Disk | Where-Object { $_.Number -ne 0 } | Sort-Object Number
$eight = @($dataDisks | Where-Object { $_.Size -gt 7GB -and $_.Size -le 9GB })
$SrcDisk = $eight[0]; $SameDisk = $eight[1]
$BigDisk = $dataDisks | Where-Object { $_.Size -gt 9GB -and $_.Size -le 20GB } | Select-Object -First 1
$BackupDisk = $dataDisks | Where-Object { $_.Size -gt 30GB } | Select-Object -First 1
$RescueDisk = $dataDisks | Where-Object { $_.Size -le 4GB } | Select-Object -First 1
Info "Disks: source=$($SrcDisk.Number) same-size target=$($SameDisk.Number) bigger target=$($BigDisk.Number) backups=$($BackupDisk.Number) rescue=$($RescueDisk.Number)"

function Reset-Disk($n) {
    if ((Get-Disk -Number $n).PartitionStyle -ne 'RAW') { Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false }
    Initialize-Disk -Number $n -PartitionStyle GPT
}

# Source: a data partition (S:) followed by a small one (T:), so a bigger restore must grow S: and move T:.
Reset-Disk $SrcDisk.Number
New-Partition -DiskNumber $SrcDisk.Number -Size 6GB -DriveLetter S | Format-Volume -FileSystem NTFS -NewFileSystemLabel SRC -Confirm:$false | Out-Null
New-Partition -DiskNumber $SrcDisk.Number -UseMaximumSize -DriveLetter T | Format-Volume -FileSystem NTFS -NewFileSystemLabel TAIL -Confirm:$false | Out-Null
Reset-Disk $BackupDisk.Number
New-Partition -DiskNumber $BackupDisk.Number -UseMaximumSize -DriveLetter B | Format-Volume -FileSystem NTFS -NewFileSystemLabel BACKUPS -Confirm:$false | Out-Null
New-Item -ItemType Directory -Force 'B:\Backups', 'B:\SystemBackups', 'B:\NAS' | Out-Null
foreach ($n in $SameDisk.Number, $BigDisk.Number) { if ((Get-Disk -Number $n).PartitionStyle -ne 'RAW') { Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false } }

# Kept small so the run is quick (about 110 MB): what matters is that every byte comes back identical.
function Write-RandomFile($path, $mb) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $buf = New-Object byte[] (1MB)
    $fs = [IO.File]::Create($path)
    try { for ($i = 0; $i -lt $mb; $i++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) } } finally { $fs.Dispose() }
}
New-Item -ItemType Directory -Force S:\Data | Out-Null
for ($i = 1; $i -le 10; $i++) { Write-RandomFile "S:\Data\file$i.bin" 10 }
1..50 | ForEach-Object { Set-Content "S:\Data\note$_.txt" "note $_ $(Get-Date -Format o)" }
Write-RandomFile 'T:\tail.bin' 5
# An excluded folder: its files must not be backed up, and it must come back empty after a restore.
New-Item -ItemType Directory -Force S:\Data\Skip | Out-Null
Write-RandomFile 'S:\Data\Skip\skip1.bin' 20
Write-RandomFile 'S:\Data\Skip\skip2.bin' 20
function Manifest { Get-ChildItem S:\, T:\ -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch 'System Volume Information' -and $_.FullName -notmatch '\\Data\\Skip\\' } |
    ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring(0, 1) + $_.FullName.Substring(2); Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } } }
$Manifest1 = Manifest
Info "Source data: $($Manifest1.Count) files"

# ---------------------------------------------------------------- 3. the "NAS": a share only nasuser may open
$NasUser = "$env:COMPUTERNAME\nasuser"
$NasPw = 'Nas-' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '!7'   # test VM only, never leaves it
$secPw = ConvertTo-SecureString $NasPw -AsPlainText -Force
if (Get-LocalUser -Name nasuser -ErrorAction SilentlyContinue) { Set-LocalUser -Name nasuser -Password $secPw }
else { New-LocalUser -Name nasuser -Password $secPw -PasswordNeverExpires -AccountNeverExpires -Description 'DiskImager test NAS login' | Out-Null }
icacls B:\NAS /grant "${NasUser}:(OI)(CI)M" | Out-Null
New-SmbShare -Name $NasShare -Path B:\NAS -FullAccess $NasUser | Out-Null
Revoke-SmbShareAccess -Name $NasShare -AccountName Everyone -Force -ErrorAction SilentlyContinue | Out-Null
$NasRoot = "\\$env:COMPUTERNAME\$NasShare"
$access = @(Get-SmbShareAccess -Name $NasShare -ErrorAction SilentlyContinue)
Check ($access.Count -eq 1 -and $access[0].AccountName -match 'nasuser') "NAS share $NasRoot created; only nasuser may open it"
# Without the NAS login (this script runs as Administrator) the share must refuse access.
net use $NasRoot /delete /y 2>$null | Out-Null
Check (-not (Test-Path "$NasRoot\")) "the NAS share refuses access without its login"

# DiskImager stores the NAS password encrypted for this PC (DPAPI, LocalMachine), as the job editor does.
Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes('DiskImager.NetCredential.v1')
$NasPwProtected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($NasPw), $entropy, 'LocalMachine'))

# ---------------------------------------------------------------- 4. scheduled jobs (run by Task Scheduler as SYSTEM)
function DiskId($n) { 'GPT:' + (Get-Disk -Number $n).Guid.Trim('{}').ToUpper() }
function NewJob($name, $diskNumber, $dest) {
    [ordered]@{
        Id = [guid]::NewGuid().ToString(); Name = $name; SourceDiskId = (DiskId $diskNumber); SourceDiskDescription = "Disk $diskNumber"
        Destination = $dest; NetUser = $null; NetPasswordProtected = $null; Compression = 'Fast'
        Full = [ordered]@{ Kind = 'Weekly'; Time = '03:00'; Days = @('Sunday'); DayOfMonth = 1 }
        Incremental = [ordered]@{ Kind = 'Daily'; Time = '04:00'; Days = @(); DayOfMonth = 1 }
        KeepFullSets = 2; MaxIncrementals = 6; After = 'None'; Notify = 'Never'; VerifyAfter = $true; WakeToRun = $false
        RegisteredTasks = @(); ScheduleSinceUtc = (Get-Date).ToUniversalTime().ToString('o'); RecentAttemptsUtc = @()
    }
}
$jobList = @()
$jobData = NewJob 'SrvData' $SrcDisk.Number 'B:\Backups'
$jobData.ExcludeFolders = @('S:\Data\Skip')
$jobData.ChangeTracking = $true
$jobList += $jobData
$jobNas = NewJob 'SrvNas' $SrcDisk.Number "$NasRoot\DiskImager"
$jobNas.NetUser = $NasUser; $jobNas.NetPasswordProtected = $NasPwProtected
# The NAS job's backups are encrypted with a password (stored for this PC the same way as the NAS login).
$EncPw = 'Enc-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$jobNas.EncryptionPasswordProtected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($EncPw), $entropy, 'LocalMachine'))
$jobList += $jobNas
if ($IncludeSystemDisk) { $jobList += NewJob 'SrvSystem' 0 'B:\SystemBackups' }
New-Item -ItemType Directory -Force 'C:\ProgramData\DiskImager' | Out-Null
ConvertTo-Json $jobList -Depth 5 | Set-Content $Jobs -Encoding UTF8
$code = Run-Exe $Exe '--reschedule' 5
Check ($code -eq 0) "jobs registered with Task Scheduler (--reschedule, exit code $code)"
$tasksOk = (schtasks /Query /TN "DiskImager\SrvData - Full" 2>$null) -ne $null
if ($code -ne 0 -or -not $tasksOk) {
    Diagnose '--reschedule'
    $saved = Get-Content $Jobs -Raw -ErrorAction SilentlyContinue
    Info "jobs.json after --reschedule: $(if ($saved) { $saved.Length } else { 0 }) characters"
}
$taskNames = @('SrvData - Full', 'SrvData - Incremental', 'SrvNas - Full', 'SrvNas - Incremental', 'Missed backup check', 'Tray helper')
if ($IncludeSystemDisk) { $taskNames += 'SrvSystem - Full' }
foreach ($t in $taskNames) {
    schtasks /Query /TN "DiskImager\$t" 2>$null | Out-Null
    Check ($LASTEXITCODE -eq 0) "task 'DiskImager\$t' exists"
}

function Run-Task($task, $jobName, $minutes) {
    schtasks /Query /TN "DiskImager\$task" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { return 'TASK MISSING (skipped)' }   # don't wait for a task that was never created
    $started = (Get-Date).ToUniversalTime()
    schtasks /Run /TN "DiskImager\$task" | Out-Null
    $until = (Get-Date).AddMinutes($minutes)
    while ((Get-Date) -lt $until) {
        Start-Sleep -Seconds 5
        $j = (Get-Content $Jobs -Raw | ConvertFrom-Json) | Where-Object Name -eq $jobName
        if ($j.LastRunUtc -and ([datetime]$j.LastRunUtc).ToUniversalTime() -gt $started) {
            if ($j.LastResult -notlike 'OK*') { Diagnose "task $task" }
            return $j.LastResult
        }
    }
    Diagnose "task $task (timed out)"
    return 'TIMED OUT'
}

$r = Run-Task 'SrvData - Full' 'SrvData' 15
Check ($r -like 'OK*') "scheduled FULL backup of the data disk to a local disk, run as SYSTEM (result: $r)"
$full = Get-ChildItem B:\Backups -Filter '*_F.dimg' | Sort-Object LastWriteTime | Select-Object -Last 1
Check ($null -ne $full) "full image file created ($($full.Name), $([math]::Round($full.Length / 1MB)) MB)"
$log = AllLogs
Check ($log -match 'S:+ snapshot created') "Windows snapshot (VSS) used for the data volume"
Check ($log -match 'Image verified') "verify image when complete ran"
$ex = [regex]::Match($log, 'S:+ excluding 1 folder\(s\), 2 files; saved ([\d.]+) MB')
Check ($ex.Success -and [double]$ex.Groups[1].Value -ge 36) "excluded folder S:\Data\Skip left out of the backup ($(if ($ex.Success) { $ex.Groups[1].Value } else { 0 }) of its 40 MB not stored)"
Check ($log -match 'S:+ change tracking map built') "change tracking map built from the snapshot"

# Change some data, then an incremental.
Write-RandomFile 'S:\Data\file3.bin' 10
Write-RandomFile 'S:\Data\new1.bin' 5
Remove-Item 'S:\Data\note7.txt'
$Manifest2 = Manifest
$r = Run-Task 'SrvData - Incremental' 'SrvData' 15
Check ($r -like 'OK*') "scheduled INCREMENTAL backup (result: $r)"
$incr = Get-ChildItem B:\Backups -Filter '*_I001.dimg' | Select-Object -Last 1
Check ($null -ne $incr -and $incr.Length -lt $full.Length / 2) "incremental is much smaller than the full ($([math]::Round($incr.Length / 1MB)) MB vs $([math]::Round($full.Length / 1MB)) MB)"
$log = AllLogs
$m = [regex]::Matches($log, "S:+ change tracking: ([\d,]+) block\(s\) may have changed; ([\d,]+) unchanged block\(s\) won't be read") | Select-Object -Last 1
Check ($null -ne $m -and [int]($m.Groups[2].Value -replace ',', '') -gt 0) "change tracking: the incremental skipped unchanged blocks ($(if ($m) { "$($m.Groups[2].Value) skipped, $($m.Groups[1].Value) re-read" } else { 'not used' }))"
Check ($log -match 'Change tracking check passed') "change tracking: the random spot-check of skipped blocks matched"

# ---------------------------------------------------------------- 5. verify and restore (command line)
$code = Run-Exe $Exe "--cli verify `"$($incr.FullName)`" --out C:\DITest\cli-verify.txt" 15
Check ($code -eq 0) "Verify image on the incremental"

function Compare-Restored($diskNumber, $expected, $label) {
    Update-HostStorageCache
    $d = Get-Disk -Number $diskNumber
    if ($d.IsOffline) { Set-Disk -Number $diskNumber -IsOffline $false }
    Start-Sleep 3
    $parts = Get-Partition -DiskNumber $diskNumber | Where-Object { $_.Type -eq 'Basic' } | Sort-Object Offset
    $letters = @()
    foreach ($pt in $parts) {
        if (-not $pt.DriveLetter -or $pt.DriveLetter -eq [char]0) { $pt | Add-PartitionAccessPath -AssignDriveLetter; Start-Sleep 2 }
        $letters += (Get-Partition -DiskNumber $diskNumber -PartitionNumber $pt.PartitionNumber).DriveLetter
    }
    if ($letters.Count -lt 2) { Check $false "$label - partitions visible (found $($letters.Count))"; return $null }
    $map = @{ 'S' = $letters[0]; 'T' = $letters[1] }
    $bad = 0
    foreach ($e in $expected) {
        $path = "$($map[$e.Rel.Substring(0,1)]):$($e.Rel.Substring(1))"
        if (-not (Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $e.Hash) { $bad++ }
    }
    Check ($bad -eq 0) "$label - all $($expected.Count) files identical to the backup ($bad different)"
    return $parts
}

$code = Run-Exe $Exe "--cli restore `"$($incr.FullName)`" $($SameDisk.Number) --layout keep --new-ids --out C:\DITest\cli-restore-same.txt" 15
Check ($code -eq 0) "restore incremental onto the same-size disk (incremental made with change tracking)"
if ($code -eq 0) {
    $parts = Compare-Restored $SameDisk.Number $Manifest2 'same-size restore'
    if ($parts) {
        $L = (Get-Partition -DiskNumber $SameDisk.Number -PartitionNumber $parts[0].PartitionNumber).DriveLetter
        $skipItems = @(Get-ChildItem "${L}:\Data\Skip" -Force -ErrorAction SilentlyContinue)
        Check ((Test-Path "${L}:\Data\Skip") -and $skipItems.Count -eq 0) "the excluded folder is restored, empty ($($skipItems.Count) items)"
    }
}

$code = Run-Exe $Exe "--cli restore `"$($full.FullName)`" $($BigDisk.Number) --layout auto --new-ids --out C:\DITest\cli-restore-big.txt" 15
Check ($code -eq 0) "restore full onto the bigger disk with automatic resize"
if ($code -eq 0) {
    $parts = Compare-Restored $BigDisk.Number $Manifest1 'bigger-disk restore'
    if ($parts) {
        $first = Get-Partition -DiskNumber $BigDisk.Number -PartitionNumber $parts[0].PartitionNumber
        $vol = $first | Get-Volume
        Check ($first.Size -gt 12GB) "first partition grew from 6 GB to $([math]::Round($first.Size / 1GB, 1)) GB"
        Check ($vol.Size -gt $first.Size - 100MB) "NTFS was extended to fill it (volume $([math]::Round($vol.Size / 1GB, 1)) GB)"
        Check ($parts[1].Offset -gt 12GB) "second partition moved towards the end of the bigger disk (now at $([math]::Round($parts[1].Offset / 1GB, 1)) GB, was 6 GB)"
    }
}

# ---------------------------------------------------------------- 6. optional: live backup of the running Windows disk
if ($IncludeSystemDisk) {
    $r = Run-Task 'SrvSystem - Full' 'SrvSystem' 45
    Check ($r -like 'OK*') "scheduled FULL backup of the running Windows disk (result: $r)"
    $log = AllLogs
    Check ($log -match 'C:+ snapshot created') "Windows snapshot (VSS) used for C:"
    Check ($log -match 'skipping .*pagefile\.sys') "page file skipped"
    $sys = Get-ChildItem B:\SystemBackups -Filter '*_F.dimg' | Select-Object -Last 1
    if ($sys) { Info "System image: $([math]::Round($sys.Length / 1GB, 2)) GB (Windows uses $([math]::Round(((Get-Volume C).Size - (Get-Volume C).SizeRemaining) / 1GB, 1)) GB)" }

    if ($IncludeRescue -and $RescueDisk) {
        # Rescue USB on the small "rescue" disk, set to restore this backup by itself onto a blank 60 GB disk.
        $auto = @{ ImageFolder = 'SystemBackups'; TargetSize = 60GB; ResultLabel = 'BACKUPS'; PowerOff = $true; Layout = 'auto' } | ConvertTo-Json
        Set-Content C:\DITest\autotest.json $auto -Encoding ASCII
        if ((Get-Disk -Number $RescueDisk.Number).PartitionStyle -ne 'RAW') { Clear-Disk -Number $RescueDisk.Number -RemoveData -RemoveOEM -Confirm:$false }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $code = Run-Exe $Exe "--cli make-rescue $($RescueDisk.Number) --autotest C:\DITest\autotest.json --out C:\DITest\cli-rescue.txt" 60
        Check ($code -eq 0) "rescue USB built on the rescue disk ($([int]$sw.Elapsed.TotalMinutes) min, exit code $code)"
        if ($code -ne 0) { Diagnose 'make-rescue' }
        $rv = Get-Volume -FileSystemLabel DIRESCUE -ErrorAction SilentlyContinue | Select-Object -First 1
        Check ($rv -and (Test-Path "$($rv.DriveLetter):\sources\boot.wim") -and (Test-Path "$($rv.DriveLetter):\EFI\Boot\bootx64.efi") -and (Test-Path "$($rv.DriveLetter):\EFI\Microsoft\Boot\BCD")) "rescue stick has the boot files and the rescue system"
        $code = Run-Exe $Exe "--cli make-rescue --iso C:\DITest\rescue.iso --no-drivers --out C:\DITest\cli-rescue-iso.txt" 60
        $iso = Get-Item C:\DITest\rescue.iso -ErrorAction SilentlyContinue
        Check ($code -eq 0 -and $iso -and $iso.Length -gt 200MB) "rescue ISO created ($(if ($iso) { [math]::Round($iso.Length / 1MB) } else { 0 }) MB)"
        Remove-Item C:\DITest\rescue.iso -ErrorAction SilentlyContinue
        Write-Host "RESCUE-READY"
    }
} else {
    Info "Live backup of the Windows disk (C:) skipped to keep the run short; add -IncludeSystemDisk to include it."
}

# ---------------------------------------------------------------- 7. NAS: scheduled backup to the share, then pull it back
$r = Run-Task 'SrvNas - Full' 'SrvNas' 15
Check ($r -like 'OK*') "scheduled FULL backup to the NAS share $NasRoot, run as SYSTEM (result: $r)"
Check ((AllLogs) -match [regex]::Escape("Connected to $NasRoot as $NasUser")) "DiskImager logged on to the NAS with the saved login"
$nasFull = Get-ChildItem B:\NAS\DiskImager -Filter '*_F.dimg' -ErrorAction SilentlyContinue | Select-Object -Last 1
Check ($null -ne $nasFull) "full image stored on the NAS ($(if ($nasFull) { "$($nasFull.Name), $([math]::Round($nasFull.Length / 1MB)) MB" }))"

if ($nasFull) {
    # Pull the image straight from the share, as you would on a replacement PC.
    net use $NasRoot /user:$NasUser $NasPw 2>$null | Out-Null
    $unc = "$NasRoot\DiskImager\$($nasFull.Name)"
    Check (Test-Path $unc) "image visible over the network after logging on to the NAS"
    $code = Run-Exe $Exe "--cli verify `"$unc`" --out C:\DITest\cli-verify-nas-nopw.txt" 15
    $said = Get-Content C:\DITest\cli-verify-nas-nopw.txt -Raw -ErrorAction SilentlyContinue
    Check ($code -eq 1 -and $said -match 'encrypted') "the NAS backup is encrypted: it can't be read without the password"
    $code = Run-Exe $Exe "--cli verify `"$unc`" --password $EncPw --out C:\DITest\cli-verify-nas.txt" 15
    Check ($code -eq 0) "Verify image read from the NAS (with the password)"
    $code = Run-Exe $Exe "--cli restore `"$unc`" $($SameDisk.Number) --layout keep --new-ids --password $EncPw --out C:\DITest\cli-restore-nas.txt" 15
    Check ($code -eq 0) "restore from the NAS onto the same-size disk"
    if ($code -eq 0) { Compare-Restored $SameDisk.Number $Manifest2 'restore from NAS' | Out-Null }
    net use $NasRoot /delete /y 2>$null | Out-Null
}

# ---------------------------------------------------------------- 8. last: NAS incremental with "When done: Shut down"
Write-RandomFile 'S:\Data\new2.bin' 5
$all = @((Get-Content $Jobs -Raw | ConvertFrom-Json) | ForEach-Object { $_ })   # PowerShell 5.1 returns the array as one object
foreach ($j in $all) { if ($j.Name -eq 'SrvNas') { $j.After = 'Shutdown' } }
ConvertTo-Json $all -Depth 8 | Set-Content $Jobs -Encoding UTF8
Info "SrvNas job now set to: When done = $((($all | Where-Object Name -eq 'SrvNas').After))"
$r = Run-Task 'SrvNas - Incremental' 'SrvNas' 15
Check ($r -like 'OK*') "scheduled INCREMENTAL backup to the NAS with 'When done: Shut down' (result: $r)"
Check ($null -ne (Get-ChildItem B:\NAS\DiskImager -Filter '*_I001.dimg' -ErrorAction SilentlyContinue)) "incremental stored on the NAS"
Start-Sleep -Seconds 3
Check ((AllLogs) -match 'After-backup action: Shutdown') "DiskImager started the shutdown after the backup"

"SUMMARY  $($script:Pass) passed, $($script:Fail) failed" | Tee-Object -FilePath $Report -Append
# Run-ServerTest.ps1 looks for this line, then waits for the VM to turn itself off.
Write-Host "SHUTDOWN-EXPECTED"
