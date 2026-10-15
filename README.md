# RecBox

A simple, lightweight **Windows-only** screen recorder. Capture one monitor at native resolution, with optional microphone and system audio — no scenes, no streaming, no editing suite.

**Product guide:** [lcarlini.github.io/RecBox](https://lcarlini.github.io/RecBox/) · [local guide](index.html)

---

## Install & run (easiest)

**Option A — one command** (needs [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
powershell -ExecutionPolicy Bypass -File .\run.ps1
```

This downloads FFmpeg if needed, builds RecBox, and launches it.

**Option B — portable folder** (shareable zip, no SDK on the target PC):

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Then open `dist\RecBox\RecBox.exe`. Zip that folder to give RecBox to someone else.

**Option C — manual**

```powershell
powershell -ExecutionPolicy Bypass -File tools/download-ffmpeg.ps1
dotnet build RecBox.slnx -c Release
dotnet run --project src/RecBox/RecBox.csproj -c Release
```

### Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build (not needed for a published portable build)
- FFmpeg (`run.ps1` / `publish.ps1` fetch it automatically)

---

## How to record

1. Choose monitor, microphone, system audio, and quality.
2. Click the big **Start** button.
3. Minimize or close the window — RecBox hides to the tray so it stays out of your capture.
4. Stop from the window or tray menu when done.
5. Files land in `Videos\RecBox` by default (changeable in Settings).

---

## Features

- Modern UI with minimize-to-tray (OBS-like)
- Native monitor resolution, no scaling
- Microphone selection (Default, any device, NVIDIA Broadcast)
- Independent system-audio loopback
- Quality presets: Low file size · Balanced · Maximum quality
- Hardware encode when available (NVENC → Quick Sync → AMF → x264)
- MP4 output (H.264 + AAC)

## Project layout

```
run.ps1 / publish.ps1   Easy launch & portable publish
src/RecBox/             WPF application
tools/ffmpeg/           Place ffmpeg.exe here (auto-downloaded)
tools/download-ffmpeg.ps1
index.html              Product website & user guide
```

## Settings

Stored at `%AppData%\RecBox\settings.json`.

## Smoke test

```powershell
dotnet run --project tools/SmokeTest/SmokeTest.csproj -c Release
```

## License notes

RecBox application code is part of this repository. FFmpeg is a separate project with its own license; download and redistribute FFmpeg according to its terms.
