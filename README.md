# RecBox

A simple, lightweight **Windows-only** screen recorder. Capture one monitor at native resolution, with optional microphone and system audio — no scenes, no streaming, no editing suite.

Product guide: open [`index.html`](index.html) in your browser.

## Features

- Minimize to the system tray (stays out of your recording, like OBS)
- Select which monitor to record (native resolution, no scaling)
- Microphone selection (Default, any WASAPI device, including NVIDIA Broadcast)
- Independent system-audio capture via Windows WASAPI loopback (no virtual cable)
- Three quality presets: Low file size, Balanced, Maximum quality
- Automatic hardware encoding when available (NVENC → Quick Sync → AMF → x264)
- MP4 output (H.264 + AAC)

## Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build)
- [FFmpeg](tools/ffmpeg/README.md) (`ffmpeg.exe` in `tools/ffmpeg` or on `PATH`)

## Quick start

```powershell
# 1) Get FFmpeg
powershell -ExecutionPolicy Bypass -File tools/download-ffmpeg.ps1

# 2) Build & run
dotnet build RecBox.slnx -c Release
dotnet run --project src/RecBox/RecBox.csproj -c Release
```

1. Choose monitor, microphone, system audio, and quality.
2. Click **Start recording**.
3. Minimize the window so RecBox moves to the tray and leaves the taskbar.
4. Click **Stop recording** (window or tray menu) when done.
5. Files land in `Videos\RecBox` by default.

## Project layout

```
RecBox.slnx
src/RecBox/           WPF application
tools/ffmpeg/         Place ffmpeg.exe here
tools/download-ffmpeg.ps1
index.html            Product website & user guide
```

## Settings

Stored at `%AppData%\RecBox\settings.json`.

## License notes

RecBox application code is part of this repository. FFmpeg is a separate project with its own license; download and redistribute FFmpeg according to its terms.
