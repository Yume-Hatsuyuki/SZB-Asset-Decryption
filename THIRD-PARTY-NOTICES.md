# Third-party notices

This project's own source code is licensed under the **MIT License** (see `LICENSE`).

It depends on two external command-line programs at runtime. They are **downloaded separately**
by the user (or by `tools/fetch-deps.ps1`) and are invoked as **separate processes**.
They are not linked into, compiled into, or redistributed with this application.

---

## FFmpeg

* **Used for**: H.264 / MPEG-1 / VP9 transcoding, MP4 muxing, ADX audio decoding
* **Website**: https://ffmpeg.org/
* **Windows builds**: https://github.com/BtbN/FFmpeg-Builds
* **License**: **GPL v3** (the build used here is `ffmpeg-n8.1-latest-win64-gpl-8.1`)

### Why the GPL build and not LGPL

The MP4 output path requires the `libx264` encoder, which is GPL-licensed.
The LGPL builds of FFmpeg do **not** include `libx264`; attempting to transcode with
them fails with:

```
Unknown encoder 'libx264'
```

If you need an LGPL-only distribution, use an LGPL FFmpeg build and export to `.mkv`
with `-c:v libvpx-vp9` or `-c copy` instead of transcoding to H.264.

### GPL compliance

Because FFmpeg is invoked as a standalone executable via `Process.Start`, this project
considers it "mere aggregation" under the GPL and does not apply the GPL to its own code.

If you redistribute this project **together with** an FFmpeg binary, you take on the GPL
obligations for that binary. To keep things simple, this repository does not ship any
FFmpeg binary. Source code for the exact build is available at:

```
https://github.com/BtbN/FFmpeg-Builds
```

---

## vgmstream

* **Used for**: decoding Wwise `.wem` files (Vorbis variants, Platinum ADPCM) to `.wav`
* **Website**: https://github.com/vgmstream/vgmstream
* **License**: **MIT** (the `vgmstream` library itself), with some components under
  other permissive licenses. See the `COPYING` file inside the release archive.
* **Release used**: `r2168`, asset `vgmstream-win64.zip`

The release archive bundles a number of DLLs (`libvorbis`, `libmpg123`, `libspeex`,
`libcelt`, and FFmpeg-derived `avcodec`/`avformat`/`avutil`/`swresample` for vgmstream).
Each of those carries its own license; refer to the archive's `COPYING` file.

vgmstream is invoked as a separate process and is never linked into this application.

---

## Referenced but not bundled

### wbunpacker

* https://github.com/Hypnos-PD/wbunpacker
* No code copied. Used as **format documentation** — see the "Credits" section of the
  main README for exactly which facts came from it.

### VGMToolbox

* https://github.com/loveemu/VGMToolbox (and the original SourceForge project)
* No code copied. The CRI USM block layout documented in `docs/07_VIDEO.md` was verified
  against the behavior of its `CRID USM Demuxer` module. Our implementation is independent.

### AssetRipper / AssetStudio

* https://github.com/AssetRipper/AssetRipper
* https://github.com/Perfare/AssetStudio
* Downstream consumers of this project's output. Not bundled, not modified.

### UnityPy

* https://github.com/K0lb3/UnityPy
* Used only in the Python teaching scripts (`lessons/`) as an **independent verification
  implementation**. Not a runtime dependency of the C# tool.

---

## Game assets

No game assets, extracted textures, audio, video, or game code are included in this
repository. All sample values quoted in the documentation are short byte sequences
needed to describe a file format, not usable content.

*Shadowverse: Worlds Beyond* is a trademark of Cygames, Inc. This project is not
affiliated with or endorsed by Cygames.
