# Dependencies

Drop these executables here and the app will find them at startup:

```
Dependencies/
├─ ffmpeg.exe
├─ vgmstream-cli.exe
└─ (the DLLs that ship next to vgmstream-cli.exe)
```

`ffprobe.exe` is **not** needed — the app never calls it (it reads frame size and
rate out of the USM/DKIF headers itself). Shipping it would add 158 MB for nothing.

The app also checks `tools/bin/ffmpeg/bin`, `tools/bin/vgmstream`, the repository
root, and finally `PATH`. A copy that sits next to the app always wins, because a
stale system FFmpeg silently changes the output.

## Where to get them

- **ffmpeg** — needs a build that includes `libx264`. The LGPL builds do not; use a
  GPL build: <https://github.com/BtbN/FFmpeg-Builds/releases> → `ffmpeg-master-latest-win64-gpl.zip`.
- **vgmstream** — <https://github.com/vgmstream/vgmstream/releases> → `vgmstream-win64.zip`.

Or run the helper from the repository root, which downloads both and lays them out
under `tools/bin`:

```powershell
pwsh -File tools/fetch-deps.ps1
```

## Why not implement these ourselves

Decoding Wwise Vorbis means re-implementing a proprietary codebook mapping.
Decoding/encoding video means re-implementing a codec. Both are decades of work with
no analysis value. The interesting part of this project is the container formats and
the keystream — so that is what we wrote, and these two do the rest.
