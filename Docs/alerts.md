# Alerts

An alert is a detection somebody asked to be told about — `DetectionDocument.IsAlert` and
`SoundDocument.IsAlert` — and the alert queue is where those are displayed.

The queue is deliberately **not** a filtered view of the activity
feed. A queue is read top to bottom, cleared by hand, and arrives whether or not anybody asked for
it that morning. A feed is browsed. That difference decides nearly everything below.

## What an alert is made of

```
alerts (Mongo)                        the queue: state, and what to draw
{Media.Root}/{Media.AlertsRoot}/
    {alertId}.mp4                     a short preview clip, with audio
    {alertId}.jpg                     the frame it fired on
```

The document is [`Alert`](../Server/Serval.Server/Alerts/Alert.cs). Its `_id` **is** the detection's
or sound's own id, which is what makes raising one idempotent — see below.

### Why it is not a flag on the detection

Four reasons, any one of which would be enough:

* **The queue is cross-camera and newest-first.** Every telemetry index leads with `camera_id`, so
  there was no index in the system that could serve "everything, newest first".
* **Alerts carry state that changes** — read, dismissed — on documents that are otherwise written
  once at episode close and never revised.
* **Object and sound alerts interleave in one list**, and they live in two collections.
* **An alert owns files**, so it needs a lifetime of its own. Nothing prunes telemetry at all.

The episode goes on living in `detections` with its full track. The alert holds only what the queue
draws.

## The preview clip

This is the part that needed new machinery.

**Nothing can reconstruct the seconds before a detection after the fact.** Detect frames are deleted
as they are read, `SnapshotBroadcaster` keeps one JPEG per camera, and `FrameRing` holds a handful
for the vision model. And a camera whose `Recording` switch is off has no footage anywhere — yet it
detects exactly the same, so its alerts need a preview as much as anyone's.

So each camera's detect stream is written to a **rolling ring** on disk, and the alert's clip is cut
out of it.

### The ring

[`PreviewRing`](../Server/Serval.Server/Ingest/PreviewRing.cs) adds a third output to
[`FfmpegSnapshotSession`](../Server/Serval.Server/Ingest/FfmpegSnapshotSession.cs):

```
preview.m3u8                          the window, not the session
preview-init-{stamp}.mp4              fMP4 init
preview-{stamp}-00000.m4s             segments, pruned by ffmpeg
```

`-c:v copy`, so it is a mux and not an encode: no decode, no encoder, a few hundred kilobytes a
second off a 640×360 sub stream. `-hls_list_size` is set from `Ingest:PreviewBufferSeconds` and
`hls_flags` carries **`delete_segments`** — the exact inverse of the recording output, which omits
the flag so nothing it writes is ever lost. Here ffmpeg owning the
pruning is the point: the ring is bounded at its source, so there is no janitor to write and no way
for a camera to fill a disk with footage nobody asked to keep.

Audio is mapped optionally and **always re-encoded to AAC 64k mono**, which is the opposite of the
recorder's rule. Copying it would mean a second ffprobe this session otherwise never runs — capable
of stalling fifteen seconds before detection starts — and then handling G.711, which fMP4 cannot
carry at all. One mono AAC encode is a fraction of a percent of a core in a session already decoding
every frame for snapshots.

**Which cameras get a ring**, and it needs no new condition —
[`StreamIngestManager`](../Server/Serval.Server/Ingest/StreamIngestManager.cs) already starts
`FfmpegSnapshotSession` exactly when the detect stream is not the one being recorded:

| Camera | Ring? | Preview cut from |
|---|---|---|
| main `[record,live]` + sub `[detect]` | yes | the ring |
| the same, `Recording` off | yes | the ring |
| one stream `[record,detect,live]`, recording on | no | the recording index |
| one stream, `Recording` off | yes, that stream | the ring |

The third row is the interesting one: writing a second identical copy of bytes already on disk would
double a main stream's disk traffic to gain nothing, so the recording is the ring.

A detect stream ffmpeg cannot copy — mjpeg — gets **no ring and one log line**, never a failed
session. This process's actual job is producing frames for detection; failing it over a preview
buffer would cost that camera its detection, its wall tile and its vision model.

