# Live view and camera control

Sub-second video, talk-back, and PTZ — the three things that only make sense together, because
driving a camera you are watching on a five-second delay does not work.

## Low-latency live: WebRTC

HLS is a great NVR transport but has multi-second latency — fine for watching, useless for
*interacting* (PTZ, talk-back). For the focused single-camera view, the server can offer **WebRTC**
(sub-second) **alongside** HLS, served by a [go2rtc](https://github.com/AlexxIT/go2rtc) sidecar.
HLS is untouched: it stays the recorder, the dashboard snapshot source, and archive playback;
WebRTC serves only the one focused view.

The split matters for load. go2rtc pulls a camera's RTSP **lazily** — only while a viewer has the
focused view open — so it adds *no* constant second connection to the camera. The always-on
ffmpeg→HLS pipeline remains the only permanent consumer, and go2rtc writes **nothing** to disk.

How it fits together:

- **The server mirrors the camera registry into go2rtc.** `Go2RtcSyncWorker` reconciles on a timer
  (like the ingest manager does for ffmpeg): every enabled RTSP camera gets a go2rtc stream named
  after its id; file (test) cameras are skipped. Adding or disabling a camera is all it takes.
- **Signaling is proxied through the server**, media is not. The App POSTs its SDP offer to
  `POST /api/cameras/{id}/webrtc` (`application/sdp`); the server forwards it to go2rtc and returns
  the SDP answer. The actual media (SRTP) then flows **directly** browser ↔ go2rtc — never through
  the server. Proxying signaling gives a single origin and one place to add auth; go2rtc is never
  exposed to clients.
- **Networking:** the browser needs to reach go2rtc's media port (`8666`). On a LAN (the typical
  NVR case) this just works once go2rtc advertises the host's address as an ICE candidate — set
  `webrtc.candidates` in [go2rtc.yaml](../Server/go2rtc.yaml) to the host's LAN IP (or `stun` to
  auto-discover). **Reaching it from outside the LAN needs a STUN+TURN server** (go2rtc supports an
  external TURN); that's not set up here.
- **It follows the `live` role, and nothing else.** A camera with no `record` role keeps nothing and
  still has a full WebRTC view — go2rtc pulls the camera itself and never reads the archive, so
  live view and recording are independent choices. The reverse also holds: a camera whose `live`
  role points at a file path has no WebRTC view, because go2rtc cannot serve a file.

Turn it on with `Serval:WebRtc:Enabled=true` and point `Serval:WebRtc:Go2RtcUrl` at the sidecar
(`http://go2rtc:1984` in compose). When disabled, the worker stays idle and the endpoint returns
503 — recording is unaffected either way. The compose file includes the go2rtc service, wired and
enabled.

## Coming back from the background

The App reads the lifecycle in exactly two places, both `AppLifecycleListener`, and both on
**`onShow`** rather than `onResume`: the hidden→visible edge only. Flutter reports `inactive` when
the window merely loses focus, and a second monitor showing the wall while you work elsewhere is the
case this must leave alone — `onShow` buys that rule with no bookkeeping of its own.

- **`_RepositoryStarter` (`main.dart`)** calls `LiveServalRepository.resumeLive()`, which restarts
  the listening window (below) and calls `reconnectNow()` on both sockets. Guarded on the session
  being up, so coming back to a tab sitting on `/login` raises nothing.
- **`WebRtcView`** rebuilds its own session, because the widget owns the connection.

`reconnectNow()` exists because the backoff is exactly wrong here: a phone away for ten minutes is
sitting on the thirty-second cap, so the wall would hold its last frames for most of a minute after
somebody looked at it. It is unconditional rather than skipped when a channel is open — a socket
whose peer went away while the radio slept is half-open, reads as connected, and will never deliver
another frame. Being wrong the other way costs one handshake, since `DashboardEndpoint` repaints
`broadcaster.AllLatest()` on connect. `EventsSocket.reconnectNow()` additionally sets its
`_gapToClose` flag, which `_teardown` does not: without it a resume would reconnect silently and
leave a hole in the activity column.

### Connecting is not offline

Camera status is derived entirely from snapshot staleness on `WS /api/dashboard` — the Server
publishes no status field — and the failure that follows from reading staleness alone is that a
resumed PWA has *every* camera stale at once. The wall painted all six `"<name> is offline"` for the
second or two the reconnect took: a claim about six cameras made on the strength of one socket
nobody had told it went away.

So `Camera.connection` is a three-way `CameraConnection` and **`offline` is a failure state** —
we listened, and heard nothing. `LiveServalRepository._frameStateOf` decides:

