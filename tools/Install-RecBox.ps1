<#
.SYNOPSIS
  Install or update RecBox on Windows (per-user, no admin required).

.DESCRIPTION
  - Installs to %LocalAppData%\Programs\RecBox
  - Bundles/keeps FFmpeg with the app
  - Creates Start Menu (+ optional Desktop) shortcuts
  - Upgrades replace program files only — settings in %AppData%\RecBox are preserved

.PARAMETER FromZip
  Path to a RecBox portable zip (from publish / GitHub Release).

.PARAMETER FromFolder
  Path to an already-published RecBox folder (e.g. dist\RecBox).

.PARAMETER FromGitHub
  Download and install the latest GitHub Release asset matching *win-x64*.zip.

.PARAMETER DesktopShortcut
  Also create a Desktop shortcut.

.PARAMETER Launch
  Start RecBox after install.
#>
[CmdletBinding(DefaultParameterSetName = "GitHub")]
param(
    [Parameter(ParameterSetName = "Zip")]
    [string] $FromZip,

    [Parameter(ParameterSetName = "Folder")]
    [string] $FromFolder,

    [Parameter(ParameterSetName = "GitHub")]
    [switch] $FromGitHub,

    [string] $Repo = "lcarlini/RecBox",

    [switch] $DesktopShortcut,
    [switch] $Launch
)

$ErrorActionPreference = "Stop"

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\RecBox"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\RecBox"
$settingsDir = Join-Path $env:APPDATA "RecBox"
$versionFile = Join-Path $installRoot "version.txt"

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "  $msg" -ForegroundColor Green }

function Get-InstalledVersion {
    if (Test-Path $versionFile) { return (Get-Content $versionFile -Raw).Trim() }
    $exe = Join-Path $installRoot "RecBox.exe"
    if (Test-Path $exe) {
        try { return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion } catch { return "unknown" }
    }
    return $null
}

function New-Shortcut([string]$Path, [string]$Target, [string]$WorkDir) {
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut($Path)
    $sc.TargetPath = $Target
    $sc.WorkingDirectory = $WorkDir
    $sc.IconLocation = "$Target,0"
    $sc.Description = "RecBox screen recorder"
    $sc.Save()
}

function Expand-SourceZip([string]$zipPath, [string]$dest) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Expand-Archive -Path $zipPath -DestinationPath $dest -Force

    # Zip may contain a single top-level RecBox folder
    $exe = Get-ChildItem -Path $dest -Filter "RecBox.exe" -Recurse | Select-Object -First 1
    if (-not $exe) { throw "RecBox.exe not found inside archive." }
    return $exe.Directory.FullName
}

