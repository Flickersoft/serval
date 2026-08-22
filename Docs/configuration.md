# Configuration

Every key is overridable by environment variable, with `__` for the nesting separator:
`Serval__Ingest__SnapshotFps=2`, `CameraModule__Asr__Language=en`.

The module pulls **no** configuration from the Server. They are configured independently.

## Three tiers, and which one a setting is in

The Server layers three sources over the values built into the option classes, last wins:

    the option classes  →  environment variables  →  the stored settings overlay
       (what it does with     (this deployment's        (what someone has since
        nothing set)           choices)                  changed in the App)

**The built-in defaults live in C#, and only in C#** —
[`ServerOptions.cs`](../Server/Serval.Server/Configuration/ServerOptions.cs) and the shared
[`AiOptions.cs`](../Shared/Serval.Ai.Core/AiOptions.cs). The Server's `appsettings.json`
carries logging and nothing else. Restating a default in the file buys nothing and costs three
things: the settings page reports every restated key as `deployment` — *set by this deployment* —
for a value nobody chose; each default gains a second home free to drift from the first; and for a
list it breaks narrowing outright (see below). `ConfigurationBindingTests` pins the section's absence.

A property initialiser is not configuration and never becomes configuration — binding constructs the
object, runs the initialisers, and then overwrites only the properties a source has a key for. So
`GET /api/settings` cannot answer "what is this when nothing sets it" from `IConfiguration`; it walks
the option classes instead, in
[`BuiltInDefaults`](../Server/Serval.Server/Configuration/BuiltInDefaults.cs). That is deliberately
*not* a configuration source: layered under the real ones it would sit in the deployment's own
configuration too, and every setting would go back to reporting itself as someone's choice.

The **CameraModule** is different, and stays different: its `appsettings.json` does restate its
defaults, because it has no settings page to misreport a source to and the file is the only
browsable list of its knobs.

**Most settings are editable in the App, at *Settings → Server settings*.** Those writes go to a
single document in Mongo's `settings` collection, layered over everything else as a real
`IConfigurationProvider` — so the option classes, the binder and the `Serval__Foo__Bar` convention
are unchanged, and only which value wins is different. A key absent from the overlay means "not
overridden", not "empty": that is what makes *Use the default* a delete rather than a magic value.

Three tiers of setting, then:

| | Where it lives | How it takes effect |
|---|---|---|
| **Live** | App or environment | Within a few seconds. No restart. |
| **Restart-required** | App or environment | Stored and reported immediately; in use after a restart. The App marks the field itself and the group it sits in. |
| **Environment-only** | Environment | Restart. The App cannot write these at all. |

`GET /api/settings` returns the whole catalogue with, per setting, the value in force, the value it
would revert to, and a `source` of `default`, `deployment` or `user`. A `default` carries the value
built into the class, not a blank. `PUT /api/settings` writes;
a **null value resets** a setting. Both are described in
`Server/Serval.Server/Configuration/SettingsEndpoints.cs`, and the catalogue itself — every key,
its bounds and the sentence the App shows under the field — is
`Server/Serval.Server/Configuration/SettingsCatalog.cs`. That file is the one to edit when adding a
setting: a key not in it cannot be written, however it is spelled.

**Restart-required** covers anything read while the process is being composed: the `Enabled`
switches that decide whether a model is loaded at all (`ServerAi`, `Ai.Vision`, `Ai.Detection`,
`Ai.Sound`, `Ai.Speaker`), every model path, execution provider, thread count, `GpuLayers`,
`Detection.InputPixels`, and `WebRtc.Go2RtcUrl`.

**Environment-only**, and staying that way: `Mongo.*`, `Media.Root`, `Ingest.FfmpegPath` /
`FfprobePath` / `HwAccelDevice` / `Encoder`, `Auth.SigningKey`, `Auth.BootstrapAdmin*`, `ApiKey`,
`Cors.AllowedOrigins`, `OpenApi.Enabled`, and the whole of `GoogleHome.*`. Each is either
load-bearing for the Server's ability to start, or a thing that changing through a UI could lock the
operator out of that UI.

`GoogleHome.*` is there as a **whole tree** rather than the two secrets in it, which is worth the
sentence. `ClientId` and `ClientSecret` are secrets and the catalogue round-trips through a JSON API
any Admin can read. `PublicBaseUrl` is the address this Server hands Google to send credentials to,
and `ProjectId` decides which redirect URIs an anonymous endpoint will honour — writable, either is
a redirect knob rather than a preference. `HomeGraphKeyPath` is a file path, which writable is a
file-read primitive. And `Enabled` follows them because everything else that feature needs — a
reverse proxy, a certificate, a Google console project — is outside the UI too, and a feature
configured half in the App and half in `.env` is worse than either. What the App gets instead is a
read-only card naming whichever condition is unmet. See [google-home.md](google-home.md).