| | |
|---|---|
| a frame inside `_staleAfter` (15s) | `online` |
| no frame ever | `connecting` — never measured, and unbounded |
| stale, inside `_listeningWindow` | `connecting` |
| stale, window closed | `offline` |

`_listeningSince` is stamped by `start()`, by the dashboard socket's connect edge, and by
`resumeLive()`. `_listeningWindow` is defined *as* `_staleAfter` rather than as its own figure:
both answer how long silence is allowed before it counts as absence, from two different starting
points, and two constants that happened to agree would be one edit away from a wall that calls a
camera dead sooner after a reconnect than it does while running.

The obvious alternative was to **clear `_lastFrameAt` on resume** and let the cold-start branch
cover it. That is worse, and the reason is worth keeping: clearing forgets *which* cameras were
dead, so a camera unreachable for a week would read "connecting" alongside its working neighbours
after every resume. Keeping the clock and dating our own listening instead gives every camera the
same fifteen seconds and then lets them part company on the evidence.

Two backstops, because the lifecycle event is the one thing here that depends on the platform
behaving:

- The 5s sweep in `start()` notices its **own tick arriving late** — `Stream.periodic` is throttled
  in a hidden tab and suspended outright in a backgrounded PWA — and restarts the window itself. So
  the wall still recovers on a browser that never reports being hidden, just up to five seconds
  later and without the fast reconnect.
- `clockDigest()` carries `_frameStateOf` per camera rather than the freshness it was derived from,
  so the window *closing* moves the digest and wakes the sweep. Without that the wall would read
  "connecting" until something unrelated happened to rebuild it.

### The focused view recovers too

`WebRtcSession` previously reacted only to `RTCPeerConnectionStateFailed`. `Disconnected` — what a
backgrounded phone's session lands in — was ignored, so on resume the view sat in `WebRtcStage.live`
showing a frozen picture with no sign anything was wrong, which is a worse lie than the wall's
flash. And there was no retry from `failed` at all.

Now: `disconnected` starts a 5s settle timer (WebRTC recovers from it routinely — a Wi-Fi handover,
a moment of congestion — and swapping the picture out on every one of those would be worse than the
freeze), and if it has not recovered the stage drops to `connecting` and `WebRtcView._restart()`
builds a new session. `failed` and `closed` are terminal for a peer connection, so they restart
too; go2rtc's signalling is one request/response and there is nowhere to renegotiate to.

Restarts are budgeted at three, widening 2s → 4s → 8s, reset only by a session actually reaching
`live`. Signalling is a `POST /api/cameras/{id}/webrtc` per attempt, so a camera that is genuinely
gone would otherwise be an open loop for as long as the screen stays open. A resume is exempt: it
refills the budget and restarts immediately, because it is a person asking rather than a failure.
`WebRtcView` restarts on resume when the session reports an unhealthy connection state **or** the
App was away longer than ten seconds — the second condition covers the phone whose peer connection
is dead and has not been told.

A restart is a new session, so it takes the microphone with it: `_restart()` republishes
`MicStage.closed` on the way through, because the new session's own gate only reports on a press and
nothing else would correct a talk button still claiming the old one was open. Switching cameras goes
through the same path for the same reason.

None of that section is reachable from `flutter test`: `SampleServalRepository.canStreamLive` is
false precisely so tests never construct a peer connection. It is verified on a device.

## Still not done: pausing live video when the App is hidden

The App notices being hidden; it does not yet *stop* anything. A backgrounded tab holds its WebRTC
connection open and keeps draining every camera's JPEG off `WS /api/dashboard` for as long as it is
open. The wall socket is opened once in `LiveServalRepository.start()` and closed only when the
repository is disposed, which never happens.

Two things would pause, and they are worth very different amounts:

- **The focused WebRTC view is the real saving**, because go2rtc pulls RTSP lazily (above): closing
  the peer connection stops the upstream pull as well as the browser's decode. `WebRtcView` now has
  `_restart()`, which is most of the work — a pause is that without the reopen — and the resume path
  it needs already exists.
- **The wall socket costs bandwidth, not Server CPU.** Those JPEGs are encoded by ffmpeg
  unconditionally at `Ingest:SnapshotFps` to feed `CameraVisionPipeline` and `/snapshot.jpg`, so
  closing the socket saves egress and the client's decode and nothing else. Gating the encode itself
  on whether anyone is watching means reaching back into `StreamIngestManager` and `RecordArguments`,
  and contending with those two non-UI consumers — a separate and much larger job.
  `DashboardSocket` would need a `pause()`/`resume()` pair; `close()` cannot be reused, as it closes
  the broadcast controllers one-way and would kill the repository's `frames` subscription with them.

