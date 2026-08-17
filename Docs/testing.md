# Testing

Every suite here runs on a fresh clone with no models, no hardware and no network. Anything that
genuinely needs a model or a device is covered by a diagnostic you run by hand instead — the split
is deliberate and identical on both hosts.

## Server

```bash
dotnet test Server/Serval.Server.Tests
```

Runs in a moment and needs **no MongoDB, ffmpeg, or video**. The suite covers the
pieces decidable in memory: the HLS playlist builder and parser, the encoder selector, the ingest
planner (copy vs transcode vs refuse) and the record-argument builder — the pure functions in the
video path — plus the ffmpeg encoder-table parse, camera and transcode validation, how a camera is
stored in BSON, the snapshot broadcaster's fan-out, and — most importantly — the telemetry
contract, deserialising the module's exact published JSON.

Anything needing live infra (ffmpeg, RTSP, Mongo) is verified by running the server against a
file-source camera, not unit-tested.

### A camera with no camera

A camera may point at a local video file instead of an RTSP URL; ffmpeg loops it in realtime as
a stand-in, so the whole pipeline — segmenting, snapshots, the recording index, playback — runs
with no hardware. This is the server's equivalent of the module's `--replay`.

```bash
# a looped test clip as a pseudo-camera
ffmpeg -y -f lavfi -i testsrc=size=640x480:rate=15 -t 10 -c:v libx264 -pix_fmt yuv420p testcam.mp4

curl -X POST localhost:5211/api/cameras -H 'Content-Type: application/json' \
  -d '{"id":"testcam","name":"Test","streams":[
        {"name":"main","url":"'"$PWD"'/testcam.mp4","roles":["record","detect","live"]}]}'

curl -o snap.jpg localhost:5211/api/cameras/testcam/snapshot.jpg
curl "localhost:5211/api/cameras/testcam/vod.m3u8?from=$(date -u -d '-5 min' +%FT%TZ)&to=$(date -u +%FT%TZ)"
```

The clip above is H.264, so it is recorded by copy. To exercise the encode path instead, PUT the
camera back with a `transcode` on its record stream — `"transcode": {"codec":"vp9"}` makes the
served playlist advertise `codecs="vp09…"`. A file URL on the `live` stream is accepted but has
no WebRTC view; go2rtc cannot serve a file, and the startup sweep says so.

`snapshot.jpg` is served from memory only, so it 404s until ingest has published its first frame —
about a second. The frames themselves land in `<media>/<camera>/snapshots/` named for their position
in the stream, and the detector reads and deletes each one; a directory that is accumulating them
means nothing is consuming them.

### Two clocks that have to agree

A detection's timestamp and a recorded segment's timestamp are derived independently, and when they
disagree every box drawn over replayed footage is wrong by the difference. Both now anchor on their
session's start — segment starts accumulate real `#EXTINF` durations, snapshot times count frames at
`SnapshotFps` — so on a camera whose record stream also carries `detect` they are the same clock and
agree exactly.

Worth checking after any change to either path, and measurable without a detection model at all:
the moving-object fixture below encodes the clip's own time in the object's position, so measuring
it in a recorded frame and in a snapshot gives two independent readings of the same instant. On a
correct build the two agree to within the measurement noise; a regression shows up as a constant
offset between them.

Restarting ffmpeg is the other case worth exercising, because the on-disk `live.m3u8` — which the
recording index reads for segment ownership — outlives the run that
wrote it and `hls_list_size 0` means it holds every segment that run produced. Kill ffmpeg, let the
supervisor restart it, then confirm the new session's first segment is labelled at its own start,
that no segment's filename stamp disagrees with its `InitFileName`, and that
`max(StartedAt + DurationSeconds)` is behind `now` rather than ahead of it.

### Saved clips outliving the footage

The point of a saved clip is that retention cannot reach it, and that is one thing no unit test can
show. Against a file-source camera as above:

