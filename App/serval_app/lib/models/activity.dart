/// The six telemetry document types the Server publishes, discriminated on
/// the wire by a `type` field on both `WS /api/events` and the REST reads.
///
/// Two kinds of overlap here are deliberate, not bugs to collapse. Utterances
/// and conversation transcripts describe the same audio — the transcript is a
/// better *later* view, not a correction — so a feed rendering both without
/// collapsing on `conversation_id` shows the same words twice. Sounds overlap
/// utterances differently: speech and sound detection run independently over
/// the same audio, so a conversation with a dog barking over it legitimately
/// produces both, and neither is the other's duplicate.
///
/// Detections overlap scenes the same way and for the same reason: a detection
/// is what the object gate *saw*, a scene is what the vision model was asked to
/// *say* about it, and one arrival produces at most one of each.
enum TelemetryKind {
  utterance('utterance'),
  diarization('diarization'),
  conversationTranscript('conversation_transcript'),
  scene('scene'),
  detection('detection'),
  sound('sound');

  const TelemetryKind(this.wireName);

  /// The `type` discriminator value.
  final String wireName;
}

/// The four readings the column's *What happened* facet offers.
///
/// A partition of everything the feed can hold: every row is exactly one of
/// these, so the facet is multi-select without any of its options overlapping.
/// Diarizations never reach the column — the transcript supersedes them — and
/// utterances and transcripts are both [speech].
///
/// Declaration order is the order the panel lists them in.
enum ActivityKind {
  speech('Speech'),
  scenes('Scene descriptions'),
  objects('Objects seen'),
  sounds('Sounds');

  const ActivityKind(this.label);

  final String label;
}

/// Which of the four readings a wire type is.
///
/// A free function rather than a getter on [TelemetryKind] because it is the
/// answer for anything carrying a type — the column's rows, the scrubber's
/// marks — and two switches over the same six values are two places for the
/// partition to drift.
///
/// An utterance and a settled transcript are both [ActivityKind.speech]; the
/// rest map one to one. A diarization falls to [ActivityKind.speech] rather
/// than throwing — it is speech if it ever surfaces.
ActivityKind activityKindOf(TelemetryKind kind) => switch (kind) {
  TelemetryKind.scene => ActivityKind.scenes,
  TelemetryKind.detection => ActivityKind.objects,
  TelemetryKind.sound => ActivityKind.sounds,
  _ => ActivityKind.speech,
};

/// One thing the filter is currently doing, as the column draws it: a small
/// removable chip under the search field.
///
/// Carries its own remover rather than a discriminated key, so the chip row
/// stays a `map` over this list and never has to know what kind of facet it is
/// undoing.
class ActivityChip {
  const ActivityChip({required this.label, required this.without});

  final String label;

  /// The filter this chip's × leaves behind.
  final ActivityFilter without;
}

/// Everything the "What's happening" column is currently filtered by, as one
/// value.
///
/// Facets rather than a segmented control, which expresses one choice and so
/// makes *Speech* and *Sounds* rivals when the data says they are not: the two
/// run independently over the same audio, and a conversation with a dog barking
/// over it belongs under both. Facets also grow — the telemetry carries which
/// camera, what class was seen or heard, and `is_alert`.
///
/// Held as one value rather than a field per facet on each screen so that the
/// whole state can be passed, compared and — one day — saved as a named view.
///
/// An empty facet set means *everything*. Ticking no camera is not a request
/// for a blank column, so [apply] only narrows on a facet that has something
/// in it.
class ActivityFilter {
  const ActivityFilter({
    this.query = '',
    this.kinds = const {},
    this.cameraIds = const {},
    this.labels = const {},
    this.alertsOnly = false,
  });

  /// The resting state: everything, as it happens.
  static const none = ActivityFilter();

  /// What was typed into the search field. Matched against what was said or
  /// seen — see [apply].
  final String query;

  final Set<ActivityKind> kinds;

  /// Which cameras, by id. Always empty on the single-camera panel, where the
  /// question is settled by being on that screen.
  final Set<String> cameraIds;

  /// Which classes the model named — a detector's `person`, a sound's full
  /// AudioSet phrase. See [ActivityItem.label].
  final Set<String> labels;

  /// A flag rather than a fifth [ActivityKind], because `is_alert` crosses
  /// sounds and detections both.
  final bool alertsOnly;