If Mongo is unreachable at startup the overlay is skipped with a warning and the Server runs on its
built-in and environment configuration alone — recording is the job, and a settings page is not a
reason to fail to boot.

### Lists are the one shape with a trap

The .NET binder **appends** to a list that already has entries rather than replacing it, and
`ConfigurationRoot` unions child keys across sources. A three-entry overlay over a ten-entry array
from a lower source therefore yields ten, and narrowing a list silently does nothing.

Every list the settings page can write is therefore empty-by-default with a `Default…`/`Effective…`
pair on the option class, and declared by **no configuration source at all** —
`Ingest.VideoPassthroughCodecs`, `Ingest.AudioPassthroughCodecs`, `Detection.Classes`,
`Detection.DescribeClasses`, `Detection.AlertClasses`, `Sound.AlertLabels`. Read the `Effective…`
property, never the raw one. `BuiltInDefaults` skips lists for the same reason, so the settings page
draws an untouched one as empty with its built-in set shown as placeholder text — which is how *using
the built-in list* stays distinguishable from *someone chose exactly these entries*. Adding a
writable list means following that pattern; `SettingsOverlayTests` pins it.

---

## Server — everything under `Serval`

**The per-setting reference is the catalogue itself.** Every editable setting's label, help text,
bounds, unit and restart requirement live in
[`SettingsCatalog.cs`](../Server/Serval.Server/Configuration/SettingsCatalog.cs), and the App shows
that text beside each field at *Settings → Server settings* — so a prose copy here would only
drift, and had. What the catalogue cannot express is above: the tier a setting is in, the list
trap, and the environment-only keys.

The measured tuning guidance — what the detection knobs cost and where their defaults came from —
is in [detection.md](detection.md).

### Groups, and the two settings pages that share their names

The catalogue's `Group` is a section of the settings page, and six of those names are also section
names in the **camera** editor: *Streams*, *Recording*, *Objects & alerts*, *Motion detection*,
*Speech & transcription* and *Sound recognition*. That is deliberate — a camera overrides a subset
of each — and it is why `CameraSection` in
[`camera_settings_form.dart`](../App/serval_app/lib/widgets/camera_settings_form.dart) spells them
identically. Renaming one side without the other puts two vocabularies on two pages describing the
same setting.

The overridable fields carry a second copy of their label and bounds in
[`server_camera_defaults.dart`](../App/serval_app/lib/models/server_camera_defaults.dart), used only
when `GET /api/settings` cannot be read. `CameraSettingFallbackParityTests` reads that Dart file and
fails if it disagrees with the catalogue.

**Labels are unique within a group, not globally.** *Counts as silence below* names both
`Ai:AudioGate:RmsThreshold` and `Ai:Sound:Gate:RmsThreshold`, because they are the same knob on two
pipelines and the group is what separates them; the key has always been the identity.
`SettingsCatalogTests` pins the within-a-group half of that, which is the half a person reading one
pane can actually see.

### `Advanced` — who a setting is for

Roughly half the catalogue is marked `Advanced: true`: model paths, thread counts, GPU layers, the
tracker's filter noise, tile geometry, `go2rtc address`. The App draws these below an `ADVANCED`
rule at the foot of their group, **never hidden** — somebody following a support thread needs the
field on screen.

It marks *audience*, not risk. Plenty of everyday settings will fill a disk or silence every alert;
these are the ones whose label cannot be made to mean anything to somebody who has never read the
code. A group where every setting is advanced is a group the operator never opens, and usually means
those settings belong in a neighbouring group — `Object tracking` is the one deliberate exception,
pinned as such.

### How a change actually reaches a running camera

Cameras themselves are not configuration: they are managed through `/api/cameras`, and the ingest
manager reconciles against that registry every few seconds, so adding, disabling or deleting one is
all it takes to start or stop its stream. A dead RTSP source is retried forever with capped backoff;
a *misconfigured* one is logged once with what to change and then left alone, rather than retried
every two seconds until the message explaining it has scrolled away.

Settings reach a running camera through the same loops. Both reconcilers compare a
**signature** and restart what has changed:

