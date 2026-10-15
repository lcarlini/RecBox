# Publish a portable RecBox folder (self-contained) with FFmpeg bundled
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "dist\RecBox"
Write-Host "Publishing to $out ..." -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $PSScriptRoot "tools\ffmpeg\ffmpeg.exe"))) {
    Write-Host "Downloading FFmpeg..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "tools\download-ffmpeg.ps1")
}

dotnet publish (Join-Path $PSScriptRoot "src\RecBox\RecBox.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out `
    --nologo

New-Item -ItemType Directory -Force -Path (Join-Path $out "ffmpeg") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "tools\ffmpeg\ffmpeg.exe") (Join-Path $out "ffmpeg\ffmpeg.exe") -Force
Get-ChildItem (Join-Path $PSScriptRoot "tools\ffmpeg\*.dll") -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $out "ffmpeg") -Force }

# Prefer ffmpeg next to the app for published builds
Copy-Item (Join-Path $out "ffmpeg\ffmpeg.exe") (Join-Path $out "ffmpeg.exe") -Force

$csproj = Join-Path $PSScriptRoot "src\RecBox\RecBox.csproj"
[xml]$xml = Get-Content $csproj
$version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }
Set-Content -Path (Join-Path $out "version.txt") -Value $version -Encoding UTF8

Write-Host ""
Write-Host "Portable build ready:" -ForegroundColor Green
Write-Host "  $out\RecBox.exe  (v$version)"
Write-Host ""
Write-Host "Next: .\build-installer.ps1  → zip + optional Setup.exe"
Write-Host "Or zip this folder / run tools\Install-RecBox.ps1 -FromFolder dist\RecBox"