**The events socket is never paused.** `WS /api/events` is the alerting path and is nearly free.

About ten seconds of grace before pausing keeps an alt-tab round trip from churning the socket and
renegotiating WebRTC for a glance at something else — the same figure `WebRtcView` already uses to
decide whether a resume was long enough to be worth rebuilding for. There is nothing visible to opt
out of, so this wants to be unconditional rather than a preference.

Independent of all of the above, and with the same trigger: `DashboardEndpoint.SendAsync` has no send
timeout and the endpoint has no drain loop, so a client that stops reading without closing — a frozen
tab, a phone off the network — can wedge the send loop indefinitely. `AudioLevelsEndpoint` already
solves this, with a per-send timeout and a drain that observes the close frame.

## Two-way talk-back

A camera with an audio backchannel (ONVIF Profile T / RTSP backchannel) can play a viewer's voice
through its speaker. This rides entirely on the **existing WebRTC session** — the browser declares a
*sending* audio m-line in the SDP offer it already sends to `POST /api/cameras/{id}/webrtc`, and
go2rtc routes that audio to the camera's backchannel. There is no separate talk-back endpoint or
media path; the same signaling proxy and the same direct browser ↔ go2rtc media carry it.

**The microphone is not opened until you press the button.** The m-line goes out with no track on
it, and the first *Hold to talk* calls `getUserMedia` and attaches the result with `replaceTrack` —
which touches no SDP, so nothing renegotiates. Two things worth knowing before debugging this
against go2rtc:

- A trackless `sendrecv` transceiver still writes `a=ssrc` and `a=msid` into the offer, provided it
  is declared against a stream (`createLocalMediaStream`); declared without one it writes `a=msid:-`
  instead. So the offer's shape does not change when the track is deferred.
- **No RTP flows on that SSRC until the first press.** This is the real behavioural change: a
  disabled track still transmits, so the old always-attached microphone meant go2rtc saw a
  continuous silent stream from the moment the connection came up, and now it sees nothing until
  someone speaks. If talk-back stops reaching the camera at all, suspect this first: go2rtc would
  have to be deciding there is a backchannel to wire up from the offer's media direction rather
  than from the first packet to arrive.

