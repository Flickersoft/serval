# Recording

How the Server gets a camera's bits onto disk, what it does and does not re-encode, and how a
range comes back out as a file.

A camera is pulled by one ffmpeg process producing two things from a single connection: HLS fMP4
segments (which *are* both the recording and the live stream — the standard NVR trick) and
a ~1 fps JPEG snapshot for the dashboard.

A camera does not have to be recorded at all. The `record` role is optional — leave it off every
stream and the camera runs the snapshot process alone: still watched, still alerting, still viewable
over WebRTC, with nothing on disk and none of this page in force. See
[Streams and roles](../Server/Serval.Server/README.md#streams-and-roles).

## Codecs: copy by default

**Serval does not re-encode video you did not ask it to.** A camera's bits go into the archive
exactly as they arrive — no decode, no encode — provided the codec is one `Ingest:VideoPassthroughCodecs`
lists. It defaults to `["h264", "hevc", "av1", "vp9"]`: the four video codecs with a standardised
ISO-BMFF sample entry that ffmpeg's mp4 muxer writes (`avc1`, `hvc1`, `av01`, `vp09`) and that a
browser can plausibly play.

A camera sending anything else — mjpeg, mpeg4, h263 — is **an error naming the codec**, not a
silent transcode. Normalizing everything to one configured codec instead would make the expensive
outcome the default, and reach it for reasons nobody sees: an unlisted codec, or a two-second
ffprobe blip at session start pinning a 4K camera to a permanent re-encode.

**To re-encode, ask for it, per stream:**

```json
{ "name": "main", "url": "rtsp://…/main", "roles": ["record", "live"],
  "transcode": { "codec": "h264", "bitrate": "4M" } }
```

`codec` is one of `h264`, `vp9`, `av1`. `bitrate` is optional and falls back to `Ingest:Bitrate`.
Transcoding is per-stream rather than server-wide because it costs roughly a core (or a share of a
GPU) per camera, continuously — you fix the one camera that needs it, not all of them. Only the
`record` stream can carry it; nothing else is written to disk, and a transcode elsewhere is
rejected rather than ignored.

The request is checked against the encoders **this host's ffmpeg actually has** and rejected with a
400 naming the missing one. See [Transcoding and hardware](#transcoding-and-hardware).

**The trade you are now making by default is reach.** A deployment of HEVC cameras archives HEVC,
which plays in Safari, Edge, and a Chrome with hardware decode — and not elsewhere. Each session
says which codec it is archiving in its startup log line, and switching a camera to H.264 is one
`transcode` field. Over WebRTC the same recording plays natively in Chrome 136+ and Safari 18+,
which negotiate H265; go2rtc transcodes per viewer only for a browser that doesn't offer it
(Firefox, and Edge, which ships the Chromium support disabled).

**Segment boundaries under copy.** A copy cannot insert a keyframe the source does not contain, so
`-force_key_frames` only applies on the transcode branch. The HLS muxer instead starts each segment
at the first keyframe at or after `hls_time`: every segment still begins on a keyframe, but its
duration is `>= SegmentSeconds`, quantised to the camera's GOP. Durations are therefore read from
the playlist's `#EXTINF` rather than assumed — a 10-second GOP against a 4-second target would
otherwise drift the recording index by six seconds per segment. Setting the camera's I-frame
interval to `SegmentSeconds`, or a divisor of it, keeps segments the length they were asked to be.

## Audio in recordings

Set `RecordAudio` on a camera and its audio is muxed into the **same segment files** as the video:
one `.m4s` you can concatenate onto its ~1 KB init and play in VLC, with sound. It is per-camera and
off by default, because recording audio is treated differently from recording pictures in many
jurisdictions.

This is why recording uses **HLS** rather than DASH. ffmpeg's DASH muxer writes one file per stream —
`-adaptation_sets` groups streams into sets but each still becomes its own Representation with its
own files, so audio and video can never share a segment. Its HLS muxer with `fmp4` segments puts
every mapped stream into one variant, which is exactly what "one file that plays" requires. DASH's
per-track bitrate adaptation is meaningless here anyway: there is one quality level.

Audio in `Ingest:AudioPassthroughCodecs` — `aac`, `opus`, `mp3` by default — is copied. Anything
else is transcoded to AAC 64 kbps mono, and a log line says which codec and why.

That asymmetry with video is deliberate, and it is a **container constraint rather than a codec
policy**. Most IP cameras emit **G.711** (`pcm_mulaw`/`pcm_alaw`), which has no fMP4 sample entry
at all — copying it produces a file nothing can open. A video transcode costs a core per camera
forever and changes what you archived, so it is a decision worth delegating; an audio transcode has
exactly one legal target and costs ~64 kbps, around 0.7 GB per camera per month, a rounding error
next to video. There is nothing to decide.

A camera with no audio track records video regardless; the source is probed first, and `-map 0:a?`
keeps a camera that loses audio mid-session recording.

## Exporting a clip

`GET /api/cameras/{id}/clip.mp4?from=&to=` streams back a standalone MP4 for any
range. This is the only place a remux happens: storing standalone files instead would mean remuxing
on *every playback request for every viewer*, whereas exporting a clip is something a person does
occasionally. By hand, the same thing is `cat init-<stamp>.mp4 seg-<stamp>-*.m4s > out.mp4`.

Only segments sharing one fMP4 `init` can go in a single file, so a range that crosses an ffmpeg
restart is exported up to that boundary rather than as a file that plays and then breaks. That used
to be logged and nothing else — the client got a 200 and a clip quietly shorter than it asked for,
which reads as missing footage rather than as a restart. Three headers now say so, computed before
a byte of the body is written:

```
Content-Disposition:      attachment; filename="front-door-20260802-140530.mp4"
X-Serval-Clip-From:       what the file actually starts at
X-Serval-Clip-To:         and ends at
X-Serval-Clip-Truncated:  true when the range was cut at a session boundary
```

A browser can read none of those without the CORS policy naming them in `WithExposedHeaders` —
`AllowAnyHeader` governs the *request* — so the policy lists all four. Without it the web build
cannot even see the filename it is meant to save under, and only on web.

One failure this cannot make honest: if ffmpeg dies mid-pipe the client already holds a 200 and a
partial body. Buffering the export to fix that would defeat the reason it streams.

### Or keeping it

The same remux writes a **saved clip** — a copy that never rolls off, kept beside the camera
directories rather than inside one. It differs from the export in a single ffmpeg flag: a file can
seek where a pipe cannot, so a kept clip gets `+faststart` and an ordinary `moov` instead of a
fragmented one, which is what lets it be scrubbed, resumed and shared. See
[clips.md](clips.md).

## Transcoding and hardware

Nothing is transcoded unless a stream asks for it, so on a server that only records these settings
sit idle. When a stream does declare a `transcode`, `Ingest` decides how it is encoded:

- `HwAccelDevice` — a VAAPI render node, e.g. `/dev/dri/renderD128`. When set, encoding is on the
  GPU via VAAPI (the realistic hardware path for VP9/AV1 on Intel, H.264 on Intel/AMD). Empty →
  software encoding (`libx264` / `libvpx-vp9` / `libsvtav1`).
- `Encoder` — an explicit ffmpeg encoder (e.g. `h264_nvenc`, `av1_nvenc`) for hardware VAAPI can't
  reach. Overrides the software default; **ignored when `HwAccelDevice` is set**.
- `Bitrate` — default `-b:v` for a stream whose `transcode` does not specify one.

**Impossible requests are rejected, not retried.** The server reads `ffmpeg -encoders` once at
startup and validates every transcode request against it, so asking for a codec this host cannot
encode is a 400 on the request that asked for it. The message names the encoder the settings above
actually resolve to — which is how the `HwAccelDevice`-beats-`Encoder` precedence becomes visible
at the moment it bites rather than staying a footnote. A `HwAccelDevice` that does not exist, or
that the process cannot open, fails at startup with the fix (`devices:` vs `group_add:`) rather
than per camera at runtime. Startup also logs which of `h264`/`vp9`/`av1` this host can reach.

**Encode cost is the ceiling.** Software VP9/AV1 is ~1–2 cores per 1080p stream; for several
cameras that's a lot of CPU, so use `HwAccelDevice`/`Encoder` for hardware encode at scale. NVENC
encodes H.264/AV1 but **not** VP9 — VP9 hardware means Intel VAAPI.

**On AMD, `h264` is the only safe transcode target.** No AMD part has ever shipped a VP9 *encoder*
(VCN does VP9 decode only), and AV1 encode arrived with RDNA3 / VCN 4 — anything older, including
every Ryzen APU through Cezanne, cannot. Both are caught by the startup encoder probe and
reported as a 400 naming the missing encoder, instead of an ffmpeg that dies per camera at runtime.
`vainfo` still tells you the same thing ahead of time.

## When a camera stops producing

A camera that drops its connection makes ffmpeg exit, and the supervisor reconnects with capped
backoff. That covers the loud failure. The quiet one is a **half-open TCP socket**: ffmpeg blocks in
a read that will never complete, and because nothing ever kills the process, it stays alive holding
the session open. Every layer here — `FfmpegRunner`'s `WaitForExitAsync`, the supervisor's retry
loop — is keyed on process *exit*, so all of them wait with it. Observed on a real camera for over
five hours, recording nothing.

It is worth being clear about how little of that is visible, and why. A camera's processes fail
**independently** — recording on the main stream, snapshots and detect frames on the sub, an audio
tap on the sub — so the survivors go on making it look healthy. With the recorder wedged, the wall
tile still updates, live view still works, and the dashboard's REC dot stays lit, because it is
inferred from a fresh frame plus the `record` role and neither term observes the recorder. The only
symptom is that seeking into the gap finds nothing there.

`Ingest:StallTimeoutSeconds` (default 60, zero to disable) is the answer. A session that produces
nothing for that long is killed and reconnected through the path that already exists. **Both
sessions are watched**, separately: a wedged snapshot process stops detection, the vision model and
the wall just as quietly as a wedged recorder stops footage.

- **Progress is `out_time` from ffmpeg's own `-progress` stream**, and specifically not the arrival
  of that stream. **A stalled ffmpeg keeps heartbeating**: cut off mid-stream with its input held
  open, it printed a full progress block every second for fifteen seconds, each saying
  `progress=continue`, each carrying the same frozen `out_time_us`. A watchdog waiting for the
  blocks to stop would wait forever while looking like protection. Only the position going up counts.
- **Asking the producer beats watching its output.** A file or directory timestamp is a step removed
  and inherits the filesystem's semantics — correct on ext4 or ZFS, but attribute caching on an NFS
  or FUSE-backed media root can hold a stale one for as long as the timeout itself. A pipe behaves
  the same everywhere. It also rules out measuring a *consumer*: watching the recording index or the
  published frames would fold in Mongo and the reader loops, so a database outage would read as
  every camera stalling at once.
- **`-stats_period` is deliberately not passed.** It arrived in ffmpeg 4.4 and Ubuntu 20.04 still
  ships 4.2, where an unrecognised option would fail every camera on the server at once. The default
  half-second cadence costs a dozen short lines to skim. `-progress` itself dates to 2012.
- **The kill is a SIGKILL**, via `Process.Kill(entireProcessTree: true)`. A wedged ffmpeg never
  reaches the handler a polite signal needs — `SIGTERM` was tried against the real one and did
  nothing at all.
- **The clock starts at launch**, not at the first output, so a source that never produces anything
  is caught by the same timeout as one that stops later.

Set it well above the longest gap between outputs, which for recording under copy is the camera's
**GOP**, not `SegmentSeconds`.

None of this applies to a camera that isn't running one of these processes. A watchdog is armed by
the session itself, so a camera that is disabled, or has no `record` role, simply has no recording
session and nothing to watch — see [Streams and roles](../Server/Serval.Server/README.md#streams-and-roles).
Turning detection off with `DetectFps: 0` is the one case worth stating: it drops the raw frame
output but not the snapshots, so that session keeps producing and stays watched.

## The other thing writing into a camera's directory

A camera with a detect stream of its own also keeps a **rolling buffer of that stream** —
`preview.m3u8` and `preview-*` files, flat in the same directory as the recording. It is what alert
preview clips are cut from, and it exists so that an alert has something to show on a camera nobody
is recording.

Two things about it that matter when reading this file:

* It is the one HLS output in Serval that **deletes what it wrote**. `hls_list_size` is finite and
  `hls_flags` carries `delete_segments` — the exact inverse of the recording output above, where
  both are set the other way precisely so recordings survive.
* It is never put in the recording index, and its filenames all begin `preview-`. That is what keeps
  the sweep below from seeing it and `MediaEndpoints` from serving it.

Details in [alerts.md](alerts.md).

## Retention and pruning

`Media.RetentionDays` is the default cutoff and a camera can override its own. Pruning removes both
the segments and their index rows. `Ingest.SegmentSeconds` is the granularity of both seeking and
pruning — see [configuration.md](configuration.md).

The sweep deletes **only inside `Media.Root/{cameraId}`, and only filenames the index handed back**.
Both halves are load-bearing, and they are what makes two other things safe by construction rather
than by a rule somebody has to keep in step: saved clips sit outside the camera directories, and the
preview buffer's segments are inside one but in no index. Neither is a case this worker knows about.
Alert media has its own sweep — see [alerts.md](alerts.md).