- `StreamIngestManager.Signature` covers the camera's streams, every ingest setting that reaches an
  ffmpeg command line, and `StallTimeoutSeconds`, which the session reads once when it arms its
  watchdog — so shortening `SegmentSeconds` rebuilds the commands rather than waiting for a process
  to die, and lengthening the stall timeout for a slow camera stops the old one killing it. The
  reconnect backoff is deliberately **not** in it — it is read on each retry and needs no restart.
- `AiSessionSignature` covers everything a detection session reads when it is *built*, computed
  over the camera's **effective** settings — the server-wide values with that camera's overrides
  applied. That is what makes a server-wide change and a per-camera change behave identically.
  Model paths and thread counts are deliberately outside it: they belong to singletons loaded once,
  so restarting a session would arrive at the same weights.

The rule when adding a setting: if a session reads it at construction, it belongs in a signature. A
setting stored, reported, and not in use is worse than one that was never offered.

---

## CameraModule — everything under `CameraModule`

### Audio input

- `Audio.DeviceName` — substring of the input device name. **Set this explicitly for
  deployment.** Device order is not stable across reboots, and the wrong device is
  indistinguishable from a dead microphone.
- `Audio.InputGain` — leave at `1.0`. If audio seems quiet, fix the mixer level first and
  check the startup peak-level log; a large gain here clips.
