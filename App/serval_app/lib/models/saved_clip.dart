/// How far a clip has got on the Server.
enum ClipState {
  /// ffmpeg is still running. Not listed, not playable, followed through `/status`.
  writing,

  /// Its video, poster and frozen documents are all on disk.
  ready,

  /// It could not be written. [SavedClip.error] says why.
  failed;

  static ClipState parse(String? value) => switch (value) {
    'ready' => ClipState.ready,
    'failed' => ClipState.failed,
    _ => ClipState.writing,
  };
}

/// A clip somebody chose to keep.
///
/// The one thing in Serval that does not roll off, which is what most of its fields are for: the
/// camera's *name* rather than only its id, because the camera may be renamed or deleted and the
/// clip still has to say where it came from; a size, because these are the only files whose weight
/// is the user's to manage.
class SavedClip {
  const SavedClip({
    required this.id,
    required this.cameraId,
    required this.cameraName,
    required this.name,
    required this.savedBy,
    required this.from,
    required this.to,
    required this.savedAt,
    required this.duration,
    required this.sizeBytes,
    this.state = ClipState.ready,
    this.summary,
  });

  factory SavedClip.fromJson(Map<String, dynamic> json) => SavedClip(
    id: json['id'] as String,
    cameraId: json['cameraId'] as String? ?? '',
    cameraName: json['cameraName'] as String? ?? '',
    name: json['name'] as String? ?? '',
    savedBy: json['savedBy'] as String? ?? '',
    from: DateTime.parse(json['from'] as String).toLocal(),
    to: DateTime.parse(json['to'] as String).toLocal(),
    savedAt: DateTime.parse(json['savedAt'] as String).toLocal(),
    duration: Duration(
      milliseconds: (((json['durationSeconds'] as num?) ?? 0) * 1000).round(),
    ),
    sizeBytes: (json['sizeBytes'] as num?)?.toInt() ?? 0,
    state: ClipState.parse(json['state'] as String?),
    summary: json['summary'] as String?,
  );

  final String id;
  final String cameraId;

  /// The camera's name *when the clip was taken*. Renaming the camera does not rewrite this.
  final String cameraName;

  final String name;

  /// Username of whoever saved it. Only they and an Admin may rename or delete it.
  final String savedBy;

  final DateTime from;
  final DateTime to;
  final DateTime savedAt;
  final Duration duration;
  final int sizeBytes;
  final ClipState state;

  /// Serval's own sentence about what happens across the clip.
  ///
  /// Null on a server with no vision model, and null for a few seconds after a save while the pass
  /// runs. Both mean the same thing to the screen — draw no summary block — so neither needs
  /// distinguishing.
  final String? summary;

  /// Whether [user] may rename or delete this.
  bool mayEdit({required String? user, required bool isAdmin}) =>
      isAdmin || (user != null && user == savedBy);
}

/// One thing said inside a clip, positioned against the clip.
class ClipSpeechLine {
  const ClipSpeechLine({
    required this.at,
    required this.offset,
    required this.text,
    this.speaker,
  });

  factory ClipSpeechLine.fromJson(Map<String, dynamic> json) => ClipSpeechLine(
    at: DateTime.parse(json['timestamp'] as String).toLocal(),
    offset: Duration(
      milliseconds: (((json['offsetSeconds'] as num?) ?? 0) * 1000).round(),
    ),
    text: json['text'] as String? ?? '',
    speaker: json['speaker'] as String?,
  );

  final DateTime at;

  /// How far into the clip it was said — the only clock somebody watching it has.
  final Duration offset;

  final String text;
  final String? speaker;
}

/// A clip with the telemetry frozen alongside it.
///
/// Separate from [SavedClip] because the list draws fourteen cards and must not carry fourteen
/// transcripts to do it. Fetched when one is opened.
class SavedClipDetail {
  const SavedClipDetail({required this.clip, this.speech = const []});

  factory SavedClipDetail.fromJson(Map<String, dynamic> json) =>
      SavedClipDetail(
        clip: SavedClip.fromJson(json),
        speech: [
          for (final line in (json['speech'] as List<dynamic>? ?? const []))
            ClipSpeechLine.fromJson(line as Map<String, dynamic>),
        ],
      );

  final SavedClip clip;

  /// "Said in it", in order.
  final List<ClipSpeechLine> speech;
}

/// How far a save has got, as the dialog reads it.
///
/// [bytesWritten] rather than a percentage because there is no total to divide by until the file is
/// finished — the same honest measure the download path already shows.
class ClipProgress {
  const ClipProgress({
    required this.state,
    this.bytesWritten = 0,
    this.sizeBytes,
    this.error,
  });

  factory ClipProgress.fromJson(Map<String, dynamic> json) => ClipProgress(
    state: ClipState.parse(json['state'] as String?),
    bytesWritten: (json['bytesWritten'] as num?)?.toInt() ?? 0,
    sizeBytes: (json['sizeBytes'] as num?)?.toInt(),
    error: json['error'] as String?,
  );

  final ClipState state;
  final int bytesWritten;
  final int? sizeBytes;
  final String? error;

  bool get done => state == ClipState.ready;
  bool get failed => state == ClipState.failed;
}
