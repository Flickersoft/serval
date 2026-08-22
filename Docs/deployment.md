# Deploying the Server

**Start from [deploy/docker-compose.yml](../deploy/docker-compose.yml)** — the quickstart stack
(server + MongoDB + the go2rtc sidecar for WebRTC), configured through a sibling `.env`. This
document is the detail behind it: why the stack is shaped that way, and the levers that are not
in the quickstart. Two tuned real-hardware references live in
[deploy/examples/](../deploy/examples/README.md).

The image itself is built by [the Dockerfile](../Server/Serval.Server/Dockerfile) (ASP.NET
runtime + ffmpeg); [Server/docker-compose.yml](../Server/docker-compose.yml) is the developer
stack that builds it from the working tree.

For the edge module's own deployment, see [rk3588.md](rk3588.md).

## Docker

Two rules matter for this write-heavy, 24/7 workload and are baked into the compose file:

- **Named volumes** for the media directory and Mongo's data dir, so continuous segment writes
  never go through the container's overlay layer (slow + bloat). Size the video volume for
  `cameras × bitrate × RetentionDays`.
- **A memory limit per service** and a WiredTiger cache cap on Mongo, so neither balloons on a
  big host. .NET and Mongo both honour cgroup limits.

For **hardware encode** in the container, uncomment the `devices: [/dev/dri:/dev/dri]` mapping
(VAAPI, Intel/AMD) and set `Serval__Ingest__HwAccelDevice`; NVENC needs the NVIDIA container
runtime instead. Software encode needs neither — just more CPU.

For **GPU utilisation on the status page**, that mapping is enough on AMD. Intel publishes no
busy-percent file and reports usage through system-wide perf counters instead, so it also needs
`cap_add: [PERFMON]` — commented out in every compose file, and named on the page itself when it is
missing. See [configuration.md](configuration.md#vitals). It grants reading performance
counters for the whole machine, nothing else, and takes effect on `--force-recreate`.

The **go2rtc** service publishes port `8666` (WebRTC media) and is reached by the server on its
internal API port `1984`, which stays unpublished. The address browsers are told to reach the
media port on is `SERVAL_WEBRTC_CANDIDATES` in the quickstart's `.env` (the host's LAN IP;
comma-separate a second address for access from outside) — the compose file merges it into
go2rtc's config on the command line, so [go2rtc.yaml](../deploy/go2rtc.yaml) itself never needs
editing. See [live-view.md](live-view.md).

```bash
cd Server && docker compose up --build
```

**The build context is the repo root**, not the Server directory — the project references
`Shared/*`, which a context rooted there cannot see. Compose handles that; building by hand
means running from the root:

```bash
docker build -f Server/Serval.Server/Dockerfile -t serval-server .
```

