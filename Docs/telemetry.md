# Telemetry

The AI output contract, told from both ends: what the CameraModule emits, and what the Server
ingests and serves.

The shapes are declared **once**, in
[`Serval.Contracts`](../Shared/Serval.Contracts/TelemetryDocuments.cs), and both sides
deserialize those same types.

## The records

One JSON object per record. Six record types share the stream, discriminated by `type`, and all
are **append-only**: nothing is ever corrected or retracted. On the module they are appended to
`data/telemetry.jsonl` when no server is configured; otherwise they are POSTed to the Server.

```json
{
  "schema_version": 7,
  "type": "utterance",
  "id": "a46eeac3-1ea5-4e2c-9126-a2f63975b045",
  "conversation_id": "da48efb4-9cba-4a6f-adf3-c9c015a579c0",
  "timestamp": "2026-07-15T20:33:08.3144473+00:00",
  "transcript": "The tribal chieftain called for the boy and presented him with 50 pieces of gold.",
  "language": "en",
  "emotion": "neutral",
  "audio_event": "speech",
  "duration_seconds": 5.51,
  "speaker": "speaker_0",
  "speaker_source": "live",
  "source": "module"
}
```

```json
{
  "schema_version": 7,
  "type": "diarization",
  "conversation_id": "da48efb4-9cba-4a6f-adf3-c9c015a579c0",
  "started_at": "2026-07-15T20:31:00Z",
  "audio_seconds": 14.79,
  "speaker_count": 2,
  "segments": [
    { "start": 0.03, "end": 1.77, "speaker": 0 },
    { "start": 7.64, "end": 9.79, "speaker": 1 }
  ],
  "source": "module"
}
```

A **`scene`** record is a description that stands on its own. Vision is not gated on speech, so a
motion-triggered description happens when nobody is talking and there is no utterance for it to
ride on. `motion_score` is recorded so a threshold can be judged after the fact,
and `frame_count` above 1 means the model was shown consecutive frames and could describe movement
rather than a still.

