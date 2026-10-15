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
powershell -ExecutionPolicy Bypass -File .\Install-RecBox.ps1 -FromZip .\RecBox-1.1.0-win-x64.zip -DesktopShortcut -Launch

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

```bat
run.cmd
```

(or `powershell -ExecutionPolicy Bypass -File .\run.ps1` if scripts are blocked by policy)

**Create release packages** (portable zip + Setup.exe if [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed):

```bat
build-installer.cmd
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
- RecBox's own window is excluded from the capture, even when it is open on the recorded monitor
- Single instance — launching RecBox again just brings the running window back

## Reliability (you should not lose a recording)

- **Crash-safe files.** Video is written as MKV in small flushed chunks while recording; on stop it is
  converted to MP4 and verified before the MKV is deleted. If RecBox, Windows or the power dies,
  at most a fraction of a second at the end is lost.
- **Automatic recovery.** Unfinished recordings are detected and saved as MP4 the next time RecBox starts.
- **Auto-resume.** If FFmpeg stops or stalls (UAC prompt, lock screen, display or GPU driver change),
  RecBox restarts capture within seconds and joins everything into one file. If the hardware encoder
  fails, it falls back to another encoder automatically.
- **Audio devices.** A slow or unplugged microphone never stops the recording: it reconnects when it
  comes back, and follows the Windows default device if you selected *Default*.
- **Sleep, logoff and shutdown** stop and save the recording cleanly. The PC is kept awake while recording.
- **Disk space.** Recording needs 1 GB free to start and stops cleanly (keeping the file) below 400 MB.
- **Logs:** `%AppData%\RecBox\logs\recbox-YYYYMMDD.log` (kept for 14 days) — attach it when reporting a problem.

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
dotnet run --project tools/SmokeTest/SmokeTest.csproj -c Release          # all reliability tests
dotnet run --project tools/SmokeTest/SmokeTest.csproj -c Release -- soak  # 5-minute recording
powershell -ExecutionPolicy Bypass -File tools\SmokeTest\ui-test.ps1      # end-to-end test of the real app UI
```

The reliability suite covers every audio mode, all monitors, all presets, encoder fallback, A/V sync,
FFmpeg being killed mid-recording, repeated restart failures, rapid start/stop cycles and crash recovery.

## License notes

RecBox application code is part of this repository. FFmpeg is a separate project with its own license; download and redistribute FFmpeg according to its terms.
