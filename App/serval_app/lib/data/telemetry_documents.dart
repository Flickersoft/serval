/// Dart mirrors of the six documents in `Shared/Serval.Contracts/TelemetryDocuments.cs`.
///
/// **These are snake_case and the camera record is camelCase.** The Server serializes
/// `/api/cameras*` from a C# class with default naming, and telemetry from `[JsonPropertyName]`
/// attributes chosen to match what the CameraModule writes — so there is no one naming strategy
/// that reads both, and trying to have one produces a model that silently deserializes to
/// defaults.
///
/// Only the fields the App actually renders are parsed. The Server tolerates unknown fields in
/// the other direction and nothing here is ever sent back, so a document gaining a field is not
/// a breaking change for this file.
library;

import 'dart:ui' show Rect;

import '../models/activity.dart';
import '../models/camera.dart' as camera_model;

/// The common tail every document carries: which camera, and when the Server took delivery.
sealed class TelemetryDocument {
  const TelemetryDocument({
    required this.cameraId,
    required this.source,
    required this.when,
  });

  /// Stamped by the Server from the ingest URL; the module has no identity of its own.
  final String cameraId;

  /// `module` or `server` — edge AI, or the Server running it on the camera's behalf.
  final String source;

  /// The single time field this document sorts by. There is no unified one on the wire:
  /// utterances and scenes carry `timestamp`, diarizations and transcripts carry `started_at`.
  /// Resolving that here is what lets the activity feed merge all four into one list.
  final DateTime when;

  TelemetryKind get kind;

  /// Stable identity for the feed. Utterances and scenes have their own `id`; the conversation
  /// documents are keyed by `conversation_id` and there is at most one of each per conversation.
  String get feedId;

  static DateTime _time(Map<String, dynamic> json, String key) =>
      DateTime.parse(json[key] as String).toLocal();

  static String _string(Map<String, dynamic> json, String key) =>
      (json[key] as String?) ?? '';
}

class UtteranceDocument extends TelemetryDocument {
  const UtteranceDocument({
    required super.cameraId,
    required super.source,
    required super.when,
    required this.id,
    required this.conversationId,
    required this.transcript,
    required this.speaker,
    required this.emotion,
    required this.audioEvent,
    required this.durationSeconds,
  });

  factory UtteranceDocument.fromJson(Map<String, dynamic> json) =>
      UtteranceDocument(
        cameraId: TelemetryDocument._string(json, 'camera_id'),
        source: TelemetryDocument._string(json, 'source'),
        when: TelemetryDocument._time(json, 'timestamp'),
        id: TelemetryDocument._string(json, 'id'),
        conversationId: json['conversation_id'] as String?,
        transcript: TelemetryDocument._string(json, 'transcript'),
        speaker: json['speaker'] as String?,
        emotion: json['emotion'] as String?,
        audioEvent: json['audio_event'] as String?,
        durationSeconds: (json['duration_seconds'] as num?)?.toDouble() ?? 0,
      );

  final String id;

  /// Joins this to the conversation's later, better-attributed transcript. Null when the
  /// utterance stands alone.
  final String? conversationId;

  final String transcript;

  /// The live, best-effort label — explicitly *not* the considered answer the diarization gives.
  final String? speaker;

  final String? emotion;
  final String? audioEvent;
  final double durationSeconds;

  @override
  TelemetryKind get kind => TelemetryKind.utterance;

  @override
  String get feedId => 'utterance:$id';
}

/// One speaker's turn with the words attributed to it. Times are seconds from `started_at`.
class ConversationTurn {
  const ConversationTurn({
    required this.start,
    required this.end,
    required this.speaker,
    required this.text,
    required this.emotion,
  });

  factory ConversationTurn.fromJson(Map<String, dynamic> json) =>
      ConversationTurn(
        start: (json['start'] as num?)?.toDouble() ?? 0,
        end: (json['end'] as num?)?.toDouble() ?? 0,
        speaker: (json['speaker'] as num?)?.toInt() ?? 0,
        text: TelemetryDocument._string(json, 'text'),
        emotion: json['emotion'] as String?,
      );

  final double start;
  final double end;

  /// Scoped to this conversation and meaningless across conversations.
  final int speaker;

  final String text;

