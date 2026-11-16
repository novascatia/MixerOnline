<#
.SYNOPSIS
    Packages MixerOnline Beta into a standalone, portable distribution directory and ZIP archive.
#>

param(
    [string]$Version = "1.2.1"
)

$ErrorActionPreference = "Stop"
$Version = $Version.TrimStart('v', 'V')
$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root "dist"
$exeName = "MixerOnline-beta.exe"
$archiveName = "MixerOnline-beta-v$Version-win-x64.zip"
$archivePath = Join-Path $distDir $archiveName

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Packaging MixerOnline Beta v$Version (win-x64)" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

if (Test-Path $distDir) {
    Remove-Item -Recurse -Force $distDir
}
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host "`n[1/4] Publishing self-contained executable for Windows x64..." -ForegroundColor Yellow
dotnet publish "$root\src\MixerOnline.App\MixerOnline.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o "$distDir"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $distDir $exeName
if (-not (Test-Path $publishedExe)) {
    $fallbackAppExe = Join-Path $distDir "MixerOnline.App.exe"
    $fallbackBaseExe = Join-Path $distDir "MixerOnline.exe"
    if (Test-Path $fallbackAppExe) {
        Move-Item -Path $fallbackAppExe -Destination $publishedExe -Force
    }
    elseif (Test-Path $fallbackBaseExe) {
        Move-Item -Path $fallbackBaseExe -Destination $publishedExe -Force
    }
    else {
        throw "Expected executable '$exeName' was not produced in $distDir"
    }
}

Get-ChildItem -Path $distDir -Filter "*.pdb" -File | Remove-Item -Force

Write-Host "`n[2/4] Copying Chromium Audio Bridge extension files..." -ForegroundColor Yellow
$extTarget = Join-Path $distDir "extension"
Copy-Item -Recurse -Force "$root\extension" $extTarget

Write-Host "`n[3/4] Copying documentation and license..." -ForegroundColor Yellow
Copy-Item -Force "$root\README.md" (Join-Path $distDir "README.md")
if (Test-Path "$root\LICENSE") {
    Copy-Item -Force "$root\LICENSE" (Join-Path $distDir "LICENSE")
}
if (Test-Path "$root\docs") {
    Copy-Item -Recurse -Force "$root\docs" (Join-Path $distDir "docs")
}

Write-Host "`n[4/4] Creating release archive: $archiveName..." -ForegroundColor Yellow
$archiveItems = @(
    $publishedExe,
    $extTarget,
    (Join-Path $distDir "README.md")
)
if (Test-Path (Join-Path $distDir "LICENSE")) {
    $archiveItems += (Join-Path $distDir "LICENSE")
}
if (Test-Path (Join-Path $distDir "docs")) {
    $archiveItems += (Join-Path $distDir "docs")
}

Compress-Archive -Path $archiveItems -DestinationPath $archivePath -Force

Write-Host "`nPackage created successfully in: $distDir" -ForegroundColor Green
Write-Host "  -> Executable: $publishedExe" -ForegroundColor White
Write-Host "  -> Archive:    $archivePath" -ForegroundColor White
Write-Host "  -> Extension:  $extTarget\" -ForegroundColor White
Write-Host "=============================================" -ForegroundColor Green