  bool get isEmpty =>
      query.isEmpty &&
      kinds.isEmpty &&
      cameraIds.isEmpty &&
      labels.isEmpty &&
      !alertsOnly;

  /// The number on the *Filter* button's badge.
  ///
  /// The query is not counted: it is on screen in its own field, and a badge
  /// that included it would be counting something the reader can already see.
  int get facetCount =>
      kinds.length + cameraIds.length + labels.length + (alertsOnly ? 1 : 0);

  /// What is filtered, on the surface and removable one at a time — so a column
  /// that has been narrowed can never be mistaken for a quiet house, and
  /// dropping one filter never means opening the panel to find it.
  ///
  /// [cameraNames] resolves the ids: the chip says *Front door*, not
  /// `front-door`. An id with no name falls back to itself, which is what a
  /// camera removed from the registry while its events are still in the column
  /// leaves behind.
  List<ActivityChip> chips(Map<String, String> cameraNames) => [
    if (alertsOnly)
      ActivityChip(label: 'Alerts only', without: copyWith(alertsOnly: false)),
    for (final kind in ActivityKind.values)
      if (kinds.contains(kind))
        ActivityChip(label: kind.label, without: toggleKind(kind)),
    for (final id in cameraIds)
      ActivityChip(label: cameraNames[id] ?? id, without: toggleCamera(id)),
    for (final label in labels)
      ActivityChip(label: label, without: toggleLabel(label)),
  ];

  /// The rows this filter leaves, in the order they arrived.
  ///
  /// The one home for these predicates. Per repository, they are two places for
  /// the same reading of the same data to be wrong.
  List<ActivityItem> apply(List<ActivityItem> items) {
    if (isEmpty) return items;

    final needle = query.trim().toLowerCase();

    return [
      for (final item in items)
        if ((!alertsOnly || item.isAlert) &&
            (kinds.isEmpty || kinds.contains(item.activityKind)) &&
            (cameraIds.isEmpty || cameraIds.contains(item.cameraId)) &&
            (labels.isEmpty ||
                (item.label != null && labels.contains(item.label))) &&
            (needle.isEmpty || item.matches(needle)))
          item,
    ];
  }

  ActivityFilter copyWith({
    String? query,
    Set<ActivityKind>? kinds,
    Set<String>? cameraIds,
    Set<String>? labels,
    bool? alertsOnly,
  }) => ActivityFilter(
    query: query ?? this.query,
    kinds: kinds ?? this.kinds,
    cameraIds: cameraIds ?? this.cameraIds,
    labels: labels ?? this.labels,
    alertsOnly: alertsOnly ?? this.alertsOnly,
  );

  ActivityFilter toggleKind(ActivityKind kind) =>
      copyWith(kinds: _toggle(kinds, kind));

  ActivityFilter toggleCamera(String cameraId) =>
      copyWith(cameraIds: _toggle(cameraIds, cameraId));

  ActivityFilter toggleLabel(String label) =>
      copyWith(labels: _toggle(labels, label));

  /// Everything the panel holds, leaving the search field alone — the *Reset
  /// all* in the panel's header, which is not the *Clear* under the chips.
  ActivityFilter get withoutFacets => ActivityFilter(query: query);

  static Set<T> _toggle<T>(Set<T> set, T value) =>
      set.contains(value) ? ({...set}..remove(value)) : {...set, value};

  /// Compared by value, not identity: the live repository memoises against a
  /// key holding one of these, and a filter that never equalled an identical
  /// filter would defeat the memo on every rebuild.
  @override
  bool operator ==(Object other) =>
      other is ActivityFilter &&
      other.query == query &&
      other.alertsOnly == alertsOnly &&
      _same(other.kinds, kinds) &&
      _same(other.cameraIds, cameraIds) &&
      _same(other.labels, labels);

  @override
  int get hashCode => Object.hash(
    query,
    alertsOnly,
    // Order-independent, which is what makes two sets built by different
    // sequences of taps hash alike.
    Object.hashAllUnordered(kinds),
    Object.hashAllUnordered(cameraIds),
    Object.hashAllUnordered(labels),
  );

  static bool _same<T>(Set<T> a, Set<T> b) =>
      a.length == b.length && a.containsAll(b);
}