  /// How the audio behind *this speaker's* words sounded, resolved Server-side.
  ///
  /// Not something this side could work out for itself: an utterance's timestamp is when the VAD
  /// emitted it — after the speech plus the trailing silence it waited through — so joining
  /// utterances onto turn times needs the VAD's own minimum-silence setting, which never leaves the
  /// module. Null where nothing could be attributed, and a conversation reprocessed before the
  /// field existed reads null for every turn.
  final String? emotion;
}

class ConversationTranscriptDocument extends TelemetryDocument {
  const ConversationTranscriptDocument({
    required super.cameraId,
    required super.source,
    required super.when,
    required this.conversationId,
    required this.text,
    required this.turns,
    required this.speakerCount,
  });

  factory ConversationTranscriptDocument.fromJson(Map<String, dynamic> json) =>
      ConversationTranscriptDocument(
        cameraId: TelemetryDocument._string(json, 'camera_id'),
        source: TelemetryDocument._string(json, 'source'),
        when: TelemetryDocument._time(json, 'started_at'),
        conversationId: TelemetryDocument._string(json, 'conversation_id'),
        text: TelemetryDocument._string(json, 'text'),
        turns: [
          for (final turn in (json['turns'] as List<dynamic>? ?? const []))
            ConversationTurn.fromJson(turn as Map<String, dynamic>),
        ],
        speakerCount: (json['speaker_count'] as num?)?.toInt() ?? 0,
      );

  final String conversationId;

  /// The whole conversation as flowing text — what the activity row shows.
  final String text;

  final List<ConversationTurn> turns;
  final int speakerCount;

  @override
  TelemetryKind get kind => TelemetryKind.conversationTranscript;

  @override
  String get feedId => 'conversation_transcript:$conversationId';
}

/// The after-the-fact speaker picture. Parsed for completeness — nothing in the two screens
/// renders it, since the transcript that supersedes it is the better view of the same audio.
class DiarizationDocument extends TelemetryDocument {
  const DiarizationDocument({
    required super.cameraId,
    required super.source,
    required super.when,
    required this.conversationId,
    required this.speakerCount,
  });

  factory DiarizationDocument.fromJson(Map<String, dynamic> json) =>
      DiarizationDocument(
        cameraId: TelemetryDocument._string(json, 'camera_id'),
        source: TelemetryDocument._string(json, 'source'),
        when: TelemetryDocument._time(json, 'started_at'),
        conversationId: TelemetryDocument._string(json, 'conversation_id'),
        speakerCount: (json['speaker_count'] as num?)?.toInt() ?? 0,
      );

  final String conversationId;
  final int speakerCount;

  @override
  TelemetryKind get kind => TelemetryKind.diarization;

  @override
  String get feedId => 'diarization:$conversationId';
}

class SceneDocument extends TelemetryDocument {
  const SceneDocument({
    required super.cameraId,
    required super.source,
    required super.when,
    required this.id,
    required this.description,
    required this.trigger,
    required this.motionScore,
    required this.frameSpanSeconds,
  });

  factory SceneDocument.fromJson(Map<String, dynamic> json) => SceneDocument(
    cameraId: TelemetryDocument._string(json, 'camera_id'),
    source: TelemetryDocument._string(json, 'source'),
    when: TelemetryDocument._time(json, 'timestamp'),
    id: TelemetryDocument._string(json, 'id'),
    description: TelemetryDocument._string(json, 'description'),
    trigger: TelemetryDocument._string(json, 'trigger'),
    motionScore: (json['motion_score'] as num?)?.toDouble(),
    frameSpanSeconds: (json['frame_span_seconds'] as num?)?.toDouble() ?? 0,
  );

  final String id;
  final String description;

  /// `motion`, `speech` or `periodic`.
  final String trigger;

  /// Fraction of pixels the gate saw change; absent unless motion-triggered.
  final double? motionScore;

  /// Seconds between the oldest and newest frame the model was shown. 0 for a single frame,
  /// which is also the case where it could infer no movement.
  final double frameSpanSeconds;

  @override
  TelemetryKind get kind => TelemetryKind.scene;

  @override
  String get feedId => 'scene:$id';
}

