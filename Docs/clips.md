# Saved clips

Recordings roll off. A saved clip does not — it is the one thing in Serval that stays until somebody
deletes it, which is what most of its design follows from.

A clip is an **independent copy**, not a bookmark into the recording:

```
{Media.Root}/{Media.ClipsRoot}/
    {clipId}.mp4      the video, with audio
    {clipId}.jpg      a frame from the middle of it
```

plus a Mongo document in `clips` holding the range, the size, who saved it, and **a frozen copy of
the telemetry for its window** — the detections, scenes, speech and sounds as they were. A clip that
merely pointed at a time range would become an unplayable row with a transcript attached the moment
retention caught up with it.

## Why the files live where they do

Two constraints, and between them there is only one place to put a clip.

**Not inside a camera's directory.** [`RetentionWorker`](../Server/Serval.Server/Recordings/RetentionWorker.cs)
deletes inside `Media.Root/{cameraId}` and nowhere else, for the filenames its index hands back. A
clip stored under a camera would be pruned along with the footage it exists to outlive. Sitting
beside the camera directories makes it exempt by construction rather than by a rule someone has to
keep in step.

**Not one directory per clip.** [`DiskUsageScanner`](../Server/Serval.Server/Vitals/DiskUsageScanner.cs)
refuses to recurse, so that a symlink or a stray mount cannot turn a bounded walk into an unbounded
one. A clip one level down would measure as zero bytes, and the only footage that never rolls off
would be the only footage missing from the storage figures. So: flat files, named for the clip.

`Media:ClipsRoot` is a relative name combined against `Media:Root`, or an absolute path to put clips
on different storage. Like `Media:Root` it is environment-only — paths stay out of the settings
catalog.

## The video is not the same file the export streams

Both come out of [`ClipExporter`](../Server/Serval.Server/Media/ClipExporter.cs), which concatenates
the fMP4 `init` and its segments into ffmpeg and remuxes with `-c copy`. They differ in one flag,
and the reason is that **a pipe cannot seek**:

| | `WriteAsync` → the response | `WriteFileAsync` → a saved clip |
|---|---|---|
| Output | `frag_keyframe+empty_moov+default_base_moof` | `+faststart` |
| Result | fragmented MP4, no `Content-Length` | ordinary MP4, `moov` at the front |
| Can be scrubbed | no | yes |
| Can resume a download | no | yes |

A kept clip is going to be watched, seeked and shared, so it gets the container that supports all
three. The streamed export keeps its shape — nothing about *Download* changed.

## Ranges are whole segments

A segment is the smallest thing that can be copied without re-encoding, so a clip's ends are segment
boundaries and the App's trimmer snaps to them. That is what lets the range asked for and the range
saved be the same thing — there is no "requested" time stored separately because there is no
difference to record.

Two consequences worth knowing:

- **The nudge is a segment, not a second.** Under `-c:v copy` a segment is as long as the camera's
  GOP made it, so the App reads the step from the segments themselves rather than from
  `Ingest:SegmentSeconds`.
- **`InRangeAsync` is half-open.** A segment starting exactly at `to` shares no time with the window,
  and including it made every boundary-snapped clip one segment longer than asked for.

A range crossing an ffmpeg restart is **refused**, not truncated. The streamed export truncates and
says so in a header, which works when somebody is watching the response; a clip silently half the
length asked for would be discovered weeks later by the person who needed the other half.

## Saving is a job

`POST /api/clips` validates and returns **202** with the clip in state `writing`. Half an hour of a
main stream is a couple of gigabytes and tens of seconds of ffmpeg — too long to hold a request open
and far too long for a dialog to look frozen. [`ClipWriteWorker`](../Server/Serval.Server/Clips/ClipWriteWorker.cs)
does the rest, one clip at a time so a remux never competes with the recorders for the same volume.

Everything a caller can get wrong is refused before the 202: an unknown camera, a backwards range,
one over `Media:ClipMaxMinutes`, one with no footage, one crossing a restart.

`GET /api/clips/{id}/status` reports `writing` / `ready` / `failed` and the bytes written so far —
a count rather than a percentage, because there is no total until the file is finished. A `writing`
clip is not listed, so a half-written one never appears as a card that cannot be played.

**A clip left `writing` by a restart is failed on the next boot**, and its partial file removed. The
queue lived in the process that stopped, so nothing will ever pick it up; left alone it would sit in
that state forever.

## What's in it

The one-sentence summary is a **vision pass over the finished clip** — frames sampled evenly across
it, handed to the same `IVisionInferenceRunner` the scene descriptions use, with `Ai:Vision:ClipPrompt`.

It is the only place in Serval that describes a *span*. Every other description is one frame's — a
scene record is what the camera could see at an instant — so a clip's summary could not be assembled
from them without claiming a still was a summary. See [telemetry.md](telemetry.md).

Entirely best-effort and off the save path: a server with no vision model saves clips normally and
they simply have no summary, which the App draws as no block rather than an empty one.

## Who may change one

A **shared library with owned edits**. Everyone signed in sees every clip, because the footage is
the household's rather than the account's. Renaming and deleting are restricted to the person who
saved it, or an Admin — they are the two operations nobody else can undo, and an Admin has to be
able to clear up after an account that is gone.

The App hides what it knows will be refused; the Server enforces it regardless, with a 403.

## Routes

| Route | Auth | |
|---|---|---|
| `GET /api/clips?query=&cameraId=` | Bearer | Ready clips, newest first. `query` matches the name and everything said inside. |
| `GET /api/clips/{id}` | Bearer | With the frozen telemetry, speech carrying its offset from the clip's start. |
| `POST /api/clips` | Bearer | 202 and a clip id. |
| `GET /api/clips/{id}/status` | Bearer | How far the write has got. |
| `PATCH /api/clips/{id}` | Bearer | Rename. 403 unless owner or Admin. |
| `DELETE /api/clips/{id}` | Bearer | Row and files. Same rule. The only way a clip goes away. |
| `GET /api/clips/{id}/clip.mp4` | **MediaAccess** | Range-served, real `Content-Length`, named after the clip. |
| `GET /api/clips/{id}/poster.jpg` | **MediaAccess** | 404 where the poster could not be made. |

The two file routes take a `?stream_token=` as well as a header, like the HLS routes: a `<video>`
element and libmpv are handed a URL and cannot set one.

## Settings

| Key | Default | |
|---|---|---|
| `Serval:Media:ClipsRoot` | `clips` | Environment-only, like `Media:Root`. Absolute paths allowed. |
| `Serval:Media:ClipMaxMinutes` | `30` | In the settings catalog. The App renders its own "up to N min" caption from it. |
| `Serval:Ai:Vision:ClipPrompt` | see `VisionOptions` | What the summary asks for. |
| `Serval:Ai:Vision:ClipFrames` | `4` | Clamped by the backend's `MaxFrames` — the RK3588 NPU path takes exactly one. |