```bash
# Save a whole number of segments — the range a trimmed clip actually produces.
curl -X POST localhost:5211/api/clips -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" \
  -d '{"cameraId":"testcam","from":"...","to":"...","name":"Retention check"}'   # 202

curl -H "Authorization: Bearer $TOKEN" localhost:5211/api/clips/$ID/status       # until "ready"
ffprobe -v error -show_entries format=duration -show_entries stream=codec_type \
  -of default=noprint_wrappers=1 <media>/clips/$ID.mp4
```

Two things to check on that file, because both have been wrong: it carries **an audio stream as well
as video** (the camera needs `"recordAudio": true`, or the recording has none to copy), and its
duration is **exactly** the range asked for. A clip one segment longer than requested is
`RecordingIndex.InRangeAsync` including a segment that starts where the window ends.

Then age the index past the cutoff and let the sweep run:

```bash
mongosh serval --eval 'db.recordings.updateMany({}, {$set:{StartedAt: new Date(Date.now()-10*864e5)}})'
# with Serval__Media__RetentionDays=1 and Serval__Media__RetentionSweepMinutes=1
```

The camera's `.m4s` files go; `clips/$ID.mp4` stays and still plays. **This is the feature.**

Worth exercising alongside it: kill the Server mid-write and restart it — the clip left `writing`
must come back `failed` with its partial file removed, rather than sitting in that state forever.

## CameraModule

```bash
dotnet test CameraModule/Serval.CameraModule.Tests    # the module: outbox, sink, V4L2 ABI
dotnet test Shared/Serval.Ai.Tests                    # the shared library: gates, contract, conversations
```

Both run in a couple of seconds and need **no models, fixtures, or devices**.
Between them they cover the pieces that are decidable without a model: the two detection gates,
the ring buffer, the JSON output contract, the conversation-audio WAV round trip and crash
recovery, conversation boundaries and the stream-join math, the SQLite outbox, the V4L2 struct ABI,
and SenseVoice label parsing.

There is no `.sln` inside the module — `dotnet build` there builds just the worker. The repo root
carries `Serval.slnx`, which ties the module together with the server; `dotnet build Serval.slnx`
from the root builds everything.

## Multiple speakers, from the microphone to the screen

Everything else in the audio suite either supplies its own utterances or runs on synthetic samples,
so all of it passes on a pipeline that transcribes nothing. `--speakers` counts voices without
reading words; `SenseVoiceReferenceTests` reads words from a single speaker. Between them sits the
failure people actually report — the right words against the wrong person — and nothing watched it.

`ConversationOverFixtureTests` does, over sherpa-onnx's own two-speaker reference clips
(`1-two-speakers-en.wav`, 16s, and `2-two-speakers-en.wav`, 34s), which ship beside the segmentation
model and are known to be within what these weights handle.

**That last part is the whole selection rule.** Audio the models are not known to cope with produces
failures indistinguishable from ours, and a suite that cannot tell "we broke it" from "nothing could
do this" reports noise. Harder recordings belong in a measurement, not in a test that has to stay
green.

```bash
SERVAL_MODELS=~/serval-local/models \
  dotnet test Shared/Serval.Ai.Tests --filter ConversationOverFixtureTests
```

`SERVAL_MODELS` points at a directory in the layout `fetch-models.sh` produces; fixtures are found
under `speaker/fixtures` beneath it, or wherever `SERVAL_SPEAKER_FIXTURES` says. **With neither set
the suite still runs** — every case skips with a reason, which is the fresh-clone rule the rest of
this file keeps.

The clock is the one thing the harness substitutes. `ConversationReprocessor` recovers an
utterance's span by subtracting `Vad.MinSilenceSeconds` and its duration from its timestamp, so a
test that pushes thirty seconds through in two and stamps `UtcNow` collapses every turn onto one
instant — and attribution then fails for a reason that has nothing to do with the models. Utterances
are stamped at their true position in the stream instead: what a realtime host would have written,
computed rather than waited for.

### What the assertions are really for

Speaker counts are pinned, because the filenames are ground truth. The rest is structural — turns
arrive in order, with words in them, attribution has not collapsed onto one speaker — because
anything phrased in terms of specific words would be rewritten to match whatever a new model said,
which is not a test. The words themselves are pinned against an outside authority in
`SenseVoiceReferenceTests`.

