# Serval.Server

The back end for Serval: a lightweight NVR plus AI-telemetry hub. It pulls camera streams and
records them to disk in chunks, ingests the AI telemetry the CameraModule produces, and serves
both — live and historical — to the App. Metadata lives in MongoDB; the video itself stays on
the filesystem.

## What it does

Everything below works today.

| Capability | How |
|---|---|
| Record + live view | One ffmpeg per camera → HLS fMP4; the segments *are* both the recording and the live stream |
| Audio in recordings | Muxed into the same segment files, per-camera opt-in |
| Multi-camera dashboard | ~1 fps snapshot wall, every camera over one WebSocket |
| Playback, any time range | VOD playlist synthesised from the segment index |
| Clip export | `clip.mp4` remuxes any range into a standalone file |
| Retention | Prunes segments + index past each camera's cutoff |
| Low-latency focused view | WebRTC via a go2rtc sidecar, with two-way talk-back (opt-in) |
| PTZ control | ONVIF pan/tilt/zoom + presets, per camera |
| **Server-side AI detection** | The shared library, for cameras with no edge module |
| AI telemetry | Idempotent ingest from the module; served back as REST history + a live WebSocket |

Both outputs come off a **single** connection per camera — the HLS segments and the dashboard
snapshot. Running N live decoders in a grid is expensive; a snapshot grid is not.

**Nothing is re-encoded unless a stream asks for it.** A camera's bits go into the archive exactly
as they arrive, and a codec fMP4 cannot carry is an error naming it rather than a silent transcode.
See [Docs/recording.md](../../Docs/recording.md).

## Streams and roles

A camera is a list of **streams**, each with a URL and one or more **roles**. Most IP cameras offer
a high-quality main stream and a small sub stream, and the two are good at different jobs:

| Role | What it drives | Cardinality |
|---|---|---|
| `record` | HLS segments on disk, the recording index, playback, VOD, clip export | **one, or none** |
| `detect` | the ~1 fps snapshots — motion, scene description, the dashboard wall, `/snapshot.jpg` — and the audio the AI transcribes | **exactly one** |
| `live` | the low-latency WebRTC view, registered with go2rtc | **exactly one** |

Every role is assigned explicitly. There is no fallback: a camera with only one stream declares all
three roles on it. A stream may carry **no** roles, in which case it is stored and never pulled —
a way to hold a source out of service without deleting it. The registry check names every
role-less stream on startup.

`record` is the one role a camera may leave unassigned, because it is the only one that costs disk.
Leave it off every stream and the camera is still watched, still alerts, and is still viewable over
WebRTC — nothing is written, so it has no playback, no timeline and no clip export, and
`retentionDays` and `recordAudio` stop meaning anything. The registry check says so in the log
rather than leaving the missing footage to be discovered.

**`"recording": false` is the temporary version of that.** It stops the recorder without touching
the `record` role — the switch is on the App's *Keeping footage* page. It defaults to `true` and
may not be `true` with no `record` stream. Footage already on disk stays playable and still
expires under `retentionDays`.

```json
{
  "id": "driveway", "name": "Driveway",
  "streams": [
    { "name": "main", "url": "rtsp://…/h264Preview_01_main", "roles": ["record", "live"] },
    { "name": "sub",  "url": "rtsp://…/h264Preview_01_sub",  "roles": ["detect"] }
  ]
}
```

