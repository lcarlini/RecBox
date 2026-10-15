# Build portable zip + optional Inno Setup installer (Setup.exe)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$csproj = Join-Path $PSScriptRoot "src\RecBox\RecBox.csproj"
[xml]$xml = Get-Content $csproj
$version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }

Write-Host ""
Write-Host "  Building RecBox $version release packages" -ForegroundColor Cyan
Write-Host ""

# 1) Publish portable app + FFmpeg
& (Join-Path $PSScriptRoot "publish.ps1")

$publishDir = Join-Path $PSScriptRoot "dist\RecBox"
Set-Content -Path (Join-Path $publishDir "version.txt") -Value $version -Encoding UTF8

# 2) Versioned zip for GitHub Releases / Install-RecBox.ps1 -FromGitHub
$distDir = Join-Path $PSScriptRoot "dist"
$zipName = "RecBox-$version-win-x64.zip"
$zipPath = Join-Path $distDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Creating $zipName ..." -ForegroundColor Yellow
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

# Also copy installer scripts into dist for convenience
Copy-Item (Join-Path $PSScriptRoot "tools\Install-RecBox.ps1") (Join-Path $distDir "Install-RecBox.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "tools\Uninstall-RecBox.ps1") (Join-Path $distDir "Uninstall-RecBox.ps1") -Force

# 3) Inno Setup -> Setup.exe (if installed)
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

$installerDir = Join-Path $distDir "installer"
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

if ($iscc) {
    Write-Host "Compiling Inno Setup installer..." -ForegroundColor Yellow
    $iss = Join-Path $PSScriptRoot "installer\RecBox.iss"
    & $iscc "/DMyAppVersion=$version" "/DSourceDir=$publishDir" $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
    Write-Host "Setup.exe ready in dist\installer\" -ForegroundColor Green
}
else {
    Write-Host "Inno Setup 6 not found - skipping Setup.exe." -ForegroundColor Yellow
    Write-Host "Install from https://jrsoftware.org/isinfo.php then re-run, or use the zip + Install-RecBox.ps1." -ForegroundColor Yellow

    $bootstrap = Join-Path $distDir "Install-RecBox-FromZip.ps1"
    $lines = @(
        "# Installs the RecBox zip in this folder",
        "`$zip = Join-Path `$PSScriptRoot '$zipName'",
        "if (-not (Test-Path `$zip)) { throw `"Missing $zipName next to this script.`" }",
        "& (Join-Path `$PSScriptRoot 'Install-RecBox.ps1') -FromZip `$zip -DesktopShortcut -Launch"
    )
    Set-Content -Path $bootstrap -Value $lines -Encoding UTF8
}

Write-Host ""
Write-Host "Release artifacts:" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  dist\Install-RecBox.ps1"
if ($iscc) {
    Write-Host "  dist\installer\RecBox-$version-Setup.exe"
}
Write-Host ""
Write-Host "End users:" -ForegroundColor Cyan
Write-Host "  - Run Setup.exe (if built), OR"
Write-Host "  - powershell -ExecutionPolicy Bypass -File Install-RecBox.ps1 -FromZip .\$zipName -DesktopShortcut -Launch"
Write-Host "  - Or after a GitHub Release: Install-RecBox.ps1 -FromGitHub -DesktopShortcut -Launch"
Write-Host ""
Write-Host "Settings always stay in %AppData%\RecBox across upgrades." -ForegroundColor Green