One of those structural checks earned its place immediately. **No turn may carry more words than its
own duration could hold** (six a second; conversational English runs two to three). That is what
catches an utterance being attributed to a turn it does not fit in, and it found exactly that:

> A seventeen-second utterance spanned five diarized turns across both speakers. Its best match held
> 29.8% of it; the runner-up held 19.3%. `Attribute` asked whether the *runner-up* was at least 20%
> and a different speaker — it missed by seven tenths of a percentage point, so the utterance was
> judged "inside one turn" and all sixty of its words were stamped onto a single 5.04-second turn.
> The four other turns it covered received no text and were dropped from the record for being empty.
> On screen that is one speaker delivering a wall of text that includes the other person's lines,
> and half the conversation simply missing.

The fix asks about the winner instead — see `Speaker.ContainedOverlapFraction`. The reference clip
went from five turns to nine, and reads as a dialogue.

### To the screen

`App/serval_app/test/activity_conversation_fixture_test.dart` runs on every `flutter test` with no
models, over `test/fixtures/multi_speaker_conversation.json` — the verbatim output of the harness
above. Every other feed test hands the app documents written by hand to match what the test expects;
those agree with the parser by construction and with the pipeline only by luck. Regenerate it after
a pipeline change:

```bash
SERVAL_MODELS=~/serval-local/models \
SERVAL_TRANSCRIPT_GOLDEN_OUT=$PWD/App/serval_app/test/fixtures/multi_speaker_conversation.json \
  dotnet test Shared/Serval.Ai.Tests --filter Capturing
```

The fixture declares its own speaker count and the assertions read it, so re-baking is a command
rather than an edit. It is also the only test with real paired documents, which is what lets it
exercise the rule that a settled conversation *replaces* its own live utterances — the failure being
every line on screen twice, once live and once settled.

### The same fixtures through the running Server

The harness answers whether the models can read a recording. It cannot answer whether ffmpeg, the
detector loop, the reprocessing pass, Mongo and the REST reads carry the result out — so drive the
real thing with a file-source camera. Mux a clip with silence padded on the end so each loop closes
a conversation:

```bash
F=~/serval-local/models/speaker/fixtures
ffmpeg -y -i $F/2-two-speakers-en.wav -af apad=pad_dur=20 -ar 16000 -ac 1 /tmp/padded.wav
ffmpeg -y -f lavfi -i testsrc=size=640x360:rate=10 -i /tmp/padded.wav \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest /tmp/two-speaker-cam.mp4
```

Start the Server as in [browser-testing.md](browser-testing.md), adding the model paths and:

```bash
Serval__ServerAi__Enabled=true
Serval__Ai__Speaker__Enabled=true
Serval__Ai__Speaker__SilenceTimeoutMinutes=0.25
```

Register the clip as a camera with `"aiAudio": true` and a `detect` + `live` stream.

Two settings are load-bearing and neither is obvious:

- **`SilenceTimeoutMinutes`.** A file camera loops forever (`-stream_loop -1 -re`), so at the
  default three minutes the clip restarts long before the timeout fires and one conversation runs
  until `MaxConversationMinutes` — half an hour with no transcript. Fifteen seconds against twenty
  seconds of padded silence closes one conversation per loop.