/// Where a detection sat in the frame, as a fraction of the frame's own size.
///
/// Normalised rather than in pixels so the same box is correct drawn over the
/// snapshot, a full-resolution still, or a scaled-down wall tile — none of which
/// are the picture the detector actually looked at.
///
/// Distinct from `DetectionBox` in `models/camera.dart`, which is the *rendered*
/// form: this is only geometry off the wire, that one carries the label and
/// confidence the overlay draws. [DetectionDocument.overlays] converts.
class DetectionBounds {
  const DetectionBounds({
    required this.x,
    required this.y,
    required this.width,
    required this.height,
    this.score = 0,
  });

  factory DetectionBounds.fromJson(Map<String, dynamic> json) =>
      DetectionBounds(
        x: (json['x'] as num?)?.toDouble() ?? 0,
        y: (json['y'] as num?)?.toDouble() ?? 0,
        width: (json['width'] as num?)?.toDouble() ?? 0,
        height: (json['height'] as num?)?.toDouble() ?? 0,
        score: (json['score'] as num?)?.toDouble() ?? 0,
      );

  /// A wire box, absent or null reading as none. A record carrying no geometry
  /// lands here as null rather than as an error — it draws nothing, which is the
  /// honest answer.
  static DetectionBounds? _maybe(Object? value) => value is Map
      ? DetectionBounds.fromJson(value.cast<String, dynamic>())
      : null;

  final double x;
  final double y;
  final double width;
  final double height;

  /// How well the object was seen on this frame. Not the episode's
  /// [DetectionDocument.peakConfidence], which is the best it ever reached.
  final double score;
}

/// Where one object was, from [at] until the next sample in the episode's track.
///
/// Run-length encoded, so a sample holds until the one after it and the last one
/// holds until the episode's end. A car parked for ten minutes is one entry.
///
/// A null [box] is a gap: the object was looked for, not found, and no longer
/// predictable, while the episode stayed open. It is what stops the run-length
/// rule from painting a box over footage the object had already walked out of.
class DetectionTrackSample {
  const DetectionTrackSample({required this.at, this.box});

  factory DetectionTrackSample.fromJson(Map<String, dynamic> json) =>
      DetectionTrackSample(
        at: TelemetryDocument._time(json, 'at'),
        box: DetectionBounds._maybe(json['box']),
      );

  final DateTime at;
  final DetectionBounds? box;
}

/// One object present in front of one camera for a stretch of time, rather than
/// one frame it was seen in.
///
/// One row per object, not per class: three people in shot is three of these,
/// each with its own start, duration and path, because the Server's tracker
/// tells them apart across frames.
///
/// The only record type that is *updated* rather than only appended: it arrives
/// once when the episode opens, with [endedAt] null, and again under the same id
/// when it closes. The feed is keyed on [feedId], so the second delivery replaces
/// the first instead of adding a second row.
class DetectionDocument extends TelemetryDocument {
  const DetectionDocument({
    required super.cameraId,
    required super.source,
    required super.when,
    required this.id,
    required this.label,
    required this.peakConfidence,
    required this.frameCount,
    required this.isAlert,
    this.endedAt,
    this.bestBox,
    this.track = const [],
  });

  factory DetectionDocument.fromJson(Map<String, dynamic> json) =>
      DetectionDocument(
        cameraId: TelemetryDocument._string(json, 'camera_id'),
        source: TelemetryDocument._string(json, 'source'),
        when: TelemetryDocument._time(json, 'timestamp'),
        id: TelemetryDocument._string(json, 'id'),
        label: TelemetryDocument._string(json, 'label'),
        peakConfidence: (json['peak_confidence'] as num?)?.toDouble() ?? 0,
        frameCount: (json['frame_count'] as num?)?.toInt() ?? 0,
        isAlert: json['is_alert'] as bool? ?? false,
        endedAt: json['ended_at'] == null
            ? null
            : TelemetryDocument._time(json, 'ended_at'),
        bestBox: DetectionBounds._maybe(json['best_box']),
        // Empty where no track was kept, which reads as "no geometry over time"
        // rather than "it never moved" — the overlay draws nothing instead of
        // pinning a box where the object once was.
        track: [
          for (final sample in (json['track'] as List?) ?? const [])
            DetectionTrackSample.fromJson(
              (sample as Map).cast<String, dynamic>(),
            ),
        ],
      );

  final String id;

  /// The model's own class string, verbatim.
  final String label;

