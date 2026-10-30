# Serval TODOs

Actionable engineering checklist for the quick wins and medium-term items from the project evaluation.

---

## Quick Wins

### 1. Clear account lockout on Admin password reset (and/or add Admin unlock route)
- **Problem:** [`AuthEndpoints.LoginAsync`](Server/Serval.Server/Auth/AuthEndpoints.cs#L79-L117) checks `UserRepository.IsLocked(existing)` before checking the password, and [`UserRepository.ClearFailedLoginsAsync`](Server/Serval.Server/Auth/UserRepository.cs#L130-L134) is only called on a successful login. Meanwhile, [`UserRepository.UpdateRestoredAsync`](Server/Serval.Server/Auth/UserRepository.cs#L209-L214) clears `FailedLoginAttempts` and `LockedUntil` when a password hash is restored, but [`UserRepository.SetPasswordAsync`](Server/Serval.Server/Auth/UserRepository.cs#L141-L167) does not. An attacker who knows the admin username can send 5 bad passwords every 30 minutes to keep the account permanently locked out.
- **Tasks:**
  - [ ] Update [`UserRepository.SetPasswordAsync`](Server/Serval.Server/Auth/UserRepository.cs#L161-L164) to also reset `FailedLoginAttempts` to `0` and `LockedUntil` to `null` (matching `UpdateRestoredAsync`).
  - [ ] Optionally add `POST /api/auth/users/{username}/unlock` (Admin-only) in [`AuthEndpoints.cs`](Server/Serval.Server/Auth/AuthEndpoints.cs#L62-L77) calling `ClearFailedLoginsAsync`, and expose `LockedUntil` on [`UserResponse`](Server/Serval.Server/Auth/AuthEndpoints.cs#L356) so an Admin can see and clear a lockout without changing the user's password.
  - [ ] Add unit/integration test coverage for lockout clearing on password reset.

### 2. Redact camera credentials from `ffmpeg` stderr logs
- **Problem:** RTSP URLs embed `user:password@host`. When `ffmpeg` logs connection errors to stderr, those lines are logged verbatim at `LogLevel.Warning`.
- **Tasks:**
  - [ ] Add a small helper (e.g. `RedactedLine(string line)` using a compiled `Regex` matching `([a-zA-Z][a-zA-Z0-9+.-]*://[^/\s:@]+:)([^@\s/]+)(@)`) to replace embedded URL passwords with `***`.
  - [ ] Apply redaction in [`FfmpegRunner.PumpStderrAsync`](Server/Serval.Server/Ingest/FfmpegRunner.cs#L183-L195).
  - [ ] Apply the same redaction in [`ClipExporter`](Server/Serval.Server/Media/ClipExporter.cs) and [`FfmpegAudioSession`](Server/Serval.Server/Ai/FfmpegAudioSession.cs) stderr pumps.
  - [ ] Add unit tests verifying that `rtsp://admin:secret123@192.168.1.50:554/stream` in an stderr line is logged with the password redacted while non-credential text is untouched.

### 3. Validate `Go2RtcUrl` and harden `SourceArguments` file/protocol handling
- **Problem:**
  1. `Serval:WebRtc:Go2RtcUrl` in [`SettingsCatalog.cs`](Server/Serval.Server/Configuration/SettingsCatalog.cs#L342-L345) is `SettingKind.Text`, which only checks for non-blank strings. A non-URI causes `new Uri(...)` in [`Program.cs`](Server/Serval.Server/Program.cs#L324) to throw an unhandled 500, and an arbitrary internal URL turns [`POST /api/cameras/{id}/webrtc`](Server/Serval.Server/WebRtc/WebRtcEndpoints.cs#L29-L82) into a Viewer-triggerable SSRF.
  2. [`SourceArguments.Scheme`](Server/Serval.Server/Ingest/SourceArguments.cs#L138-L150) only looks for `"://"`, so `ffmpeg` pseudo-protocols (`concat:`, `subfile:`, `crypto:`) or arbitrary container paths (`/proc/self/environ`) pass `IsSupported` as "local files".
- **Tasks:**
  - [ ] Validate URL settings (`Serval:WebRtc:Go2RtcUrl`, `Serval:Push:ContactUri`) in [`SettingsCatalog.Validate`](Server/Serval.Server/Configuration/SettingsCatalog.cs#L1180-L1223) so `Go2RtcUrl` requires an absolute `http` or `https` URI.
  - [ ] Reject `scheme:` strings without `://` in [`SourceArguments.IsFile`](Server/Serval.Server/Ingest/SourceArguments.cs#L127) / `IsSupported` (except valid paths), and pass `-protocol_whitelist` (`file,crypto,data` or `rtsp,rtp,tcp,udp,http,https,tls,rtmp,srt` as appropriate) in [`SourceArguments.InputArgs`](Server/Serval.Server/Ingest/SourceArguments.cs#L52-L78) and `ProbeArgs`.
  - [ ] Add tests in [`SettingsCatalogTests.cs`](Server/Serval.Server.Tests/SettingsCatalogTests.cs) and [`SourceArgumentsTests.cs`](Server/Serval.Server.Tests/SourceArgumentsTests.cs).

### 4. Pin HuggingFace revision and verify SHA-256 checksums in `scripts/fetch-models.sh`
- **Problem:** [`scripts/fetch-models.sh`](scripts/fetch-models.sh#L120-L133) fetches `Qwen3-VL-2B-Instruct-GGUF` from HuggingFace's mutable `/resolve/main` ref and downloads `sherpa-onnx` archives from GitHub without checksum verification.
- **Tasks:**
  - [ ] Pin `VLM_BASE` in [`scripts/fetch-models.sh`](scripts/fetch-models.sh#L120) to the exact HuggingFace commit revision SHA instead of `main`.
  - [ ] Add expected SHA-256 hashes for `SenseVoice`, `Qwen3-VL-2B-Instruct-Q8_0.gguf`, `mmproj-Qwen3-VL-2B-Instruct-Q8_0.gguf`, `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`, `sherpa-onnx-pyannote-segmentation-3-0.tar.bz2`, and `sherpa-onnx-zipformer-small-audio-tagging-2024-04-15.tar.bz2`.
  - [ ] Verify each downloaded artifact with `sha256sum -c -` before extracting or moving it into `$MODEL_DIR` (remove the partial file on checksum failure).

### 5. Fix minor documentation drift
- **Tasks:**
  - [ ] [`CONTRIBUTING.md`](CONTRIBUTING.md#L7): Update `[What's missing](README.md#whats-missing)` to `[Status](README.md#status)`.
  - [ ] [`Server/Serval.Server/README.md`](Server/Serval.Server/README.md#L206-L209): Update the `Microsoft.OpenApi` note to reflect the current `2.12.2` pin and the `3.0` `CS0200` source-generator constraint documented in [`Serval.Server.csproj`](Server/Serval.Server/Serval.Server.csproj#L12-L21).
  - [ ] [`CameraModule/Serval.CameraModule/README.md`](CameraModule/Serval.CameraModule/README.md#L124-L128): Update or remove the `CVE-2025-6965` (`SQLitePCLRaw.lib.e_sqlite3`) known-issue bullet to match [`Serval.CameraModule.csproj`](CameraModule/Serval.CameraModule/Serval.CameraModule.csproj#L42-L49), where `2.1.13` is directly referenced and `Microsoft.Data.Sqlite` `10.0.12` already clears the advisory.

---

## Medium-Term Priorities

### Medium 1: Surface camera ingest health & add low-disk emergency retention pruning
- **Problem:**
  1. When a camera drops or refuses connections, [`StreamIngestManager`](Server/Serval.Server/Ingest/StreamIngestManager.cs) retries forever with backoff in the logs while [`SnapshotBroadcaster`](Server/Serval.Server/Snapshots/SnapshotBroadcaster.cs) continues serving the last good frame (or nothing after a restart). Neither the API nor the UI can distinguish a healthy idle stream, a reconnecting camera, and a misconfigured/unreachable camera, nor is an alert raised when a camera stays down.
  2. [`RetentionWorker`](Server/Serval.Server/Recordings/RetentionWorker.cs) prunes strictly by `RetentionDays`. If bitrates spike and free disk space falls below critical, writes fail rather than evicting the oldest segments first.
- **Tasks:**
  - [ ] **Track per-camera ingest state in memory:** In [`StreamIngestManager`](Server/Serval.Server/Ingest/StreamIngestManager.cs), record connection state per camera (`Starting`, `Connected`, `Reconnecting`, `Faulted`), consecutive failure count, `LastConnectedAt`, `LastError`, and `NextRetryAt`.
  - [ ] **Expose health via API & WebSocket:** Include the live ingest status on camera responses (`GET /api/cameras`, `GET /api/cameras/{id}`) or `GET /api/system/stats`, and push state transitions over `/api/events` so the App updates without polling.
  - [ ] **Stop serving stale snapshots as live:** Have [`SnapshotBroadcaster`](Server/Serval.Server/Snapshots/SnapshotBroadcaster.cs) / the App clearly mark a camera as offline with the last-seen timestamp and error reason when its session is in backoff.
  - [ ] **Add optional low-space emergency pruning in [`RetentionWorker`](Server/Serval.Server/Recordings/RetentionWorker.cs):** When volume free space falls below `Serval:Vitals:DiskCriticalPercentFree` (or a dedicated `Serval:Media:MinFreePercent` floor), delete the oldest recording segments across cameras until free space recovers above the threshold, logging a warning with how much history was trimmed.

### Medium 2: Multi-arch (`linux/arm64`) Server container image
- **Problem:** [`server-image.yml`](.github/workflows/server-image.yml#L109) and [`Server/Serval.Server/Dockerfile`](Server/Serval.Server/Dockerfile) only build `linux/amd64`. However, [`Serval.Ai.csproj`](Shared/Serval.Ai/Serval.Ai.csproj#L9) already supports `linux-arm64` (`org.k2fsa.sherpa.onnx.runtime.linux-arm64` and `LLamaSharp.Backend.Cpu` both ship `linux-arm64` natives). Supporting `linux/arm64` unlocks Raspberry Pi 5, Orange Pi 5 / RK3588, ARM cloud instances, and Apple Silicon Macs as Server hosts.
- **Tasks:**
  - [ ] **Parameterize architecture-specific stages in [`Server/Serval.Server/Dockerfile`](Server/Serval.Server/Dockerfile):**
    - Use `TARGETARCH` (`amd64` vs `arm64`).
    - In the `app` stage, Flutter's Linux SDK clone works on x86_64; run the `app` stage with `FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS app` so `flutter build web` always executes natively on the x86_64 runner without QEMU emulation.
    - In the `build` stage, default `LLAMA_BACKEND` to `Vulkan` on `amd64` and `Cpu` on `arm64` (since `LLamaSharp.Backend.Vulkan.Linux` is `linux-x64` only), and pass `-r linux-x64` / `-r linux-arm64` as appropriate.
    - In the `edgetpu` stage, fetch the matching `arm64` `.deb` (`libedgetpu1-std_16.0tf2.16.1-1.bullseye_arm64.deb` from `feranick/libedgetpu` with its SHA-256) when `TARGETARCH=arm64`.
    - In the `runtime` stage, install `intel-media-va-driver` only on `amd64`, and run the `runtimes/linux-x64/native/vulkan` `libggml.so` overlay only when `/app/runtimes/linux-x64/native/vulkan` is present.
  - [ ] **Update [`.github/workflows/server-image.yml`](.github/workflows/server-image.yml):**
    - Add `docker/setup-qemu-action` (or a native `ubuntu-24.04-arm` matrix leg joined via `docker buildx imagetools create` so the `tflite` CMake compile runs natively on ARM without QEMU overhead).
    - Set `platforms: linux/amd64,linux/arm64`.
  - [ ] **Update documentation:** Update Hardware Support in [`README.md`](README.md#L127-L157) and [`Docs/deployment.md`](Docs/deployment.md) once the multi-arch manifest is verified.

