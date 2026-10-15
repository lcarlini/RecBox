<#
.SYNOPSIS
  Uninstall RecBox (per-user install). Keeps settings by default.
#>
param(
    [switch] $RemoveSettings
)

$ErrorActionPreference = "Stop"

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\RecBox"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\RecBox"
$desktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "RecBox.lnk"
$settingsDir = Join-Path $env:APPDATA "RecBox"

Write-Host "Uninstalling RecBox..." -ForegroundColor Cyan

Get-Process -Name "RecBox" -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 1
    if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}

if (Test-Path $installRoot) {
    Remove-Item $installRoot -Recurse -Force
    Write-Host "Removed $installRoot"
}

if (Test-Path $startMenuDir) {
    Remove-Item $startMenuDir -Recurse -Force
    Write-Host "Removed Start Menu shortcuts"
}

if (Test-Path $desktopLnk) {
    Remove-Item $desktopLnk -Force
    Write-Host "Removed desktop shortcut"
}

if ($RemoveSettings) {
    if (Test-Path $settingsDir) {
        Remove-Item $settingsDir -Recurse -Force
        Write-Host "Removed settings $settingsDir"
    }
}
else {
    Write-Host "Settings kept at $settingsDir (pass -RemoveSettings to delete)" -ForegroundColor Yellow
}

Write-Host "RecBox uninstalled." -ForegroundColor Green
