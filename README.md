# SZB Asset Decryption

Reverse-engineering notes and a working restoration toolkit for the **encrypted Unity AssetBundles**
shipped with *Shadowverse: Worlds Beyond* (SZB / SVWB).

The game modifies every AssetBundle with a streaming XOR cipher applied **after** the first 256 bytes.
Stock tools such as AssetRipper or AssetStudio therefore read the bundle header fine, but fail on every
single data block:

```
AssetRipper.IO.Files.Exceptions.DecompressionFailedException:
  Could not write any bytes for 'cab-b730676aac4eb825c976cf950b770e95'
  while decompressing Lz4HC.
```

This repository documents how that cipher works, how the key was recovered **without reading it out of
memory or the executable**, and provides a C# desktop tool that turns the on-disk files back into
plain, tool-readable `UnityFS` bundles.

| | |
|---|---|
| **Cipher** | `keystream[i] = base[(i % 88) >> 3] ^ key[(i % 88) & 7]`, applied from absolute offset `0x100` |
| **Base key** | 11 bytes (period 88), recovered by GF(2) linear algebra. **Neither compiled in nor downloaded** — derived from the local install each time |
| **Per-file key** | `int64`, published in the game's own `assetbundle.manifest` |
| **Coverage** | 36 673 / 36 673 UnityFS bundles restored, 0 failed block checks, 20.49 GiB |
| **Verification** | 400 random bundles parsed by UnityPy (an independent implementation), 22 111 objects read |

---

## Contents

| Path | What is inside |
|---|---|
| `docs/` | The full analysis, written as a security report. Start with [`docs/03_WALKTHROUGH.md`](docs/03_WALKTHROUGH.md) |
| `lessons/` | The original Python scripts used during the investigation. **Teaching material, not a product.** |
| `src/SzbTool.Core/` | The real implementation: bundle parsing, decryption, manifest, audio and video containers |
| `src/SzbTool.App/` | WPF desktop interface |
| `src/SzbTool.Cli/` | Command-line front end (`derive`, `restore`, `audio`, `video`, `verify`, `selftest`) |
| `tools/fetch-deps.ps1` | Downloads the external CLI decoders (`ffmpeg`, `vgmstream-cli`) |
| `.github/workflows/` | CI: compile check on every push, and tag-triggered builds published to Releases |

---

## Quick start

### Just want to run it

Grab the latest `SzbTool-*-win-x64.zip` from [Releases](../../releases), unpack it, run `SzbTool.exe`.
It carries its own .NET runtime and the two decoders, so nothing else needs installing.

### Requirements

* Windows 10/11 and the .NET 10 SDK (the projects target `net10.0` / `net10.0-windows`)
* The game installed locally (only the `Persistent/dat` folder is needed)
* `ffmpeg.exe` and `vgmstream-cli.exe` — bundled in the release zip; for a source build, the app can fetch them for you, see [docs/08_TOOLING.md](docs/08_TOOLING.md). `ffprobe.exe` is not used.
* **No network access at any point.** The base key is computed from the files already on disk.

### Build

```powershell
dotnet build src/SzbTool.sln -c Release
```

### Run

```powershell
dotnet run --project src/SzbTool.App -c Release
```

Then:

1. **Game folder** → point at `...\ShadowverseWB_Data\Persistent\dat`
2. **Manifest** → normally auto-detected. The game stores its own exact-version manifest inside `dat`,
   and the tool finds and parses it for you.
3. **Output** → pick an empty folder
4. **Key** → fetched automatically on start-up, see below
5. Press **Restore**. The tool writes a mirror tree of plain `.ab` files named after the real asset path,
   ready to be dropped into AssetRipper.

The `Audio` and `Video` tabs do the same for the two container formats that are *not* Unity bundles.

### Where the base key comes from

The key is **data, not code** — and it is not hosted anywhere either. It is *computed* from the local
install every time the tool needs it. Nothing is downloaded; no key ships in the binary or in the repo.