It's **opt-in per camera** via `twoWayAudio`, and for a good reason: go2rtc probes the backchannel
by default, and that probe *breaks* some cameras (certain doorbells drop the whole stream). So the
sync worker registers every camera's source with `#backchannel=0` (backchannel off) **unless**
`twoWayAudio` is set, in which case go2rtc's default backchannel stays on. Toggling the flag
re-registers the stream. Two caveats from the browser side: it grants microphone access **only over
HTTPS**, and the camera must actually support a backchannel (go2rtc's *stream probe* tells you).

Nothing captures or transcribes **your** side of it. The outbound audio goes to the camera and is
not recorded, so it appears in no transcript and no activity row — the app's feed is only ever what
Serval heard, never what it said.

## PTZ control (ONVIF)

Set the camera's `onvifUrl` (its ONVIF **device service**, e.g.
`http://192.168.1.50/onvif/device_service`) plus `onvifUsername`/`onvifPassword`, and the PTZ
endpoints listed in the [Server README](../Server/Serval.Server/README.md#api) light up.

**A configured camera is not a capable one.** `camera.ptzConfigured` means only that an ONVIF URL
is set — a fixed-lens pan/tilt dome and a motorised zoom both answer true, and a client drawing its
controls from that flag offers a zoom slider that does nothing. `/ptz/capabilities` asks the camera
instead (ONVIF `GetNodes` + `GetPresets`) and answers:

```json
{ "panTilt": true, "zoom": false, "absoluteZoom": false, "home": true, "maximumPresets": 16,
  "presets": [ { "token": "1", "name": "Gate" }, { "token": "2", "name": null } ],
  "profileToken": "Profile_1", "nodeToken": "PTZNodeToken0", "probedAt": "..." }
```

`panTilt` and `zoom` are decided on the **continuous velocity** spaces, because that is how both
axes are driven. `absoluteZoom` is the one exception and reads the absolute zoom space: zoom is the
axis with a position worth showing, so a camera that can be told where to go is sent there rather
than nudged towards it. Pan and tilt stay velocity-only — the pad is a direction, not a
destination, so an absolute pan/tilt space would be a capability nothing here would call.
A preset's `token` is what `/ptz/preset` takes; the name is optional in ONVIF and often absent, and
an entry with no token is dropped rather than given a synthesised one.

It is a **separate route rather than a field on the camera record** because probing is a live SOAP
round trip: the App awaits `GET /api/cameras` before its first frame, so folding this in would put
an ONVIF timeout per unreachable camera on every cold start. Results are cached for
`Ptz.CapabilityCacheMinutes` (10) — short, because presets change the moment somebody saves one on
the camera's own web UI and a server restart is not an acceptable way to notice. Failures are not
cached at all. Pass `?refresh=true` to re-probe.

Velocities are −1..1 (positive pan = right, tilt = up, zoom = in), clamped server-side. The UI
pattern is *press-and-hold*: re-send `move` on a repeat while a button is held, `stop` on release.
As a safety net every `move` also carries an ONVIF auto-stop `Timeout` (`Ptz.MoveTimeoutSeconds`,
default 1s), so a dropped connection or a missed `stop` can't leave the camera spinning.

### Where the lens actually is

`GET /ptz/status` is a live ONVIF `GetStatus`, never cached — the point of it is that it disagrees
with what was last commanded:

```json
{ "cameraId": "front-door", "zoom": 0.42, "pan": -0.15, "tilt": 0.0, "readAt": "..." }
```

`zoom` is 0..1 and `pan`/`tilt` are −1..1, in ONVIF's **generic position spaces**. Any axis can be
`null`, and three real cases produce it: `PTZStatus/Position` absent entirely (it is optional and
cameras omit it freely), the axis absent from a position carrying the other one (a fixed lens on a
moving head), and a `space` attribute naming a vendor space whose range the specification does not
define — scaling that onto a 0..1 track would be inventing the scale. **Null means unknown, never
zero**: a zoom knob resting at the wide end is a claim, and it is wrong exactly when the lens is
zoomed in.

This is what makes the zoom track a control rather than a guess. It gives three tiers:

| The camera reports | How zoom is driven | What the track means |
|---|---|---|
| `absoluteZoom` | `POST /ptz/zoom` with a position | Where the lens is; survives reopening |
| a `GetStatus` position only | `POST /ptz/move` velocities, then re-read | Where the lens is, after it settles |
| neither | `POST /ptz/move` velocities | Only what *we* asked for since the view opened |

The App shows a percentage of travel for the first two and **no figure at all** for the third,
because dead reckoning is a count of our own commands: it starts wrong the moment anything else
touches the camera and there is no way to notice.

**There is still no zoom factor, and there cannot be.** The generic zoom space is a fraction of the
lens's travel; nothing in ONVIF publishes the optical range that would turn 0.42 into `2.4×`, and
the curve between the two is vendor-specific and nonlinear. The `2.4×` the design drew is not
merely unsourced — it is not derivable.

### The SOAP layer

Under the hood the server speaks ONVIF SOAP directly — no library. Each call is a SOAP 1.2 envelope
with a **WS-Security UsernameToken**: the password is sent only as a `Base64(SHA1(nonce + created +
password))` digest, never in clear. The camera exposes only its device service, so the PTZ service
address and a PTZ-capable media profile are **discovered** (GetCapabilities → GetProfiles) on the
first command and cached; pin a specific profile with `onvifProfileToken` if discovery picks wrong.
An ONVIF failure (camera offline, SOAP fault) surfaces as **502**. Cameras that use HTTP Digest auth
instead of WS-Security aren't handled.

## What the camera says it is

```
GET /api/cameras/{id}/device-information[?refresh=true]
→ { "manufacturer": "Reolink", "model": "RLC-810A", "firmwareVersion": "3.1.0.956",
    "serialNumber": "…", "hardwareId": "…", "readAt": "…" }
```

ONVIF `GetDeviceInformation` against the device service — so it needs no PTZ and no media profile,
and a camera with no pan/tilt at all still answers. Cached per camera until its ONVIF settings
change, with no TTL: make, model and serial never change, and firmware changes about as often as
somebody flashes the camera, so `?refresh=true` covers it.

Every field is optional in practice. ONVIF requires all five and cameras omit them anyway, so a
null means *the camera did not say* and should be rendered as absence rather than as "unknown".
A make that is obviously a placeholder is dropped: Reolink's E1 Pro firmware answers the literal
string `Manufacturer`, observed live.

There is **no uptime**: ONVIF's Device service exposes the system clock, not how long the device
has been running.
