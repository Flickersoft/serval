# Serval — app

The Flutter web and Linux desktop front end for Serval. The UI uses a custom dark theme and widget
library called **Nocturne** ([`lib/theme/nocturne.dart`](lib/theme/nocturne.dart)).

### Screens

- **The wall** ([`lib/screens/wall_screen.dart`](lib/screens/wall_screen.dart)) — a 64px left
  navigation rail, a tile grid where each camera keeps its native aspect ratio, and a *What's
  happening* column showing detections, scene descriptions, sounds, and speech across all cameras
  without covering the video.
- **Single-camera view** ([`lib/screens/camera_screen.dart`](lib/screens/camera_screen.dart)) —
  opened by clicking a camera tile. Provides WebRTC live video, push-to-talk, ONVIF pan/tilt/zoom,
  a merged activity and transcript feed (including scene descriptions and clip summaries), and a
  timeline scrubber for replaying recorded HLS footage.
- **Alerts & Saved clips** ([`lib/screens/alerts_screen.dart`](lib/screens/alerts_screen.dart),
  [`lib/screens/clips_screen.dart`](lib/screens/clips_screen.dart)) — filterable alert history and
  exported clip library.
- **Cameras** ([`lib/screens/cameras_screen.dart`](lib/screens/cameras_screen.dart)) — camera
  registry and per-camera settings: name, location, stream URLs and roles, retention, ONVIF
  connection, alert rules, and which AI features are enabled.
- **Administration & auth** — **Sign-in** ([`lib/screens/login_screen.dart`](lib/screens/login_screen.dart)),
  **AI models** ([`lib/screens/ai_models_screen.dart`](lib/screens/ai_models_screen.dart)),
  **Notifications** ([`lib/screens/notifications_screen.dart`](lib/screens/notifications_screen.dart)),
  **Users & access** ([`lib/screens/users_screen.dart`](lib/screens/users_screen.dart)),
  **Server settings** ([`lib/screens/settings_screen.dart`](lib/screens/settings_screen.dart)), and
  **Server status** ([`lib/screens/server_screen.dart`](lib/screens/server_screen.dart)).

Talk-back and pan/tilt/zoom appear only on the single-camera view. On the wall, an alert offers
*Open camera* instead, so audio and PTZ actions always happen while watching that feed.

*Server status* shows live host metrics (CPU, memory, GPU/Coral, and disk from
`GET /api/system/stats`), while *Server settings* edits runtime configuration from
`GET /api/settings`. The settings screen renders dynamically from the server's settings catalogue
(including validation bounds and field descriptions), so new server settings appear automatically.
See [Docs/configuration.md](../../Docs/configuration.md).

## What's behind each screen

Screens read [`ServalRepository`](lib/data/serval_repository.dart); [`LiveServalRepository`](lib/data/live_repository.dart)
implements that interface against the Server's HTTP and WebSocket endpoints, while widget tests use
an in-memory sample repository.

| Screen element | Server surface |
|---|---|
| Wall tiles | `WS /api/dashboard` — binary frames, `[uint32 BE idLen][id utf8][jpeg]`, ~1 fps |
| Activity column, live | `WS /api/events` — `{camera_id, type, document}` |
| Activity column, history | `GET /api/cameras/{id}/scenes\|utterances\|sounds\|conversation-transcripts?from&to&limit` |
| Camera list, and all of settings | `GET/POST /api/cameras`, `GET/PUT/DELETE /api/cameras/{id}` |
| Single-camera video | `POST /api/cameras/{id}/webrtc` — raw SDP offer in, raw SDP answer out |
| Talk-back | the mic track rides that **same** SDP offer; there is no second signalling path |
| Pan/tilt/zoom | `POST /api/cameras/{id}/ptz/move` `{pan,tilt,zoom}`, `…/ptz/stop`, `…/ptz/preset`, `…/ptz/home` |
| Which PTZ controls to draw | `GET /api/cameras/{id}/ptz/capabilities` — asked of the camera, not inferred |
| Settings subtitle | `GET /api/cameras/{id}/device-information` — make, model, firmware |
| Audio level meter | `WS /api/cameras/{id}/audio-levels` — measured level + the thresholds in force |
| Snapshot, Save clip | `GET /api/cameras/{id}/snapshot.jpg`, `GET /api/cameras/{id}/clip.mp4?from&to` |
| Fallback picture | `GET /api/cameras/{id}/snapshot.jpg`, when WebRTC cannot start |
| Timeline scrubber, coverage | `GET /api/cameras/{id}/coverage?from&to` — contiguous runs of footage |
| Timeline scrubber, marks | the same telemetry reads, over the scrubber's window |
| Replay | `GET /api/cameras/{id}/vod.m3u8?from&to` — a VOD playlist per 15-minute window |

Why the client is shaped this way — the synchronous pull-shaped repository, the deliberate casing
split, replacing rather than merging on `PUT`, the two playback backends behind one interface — is
in [Docs/app-notes.md](../../Docs/app-notes.md).

## Run

```bash
flutter pub get
flutter run -d chrome --web-port=5000
```

Point it at a Server with `--dart-define`. Off web it defaults to `localhost:8080`, the port the
compose files publish, so a bare `flutter run` reaches a Server on the same machine:

```bash
flutter run -d chrome --dart-define=SERVAL_BASE_URL=http://nvr.example.lan:8080
```

**libmpv is a system dependency on Linux** — `pacman -S mpv`, `apt install libmpv-dev mpv`.
`media_kit_libs_linux` resolves it from the system rather than shipping it, so replay fails at
CMake configure or at the first `Player()` without it. The live view does not need it.

