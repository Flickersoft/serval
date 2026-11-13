<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Icons/serval-purple-dark-bg.svg">
    <img src="Icons/serval-purple-light-bg.svg" alt="Serval" width="96">
  </picture>
</p>

# Serval

**Serval is a self-hosted network video recorder (NVR) with optional local object detection, scene
descriptions, and speech transcription.**

[![License: AGPL-3.0-or-later](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue)](LICENSE)

![The camera dashboard: six live cameras, the timeline, and the activity feed](Docs/media/wall.webp)

Video recordings and AI inference stay on your host. Models run inside the server container
without cloud APIs or external inference servers.

## What it does

- **24/7 recording** — one `ffmpeg` process per camera writes HLS fMP4 segments used for both
  storage and playback. Video is stored as the camera sends it (stream-copy by default, with
  optional per-stream transcoding) and pruned by per-camera age retention.
- **Timeline playback** — scrub through recorded footage on a single camera with coverage and
  event markers, replay the multi-camera dashboard grid in sync, or export any range as a
  standalone MP4.
- **Live view** — a multi-camera dashboard (~1 fps snapshots) and a sub-second single-camera
  WebRTC live view, with two-way audio talk-back and ONVIF pan/tilt/zoom where supported by the
  camera.
- **Object detection** *(optional)* — tracks objects across frames into continuous episodes (so a
  person crossing a yard is stored as one event rather than dozens of frames), with polygon masks,
  class filters, and per-camera confidence thresholds.
- **Scene descriptions** *(optional)* — a local vision-language model summarizes consecutive
  frames into searchable text in the activity feed and generates summaries for saved clips.
- **Audio analysis** *(optional)* — speech-to-text with emotion labels and post-conversation
  speaker diarization, plus 527-class sound classification (such as breaking glass, alarms, or
  barking dogs).
- **Alerts** — configurable object and sound triggers, each saved with a short preview clip and
  detection frame, with optional Web Push browser notifications.
- **Saved clips** — exported MP4 clips stored outside the rolling camera directories so automatic
  retention pruning does not delete them, bundled with the detections, descriptions, and
  transcripts from that window.
- **MCP server for AI assistants** *(optional)* — a read-only [MCP](Docs/mcp.md) endpoint so
  clients like Claude, Gemini, or ChatGPT can query camera detections, transcripts, descriptions,
  and snapshots.
- **Administration** — role-based user accounts, configuration backup and restore, host
  CPU/GPU/disk metrics, and a responsive web UI served from the server container.

Every AI capability is disabled by default. Enable and download models under **Settings → AI
models**.

| | | |
|---|---|---|
| ![Alerts, each with a preview clip and the frame it fired on](Docs/media/alerts.webp) | ![Replay with the timeline and scene descriptions](Docs/media/camera-replay.webp) | ![Saved clips with automatic summaries](Docs/media/clips.webp) |
| ![Server settings](Docs/media/settings.webp) | ![Server status and vitals](Docs/media/server-status.webp) | ![The dashboard on a phone](Docs/media/phone-wall.webp) |

## Network and privacy

By default, Serval requires no external accounts and makes no outbound runtime requests. The
Compose stack runs three containers: the Serval server (API and web UI), MongoDB (configuration
and event metadata), and `go2rtc` (on-demand WebRTC media relay).

Model weights are downloaded once from GitHub and Hugging Face when you enable AI features. Three
optional integrations communicate outside your local network only if you configure them:

- **Web Push notifications** — sends encrypted alert payloads through your browser vendor's push
  relay (Google, Mozilla, or Apple).
- **[Google Home](Docs/google-home.md)** — exposes signaling endpoints so Nest Hub displays can
  open WebRTC streams over your LAN; video streams do not pass through Google.
- **[AI assistants (MCP)](Docs/mcp.md)** — sends queried transcripts, scene descriptions,
  detections, and still images to whichever MCP client/provider you connect.

Compared to broader open-source NVRs like Frigate, Serval supports fewer hardware platforms and
third-party integrations, focusing instead on built-in speech transcription, sound classification,
and vision-language scene descriptions.

## Quickstart