function Install-FromDirectory([string]$sourceDir, [string]$version) {
    Write-Step "Installing to $installRoot"
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

    # Stop running instance if present
    Get-Process -Name "RecBox" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Step "Closing running RecBox (PID $($_.Id))..."
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 1
        if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    }

    # Replace files in place (keeps folder unblocked for upgrades)
    $staging = Join-Path $env:TEMP ("RecBoxInstall_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    try {
        Copy-Item -Path (Join-Path $sourceDir "*") -Destination $staging -Recurse -Force
        # Clear install dir contents but never touch AppData settings
        Get-ChildItem -Path $installRoot -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $staging "*") -Destination $installRoot -Recurse -Force
    }
    finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not $version) {
        $exe = Join-Path $installRoot "RecBox.exe"
        try { $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion } catch { $version = "1.0.0" }
    }
    Set-Content -Path $versionFile -Value $version -Encoding UTF8

    # Ensure recordings default folder exists (settings still owned by app)
    $videos = Join-Path ([Environment]::GetFolderPath("MyVideos")) "RecBox"
    New-Item -ItemType Directory -Force -Path $videos | Out-Null
    New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null

    Write-Step "Creating shortcuts"
    New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
    $exePath = Join-Path $installRoot "RecBox.exe"
    New-Shortcut -Path (Join-Path $startMenuDir "RecBox.lnk") -Target $exePath -WorkDir $installRoot

    if ($DesktopShortcut) {
        $desktop = [Environment]::GetFolderPath("Desktop")
        New-Shortcut -Path (Join-Path $desktop "RecBox.lnk") -Target $exePath -WorkDir $installRoot
    }

    # Uninstall helper next to app
    $uninstallPs1 = Join-Path $installRoot "Uninstall-RecBox.ps1"
    $uninstallSrc = Join-Path $PSScriptRoot "Uninstall-RecBox.ps1"
    if (-not (Test-Path $uninstallSrc)) {
        $uninstallSrc = Join-Path (Split-Path $PSScriptRoot -Parent) "tools\Uninstall-RecBox.ps1"
    }
    if (Test-Path $uninstallSrc) {
        Copy-Item $uninstallSrc $uninstallPs1 -Force
        $wsh = New-Object -ComObject WScript.Shell
        $sc = $wsh.CreateShortcut((Join-Path $startMenuDir "Uninstall RecBox.lnk"))
        $sc.TargetPath = "powershell.exe"
        $sc.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$uninstallPs1`""
        $sc.WorkingDirectory = $installRoot
        $sc.IconLocation = "$exePath,0"
        $sc.Description = "Uninstall RecBox"
        $sc.Save()
    }

    Write-Ok "Installed RecBox $version"
    Write-Ok "Settings folder (kept across updates): $settingsDir"
}

# --- resolve source ---
Write-Host ""
Write-Host "  RecBox installer" -ForegroundColor Cyan
Write-Host ""

$previous = Get-InstalledVersion
if ($previous) { Write-Step "Currently installed: $previous" }

$work = Join-Path $env:TEMP ("RecBoxSetup_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$version = $null

try {
    if ($PSCmdlet.ParameterSetName -eq "Folder" -or $FromFolder) {
        if (-not $FromFolder) { throw "-FromFolder path is required." }
        $sourceDir = (Resolve-Path $FromFolder).Path
        if (-not (Test-Path (Join-Path $sourceDir "RecBox.exe"))) {
            throw "RecBox.exe not found in $sourceDir"
        }
    }
    elseif ($PSCmdlet.ParameterSetName -eq "Zip" -or $FromZip) {
        if (-not $FromZip) { throw "-FromZip path is required." }
        $sourceDir = Expand-SourceZip -zipPath (Resolve-Path $FromZip).Path -dest (Join-Path $work "extract")
        if ($FromZip -match "(\d+\.\d+\.\d+)") { $version = $Matches[1] }
    }
    else {
        # Default / -FromGitHub: latest release
        Write-Step "Fetching latest release from GitHub ($Repo)..."
        $api = "https://api.github.com/repos/$Repo/releases/latest"
        try {
            $release = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "RecBox-Installer" }
        }
        catch {
            throw "Could not reach GitHub releases. Build a local package with publish.ps1 and use -FromFolder/-FromZip, or publish a Release first."
        }

        $version = $release.tag_name.TrimStart("v")
        $asset = $release.assets |
            Where-Object { $_.name -match "win-x64.*\.zip$" -or $_.name -match "RecBox-.*\.zip$" } |
            Select-Object -First 1

        if (-not $asset) {
            throw "No zip asset found on the latest release. Upload RecBox-*-win-x64.zip when publishing."
        }

        if ($previous -and $previous -eq $version) {
            Write-Ok "Already on latest version ($version)."
            if ($Launch) { Start-Process (Join-Path $installRoot "RecBox.exe") }
            return
        }

        $zipPath = Join-Path $work $asset.name
        Write-Step "Downloading $($asset.name)..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath
        $sourceDir = Expand-SourceZip -zipPath $zipPath -dest (Join-Path $work "extract")
    }

    Install-FromDirectory -sourceDir $sourceDir -version $version

    if ($Launch) {
        Write-Step "Launching RecBox..."
        Start-Process (Join-Path $installRoot "RecBox.exe")
    }

    Write-Host ""
    Write-Host "  Done. Start Menu → RecBox" -ForegroundColor Green
    Write-Host ""
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
