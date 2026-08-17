# Serval.CameraModule

Listens to a microphone, detects speech, and for each utterance produces a transcript,
a speech-emotion label, and an audio-event label. Results are delivered to the Serval Server
over HTTP (or written as JSON to the filesystem when no server is configured).

All inference runs **locally and in-process** via native C++ (sherpa-onnx over its C API).
No network calls at runtime, no external model servers.

The detection itself lives in [`Shared/Serval.Ai`](../../Shared/), which the Server references too —
a camera gets the same AI whether or not it has an edge device. What stays in *this* project is
what only an edge module has: V4L2 capture, PortAudio, and the durable SQLite outbox.

Targets:
- **Desktop (linux-x64)** — development and debugging.
- **Orange Pi 5 / RK3588 (linux-arm64)** — deployment target.

The same code and the same models run on both. There is no per-architecture branch in the
audio path.

The rule throughout: **a capability either runs a real model or reports nothing.** Never add a mock
that emits plausible-looking output.

## Status

| Capability | State |
|---|---|
| Sound-level gate | Working — RMS with pre-roll + hangover, in front of the VAD |
| Speech detection (VAD) | Working — Silero v5 via sherpa-onnx |
| Transcription | Working — SenseVoice (zh/en/ja/ko/yue) |
| Emotion from audio | Working — SenseVoice SER |
| Audio events | Working — laughter, applause, BGM, … |
| Camera capture | Working — V4L2 MJPEG, no image library |
| Motion gate | Working — frame differencing, in front of the vision model |
| Image description | Working — Qwen3-VL-2B via llama.cpp mtmd (**CPU**), **multi-frame** |
| Speaker labels (live) | Working — best-effort, per utterance |
| Speaker diarization | Working — after the fact, per conversation |
| Conversation transcript | Working — diarized turns with the transcripts re-attributed |
| JSON output | Working — JSONL via a SQLite outbox |
| HTTP delivery | Working — `HttpTelemetrySink` POSTs to the Server (JSONL fallback offline) |
| NPU acceleration | Working — RK3588 vision on the NPU |
| Video relay to Server | **Not started** — module emits telemetry only (see [Roadmap](#roadmap)) |

Vision is **off by default** (`CameraModule:Vision:Enabled`): it costs a 2.3 GB model download and
seconds of CPU per description. Audio behaves identically either way, and with vision off the
`vision` field is simply absent — never fabricated.

## Setup

```bash
./scripts/fetch-models.sh   # ~3.5 GB, not in git; SKIP_VISION=1 for ~1.2 GB without the VLM
dotnet run
```

Speak into the microphone. Transcripts appear on stdout and in `data/telemetry.jsonl`.

To enable vision: `CameraModule__Vision__Enabled=true dotnet run`

### Verifying without a microphone or camera

The diagnostics live in `../Serval.CameraModule.Tools` — one binary beside the worker, reading
the same configuration section, so the deployed service carries none of them. Run them from this
directory so relative model paths resolve:

```bash
alias tools='dotnet run --project ../Serval.CameraModule.Tools --'
tools --selftest                 # decode a bundled fixture, check the emotion interop
tools --capture-test frame.jpg   # grab one camera frame, validate it is real JPEG
tools --motion a.jpg b.jpg       # would this scene have woken the vision model?
tools --detect a.jpg b.jpg       # what does the object detector see, and what would it open?
tools --replay-gates frames/     # both gates over the same frames, side by side
tools --describe frame.jpg       # describe an image with the real model
tools --describe a.jpg b.jpg     # describe what changed across frames (multi-frame)
tools --tag-sounds clip.wav      # classify sounds and print the scored shortlist
tools --speakers a.wav --expect 2  # measure speaker labelling at many thresholds
```

On a new board, run these in order — each isolates one layer, so a failure tells you which.
WAV inputs must be 16 kHz 16-bit mono (`sox in.wav -r 16000 -c 1 -b 16 out.wav`).

```bash
dotnet test ../Serval.CameraModule.Tests    # the module: outbox, sink, V4L2 ABI
dotnet test ../../Shared/Serval.Ai.Tests    # the shared library: gates, contract, conversations
```

Both run in a couple of seconds and need **no models, fixtures, or devices** — clone and run.
[Docs/testing.md](../../Docs/testing.md) covers what each diagnostic proves, and how to calibrate
a camera's motion and speaker thresholds by measurement rather than guesswork.

## Models

| Model | Purpose | Size |
|---|---|---|
| `silero_vad.onnx` | Speech detection (v5, 512-sample windows) | 2.3 MB (in git) |
| `sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17` | ASR + emotion + audio events | 233 MB |
| `Qwen3-VL-2B-Instruct-GGUF` | Image description (weights + mmproj) | 2.28 GB |
| `3dspeaker_campplus_sv_zh_en` | Speaker embeddings (live labels) | 27 MB |
| `sherpa-onnx-pyannote-segmentation-3-0` | Speaker segmentation (diarization) | 7 MB |

`SKIP_VISION=1` skips the 2.3 GB vision download; `SKIP_SPEAKER=1` skips the speaker models.
Two traps worth knowing before you change any of these — the SenseVoice build that looks like an
upgrade but is a Cantonese fine-tune, and why Silero is vendored in git — are in
[Docs/detection.md](../../Docs/detection.md#models).

## Further reading

- [Docs/detection.md](../../Docs/detection.md) — the gates, and the measurements behind their
  defaults. **Read the sound-gate section before deploying**: it is the setting most likely to be
  wrong, and it fails silently.
- [Docs/telemetry.md](../../Docs/telemetry.md) — the six record schemas and the two speaker streams
- [Docs/configuration.md](../../Docs/configuration.md#cameramodule--everything-under-cameramodule) —
  every `CameraModule:*` setting
- [Docs/architecture.md](../../Docs/architecture.md#inside-the-cameramodule) — the thread and
  worker layout, and the three invariants that keep audio real-time
- [Docs/rk3588.md](../../Docs/rk3588.md) — Orange Pi 5 deployment and NPU vision

## Known issues

- **`data/telemetry.jsonl` grows without bound.** When no `Output.ServerUrl` is set, the JSONL
  fallback sink ([`TelemetrySyncWorker`](Output/TelemetrySyncWorker.cs)) appends forever with no
  size cap or rotation — on an SD card that eventually fills the device. **TODO: add a size cap.**
  Note this is telemetry *data*, not a log, so it is deliberately not covered by the journald
  setup; rotating it would drop records the Server has never seen.
- **CVE-2025-6965** (`SQLitePCLRaw.lib.e_sqlite3`, high) is reported at build. It arrives
  transitively through `Microsoft.Data.Sqlite`, not as a direct reference. It requires
  attacker-controlled SQL; every query here is parameterised and internally authored, so it is not
  reachable. **Re-check on any `Microsoft.Data.Sqlite` bump** — that is what will pull in a patched
  native package, and this note should go with it.

## Roadmap

**Video relay to the Server.** Today the module emits **telemetry only** — the camera's video
never leaves the edge device. But the module does *not* transcode to Serval's unified codec, and
its camera is local (V4L2 `/dev/video0`), so for a module camera to be **recorded** its video
must still reach the Server's ffmpeg. The module needs to relay it over the LAN.

Recommended shape: capture a full-quality stream alongside the low-res AI capture, hardware-encode
it on the RK3588 in whichever codec the SoC does best, and **serve it as RTSP/RTP** on the LAN.
Then a module camera's `record` stream URL on the Server simply points at the module, and the
entire existing Server ingest → HLS / snapshots / WebRTC pipeline records it with **no Server
changes**. Until this exists, module cameras contribute AI but are not recorded. Cross-project;
sized separately.

One config note when this lands: the Server records video exactly as it arrives and never
re-encodes unless a stream explicitly asks it to, so there is no codec to match and no
double-encode to avoid. Just encode to something in the Server's `Ingest:VideoPassthroughCodecs`
(`h264`, `hevc`, `av1`, `vp9` by default) — the RK3588's VPU does H.264 and HEVC, both on that list.

Two smaller items are documented where they bite: [multi-frame vision on the
NPU](../../Docs/rk3588.md#vision-on-the-npu) and [conversation audio on
tmpfs](../../Docs/rk3588.md#conversation-audio-on-tmpfs).
