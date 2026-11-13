# Serval documentation

The component READMEs cover how to build and run each part of the repository. The documents below
cover deployment, configuration, hardware acceleration, integrations, and internal architecture.

**Deploying?** Read in this order: [root README](../README.md) quickstart →
[deployment.md](deployment.md) (including [Enabling the AI](deployment.md#enabling-the-ai)) →
[configuration.md](configuration.md) → [coral.md](coral.md) or [rk3588.md](rk3588.md) if you
use that hardware → [google-home.md](google-home.md) or [mcp.md](mcp.md) if you want external
integrations over a public HTTPS endpoint.

| Document | Contents |
|---|---|
| [architecture.md](architecture.md) | Camera ingestion modes, video and telemetry data flow, explicit stream roles (`record`, `detect`, `live`), and `CameraModule` threading |
| [detection.md](detection.md) | Shared AI pipeline (`Serval.Ai`): motion and audio gates, adaptive cropping and tiling, tracking, alert rules, and model selection |
| [telemetry.md](telemetry.md) | Telemetry HTTP ingest contract, the six record schemas, and live vs. diarized speaker streams |
| [recording.md](recording.md) | Stream-copy HLS fMP4 recording, audio muxing, MP4 clip export, and hardware transcoding |
| [clips.md](clips.md) | Saved clips storage layout, segment-exact export ranges, background write jobs, automatic clip summaries, and permissions |
| [alerts.md](alerts.md) | Alert generation, rolling detect-stream preview buffer, alert filtering, and Web Push notifications |
| [live-view.md](live-view.md) | Low-latency WebRTC via `go2rtc`, two-way talk-back, digital zoom, and ONVIF PTZ |
| [configuration.md](configuration.md) | Configuration precedence tiers, array/list binding rules, environment-only keys, `CameraModule` settings, and JSON backup/restore |
| [deployment.md](deployment.md) | Docker Compose setup, image tags, enabling AI models, secrets, TLS/reverse proxy exposure, GPU offload, and container logs |
| [memory.md](memory.md) | Per-camera `ffmpeg` memory cost, Intel iGPU shared-memory (`shmem`) accounting, cgroup working-set metrics, and process leak checks |
| [rk3588.md](rk3588.md) | `Serval.CameraModule` deployment on Orange Pi 5 (RK3588) and RKLLM NPU vision offload |
| [coral.md](coral.md) | Object detection on USB Coral Edge TPUs (`tflite-edgetpu`), container device rules, YOLOv9/SSD model trade-offs, and troubleshooting |
| [google-home.md](google-home.md) | Google Home cloud-to-cloud integration for Nest Hub displays and custom Cast receiver setup for televisions |
| [mcp.md](mcp.md) | Read-only Model Context Protocol (`/mcp`) server, OAuth 2.1 linking, exposed endpoints, and available tools |
| [releasing.md](releasing.md) | Release tagging workflow, CI image publishing, and `compose.yaml` release asset rules |
| [testing.md](testing.md) | Unit/integration test suites, synthetic file cameras, and `Serval.CameraModule.Tools` CLI diagnostics |
| [browser-testing.md](browser-testing.md) | End-to-end browser testing against Flutter CanvasKit with Playwright, plus HTTP vs. HTTPS origin behavior |
| [app-notes.md](app-notes.md) | Flutter client (`serval_app`) architecture, routing, state management, aspect-ratio rules, and UI placeholders |
