<#
.SYNOPSIS
    Runs the tests, then builds the Windows downloads into artifacts/.

.DESCRIPTION
    artifacts/GitSona-portable.exe   portable, win-x64 (self-contained, no .NET needed)
    artifacts/GitSona-arm64.exe      portable, win-arm64
    artifacts/GitSona-Setup.exe      Velopack installer (per-user, no admin), win-x64
    artifacts/installer/                Velopack feed files (nupkg + releases.win.json) used for auto-update
    artifacts/SHA256SUMS.txt            checksums of every download above

    Code signing is optional and secret-driven: set GITSONA_SIGN_PARAMS to a signtool parameter string
    (for example "/f cert.pfx /p <password> /fd sha256 /tr http://timestamp.digicert.com /td sha256")
    and the installer's files are signed. Without it, builds are unsigned (see README: SmartScreen).

.EXAMPLE
    ./build.ps1 -Version 1.2.3
#>
param(
    [string]$Version = "0.1.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Invoke-Step([string]$Name, [scriptblock]$Block) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Invoke-Step "Tests" { dotnet test GitSona.slnx -c Release }
}

$artifacts = Join-Path $PSScriptRoot "artifacts"
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory $artifacts | Out-Null

$runtimes = @{ "win-x64" = "GitSona-portable.exe"; "win-arm64" = "GitSona-arm64.exe" }
foreach ($rid in $runtimes.Keys) {
    $out = Join-Path $artifacts "publish/$rid"
    Invoke-Step "Publish $rid" {
        dotnet publish src/GitSona.App -c Release -r $rid -p:Version=$Version -o $out
    }
    Copy-Item (Join-Path $out "GitSona.exe") (Join-Path $artifacts $runtimes[$rid])
}

# Installer: only the exe goes into the package (not the .pdb files).
$packDir = Join-Path $artifacts "pack-win-x64"
New-Item -ItemType Directory $packDir | Out-Null
Copy-Item (Join-Path $artifacts "publish/win-x64/GitSona.exe") $packDir

Invoke-Step "Restore tools" { dotnet tool restore }
$vpkArgs = @(
    "pack", "--packId", "GitSona", "--packVersion", $Version, "--packTitle", "GitSona", "--runtime", "win-x64",
    "--packAuthors", "GitSona", "--packDir", $packDir, "--mainExe", "GitSona.exe",
    "--icon", "src/GitSona.App/Assets/app.ico", "--outputDir", (Join-Path $artifacts "installer")
)
if ($env:GITSONA_SIGN_PARAMS) { $vpkArgs += @("--signParams", $env:GITSONA_SIGN_PARAMS) }
else { Write-Host "No GITSONA_SIGN_PARAMS set: installer will be unsigned." -ForegroundColor Yellow }
Invoke-Step "Velopack installer" { dotnet vpk @vpkArgs }

Copy-Item (Join-Path $artifacts "installer/GitSona-win-Setup.exe") (Join-Path $artifacts "GitSona-Setup.exe")
Remove-Item $packDir -Recurse -Force

# Checksums, so people can verify downloads (the exes are not code-signed in v1).
$sums = Get-ChildItem $artifacts -File | Where-Object { $_.Name -match '\.exe$' } | Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
$sums | Set-Content (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ascii

Write-Host "`nDone. Version $Version" -ForegroundColor Green
Get-ChildItem $artifacts -File | ForEach-Object { "{0,12:N0}  {1}" -f $_.Length, $_.Name }
Write-Host "`nSHA256SUMS.txt:"; $sums