Pointing `detect` at a small sub stream is what decouples detection's cost from recording quality —
[Docs/architecture.md](../../Docs/architecture.md#streams-and-roles) explains why, and which source
schemes are accepted.

## Prerequisites

- **.NET 10 SDK**
- **ffmpeg** and **ffprobe** on `PATH` (or set `Serval:Ingest:FfmpegPath` / `FfprobePath`)
- **MongoDB** reachable at `Serval:Mongo:ConnectionString`

```bash
dotnet run          # from Server/Serval.Server
dotnet test ../Serval.Server.Tests
```

The server creates its Mongo indexes on startup and fails loudly if Mongo is unreachable — it
has nothing to do without it. The tests need no MongoDB, ffmpeg or video; a camera can point at a
local video file to exercise the whole pipeline with no hardware. See
[Docs/testing.md](../../Docs/testing.md).

## API

```
Cameras   GET/POST /api/cameras   GET/PUT/DELETE /api/cameras/{id}
          a camera is {id, name, streams:[{name, url, roles:[record|detect|live]}], ...}
Live      GET /api/cameras/{id}/snapshot.jpg       latest still (memory-cached)
          WS  /api/dashboard                       one socket, ~1fps JPEG per camera
          POST /api/cameras/{id}/webrtc            WebRTC signaling: SDP offer → answer
Control   POST /api/cameras/{id}/ptz/move          {pan,tilt,zoom} each -1..1
          POST /api/cameras/{id}/ptz/stop          halt pan/tilt/zoom
          POST /api/cameras/{id}/ptz/zoom          {position} 0..1 of the lens's travel
          POST /api/cameras/{id}/ptz/preset        {preset} recall a stored position
          POST /api/cameras/{id}/ptz/home          recall the home position
          GET  /api/cameras/{id}/ptz/capabilities  what the camera says it can do
          GET  /api/cameras/{id}/ptz/status        where the camera says its lens is
          GET  /api/cameras/{id}/device-information make, model, firmware
Playback  GET /api/cameras/{id}/recordings?from&to segment index for a scrubber
          GET /api/cameras/{id}/vod.m3u8?from&to   VOD playlist over stored segments
          GET /api/cameras/{id}/clip.mp4?from&to   standalone MP4 export (video+audio)
          GET /api/cameras/{id}/coverage?from&to   contiguous runs of footage
Settings  GET /api/settings                        every setting a user may change, with
                                                   its value, what a reset restores, and where
                                                   the value in force came from
          PUT /api/settings                        { "Serval:Media:RetentionDays": 14 }
                                                   a null value resets that setting (Admin)
Prefs     GET /api/preferences                     the signed-in account's own state
          PUT /api/preferences                     { "wallLayout": [...] } — merges, so an
                                                   omitted property is left alone. No id in
                                                   the route: it is always your own.
Telemetry POST /api/cameras/{id}/telemetry         module → server (X-Api-Key)
Google    GET  /api/google/status                  is the integration live, and if not, why (Admin)
          GET  /api/google/links                   the linked Google account, if any (Admin)
          DELETE /api/google/links/{agentUserId}    unlink, revoking every credential (Admin)
          GET  /api/google/oauth/authorize         account linking starts here (public)
          POST /api/google/oauth/token             code and refresh grants (public)
          POST /api/google/fulfillment             SYNC · QUERY · EXECUTE · DISCONNECT (public)
          POST /api/google/camerastream/signal     WebRTC signaling for one camera (public)
AI serve  GET /api/cameras/{id}/utterances?from&to&limit
          GET /api/cameras/{id}/scenes?from&to&limit
          GET /api/cameras/{id}/detections?from&to&limit
          GET /api/cameras/{id}/sounds?from&to&limit
          GET /api/cameras/{id}/conversation-transcripts?from&to&limit
          WS  /api/events[?camera={id}]            live AI push to the App
          WS  /api/cameras/{id}/audio-levels       measured level + thresholds, ~10 Hz
```

**Dashboard frames** are binary — `[uint32 cameraId length][cameraId UTF-8][JPEG]` — to skip the
~33% base64 tax of images-in-JSON. **Live events** are JSON text — `{ camera_id, type, document }`.

**The four public `/api/google/*` routes are the only ones on this server meant to be reachable
from the internet**, and each authenticates itself rather than relying on a session — Google's
servers have none. They all answer 503 until the integration is configured, which is the default.
See [Docs/google-home.md](../../Docs/google-home.md).

`GET /scalar/v1` serves a [Scalar](https://scalar.com) API reference over the generated OpenAPI
document, with a "Test Request" button that calls the live server. Unlike the usual ASP.NET
template it is **not** gated on `Development` — camera CRUD needs a GUI and the deployed container
runs as Production. Turn it off with `Serval__OpenApi__Enabled=false` before exposing the server.

## Configuration

Everything lives under `Serval`, overridable by environment variable
(`Serval__Ingest__SnapshotFps=2`) and, for most settings, by the App. The full reference is in
[Docs/configuration.md](../../Docs/configuration.md#server--everything-under-serval).

Three sources over the built-in values, last wins: the option classes say what the Server does with
nothing set, environment variables carry the deployment's choices, and a single document in Mongo's
`settings` collection carries whatever has since been changed at *Settings → Server settings*.
`appsettings.json` carries logging and nothing else — a default restated there would report itself
to the settings page as this deployment's choice, and would be a second home free to drift from the
first. `Configuration/BuiltInDefaults.cs` is how the settings page reads a value that exists only as
a property initialiser. The overlay is a real
`IConfigurationProvider` (`Configuration/SettingsConfigurationProvider.cs`), added last, so the
option classes and the binder are untouched and only which value wins is different. A write reloads
it in-process — there is no change stream and nothing to poll — and every service reads through
`IOptionsMonitor`, so a change is in use within a few seconds without a restart.

Not everything can be: model paths, thread counts and the `Enabled` switches that decide whether a
model is loaded at all are read while the process is composed, so those are stored, reported and
applied on the next restart. `Configuration/SettingsCatalog.cs` says which is which, carries the
bounds, and holds the sentence the App shows under each field. **A key not in that catalogue cannot
be written** — which is what keeps `Mongo.*`, `Media.Root`, `Auth.SigningKey`, the ffmpeg paths and
the exposure settings reachable only from the environment.

Cameras are **not** configuration. They are managed at runtime through `/api/cameras` — the ingest
manager reconciles against the registry every few seconds, so adding, disabling, or deleting a
camera is all it takes to start or stop its stream. The same reconcile loops are how a settings
change reaches a running camera: both compare a signature that covers the settings a session reads
when it is built, and restart what changed.

### Serving over plain HTTP

Supported, and the default: getting a look at Serval should not require certificates first. One
feature genuinely cannot work that way — **talk-back**, because browsers only hand over a
microphone (`getUserMedia`) to a *secure context*. The App disables *Hold to talk* and says so
rather than leaving a button that does nothing.

Everything else degrades on its own: sessions fall back from the browser's encrypted store to plain
`localStorage`, and image decoding falls back to CanvasKit's own codec. Note that `localhost` and
`127.0.0.1` are secure contexts even over HTTP, so none of this shows up in local development —
[Docs/browser-testing.md](../../Docs/browser-testing.md#test-the-insecure-origin-too-and-know-what-it-costs)
has the full list and how to test the insecure path.

TLS is still worth adding for a deployment you keep: it restores talk-back, and it is what stops
the WebRTC SDP answer — which carries the ICE credentials and DTLS fingerprint for the media
session — from being readable on the wire.

## Further reading

- [Docs/recording.md](../../Docs/recording.md) — codecs, audio in segments, clip export, transcoding
- [Docs/live-view.md](../../Docs/live-view.md) — WebRTC via go2rtc, talk-back, ONVIF PTZ
- [Docs/detection.md](../../Docs/detection.md) — server-side AI for module-less cameras
- [Docs/telemetry.md](../../Docs/telemetry.md) — the ingest contract and record schemas
- [Docs/deployment.md](../../Docs/deployment.md) — Docker, the deployment examples, GPU offload, logs

## Known issues

- **`Microsoft.OpenApi` is pinned, not inherited.** `Microsoft.AspNetCore.OpenApi` drags in 2.0.0
  transitively, which carries NU1903 (high). Since the document endpoint is served in Production,
  the csproj pins 2.11.0 (fixed in 2.7.5). **Re-check on any `Microsoft.AspNetCore.OpenApi` bump**:
  the pin can go once that package references a patched version itself.
`dotnet list Serval.slnx package --vulnerable --include-transitive` is the check, and it reports
nothing today.

## Not done yet

- **Camera credentials are still stored in clear.** `GET /api/cameras` now strips them for every
  role below Admin — both the `OnvifPassword` field and any `user:password` in a stream URL — but
  what sits in MongoDB is the plaintext, and the shipped Mongo has no authentication of its own.
  Anything with database access, or with an Admin token, reads them. Encrypting at rest needs a key
  the server can reach at boot, which is a different problem from this one.
- **WebRTC beyond the LAN.** The signaling proxy works anywhere, but the media path needs a
  STUN+TURN server to traverse NAT from outside the LAN. Only LAN (host-candidate) is wired now.
- **Model distribution for server-side AI.** The dev compose file does not yet mount the model
  directory as a volume, so it wants the ~2.5 GB baking into the image. The quickstart and example stacks mount it.
- **Server-side vision scaling.** One model instance is shared across every camera, drained
  round-robin — right as a default, but many busy cameras will want a GPU backend or a per-camera
  description budget.
- **Ingest failure is invisible.** An unreachable camera is retried forever with capped backoff and
  says so only in the log. There is no `Camera.Status`, no health endpoint and no event, and
  `SnapshotBroadcaster` keeps serving the last good frame — so a dead camera looks like a frozen
  one. Validation rejects the unpullable URLs it can detect up front, which narrows the trap
  without closing it.
- **Cameras that cap concurrent RTSP sessions.** With server-side AI audio on, a camera holds two
  permanent sessions, three with a separate `detect` stream — and a budget camera allowing only two
  will then refuse the WebRTC viewer. The workarounds, and two shapes that look like fixes and
  aren't, are in
  [Docs/architecture.md](../../Docs/architecture.md#cameras-that-cap-concurrent-rtsp-sessions).
