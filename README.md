<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Icons/serval-purple-dark-bg.svg">
    <img src="Icons/serval-purple-light-bg.svg" alt="Serval" width="96">
  </picture>
</p>

# Serval

**A self-hosted NVR with on-device AI: 24/7 recording, sub-second live view, and object, scene
and speech understanding that never leaves your network.**

[![License: AGPL-3.0-or-later](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue)](LICENSE)

![The camera wall: six live cameras, the timeline, and the activity feed](Docs/media/wall.webp)

Cameras produce video and AI-derived information — what is in frame, what is happening, who is
speaking, what is being said — and Serval keeps all of it **local**. Every model runs in-process
on your own hardware, with no cloud service and no external model server in the runtime path.

## What it does

- **24/7 recording** — one ffmpeg per camera writes HLS fMP4 segments that are both the
  recording and the live stream. Video is stored exactly as the camera sends it (no re-encode
  unless you ask), with age-based retention per camera.
- **Replay everywhere** — scrub any camera back through the day on a timeline with coverage and
  event marks, or replay the *entire wall* in sync. Export any range as a standalone MP4.
- **Live view** — a snapshot wall of every camera over one WebSocket, and a sub-second WebRTC
  view when you open one, with two-way talk-back and ONVIF pan/tilt/zoom where the camera
  supports them.
- **Object detection** *(optional)* — episodes with tracking (a person crossing the yard is one
  event, not 40 frames), detection zones and masks, per-class thresholds and per-camera tuning.
- **Scene descriptions** *(optional)* — a compact vision-language model watches consecutive
  frames and writes what is *happening*, searchable in the activity feed; saved clips get an
  automatic summary.
- **Audio understanding** *(optional)* — speech-to-text with emotion, speaker labels and
  after-the-fact diarization, plus 527-class sound tagging: breaking glass, alarms, a barking
  dog.
- **Alerts** — the detections you asked to be told about, each with a preview clip and the frame
  it fired on, delivered as web push notifications if you want them.
- **Saved clips** — independent MP4s with a frozen snapshot of that moment's telemetry, out of
  retention's reach.
- **The rest of an NVR** — users and roles, per-camera settings with help text in the UI,
  configuration backup/restore, server vitals with GPU/accelerator load, an installable
  responsive web app served by the same container as the API.

Every AI capability is **off until you add its model files** — a fresh install is a fast,
dependable recorder first. And throughout, a capability either runs a real model or reports
nothing: where the design asks for something no endpoint can supply, the UI says so rather than
rendering a plausible number.

| | | |
|---|---|---|
| ![Alerts, each with a preview clip and the frame it fired on](Docs/media/alerts.webp) | ![Replay with the timeline and scene descriptions](Docs/media/camera-replay.webp) | ![Saved clips with automatic summaries](Docs/media/clips.webp) |
| ![Server settings, every key with its help text](Docs/media/settings.webp) | ![Server status and vitals](Docs/media/server-status.webp) | ![The wall on a phone](Docs/media/phone-wall.webp) |

## Why Serval

- **Everything local.** No accounts, no cloud inference, no phoning home. The AGPL and your
  firewall are the whole privacy policy.
- **One coherent stack.** A single container serves the API and the UI from the same origin;
  MongoDB and go2rtc ride alongside in one compose file. No plugin matrix, no separate frontend
  host, no CORS.
- **Honest engineering.** The docs record what was measured, not what was hoped — and the UI
  never invents data. If you want the most mature ecosystem, Frigate exists; Serval trades
  breadth for audio/scene understanding and a stack you can hold in your head.

## Quickstart