  final double peakConfidence;

  /// Frames the object was actually detected in — frames it was only predicted
  /// to be present on do not count.
  final int frameCount;

  final bool isAlert;

  /// Null while the object is still there, which is the whole difference
  /// between "a person is at the door" and "a person was at the door".
  final DateTime? endedAt;

  /// Where it was on the best-scoring frame, null if the record carries no
  /// geometry at all.
  final DetectionBounds? bestBox;

  /// Where it was as it went, oldest first. See [DetectionTrackSample] for how
  /// to read one, and [overlaysAt] for the reading itself.
  final List<DetectionTrackSample> track;

  /// Whether the object is still in view.
  bool get isOngoing => endedAt == null;

  /// How long an open episode keeps covering time after its last measurement.
  ///
  /// Thirty seconds, matching the Server's `Detection:AbsenceSeconds` — how long it waits before
  /// declaring an object gone. Past that the Server itself would have closed the episode, so an
  /// instant further out than this is one nothing measured and nothing can vouch for.
  static const openEpisodeGrace = Duration(seconds: 30);

  /// The last instant this episode is a claim about.
  ///
  /// [endedAt] when there is one. An open episode is bounded by its most recent evidence plus
  /// [openEpisodeGrace] instead, because *still open* means nothing has closed it yet — not that
  /// the object is still there. The two come apart when a record outlives whatever would have
  /// closed it, and a null end read as "present at every instant from here on" paints boxes over
  /// every frame recorded after it.
  DateTime get coversUntil =>
      endedAt ??
      (track.isNotEmpty ? track.last.at : when).add(openEpisodeGrace);

  /// This detection as the overlay's boxes, empty when it carries no geometry.
  ///
  /// What live draws. For an open episode the Server re-sends the record every
  /// frame with this set to where the object is *now*; for a closed one it is
  /// the peak frame's, which is the clearest look rather than the last.
  ///
  /// A list of at most one, because the overlay pools boxes across every
  /// detection it is drawing and one episode contributes one object to that.
  ///
  /// Stale when the snapshot's sample carries no box: the Server is saying it
  /// knows where this was and did not see it this frame, which is a dotted box
  /// rather than no box at all.
  List<camera_model.DetectionBox> get overlays =>
      _drawn(bestBox, stale: track.isNotEmpty && track.last.box == null);

  /// This detection as the overlay's boxes at [when], empty if there was nothing
  /// to see then.
  ///
  /// What replay draws, where [overlays] is what live draws. The peak frame is
  /// the clearest single look at the object and is in the wrong place for every
  /// other second of a walk across the view, so pinning it over footage would be
  /// a worse lie than drawing nothing.
  ///
  /// Empty outside the episode's own span, inside a gap, and for an episode that
  /// carries no track at all.
  ///
  /// The right edge is [coversUntil] rather than [endedAt], so an episode that
  /// never closed stops drawing a grace period after its last sample instead of
  /// covering every instant that follows it.
  List<camera_model.DetectionBox> overlaysAt(DateTime when) {
    if (when.isBefore(this.when)) return const [];
    if (when.isAfter(coversUntil)) return const [];

    // The last sample at or before the instant. A sample holds until the one
    // after it, so this is the whole of reading a run-length track — and a
    // linear walk over a list that is usually a handful of entries beats any
    // index that would have to be kept in step with the feed.
    //
    // The last *positioned* sample is carried alongside, because a gap is
    // marked by a sample with no box and the honest thing to draw over one is
    // where the object was before it, dotted.
    DetectionTrackSample? current;
    DetectionBounds? lastKnown;
    for (final sample in track) {
      if (sample.at.isAfter(when)) break;
      current = sample;
      if (sample.box != null) lastKnown = sample.box;
    }

    if (current == null) return const [];

    // A gap before the first sighting has nothing behind it to fall back on, so
    // it stays empty — `_drawn` answers null with no box.
    return current.box == null
        ? _drawn(lastKnown, stale: true)
        : _drawn(current.box);
  }

