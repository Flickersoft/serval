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
`hls_flags` carries **`delete_segments`** — the exact inverse of the recording output, which sets
`hls_list_size 0` and omits the flag so nothing it writes is ever lost. Here ffmpeg owning the
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
episode is still open** — above the early return that skips storage. Whether an episode is an alert
is decided when it opens and never revised, so waiting for the close would delay the queue by
`AbsenceSeconds`: half a minute after the thing somebody is being told about, by which time the
footage is rolling out of the buffer the clip is cut from.

That path runs once a frame for as long as the object is in view, which is why the alert's `_id` is
the episode's: all but the first insert are a duplicate key, and
[`AlertRepository.RaiseAsync`](../Server/Serval.Server/Alerts/AlertRepository.cs) reports which one
it was so only the first queues a clip.

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
| `GET /api/alerts/{id}/poster.jpg` | the frame it fired on |

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

Design round 14. `/alerts` is a peer of the wall and of saved clips, third on the rail with a
boolean dot rather than a count — at 64px a number is unreadable, and the question the rail answers
is whether there is anything. On a phone there is no rail, so the wall's app bar carries the bell
and the dot.

`AlertsScreen` is 14a wide and 14b on a phone: rows rather than the cards saved clips use, grouped
by day, with the heading saying `Yesterday · seen` when a whole day has been read. Seen rows drop
back but stay — clearing the list is a decision rather than something time does. `AlertScreen` is
one alert, and where a tapped notification lands: one destination however you arrive.

**Round 14 drew a still; the card plays a clip.** The design predates the preview buffer, and 14d
was drawn as a dead end — a still, and a card explaining that the still was all there was. It is
not, any more. A camera that was not recording gets an ordinary card whose preview plays like every
other; the only difference left is that there is no recording to go on to, so *Watch it on <camera>*
is absent and a dashed `Not recorded` chip says why. There is still no export on this screen, which
*is* from the design and stands: a clip is made in the recording, and this screen's job is to put
you there.

The box is drawn **over** the poster rather than burned into it. `DetectionBox` is normalised, so
one picture serves the 132×74 row thumbnail, the phone's 112×64, and a full-width hero, with the box
a hairline at every size.

### The poster arrives before the clip

An alert is raised the moment the detector fires and its clip is cut sixteen seconds later, so for
that whole window there was nothing to draw — the card fell back to the camera's stripe with a
detection box floating on it, which is what a tapped notification looked like every single time.

So the poster no longer waits for the clip. `AlertService.RaiseAsync` reads
`SnapshotBroadcaster.Latest` — already encoded, already in memory — and hands it to
`AlertClipWorker.EnqueuePoster`, which writes it to the alert's poster path. `AlertClipWorker` then
overwrites it with the exact detection frame when the clip settles. Four consequences worth
knowing:

- **`poster.jpg` is gated on the file, not on `clip_state`.** Gating on `Ready` would 404 the
  stand-in for the whole window it exists to cover. It also means an `Unavailable` alert — one there
  was never any footage to cut — now has a picture, where before it had the stripe forever.
- **The clip states did not change.** `pending` simply comes to mean *poster yes, clip no*, which is
  what the *Clip in a moment* pill already said.
- **The poster URL carries the clip state**, and it has to. `Image.network` caches by URL, so
  without something that moves when the file does, the approximate picture is the only one the App
  would ever show — and nothing would look wrong, which is what would make it hard to find.
- **`recorded` is null for that window, not false.** It is settled with the clip, because at the
  raise the segment covering the alert has not closed or been indexed. Published as `false` it made
  every alert on a recording camera wear the `Not recorded` chip for its first sixteen seconds and
  then quietly take it back, so a screen watched live disagreed with itself minute to minute. Both
  the App and anything else reading the queue have to ask `== false` for the chip and `== true` for
  *Watch it on <camera>*; null is neither, and the card says nothing about the recording.

The cost is that a snapshot is published at `Ingest:SnapshotFps` — once a second — while `box` is
measured against the exact frame. So the box can sit slightly off a fast-moving subject until the
clip settles. The exact fix is to encode the `DetectFrame` the detector actually ran on, which is in
memory at the raise; it is not done because that is a JPEG encode next to the detection loop, and
that loop is the one thing nothing may slow down.