/// How many of the rows the column is holding carry each facet value.
///
/// Counted over the *unfiltered* pool, so a number never moves as boxes are
/// ticked and the camera counts always sum to [total]. The panel's subtitle
/// says as much: the counts describe the column itself — the events it is
/// holding at this moment, not a query against the day — so they can never
/// disagree with the list underneath them, and there is no midnight at which
/// they all fall to zero.
class ActivityFacets {
  const ActivityFacets({
    required this.total,
    required this.alerts,
    required this.kinds,
    required this.cameras,
    required this.labels,
  });

  /// Counts [items], with the sections below the alert toggle scoped to the
  /// alerts when [alertsOnly] is on.
  ///
  /// That one exception to "counts are the whole pool" is what stops the toggle
  /// leading somewhere blank. With it off, ticking *Speech* under *Alerts only*
  /// showed `Speech 3` and then emptied the column, because none of those three
  /// was an alert — and nothing on screen had said so. Scoped, the row reads 0
  /// and will not be ticked at all.
  ///
  /// Only the toggle does this, not the other facets: it is the one control the
  /// design puts *above* the list, and its own subtitle already claims to
  /// govern "everything below". Cross-filtering the rest against each other
  /// would make every number move as you tick and stop the camera counts
  /// summing to [total], which is the readout they are checked against.
  ///
  /// [total] and [alerts] stay whole either way — the first is the M in the
  /// column's *N of M*, and the second would otherwise say "2 of 2".
  factory ActivityFacets.of(
    List<ActivityItem> items, {
    bool alertsOnly = false,
  }) {
    var alerts = 0;
    final kinds = <ActivityKind, int>{};
    final cameras = <String, int>{};
    final labels = <String, int>{};

    // The glyph a chip wears, taken from the first row that carried the label.
    // Every row with the same class draws the same icon, so the first is as
    // good as any and cheaper than agreeing on one.
    final glyphs = <String, ActivityIcon>{};

    for (final item in items) {
      if (item.isAlert) alerts++;

      // Registered from every row, scoped or not, and only *counted* from the
      // ones in scope. A label the toggle has emptied has to stay in the list
      // at zero — dimmed is a fact about it, gone is a chip that moved.
      // The kinds and the cameras need no such seeding: the panel lists those
      // from the enum and the registry, and reads a missing key as none.
      if (item.label case final label?) {
        labels.putIfAbsent(label, () => 0);
        glyphs.putIfAbsent(label, () => item.icon);
      }

      if (alertsOnly && !item.isAlert) continue;

      kinds.update(item.activityKind, (n) => n + 1, ifAbsent: () => 1);
      cameras.update(item.cameraId, (n) => n + 1, ifAbsent: () => 1);
      if (item.label case final label?) labels[label] = labels[label]! + 1;
    }

    // Most frequent first, then alphabetical so a tie does not reshuffle
    // between rebuilds — a chip that swaps places with its neighbour every
    // time an event arrives is a chip you cannot click.
    final ranked = labels.entries.toList()
      ..sort((a, b) {
        final byCount = b.value.compareTo(a.value);
        return byCount != 0 ? byCount : a.key.compareTo(b.key);
      });

    return ActivityFacets(
      total: items.length,
      alerts: alerts,
      kinds: kinds,
      cameras: cameras,
      labels: [
        for (final entry in ranked)
          ActivityLabelFacet(
            label: entry.key,
            count: entry.value,
            icon: glyphs[entry.key] ?? ActivityIcon.scene,
          ),
      ],
    );
  }

  static const empty = ActivityFacets(
    total: 0,
    alerts: 0,
    kinds: {},
    cameras: {},
    labels: [],
  );

  final int total;
  final int alerts;
  final Map<ActivityKind, int> kinds;

  /// By camera id, not name — the registry is what turns one into the other,
  /// and this is counted without it.
  final Map<String, int> cameras;

  /// Only the classes this server has really seen, most frequent first. Short
  /// on a quiet house and empty on a fresh install, which is the honest answer:
  /// a facet listing every class the model *could* name would be a list of
  /// filters that all come back blank.
  final List<ActivityLabelFacet> labels;
}

/// What a narrowed column is holding, as the one sentence that says it.
///
/// Shared rather than written out where it is drawn, because there are three
/// such places — the desktop column's header, the phone sheet's head, and the
/// bar the phone's filter puts *Done* in — and three copies of a sentence that
/// has to agree is three chances for it not to.
String eventsInView(int shown, int total) =>
    '$shown of the $total ${total == 1 ? 'event' : 'events'} in view';