  /// Wire geometry as a box the overlay can draw, or nothing.
  ///
  /// It keeps the score it was seen at rather than the episode's
  /// [peakConfidence], which is the best the object ever reached and would be a
  /// claim about this frame that nothing measured.
  ///
  /// [isAlert] rides along because it is what colours the box, and it belongs to
  /// the episode rather than to the frame — the Server decides it once, when the
  /// episode opens. [stale] is the other half of the box's appearance and is a
  /// fact about the frame instead: whether anything was measured at the instant
  /// being drawn, or this is only the last place the object was known to be.
  List<camera_model.DetectionBox> _drawn(
    DetectionBounds? box, {
    bool stale = false,
  }) => [
    if (box != null)
      camera_model.DetectionBox(
        label: label,
        confidence: box.score,
        rect: Rect.fromLTWH(box.x, box.y, box.width, box.height),
        isAlert: isAlert,
        isStale: stale,
      ),
  ];

  /// How long it has been present, so far or in total.
  Duration get duration => (endedAt ?? DateTime.now().toUtc()).difference(when);

  @override
  TelemetryKind get kind => TelemetryKind.detection;

  @override
  String get feedId => 'detection:$id';
}

/// One AudioSet class the tagger scored, as the model named it.
class SoundAlternate {
  const SoundAlternate({required this.label, required this.confidence});

  factory SoundAlternate.fromJson(Map<String, dynamic> json) => SoundAlternate(
    label: (json['label'] as String?) ?? '',
    confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
  );

  final String label;
  final double confidence;
}

/// A non-speech sound: a car horn, breaking glass, a dog, a door.
///
/// [label] is the model's own AudioSet string, verbatim and un-tidied — "Vehicle horn, car horn,
/// honking", not a slug. Grouping it into something displayable is this side's job, deliberately:
/// the server storing raw labels means the grouping can change here without a schema version.
/// See `soundIcon` in `live_repository.dart`.
class SoundDocument extends TelemetryDocument {
  const SoundDocument({
    required super.cameraId,
    required super.source,
    required super.when,
    required this.id,
    required this.label,
    required this.confidence,
    required this.alternates,
    required this.isAlert,
    required this.durationSeconds,
  });

  factory SoundDocument.fromJson(Map<String, dynamic> json) => SoundDocument(
    cameraId: TelemetryDocument._string(json, 'camera_id'),
    source: TelemetryDocument._string(json, 'source'),
    when: TelemetryDocument._time(json, 'timestamp'),
    id: TelemetryDocument._string(json, 'id'),
    label: TelemetryDocument._string(json, 'label'),
    confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
    alternates: [
      for (final entry in (json['alternates'] as List<dynamic>? ?? const []))
        SoundAlternate.fromJson(entry as Map<String, dynamic>),
    ],
    // Absent should never happen — the field is non-nullable on the wire precisely so it is
    // always written — but defaulting to false keeps an old server from raising phantom alerts.
    isAlert: (json['is_alert'] as bool?) ?? false,
    durationSeconds: (json['duration_seconds'] as num?)?.toDouble() ?? 0,
  );

  final String id;

  /// The winning AudioSet label, exactly as the model spells it.
  final String label;

  final double confidence;

  /// The rest of the scored shortlist, best first, with the winner at index 0.
  final List<SoundAlternate> alternates;

  /// Whether this label is one the operator asked to be told about.
  final bool isAlert;

  final double durationSeconds;

  /// The label without its trailing synonyms: AudioSet names them as comma-separated phrases
  /// ("Vehicle horn, car horn, honking"), and only the first is worth a row in a feed.
  String get shortLabel => label.split(',').first.trim();

  @override
  TelemetryKind get kind => TelemetryKind.sound;

  @override
  String get feedId => 'sound:$id';
}

/// Parses one document given its `type` discriminator, which is how both the REST reads and the
/// `/api/events` socket identify what they are carrying.
///
/// Returns null for a type this build does not know. A future schema version adding a seventh
/// record should not empty the activity column.
TelemetryDocument? parseTelemetryDocument(
  String type,
  Map<String, dynamic> json,
) => switch (type) {
  'utterance' => UtteranceDocument.fromJson(json),
  'conversation_transcript' => ConversationTranscriptDocument.fromJson(json),
  'diarization' => DiarizationDocument.fromJson(json),
  'scene' => SceneDocument.fromJson(json),
  'detection' => DetectionDocument.fromJson(json),
  'sound' => SoundDocument.fromJson(json),
  _ => null,
};