The tool needs exactly one input that it cannot generate: the manifest the game already stores inside
`Persistent/dat`, which supplies every bundle's own `int64` key. Everything else comes from arithmetic:

| Step | What happens |
|---|---|
| 1 | Read the local manifest → one `int64` key per bundle. |
| 2 | Scan bundle headers. Two archives with the same content but different data offsets form a pair where the plaintext cancels out. |
| 3 | XOR the two ciphertexts and the two keys: `base[a] ^ base[b]`. If the plaintext really is identical this value stays constant across eight consecutive bytes (2^-56 by chance otherwise). Each such window is a linear equation over GF(2). |
| 4 | Try key lengths 2..64 and run Gaussian elimination. A wrong length produces `0 = 1`. Only 11 — and its multiples — are consistent. |
| 5 | Rank is 80 of 88 unknowns, so 256 candidates survive. Feed each to LZ4: only the real one decompresses to the declared block size. |

About twenty seconds, ~34 000 readable bundles, and a few dozen same-content pairs. `SzbTool.Cli derive
--dat <dir>` runs exactly this and prints the answer with the counts that produced it.

An empty key is not a soft failure: `Restore` refuses to start rather than write twenty gigabytes of
half-encrypted bundles.

The result is cached at `%LOCALAPPDATA%\SzbTool\base-key.json`, tied to the manifest's size and
timestamp — a game update invalidates it automatically, and **重新计算** in Settings forces a fresh run.

---

## Desktop interface

Six pages, one job each. Everything the CLI can do is reachable by clicking, and the run log is shared
so a mixed session reads as a single timeline.

![Overview](docs/img/01_overview.png)

**Overview** picks the two folders, loads the manifest (42 119 rows, auto-detected from inside `dat`) and
runs a read-only census: how many files, how big, which kinds, how many bundles parse, how many straddle
the `0x100` boundary where the cipher starts.

![Restore](docs/img/02_assets.png)

**Restore** removes the XOR layer and writes a mirror tree named after the real asset paths. The run above
is a 12-bundle smoke test: 12/12 in 59 ms, 3.75 MiB.

![Video](docs/img/04_video.png)

**Video** demuxes USM and hands the elementary streams to ffmpeg. Quality tiers are collapsed first — one
movie usually ships as `High` / `Middle` / `Low`, so 200 files become 1.

Two details worth knowing:

* **Failures are explained, not counted.** Every error line names the file and the reason
  (`解析失败` = wrong key, `布局校验失败` = version mismatch, `清单缺失` = not in the manifest), because
  "1 234 files failed" is useless on its own.
* **Any run can be capped.** The `本次只处理前 N 个` box limits a job to the first N items. It exists so
  nobody discovers a wrong output folder 20 GiB later.

The screenshots above are generated by the app itself (`SzbTool.exe --shot docs/img --run`), which renders
every page off-screen and, with `--run`, executes a small real job on each task page first. It is used as a
smoke test: if any page fails to build its visual tree, the command throws. The whole run happens offline —
including the key derivation shown in the Settings page.

### Command line

`SzbTool.Cli` exposes the same engine:

```powershell
# restore every UnityFS bundle
SzbTool.Cli restore --dat "D:\...\Persistent\dat" --out "D:\restored"

# unpack every Wwise .pck into .wem and convert to .wav
SzbTool.Cli audio  --dat "D:\...\Persistent\dat" --out "D:\audio" --format wav

# demux every CRI .usm into .mp4
SzbTool.Cli video  --dat "D:\...\Persistent\dat" --out "D:\video" --format mp4

# classify the whole directory and print a census
SzbTool.Cli scan   --dat "D:\...\Persistent\dat"
```

---

## What the game actually changed

Nothing about the container. It is a completely standard **UnityFS v8** bundle:

```
"UnityFS\0" | version | unityVersion | unityRevision | size | ciBlock | uiBlock | flags
            | compressed blocksInfo | 16-byte align | data blocks
```