You need Docker with Compose on an x86-64 host, and cameras that speak RTSP (or HTTP-FLV, RTMP,
SRT). Linux is what is tested, and what the optional GPU and Coral acceleration require — see
[Hardware support](#hardware-support). Two minutes to a recording NVR:

```bash
git clone --depth 1 https://github.com/Flickersoft/Serval.git
cd Serval/deploy
cp .env.example .env
# fill in the two secrets in .env — each has its generation command next to it
docker compose up -d
```

(No clone wanted? The three files `docker-compose.yml`, `.env.example` and `go2rtc.yaml` from
[deploy/](deploy/) are all it needs.)

Open `http://<host>:8080/`, sign in with the admin account from `.env`, and add your first
camera under **Settings → Cameras → Add camera**: give it a name and its stream URL
(`rtsp://user:pass@camera-ip/...`), and tick which roles the stream carries — record, detect,
live. There is no auto-discovery; you paste the URL, and ingest starts within seconds, no
restart. For sub-second live view, set `SERVAL_WEBRTC_CANDIDATES` in `.env` to your host's LAN
IP.

> **Serval is built for a trusted LAN.** It serves plain HTTP, and out of the box it allows any
> browser origin and publishes its API documentation without a login. Do not put it directly on
> the internet — put a reverse proxy with TLS in front of it, or reach it over a VPN, and read
> the hardening notes in [Docs/deployment.md](Docs/deployment.md#tls-and-exposure) first.

**No camera yet?** A camera's URL may be a local video file path — ffmpeg loops it in realtime
as a stand-in, and the entire pipeline runs against it. See
[Docs/testing.md](Docs/testing.md#a-camera-with-no-camera).

## Enabling the AI

Two one-shot commands from the `deploy/` directory — the first downloads the speech, sound,
speaker and scene models (~3.5 GB; `SKIP_VISION=1` skips the 2.3 GB scene model), the second
exports the object-detection model with its matching labels:

```bash
docker compose --profile setup run --rm fetch-models
```

```bash
docker compose --profile setup run --rm export-detector
```

Then uncomment the clearly-marked AI block in `docker-compose.yml`, raise the memory limit as
its comment says, and `docker compose up -d --force-recreate server`. Detection boxes, scene
descriptions and transcripts appear in the activity feed as they happen. The full story — what
each model costs on disk and CPU, and what to expect from small hosts — is in
[Docs/deployment.md](Docs/deployment.md#enabling-the-ai).

## Hardware support

**Works today, tested:**

| | |
|---|---|
| Server | x86-64 Linux (the published image is `linux/amd64`; other hosts below) |
| Object detection | Any CPU via ONNX Runtime (the default), or **Coral USB Edge TPU** — the runtime ships in the image; ~96 inferences/s measured on two Corals vs ~10–13/s on an N100's four cores |
| Video encode | VAAPI on Intel and AMD GPUs (drivers in the image; only used when a stream asks to be transcoded — recording is stream-copy) |
| Scene model | CPU, or GPU offload via Vulkan (Intel/AMD) |
| Sizing anchor | Designed for ~10 cameras on an Intel N100 with Corals for detection |

**Other hosts.** The Server is a Linux container, so what it strictly needs is a Docker that runs
Linux containers on x86-64 — a recording-and-replay stack should come up unchanged under Docker
Desktop on Windows or an Intel Mac. Acceleration is where Linux stops being optional: Docker
Desktop runs containers inside a VM that cannot see the host's devices, so there is no `/dev/dri`
for VAAPI or Vulkan and no `/dev/bus/usb` for a Coral. Two quieter consequences — the status
page's whole-machine CPU and load come from a procfs describing that VM rather than the machine
you are sitting at, and a few hundred gigabytes a day of segment writes land inside the VM's
virtual disk. **arm64 is not supported at all**, Apple Silicon included: the image is
`linux/amd64`, and emulating ffmpeg, ONNX Runtime and llama.cpp is not a working NVR.

**Wired in code, not packaged:** CUDA, OpenVINO and TensorRT detection backends exist behind
one setting, but the shipped image binds a CPU-only ONNX Runtime — using them means supplying
your own `libonnxruntime.so` build. NVENC encode works via a manual encoder setting with the
NVIDIA container runtime, untested.

**On the roadmap:** an arm64 server image; the RK3588 edge module
([Docs/rk3588.md](Docs/rk3588.md)) contributes audio/scene telemetry today and its video relay
is not built yet.

Tuned reference deployments — a TrueNAS SCALE + AMD iGPU box and an N100 + dual-Coral box, with
the measurements that justify every number — are in [deploy/examples/](deploy/examples/README.md).

## Storage and sizing

Recordings dominate: budget **cameras × bitrate × retention days** for the media volume, and
size retention to the disk. As anchors from real deployments: six cameras recording 4K main
streams write roughly 300–400 GB a day — seven days needs about 2.5 TB; sub-stream-only
recording is orders of magnitude less. MongoDB stays small but wants IOPS — on a multi-disk
host put it on the fast disk and the media on the big one. Memory: ~2 GB recording-only, 8–10 GB
with the full AI stack loaded.

## Security model

Serval assumes a **trusted LAN** and degrades explicitly outside one. Secrets have no defaults —
the server refuses to boot with a placeholder signing key or admin password, so a copied compose
file cannot become somebody's open deployment. Camera credentials, however, are stored in clear
in MongoDB today ([Server README — not done yet](Server/Serval.Server/README.md#not-done-yet)).
For remote access, terminate TLS at your own reverse proxy or use a VPN; browsers additionally
gate talk-back, push notifications and encrypted session storage behind HTTPS, and the app tells
you so where each is affected. WebRTC beyond the LAN needs a TURN server, which is not wired yet.

## Architecture

```mermaid
flowchart LR
    CAM["IP cameras<br/>RTSP · ONVIF"] -->|always-on ffmpeg| SRV["Serval Server<br/>records · indexes · runs the AI"]
    SRV --- META[("MongoDB<br/>telemetry · settings")]
    SRV --- REC[("recordings<br/>HLS fMP4")]
    SRV ==>|"same origin: API + web app"| APP["Your browser"]
    APP <-->|sub-second WebRTC| G2["go2rtc sidecar"]
    G2 -.->|on demand| CAM
```

One always-on ffmpeg per camera writes segments that serve recording, live and replay alike;
detection reads frames from the same decode; go2rtc pulls a camera only while someone is
watching. An optional edge module (Orange Pi/RK3588) runs the same AI library next to a local
camera and streams telemetry in. The full picture, including why streams carry explicit roles,
is in [Docs/architecture.md](Docs/architecture.md).

## Status

Serval is young and honest about it. The notable gaps, each tracked with its reasoning:
module cameras contribute AI but not video yet
([roadmap](CameraModule/Serval.CameraModule/README.md#roadmap)); clips are saved by hand, not
automatically on an alert; a backgrounded tab keeps its streams open
([Docs/live-view.md](Docs/live-view.md#not-yet-pausing-live-video-when-the-app-is-hidden));
a handful of designed UI elements have no backend source and say so
([Docs/app-notes.md](Docs/app-notes.md#what-still-has-no-backend-source)); an unreachable camera
is retried forever but not yet surfaced as unhealthy; and plain-HTTP deployments lose HTTPS-only
features silently rather than with a visible warning.

## Documentation

[Docs/](Docs/README.md) carries the long-form documentation — deployment, configuration,
detection and its measured tuning, recording, alerts, the Coral and RK3588 guides, and the
testing story (every suite runs on a fresh clone with no camera hardware).

## Contributing

Bug reports, documentation and code are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for
the issue-first workflow, the build/test commands, and the CLA that covers contributions.
Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

## License

Copyright (C) 2026 Flickersoft LLC. Serval is free software under the
**[GNU AGPL-3.0-or-later](LICENSE)**: run a modified version for others over a network and they
are entitled to its source — an offer the app itself makes, linking the exact commit it was
built from. The Serval name and logos are separate ([TRADEMARK.md](TRADEMARK.md)), and
[LICENSE-EXCEPTIONS.md](LICENSE-EXCEPTIONS.md) grants additional permissions for app-store
distribution. Contributions require the [CLA](CLA.md), which grants Flickersoft LLC the right
to sublicense contributed code, including under commercial terms. Third-party material that
travels with Serval — the vendored fonts and player, the image's native dependencies, and the
licences the downloaded models arrive under — is listed in
[THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md).