- **The gate.** `AudioGate:RmsThreshold` defaults to 0.01. A quiet source needs a per-camera
  override — `"audioTuning": {"speechGateRmsThreshold": 0.0015}` — or the gate admits nothing and no
  record is ever written. See
  [detection.md](detection.md#the-sound-gates-threshold-is-per-camera-and-it-matters-more-than-it-looks).

About two minutes later:

```bash
cd App/serval_app
flutter test integration/conversation_transcript_test.dart \
  --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
  --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=browsertest123
```

It is read-only and skips cleanly when nothing has settled, so it is also safe to point at a real
deployment to see what that house's rooms are producing.

For the pixels, run the Server as above and look: the wall's *What's happening* column should show
the camera with numbered speaker bubbles against alternating lines of the conversation.

### Diagnostics

Anything that needs a model or hardware (SenseVoice, diarization, Qwen3-VL, the mic, the camera)
is verified with the `Serval.CameraModule.Tools` verbs — the command list is in the
[CameraModule README](../CameraModule/Serval.CameraModule/README.md#verifying-without-a-microphone-or-camera).
What each one proves:

On a new board, run them in order — each isolates one layer, so a failure tells you which.
`--capture-test` proves the camera before any model is involved; `--selftest` and
`--describe` prove the models load and produce real output.

### Calibrating a camera

Whichever gate a camera runs, its thresholds differ per view — a wide outdoor shot with foliage
wants different numbers from a static hallway — and the alternative to measuring is guessing, then
wondering why a camera describes everything or nothing.

`--motion` loads no model at all, so it answers in milliseconds, and prints the changed-pixel
fraction for every comparison including the ones that did not trigger.

`--detect a.jpg [b.jpg ...]` is its counterpart for the object gate. It prints every box above the
threshold and, treating the images as consecutive, which episodes they would open — which is the
number that matters, since a detection that never survives `Tracking:ConfirmSeconds` costs nothing
downstream. It steps the clock a second per image, which is the hardest rate for the tracker, so
expect a folder of 1 fps snapshots to fragment tracks in a way the Server's detect stream does not.

`--replay-gates <frame-directory>` runs **both** gates over the same frames and reports what each
would have cost. This is how the object gate earns its place on a given site rather than in the
abstract:

```bash
# One camera's recorded segments back to the frames the server would have seen.
cat init-*.mp4 $(ls seg-*.m4s | sort) > joined.mp4
ffmpeg -i joined.mp4 \
  -vf "fps=1,scale=w='max(2,trunc(min(iw,iw*sqrt(250000/(iw*ih)))/2)*2)':h=-2" \
  -q:v 5 frames/f-%05d.jpg

cd CameraModule/Serval.CameraModule
dotnet run --project ../Serval.CameraModule.Tools -- --replay-gates ../../frames
```

The filter matches `SnapshotWatcher`'s exactly, so the measurement reflects what the server would
actually have looked at. The `250000` is `Ingest.SnapshotMaxMegapixels` in pixels — substitute the
deployment's own value, or the frames will be a different size from the ones it sees. Detections are cached to a `detections-*.tsv` beside the directory on the
first run, which makes sweeping a policy setting instant rather than a fresh inference pass each
time — delete it to re-detect.

**Read the `misses-*` directory it writes.** Those are the frames motion would have described and
the object gate would not, and they are the only thing a reduction ratio cannot speak to: a gate
that describes nothing scores perfectly and is worthless. There is no ground truth here, so they
have to be looked at.

`--speakers` does the same job for `Speaker.ClusterThreshold`; the fixture results that produced
the shipped default are in [detection.md](detection.md#tuning-the-models).

### Masks over real video

`--replay-gates` feeds frames whole, so it exercises no `RegionPlanner` and cannot see a masking
decision at all. [`MaskedTrackingOverVideoTests`](../Server/Serval.Server.Tests/MaskedTrackingOverVideoTests.cs)
is the one thing that does: it composites a real person over a still with ffmpeg, decodes the clip
back with the `DetectFrameReader` recipe, and runs planner → preparer → detector → tracker → policy
in the Server's own order. Skipped unless ffmpeg is on PATH and a model is pointed at:

```bash
SERVAL_DETECT_MODEL=~/serval-local/models/detect/model.onnx \
SERVAL_DETECT_LABELS=~/serval-local/models/detect/labels.txt \
dotnet test Server/Serval.Server.Tests --filter MaskedTrackingOverVideoTests
```

Frames are 1280x720 so crops resolve **on** — at a crops-off size the planner returns the whole
frame every time and there is no decision left for a mask to affect, which the test asserts rather
than assumes. The case worth keeping is the one where a person's box is four fifths inside the mask
and their feet are below it: they must be tracked exactly as if the mask were not there.

## App

```bash
cd App/serval_app && flutter test
```

Hermetic — no Server, no network.
[`test/widget_test.dart`](../App/serval_app/test/widget_test.dart) covers the
routing and the wall/camera split. [`test/golden_capture_test.dart`](../App/serval_app/test/golden_capture_test.dart)
renders every screen at the design's 1440x900 with the real fonts, so drift from the mock
fails the build; regenerate with `flutter test --update-goldens` and look at the PNGs in
[`test/goldens/`](../App/serval_app/test/goldens/). Which goldens move is itself a signal — a
change to one screen that shifts another means something leaked between them. The `on a phone`
group captures the compact designs at 412x892 instead — the wall's sheet at rest, raised and as the
filter, one camera at its peek and stowed, and the settings index. Those exist because the phone is where the App
diverges most from the desktop it was drawn for, and until round 11 it was pinned only by widget
tests measuring one figure each: enough to say a sheet is 236 tall, and nothing about what is in
it.
[`test/cameras_screen_test.dart`](../App/serval_app/test/cameras_screen_test.dart)
lays the settings form out for every camera and asserts no exception — it is the one screen with a
dense two-column form inside a scroll view, which is where unbounded-constraint and overflow bugs
live, and they fail nothing else. Note it does **not** load the vendored fonts: the fallback
metrics are wider than Inter, which makes it a stricter overflow test than the real thing.

Two suites run narrow, and everything else runs at 1200px or wider. That split is the point: the
wide suites pin the columns, these pin what replaces them, and both answers are correct depending
only on the size.

### The one screen with no golden

Round 12 — the camera screen as a trimmer — is pinned by
[`test/clip_mode_test.dart`](../App/serval_app/test/clip_mode_test.dart) rather than by a capture,
and the reason is worth knowing before anyone adds one. The trimmer opens around the playhead,
which live is the wall clock: every tick label, both time fields and the range named in the button
are different on every run, so a pixel comparison fails a minute after it is baked. What that
screen is *for* is structural anyway — that the press enters a mode rather than starting an export,
that the ways off the screen go inert while a range is being set, and that the two ways of moving
an end agree about which end is moving.

The arithmetic underneath it is
[`test/clip_selection_test.dart`](../App/serval_app/test/clip_selection_test.dart), which is where
the real risk lives: every case there decides what ends up in a saved file rather than what a
screen looks like, and getting one wrong keeps the wrong minute — which nobody notices until the
clip is the only copy left.

[`test/settings_compact_test.dart`](../App/serval_app/test/settings_compact_test.dart) is design 7b
at 412x892 — the settings drill-down. It drives the real router, because a drill-down navigates and
a harness that pumps one screen cannot see it move.

[`test/camera_compact_test.dart`](../App/serval_app/test/camera_compact_test.dart) is design 8 at
three sizes: 412x892 for the phone held upright, 892x412 for the phone turned, and 900x600 for the
case those two must not swallow — a desktop window somebody squashed, which is also wider than it
is tall and must keep its chrome. What it pins beyond layout: that the picture's bottom edge is the
tray's top edge at every detent, that the tray's peek is **measured** from what it is carrying —
press *Snapshot* and the tray grows by the line that appears rather than taking it from the feed,
which is the regression test for the estimate this replaced — that the tray never reaches over *Hold
to talk*, that a fling steps one detent rather than crossing the whole travel, that a drag past the
peek stows the tray to a bar that is only its name and gives the picture the rest of the screen,
that a tap on that bar is a way back that does not need the 18px handle, that clicking a row
puts the tray down, that expanding *rotates* rather than stretching, that the mode is left when the
window grows back to a desktop, and that the wall's feed is a tray at one size and a column at the
other. There is also a sweep that drives every detent at four window sizes and asserts only that
nothing throws: nothing divides a budget any more, so what used to need arithmetic to fit now only
needs somewhere to be. The wall at 412px is covered here rather than in the settings
suite because it only started laying out at all in round 8 — before that its 376px column left the
timeline track under a pixel wide and `_TrackGeometry` threw on the clamp.

The rest pin the parts that would otherwise fail silently against a real Server: the telemetry
documents (from payloads copied off a live one), the wall socket's binary frame format, the camera
record's round trip, and the role rules.

"What's happening" is one feed on two screens, so it gets two suites.
[`activity_filter_test.dart`](../App/serval_app/test/activity_filter_test.dart) covers the
predicates — a wrong one here hides events rather than failing — plus what the single-camera panel
does differently: no camera name on a row, the speaker in the slot it left, and a different empty
message for "nothing happened" than for "the filter is hiding it".
[`activity_panel_test.dart`](../App/serval_app/test/activity_panel_test.dart) covers the hazard in
a newest-first list: rows arrive at the top, so without holding the scroll offset the row you are
reading walks off the bottom. Neither the goldens nor the sample data can catch that, because it
only happens on a camera that is actually busy.
Clicking a row gets its own pair, split the way the behaviour is:
[`open_camera_at_test.dart`](../App/serval_app/test/open_camera_at_test.dart) covers the wall's
side — the instant handed over, the lead-in, the live edge, and a camera that keeps nothing having
inert rows — and [`feed_row_seek_test.dart`](../App/serval_app/test/feed_row_seek_test.dart) covers
the panel's, where the same click is a seek rather than a route. Both assert against
`replayStartFor` rather than a hard-coded offset, because the point is that the two screens cannot
disagree about *when*.
[`activity_collapse_test.dart`](../App/serval_app/test/activity_collapse_test.dart) covers the
panel giving its width back: that the chevron shuts it to the rail and the rail's chevron reopens
it, and — the part worth a test rather than a look — that collapsing it on the wall arrives
collapsed on the single-camera view, because that preference is the repository's and not either
screen's. The write behind it is not covered here; `SampleServalRepository` honours the toggle but
persists nothing, so a restart is a browser check.

One trap in any widget test that measures text: `flutter test` renders every glyph at `fontSize`
wide, so a six-character label measures six times the point size — nothing like Inter. A layout
that overflows by a pixel under `flutter test` and not in the goldens is usually this, not a real
one; the goldens load the vendored fonts and are the honest measurement.

Replay is covered in three layers, none of which needs a decoder:
[`timeline_window_test.dart`](../App/serval_app/test/timeline_window_test.dart) for the arithmetic between an x and
an instant — an off-by-one there is not a glitch, it is a seek to the wrong hour;
[`timeline_scrubber_test.dart`](../App/serval_app/test/timeline_scrubber_test.dart) for the gesture, including that a
drag scrubs continuously but seeks exactly once; and
[`replay_controller_test.dart`](../App/serval_app/test/replay_controller_test.dart) for the window arithmetic, which
is where "does this gesture cost a request" is decided, and where a position is turned back into a
wall-clock instant. That last one injects a fake player through `ReplayController`'s factory
parameter — the only seam added for testing, and the reason the whole suite still runs on a machine
with no libmpv.

The instant is worth its own group there. Playback position counts from the segment boundary the
playlist starts on rather than from the instant the window was asked for, and the two are up to one
segment apart, so the controller subtracts `EXT-X-START:TIME-OFFSET` from every position and adds it
to every seek. Nothing in the app *looks* wrong when that term is missing — the timestamp pill is
merely a few seconds fast — which is exactly why it went unnoticed until detection boxes were drawn
over the same frames and sat 43 px behind a walking object.
[`timeline_ticks_test.dart`](../App/serval_app/test/timeline_ticks_test.dart) is worth singling out: against the
design's own capture time the derived tick labels come out *identical* to the strings the scaffold
had hard-coded, which is what says the rounding rule is the design's and not one invented to
replace it.

`ServalApp` still defaults to [`SampleServalRepository`](../App/serval_app/lib/data/sample_repository.dart) — the
design's own content — so the tests and goldens render the mock rather than whatever a live NVR
happens to be looking at. `main()` builds the live one. Its `vodUrlFor` returns null, which is what
keeps `flutter test` from ever constructing a player; `widget_test.dart` asserts that a tap on the
scrubber stays on the live placeholder, so that property cannot rot silently.

### Against a real Server

**Run your own.** Everything below wants a Server you started, on a throwaway database, with a
pseudo-camera — not a deployment someone is relying on. A live NVR is somebody's house: its
registry has no undo, `move` physically turns a real camera, and a camera you disable to make a
test deterministic is a camera that stopped recording. Signing in is not the safeguard here —
these run as an Admin, which is exactly the account that can do all of that. Stand one up with the same
invocation [browser-testing.md](browser-testing.md#standing-up-a-server-and-a-bundle) uses (drop
`ASPNETCORE_WEBROOT` if you are not serving the bundle), and give it something to look at with the
file-source camera from [A camera with no camera](#a-camera-with-no-camera) — ffmpeg loops the clip
in realtime, so recording, `/coverage` and `vod.m3u8` all have real data behind them with no
hardware.

[`integration/`](../App/serval_app/integration/) sits outside `test/` so `flutter test` never picks
it up:

All three sign in, so all three need an account as well as a URL:

```bash
cd App/serval_app

flutter test integration/live_server_test.dart \
  --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
  --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=browsertest123
flutter test integration/audio_levels_test.dart --dart-define=…   # same three

# This one writes, so its account has to be an Admin — reads take any role, every camera
# write is Admin-only.
flutter test integration/registry_crud_test.dart --dart-define=…  # same three
```

Omitting the credentials fails in `setUpAll` with "Could not sign in", which reads like a server
fault and is not one. Passing a Viewer's credentials to `registry_crud_test.dart` gets further —
the sign-in succeeds and the first POST comes back 403.

`live_server_test.dart` only reads. `registry_crud_test.dart` writes, and only ever to one throwaway
id, created **disabled** so the ingest manager never starts an ffmpeg against its fake source — that
pattern is worth keeping even against your own Server, since it is what makes the suite re-runnable.

`live_server_test.dart` covers replay's data path too: that `/coverage` answers, that its spans sit
inside the window they were asked for, and that a fifteen-minute `vod.m3u8` over footage the Server
said exists comes back as a playlist with segments in it. It skips rather than fails when the
camera has recorded nothing in the last day.

### Checking a box lands on the thing it describes

Anything drawn over the video in normalised coordinates — detection boxes today — cannot be checked
by looking at it. A box is plausible anywhere, so the only honest test is footage whose contents you
already know, and *measured* rather than computed: what the loop of a file-source camera records is
offset from the clip you fed it by ffmpeg's startup and by wherever the loop happened to be.

The fixture is a clip with a moving object, a burned-in timestamp, and a stretch where the object
is absent so gaps get exercised:

```bash
ffmpeg -y -f lavfi -i "color=c=black:s=640x480:r=15:d=60" \
       -f lavfi -i "color=c=red:s=80x140:r=15:d=60" \
  -filter_complex "[0][1]overlay=x='40+11*t':y='160+60*sin(t/4)':enable='lt(t,40)'[v];\
                   [v]drawtext=text='%{pts\:hms}':x=10:y=10:fontsize=28:fontcolor=white[o]" \
  -map "[o]" -c:v libx264 -pix_fmt yuv420p -t 60 moving.mp4
```

`overlay` and not `drawbox`: `drawbox`'s `t` is its thickness option, so a `t`-based position
expression silently evaluates to nothing and draws no box at all — a black clip that looks like a
recording fault. `enable=` is the timeline mechanism and is what produces the absence.

Then point a camera at it, let it record a few minutes, pull the window back through `vod.m3u8`,
cut it to frames at 1 fps, find the object in each one, and post *that* as a detection episode
through `POST /api/cameras/{id}/telemetry`. The track is now ground truth for the footage, so a box
that does not sit on the object is a real defect rather than a fixture that drifted. Both bugs in
[app-notes.md](app-notes.md#what-still-has-no-backend-source) — the letterbox and the playlist start
offset — were found this way and are invisible any other.

The live view cannot be checked like this: go2rtc cannot serve a file, so a file-source camera has
no WebRTC session and the pseudo-camera trick stops at replay. The letterbox arithmetic is pinned
for both sources by
[`picture_aligned_test.dart`](../App/serval_app/test/picture_aligned_test.dart) instead, including
the rotated case, which is the part that needs a real camera to see.

The same arithmetic over a *still* is pinned by
[`picture_fit_test.dart`](../App/serval_app/test/picture_fit_test.dart), which puts a 4:3 poster in
a 16:9 slot and asserts an alert's box starts from the pillarbox rather than from the slot's edge —
along with the fit of every surface that draws a camera picture, since a crop of a frame is still a
frame and no golden can tell the two apart.

### In a browser

Nothing here opens one. Driving the real web build — by hand or under Playwright — has its own
page: [browser-testing.md](browser-testing.md). It covers standing up a Server and a bundle
together, what a blank white page means, the handful of things a canvaskit app does differently
under a browser driver, and the browser APIs an insecure origin withholds.


### Alerts, end to end

The alert queue needs a real detection to fill it, and the detector will not oblige a `testsrc`
pattern — but it does not need a real camera either. Two things have to be true of the source, and
both are easy to miss:

**The camera must have a detect stream of its own.** A ring is only written by the session that
owns the detect stream when that stream is not the one being recorded — a one-stream
`["record","detect","live"]` camera has no ring and cuts previews from the recording instead. So a
two-file camera is what exercises the ring:

```bash
P=Server/Serval.Server.Tests/Assets/person.png

# Empty for 150s, then somebody walks in. The lead-in is the point — see below.
ffmpeg -y -f lavfi -i "color=c=0x2a3038:s=640x360:r=15:d=230" -i "$P" \
  -filter_complex "[1:v]scale=-1:300[p];[0:v][p]overlay=x='if(between(t,150,185), 40+(W-w-80)*(t-150)/35, -400)':y=30" \
  -c:v libx264 -g 30 -pix_fmt yuv420p sub.mp4     # and the same at 1280x720 for main.mp4

curl -X POST localhost:5211/api/cameras -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"id":"front-door","name":"Front door","aiVision":true,"streams":[
        {"name":"main","url":"'"$PWD"'/main.mp4","roles":["record","live"]},
        {"name":"sub","url":"'"$PWD"'/sub.mp4","roles":["detect"]}]}'
```

**And the subject has to *arrive*.** An alert needs `IsArrival`, which is false for anything in
shot within `Detection:NoveltySeconds` (120) of the session starting — everything a camera opens on
is inventory. A clip whose subject is present from the first frame produces episode after episode
with `is_alert: false` and looks exactly like a broken feature. The 150-second lead-in above is
what makes the walk-in count, and any edit that restarts ingest (a `PUT` to the camera, a settings
change in the signature) resets the clock.

What to watch for, in order:

```
Alert on camera front-door: Person at Front door at 18:46:15.        # raised at episode open
Alert 5addfa83… on camera front-door: 20s preview from 5 segment(s)  # cut, post-roll + 1s later
    of the preview buffer.
```

`preview from N segment(s) of the preview buffer` versus `of the recording` is the one line that
says which store the clip came from. A preview of 4 seconds from 1 segment means the ring had just
been cleared — a session restart does that — rather than anything being wrong with the cut.

Then, on `GET /api/alerts`: `clip_state: ready`, `recorded` matching the camera's switch, and
`box` non-null for an object and null for a sound. `ffprobe` on
`{Media.Root}/{Media.AlertsRoot}/{id}.mp4` should report the **detect** stream's dimensions, not the
recorded one's — that is the check that the ring, rather than the recording, is what was cut.

The two states that are not failures, and are worth producing deliberately:

* **`unavailable`** — post a module alert dated before the buffer reaches (`POST
  /api/cameras/{id}/telemetry` with an old `timestamp` and `is_alert: true`). No footage, no files,
  and the card draws its camera's stripe.
* **`recorded: false`** — `PUT` the camera with `"recording": false`. The detect session and its
  ring carry on; the recorder stops. The alert still gets a full preview, and only *Watch*
  disappears. This is the case the whole feature exists for, so it is worth seeing at least once.

Retention is provable in one sweep: `PUT /api/settings` with `Serval:Media:AlertRetentionDays: 1`
and `Serval:Media:RetentionSweepMinutes: 1`, then restart — the period is read when the timer is
armed, so a running server keeps its old cadence until the current one elapses.