The only modification is a byte-wise XOR applied to everything from offset `0x100` to EOF:

```
offset 0x000 .. 0x0FF   plaintext   (header, block table — this is why tools see a valid bundle)
offset 0x100 .. EOF     ciphertext  (blocksInfo tail + every data block)
```

The keystream is periodic with period `8 × len(base)`, and within one period byte `i` is
`base[i >> 3] ^ key[i & 7]`. `key` is a signed 64-bit integer that differs per file.

The base was not taken from the executable or from process memory. It was solved as a system of linear
equations over GF(2), using pairs of bundles that share the same compression stream but start at a
different `data_start`. The full derivation — including the two dead ends that had to be cleared first —
is in [`docs/03_WALKTHROUGH.md`](docs/03_WALKTHROUGH.md).

---

## Tooling decisions

A deliberate split between “write it” and “shell out to it”.

| Task | Own code | External CLI | Why |
|---|---|---|---|
| UnityFS parsing + XOR | ✅ C# | — | Nothing off-the-shelf knows about this cipher |
| Manifest (msgpack) parsing | ✅ C# | — | ~300 lines, fixed schema, no reason to add a dependency |
| AKPK (Wwise `pck`) unpack | ✅ C# | — | Chunk chain + a 12-byte-per-entry index table. `wwiser` analyses banks, it does not export |
| CRI USM demux | ✅ C# | — | Block chain is trivial; the hard part is codec work, which is *not* ours |
| WEM → WAV | — | ✅ `vgmstream-cli` | Wwise Vorbis uses a private codebook layout. Reimplementing it would be months of work and worse results |
| MPEG-1 / VP9 / H.264 decode | — | ✅ `ffmpeg` | Never write a video decoder |
| MP4 / MKV muxing | — | ✅ `ffmpeg` | Same |
| GUI | ✅ WPF | — | Product-specific |

Both external tools are invoked as **separate processes**. They are never linked into this application,
so the MIT licence of this repository stays clean; see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

---

## Code census

The directory holds 40 702 files. They are four different things wearing the same shape:

| Kind | Count | Size | Handling |
|---|---|---|---|
| UnityFS bundle | 36 673 | 20.5 GiB | XOR restore → `.ab` |
| AKPK (Wwise `pck`) | 3 660 | 2.92 GiB | unpack → `.wem` → `vgmstream` → `.wav` |
| CRID (CRI `usm`) | 346 | 2.80 GiB | demux → `ffmpeg` → `.mp4` |
| PNG / OTF / other | 23 | 54 MiB | already plain files |

Of the 346 videos, 326 are VP9-in-IVF and 20 are MPEG-1 elementary streams; 6 carry an ADX audio track.
Of the 3 660 sound banks, 14 986 individual waveforms were counted.

---

## Credits — what `wbunpacker` contributed