**A scene is an instant, not a span.** Even a multi-frame description covers the couple of seconds
its frames were taken across; nothing here describes a minute. That is why a saved clip's *What's in
it* is a separate vision pass over the finished file rather than a scene picked out of the window —
see [clips.md](clips.md#whats-in-it). A scene relabelled as a summary would be one frame's reading
presented as the arc of the clip.

```json
{
  "schema_version": 7,
  "type": "scene",
  "id": "0f0a1d0e-7e94-4a0b-9c6f-2b0f1f2a3c4d",
  "timestamp": "2026-07-15T20:33:06Z",
  "description": "A person walks from the doorway toward the desk, carrying a box.",
  "trigger": "motion",
  "motion_score": 0.0431,
  "frame_count": 2,
  "frame_span_seconds": 2.0,
  "source": "module"
}
```

A **`detection`** record is one object's continuous presence in front of one camera — an *episode*,
not a frame it appeared in. Its own record type for the reason `scene` is: there is no description
for it to ride on, since a description is produced on a multi-second floor and needs a 2.3 GB model
while a detection happens at a specific instant and needs one a couple of hundred times smaller.

`ended_at` is **null while the object is still there**, which is the whole difference between "a
person is at the door" and "a person was at the door". Storing one record per frame instead of one
per episode would be 86,400 a day per camera against roughly twenty.

`peak_confidence` and `peak_frame_at` are the best look at the object rather than the latest, so a
consumer can fetch that exact snapshot. `frame_count` counts the frames it was actually detected in;
frames it was only predicted to be present on do not count, so `frame_count` against the episode's
duration is how you tell a solid sighting from an intermittent one.

An episode is one **object** and not one class. Three people in shot is three records, each with its
own `id`, its own start, its own duration and its own path, because the Server's tracker tells them
apart across frames. A consumer can say "this person has been at the door for four minutes", which
the class-shaped record it replaces could not express.

```json
{
  "schema_version": 7,
  "type": "detection",
  "id": "b4d8e2c1-3f7a-4e29-8c15-9a0e6f3d2b71",
  "camera_id": "front-door",
  "timestamp": "2026-08-04T12:00:00Z",
  "ended_at": "2026-08-04T12:00:42Z",
  "label": "person",
  "peak_confidence": 0.91,
  "peak_frame_at": "2026-08-04T12:00:12Z",
  "frame_count": 40,
  "best_box": { "x": 0.33, "y": 0.24, "width": 0.20, "height": 0.48, "score": 0.91 },
  "track": [
    { "at": "2026-08-04T12:00:00Z", "box": { "x": 0.10, "y": 0.24, "width": 0.20, "height": 0.48, "score": 0.88 } },
    { "at": "2026-08-04T12:00:12Z", "box": { "x": 0.33, "y": 0.24, "width": 0.20, "height": 0.48, "score": 0.91 } },
    { "at": "2026-08-04T12:00:30Z", "box": null }
  ],
  "is_alert": true,
  "source": "server"
}
```

Each box carries the `score` it was seen at on that frame, which is not `peak_confidence` — that is
the best the object ever reached, and is the right number for deciding whether the episode is an
alert.

`best_box` is where it was on the peak frame; `track` is where it was as it went, which is what a
consumer drawing over recorded footage wants.

The track is **run-length encoded** — a sample holds until the next one and the last holds until
`ended_at` — and a sample whose `box` is null is a gap, the stretch where the object was looked for,
not found, and no longer predictable, while the episode stayed open. See
[the track](detection.md#the-track). Absent where no track was kept.

**An open episode is broadcast but not stored.** While something is still there the record is
re-sent on the live feed every examined frame, with `ended_at` absent and its geometry set to where
things are *now* rather than to the peak frame — that is what lets a box follow what it is drawn
around. Only the close is written, so storage holds one document per episode and never one that
claims something is still present when the process that was watching has gone.

**Those two are not equally droppable, and the socket treats them differently.** A re-send is a
position that the next frame supersedes; the close is the one message that ever says the episode
ended. They travel in separate queues per subscriber for that reason — positions in a small
drop-oldest one, everything else in a large one that is drained first — because a single queue
dropped the close *first* under load: eviction takes the oldest, and the close was published before
all the positions that piled up behind it. A consumer that misses a close is left believing the
object is still there with no later message to correct it and no broken socket to notice, so it
should bound how long it will vouch for an open episode: past `Detection:AbsenceSeconds` from its
last track sample, the Server would have closed it, and an instant further out than that is one
nothing has measured.

A **`conversation_transcript`** is the settled account of a finished conversation: the diarized
turns with the words attributed to them. See [the two speaker streams](#the-two-speaker-streams).

```json
{
  "schema_version": 7,
  "type": "conversation_transcript",
  "conversation_id": "da48efb4-9cba-4a6f-adf3-c9c015a579c0",
  "started_at": "2026-07-15T20:31:00Z",
  "audio_seconds": 14.79,
  "speaker_count": 2,
  "text": "Did you get the package? Yes, it's by the door.",
  "turns": [
    { "start": 0.03, "end": 1.77, "speaker": 0, "text": "Did you get the package?",
      "emotion": "neutral" },
    { "start": 7.64, "end": 9.79, "speaker": 1, "text": "Yes, it's by the door.",
      "emotion": "happy" }
  ],
  "retranscribed_turns": 0,
  "source": "module"
}
```

A turn's `emotion` is the same vocabulary an utterance carries, and absent on the same terms —
never a neutral default. It is resolved here rather than left to a reader because **a reader
cannot do it**: turn times are seconds from `started_at`, while an utterance's `timestamp` is when
the VAD *emitted* it, which is after the speech plus the trailing silence the VAD waited through.
Lining the two up needs `Vad:MinSilenceSeconds`, which never leaves the module. A client attempting
the join has the span both backwards and offset, and fails silently.

A **`sound`** record is a non-speech sound: a car horn, breaking glass, a dog, a door. It is its own
record type for the same reason `scene` is — there is no utterance for it to ride on. Sound
detection runs parallel to speech detection over the same audio, gated on level alone, where the
speech path is gated on a VAD that rejects everything which is not speech. The two overlap freely: a
conversation with a dog barking over it produces an utterance *and* a sound.

```json
{
  "schema_version": 7,
  "type": "sound",
  "id": "b71c4f2a-3d18-4e55-9a0b-6c2e8f14d9a3",
  "timestamp": "2026-07-15T20:34:12Z",
  "label": "Vehicle horn, car horn, honking",
  "confidence": 0.81,
  "alternates": [
    { "label": "Vehicle horn, car horn, honking", "confidence": 0.81 },
    { "label": "Truck", "confidence": 0.06 }
  ],
  "is_alert": true,
  "duration_seconds": 2.0,
  "source": "module"
}
```

`label` is the model's own AudioSet string, verbatim and un-renamed — grouping labels into
categories is a presentation decision, and a consumer that makes it locally can change its mind
without a schema change. The scored shortlist rides along in `alternates` for the same reason:
thresholds can be re-derived later from what was actually stored. `is_alert` says whether the label
is one the operator asked to be told about, and is always written rather than omitted when false —
it is the one field here a person is woken up by, and absence read as "not an alert" would be right
by accident rather than by contract.

Null fields are omitted. An absent `emotion` means undetermined — never a default guess.
`source` distinguishes the module's output from the Server running the same library on behalf of
a camera that has none.

Emotions: `happy`, `sad`, `angry`, `neutral`, `fearful`, `disgusted`, `surprised`,
`emo_unknown`. Events: `speech`, `laughter`, `applause`, `bgm`, `cry`, `cough`, `breath`,
`sing`, `sneeze`, `speech_noise`.

**No record carries a copy of the scene description.** Vision runs roughly a thousand times slower
than transcription and never blocks it, so a description finishing "during" an utterance is a matter
of timing rather than of structure. Every completed description is published as its own `scene`
record whatever triggered it — speech included, which is what `"trigger": "speech"` marks — so a
consumer wanting visual context for an utterance or a sound correlates the two on `timestamp` and
chooses its own idea of how near is near enough.

## The two speaker streams

Speakers are reported **twice, independently, and are never reconciled**:

- **`speaker` on an utterance** — live, as it happens, marked `"speaker_source": "live"`.
- **A `diarization` record** — after the conversation ends, from the whole exchange.

Join them on `conversation_id` and decide which you trust. They are deliberately not merged:
each is produced by a different method with different failure modes, and keeping them apart
means the offline half can be replaced without disturbing the live half. The
`conversation_transcript` record is the *third* thing — the offline speaker picture with the live
transcripts mapped onto it — published alongside, never rewriting what was already emitted.

**The live label is the weaker one, and unavoidably so.** The VAD splits on *silence*, not on
speaker change, so when two people talk without a real gap they land in one utterance and get
one label. Measured on the bundled fixtures: on `2-two-speakers-en.wav` the live pass reports
**1** speaker (at *every* threshold — it cannot be tuned out) where diarization correctly
reports 2. That gap is the entire reason the offline pass exists.

**Aligning the two streams** is subtraction, but not *quite* the obvious one, and the difference
bites. Segment times are seconds from `started_at`, while an utterance's `timestamp` is when the
VAD **emitted** it — which is after the speech itself *plus the trailing silence the VAD waited
through before deciding the utterance had ended*. So the audio sits **before** the timestamp:

```
end   = (utterance.timestamp - started_at) - Vad:MinSilenceSeconds
start = end - utterance.duration_seconds
```

Taking `timestamp .. timestamp + duration_seconds` instead — the shape it looks like — is wrong at
both ends and off by roughly `duration + minSilence`. And since `Vad:MinSilenceSeconds` never
leaves the module, **this join can only be done module-side**. That is why `emotion` is resolved
onto each turn there and published on the record, rather than being left for a client to work out:
see `ConversationReprocessor.SpanOf`.

**Why the offline pass does not simply re-transcribe.** Re-diarizing genuinely gains from seeing the
whole conversation: pyannote clusters speaker embeddings *globally*, so a turn that was unlabelable
in the moment gets resolved by comparison against every other turn. Re-transcribing gains nothing —
SenseVoice is a non-autoregressive encoder with no decoder conditioned on previously emitted text,
so a longer window gives it no extra context and it returns the same words for the same audio.

So the pass **re-attributes** instead: each live utterance is mapped onto the corrected turns by
overlap, at zero ASR cost. ASR is re-run in exactly one case — an utterance that *straddles* a
speaker change, the case the VAD gets wrong by construction — because there the audio genuinely has
to be cut somewhere the live pass never cut it. `retranscribed_turns` reports how often that
happened; if it is a large share of the turns, `Speaker:ClusterThreshold` or the VAD settings want
re-measuring with `--speakers`.

**Emotion rides that same attribution**, which is what makes the straddling case come out right
rather than merely tolerable. A turn takes the reading of whichever utterance covered most of it,
ties going to the earlier — longest rather than a vote, because counting readings would let the
VAD's cutting decide the answer, and where audio was cut is not evidence about how it sounded.
Where an utterance *did* straddle, its own reading is discarded entirely: it was measured over
audio holding both voices, so lending it to either would describe the other one too. Each
re-transcribed piece carries its own emotion instead, since SenseVoice returns text and emotion
from one forward pass and the audio has already been cut per speaker.

**Speaker numbers are per conversation.** `speaker_0` in one conversation has nothing to do
with `speaker_0` in the next — identity is reset after
`Speaker:SilenceTimeoutMinutes` of quiet, and the numbering restarts.

## Ingest into the Server

`POST /api/cameras/{id}/telemetry` takes the module's batch verbatim: a JSON array of the records
above, discriminated by `type`.

The server stamps each with the camera from the URL (the module has no identity of its own) and a
`received_at`, then **upserts by the record's own id** so a batch the module re-delivers after a
network gap updates in place rather than duplicating. That's the server half of the outbox's
at-least-once delivery.

The response is `{ accepted, rejected }`. A record that fails to parse is **rejected and skipped**
— re-sending it would produce the same failure. A record that parses but fails to **store** — the
database being down — fails the whole batch with a 5xx instead, so the module keeps it in the
outbox and re-delivers; the upserts make the retry safe.

Because the contract is shared, it carries no MongoDB attributes — an edge worker has no business
acquiring a database driver to describe its own output. Storage mapping lives in
[TelemetryClassMaps.cs](../Server/Serval.Server/Storage/TelemetryClassMaps.cs) instead, which also
keeps every field stored under its wire name so the module, the database and the App speak one
vocabulary.

The module opts in by setting `Output:ServerUrl` + `Output:CameraId` (+ `Output:ApiKey`); its
`HttpTelemetrySink` then POSTs here instead of writing JSONL locally.

## Reading it back

```
GET /api/cameras/{id}/utterances?from&to&limit
GET /api/cameras/{id}/scenes?from&to&limit
GET /api/cameras/{id}/detections?from&to&limit
GET /api/cameras/{id}/sounds?from&to&limit
GET /api/cameras/{id}/conversation-transcripts?from&to&limit
WS  /api/events[?camera={id}]        live push, {camera_id, type, document}
```

`diarization` records are stored and pushed live, but have no query route of their own — the
`conversation_transcript` already carries the corrected turns a reader wants.

`/detections` returns episodes **present during** the window rather than starting in it: one that
opened before `from` and is still open, or closed inside it, was there. Filtering on start alone
would hide exactly the long-running presences most worth asking about.
