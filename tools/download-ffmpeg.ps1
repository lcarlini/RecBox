# Downloads a Windows x64 FFmpeg build into tools/ffmpeg
# RecBox needs ddagrab/gdigrab + H.264 encoders. Audio is captured via NAudio (WASAPI), not FFmpeg.
$ErrorActionPreference = "Stop"
$destDir = Join-Path $PSScriptRoot "ffmpeg"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

$url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$zipPath = Join-Path $env:TEMP "ffmpeg-release-essentials.zip"
$extractPath = Join-Path $env:TEMP "ffmpeg-essentials-extract"

Write-Host "Downloading FFmpeg essentials..."
Invoke-WebRequest -Uri $url -OutFile $zipPath

if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

$ffmpeg = Get-ChildItem -Path $extractPath -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
if (-not $ffmpeg) { throw "ffmpeg.exe not found in archive." }

Copy-Item $ffmpeg.FullName -Destination (Join-Path $destDir "ffmpeg.exe") -Force

$binDir = $ffmpeg.DirectoryName
Get-ChildItem -Path $binDir -Filter "*.dll" -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName -Destination $destDir -Force }

Write-Host "Installed: $(Join-Path $destDir 'ffmpeg.exe')"