- `Vad.WindowSize` — must stay `512`. Silero v5 rejects other sizes at 16 kHz.
- `AudioGate.RmsThreshold` — the level below which the VAD is skipped. **This is the setting most
  likely to be wrong, and it fails silently** — read
  [detection.md](detection.md#the-sound-gates-threshold-is-per-camera-and-it-matters-more-than-it-looks)
  before changing it.
- `AudioGate.HangoverSeconds` — keep it **longer than `Vad.MinSilenceSeconds`**. Silero only emits
  a finished utterance after seeing that much trailing silence; a shorter hangover would cut the
  audio off before it got there and the last utterance of every exchange would vanish.

### Transcription

- `Asr.Language` — **pin it**; the default is `en`. `auto` re-decides per utterance, and a few
  seconds of one voice across a room is not enough to identify a language from. Measured over a
  90-second English meeting it emitted a line of Mandarin, and pinning `en` improved nearly every
  other line as well. Only reach for `auto` if a camera genuinely hears more than one language.

### Vision

- `Motion.MinChangedFraction` / `MaxChangedFraction` — per-camera, and the one thing worth
  measuring with `--motion` rather than guessing. The upper bound rejects whole-frame changes as
  lighting rather than movement; drop it if IR-cut transitions still get through, raise it if
  legitimately busy scenes are being ignored.
- `Vision.Enabled` — off by default; costs a 2.3 GB model download and seconds of CPU per
  description.
- `Vision.MaxFrames` — how many consecutive frames a description is shown. `1` restores
  single-image behaviour; `2` (default) lets the model describe movement. Each extra frame costs
  roughly another frame's worth of image tokens. The **NPU path is single-frame regardless** — its
  image-embedding buffer is sized for one image — so `UseNpu` describes stills.
- `Vision.UseNpu` — RK3588 only; see [rk3588.md](rk3588.md#vision-on-the-npu).
- `Vision.NumThreads` — kept low on purpose. llama.cpp will take every core it is given; on
  the Pi that starves the VAD thread. If `Ring buffer overrun` appears in the logs, lower it.
- `Vision.MinSecondsBetweenDescriptions` — floor on description frequency, and the cooldown the
  motion gate leans on. The gate says *whether* the scene changed; this says how often we are
  willing to pay to look. Without it, a continuously busy scene means continuous inference.
- `Capture.CaptureIntervalSeconds` — doubles as the spacing between the frames a multi-frame
  description compares, so it defines what counts as movement: too fast and nothing has changed
  between them, too slow and the model is shown two unrelated scenes.
- `Capture.Width`/`Height` — **the biggest performance lever**, well ahead of thread count. The
  measurements are in [detection.md](detection.md#tuning-the-models).

### Speakers

- `Speaker.ClusterThreshold` — **the number that decides whether diarization is useful**, and it
  can only be chosen by measurement. The fixture results are in
  [detection.md](detection.md#tuning-the-models).
- `Speaker.SilenceTimeoutMinutes` — how long the room must be quiet before a conversation ends,
  its audio is diarized, and speaker identity resets.
- `Speaker.MinSecondsToRegister` — below this an utterance may *match* a known speaker but
  never *create* one. Embeddings need ~1.5–3s to mean anything, while the VAD emits from
  0.25s; without this every short "yeah" would mint a new speaker.
- `Speaker.ContainedOverlapFraction` — how much of a live utterance its best-matching diarized turn
  must account for before that turn inherits the whole transcript. Below it the audio is re-cut per
  turn and re-transcribed. Too high and boundary jitter triggers pointless ASR; too low and an
  utterance spanning several turns dumps all its words on one of them, leaving the others empty and
  therefore absent from the record.
- `Speaker.MaxConversationMinutes` — the cap that bounds diarization's memory use.
- `Speaker.ConversationAudioDirectory` — where the conversation tee is written. See
  [rk3588.md](rk3588.md#conversation-audio-on-tmpfs) before pointing it at tmpfs.

### Output

- `Output.ServerUrl` / `Output.CameraId` / `Output.ApiKey` — set these and `HttpTelemetrySink`
  POSTs to the Server instead of writing JSONL locally.
- `Output.DeleteAfterSync` — set `true` on the Pi so storage cannot fill.

---

## Logging (both hosts)

Two knobs, plain environment variables, neither needing a rebuild:

- `Logging__LogLevel__Serval=Debug` — turn up verbosity for Serval's own categories only, leaving
  the framework at its usual level.
- `Logging__Console__FormatterName=json` — one JSON object per line, which is what a log aggregator
  (Loki, Seq, Elastic) wants to ingest. The default is `simple` because nothing is aggregating yet
  and the usual reader is a person. When making the switch permanent, also drop the trailing space
  from `TimestampFormat` — `simple` needs it as a separator, `json` renders it inside the
  `Timestamp` string.

Neither host writes a log file; see [deployment.md](deployment.md#logs) and
[rk3588.md](rk3588.md#logs).

---

## Backing up and restoring the configuration

*Server status* → *Configuration backup*, Admin only. `GET /api/config/backup` writes one JSON
file, `POST /api/config/restore` merges one back.

**What is in it.** The camera registry, the stored settings overlay, the accounts, and each
account's preferences — the four collections that hold things somebody typed. Detection masks
travel with their camera, in `detectionTuning.masks`, which is also the only place they live.

Each account's preferences means its wall layout *and* its notification rules — both are choices
somebody made.

**What is not.** Recorded footage, detections, transcripts, sounds, telemetry. Those are what the
configuration produces, they are enormous, and they are on a volume the operator already backs up
by whatever means backs up a disk. This file is for the part a disk image cannot recover.

Two things belonging to notifications are also absent, for a different reason: they are not
configuration. A push *subscription* belongs to a browser rather than to a person — it expires on
its own, is re-registered every launch, and one restored onto another machine would name a device
that deployment has never spoken to. The VAPID signing key is left out as described under
[Notifications](#notifications).

**It carries secrets in plain text** — every camera's ONVIF password, any `user:password` inside a
stream URL, and every account's password hash. That is deliberate: a backup that strips them
restores a list of cameras that cannot connect and accounts nobody can sign in to. The file says so
as its second key, the App says so before it downloads one, and both mean it. Store it where you
store those passwords.

**Only the overridden settings travel**, because that is what the overlay is — a setting left at
its deployment or built-in value is absent. Restoring onto a differently-deployed Server therefore
restores the *choices* somebody made, and leaves that deployment's own values alone. Settings that
are environment-only here (see the three-tier note above) are not in the file, and a hand-edited
one naming them is refused key by key.

### Restore merges, and never deletes

Every camera, setting, account and preference the file names is created or overwritten; anything
the Server has that the file does not name is left exactly as it is. A camera added after the
backup survives it. Overwriting is not field-level — a camera in the file replaces the one here
whole, including a password rotated since.

The one thing a restore removes is the stale tail of a *list* setting the file has shortened. A
list is stored one entry per key, so merging it index by index would leave the old indices behind
and produce a list neither side asked for. Lists the file does not name are untouched.

It is best-effort and reports what it refused, in the words of whatever validator refused it —
usually the same sentence the settings or camera form would have shown. The refusal to expect when
moving between machines is a camera whose transcode codec this host's ffmpeg cannot encode; that
camera is skipped and everything else lands. Fix it and restore the same file again, which is safe:
a restore is idempotent.

**Your own account is never demoted and never has its password replaced by a restore**, so it
cannot sign you out of the Server you are restoring. Other accounts whose password the file changes
are signed out of every device, on the same reasoning as an admin-initiated password reset. A file
written by a newer version of Serval is refused outright rather than partly applied.
