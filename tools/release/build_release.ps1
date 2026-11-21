# =============================================================================
# AuraAudio Unified Release Packager (Portable ZIP + Windows Setup Wizard EXE)
# =============================================================================
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\release\build_release.ps1
#   powershell -ExecutionPolicy Bypass -File tools\release\build_release.ps1 -SkipInstaller
#   powershell -ExecutionPolicy Bypass -File tools\release\build_release.ps1 -PruneOldArtifacts
#
# Uses one fixed staging directory (artifacts\publish\staging) so published
# folders never accumulate across versions.
# =============================================================================

[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SkipInstaller,
    [switch]$PruneOldArtifacts,
    [switch]$KeepStaging
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$csprojPath = Join-Path $repoRoot "AuraAudio.csproj"
$issPath = Join-Path $repoRoot "installer\AuraAudio.iss"
$publishRoot = Join-Path $repoRoot "artifacts\publish"
$stagingDir = Join-Path $publishRoot "staging"
$releaseDir = Join-Path $repoRoot "artifacts\releases"

if (-not (Test-Path $csprojPath)) {
    throw "AuraAudio.csproj not found at: $csprojPath"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csprojXml = Get-Content -Path $csprojPath -Raw
    $Version = ($csprojXml.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Could not read <Version> from AuraAudio.csproj"
    }
    $Version = $Version.Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z\.-]+)?$') {
    throw "Invalid SemVer version string: '$Version'"
}

Write-Host "=== AuraAudio v$Version Release Build ===" -ForegroundColor Cyan

# Optionally prune legacy publish folders and older release archives
if ($PruneOldArtifacts) {
    if (Test-Path $publishRoot) {
        Get-ChildItem -Path $publishRoot -Force | Remove-Item -Recurse -Force
    }
    if (Test-Path $releaseDir) {
        Get-ChildItem -Path $releaseDir -File | Where-Object {
            $_.Name -notlike "*v$Version*"
        } | Remove-Item -Force
    }
}

# 1. Clean single fixed staging directory and publish self-contained single-file win-x64 build
if (Test-Path $stagingDir) {
    Remove-Item -Recurse -Force $stagingDir
}
New-Item -ItemType Directory -Force -Path $stagingDir, $releaseDir | Out-Null

Write-Host "[1/4] Publishing self-contained win-x64 single-file binary to artifacts\publish\staging..."
& dotnet publish $csprojPath -c Release -p:AuraStandalonePublish=true -o $stagingDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2. Strict publish directory hygiene audit
Write-Host "[2/4] Auditing published output..."
$publishedItems = @(Get-ChildItem -Path $stagingDir -Recurse)
$publishedFiles = @($publishedItems | Where-Object { -not $_.PSIsContainer })
$forbidden = @($publishedItems | Where-Object { $_.Name -match 'BenchmarkWorker|NAudio\.Flac|BunLabs|^Cache$|\.log$|\.db$' })

if ($forbidden.Count -gt 0) {
    $names = ($forbidden | Select-Object -ExpandProperty Name) -join ", "
    throw "Forbidden artifact(s) found in publish folder: $names"
}

$exePath = Join-Path $stagingDir "AuraAudio.exe"
if (-not (Test-Path $exePath)) {
    throw "Expected AuraAudio.exe missing from $stagingDir"
}

$unexpected = @($publishedFiles | Where-Object { $_.Name -notin @("AuraAudio.exe", "AuraAudio.pdb") })
if ($unexpected.Count -gt 0) {
    $names = ($unexpected | Select-Object -ExpandProperty Name) -join ", "
    throw "Unexpected file(s) in single-file publish folder: $names"
}

$exeSize = (Get-Item $exePath).Length
$exeHash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash
Write-Host ("  OK: {0} files ({1})" -f $publishedFiles.Count, (($publishedFiles | Select-Object -ExpandProperty Name) -join ", "))

# 3. Package portable standalone ZIP + SHA-256 sidecar
Write-Host "[3/4] Packaging portable ZIP..."
$zipName = "AuraAudio-v$Version-win-x64-standalone.zip"
$zipPath = Join-Path $releaseDir $zipName
$zipShaPath = "$zipPath.sha256"

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipInfo = Get-Item $zipPath
$zipHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
"$zipHash  $zipName" | Set-Content -Path $zipShaPath -Encoding ASCII

# 4. Compile Windows Setup Wizard (Inno Setup 6) + SHA-256 sidecar
$setupInfo = $null
$setupHash = $null
$setupName = "AuraAudio-v$Version-Setup-win-x64.exe"
$setupPath = Join-Path $releaseDir $setupName
$setupShaPath = "$setupPath.sha256"

function Find-InnoSetupCompiler {
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }
    return $null
}

if (-not $SkipInstaller) {
    Write-Host "[4/4] Compiling Windows Setup Wizard via Inno Setup 6..."
    if (-not (Test-Path $issPath)) {
        throw "Inno Setup script not found at: $issPath"
    }

    $iscc = Find-InnoSetupCompiler
    if ([string]::IsNullOrWhiteSpace($iscc)) {
        throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install it once via 'winget install JRSoftware.InnoSetup' or re-run with -SkipInstaller to build only the portable ZIP."
    }

    if (Test-Path $setupPath) { Remove-Item -Force $setupPath }
    & $iscc "/DMyAppVersion=$Version" "/DPublishDir=$stagingDir" "/DOutputDir=$releaseDir" $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC.exe failed with exit code $LASTEXITCODE"
    }
    if (-not (Test-Path $setupPath)) {
        throw "Expected installer output not found at: $setupPath"
    }

    $setupInfo = Get-Item $setupPath
    $setupHash = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash
    "$setupHash  $setupName" | Set-Content -Path $setupShaPath -Encoding ASCII
} else {
    Write-Host "[4/4] Skipping Windows Setup Wizard (-SkipInstaller)." -ForegroundColor Yellow
}

if (-not $KeepStaging -and (Test-Path $stagingDir)) {
    Remove-Item -Recurse -Force $stagingDir
}

Write-Host ""
Write-Host "=== Release Checksums (v$Version) ===" -ForegroundColor Green
Write-Host "| File | Size | SHA-256 |"
Write-Host "| :--- | :---: | :--- |"
if ($setupInfo -ne $null) {
    Write-Host ("| ``{0}`` | ``{1:N0} bytes`` ({2:N2} MB) | ``{3}`` |" -f $setupName, $setupInfo.Length, ($setupInfo.Length / 1MB), $setupHash)
}
Write-Host ("| ``{0}`` | ``{1:N0} bytes`` ({2:N2} MB) | ``{3}`` |" -f $zipName, $zipInfo.Length, ($zipInfo.Length / 1MB), $zipHash)
Write-Host ("| ``AuraAudio.exe`` (inside zip) | ``{0:N0} bytes`` ({1:N2} MB) | ``{2}`` |" -f $exeSize, ($exeSize / 1MB), $exeHash)
Write-Host ""
Write-Host "Output folder: $releaseDir" -ForegroundColor Green