/// One class the server has seen, as the *What was seen or heard* facet draws
/// it: the model's own word for it, how often, and the glyph its rows wear.
class ActivityLabelFacet {
  const ActivityLabelFacet({
    required this.label,
    required this.count,
    required this.icon,
  });

  final String label;
  final int count;
  final ActivityIcon icon;
}

/// One row in the "What's happening" column.
///
/// This is the flattened, render-ready shape the column wants, not a mirror of
/// any single telemetry document — the column merges all four kinds into one
/// running list, which is the whole point of the design. Building one from a
/// document means picking `timestamp` for utterances and scenes but
/// `started_at` for diarizations and transcripts; there is no single unified
/// time field across the four.
class ActivityItem {
  const ActivityItem({
    required this.id,
    required this.kind,
    required this.cameraId,
    required this.cameraName,
    required this.at,
    required this.timeLabel,
    required this.text,
    required this.icon,
    this.speaker,
    this.label,
    this.speakerNumber,
    this.emotion,
    this.turns = const [],
    this.isAlert = false,
    this.isSpeech = false,
    this.isRecent = false,
  });

  final String id;
  final TelemetryKind kind;
  final String cameraId;

  /// Telemetry carries only `camera_id`; the column shows the camera's name,
  /// so the feed has to be joined against the camera list.
  final String cameraName;

  /// When it happened.
  ///
  /// Carried alongside [timeLabel] rather than parsed back out of it, because the label is prose
  /// — "now", "4:12 pm yesterday" — and this is what *Open camera* seeks the recording to.
  final DateTime at;

  /// Prose rather than a timestamp — "now", "heard just now", "4:12 pm",
  /// "6:40 pm yesterday" — formatted by `activityTimeLabel` from the document's
  /// `timestamp` / `started_at` against the clock.
  final String timeLabel;

  final String text;

  /// Which Phosphor glyph leads the row — a person, a car, a cat, a dropped
  /// connection. No telemetry field names one, so it is read from what the
  /// document already says: a detection's label, a sound's label, and failing
  /// both, what a scene description mentions.
  final ActivityIcon icon;

  /// Where the voice was, or how many there were — never *which*.
  ///
  /// Deliberately no longer the wire's own `speaker_0`: that is an index into
  /// one conversation, meaningless across conversations and meaningless to a
  /// reader, and it was being printed to people verbatim. Identity now rides in
  /// [speakerNumber] on the quote itself, which is why this slot can afford to
  /// answer the vaguer question. Null on scenes and sounds, which have nobody
  /// to name.
  ///
  /// Shown only where [cameraName] is not: on the wall the heading answers
  /// "which camera", and there is no room for a second question. On the
  /// single-camera panel the camera is a given, so the slot goes to this.
  final String? speaker;

  /// Which voice this was, 1-based, or null to draw no bubble.
  ///
  /// The wire counts from zero and the screen counts from one, because ① is for
  /// a person to read and `speaker_0` never was.
  ///
  /// **Only set when the conversation had more than one voice.** Null means
  /// there was nobody to distinguish this from — a permanent ① beside a
  /// monologue is a distinction with nothing on the other side of it. An
  /// utterance with no conversation behind it is null for the same reason:
  /// there is no scope in which to count.
  final int? speakerNumber;

  /// How this row sounded, where a reading survived. Null draws nothing.
  ///
  /// Set on a live utterance from its own record. **Not** set on a settled
  /// conversation: there is no whole-conversation feeling, and averaging its
  /// turns' readings into one would be an invention. Those live on [turns].
  final ActivityEmotion? emotion;

  /// A settled conversation's turns, or empty on every other row.
  ///
  /// A settled conversation stays *one row* — that decision is unchanged — but
  /// its turns are now drawn inside it, so the attribution you were watching
  /// during the conversation does not vanish the moment it ends.
  ///
  /// [text] is kept alongside rather than replaced: it is the flowing fallback
  /// for a transcript that arrives with no turns at all, and it is what the
  /// flat search reads.
  final List<ActivityTurn> turns;

  /// The class the model named, verbatim — a detector's `person`, a sound's
  /// full AudioSet phrase. Null on speech and scenes, which have no class: a
  /// scene is prose the vision model was asked to write, not a label it picked
  /// from a list.
  ///
  /// The sound's whole phrase rather than its first synonym, so *Gunshot,
  /// gunfire* stays one filter. The row itself still reads the short label —
  /// a label is what you filter by, not what you put in a sentence.
  final String? label;