### Where the ring's segment times come from

[`PreviewRingIndex`](../Server/Serval.Server/Recordings/PreviewRingIndex.cs), in memory rather than
in Mongo. A ring segment lives for `PreviewBufferSeconds` and is then deleted, so indexing it would
mean an insert and a delete every few seconds per camera, forever, to describe files already gone by
the time anything could query them. A restart loses the index — and loses the ring with it, since
`PreviewRing.Reset` clears the last session's files at start, so there is nothing a persisted index
could have pointed at.

Start times accumulate from the **session anchor**, the same one the detect frames are timed
against, which is what lets a preview actually contain the moment its alert fired. `#EXTINF` is the
only honest duration under `-c:v copy`, where a segment is as long as the camera's GOP made it — the
same reason the recording index re-reads its playlist. Unlike a recording, the playlist here is a
window rather than the whole session, so the accumulation is carried across passes: what has fallen
off the front is gone from the file but its duration still counts.

### Cutting

[`AlertClipWorker`](../Server/Serval.Server/Alerts/AlertClipWorker.cs), one at a time beside
`ClipWriteWorker` and for the same reason — ten cameras alerting at once must not become ten
ffmpegs.

Each alert is queued with a due time of `at + AlertPostRollSeconds + 1s`, because the footage does
not exist before then. Every item waits the same fixed delay, so the channel's arrival order is
already due order and the worker is a `Task.Delay` rather than a priority queue.

The cut itself is [`ClipExporter.WriteFileAsync`](../Server/Serval.Server/Media/ClipExporter.cs) —
existing, `-c copy`, `+faststart`, and it already truncates at a session boundary. The poster is
`ClipMedia.TryWritePosterAsync` seeking to `peak_at`, **not** the clip's midpoint the way a saved
clip's poster does: the alert card labels it *the frame it fired on*, and it is the frame the
bounding box describes.

Segments only cut on keyframes, so a 5 s + 15 s preview is usually longer than 20 s, by however long
the camera's GOP is. `clip_seconds` reports what was actually written.

### When there is no clip

`clip_state` distinguishes four outcomes, and only one of them is an error:

| State | Means |
|---|---|
| `pending` | Queued; the footage after it has not been written yet. Resolves within the post-roll. |
| `ready` | Cut, with a poster. |
| `unavailable` | There was no footage and there never will be — the ring had rolled past, the camera has no ring, or a restart happened while it was pending. |
| `failed` | ffmpeg went wrong. `clip_error` says how. |

`unavailable` is ordinary rather than wrong, which is why it is not `failed`: a camera whose sub
stream cannot be copied lands there every time, and the card has a shape for it.

On startup the worker resolves every alert a previous run left `pending` — bounded to those raised
*before* this process started, so an alert raised during boot is not mistaken for a stranded one.

## Raising

Object alerts are raised from
[`CameraAiCoordinator.StoreAsync`](../Server/Serval.Server/Ai/CameraAiCoordinator.cs), **while the
episode is still open** — above the early return that skips storage. Waiting for the close would
delay the queue by `AbsenceSeconds`: half a minute after the thing somebody is being told about, by
which time the footage is rolling out of the buffer the clip is cut from.