The image is built with the **Vulkan** llama.cpp backend by default so the vision model can
offload to a GPU (see [below](#server-side-ai-on-a-gpu)). `--build-arg LLAMA_BACKEND=Cpu` builds
the CPU-only variant; the local compose file does this, since a dev box does not need the extra
~250 MB of Vulkan drivers.

## Versions and image tags

Which tag a deployment pins to is the whole update policy:

| Tag | Moves | Pin to it when |
| --- | --- | --- |
| `latest` | to each new release | you want the newest release, unattended |
| `0.4` | within a minor | you want fixes without the shape changing under you |
| `0.4.2` | never | you want this exact release and nothing else |
| `edge` | every push to `main` | you want head and will notice if it breaks |
| `sha-abc1234` | never | you are rolling back to a build you have already run |

A release is a git tag and nothing else. The tag names the version, so any version is available —
patch, minor or major:

```bash
git tag v0.4.2 && git push origin v0.4.2
```

[releasing.md](releasing.md) is the maintainer's side of that line: the seven steps from a merged pull
request to a published release, and the two rules that keep it from going wrong.

A push to `main` is not a release. It publishes `edge` and its `sha-` tag, consumes no version
number, and stamps the last released version into the assembly — the revision is what tells two
`edge` builds apart. A build made outside the workflow reports `0.0.0-dev`, because it has no tags
to describe against and inventing a number would collide with a real one.

`GET /api/system/version` reads it back off the running assembly:

```console
$ curl -s -H "Authorization: Bearer $TOKEN" http://<host>:8080/api/system/version
{"version":"0.4.2","revision":"abc1234…"}
```

The App shows the same pair under *Source* in the icon rail — the version to quote, the commit
because that is what AGPL section 13 actually offers.

## Deploying to a server

[deploy/examples/docker-compose.amd-gpu.yml](../deploy/examples/docker-compose.amd-gpu.yml) is a
real deployed stack (TrueNAS SCALE Apps, AMD iGPU), as opposed to the dev one above. It differs
in the ways the host forces:

- **No `build:`** — TrueNAS Apps only runs pre-built images, so the server image comes from GHCR,
  pushed by [.github/workflows/server-image.yml](../.github/workflows/server-image.yml). On
  `:latest` that is each new release; switch the tag to `:edge` to follow `main` instead.
  `pull_policy: always` makes stop→start in the Apps UI the update loop.
- **Bind mounts on ZFS datasets** rather than named volumes, so recordings and the database sit
  on the pool where they are visible and snapshottable.
- **Model weights mounted at `/app/models`**, never baked into the image — 3.5 GB that changes
  almost never has no business in a layer that is re-pulled on every code change. The built-in
  model paths are relative and resolve against the content root (`WORKDIR /app`), so the mount
  point is all the configuration needed. Populate it with:

  ```bash
  MODEL_DIR=/mnt/<pool>/serval/models ./scripts/fetch-models.sh
  ```

Once it is up, `http://<host>:8080/` is the App — sign in and add cameras under
*Settings → Cameras*.

## Enabling the AI

A fresh deployment records, replays and serves live view with no model files at all. Every AI
capability is opt-in, each behind its own switch with `Serval__ServerAi__Enabled` as the master —
and each loads only if its weights are present, so the order is: get the files, flip the
switches, recreate the container.

**1. Fetch the models** into the directory mounted at `/app/models`. From `deploy/`, without
even a repo clone:

```bash
docker compose --profile setup run --rm fetch-models
```

(or `MODEL_DIR=./models ../scripts/fetch-models.sh` from a checkout). What that downloads, and
what each file costs on disk:

| Weights | Capability | Switch (`Serval__…`) | Disk |
|---|---|---|---|
| SenseVoice | transcription + emotion + audio events | on with `ServerAi__Enabled` | ~1.1 GB |
| Silero VAD | speech gating for the above | (always, copied from the repo/image) | 2.3 MB |
| Qwen3-VL-2B + mmproj | scene descriptions, clip summaries | `Ai__Vision__Enabled` | ~2.3 GB |
| speaker embedding + pyannote | speaker labels + diarization | `Ai__Speaker__Enabled` | ~35 MB |
| zipformer audio tagging | sound events (glass, alarms, dogs…) | `Ai__Sound__Enabled` | ~26 MB |

`SKIP_VISION=1` (and `SKIP_SPEAKER`/`SKIP_SOUND`) trim the download for partial installs — a
detection-only deployment does not need the 2.3 GB VLM.

**2. Export the object detector** — the one set of weights nothing downloads for you
([why below](#the-object-detector)):

```bash
docker compose --profile setup run --rm export-detector
```

(or `MODEL_DIR=./models ../scripts/export-detector.sh`). Writes `detect/model.onnx` +
`detect/labels.txt`, ~10 MB. Switch: `Ai__Detection__Enabled`.

Both one-shots run as root — the server image has no unprivileged user — and each gives the
models directory back to whoever owns it before exiting, including when it fails part-way, so the
weights stay deletable by the person who fetched them. The one case with no ownership to copy is
a deployment with no clone at all, where Docker created `./models` as root: set
`MODEL_UID`/`MODEL_GID` in `.env` (your `id -u`/`id -g`). Running either script natively rather
than through compose already writes as you, and the handback is skipped.

**3. Flip the switches** — in the quickstart compose they are a commented block — and recreate:

```bash
docker compose up -d --force-recreate server
```

**What to expect on CPU.** Detection at `onnx-cpu` costs ~44 ms/frame at two threads on a
desktop core — fine for a handful of cameras at 1 fps, but an N100-class box running many
cameras wants an accelerator: measured ~96 inferences/s on two USB Corals against an estimated
10–13/s on the N100's four cores (see [coral.md](coral.md)). Scene description on CPU takes tens
of seconds per description on small hosts; a GPU brings it down ([below](#server-side-ai-on-a-gpu)).
Budget ~8–10 GB of container memory with everything on, against ~2 GB for recording only.

## Secrets

The Server refuses to start while `Serval__Auth__SigningKey` or `Serval__Auth__BootstrapAdminPassword`
still reads `CHANGE-ME`. That is deliberate, and it is not only about weak values: this repository is
public, so anything shipped in a compose file here is a value an attacker already has. The signing
key is the sharp one — it is the HMAC key for every access and stream token, so someone who knows it
mints an Admin token directly and never touches `/api/auth/login`, where the rate limiter and the
account lockout live.

```bash
openssl rand -base64 32
```

`Serval__ApiKey` is the third secret and behaves differently: it is the shared secret a CameraModule
presents when POSTing telemetry, and leaving it unset **closes** that route rather than opening it.
A Server with no edge modules needs no key and is unaffected; the boot log says so once, so a module
retrying against a 401 is diagnosable from the other end of the wire.

The [Google Home integration](google-home.md) adds two more of that kind —
`Serval__GoogleHome__ClientId` and `__ClientSecret` — and they follow the same rule: unset closes
the integration rather than opening it. **The client id is a secret here** even though OAuth
usually treats it as public, because it is the only thing that decides whose Google account may
link to this server. Both are values you generate; neither comes from Google. The one that does —
the HomeGraph service-account key — is a **file**, bind-mounted read-only and named by
`Serval__GoogleHome__HomeGraphKeyPath`, so it never passes through configuration or the API at all.
It is a live Google credential: keep it wherever you keep the three above.

Keep all three out of version control on any machine that is not your own. Compose reads a sibling
`.env`, so this is enough:

```yaml
Serval__Auth__SigningKey: ${SERVAL_SIGNING_KEY}
```

Rotating the signing key signs everyone out — existing tokens no longer verify — which is also the
way to end every session at once if you ever need to.

## TLS and exposure

**Serval is built for a trusted LAN, and nothing here is safe to put directly on the internet.**
The Server speaks plain HTTP (`ASPNETCORE_URLS=http://+:8080`), and three of its defaults assume a
network where everything on it is already trusted:

| Default | What it means off-LAN |
|---|---|
| No TLS | Passwords, bearer tokens and `?stream_token=` cross the wire in clear |
| `Serval:Cors:AllowedOrigins` empty | Any web page a viewer visits can call the anonymous routes |
| `Serval:OpenApi:Enabled` true | `/scalar/v1` publishes the whole API surface without a login |

Reaching it from outside means one of two things, and a VPN — Tailscale, WireGuard — is the simpler
one, because it leaves every default above correct rather than needing each to be changed.

The other is a reverse proxy terminating TLS. With Caddy that is two lines, and it obtains the
certificate itself:

```caddyfile
serval.example.com {
    reverse_proxy localhost:8080
}
```

Behind either, set `Serval__Cors__AllowedOrigins` to the App's real origin and
`Serval__OpenApi__Enabled=false`. Do not publish port `8666` (go2rtc's WebRTC media) or `1984`
(its API — unauthenticated, and it holds every camera's RTSP credentials).

Two things follow from having TLS that are easy to attribute to the wrong cause:

- **Talk-back only works over HTTPS.** Browsers gate microphone access on a secure context, so on
  plain HTTP the button is there and the audio silently never arrives.
- **Push notifications need HTTPS too, and there is no degraded mode.** Service workers and
  `PushManager` are both withheld outside a secure context, so on plain HTTP there is nothing to
  register and no subscription to make. Unlike the two above this one says so: *Settings →
  Notifications* draws its switch dead and explains why, rather than offering something that cannot
  work. `localhost` counts as secure, so development never sees it.
- **The App stores its session in plain `localStorage` on plain HTTP.** `flutter_secure_storage`
  refuses to work outside a secure context, and the fallback holds a refresh token good for 30 days.

Note that a VPN alone does **not** fix the first two: a Tailscale or WireGuard address over plain
HTTP is not a secure context either. Only real TLS is — which for a LAN name means either a reverse
proxy with a certificate, or something like `tailscale serve`, which terminates TLS with a valid
`*.ts.net` certificate without any DNS or port-forwarding of your own.

Push has one further consequence worth stating: it is one of only two features that make the Server
reach the public internet on its own initiative, since a browser's push service is Google's,
Mozilla's or Apple's. What crosses that boundary is ciphertext encrypted to the subscribing browser
— the relay cannot read the alert text, the camera name, or the token in it. A deployment with no
outbound internet access simply gets no notifications; nothing else is affected.

The other is [Google Home](google-home.md), and it is the only thing here that needs the Server to
be reachable *inbound* from the internet. It is off by default. Turning it on makes the two
defaults above stop being optional — set `Serval__Cors__AllowedOrigins` and
`Serval__OpenApi__Enabled=false` — and the recommended shape publishes only the four `/api/google/*`
routes while everything else stays on the LAN. No video reaches Google either way — it only sets
the connection up — and on the displays it streams live to, the picture goes directly from go2rtc
over the LAN without touching that boundary at all. The one case that does cross it is a Cast
device Google will not stream live to, which fetches video from the published address instead; see
[google-home.md](google-home.md#what-leaves-your-network).

## The object detector

Two files, mounted alongside the other weights and pointed at by
`Serval__Ai__Detection__ModelPath` and `__LabelsPath`. Neither ships in the image and neither is
downloaded for you — export both with [`scripts/export-detector.sh`](../scripts/export-detector.sh)
(or `docker compose --profile setup run --rm export-detector` from `deploy/`), and see the note in
[`fetch-models.sh`](../scripts/fetch-models.sh). Missing either
one is a startup *warning* and a fall back to the motion gate, never a failure to start; a model
that is present but is not the end-to-end head is fatal, because there is nothing useful to decode
it into. Note that a labels file disagreeing with the weights is *not* caught — that head declares
no class count to check one against, so it stays a live hazard rather than a startup error.

**`Serval__Ai__Detection__Device` selects what runs it**, as one `runtime-device` name: `onnx-cpu`
(the default), `onnx-cuda`, `onnx-openvino`, `onnx-tensorrt` or `tflite-edgetpu` (Coral, x86 only).

One setting rather than two, because the two axes are mutually exclusive in practice — `tflite-edgetpu`
crossed with `cuda` means nothing. Internally the runtimes are still sibling implementations of
`IObjectDetector`, each with its own compiler and model format, and an Edge TPU is not an ONNX Runtime
execution provider; but `ObjectDetectorFactory` maps the one name onto both, so nothing asks an
operator to say it twice. A Hailo or RKNN part would arrive as another prefix.

The runtime half also decides how `Detection:ModelPath` is read — as ONNX weights or as the
`edgetpu_compiler` output. **One path for both**, with the file checked against the device at startup
by its header rather than its extension, since pointing it at the wrong family is the mistake sharing
a path makes possible. The labels file is not interchangeable either: a compiled Coral model usually
carries a different vocabulary from the ONNX weights.

Measured on two USB Corals: **95.8 inferences a second** against an estimated 10–13/s for the same
host's four cores. Deployment, bring-up and failure modes are in [coral.md](coral.md); the reasoning is
under [Coral / EdgeTPU](detection.md#coral--edgetpu--built-and-measured). **Plan for CPU when sizing a
host that has no device.**

**It runs on the CPU, and that is a property of the image rather than a choice in configuration.**
The detector uses the `Microsoft.ML.OnnxRuntime.Managed` package — the managed P/Invoke surface with
no native of its own — and binds to the ONNX Runtime that sherpa-onnx already publishes. This is
load-bearing: the full package would put a second `libonnxruntime.so` at the same publish-relative
path as sherpa's, and the SDK resolves that collision silently, in either package's favour depending
on ordering, with no error at any verbosity. Losing that coin toss rebinds the entire ASR path onto
a different runtime build. Referencing Managed alone means exactly one native ships.

The consequence is that of the four `onnx-*` devices, only `onnx-cpu` can run out of the box: sherpa's
build carries no other provider. A GPU one means building ONNX Runtime with it, installing that `.so`
on the image, and setting `Detection:NativeLibraryPath` — not a different NuGet package.

**The settings page greys out whichever devices this image and host cannot deliver**, asking
`OrtEnv.GetAvailableProviders()` and the Coral enumeration rather than assuming, so the answer is
right on an image built with more than the default. The value stays writable through configuration
either way, so a config backup restores onto a host with different hardware; an unavailable one that
is nonetheless set shows as the selection with its reason attached, since that is a deployment whose
choice is being ignored and there is nowhere else to see it.

Detection runs on raw frames ffmpeg has already scaled — no JPEG decode and no resize on the .NET
side; the buffer is cropped and converted straight into the shape the detector declared. The
`camera-module-tools` diagnostics (`--detect`, `--replay-gates`) read stills from disk and decode
them through the same `FramePreparer` path, so their scores match a deployment's.

Measured on a Ryzen 9 7950X3D, against real 4K driveway footage and YOLO26n:

| | 1280x720 frames | 1920x1080 frames |
|---|---|---|
| crop, convert and letterbox | 2.9 ms | 3.6 ms |
| inference at 640² | 26-33 ms | 26-33 ms |
| **per whole frame** | **~36 ms** | **~30 ms** |

Note the inference figure does not move with frame size: it is fixed by the model's input shape,
which is why raising the frame width costs so little — and why raising it buys nothing at all
unless regions are cropping (see
[detection.md](detection.md#what-this-is-actually-worth-measured)).

**Export the model at the detect stream's aspect ratio.** A square input spends 44% of a 16:9 frame's
inference on mid-grey padding: 640×384 costs 16.3 ms where 640×640 costs 24.1 ms, for the same
detections. Across ten cameras at the default 2 fps that is a substantial share of an N100, on a host
that has none to spare — see [detection.md](detection.md#coral--edgetpu--built-and-measured) for
what four cores actually manage. The export flag and the
measurements are in
[detection.md](detection.md#export-with-dynamic-axes-and-let-each-camera-pick-its-own-shape).

The figure to check on a given host is the per-frame median in the detector's own debug log.

Two settings govern the frames themselves, and they are separate from the wall's snapshot rate on
purpose — the dashboard wants a picture a second, detection wants temporal resolution:

- `Ingest:DetectFps` (default `1`) — frames a second handed to detection. Cheap on the camera side,
  because those frames are already being decoded to produce the snapshot; what it multiplies is how
  much inference is asked for. Zero turns detection frames off entirely.
- `Ingest:DetectFrameWidth` (default `1920`) — how wide those frames are. Not the detector's input
  size: it is the ceiling on how much detail a distant subject still has when a crop is taken. A cap,
  so a smaller source is left alone.

**The detect *stream* is usually the real constraint, not this setting.** A camera pointed at its
640x360 sub stream cannot be improved by any value here — the detail is gone before Serval sees the
frame. And because a whole frame is squeezed into the model's input either way, a larger stream only
pays for a deployment that also crops. If distant subjects are being missed, raise the camera's
detect stream *and* leave `Detection:Regions:Mode` at `auto`; raising either alone does nothing. The
measurements behind that are in [detection.md](detection.md#what-this-is-actually-worth-measured).

Raising the stream also raises the *honest* magnification available, which is what a crop can actually
deliver: `Detection:Regions:MaxRegionScale` holds a crop at native scale, so the ceiling on a crop's
magnification is exactly the frame-to-input gain, and a bigger stream is the only thing that moves it.

`Ingest:DetectFrameDir` (default `/dev/shm/serval/detect`) is where they are staged, and **it must be
tmpfs**. Frames are written and deleted within milliseconds; on a real filesystem that is continuous
churn on the device holding the recordings, for no benefit. Both compose files mount it; the Server
logs a warning at startup if the path is not in memory. Only a few frames per camera are ever
resident, so the mount is a bound against a stalled reader rather than a working set.

## Server-side AI on a GPU

Two settings, and they move independently:

- `Ai:Vision:GpuLayers` — how many transformer layers llama.cpp offloads. Any value at or above the
  model's layer count offloads all of it; llama.cpp clamps.
- `Ai:Vision:ProjectorOnGpu` — whether the mmproj image encoder goes to the GPU.

**The Server ships `99` and `true`** — full offload — on the measurements below. The CameraModule
ships neither, so an edge device keeps the all-CPU path; a Pi has no GPU to offload to. Set
`GpuLayers` to `0` on a Server host without a usable device only if you want to skip the fallback:
leaving it at `99` there is harmless, just pointless.

Offloading the layers while leaving the projector on the CPU is the worst of both — the GPU's
memory gets spent and the slow part still runs on the CPU — so **if you set `GpuLayers`, set
`ProjectorOnGpu` too**. They are separate because the reverse pairing is useful: the projector is a
fraction of the model's size, so it fits a UMA carveout the model itself would overflow.

This does nothing on a CPU-backend build. On a Vulkan build without a usable device it is also
harmless: `LLamaSharp.Backend.Vulkan.Linux` depends on `.Backend.Cpu`, so llama.cpp falls back to
the CPU rather than failing to start.

### Whether it is faster is a property of the hardware, not the model

On a discrete card with its own VRAM it is a large win. On an APU the iGPU shares the CPU's memory
bus, so the two halves of a description move in opposite directions — and the description log
reports them separately for exactly this reason:

```
Described 2 frame(s) in 10.8s (setup 0.1s, first token 3.6s, 66 tok in 7.1s = 9.4 tok/s)
```

**Time to first token** is image encode plus prefill. It is compute-bound, and an iGPU wins it.
**tok/s** is generation. It is memory-bandwidth-bound, and an iGPU does not win it — offloaded
weights that spill out of VRAM into GTT are read back over the same DDR bus the CPU was already
using, so this figure gets *worse*.

Measured on a Ryzen 5000-series APU (Cezanne Vega, 512 MB VRAM carveout, 24.9 GB GTT), 16 cores,
`NumThreads=8`, Qwen3-VL-2B Q8_0, two frames per description:

| `GpuLayers` / `ProjectorOnGpu` | first token | tok/s | total | peak container CPU |
|---|---|---|---|---|
| `0` / `false` — all CPU | 7.07s | 11.86 | 12.4s | ~848% |
| `0` / `true` — projector only | 5.99s | 11.43 | 11.7s | 765% |
| **`99` / `true` — everything** | **3.56s** | 9.38 | **10.8s** | **58%** |

Generation did degrade on GTT exactly as the bandwidth argument predicts, by 21%. It did not
matter: prefill halved, and prefill is the larger share of a *vision* description, so total
wall-clock still improved. The decisive number is the last column — **peak CPU fell roughly 14×**,
because the work left the CPU entirely.

So the guidance for an APU is not "measure, it might be a wash." For a VLM it is: **offload
everything, and judge it on CPU rather than on tok/s.** The wash applies to pure text generation,
which is all bandwidth.

### Confirming it actually bound

llama.cpp will fall back to the CPU silently, which looks identical to "the GPU didn't help." Two
checks:

```bash
docker logs <server> 2>&1 | grep -E "CLIP using|compute buffer size"
#   clip_ctx: CLIP using Vulkan0 backend
#   alloc_compute_meta:    Vulkan0 compute buffer size =   355.55 MiB
```

That line only covers the projector — LLamaSharp routes the language model's own load logging
through its logger, where it does not surface at Information. To confirm the *layers* moved, read
the driver instead:

```bash
cat /sys/class/drm/card0/device/mem_info_vram_used   # 463 MB of 512 MB
cat /sys/class/drm/card0/device/mem_info_gtt_used    # 2.55 GB — the model, spilled
```

Idle is ~17 MB VRAM and ~0 GTT, so the jump is unambiguous. `vulkaninfo --summary` inside the
container confirms the device is visible at all.

Encode and inference do not contend for silicon: VAAPI encode runs on the dedicated video block,
Vulkan inference on the shader cores. They *do* contend for memory bandwidth and APU power budget,
and anything else on the host sharing `/dev/dri` — a Jellyfin transcode, say — contends for the
shader cores directly.

## Logs

The app writes **no log file**. Everything goes to stdout, and the Docker daemon owns rotation and
retention — a second, app-written rolling file would only duplicate every line and double the disk
writes on a host whose disk is already sized for video.

```bash
docker compose logs -f server            # follow
docker compose logs --since 1h server    # recent history
docker compose logs --no-color --since 24h server > serval.log   # a file to hand off
```

Each service is capped at `max-size: 10m` × `max-file: 5` (50 MB) in
[docker-compose.yml](../Server/docker-compose.yml). Without that cap Docker's default `json-file`
driver grows forever, which on this workload eventually fills the same disk the recordings live on.

Verbosity and JSON output are two environment variables — see
[configuration.md](configuration.md#logging-both-hosts).

Log records carry a **scope** rather than a category per camera: ingest and audio-detection lines
are all under `Serval.Server.Ingest.Session` / `Serval.Server.Ai.CameraAudioDetector` with
`CameraId` attached. That way a single `Logging:LogLevel` rule can target them, and the camera id
becomes a queryable field instead of unbounded label churn when the JSON formatter is switched on.