`linux/CMakeLists.txt` force-includes `<cstdint>`: `flutter_webrtc` vendors libwebrtc headers that
use `uint32_t` without it, and GCC 13 stopped providing it transitively. Remove that once the
upstream headers include it themselves.

The Chrome port is pinned because `flutter run -d chrome` otherwise picks a random one, which a
Server with a non-empty `Serval:Cors:AllowedOrigins` cannot be told about in advance.

## Test

```bash
flutter test
```

Hermetic — no Server, no network. Golden tests render the primary screens at 1440×900 with the
bundled fonts; regenerate baselines with `flutter test --update-goldens` and inspect the PNGs in
[`test/goldens/`](test/goldens/). Unit and widget tests also cover telemetry parsing, the dashboard
WebSocket binary frame format, camera record serialization, stream-role validation, and VOD replay
window arithmetic.

[`integration/`](integration/) sits outside `test/` so `flutter test` never picks it up, and runs
against a real Server:

```bash
flutter test integration/live_server_test.dart \
  --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
  --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=...
flutter test integration/registry_crud_test.dart --dart-define=…   # same three
```

Both sign in; the second needs an **Admin**, since every camera write is Admin-only while reads
take any role. Point them at a Server you started on a throwaway database, not a live NVR — the
second writes, and only ever to one throwaway id, created **disabled** so the ingest manager never
starts an ffmpeg against its fake source. A registry with no undo is not a place to be casual, and
running as an Admin is not a safeguard against that. Full detail in
[Docs/testing.md](../../Docs/testing.md#app).

Neither suite opens a browser. To drive the real web build — by hand or with Playwright — build it
and let the Server host it on a throwaway database:
[Docs/browser-testing.md](../../Docs/browser-testing.md) has the commands, the reason a blank white
page is almost always a stale `web_plugin_registrant.dart`, and the handful of things a canvaskit
app does differently under a browser driver (coordinates, not selectors).

## Layout

```
lib/
├── theme/      Nocturne's tokens (nocturne.dart), the semantic colors the design
│               adds on top (serval_tokens.dart), and the ThemeData (app_theme.dart)
├── models/     The render-ready shapes the screens read
├── data/       The repository interface, the sample and live implementations,
│               the HTTP client, the sockets, the wire-shaped records, and
│               providers.dart — the DI container the screens read
├── router/     The route tree, and the conditional import that turns web URLs
│               from #/wall into /wall
├── playback/   Replay: one VodPlayer interface, a backend per platform, and the
│               controller that decides which window is open
├── media/      Saving a snapshot or a clip: one MediaSaver interface and a
│               backend per platform, the same split playback/ makes
├── widgets/    Nocturne components and the screens' pieces, including the two
│               shells that draw the rail and the settings sidebar
└── screens/    wall_screen.dart, camera_screen.dart, cameras_screen.dart,
                users_screen.dart, server_screen.dart, login_screen.dart
```

Where you are is an address: `/wall`, `/camera/:id`, `/settings/cameras?camera=<id>`,
`/settings/users`, `/settings/server`, `/settings/status`. The rail and the settings sidebar are `ShellRoute`s rather
than something each screen redraws, and the single-camera view sits outside both on purpose — see
[Docs/app-notes.md](../../Docs/app-notes.md#decisions-worth-knowing).

The screens read the repository and the session from Riverpod; `lib/widgets/` does not, and takes
what it needs as parameters. That boundary is deliberate and is described in
[`providers.dart`](lib/data/providers.dart).

Nocturne styling conventions (`lib/theme/nocturne.dart`): surfaces use dark off-black backgrounds
with 1px hairline borders rather than drop shadows; primary buttons use a 1px accent outline on a
transparent background rather than solid fills; and form controls (`Switch`, `Slider`, inputs) are
custom widgets built directly on those tokens rather than Material's filled/ripple defaults.

Inter and JetBrains Mono are vendored in `assets/fonts/` rather than fetched at runtime, so an
offline run still renders the design as specified. Inter 4.001 and JetBrains Mono 2.211, both under
the SIL Open Font License 1.1 — `OFL-Inter.txt` and `OFL-JetBrainsMono.txt` sit beside the `.ttf`
files, which is where the license requires them. They are not listed under `assets:` in
`pubspec.yaml` and must not be: `fonts:` names the five `.ttf` paths individually, so the licenses
travel with the source without being bundled into the build.

## Not done

- **UI placeholders and omitted fields** — resolution badges, alert severity, detection bounding-box
  overlays, and per-alert historical thumbnails are either omitted or marked `// STUB:` in code. See
  [Docs/app-notes.md](../../Docs/app-notes.md#ui-placeholders-and-omitted-fields).

### Known defects

- **The single-camera control row overflows on a narrow desktop window.** *Hold to talk*, the volume
  slider, *Subtitles* and *All detections* sit in one `Row` inside the `Wrap` in `_VideoControls`
  ([`camera_screen.dart`](lib/screens/camera_screen.dart)), and that row has one intrinsic width of
  roughly 716px. The stage is whatever is left after the activity column, so below about a **1345px
  window** the row is wider than the space it is given: Flutter clips it and paints the striped
  overflow banner over the picture — 5px over at 1340, 145px at 1200, 341px at 1004. The desktop
  layout starts at `Serval.compactWidth` (950), so **the whole 950–1345 band is affected**, which is
  most of a laptop screen. The goldens render at 1440 and are clean, which is why nothing caught it.

  The outer `Wrap` drops quick-reply chips onto a second line, which does not prevent overflow
  *inside* the inner control row. Possible fixes include collapsing the volume slider to a mute
  toggle below a threshold width, switching the two labelled toggles to icons, or allowing the
  control row itself to wrap.