  /// Whether the record this row came from claimed severity — `is_alert` on a
  /// sound or a detection. Alert rows get the orange tint and the Open camera /
  /// Dismiss actions.
  final bool isAlert;

  /// Speech rows get the accent left-bar quotation treatment.
  final bool isSpeech;

  /// Splits the column into its "Right now" and "Earlier today" sections.
  final bool isRecent;

  /// Which of the filter's four readings this row is.
  ///
  /// Derived rather than stored: [kind] already carries it, and a second field
  /// saying the same thing is a second place for it to disagree.
  ActivityKind get activityKind => activityKindOf(kind);

  /// Whether this row answers a search for [needle], which is already
  /// lowercase and trimmed.
  ///
  /// What was said or seen, plus who said it. Not the camera name — that is
  /// what the camera facet is for, and a search for "kitchen" that returned
  /// every row from the Kitchen camera would bury the one where somebody
  /// actually said the word.
  ///
  /// The turns are searched as well as [text]. Today that finds nothing new —
  /// the Server builds a transcript's `text` by joining its turns — but the row
  /// now *draws* the turns, and a search that matched only a string the reader
  /// cannot see would depend on the Server keeping a promise this side has no
  /// way to check.
  ///
  /// The emotion is deliberately not searchable. It is a glyph, not a word on
  /// screen, and a search for "sad" returning every downbeat sentence would be
  /// matching something nobody typed.
  bool matches(String needle) =>
      text.toLowerCase().contains(needle) ||
      turns.any((turn) => turn.text.toLowerCase().contains(needle)) ||
      (speaker?.toLowerCase().contains(needle) ?? false) ||
      (label?.toLowerCase().contains(needle) ?? false);
}

/// One turn inside a settled conversation's row: what was said, by which voice,
/// and how it sounded.
///
/// Distinct from `ConversationTurn` in `telemetry_documents.dart` on purpose.
/// That one is the wire shape — seconds from the conversation's start, a 0-based
/// index, unquoted text. This one is what a row draws: already quoted, already
/// 1-based, with the emotion resolved.
///
/// Deliberately carries no time. The per-turn clock times were given up when the
/// design merged the transcript tab into the feed, and a spare `DateTime` here
/// would be an invitation to put them back one row at a time rather than as a
/// decision.
class ActivityTurn {
  const ActivityTurn({required this.text, this.speakerNumber, this.emotion});

  /// Already quote-wrapped, exactly as [ActivityItem.text] is for speech — so a
  /// settled row is typeset like a live one rather than looking like a
  /// different kind of thing.
  final String text;

  /// 1-based, or null when the conversation had only one voice and the row
  /// draws no bubbles at all.
  final int? speakerNumber;

  final ActivityEmotion? emotion;
}

/// How something sounded, as far as the audio model would commit.
///
/// The wire carries nine words. Only these six are members, and the omission is
/// the point: `neutral`, `emo_unknown` and `other` all mean "nothing worth
/// saying", and an enum case for one of them is an invitation for a call site to
/// draw it. A neutral face beside a sentence is a claim the model did not make,
/// and it would sit on nearly every speech row — the common case would become
/// the noisy one.
///
/// So those three, an absent field, and any word a later schema invents all
/// collapse to null through [fromWire], and null draws nothing.
enum ActivityEmotion {
  happy,
  sad,
  angry,
  fearful,
  disgusted,
  surprised;

  /// Reads the lowercase word the analyzer emits. Anything else is null — see
  /// the note above on why that is most of the vocabulary.
  static ActivityEmotion? fromWire(String? value) => switch (value) {
    'happy' => ActivityEmotion.happy,
    'sad' => ActivityEmotion.sad,
    'angry' => ActivityEmotion.angry,
    'fearful' => ActivityEmotion.fearful,
    'disgusted' => ActivityEmotion.disgusted,
    'surprised' => ActivityEmotion.surprised,
    _ => null,
  };
}

/// The glyphs the design uses to lead an activity row. Kept as an enum rather
/// than an `IconData` so the model layer stays free of Flutter widget imports
/// and can be built straight from a future JSON payload.
enum ActivityIcon {
  person,
  speech,
  car,
  cat,
  connectionLost,
  garage,
  scene,

  /// A dog, separate from [cat] rather than folded into a generic animal glyph:
  /// a barking dog under a cat reads as a bug, not as a simplification.
  dog,

  /// Something the operator asked to be told about — glass, an alarm, a siren.
  alarm,
}