### The screen a notification lands on updates itself

A tap arrives about a second after the raise, so `AlertScreen` opens on an alert whose clip is still
being cut. It subscribes to `ServalRepository.alertUpdates` for its own id, which carries the
republish `AlertClipWorker` makes when the clip settles; the poster is re-fetched at the new state
and the player goes live. `AlertsScreen` does the same for a row already listed. Without it the
screen held whatever it had at first paint until you navigated away and came back.

## Push notifications

The design's premise was that every row was already a push, and it now is. One notification per
alert row, carrying `title` verbatim — which is why that field is composed once on the server and
frozen: a notification and the queue entry it belongs to cannot disagree about what happened.

**The send hangs off the raise, not the event bus.** `AlertService.RaiseAsync` calls
`AlertNotifier.Enqueue` *inside* the branch the duplicate-key check guards, so the once-a-frame
object path costs nothing and each alert notifies exactly once. Subscribing to `EventBroadcaster`
instead would see every alert twice — once on raise, again when the clip settles. Enqueue takes a
slot in a bounded channel and returns; the caller is the detection loop and nothing may make it
wait.

**The picture is the camera's live snapshot, not the alert's poster.** `poster.jpg` is cut from
footage that does not exist yet — it 404s until the clip settles, some sixteen seconds later —
while the push goes out at the moment of the raise. `/api/cameras/{id}/snapshot.jpg` is served from
memory and is within a second or two of the frame that fired. Browsers fetch a notification's image
themselves with no `Authorization` header available to them, so the URL carries a per-recipient
`?stream_token=`; it rides inside the encrypted payload and the push service relaying it sees
nothing.

That token only works because `snapshot.jpg` is on the `MediaAccess` policy, which is the one thing
about this route that is not obvious from reading it — it was on the default policy at first, which
does not look at the query string at all, and the App's own calls set a header and so never noticed.
`EndpointRoutingTests.MediaRoutesTakeAStreamToken` pins the policy of every media route for that
reason.

**What the tap lands on is covered above** — see *The poster arrives before the clip* and *The screen
a notification lands on updates itself*. Between them, a tapped notification opens on a real picture
and fills in the clip without a navigation.

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

Design round 15 — 15b wide, 15c on a phone. `/settings/notifications` is the one settings page that
belongs to the person rather than the deployment: any role reaches it, nothing on it is
administrative, and two people signed into the same house see different answers. *What I'm alerted
on* in the alert queue's header is the way in.

It asks two questions and draws nothing else. **May this browser notify me** is one card whose
headline is whichever of five things is true — no push machinery, refused, switched off
deployment-wide, not registered, or allowed — because the cause is different in each and only the
last two are fixable from the page. Under it sits a chip per browser the account has registered,
each carrying when it was last reached; one that never has is drawn in the alert hue, since a device
that has never been notified is the visible symptom of most ways this goes wrong.

**What am I told about** is a card per camera, wrapping three across on a desktop and one on a
phone, holding only that camera's effective alert classes and sound labels. Its subtitle counts both
together — `7 of 7 on` — and a camera switched off says `Muted` instead. Writes land immediately;
there is no save bar, because every control here is one fact.

Two things the design drew are deliberately absent: a zone label on each card and a zone filter
beside the search. Serval has no notion of a zone or a group — a camera has a name and nothing
else — and a filter over a field that does not exist is a control that cannot work.

## Not built

**A second notification when the clip settles.** The push fires on the raise and carries a live
snapshot; the real poster and the playable clip arrive some seconds later, and no second push goes
out to say so. Re-notifying on the same `tag` would replace the notification silently, but it
doubles the volume for a picture almost nobody is still looking at.

The wire is ready for it. The payload carries a `quiet` flag and `sw.js` reads it into `renotify`
and `silent`, so a republish can update the card without interrupting anybody. Nothing sets it yet;
it shipped with the cooldown because a service worker updates on its own schedule, and a browser
running last month's `sw.js` would be loud for the first release that started setting it.

Only the notification is missing, though — see *The screen a notification lands on* above. The
alert itself is republished on `/api/events` when the clip settles, and the screens act on it.

**Quiet hours, and per-device rules.** The rules are per account: somebody with a phone and a laptop
gets the same alerts on both. Muting is all-or-nothing per person, via the master switch.
