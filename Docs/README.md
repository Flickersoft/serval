# Serval documentation

The READMEs say what each part is and how to run it. These are the longer explanations behind
them — kept here so a cross-project topic is written once rather than repeated per project.

**Deploying?** Read in this order: the [root README](../README.md) quickstart →
[deployment.md](deployment.md) (including [Enabling the AI](deployment.md#enabling-the-ai)) →
[configuration.md](configuration.md) → [coral.md](coral.md) or [rk3588.md](rk3588.md) if you
have that hardware. Everything else below is the engineering detail behind the system.

| Document | What's in it |
|---|---|
| [architecture.md](architecture.md) | The two kinds of camera, how video and telemetry flow, why streams carry explicit roles, and the CameraModule's internal threading |
| [detection.md](detection.md) | The shared AI library: the motion and sound gates, the tuning measurements behind their defaults, server-side AI, and the models |
| [telemetry.md](telemetry.md) | The six record schemas, the two speaker streams, and the ingest contract |
| [recording.md](recording.md) | Codec passthrough, why HLS and not DASH, audio in segments, clip export, and hardware transcoding |
| [clips.md](clips.md) | Saved clips: why they live outside the camera directories, segment-exact ranges, the write job, and who may delete one |
| [alerts.md](alerts.md) | The alert queue, the rolling detect-stream buffer its preview clips are cut from, and why an alert works on a camera nobody is recording |
| [live-view.md](live-view.md) | WebRTC via go2rtc, two-way talk-back, and ONVIF PTZ |
| [configuration.md](configuration.md) | The three configuration tiers, the list-binding trap, the environment-only keys, and the CameraModule's settings |
| [deployment.md](deployment.md) | Docker, the quickstart compose, the deployment examples, GPU offload, and logs |
| [rk3588.md](rk3588.md) | Orange Pi 5 deployment and NPU vision |
| [coral.md](coral.md) | Server deployment with Coral Edge TPUs: object detection on an accelerator, bring-up, and the failure modes |
| [testing.md](testing.md) | The test suites, running without a camera, and the module's diagnostics |
| [browser-testing.md](browser-testing.md) | Driving the real web build in a browser: Playwright against canvaskit, and what an insecure origin withholds |
| [app-notes.md](app-notes.md) | Flutter client decisions, and which design elements have no endpoint behind them |
