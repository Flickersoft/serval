import '../data/telemetry_documents.dart';

/// What kind of detection raised an alert.
///
/// Both share the queue and both carry a video clip — a sound alert is something you want to look
/// at, not only listen to. The only thing the kind changes on screen is the icon and whether there
/// is a box to draw.
enum AlertKind {
  object,
  sound;

  static AlertKind parse(String? value) =>
      value == 'sound' ? AlertKind.sound : AlertKind.object;
}

/// How far an alert's preview clip has got on the Server.
enum AlertClipState {
  /// The footage it needs has not finished being written. Resolves within the post-roll.
  pending,

  /// The clip and its poster are on disk.
  ready,

  /// There was never any footage to cut from, and there never will be. Ordinary rather than wrong:
  /// a camera whose sub stream cannot be buffered lands here every time.
  unavailable,

  /// The cut went wrong.
  failed;

  static AlertClipState parse(String? value) => switch (value) {
    'ready' => AlertClipState.ready,
    'unavailable' => AlertClipState.unavailable,
    'failed' => AlertClipState.failed,
    _ => AlertClipState.pending,
  };

  /// Whether the card should show a player at all. Everything else draws the still it has, or
  /// nothing.
  bool get playable => this == AlertClipState.ready;

  /// Whether the card should say "in a moment" rather than "there is nothing".
  bool get waiting => this == AlertClipState.pending;
}

/// A detection somebody asked to be told about.
///
/// A queue item, not a library item — which is the whole difference from [SavedClip]. It arrives
/// whether or not anyone asked for it that morning, the only thing to do with one is watch it, and
/// it leaves when somebody clears it. It carries no size and no duration on the card for the same
/// reason: nobody is managing these, they are being read.
class Alert {
  const Alert({
    required this.id,
    required this.cameraId,
    required this.cameraName,
    required this.kind,
    required this.at,
    required this.peakAt,
    required this.label,
    required this.title,
    required this.read,
    required this.clipState,
    required this.recorded,
    this.box,
    this.dismissed = false,
    this.clipSeconds,
  });

  factory Alert.fromJson(Map<String, dynamic> json) => Alert(
    id: json['id'] as String,
    cameraId: json['camera_id'] as String? ?? '',
    cameraName: json['camera_name'] as String? ?? '',
    kind: AlertKind.parse(json['kind'] as String?),
    at: DateTime.parse(json['at'] as String).toLocal(),
    peakAt: DateTime.parse((json['peak_at'] ?? json['at']) as String).toLocal(),
    label: json['label'] as String? ?? '',
    title: json['title'] as String? ?? '',
    box: json['box'] is Map
        ? DetectionBounds.fromJson((json['box'] as Map).cast<String, dynamic>())
        : null,
    read: json['read'] as bool? ?? false,
    dismissed: json['dismissed'] as bool? ?? false,
    clipState: AlertClipState.parse(json['clip_state'] as String?),
    clipSeconds: (json['clip_seconds'] as num?)?.toDouble(),
    recorded: json['recorded'] as bool?,
  );

  final String id;
  final String cameraId;

  /// The camera's name *now*, resolved by the server on every read. Unlike a saved clip, an alert
  /// is a live thing with a lifetime of days — renaming a camera should rename it in a queue
  /// somebody is looking at.
  final String cameraName;

  final AlertKind kind;

  /// When the thing that fired it began. What the queue sorts and groups on, and the second
  /// *Watch* opens the recording at.
  final DateTime at;

  /// The clearest look at it — the frame the poster is taken from, and the frame [box] describes.
  final DateTime peakAt;

  /// The detector's own class string or the AudioSet phrase, verbatim.
  final String label;

  /// The sentence this was announced with, composed by the server and stored. Shown word for word:
  /// what a notification said and what the row says have to match.
  final String title;

  /// Where in the frame it was, in normalised fractions — null for a sound, which has no place.
  ///
  /// Drawn over the poster rather than baked into it, so the same geometry works on a row thumbnail
  /// and on a full-width hero.
  final DetectionBounds? box;

  final bool read;
  final bool dismissed;

  final AlertClipState clipState;
  final double? clipSeconds;

  /// Whether the camera's recording covers [at] — null until the server has decided.
  ///
  /// The only thing that decides whether the card offers to open it. False is ordinary, not a
  /// fault: detections fire whether or not a camera is set to record, and the preview plays either
  /// way — that is what the preview is for.
  ///
  /// Null for the seconds an alert spends [AlertClipState.pending], because the server settles this
  /// with the clip and not before. Test it against `true` and `false` rather than for truthiness:
  /// "we do not know yet" must not draw the chip that means "there is no recording".
  final bool? recorded;

  Alert copyWith({bool? read, bool? dismissed}) => Alert(
    id: id,
    cameraId: cameraId,
    cameraName: cameraName,
    kind: kind,
    at: at,
    peakAt: peakAt,
    label: label,
    title: title,
    box: box,
    read: read ?? this.read,
    dismissed: dismissed ?? this.dismissed,
    clipState: clipState,
    clipSeconds: clipSeconds,
    recorded: recorded,
  );
}

/// The queue and how much of it is new.
///
/// The count travels with the list rather than being derived from it, because the list is limited
/// and the count is not — a header reading "4 new" over three rows is the kind of wrong nobody can
/// explain.
class AlertQueue {
  const AlertQueue({this.items = const [], this.unread = 0});

  factory AlertQueue.fromJson(Map<String, dynamic> json) => AlertQueue(
    items: [
      for (final item in (json['items'] as List<dynamic>? ?? const []))
        Alert.fromJson((item as Map).cast<String, dynamic>()),
    ],
    unread: (json['unread'] as num?)?.toInt() ?? 0,
  );

  final List<Alert> items;
  final int unread;

  bool get isEmpty => items.isEmpty;
}
