# RecBox one-click setup + run (Windows)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host ""
Write-Host "  RecBox - setup and run" -ForegroundColor Cyan
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "dotnet SDK not found. Install .NET 8 SDK from:" -ForegroundColor Red
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

$ffmpeg = Join-Path $PSScriptRoot "tools\ffmpeg\ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) {
    Write-Host "Downloading FFmpeg..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "tools\download-ffmpeg.ps1")
}

Write-Host "Building RecBox..." -ForegroundColor Yellow
dotnet build (Join-Path $PSScriptRoot "src\RecBox\RecBox.csproj") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Launching..." -ForegroundColor Green
$exe = Join-Path $PSScriptRoot "src\RecBox\bin\Release\net8.0-windows\RecBox.exe"
Start-Process $exe
Write-Host "Done. RecBox is starting." -ForegroundColor Green
