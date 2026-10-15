# RecBox

A simple, lightweight **Windows-only** screen recorder. Capture one monitor at native resolution, with optional microphone and system audio — no scenes, no streaming, no editing suite.

**Product guide:** [lcarlini.github.io/RecBox](https://lcarlini.github.io/RecBox/) · [local guide](index.html)

---

## Install (for end users)

Settings live in `%AppData%\RecBox` and are **kept across upgrades and reinstalls**.

### Option A — Setup.exe (recommended when available)

1. Download `RecBox-*-Setup.exe` from [GitHub Releases](https://github.com/lcarlini/RecBox/releases).
2. Run the installer (no admin required — installs under `%LocalAppData%\Programs\RecBox`).
3. Start RecBox from the Start Menu or desktop shortcut.

### Option B — PowerShell installer (zip or latest release)

```powershell
# From a downloaded release zip in the current folder:
powershell -ExecutionPolicy Bypass -File .\Install-RecBox.ps1 -FromZip .\RecBox-1.0.0-win-x64.zip -DesktopShortcut -Launch

# Or install/update from the latest GitHub Release automatically:
powershell -ExecutionPolicy Bypass -File .\Install-RecBox.ps1 -FromGitHub -DesktopShortcut -Launch
```

### Update to a new version

Run the new Setup.exe, or:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-RecBox.ps1 -FromGitHub -Launch
```

Program files are replaced; your settings and output folder preferences stay.

### Uninstall

Start Menu → **Uninstall RecBox**, or:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\RecBox\Uninstall-RecBox.ps1"
```

Add `-RemoveSettings` only if you also want to delete `%AppData%\RecBox`.

---

## Build from source (developers)

**One-command run (dev):**

```powershell
powershell -ExecutionPolicy Bypass -File .\run.ps1
```

**Create release packages** (portable zip + Setup.exe if [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed):

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

Outputs:

| Artifact | Purpose |
|----------|---------|
| `dist\RecBox\` | Portable folder |
| `dist\RecBox-VERSION-win-x64.zip` | Upload to GitHub Releases |
| `dist\installer\RecBox-VERSION-Setup.exe` | Windows installer (needs Inno Setup to build) |
| `dist\Install-RecBox.ps1` | Script installer for the zip |

Bump the version in [`src/RecBox/RecBox.csproj`](src/RecBox/RecBox.csproj) (`<Version>`) before each release.

### Requirements to build

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) for Setup.exe
- FFmpeg is downloaded automatically by the scripts

---

## How to record

1. Choose monitor, microphone, system audio, and quality.
2. Click the big **Start** button.
3. Minimize or close the window — RecBox hides to the tray so it stays out of your capture.
4. Stop from the window or tray menu when done.
5. Files land in `Videos\RecBox` by default (changeable in Settings).

## Features

- Modern UI with minimize-to-tray (OBS-like)
- Native monitor resolution, no scaling
- Microphone selection (Default, any device, NVIDIA Broadcast)
- Independent system-audio loopback
- Quality presets: Low file size · Balanced · Maximum quality
- Hardware encode when available (NVENC → Quick Sync → AMF → x264)
- MP4 output (H.264 + AAC)
- Per-user installer with shortcuts and safe upgrades

## Project layout

```
run.ps1                 Dev: download FFmpeg, build, launch
publish.ps1             Portable self-contained publish
build-installer.ps1     Zip + optional Inno Setup.exe
installer/RecBox.iss    Inno Setup script
tools/Install-RecBox.ps1
tools/Uninstall-RecBox.ps1
src/RecBox/             WPF application
index.html              Product website & user guide
```

## Settings

`%AppData%\RecBox\settings.json` — outside the install folder, so updates never wipe them.

## Smoke test

```powershell
dotnet run --project tools/SmokeTest/SmokeTest.csproj -c Release
```

## License notes

RecBox application code is part of this repository. FFmpeg is a separate project with its own license; download and redistribute FFmpeg according to its terms.
