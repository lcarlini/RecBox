# FFmpeg for RecBox

RecBox uses FFmpeg for screen capture and video encoding. Microphone and system audio are captured natively via Windows WASAPI (NAudio) and piped into FFmpeg — so a WASAPI demuxer inside FFmpeg is **not** required.

The `ffmpeg.exe` binary is **not** committed to this repository (it is large and licensed separately).

## Required features

Your FFmpeg build should include:

- `ddagrab` (Desktop Duplication capture) and/or `gdigrab`
- Encoders: `h264_nvenc`, `h264_qsv`, `h264_amf`, and/or `libx264`
- Ability to read raw PCM from a named pipe (`-f s16le`)

## Setup

### Automatic (recommended)

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools/download-ffmpeg.ps1
```

### Manual

1. Download a recent **Windows x64** FFmpeg build.
2. Copy `ffmpeg.exe` into:
   ```
   tools/ffmpeg/ffmpeg.exe
   ```
3. Optionally copy accompanying DLLs from a shared build into the same folder.

RecBox also finds `ffmpeg.exe` if it is already on your system `PATH`.
