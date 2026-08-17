# Third-party licenses

Serval itself is AGPL-3.0-or-later ([LICENSE](LICENSE)). This file covers the third-party material
that travels with it: what is committed to this repository, what the server image carries, and what
the model downloads bring with them.

It is not a generated manifest. The authoritative version list for the dependency graph is the
`PackageReference` set in the `.csproj` files and `App/serval_app/pubspec.lock` — those move every
week under dependabot, and a table restating them would be wrong by the time it was read. What is
written out here is the material a reader cannot resolve from a manifest, because it is checked in
as a binary.

## Committed to this repository

Four third-party binaries are in git. Each is vendored for a stated reason rather than fetched, and
each keeps its notice next to it.

| Component | Version | License | Path |
|---|---|---|---|
| [Inter](https://github.com/rsms/inter) | 4.001 | OFL-1.1 | `App/serval_app/assets/fonts/Inter-{400,500,600}.ttf` |
| [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) | 2.211 | OFL-1.1 | `App/serval_app/assets/fonts/JetBrainsMono-{400,500}.ttf` |
| [hls.js](https://github.com/video-dev/hls.js) | 1.6.16, "light" build | Apache-2.0 | `App/serval_app/web/hls.js` |
| [Silero VAD](https://github.com/snakers4/silero-vad) | v5 | MIT | `CameraModule/Serval.CameraModule/models/silero_vad.onnx` |

- **The fonts** carry their full license text beside them, as
  [`OFL-Inter.txt`](App/serval_app/assets/fonts/OFL-Inter.txt) and
  [`OFL-JetBrainsMono.txt`](App/serval_app/assets/fonts/OFL-JetBrainsMono.txt) — copied verbatim
  from upstream, each retaining its own copyright line. The OFL requires the license to accompany
  the font software; that is what those two files are for, and neither is listed under `assets:` in
  `pubspec.yaml`, so they cost nothing in the build.
- **hls.js** carries its notice as a banner comment at the top of the file, which also records that
  this is the `hls.light.min.js` variant and how to update it.
- **Silero VAD** is checked in rather than downloaded so its version is pinned — see the comment at
  [`scripts/fetch-models.sh:37`](scripts/fetch-models.sh), which also explains the seed-copy order.

## Carried by the server image

The image is built from `mcr.microsoft.com/dotnet/aspnet:10.0` on Debian, and installs **ffmpeg**
(LGPL/GPL depending on build) plus the Mesa and Intel VAAPI drivers from Debian. The managed
dependencies that carry their own third-party payload are worth naming, because their licenses are
not the license of the NuGet package alone:

- **sherpa-onnx** (`org.k2fsa.sherpa.onnx`, Apache-2.0) — ships native ONNX Runtime, which the
  detector also binds. The two are pinned together for that reason; see the notes in
  [`.github/dependabot.yml`](.github/dependabot.yml).
- **ONNX Runtime** (`Microsoft.ML.OnnxRuntime.Managed`, MIT).
- **LLamaSharp** and its CPU/Vulkan backends (MIT) — which bundle **llama.cpp** (MIT).
- **MongoDB.Driver** (Apache-2.0), **SixLabors.ImageSharp** (Apache-2.0 under the Six Labors Split
  License), **SQLitePCLRaw** (Apache-2.0) with **SQLite** (public domain), **PortAudioSharp** with
  **PortAudio** (MIT), **Scalar.AspNetCore** (MIT).

The Flutter client's packages — Riverpod, go_router, fl_chart, flutter_webrtc, media_kit,
phosphor_icons and the rest — are resolved from `App/serval_app/pubspec.lock`; all are permissively
licensed (MIT/BSD/Apache-2.0). **Phosphor Icons** ships an icon font under MIT, and it is what half
the golden screenshots are drawn with.

The [`deploy/`](deploy/docker-compose.yml) stack also pulls two images Serval does not build:
**mongo:8** (MongoDB, SSPL) and **alexxit/go2rtc:1.9.14** (MIT).

## Model weights

**No model weights ship in this repository** beyond the Silero VAD file above, and none are baked
into the image — `scripts/fetch-models.sh` downloads them at setup time, and each arrives under its
own upstream license.

The one that changes what a deployment may do is the object detector, which is not downloaded for
you at all: [Docs/detection.md](Docs/detection.md#models) explains why, and states plainly that
the Ultralytics exports are AGPL-3.0 — "a licence some deployments cannot take". Read that before
choosing a detector.