This project would not have started on the right foot without
[`wbunpacker` (wbu)](https://github.com/Hypnos-PD/wbunpacker). It is a Rust tool by **Hypnos-PD** that
downloads, decrypts and exports SZB assets, and it is the reason the shape of the problem was known at
all. Concretely:

### 1. It described the cipher's *shape*, which made a black-box attack possible

`crates/asset/src/lib.rs` contains `AssetBundleDecryptor`. Reading that struct tells you three things
that are otherwise impossible to guess:

```rust
const HEADER_SKIP_BYTES: u64 = 0x100;

// keystream layout, reconstructed from the struct's constructor
keystream[i * 8 + j] = base_keys[i] ^ asset_key_bytes[j];
```

* the cipher starts at absolute offset `0x100` (not at `data_start`, not after the header — `0x100`);
* the keystream is periodic, with a period that is a multiple of the base key length;
* each period is `base[i >> 3] ^ key[i & 7]` — i.e. **byte `j` of the per-file key only ever affects
  keystream positions congruent to `j` mod 8**.

That last point is the single most valuable fact in the whole investigation. It is what makes the
“8 plaintext bytes → 8 key bytes” attack in `docs/03_WALKTHROUGH.md` sound, and it is what told us the
base key had to be short enough to be searched for with linear algebra rather than brute force.

**Without this model we would have had no target to solve for.** We would have known *that* the data was
encrypted, and nothing about *how*, which is where most reverse-engineering attempts on this title stop.

### 2. It confirmed the per-file key is an `int64` that lives in the manifest

`asset_key: i64` in the constructor signature, plus the `asset_bundle_base_keys` config entry, established
that the key material is **per-asset and data-driven**, so the manifest must contain it. That turned an
open-ended search into “parse the manifest, join on filename”. `docs/05_MANIFEST.md` follows that thread.

### 3. It mapped the surrounding pipeline

* manifest retrieval from the CDN and its MasterMemory + msgpack layout,
* the fact that AKPK payloads are Wwise banks that `vgmstream` can decode,
* the `vgmstream` → `ffmpeg` chain for audio,
* `AssetStudioModCLI` as the downstream extractor (this project uses AssetRipper instead, but the idea
  of “restore to a plain bundle, then hand off” is the same),
* the existence of a SQLite3MC-encrypted `meta.db`.

### What this project adds on top

`wbunpacker` **consumes** a base key that the user must supply (`asset_bundle_base_keys`, base64).
It does not document where that value comes from, and the value is not published in the repository.

This project recovers the base key **from the game files alone**, with no memory scraping, no debugger,
and no access to the executable's constants:

```
base = <11 bytes>        (solved from the ciphertext; see docs/03_WALKTHROUGH.md)
```

The tool does not freeze one build's value into a binary or a config file: it recomputes this from the
local files every time, because the value changes when the game updates. `docs/02_ENCRYPTION.md` records
the value this analysis produced, together with the version it belongs to.

That one line is the whole point of `docs/03_WALKTHROUGH.md`. Everything else — the manifest
parsing, the audio and video work — is engineering; the recovery is the research.

We are grateful to `wbunpacker` for publishing enough of the mechanism to make the question askable.
Please support the original project.

---

## Legal

This repository contains **no game assets and no game code**. It contains file-format documentation and
original source code. Use it only on a copy of a game you legitimately own, for personal study,
interoperability, or preservation. Do not redistribute extracted assets.

---

## Documentation index

| Document | Content |
|---|---|
| [`docs/00_GLOSSARY.md`](docs/00_GLOSSARY.md) | Every term used below, in plain language |
| [`docs/01_UNITYFS_FORMAT.md`](docs/01_UNITYFS_FORMAT.md) | The container format, field by field |
| [`docs/02_ENCRYPTION.md`](docs/02_ENCRYPTION.md) | The cipher, precisely specified |
| [`docs/03_WALKTHROUGH.md`](docs/03_WALKTHROUGH.md) | **The investigation, step by step, including failures** |
| [`docs/04_PITFALLS.md`](docs/04_PITFALLS.md) | 14 traps that cost real time |
| [`docs/05_MANIFEST.md`](docs/05_MANIFEST.md) | The manifest format and how keys are joined |
| [`docs/06_AUDIO.md`](docs/06_AUDIO.md) | AKPK / Wwise banks |
| [`docs/07_VIDEO.md`](docs/07_VIDEO.md) | CRI USM / Sofdec |
| [`docs/08_TOOLING.md`](docs/08_TOOLING.md) | Why each dependency is either written or borrowed |
| [`docs/09_VERIFICATION.md`](docs/09_VERIFICATION.md) | How correctness was proven, not assumed |
| [`docs/10_UI.md`](docs/10_UI.md) | The desktop interface, and seven pitfalls hit while building it |
| [`docs/11_BASE_KEY_SOLVER.md`](docs/11_BASE_KEY_SOLVER.md) | The solver as implemented, and four pitfalls hit while porting it |
