<#
.SYNOPSIS
    Builds MixerOnline and executes the automated test suite.
#>

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "         Building MixerOnline ($Configuration)" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

# 1. Build Desktop Application
Write-Host "`n[1/3] Building WPF Desktop Application..." -ForegroundColor Yellow
dotnet build "$root\src\MixerOnline.App\MixerOnline.App.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed!" }

# 2. Build and Run Automated Test Suite
Write-Host "`n[2/3] Running Automated Test Suite..." -ForegroundColor Yellow
dotnet run --project "$root\tests\MixerOnline.Tests\MixerOnline.Tests.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Automated tests failed!" }

Write-Host "`n[3/3] Build & Verification Complete!" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green
