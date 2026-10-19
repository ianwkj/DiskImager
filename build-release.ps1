# Builds a DiskImager release: the app (single exe) and its installer.
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version 1.1.7
# Output: publish\DiskImager.exe, releases\DiskImager-v<ver>.exe, releases\DiskImagerSetup-<ver>.exe
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH

Write-Host "Publishing DiskImager $Version..."
dotnet publish "$root\DiskImager.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o "$root\publish" -nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing DiskImager failed." }

$payload = "$root\installer\DiskImagerSetup\payload"
New-Item -ItemType Directory -Force $payload | Out-Null
Copy-Item "$root\publish\DiskImager.exe" "$payload\DiskImager.exe" -Force

Write-Host "Building the installer..."
dotnet build "$root\installer\DiskImagerSetup\DiskImagerSetup.csproj" -c Release -p:Version=$Version `
    -o "$root\installer\bin" -nologo
if ($LASTEXITCODE -ne 0) { throw "Building the installer failed." }

New-Item -ItemType Directory -Force "$root\releases" | Out-Null
Copy-Item "$root\publish\DiskImager.exe" "$root\releases\DiskImager-v$Version.exe" -Force
Copy-Item "$root\installer\bin\DiskImagerSetup.exe" "$root\releases\DiskImagerSetup-$Version.exe" -Force
Get-Item "$root\releases\DiskImager-v$Version.exe", "$root\releases\DiskImagerSetup-$Version.exe" |
    ForEach-Object { "{0,-30} {1,8:N1} MB  v{2}" -f $_.Name, ($_.Length / 1MB), $_.VersionInfo.FileVersion }