You need an x86-64 host running Docker with Compose, and IP cameras that serve RTSP (or HTTP-FLV,
RTMP, or SRT). Linux is what is tested, and is required for GPU and Coral Edge TPU acceleration —
see [Hardware support](#hardware-support). Recording uses ~2 GB of RAM, and the full AI stack uses
8–10 GB ([Storage and sizing](#storage-and-sizing)).

In an empty directory:

```bash
curl -fsSLO https://github.com/Flickersoft/serval/releases/latest/download/compose.yaml
docker compose up -d
```

1. Open `http://<host>:8080/` and create the initial **Admin** account.
2. Add a camera under **Settings → Cameras → Add camera**: enter a name, paste its stream URL
   (`rtsp://user:pass@camera-ip/...`), and select its stream roles:
   - `record` — writes video segments to disk for timeline playback and clip export.
   - `detect` — decodes ~1–2 fps frames for dashboard thumbnails and AI analysis.
   - `live` — serves low-latency WebRTC video when viewing a single camera.
   (Assign all three to a single stream, or put `detect` on a camera's lower-resolution
   sub-stream to reduce CPU usage.)
3. Optionally, enable AI features under **Settings → AI models** — see [Enabling the AI](#enabling-the-ai).

Optional overrides (default retention, memory limits, pinned image tags) can be configured in a
`.env` file next to `compose.yaml`; see [deploy/.env.example](deploy/.env.example) for available
variables.

> **Trusted LAN default.** Out of the box, Serval serves plain HTTP, allows cross-origin requests,
> and exposes its OpenAPI documentation without authentication. Do not expose port 8080 directly
> to the internet — use a VPN or put a TLS reverse proxy in front of it and follow the hardening
> steps in [Docs/deployment.md](Docs/deployment.md#tls-and-exposure).

**Testing without a camera:** A camera's stream URL can be a local video file path inside the
container; `ffmpeg` loops it in realtime for testing. See
[Docs/testing.md](Docs/testing.md#a-camera-with-no-camera).

## Enabling the AI

Open **Settings → AI models**, select the capabilities you want, click **Download `<size>` and turn
on** (or **Turn on** when already downloaded), and click **Restart now** when the download
completes:

| Model | Download | What it adds |
|---|---|---|
| Object detection (YOLO26n) | bundled in image | People, vehicles, and animals tracked into episodes |
| Speech (SenseVoice) | 1.0 GB | Speech-to-text transcripts with emotion labels |
| Speaker labels | 35 MB | Live speaker IDs and post-conversation diarization (requires Speech) |
| Sound events | 111 MB | 527 AudioSet classes (breaking glass, alarms, barking dogs) |
| Scene descriptions (Qwen3-VL 2B) | 2.3 GB | Short text descriptions of camera activity (~2.9 GB RAM loaded) |

After restarting, enable the desired AI capabilities per camera under **Settings → Cameras**.

To pre-download models from the command line instead of the browser, run
`docker compose --profile setup run --rm fetch-models` next to `compose.yaml` (set
`SKIP_VISION=1`, `SKIP_SPEECH=1`, `SKIP_SPEAKER=1`, `SKIP_SOUND=1`, or `SKIP_DETECTION=1` to
omit individual models), then enable them in Settings. Disk and CPU details are in
[Docs/deployment.md](Docs/deployment.md#enabling-the-ai).

## Hardware support

**Supported and tested:**

| Component | Supported hardware |
|---|---|
| Server | x86-64 Linux (`linux/amd64` container image; see notes on other hosts below) |
| Object detection | Any x86-64 CPU via ONNX Runtime (default), or **Coral USB Edge TPU** (runtime included in image; ~96 inferences/s on dual Corals vs ~10–13/s on an Intel N100 CPU) |
| Video transcoding | VAAPI on Intel and AMD GPUs (drivers included; used only when per-stream transcoding is enabled) |
| Scene model | CPU, or GPU offload via Vulkan (Intel and AMD GPUs) |
| Reference target | Designed for ~10 cameras on an Intel N100 mini-PC with USB Coral accelerators (measured with 7 cameras) |

**Windows and Intel macOS (Docker Desktop).** Recording, playback, and CPU inference should run in
Docker Desktop on x86-64 hosts (untested), but Docker Desktop's Linux VM cannot pass through PCIe
GPUs (`/dev/dri` for VAAPI/Vulkan) or Coral USB devices (`/dev/bus/usb`). Host metrics under
**Settings → Server status** will also reflect the Docker VM rather than the physical host, and
recordings will write into Docker Desktop's virtual disk unless bind-mounted appropriately.

**ARM64 (including Apple Silicon) is not supported** for the server container: only a
`linux/amd64` image is published.

**NVIDIA GPUs and OpenVINO.** Not supported out of the box in the standard image, which bundles
CPU-only ONNX Runtime and VAAPI drivers. Using CUDA, TensorRT, or OpenVINO detection requires
supplying a custom `libonnxruntime.so` build, and NVENC transcoding requires the NVIDIA container
runtime with a manual encoder setting (untested); see
[Docs/deployment.md](Docs/deployment.md#the-object-detector).

**Roadmap:** an `arm64` server image, and video streaming from the optional RK3588 edge companion
agent (`CameraModule`, [Docs/rk3588.md](Docs/rk3588.md)), which currently captures local audio and
video frames on an Orange Pi 5 for on-device AI events without relaying video to the server.

Reference Compose configurations for TrueNAS SCALE (AMD iGPU) and Intel N100 (dual Coral USB) are
in [deploy/examples/](deploy/examples/README.md).

## Storage and sizing

Disk usage is determined primarily by `cameras × bitrate × retention days`. For example, six
cameras recording 4K main streams write roughly 300–400 GB/day (~2.5 TB for 7 days); recording
sub-streams uses much less. MongoDB stays small but benefits from low-latency storage, so on
multi-disk hosts place MongoDB on SSD and recordings on bulk storage.

Approximate container memory usage, measured on an Intel N100 with seven cameras
([Docs/memory.md](Docs/memory.md)):

| Enabled features | Server memory | Notes |
|---|---|---|
| Recording, playback, live view | ~2 GB | Plus ~300 MB per camera when decoding detect frames |
| + object detection and audio models | between the two | Depending on camera count (omits the ~2.9 GB scene model) |
| + scene descriptions (full AI stack) | 8–10 GB | Qwen3-VL 2B uses ~2.9 GB (in system/shared RAM on integrated GPUs) |

## Security model

Serval is designed for a trusted LAN by default:

- **Secrets and bootstrap:** On first boot, the server generates a random JWT signing key and
  stores it in MongoDB. Whoever visits the web UI first creates the initial Admin account — finish
  this step before exposing the port to untrusted users, or set `SERVAL_ADMIN_PASSWORD` to create
  the Admin account automatically at startup.
- **Plaintext camera credentials:** Camera RTSP and ONVIF passwords are stored unencrypted in
  MongoDB so the server can connect unattended ([Server README](Server/Serval.Server/README.md#not-done-yet)).
- **HTTPS and remote access:** Use a VPN or a TLS-terminating reverse proxy for remote access.
  Over plain HTTP (except `localhost`), browsers block microphone access and Web Push APIs — the
  UI disables two-way talk-back and push notifications with an explanation — and session tokens
  fall back to unencrypted `localStorage`. WebRTC outside the LAN works over a VPN or by forwarding
  UDP/TCP port `8666`; TURN relay (for hosts behind CGNAT or networks blocking port `8666`) is not
  wired yet.

## Architecture

```mermaid
flowchart LR
    CAM["IP cameras<br/>RTSP · ONVIF"] -->|always-on ffmpeg| SRV["Serval Server<br/>records · indexes · runs AI"]
    SRV --- META[("MongoDB<br/>events · settings")]
    SRV --- REC[("recordings<br/>HLS fMP4")]
    SRV ==>|"same origin: API + web UI"| APP["Browser"]
    APP <-->|WebRTC| G2["go2rtc sidecar"]
    G2 -.->|on demand| CAM
```

One `ffmpeg` process per camera writes HLS segments for recording and timeline playback; object
detection and snapshots read decoded frames from the `detect` stream; `go2rtc` connects to a
camera only while a viewer has its live WebRTC view open. Optionally, a standalone edge worker
(`CameraModule`) runs the shared AI library on an RK3588 SBC attached to a local camera/microphone
and posts event records (`telemetry`) to the server. Full details are in
[Docs/architecture.md](Docs/architecture.md).

## Known limitations

- **Edge companion (`CameraModule`) video:** `CameraModule` nodes send AI transcripts and scene
  descriptions to the server, but RTSP video relay is not implemented yet
  ([CameraModule roadmap](CameraModule/Serval.CameraModule/README.md#roadmap)).
- **Manual saved clips:** Alert preview clips are kept for `AlertRetentionDays` (14 days by
  default), whereas permanent saved clips are created manually rather than automatically on
  alerts.
- **Backgrounded WebRTC tabs:** Hiding the browser tab pauses the multi-camera snapshot feed after
  10 seconds, but an open single-camera WebRTC stream stays connected in the background
  ([Docs/live-view.md](Docs/live-view.md#still-not-done-the-focused-webrtc-view)).
- **Missing stream metadata in UI:** Several UI fields (such as stream resolution/FPS badges and
  saved-clip byte estimates prior to export) are stubbed or omitted because the backend does not
  expose them yet ([Docs/app-notes.md](Docs/app-notes.md#ui-placeholders-and-omitted-fields)).
- **Silent ingest retries:** Unreachable cameras are retried indefinitely with backoff in the
  server logs, without a dedicated camera-health error indicator or alert.

## Documentation

See [Docs/README.md](Docs/README.md) for detailed documentation on deployment, configuration,
object and audio detection, recording, alerts, Coral Edge TPU and RK3588 setup, Google Home, MCP,
and running the test suites.

## Contributing

Bug reports, documentation, and code contributions are welcome. See
[CONTRIBUTING.md](CONTRIBUTING.md) for the issue workflow, build/test commands, and Contributor
License Agreement (CLA). Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

## License

Copyright (C) 2026 Flickersoft LLC. Serval is free software under the
**[GNU AGPL-3.0-or-later](LICENSE)**: if you run a modified version for others over a network,
they are entitled to receive its corresponding source (the web UI includes a *Source* link to the
repository and build commit for AGPL section 13 compliance). Trademarks are covered separately in
[TRADEMARK.md](TRADEMARK.md), and [LICENSE-EXCEPTIONS.md](LICENSE-EXCEPTIONS.md) grants additional
permissions for app-store distribution. Code contributions require signing the [CLA](CLA.md),
which grants Flickersoft LLC a license to sublicense contributed code, including under commercial
terms. Third-party licenses for bundled libraries, fonts, and downloadable AI models are listed in
[THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md).