Whether an episode is an alert is settled in two halves, and only one of them at the open. Whether it
*may* be one — an arrival, of a class in `AlertClasses` — is decided there and never revisited.
Whether it was ever seen clearly enough is asked again on every measured frame until it passes, then
never again. So an alert can be raised several seconds into a sighting, which is the point: the frame
an episode opens on is the one where a subject is furthest away and least certain, and judging them
there made examining them early a penalty. See the alert rule in
[detection.md](detection.md#the-alert-rule).

That path runs once a frame for as long as the object is in view, which is why the alert's `_id` is
the episode's: all but the first insert are a duplicate key, and
[`AlertRepository.RaiseAsync`](../Server/Serval.Server/Alerts/AlertRepository.cs) reports which one
it was so only the first queues a clip. An episode continued past `MaxEpisodeSeconds` carries a new
id and so raises its own alert, which is intended — a presence long enough to be cut in half is
reported as two records, and both are true.

The consequence to keep in mind: `peak_at` and `box` are the best frame **so far** rather than the
best of the whole episode. That is the right picture anyway — the card says "the frame it fired on".

Sound alerts are raised from [`CameraAudioDetector`](../Server/Serval.Server/Ai/CameraAudioDetector.cs)
after the sound is stored. A sound is a single moment rather than an episode, so it fires once and
needs no deduplication. **It gets a video clip like anything else**: "glass at the back door" is
something you want to look at, not only listen to. It carries no box, because a sound has no place
in the frame.

Neither raise site does anything but write one small document and drop an id in a channel. The
ffmpeg happens later, elsewhere, and a database that is briefly unavailable costs an alert rather
than a camera's detection.

## Titles

Composed by [`AlertTitle`](../Server/Serval.Server/Alerts/AlertTitle.cs) and **stored**, not
composed by whichever screen is drawing the row. Every alert is a thing somebody is told about, and
what the notification said and what the queue says have to be the same words.

`Person at Front door`. `Glass heard at Back yard`. On the preposition: the natural reading varies
with the place a camera points at — "at the front door" but "in the driveway" — and nothing knows
which a name wants, so this uses the one construction that works with any of them. An AudioSet label
that lists its synonyms ("Smoke detector, smoke alarm") keeps only the first.

## Read, dismissed, and retention

Three different things, deliberately.

* **Read** is set when a card is opened. Nothing else sets it — a row scrolling past under your
  thumb is not you having read it. It drives the unread dot and the header's count.
* **Dismissed** takes a row out of the queue. The row and its files stay, so a dismissed alert is
  still reachable by id, and a push notification tapped after somebody else cleared the queue still
  lands somewhere.
* **Retention** deletes it. `Media:AlertRetentionDays`, default 14, swept by
  [`AlertRetentionWorker`](../Server/Serval.Server/Alerts/AlertRetentionWorker.cs) on the recording
  sweep's cadence.

Retention is independent of dismissal, and longer than recording retention by default. The point of
a preview is that it outlives the footage it was cut from: an alert from last month still shows what
happened even though the recording went days ago. Clearing the queue is a statement about attention,
not about evidence.

That worker is separate from [`RetentionWorker`](../Server/Serval.Server/Recordings/RetentionWorker.cs)
rather than a branch inside it. That worker has exactly one rule — it deletes only inside
`Root/{cameraId}`, and only filenames its index handed back — and the rule is what makes it safe to
reason about. Alert media lives outside every camera directory; ring files live inside one while
being in no index at all. Both are the cases that rule exists to exclude.

The same sweep removes `preview-*` files belonging to a camera with **no live ring** — one deleted,
disabled, or unable to start. A running session prunes its own, and `PreviewRing.Reset` clears the
last one at start, but nothing else ever reclaims the buffer of a camera that is not coming back.
Cameras with a live ring are skipped whole rather than by age: the init segment is written once at
session start and never touched again, so a camera up for a day has a day-old file every one of its
previews depends on.

## The API

`/api/alerts`, in [`AlertEndpoints`](../Server/Serval.Server/Alerts/AlertEndpoints.cs). Cross-camera
by default — the difference from every telemetry route, which is per-camera.

| Route | |
|---|---|
| `GET /api/alerts?cameraId=&limit=&before=` | `{ items, unread }`, newest first, excluding dismissed |
| `GET /api/alerts/{id}` | one, including a dismissed one |
| `POST /api/alerts/{id}/read` | |
| `POST /api/alerts/{id}/dismiss` | |
| `POST /api/alerts/dismiss-all?cameraId=` | |
| `GET /api/alerts/{id}/clip.mp4` | range-served; `MediaAccess`, so a `<video>` can use `?stream_token=` |
| `GET /api/alerts/{id}/poster.jpg` | the frame it fired on; `AlertImageAccess`, which also takes the one-alert token a notification's picture carries |

`unread` counts the whole queue rather than the page, so the header's figure stays true when the
rest is below the fold.

Alerts are also published on `WS /api/events` with `type: "alert"` — once when raised, and again
when the clip becomes ready so an open card can start playing. An App that does not know the type
ignores it, which is what keeps a newer Server from being an upgrade order.

## Settings

All four are in the catalog under **Alerts**, so they are editable from the settings screen.

| Key | Default | |
|---|---|---|
| `Serval:Media:AlertRetentionDays` | 14 | how long an alert and its clip are kept |
| `Serval:Media:AlertPreRollSeconds` | 5 | preview starts this far before |
| `Serval:Media:AlertPostRollSeconds` | 15 | and continues this long after |
| `Serval:Ingest:PreviewBufferSeconds` | 90 | the rolling buffer, which must comfortably exceed the two paddings |

Five seconds of pre-roll is the App's own `feedLeadIn` — "the difference between watching someone
walk up and finding them already at the door" — so a preview and a timeline seek to the same alert
start in the same place.

`PreviewBufferSeconds` reaches an ffmpeg command line, so it is part of
`StreamIngestManager.Signature`: changing it restarts the sessions rather than being a value stored,
reported, and in force everywhere except the process writing the ring.

What *counts* as an alert is elsewhere, and unchanged: `Detection:AlertClasses`,
`Detection:AlertMinConfidence` and `Sound:AlertLabels`, per-camera overridable. See
[detection.md](detection.md) — in particular the note about all of those lists defaulting to empty.

## The screens

`/alerts` sits in the left navigation rail alongside the camera wall and saved clips, showing an
unread dot rather than a numeric badge (on narrow mobile screens without a rail, the bell icon and
dot appear in the wall's top app bar).

`AlertsScreen` renders rows grouped by day (`Yesterday · seen` once every alert in a day has been
opened). Seen rows dim visually in place until explicitly dismissed. `AlertScreen` (`/alerts/:id`)
displays a single alert and serves as the landing page when a user taps a push notification.

Every alert card plays its preview clip directly, even on cameras with `Recording` turned off (using
the rolling `PreviewRing` buffer). When a camera was not recording, *Watch it on <camera>* is
omitted and a `Not recorded` badge explains that there is no timeline archive beyond the preview.
Clip export is done from the camera timeline rather than from the alert card.

The detection bounding box is drawn **over** the poster at render time rather than burned into the
JPEG. Because `DetectionBox` uses normalised `0..1` coordinates, the same image serves list
thumbnails (`132×74` desktop, `112×64` mobile) and the full-width detail view with a crisp 1px
stroke at every size.

### Provisional posters before the clip finishes

An alert is raised as soon as the detector fires, while its preview clip is cut
`AlertPostRollSeconds + 1s` (~16 seconds) later. Without a provisional image, opening an alert or
tapping a push notification during that window would show only the UI's diagonal-stripe placeholder
background behind a floating bounding box.

To avoid that gap, `AlertService.RaiseAsync` immediately writes `SnapshotBroadcaster.Latest` (already
JPEG-encoded in memory) to the alert's poster path via `AlertClipWorker.EnqueuePoster`. Once the
preview clip finishes encoding, `AlertClipWorker` overwrites that provisional poster with the exact
`peak_at` frame extracted from the clip — **unless the segment clock offset falls outside the clip**.
`posterAt` is computed as `peak_at` minus the clip's first segment start; if `PreviewRing`
re-anchored after a stream gap and made that offset negative, clamping to `0` would extract a frame
`AlertPreRollSeconds` *before* the detection. When the offset falls outside the clip, the worker logs
the mismatch and keeps the provisional snapshot instead.

Four details follow from this lifecycle:

- **`poster.jpg` is gated on file existence, not on `clip_state == Ready`.** Serving the file as
  soon as it exists covers the `pending` window and also gives `unavailable` alerts (e.g., when a
  camera has no `PreviewRing`, the ring rolled past, or a restart interrupted cutting) a still image.
- **`pending` means poster available, clip still cutting**, matching the UI's *Clip in a moment*
  badge.
- **The App's poster request URL includes `clip_state` as a cache-buster**, so `Image.network`
  fetches the exact `peak_at` frame once `clip_state` transitions from `pending` to `ready`.
- **`recorded` is `null` while `pending`, not `false`.** Whether the alert's timestamp is covered by
  permanent recordings is resolved when the clip settles (after the active recording segment has
  closed and been indexed). Clients check `recorded == false` before showing `Not recorded` and
  `recorded == true` before showing *Watch it on <camera>*.

Because snapshots run at `Ingest:SnapshotFps` (~1 fps) while `box` comes from the exact detection
frame (`Ingest:DetectFps`), the provisional poster can trail a fast-moving subject by up to ~1 second
until the clip settles. Encoding the raw `DetectFrame` synchronously during `RaiseAsync` is avoided
so JPEG compression never slows down the detection loop; `AlertService` logs `peak_at - CapturedAt`
at `Debug` level on every raise to make that delta observable.

### Live updates on open alert screens

Because a tapped notification typically opens `AlertScreen` while the clip is still `pending`, both
`AlertScreen` and `AlertsScreen` subscribe to `ServalRepository.alertUpdates` (fed by `WS /api/events`).
When `AlertClipWorker` republishes the settled alert, the poster refreshes and the video player
activates in place without requiring navigation.

## Push notifications

Each raised alert enqueues one Web Push notification carrying the server-composed `title` verbatim,
ensuring the push banner and `/alerts` queue always match.

**Notifications are enqueued from `AlertService.RaiseAsync`, not from `EventBroadcaster`.** Calling
`AlertNotifier.Enqueue` inside the duplicate-key guard ensures each episode triggers at most one
push notification (whereas `EventBroadcaster` sees every alert twice: on raise and on clip settle)
and returns immediately via a bounded channel without blocking detection.

**Notifications attach the alert's `poster.jpg`, not live `snapshot.jpg`.** Because browsers fetch a
notification's image when rendering the banner rather than when the event occurred, pointing at live
`snapshot.jpg` would show an empty scene whenever delivery lagged by a few seconds. Pointing at
`poster.jpg` serves the provisional snapshot immediately and the exact `peak_at` detection frame for
any notification rendered after the clip settles.

Browsers fetch that image with no `Authorization` header available to them, so the URL carries a
`?stream_token=`; it rides inside the encrypted payload and the push service relaying it sees
nothing.

**That token is not an ordinary stream token, and it cannot be.** A stream token lives
`Auth:AccessTokenMinutes` — ten — while `Push:TtlSeconds` is 600, so a message a push service held
for a sleeping phone was routinely fetched with a credential that had already expired. The
notification then showed no picture at all, which is the whole of the "sometimes there is no image"
complaint. Making a *general* stream token last a day would hand out a day of access to every media
route on the deployment.

So `TokenService.CreateAlertImageToken` mints one carrying an extra `alert` claim naming the single
alert it may fetch, and `Serval:Push:ImageTokenHours` (default 24) sets its life. Two halves enforce
it, and both are needed:

* `MediaAccess` **refuses** any principal carrying an `alert` claim. Central, so a route added to
  that policy later cannot silently start accepting one.
* `AlertImageAccess` — that policy minus the refusal — admits, and `poster.jpg`'s handler compares
  the claim against the id it already binds. It answers **404** on a mismatch rather than 403: the
  one thing a one-alert token's holder must not learn is which other alert ids exist, and the route
  already answers the same way for a poster that is not there.

The comparison is in the handler rather than in the policy for two reasons. A policy would have to
read the route value off `AuthorizationHandlerContext.Resource`, which is the `HttpContext` only by
a .NET 9 default that a single runtimeconfig switch reverts; and a policy cannot answer 404, which is
the answer this route owes a token asking about somebody else's alert.
`EndpointRoutingTests.AlertMediaRoutesTakeAStreamToken` pins `AlertImageAccess` to exactly that one
route, so a second route joining it fails a test and its author finds out that the comparison is
per-handler.

**Both policy bodies live in `Auth/MediaPolicies.cs`, not inline in `Program.cs`.** The refusal is
the entire security boundary around a day-long credential, and if it were dropped nothing would
break, no test would fail and no log line would appear — every media route would simply start
accepting one. A rule stated in a top-level statement is a rule nothing in this project can reach, so
the two bodies are a class `MediaPolicyTests` can build and evaluate against a real
`IAuthorizationService`.

It keeps `scope=stream` and adds a claim beside it rather than inventing a third scope *value*, and
that is load-bearing: `Program.cs` tells its two bearer schemes apart with a boolean, so a token
carrying some other scope reads as *not* a stream token, is refused by the `StreamToken` scheme and
is **accepted by the default one** — a picture credential quietly becoming a full session. The same
trap is written up at length on `CameraStreamTicketService`, which escaped it by not being a JWT;
that was not available here, because an in-memory ticket dies on restart and a notification outlives
one. What is given up is revocation, and the `alert` claim is the whole bound on that.

**Tap behavior is covered above** — see *Provisional posters before the clip finishes* and *Live
updates on open alert screens*. A tapped notification opens on the provisional poster and transitions
to the settled clip in place.

**Four links decide who is told.** The deployment's `Serval:Ai:Detection:AlertClasses`, then the
camera's own override — those two run in the detection loop and decide whether a row exists at all
— then each person's per-camera rules on `UserPreferences`, and last the cooldown. The third link
can only *narrow*: a notification needs an alert to carry it, so a class the camera does not alert
on produces no row and no amount of asking for it conjures one. The notifications screen draws the
camera's effective alert classes as the menu for that reason, and offers no way to type one in.

**The fourth is the only one about *when*.** `NotificationCooldown` holds back a notification about
something this person was told about within `Serval:Push:CooldownSeconds` — two minutes by default,
matching `NoveltySeconds` because the two are the same question from opposite ends. It is keyed on
account, camera, kind and label, so a car arriving while somebody is walking about still goes out,
and a barking dog does not silence a visible one. Each person overrides it per camera on the
notifications screen; null inherits and zero sends every alert.

The reason it lives at this layer and not in `ObjectEventPolicy` is the thing to hold on to: **a
held notification is a phone left alone, not an alert that did not happen.** The row is written,
the clip is cut, the unread dot appears, and `/api/alerts` shows both. Somebody walking out of shot
and back is genuinely a second arrival — the arrival gate is spatial, so returning to a different
part of the frame is new — and the queue should say so. What it is not is a second thing worth
reaching for a phone about.

The state is a dictionary on the notifier, not a column. It is lost on restart, which costs at most
one extra notification per camera and label, and that is cheaper than a write per alert for
something whose whole lifetime is two minutes.

**Delivery is standard Web Push**, RFC 8291 payload encryption with RFC 8292 VAPID auth,
implemented against the BCL in `Server/Serval.Server/Push/` with no dependency. Not FCM: Firefox and
Safari never touch it, and Firebase's web SDK is a wrapper over this same browser API. FCM and APNs
become *additional transports* when a native mobile app arrives — hence the `Transport` field on a
subscription row.

**It needs HTTPS.** Service workers and `PushManager` are withheld outside a secure context, so on a
plain-HTTP deployment the notifications screen explains itself and offers nothing. See
[deployment.md](deployment.md#tls-and-exposure).

### The notifications screen

`/settings/notifications` is a per-user preferences screen rather than an Admin-only deployment
page: any signed-in role can access it, and rules apply to that user's own account. *What I'm
alerted on* in the `/alerts` header links directly to it.

The screen has two sections:

- **May this browser notify me** — reports the current browser's Web Push state (unsupported/HTTP
  origin, browser permission denied, disabled server-wide, unregistered, or registered) and lists
  every browser device registered to this account with its last-notified timestamp.
- **What am I told about** — one card per camera (three columns on desktop, single column on mobile)
  listing that camera's effective alert object classes and sound labels, with an immediate per-label
  toggle and per-camera mute/cooldown controls.

## Not built

- **Updating a push banner after the clip settles.** Push fires once on raise. While `sw.js`
  supports a `quiet` payload flag mapped to `renotify`/`silent`, the server does not currently send a
  second silent push when `AlertClipWorker` finishes extracting the exact `peak_at` poster (only the
  in-app `/api/events` stream receives the settled update).
- **Quiet hours and per-device filtering.** Notification rules and mute state are stored per user
  account rather than per browser subscription, so a user signed in on both a phone and a laptop
  receives the same alerts on both.
