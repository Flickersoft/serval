# App notes

Why the Flutter client is shaped the way it is, and which parts of the design have no Server
behind them. Paths are relative to [`App/serval_app/`](../App/serval_app/).

## Decisions worth knowing

### State reaches the screens by pull, not push

**The repository stays synchronous and pull-shaped**, and is a `Listenable`. `notifyListeners` means
*the structure changed* — camera list, activity, connection. Snapshot frames deliberately do **not**
notify: at ~1 fps per camera that would relayout the whole wall several times a second to repaint one
tile, so they ride a per-camera `ValueNotifier` and rebuild only their own tile.

**The wall's five-second sweep is gated on a digest.** Staleness is a function of the clock, so
nothing arrives to say a camera stopped sending — but the tick firing is not itself news, and
notifying unconditionally rebuilds the whole signed-in tree twelve times a minute to paint the same
pixels. `LiveServalRepository.clockDigest()` carries each camera's `CameraConnection`, the two
eight-second telemetry windows, whether a live caption is up, **and the minute** — that last because
`ActivityItem.timeLabel` is a pre-rendered string, so a digest built from the camera flags alone
leaves every relative timestamp in the activity column frozen. It carries the derived *state* rather
than the freshness and listening window behind it, so every clock-driven transition the wall can
make — including a camera crossing from `connecting` to `offline`, which nothing announces — moves
exactly one character. See [live-view.md](live-view.md#connecting-is-not-offline) for what that
window is and why the wall has three readings rather than two.

**Riverpod is a dependency-injection container and nothing more, and it stops at `lib/screens/`.**
[`lib/data/providers.dart`](../App/serval_app/lib/data/providers.dart) holds the repository, the
session, and one `FutureProvider` for the account roster. Everything under `lib/widgets/` takes what
it needs as parameters, including `repository` — a hand-built Nocturne control that reaches into a
container is no longer a control, and could not be dropped into a golden or another screen without
bringing the container along. Ephemeral UI state (text controllers, drag gestures, which tab is open)
stays in `State`. The seam the tests use is the `ServalRepository` interface and
`SampleServalRepository`, not `overrideWithValue`, and `ProviderScope` lives inside `ServalApp.build`
so `const ServalApp()` still means what it says.

### Where you are is an address, not an enum

The routes are `/wall`, `/camera/:id`, `/settings`, `/settings/cameras?camera=<id>`,
`/settings/server`, `/settings/status` and `/settings/users`, in
[`lib/router/serval_router.dart`](../App/serval_app/lib/router/serval_router.dart). Web is the
primary target — the Server bundles this build and serves it at its own origin — so every screen has
to be linkable, survive a reload, and answer the browser's back button. That is why the two things
that could have been widget state live in the address: which camera is open is the path parameter,
and which camera the registry opens on is `?camera=`. Path URLs rather than `#` ones are only safe
because `Program.cs` already serves the SPA fallback.

**The rail is a `ShellRoute`, and `/camera/:id` sits outside it.** The 64px rail and the settings
sidebar are [`ServalShell`](../App/serval_app/lib/widgets/serval_shell.dart) and
[`SettingsShell`](../App/serval_app/lib/widgets/settings_shell.dart), drawn once with the selection
derived from the location rather than redrawn by each screen. The single-camera view has no rail —
its top bar carries a labelled *All cameras* button, which says more at that moment than an
unlabelled glyph, and lighting *Live view* while you are watching one would be confusing. The rail
carries one destination because it cannot show a back stack; on web the browser's own chrome shows
it, which is why that constraint does not reach the address bar.
### A camera picture is never cropped to fit

**Every surface that draws camera imagery contains it and paints the leftover.** The wall tile, the
single-camera stage and both replay players, the alert still in the queue and on the card, a clip's
poster in the grid and on the player, and the 34px preview in the registry. Nothing anywhere uses
`BoxFit.cover`.

The shapes are decided by different people and never agree. A tile's is its span on the grid, a
card's is the design's 16:9, a stage's is 232px of a phone — and a frame's is a sensor. Filling the
slot crops whichever edge is long, which on a 4:3 camera in a 16:9 slot takes a quarter of the
frame's height off the top and bottom: on a doorbell, the face and the parcel. It is also the one
kind of wrong nobody can see without the original beside them, since a crop of a picture is still a
picture — which is why
[`picture_fit_test.dart`](../App/serval_app/test/picture_fit_test.dart) asserts the fits rather than
trusting a golden to notice.

The bars are `Serval.tile`, the video ground, rather than whatever is underneath — so they read as
the edge of the picture instead of as the camera's stripe showing through it. Where the picture is
fetched rather than held, the ground appears with the first frame and not before: the stripe is what
says *a picture is coming*, and flat grey under a poster still in flight says the opposite.

### The phone

**Below 950px the settings columns become a drill-down, and there is no rail.** The wall is home and
its own header carries the gear, so `/settings` is the settings sidebar as a screen, each page
carries a back arrow to it, and a list and the record it opens are two screens rather than two
columns. `Serval.compactWidth` in
[`lib/theme/serval_tokens.dart`](../App/serval_app/lib/theme/serval_tokens.dart) is the one figure,
read through `isCompact(context)` off the **window's** width so every screen swaps at the same
moment. 950 is what the columns cost rather than a device class — rail 64, settings sidebar 236 and
camera list 272 come to 572, and an editor needs most of `kPairedMinWidth` again before it holds a
form. `/settings` therefore means two things by width, deliberately: the index on a phone, and what
the machine is doing on a desktop, where there is nothing to drill into. Below 950 the wall reads
`kPairedMinWidth` as well, so its one-to-two-column switch is the figure every other paired thing
collapses at.

**One camera on a phone is a picture and a tray over it; full screen means landscape.** The camera
sends 16:9 and a portrait phone is 9:19.5, so the scene can only be a strip. Nothing is drawn on it
but the detection boxes and the two things that date the frame; everything the desktop floats over
the video rides the tray instead — the four-item row (*Audio*, *Move*, *Snapshot*, *Clip*) and the
timeline. *Hold to talk* is the exception, a pill pinned at the bottom that the tray never reaches
over, because it is what the screen is opened to do and a thumb has one place it sits.

**Nothing on this screen divides a budget, and that is the whole of the design.** The picture takes
whatever the tray is not covering; the tray takes what it measures. Four of the things under the
picture come and go — the action row leaves in clip mode, a save writes a line, the scrubber grows a
second row the moment replay starts, the talk bar is absent on a camera with no speaker — and the
feed used to absorb every bit of that through an `Expanded`, then drop its own summary under 260px
and its listening strip under 170. The figure it was dividing was a guess (`218`) that counted
neither the save line nor the transport row, so it was wrong in exactly the states somebody is in
when they notice. The tray measures its head instead, through a `RenderProxyBox` that reports what
the controls actually laid out to, and the detent that keeps them visible follows.

**Four detents, and each is what it leaves behind.** *Stowed* is the floor of the travel: 52px of
grabber and one line saying "What's happening", and the picture takes everything else on the screen.
That height exists for the cameras whose scene is not 16:9 — a portrait doorbell letterboxed into
the room a peek leaves is a fraction of what the phone could show it at — and one drag or one tap on
the bar brings the controls back. *Peek* is the grabber and the controls and nothing else — the
picture takes the rest, which is the room a pinch is given and what the feed's collapse chevron used
to be for. *Resting* adds `Serval.activitySheetResting` of feed to that, the
search field under a thumb and an event or two below it. *Raised* is the ceiling, flush under the
app bar, where the feed is the subject; Serval's summary appears only there, and both it and the
listening strip are asked about the **settled** detent rather than the live height, so neither
appears and vanishes under a moving finger. Clicking a row puts the tray back down to resting —
clicking a row is asking to see footage, and footage behind a raised tray is not being seen.

The wall's rule about never covering everything does not hold here and should not: you asked for
this camera, and a drag, a tap on the handle or Back all bring the picture straight back. So the
single-camera tray passes `topInset: 0` and `floor: 0` where the wall keeps 96 of each.

**The picture is laid out against the settled detent, not the live drag.** Following the drag would
re-box the video forty times a second and re-anchor a pinch in the middle of one, so it is an
`AnimatedPositioned` on the same 240ms `easeOutCubic` the tray uses to reach a detent. Its bottom
edge *is* the tray's top edge, which is what keeps the corner that gives it the screen always just
above the tray rather than always behind it.

**Trimming keeps the plain column.** A tray there would carry a feed's head — a title and a search
field — over controls that are not a feed, and offer to narrow a list nobody is reading. Clip mode
is a mode that takes the screen, so it draws the band, the track and the scrolling controls, and
there is no budget to divide because none of its parts come and go.

**Expanding turns the view.** A 16:9 frame letterboxed into 9:19.5 is a *smaller* picture than the
band it came from, so the stage is laid out along the long edge and rotated a quarter — what every
video player does when full screen is tapped without rotating — and a phone actually held sideways
gets the same stage drawn upright. Pan, tilt and zoom live only there, at full size with the whole
picture underneath to aim by, which is why *Move* is a way into it. The timeline comes along and the
transcript does not; without a seek bar, landscape would be live-only with no way back into the
recording short of turning the phone upright.

**The design's *Conversation | Activity* tabs are not implemented, deliberately** — they are the
same data seen twice. [`ActivityPanel`](../App/serval_app/lib/widgets/activity_panel.dart) records
why. The phone gets the merged list.
**"What's happening" is a column on a desktop and a tray on a phone — one tray, both screens.** A
376px column at 412px wide is the whole screen, and a fixed band takes 292px whether or not you are
reading it. [`ActivitySheet`](../App/serval_app/lib/widgets/activity_sheet.dart) floats over what is
behind it instead. The wall gets **three heights**: stowed at 52, resting at 236, and raised with one
whole tile row above it and the next peeking. It has no *peek* — that one is measured from `head`,
and the wall's tray carries nothing but the feed — and deliberately no full-height detent *there*:
that would be a screen with the wall gone, which is the one thing a security app should not do
quietly. Stowed is the opposite move and is fine everywhere, because a tray pushed out of the way
gives the wall *more* of the screen rather than less — and the tiles take it: the scroll view's
bottom padding follows the settled tray down to `Serval.activitySheetStowed` and back up to
`activitySheetResting`, never further, so a wall already scrolled to its end slides down into the
room the tray gave up rather than holding the last tile above a strip of nothing. Down only, because
raising the tray is an act of reading the feed and a wall that grew room to scroll into every time
would be rearranging itself behind a sheet that is covering it anyway. The settled height and not
the live one, on the same 240ms `easeOutCubic`, for the reason the picture uses it. A scroll offset
is the reader's, so the tiles do not float back up when the tray rises — the *room* to scroll them
clear is what the padding promises, not a position. The raised detent is derived from the tile
rhythm rather than pinned at the design's 340, so "one camera whole overhead" stays true at two
columns and on a screen of another height.

The tray takes slots rather than knowing what is in it: `head` — the controls above the feed, which
is also the thing `SheetDetent.peek` is measured from, and null on the wall — and `body`, a builder
given the settled detent. That is what lets the same physics, the same filter mode and the same
`PopScope` serve a wall of tiles and one camera. The wall composes `WallActivityFeed`; the camera
composes `CameraActivity`, which is the pieces
[`ActivityPanel`](../App/serval_app/lib/widgets/activity_panel.dart) and the tray genuinely share.
`ActivityPanel` is the desktop column and nothing else — the phone is a different arrangement, not
this one with its metrics changed, which is why it is a different widget rather than a flag.

It is a `Stack` rather than a route or a `DraggableScrollableSheet`: what is behind it stays live and
tappable, and the feed's `TopAnchoredScrollView` owns the `ScrollController` its top-anchoring
depends on, which is what a draggable sheet wants to take away. **Nothing re-flows as it moves** —
the contents are laid out once at the tallest the tray can ever be, inside an `OverflowBox`, and the
tray's own box clips them. That is also what makes `peek` possible at all: at that height the feed's
head does not fit, and a `Column` asked to fit it would overflow rather than be cut off.

That invariant is why *stowed* draws its own bar rather than reordering the column. Putting the
"What's happening" line above `head` so a 52px tray would show it means the title sits over the
camera's own controls at every other height — a title over the wrong thing — so the bar is a
`Positioned` overlay that fades out over the 40px above the detent. Opaque, so the head is
*revealed* by the fade rather than showing through it; and its **face** leaves the tree once
invisible, so "What's happening" is in the tree once wherever the real one is on screen.

**Its gesture detector does not leave with it**, and that distinction is a bug worth keeping
written down. The bar carries the drag as well as the tap — at 52px the handle is a quarter of the
tray and too small to be the only way back — so taking the whole overlay out at the end of the fade
disposed the recognizer that had *won the gesture in progress*, which cancels it. Every drag up
from stowed therefore died exactly 40px in, with the search field half revealed, and the finger had
to be lifted and put down to carry on; a drag *down* was fine, which is what made it look like a
stuck detent rather than a cancelled gesture. So the detector is unconditional and `IgnorePointer`
hides it from hit tests instead — a route already established at pointer-down survives that, where
an unmount does not. Both the bar and the head read the same `activityReadout`, so the stand-in
cannot say something the thing it stands in for would not.

**The drag is the grabber and everything under it down to the feed**, which on the camera screen
includes the scrubber — and that is not a collision. A track taking horizontal drags and a tray
taking vertical ones are two recognizers in one arena, and the direction of the gesture decides
between them. The tap is the 18px handle's alone — over the controls it would be a second meaning
for pressing *Snapshot* — and the stowed bar's, where the handle is a quarter of what is left and a
target that small should not be the only way back.

**The filter is the tray**, risen to its ceiling with a scrim over what is behind. A panel floating
inside a column would be a column inside a column at this width. *Done* sits in a bar the thumb
reaches and repeats the count, so what you chose is visible before you dismiss it rather than after.
On the wall it stops 96 from the top with the app bar dimmed under it — which is why that bar is
inside the stack; on one camera it stops under the bar, so the camera being filtered stays named.

**The phone wall is live-only, and every tile is the shape its camera sends.** No scrubber below
`Serval.compactWidth`: replay is a single-camera act, and a 412px track under six cameras is a
control nobody can land on. No *Rearrange* either — the 24-column grid scaled to 412px puts a
standard tile at 88px with the gaps taking more room than the cells. Tiles run in the saved
arrangement's reading order (`WallGrid.readingOrder`, row then column): the spans are the desktop's
decision and mean nothing in a single stack, but the sequence is still a decision somebody made. One
column below `kPairedMinWidth` and two from there to 950, which is what saves a phone held sideways,
where a single 16:9 tile would be 501px tall in a 356px space.

**A tile's height comes from the picture, because nothing else knows it.** On a desktop a tile's
shape is its span on the grid and a camera that disagrees is contained inside its cell — which is
why a doorbell is a strip in a letterbox there. On a phone there is no grid and the width is already
spent, so the only thing left to set the height with is the frame; assuming 16:9 puts a vertical
camera in a narrow column down the middle of a wide empty tile.

The shape is read off the decoded frame (`_MeasuredFrame` in
[`wall_screen.dart`](../App/serval_app/lib/screens/wall_screen.dart)) rather than from the registry,
because the Server publishes no resolution — `Camera.resolutionLabel` is a stub and says so.
Resolving bytes the tile is already drawing is a cache hit, not a second decode, and only a new
buffer is measured. The shapes live on the screen's State rather than in each tile because the
sheet's raised detent is measured against the first row, and a tile cannot tell the sheet how tall it
turned out. That detent takes the *larger* of the tile rhythm and
`Serval.activitySheetRaisedShare`: a portrait camera at the top of the wall is taller than the room
above the resting sheet, and a raised sheet shorter than the resting one gives back less than it
took.
### The activity feed

**"What's happening" is one feed drawn twice.** The wall shows it merged across every camera; the
single-camera panel shows the same rows scoped to one, from the same in-memory cache — `activityFor`
takes a `cameraId` rather than there being a second route, because the merge has already happened and
every document carries its `camera_id`.
[`ActivityFeed`](../App/serval_app/lib/widgets/activity_column.dart) is the one rendering of a row;
the two callers differ only in `showCamera`, in whether an alert offers *Dismiss*, and in what a
click does.

The camera name leads a row on the wall and `ActivityItem.speaker` leads it on the panel — on a
screen showing one camera the name is the same six words above every row, answering a question the
screen has already answered. That slot carries no *identity*: the bubble on the quote does, on both
screens, which frees the heading to say where the voice was (*At the camera*) or how many there were
(*2 speakers*) — questions with an answer on every row rather than only inside a conversation.

**Every row is a way into the footage behind it.** Clicking one starts playback five seconds before
the instant it names, because a detection is stamped when the gate fired, which is already a moment
*into* whatever caused it. Both screens compute *when* from the same `replayStartFor` in
[`models/timeline.dart`](../App/serval_app/lib/models/timeline.dart) and then do different things
with it: the wall routes to `/camera/:id?at=…`, the panel seeks the picture already on screen. The
split between "seek" and "the live view is where that is" is thirty seconds
(`ReplayController.liveEdge`) — a seek inside it hands straight back to live anyway, so asking for
one buys a round trip and a flicker. A camera that keeps nothing gets inert rows, the same gate the
scrubber puts on its own track.

**Arriving from a row opens the moment and the half hour after it** (`rangeForOpenAt`, same file).
The moment sits a minute in from the left edge and the track reaches `arrivalSpan` past it, because
what you are about to do is watch the thing and then watch what followed — where the widening it
replaced put the moment against the *right* edge of an hour, six hours or a day of track, a few
pixels wide with the time before it laid out beside it. The minute of lead is not decoration: on the
edge exactly the playhead's knob is clipped, and a seek lands on the nearest footage, which across a
break in the recording can be earlier than the instant asked for and therefore off the track
entirely. A moment whose half hour has not happened yet gets the third kind of range instead of a
bar half full of future — see the scrubber section. Inside the live edge there is nothing to replay
and the screen keeps its default hour.

**The camera panel is one feed, not a feed plus a transcript.** The design's two-sided bubbles are
not drawn: nothing captures the outbound side of the push-to-talk, so every turn comes back
attributed to *them*. A separate per-turn transcript would read the same cache the activity feed does
and show everything a camera said twice. A settled conversation is **one row**, with its turns drawn
inside it, each carrying the voice that said it, so the attribution you watch during a conversation
does not vanish when it ends. What is given up is the clock time on each turn: the turns are ordered,
not dated.
**Who spoke is a bubble on the quote; how they sounded is a glyph at its right.** Both marks go in
the **body** rather than the heading, which is what lets the two screens share them — the wall's
heading is already spoken for by the camera name. Never print the wire's own `speaker_0`: it is an
index into one conversation and means nothing to a reader.

A bubble is drawn **only where the conversation had more than one voice**, because a permanent ①
beside a monologue is a distinction with nothing on the other side of it. Live rows count distinct
parsed `speaker_N` labels and so gain bubbles retroactively when a second voice arrives, which is the
correct answer arriving late rather than a wrong one arriving early. Of the analyzer's nine emotion
words only six are drawable — `neutral` would sit on nearly every speech row, and
`emo_unknown`/`other` are the model declining to answer. The bubble stays on the accent: a speech row
already wears it twice, so a third quiet use reads as part of the quotation.

**The faces are coloured, one flat hue each.** Grey at 13px they are unreadable — the angry face and
the fearful one are the same blob, which is the kind of thing only driving the real feed in a browser
shows. A face is not a word: letterforms survive a glance because their shapes are already known,
while these glyphs are a filled disc whose whole meaning is a few knocked-out pixels. Hue separates
six of them instantly and the glyph confirms which, which also keeps the encoding usable for a reader
who cannot separate two of the hues.

That is a *categorical* scale, not a fourth status role, which is what keeps `serval_tokens.dart`'s
three-role rule intact: a role answers "does this need me?" and there are three because there are
three answers; these answer "which of these is it?" and carry no urgency. `Serval.alert` and
`recording` stay off limits — an angry voice borrowing the hue that means *someone should look* would
turn a description into a summons.

**A turn's emotion is resolved on the module, not joined in the App**, and that is not a preference.
An utterance's `timestamp` is when the VAD *emitted* it — after the speech plus the trailing silence
it waited through — so aligning utterances against turn times needs `Vad:MinSilenceSeconds`, which
never leaves the module. A client-side join has the span both backwards and offset, and fails
silently. Resolving at the source also handles the straddling case: where one utterance crosses a
speaker change its audio is already re-cut and re-transcribed per speaker, and SenseVoice returns
emotion in the same forward pass, so each turn gets its *own* reading rather than one copied across
both voices. See [telemetry.md](telemetry.md#the-two-speaker-streams).

**The activity title sits above its filter, not beside it.** Four segments and a heading do not share
376 px without the segments losing their type size and the heading running to an ellipsis. On its own
line the control has the column to itself, which is the only use of
[`SegmentedControl.expand`](../App/serval_app/lib/widgets/segmented_control.dart).
### Forms and the wire

**Fields are `EditableText`, so they must be
[`NocturneEditableText`](../App/serval_app/lib/widgets/nocturne_editable_text.dart).** Nothing here
uses `TextField` — it arrives with Material's underline, filled box and focus blue, which is the
flood the design system forbids. But what comes with `TextField` and *not* with a bare
`EditableText` is the whole pointer layer: dragging to highlight, double-click for a word,
triple-click for a line, a caret placed where you clicked, and the copy/paste menu. `EditableText`
paints characters and takes keystrokes; the gestures live in
`TextSelectionGestureDetectorBuilder`, which `TextField` wires up and nobody else does.

The failure is quiet, because clicking still works: `RenderEditable` keeps a tap recognizer of its
own, which places a caret *and claims the pointer*, so the second click and the drag never reach
anything. `rendererIgnoresPointer: true` hands the pointer over. **Never reach for a raw
`EditableText`.**

**Casing is not uniform, on purpose.** `/api/cameras*` is camelCase
([`camera_record.dart`](../App/serval_app/lib/data/camera_record.dart)); telemetry is snake_case
([`telemetry_documents.dart`](../App/serval_app/lib/data/telemetry_documents.dart)). One naming
strategy over both silently deserializes half of it to defaults.

**`PUT` replaces, it does not merge.** The settings form edits a copy of the whole fetched record and
sends all of it. That is why the ONVIF password is masked *and carried* — showing an empty field and
sending it would delete the camera's credentials on the next save of any other field.

**Role rules are mirrored client-side**
([`CameraRecord.roleProblem`](../App/serval_app/lib/data/camera_record.dart)): exactly one stream for
`detect` and one for `live`, at most one for `record`, `recording` never on without one, and a
transcode only on a stream that either records or has no jobs at all. *Save camera* is disabled with
the reason shown rather than posting a request that 400s — which is why roles are chips rather than a
multi-select.

**A stream with no jobs is a saveable end state**, not a half-finished one: it keeps its address and
nothing is pulled from it. The card says so under the chips, because an empty chip row otherwise
reads as *not set up yet*.

**Recording is a field, and it lives on *Recording*.** `CameraRecord.records` — which the REC
dot, the registry subtitle and the timeline's *Nothing is kept* all ask — is `recording && a stream
carries record`, and everything below it treats the two ways of being false identically. Only that
one section tells them apart, because only there does the difference change what to do: with a
`record` stream assigned the switch acts, and without one it is inert and points at *Streams* rather
than guessing which stream to hand the role to. The retention slider is the exception to the
section's own greying — it stays live whenever a `record` stream is assigned, since the Server keeps
expiring what is on disk whether or not anything is being added to it.

### Live view and PTZ

**ONVIF auto-stops after 1 s**, so press-and-hold re-sends `move` inside that window;
[`PtzPad`](../App/serval_app/lib/widgets/ptz_pad.dart) owns that repeat.

**Talk-back needs no renegotiation, and does not open the microphone until you speak.** For a
`twoWayAudio` camera the offer declares a *sending* audio m-line from the start — but empty. The
first press of *Hold to talk* calls `getUserMedia` and hands the track to that sender with
`replaceTrack`, which changes no SDP, so there is still nothing to renegotiate on a connection that
has nowhere to renegotiate to. Every press after that only flips `track.enabled`; the device stays
open for the rest of the session. The cost is that the first press of each session waits on a
permission sheet, which is why the button reports where the microphone has got to
([`MicrophoneGate`](../App/serval_app/lib/playback/microphone_gate.dart)) rather than assuming it
has one.

**A dead live view falls back to the wall's snapshot**, over the design's placeholder — a real frame
from a second ago beats a black rectangle.

**PTZ controls are drawn from what the camera reports, never from its settings.** `ptzConfigured`
means only that an ONVIF endpoint is set, and measured against the live NVR that was wrong for both
of its cameras: the bullet camera has no PTZ service at all yet got a pad and a zoom slider, and the
pan/tilt camera has a fixed lens and no stored presets yet got a zoom slider and a home key firing
preset `'1'`. `ptzProbeFor` asks the camera instead, over a route separate from the registry read —
probing is a live SOAP round trip, and `main` awaits `GET /api/cameras` before the first frame, so
folding it in would cost every cold start an ONVIF timeout per unreachable camera. Where the camera
cannot be reached the app draws no controls plus the Server's own words, because hiding them silently
is indistinguishable from "this camera has no pan/tilt".

**The centre key follows a ladder decided on the camera's answer** — real home position, else a
preset named *home*, else the only preset, else a menu, else no key at all. Pinned in
[`ptz_probe_test.dart`](../App/serval_app/test/ptz_probe_test.dart).

**The zoom track is a position, and says whose.** Linear over the lens's travel, driven three ways
depending on what the camera admits to — absolute position where it takes one, velocity nudges plus a
`GetStatus` re-read where it does not, dead reckoning where it reports no position at all. The tiers
are in [live-view.md](live-view.md#where-the-lens-actually-is); what matters here is that
[`ZoomPosition`](../App/serval_app/lib/models/ptz.dart) carries **whether the number was measured**,
and the readout appears only when it was. Dead reckoning is a count of our own commands: it is wrong
the moment anything else touches the camera, and printing it where a measurement goes is the claim
the whole type exists to prevent. The knob moves to the finger immediately in every tier, because a
control that waits on a SOAP round trip feels broken; the re-read that follows is what makes the
position true rather than merely requested. That re-read is deliberately later than the auto-stop:
asking while the lens is still travelling reads a position it is passing through and snaps the knob
backwards.

### Preferences, and getting media out

**The wall layout is the account's; the volume and the sidebar are the machine's.** An arrangement of
tiles is something you make once and want on the next browser, so it lives in
`GET`/`PUT /api/preferences` — a `user_preferences` collection keyed by the account id, readable and
writable by **any** signed-in role, because arranging your own wall is not an administrative act. The
other two stay in `shared_preferences`: how loud you want a camera and whether a 376px column fits are
properties of what you are sitting at, and syncing them would let a phone dictate a desktop's volume.

The volume is the machine's **and the camera's** — `shared_preferences`, one key per camera id. How far
a camera needs lifting is a fact about its microphone and where it points, so a level found once should
not have to be found again on the way back, and one number shared across cameras is wrong for at least
one of them. That is only half a contradiction with the paragraph above: the *scope* is the camera, the
*storage* is still this machine.

The camera itself keeps two related fields, and neither is the applied level. `PlaybackGainDb` is where
the slider **starts** before a client has a position of its own, so a camera somebody has calibrated is
not silent on every new browser; `PlaybackGateRmsThreshold` is that camera's noise floor, measurable
only at the camera against its own live meter. Both are on the Server because they are properties of
the microphone; the position you drag is not.

One control carries all of it, and it **reads 0 to 100 and nothing else**. Unity — full volume with
nothing added — is three quarters along, marked and with a detent, so it reads 75%; the quarter above
it amplifies, up to 10x, and says so by changing colour rather than by printing a number. A readout
that went to 1000% would be four digits of arithmetic nobody asked for, and the same rule binds the
camera's starting volume in settings: both quote positions on the track, never multipliers.

The pipeline underneath is still two axes: a position splits into the volume a player takes and the dB
behind it, because attenuation is free on every backend while amplification needs a gate in front and
a limiter behind, and the limiter's threshold only means something if it knows how much gain preceded
it. `playbackFromTravel` is that seam, and `volumeLabel` is the reason none of it reaches the screen.

It is not a second `/api/settings`, which is deployment configuration, Admin-only to write, ends its
write path in a configuration reload, and is a catalogue of values with defaults and sources rather
than structured state.

The route carries **no username** — the Server takes the account off the token — so no request shape
reaches somebody else's wall. Access control is an address that cannot express the question rather
than a check a future handler could forget.

The write **merges**, which is the opposite of the camera registry's PUT and deliberate: preferences
are meant to grow, and under replace semantics a cached older build would erase a preference it had
never heard of every time somebody dragged a tile. An empty `wallLayout` array still clears it, so
absent and empty differ and *reset* stays expressible. Server-side validation stops at what would
make the document nonsense — on the grid, at least one cell, each camera once — and deliberately
**not** at overlap or packing, which are `WallGrid`'s rules and would become a second source of truth
here.

**Saving media splits by platform like playback does.** `path_provider` has no web implementation and
a `Blob` has no native one, so [`lib/media/`](../App/serval_app/lib/media/) resolves a backend by
conditional import. The web build needs the Server's CORS policy to expose `Content-Disposition` —
`AllowAnyHeader` governs the *request* — or it cannot read the filename it is meant to save under.

**The clip range clamps to the coverage span containing the anchor**
([`clip_selection.dart`](../App/serval_app/lib/models/clip_selection.dart)), which does two jobs at
once:
`clip.mp4` 404s on an empty range, and one span is one ffmpeg session is one fMP4 init, which is
exactly the boundary the export stops at. The common truncation case is therefore impossible rather
than merely reported. There is **no progress percentage anywhere** — a clip has no `Content-Length`
and the browser client buffers the whole body, so Linux shows a live byte count and web shows none.

### Replay

- **Two players behind one interface** ([`lib/playback/`](../App/serval_app/lib/playback/)). Live is
  WebRTC, but a recording is an HLS VOD playlist, and no single Flutter player covers both targets:
  media_kit's web backend drops libmpv for a plain `<video>`, which Chrome cannot play HLS from. So
  Linux gets media_kit and the web gets [hls.js](https://github.com/video-dev/hls.js) over an
  `HtmlElementView`, resolved by a conditional import. Both open the *same* URL, so there is one
  playback contract and the split stops at that directory. hls.js is vendored in
  [`web/hls.js`](../App/serval_app/web/hls.js) rather than fetched from a CDN, for the same reason
  the fonts are.
- **A seek opens fifteen minutes, not the whole day.** A VOD playlist for a 24 h range is ~21,600
  entries; for fifteen minutes it is ~225, which parses instantly — so the picture appears quickly
  and every drag *inside* that window is a decoder seek with no request at all. The cost is a
  reopen roughly every fifteen minutes of continuous replay, which is one constant in
  [`ReplayController`](../App/serval_app/lib/playback/replay_controller.dart).
- **Replay is wall-clock, so a break in the recording is played, not skipped.** An ffmpeg restart
  leaves a real hole — on the live Server, a stack restart costs 20-odd seconds on every camera, a
  few times a day. A window is clipped to the end of the run it starts in, and playback arriving
  there hands over to a plate reading *No recording* while the playhead keeps advancing on a clock
  at the current rate; the far side opens when the playhead reaches it. Stitching the two sides
  together would put frames minutes apart in consecutive positions and leave no honest playhead —
  the position-to-instant map assumes media time and wall time advance together, and across a hole
  they do not. Clicking past the break on the scrubber skips it, which is what drawing it there is
  for. The wall behaves the same way: its playhead is derived from a master clock, and a tile with
  no footage draws a plate.
- **Both players seek by `at - windowFrom`, so `windowFrom` is the playlist's own media start** —
  the segment boundary at or before the instant asked for, *not* the instant. Passing the instant
  for both makes that difference zero, and a playlist opens at its own beginning: up to a whole
  segment before what was asked for, on every seek and every reopen. Neither player reports this;
  the playhead goes on believing it is at the instant it asked for. The seek is also re-applied
  once playback has actually started, because both players discard one issued too early —
  `Player.open` resolves before libmpv has parsed the playlist, and `loadSource` attaches a
  MediaSource asynchronously.
- **The scrubber's marks come from telemetry and its coverage from
  `GET /api/cameras/{id}/coverage`**, *not* from `/api/cameras/{id}/recordings`. That route returns
  one entry per segment — measured against the live Server, two hours is 1798 rows and 208 KB, so a
  day is ~21,600 rows and megabytes of JSON — which is an expensive way to learn that the camera
  recorded all day. `/coverage` is the same information merged into one span per ffmpeg session,
  typically one to three for a day.
- **A fixed period is never refetched.** Its edges do not move, so the 30-second TTL would ask the
  same question forever; marks arriving meanwhile still ride the events socket. The span is clamped
  to 24 hours — the Server validates no maximum on `/coverage`, `/recordings` or `/vod.m3u8`, so
  this is the only guard there is — and what survives the clamp is the *right* edge, because the
  end you named last is the end you were reaching for.
- **There is a third kind of range, and it grows: pinned on the left, the clock on the right**
  (`TimelineRange.since`). It exists for arriving from the feed on something recent, where the half
  hour after the moment has not happened yet: drawn out to where it *will* be, that half of the bar
  is empty future that never fills, because a window with a fixed right edge is never refetched. So
  it starts as wide as there is footage for and widens with the clock until it reaches its span,
  after which it slides like any live window. It is `live` for exactly that reason — the anchor and
  the fetch both refresh on the TTL — which is why `_anchorFor` asks the range for its left edge
  rather than measuring one back from the right, and why the range button and the empty column
  measure the window they are describing instead of reading its label.
- **The range is one button and a panel that applies as you touch it**
  ([`timeline_range_panel.dart`](../App/serval_app/lib/widgets/timeline_range_panel.dart)). A preset
  row with a *Custom* modal beside it says the presets are the real answers and a date is the
  exception, which is backwards once a camera has days behind it — so the spans, the last week of
  days and the calendar behind *Earlier…* are all ordinary things to pick, and every one redraws the
  track immediately. Typing in the two time fields is the one thing that waits: a 350 ms debounce,
  because each keystroke is otherwise a new `TimelineRange.key` and with it a `/coverage` read and
  four telemetry reads for a half-typed time. **24 hours is the ceiling in both halves** — the
  longest span offered, what *whole day* means exactly, and where a typed window holds — because a
  day is already ~21,600 segment rows and wider buys resolution nobody can see.
- **The track is six layers, one per kind of thing that happened, and nothing crosses between
  them.** Marks merge only against their own layer, so a run is as wide as *that kind* went on. The
  hue is what the mark was, which is the whole reason to look at the bar before clicking it: an
  alert heard (`alertSound`), an alert seen (`alert`), a sound (`markSound`), an object
  (`markObject` — the accent the single band used to be), speech (`markSpeech`), and a scene
  description (`markScene`). Merged in one pass instead, the widest claim wins the whole run: one
  person in an evening of speech paints hours of track orange, and a burst of descriptions swallows
  the sound in the middle of it.
- **Priority decides who owns a shared pixel, and ties are common.** The order is alerts, then
  sounds, then objects, then speech, then scenes. Sounds outrank objects because a sound is the one
  reading the picture cannot give you afterwards. Scenes sit at the bottom because a scene describes
  the detection that triggered it and lands on the same instant, so cutting it away under the
  objects drops paint that was saying the same thing twice.
- **A mark belongs to exactly one layer.** An alert is in its alert layer and in no band. The two
  size their marks differently, so a mark in both is drawn twice at two different widths — an alert
  shorter than the 12 px tick ceiling leaves a stub of band sticking out past its own orange,
  claiming ordinary activity for time where the only thing that happened was the alert.
- **Every layer is cut clear of the ones above it, and that is not cosmetic.** Each fill is
  `Nocturne.mix`, which is alpha rather than an opaque blend, so a pixel painted twice comes out a
  colour that means nothing — and means it only on the cameras busy enough to produce the overlap.
  `layers()` keeps a running union of everything already claimed and cuts each layer against all of
  it at once; `timeline_scrubber_test.dart` pins the property directly by asserting no two painted
  rects overlap with all six kinds on the bar together.
- **The layers size their marks differently, and that is the point.** A band mark is a fixed-size
  tick — `(3 + seconds).clamp(3, 12)` px, the same at 1 h as at 24 h — because the band is *scanned*
  and a short event has to stay legible. An alert is *aimed at*: you drag the playhead into it
  expecting to find what it is telling you about, so it is drawn across the time it actually covers,
  floored at 3 px so an instant cannot vanish. Sized like a tick, a ten-second visit is twelve pixels
  wide, which at 1 h is over half a minute — the playhead dropped into the orange lands after the
  person has left, on footage with no box, which is indistinguishable from a broken overlay. The
  floor is the one place this still overstates: below 3 px it is wider than the event, so the
  guarantee is "the alert starts here", never "and ends there".
- **A band chains further than you would expect.** Marks under about two pixels apart merge, which
  at 12 h is ~86 s, so a camera with speech all evening becomes one continuous blue block. That is
  honest for a band meaning "this kind of thing went on here", and is exactly why the merge is per
  layer.
- **The hover readout names the colour, because a legend has nowhere to live.** Six hues is more
  than a bar 44 px tall can explain, and the header row is already at `scaleDown` on a 1440 layout
  with the activity column open. The label already following the cursor says what is under it —
  `14:32:07 · Sounds` — so the palette teaches itself. Hover is a desktop reading; touch falls back
  to the colour alone, which is all the bar ever said.

## What still has no backend source

Two kinds of thing are on this list, and the difference is visible in the code. Where the element
is still **drawn** per the design, the use site carries a `// STUB:` saying what is missing — those
are what `grep -rn STUB App/serval_app/lib` finds, and every one of them has a row below. Where the
element was **dropped** instead, because a plausible-looking number was worse than an absent one,
the reason sits at whatever draws the rest of that line — the camera's subtitle, the PTZ readout —
and there is no marker to find.

The object detector emits geometry, so the stage's `DetectionOverlay` is fed from telemetry, from
whichever of two readings matches what is on screen:

- **Live** asks `detectionsFor`, which returns only episodes still open. A box over something that
  has left would be a claim about the present that is no longer true.
- **Replay** asks `detectionsAt(camera, playhead)`, which reads the episode's `track` — see
  [the track](detection.md#the-track) — so a box sits where the object was at that instant rather
  than where it looked clearest. It draws nothing inside a gap, outside the episode's own span, or
  for an episode recorded before tracks were kept.

**Both draw alerts and nothing else.** The detector stores every class it is configured for —
eight by default, including cars and trucks — but only an alert is a claim that someone should
look, so a car, a truck or a person who never cleared `AlertMinConfidence` is stored, queryable,
and drawn nowhere: no box, no tick on the scrubber, and no contribution to its activity band
either. One predicate in `LiveServalRepository` decides it for all three, which is what stops the box
and the tick disagreeing — a parked car drawing a full alert-orange box over the video while the same
episode puts a calm tick on the track underneath it.

That is also why `DetectionOverlay` can paint `Serval.alert` unconditionally: every box that
reaches it is already an alert.

The boxes for a replay window are fetched when the playlist for that window is opened, not per
frame: the live feed is capped at 300 documents and does not reach as far back as the scrubber
does, so a playhead dragged into yesterday would otherwise find nothing to draw and be unable to
tell that from nothing having happened.

Two things have to be right before a box lands on the thing it describes, and neither is visible
without something to compare against — which is why the check is a recording of a moving object with
its position measured out of the recording itself:

- **A normalised box is a fraction of the picture, not of the slot the picture was given.** Nothing
  is fitted by cropping — see [above](#a-camera-picture-is-never-cropped-to-fit) — so unless a stage
  happens to share its picture's aspect ratio the picture is letterboxed inside it: at 640x480 in
  the single-camera stage, a 46 px pillarbox each side, which is most of a person. `PictureAligned`
  puts the overlays in a `Center` + `AspectRatio` so they cover the picture. The size comes from
  whichever layer is showing, since only it knows — `VodPlayer.videoSize` on replay, and
  `WebRtcView` publishes the negotiated track's into a notifier the screen owns, because the overlay
  is its sibling rather than its child. It falls back to the stage while the size is unknown; a box
  slightly out beats no box, and it corrects itself when the first frame's dimensions land.

  `WebRtcView` publishes the *displayed* size, swapping the stream's dimensions at 90 and 270:
  `RTCVideoValue` carries rotation and `RTCVideoView` letterboxes to the rotated aspect, so the
  overlay has to follow what is on screen rather than what was encoded.

  `AlertStill` does the same arithmetic from a URL rather than from a player: it measures its poster
  with `resolveImageSize`, which resolves the provider the `Image` is already drawing and is
  therefore a cache hit rather than a second fetch. It re-measures when the URL moves, because it
  does — an alert's poster is the camera's snapshot the moment it is raised and the exact frame once
  the clip is cut, and `alertPosterUrl` carries the clip state in the query so the second one is
  actually fetched.
- **Playback position counts from the segment boundary, not from the instant asked for.** Segments
  are four seconds and a window can be asked for anywhere inside one, so `/vod.m3u8` starts at the
  boundary at or before `from` and says how far in the request actually sits with
  `EXT-X-START:TIME-OFFSET`. `ReplayController` subtracts that (`_startOffset`) from every position
  it turns into a wall-clock instant, and adds it to every seek. Without it the playhead reads up to
  a whole segment ahead of the frame on screen — measured at 3.9 s, which is a curiosity on the
  timestamp pill and about 43 px of drift on a box, enough to blank one by landing in a gap that has
  not happened yet. The offset is read from the playlist rather than from the player because the two
  backends disagree about the tag: hls.js honours it, ffmpeg's HLS demuxer does not implement it.

The caption is not given the same treatment. It is a claim about *now* and has no second reading,
so replay still drops it.

| Element | Why |
|---|---|
| `1080p` / `2K` / `NIGHT` badges | No resolution on the camera model — and the snapshot cannot stand in, since `Ingest:SnapshotMaxMegapixels` fits it to a pixel budget. |
| *Someone's here* and the tile's attention dot | Sourced, but from alert **sounds** only — `_alertedRecently` reads `SoundDocument`, so an alert detection lights the activity card and the scrubber without lighting the tile it happened on. The alert card and the alerts filter are fully sourced from `is_alert` on both sounds and detections. |
| Motion marks on a camera with **motion** gating and vision off | Motion *is* scored on every frame, but it is logged and discarded unless the vision model turns it into a `SceneDocument`. Nothing persists a motion event on its own. A camera on the **object** gate does not have this problem: detection episodes are their own records and do not need the vision model. |
| Tile drag and resize | Fully sourced now — `GET`/`PUT /api/preferences`, per account. See the note above on what stayed local and why. |
| `2560 × 1920 · 20 fps · h264` per stream | The Server ffprobes internally and publishes nothing — and `SourceProbe` asks for `codec_name,width,height`, so the frame rate is not merely unpublished, it is never measured. |
| Zoom factor (`2.4×`) | Not derivable rather than merely unsourced: ONVIF's generic zoom space is a fraction of the lens's travel, nothing publishes the optical range that would make it a magnification, and the curve between them is vendor-specific. The track reads a percentage of travel where the camera reports one — see [live-view.md](live-view.md#where-the-lens-actually-is) — and nothing where it does not. |
| `up 26 days` | ONVIF's Device service exposes the system clock, not uptime. Make, model and firmware are real. |
| The ready-made replies | No phrase store, and no way to play audio at a camera outside a live WebRTC session. Not rendered, and the widget was removed — when a phrase store exists this is a fresh build, not an un-commenting. |
| A still at a past instant | `snapshot.jpg` is the *latest* frame, served from memory; no route extracts one from the archive, and there is no on-disk copy to fall back to — frames are consumed and deleted as the detector reads them, so the route 404s for about a second after a Server restart. So *Snapshot* can only ever save the live picture, and it is refused while replaying with the reason on the status line. This is also what empties the **poster on the Save-clip dialog**: the design puts a frame from the middle of the range behind the duration pill, `SaveClipDialog.poster` is declared for it, and there is nothing to pass. `/api/clips/{id}/poster.jpg` is that picture from the wrong side of the save — addressed by a clip id that does not exist until the save succeeds. |
| `84 MB or so` on the Save-clip dialog | The one number in the App reasoned from the design rather than from a camera: `_assumedBytesPerSecond` is ~12 Mbps, which is what the mock's *84 MB for 55 seconds* works out at. `RecordingSegment` indexes `DurationSeconds` and no byte count, so nothing weighs the range today and the dialog says *or so* for that reason. It is measurable, though: a clip is a copy of the segments covering its range, and those are known and on disk, so summing their sizes would answer exactly. Worth naming because the retention projection two screens away is measured rather than estimated — see below — and on screen the two numbers look alike. |
| *Dismiss* on a wall alert | Dismissing means *handled*, which is a claim about the alert and not about the screen it was clicked on — so it should survive a reload and reach everyone else watching the wall. The old control was a `Set` on one screen's state and did neither, so it was removed rather than left lying. **The route it wants now exists**: `POST /api/alerts/{id}/dismiss`, keyed by the detection's or sound's own id, which is exactly what an `ActivityItem` carries — see [alerts.md](alerts.md). What is left is deciding whether the wall clearing a card should also clear it from the Alerts queue, which is a product question rather than a missing endpoint. |

Some things the design left as stubs have a real source, derived rather than invented:

- **Offline** — snapshot staleness on the wall socket, which is what the model's own comment
  prescribed. A camera that is switched off reads as *turned off*, not as broken.
- **Recording** — `enabled && a fresh frame && the camera keeps anything`. Exact here rather than a
  guess: one ffmpeg per camera produces the snapshots *and* the segments from a single connection.
  The last term is what keeps a camera that is watched but not kept off the amber *Starting up* dot
  it would otherwise sit on forever.
- **Audio activity** — an `utterance` on `/api/events` in the last few seconds.
- **`1.8 TB of 4 TB`** — `GET /api/system/stats`, which is a `statvfs` on the volume the media root
  resolves to. The bar under it splits Serval's own recordings from everything else on the volume,
  because on a shared pool those are very different numbers and a single "used" bar would blame a
  full disk on Serval when something else filled it.
- **The disk a retention setting will cost** — measured, not estimated. The Server walks each
  camera's directory and divides by the span back to its oldest indexed segment, which beats a
  nominal bitrate multiplied out: it is what the camera has actually written, audio and keyframes
  and all. The projection only appears once the slider moves off the span the measurement covers.
- **Saved clips in the storage breakdown** — the disk sweep measures every directory under the media
  root, not only the cameras', so clips get their own row with what they weigh. It carries the note
  *kept until you delete them* rather than the *keeping 7 days* a camera shows, because they are the
  one thing on that volume no sweep ever prunes and a row that looked like every other row would not
  say so.
- **The sparklines under the processor, memory, GPU and accelerator meters** —
  `GET /api/system/stats/history`, which is a ring of the samples the Server takes every few seconds.
  
  Two properties of that route matter to the drawing. **History is in memory**, so it starts empty
  after a restart and fills in — the chart draws the short line it actually has against the full
  retained window rather than stretching it edge to edge. And **a sample the Server could not
  measure stays null**, drawn as a break in the line: a GPU series flattened to zero would claim an
  idle GPU on every host whose driver publishes nothing, which is the same lie as a meter resting
  at 0% and is guarded on both sides of the wire. An Intel host granted `CAP_PERFMON` part way
  through a window is exactly this — real figures after the break, nothing before it.

  The GPU meter carries a second line the others do not: where the source reports engines
  separately, as i915 does, the meter is the **busiest** engine and the caption names which. "Video
  41%, render 3%" and "Render 41%, video 3%" are the same meter and different servers.

  A series that is *entirely* null gets no chart at all, only the Server's sentence saying why —
  an empty frame would imply the line is merely offscreen.

- **The Edge TPU meter**, where the object detector runs on accelerators — a pair of Coral sticks on
  the N100 deployment. Its bar is the **pool**, which is the one place it deliberately departs from
  the GPU meter directly above it. Engines are not interchangeable, so a GPU has to pick one and the
  busiest is the useful pick; accelerator devices are, since a frame goes to whichever is idle and
  the detection budget is their sum, so "how close is the pool to saturated" is what a bar can mean
  here. Each device's own throughput, latency and link speed go in the caption, where a Coral
  delivering a third of its twin is a fact about the hardware rather than a meter jumping between
  two of them. See [coral.md](coral.md#on-the-status-page).

  **This is the only meter on the page that is hidden rather than degraded**, and the exception is
  worth stating because everything else here argues the opposite way. Every other meter describes
  hardware any host has, so a missing figure earns a sentence saying why — that is the whole design
  of the payload. Most hosts have no accelerator at all, and a permanent *not reported* bar on all
  of them would be noise standing in for an answer nobody asked for. A host that *has* accelerators
  and has lost one keeps the meter and says so: the detector never drops a device from its pool, so
  the card cannot vanish at the moment it starts mattering.

## What is deliberately *not* on the wall

`GET /api/system/stats` reports processor, memory, GPU and accelerator as well as disk, and only two
conditions are ever allowed to put a strip across the top of the wall: free space under its
threshold, and container memory near its cgroup limit. Both destroy something silently — the first loses footage,
the second gets the container killed with no warning and no in-app trace.

High CPU, a pinned GPU and a saturated accelerator are shown as meters on *Server status* and raise
nothing. A GPU at 100% during a VAAPI transcode is the encoder working correctly, and no CPU limit is
set on either deployment, so there is no ceiling for processor load to be near. An Edge TPU at 100%
is likewise a machine doing its job — and where it stops being that, the consequence already has an
alert of its own: coverage falling, or `DetectionDegraded` when a device is actually lost. A banner that fires for "working
as configured" is one people stop reading, and the disk warning has to be believed.

The strip is a sibling of the wall header rather than part of it, for the reason the header's own
comment gives about its fixed height. A warning can be put aside for the session and no longer —
a volume that is 94% full will still be 94% full in five minutes — and a *critical* one cannot be
dismissed at all.
