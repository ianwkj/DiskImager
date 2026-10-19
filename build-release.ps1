# Builds a DiskImager release: the app (single exe) and its installer, and signs the installer for auto-update.
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version 1.2.0
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version 1.2.0 -Publish -NotesFile notes.md
# Output: publish\DiskImager.exe, releases\DiskImager-v<ver>.exe, releases\DiskImagerSetup-<ver>.exe (+ .sig)
# -Publish (all changes must be committed) tags the current commit v<ver>, pushes it and creates the GitHub release
# with the installer and its signature. Installed copies of DiskImager pick it up the next time they open.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Publish,
    [string]$NotesFile = "",
    # Private half of the update signing key. Never commit it; back it up somewhere safe.
    [string]$SigningKey = (Join-Path $env:USERPROFILE '.diskimager\update-signing-key.xml')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:PATH = "C:\Program Files\dotnet;C:\Program Files\Git\cmd;C:\Program Files\GitHub CLI;" + $env:PATH

if (-not (Test-Path $SigningKey)) {
    throw "The update signing key isn't at $SigningKey. Restore it from your backup: without it, installed copies won't accept this release as an update."
}

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
$setup = "$root\releases\DiskImagerSetup-$Version.exe"
Copy-Item "$root\publish\DiskImager.exe" "$root\releases\DiskImager-v$Version.exe" -Force
Copy-Item "$root\installer\bin\DiskImagerSetup.exe" $setup -Force

# Signature: RSA (PKCS#1 v1.5) over the installer's SHA-256, checked by Core\Updater.cs against Core\UpdateKey.cs.
Write-Host "Signing the installer for auto-update..."
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$rsa.FromXmlString([IO.File]::ReadAllText($SigningKey))
$sha = [System.Security.Cryptography.SHA256]::Create()
$stream = [IO.File]::OpenRead($setup)
try { $hash = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
$sig = $rsa.SignHash($hash, [System.Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
[IO.File]::WriteAllText("$setup.sig", [Convert]::ToBase64String($sig))
$rsa.Dispose()

Get-Item "$root\releases\DiskImager-v$Version.exe", $setup |
    ForEach-Object { "{0,-30} {1,8:N1} MB  v{2}" -f $_.Name, ($_.Length / 1MB), $_.VersionInfo.FileVersion }

if ($Publish) {
    Push-Location $root
    try {
        if (git status --porcelain) { throw "There are uncommitted changes. Commit them first so the release matches the code on GitHub." }
        $notesArgs = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
        git tag "v$Version"
        git push origin main "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "Pushing to GitHub failed." }
        gh release create "v$Version" $setup "$setup.sig" --title "DiskImager $Version" @notesArgs
        if ($LASTEXITCODE -ne 0) { throw "Creating the GitHub release failed." }
        Write-Host "Published. Installed copies will offer $Version the next time they open."
    } finally { Pop-Location }
}
