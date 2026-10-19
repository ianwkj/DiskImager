#Requires -RunAsAdministrator
<#
  Builds the NTFS test volumes used by the tests (run once as administrator; the results are committed so the
  tests themselves don't need admin rights):
    ntfs-fixture-a.vhd.gz / .json   a small NTFS volume full of awkward cases (file-level restore)
    ntfs-fixture-b.vhd.gz / .json   the same volume after edits, deletes, renames and a defrag (change tracking)
  Each .json lists every file with its size and SHA-256.
#>
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot
Start-Transcript -Path (Join-Path $env:TEMP 'di-ntfs-fixture-log2.txt') -Force | Out-Null
$vhd = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp\di-ntfs-fixture.vhd'
if (Test-Path $vhd) { Dismount-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue | Out-Null; Start-Sleep 2; Remove-Item $vhd -Force }
$enc = New-Object System.Text.UTF8Encoding($false)

function Invoke-Diskpart([string]$script) {
    $f = Join-Path $env:TEMP "di-fixture-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content $f $script -Encoding ASCII
    try { $o = diskpart.exe /s $f; if ($LASTEXITCODE -ne 0) { throw "diskpart failed: $o" } } finally { Remove-Item $f }
}
function Attach { Mount-DiskImage -ImagePath $vhd | Out-Null; Start-Sleep 3; (Get-Volume -FileSystemLabel DIFIXTURE).DriveLetter + ':' }
function Detach { Dismount-DiskImage -ImagePath $vhd | Out-Null; Start-Sleep 2 }
function Text([string]$path, [string]$text) { New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null; [IO.File]::WriteAllText($path, $text, $enc) }
function Lines([string]$path, [string]$tag, [int]$count) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $count; $i++) { [void]$sb.Append("$tag line $i of $count - the quick brown fox jumps over the lazy dog`r`n") }
    [IO.File]::WriteAllText($path, $sb.ToString(), $enc)
}
function Manifest([string]$root, [string]$name) {
    $files = Get-ChildItem "$root\" -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\junction-to-docs\\' -and $_.FullName -notmatch 'System Volume Information' }
    $manifest = foreach ($f in $files) {
        [ordered]@{
            Path = $f.FullName.Substring(2)
            Size = $f.Length
            Sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
            Attributes = [int]$f.Attributes
            LastWriteUtcTicks = $f.LastWriteTimeUtc.Ticks
        }
    }
    [IO.File]::WriteAllText((Join-Path $out $name), (ConvertTo-Json @($manifest) -Depth 3), $enc)
    Write-Host "$name : $(@($manifest).Count) files"
}
function Gzip([string]$target) {
    $in = [IO.File]::OpenRead($vhd); $o = [IO.File]::Create((Join-Path $out $target))
    $z = New-Object IO.Compression.GZipStream($o, [IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($z); $z.Dispose(); $o.Dispose(); $in.Dispose()
    Write-Host ("{0}: {1:N1} MB" -f $target, ((Get-Item (Join-Path $out $target)).Length / 1MB))
}

Invoke-Diskpart "create vdisk file=`"$vhd`" maximum=128 type=fixed`r`nselect vdisk file=`"$vhd`"`r`nattach vdisk`r`nconvert gpt`r`ncreate partition primary`r`nformat fs=ntfs quick label=DIFIXTURE unit=4096`r`nassign`r`n"
Start-Sleep 3
$root = (Get-Volume -FileSystemLabel DIFIXTURE).DriveLetter + ':'
Write-Host "Fixture volume at $root"
$deep = "$root\Deep"; foreach ($i in 1..12) { $deep = "$deep\level$i" }
$sp = "$root\Sparse\sparse.bin"
try {
    fsutil usn createjournal m=33554432 a=4194304 $root | Out-Null   # change journal, as on a system drive

    # ---------------- state A
    Text "$root\Docs\hello.txt" "Hello from the DiskImager NTFS fixture."
    Lines "$root\Docs\report.txt" 'report' 40000
    $rnd = New-Object byte[] (1MB); (New-Object Random 7).NextBytes($rnd); [IO.File]::WriteAllBytes("$root\Docs\random.bin", $rnd)
    Text "$root\Docs\Ünïcødé 名前 файл.txt" "unicode name"
    New-Item -ItemType File "$root\Docs\empty.txt" | Out-Null
    New-Item -ItemType Directory "$root\EmptyFolder" | Out-Null
    Text "$deep\deep.txt" "twelve levels down"
    (Get-Item "$root\Docs\hello.txt").LastWriteTimeUtc = [datetime]::SpecifyKind([datetime]'2024-02-29 13:37:00', 'Utc')
    attrib +r +h "$root\Docs\hello.txt"
    foreach ($i in 1..1500) { Text ("$root\Big\file_{0:D4}.txt" -f $i) "file $i" }   # needs index blocks outside the MFT
    foreach ($i in 1..600) { Text ("$root\Filler\f{0:D3}.txt" -f $i) ('x' * 65536) }
    foreach ($i in 1..600) { if ($i % 2 -eq 0) { Remove-Item ("$root\Filler\f{0:D3}.txt" -f $i) } }
    Lines "$root\Fragmented\fragmented.txt" 'frag' 260000                               # lands in the holes: hundreds of fragments
    Lines "$root\Compressed\lznt1.txt" 'lznt1' 30000;   compact /c "$root\Compressed\lznt1.txt" | Out-Null
    Lines "$root\Wof\xpress4k.txt" 'xpress4k' 20000;    compact /c /exe:xpress4k "$root\Wof\xpress4k.txt" | Out-Null
    Lines "$root\Wof\xpress16k.txt" 'xpress16k' 20000;  compact /c /exe:xpress16k "$root\Wof\xpress16k.txt" | Out-Null
    Lines "$root\Wof\lzx.txt" 'lzx' 20000;              compact /c /exe:lzx "$root\Wof\lzx.txt" | Out-Null
    New-Item -ItemType Directory -Force (Split-Path $sp) | Out-Null
    $fs = [IO.File]::Create($sp); $fs.Close()
    fsutil sparse setflag $sp | Out-Null
    $fs = [IO.File]::OpenWrite($sp); $fs.SetLength(20MB); $fs.Position = 9MB; $b = [Text.Encoding]::ASCII.GetBytes('island of data ' * 4000); $fs.Write($b, 0, $b.Length); $fs.Close()
    fsutil sparse setrange $sp 0 (9MB) | Out-Null
    fsutil sparse setrange $sp (10MB) (10MB) | Out-Null
    New-Item -ItemType Directory "$root\Links" | Out-Null
    fsutil hardlink create "$root\Links\hardlink-to-report.txt" "$root\Docs\report.txt" | Out-Null
    cmd /c mklink /J "$root\Links\junction-to-docs" "$root\Docs" | Out-Null
    Text "$root\Streams\main.txt" "main stream"
    Set-Content -Path "$root\Streams\main.txt" -Stream hidden -Value "hidden stream"
    Manifest $root 'ntfs-fixture-a.json'
} finally { Detach }
Gzip 'ntfs-fixture-a.vhd.gz'

# ---------------- state B: the same volume after a day of changes
$root = Attach
$deep = $deep -replace '^[A-Z]:', $root
$sp = $sp -replace '^[A-Z]:', $root
try {
    $fs = [IO.File]::Open("$root\Docs\report.txt", 'Open', 'ReadWrite'); $fs.Position = 100000
    $b = [Text.Encoding]::ASCII.GetBytes('MODIFIED IN PLACE ' * 200); $fs.Write($b, 0, $b.Length); $fs.Close()
    [IO.File]::AppendAllText("$root\Fragmented\fragmented.txt", ('appended line' * 30000), $enc)
    foreach ($i in 1..100) { Remove-Item ("$root\Big\file_{0:D4}.txt" -f $i) }
    $nb = New-Object byte[] (2MB); (New-Object Random 11).NextBytes($nb); New-Item -ItemType Directory "$root\New" | Out-Null
    [IO.File]::WriteAllBytes("$root\New\newfile.bin", $nb)
    Rename-Item "$root\Docs\random.bin" 'renamed.bin'
    Move-Item "$deep\deep.txt" "$root\moved-from-deep.txt"
    $fs = [IO.File]::OpenWrite($sp); $fs.Position = 15MB; $b = [Text.Encoding]::ASCII.GetBytes('second island ' * 3000); $fs.Write($b, 0, $b.Length); $fs.Close()
    compact /u "$root\Compressed\lznt1.txt" | Out-Null
    foreach ($i in 1..600) { if ($i % 3 -eq 0 -and (Test-Path ("$root\Filler\f{0:D3}.txt" -f $i))) { Remove-Item ("$root\Filler\f{0:D3}.txt" -f $i) } }
    defrag $root /X /U | Out-Null   # moves files around; Windows writes no change-journal entries for that
    Manifest $root 'ntfs-fixture-b.json'
} finally { Detach }
Gzip 'ntfs-fixture-b.vhd.gz'
Remove-Item $vhd
Write-Host "Fixtures written to $out"
Stop-Transcript | Out-Null
