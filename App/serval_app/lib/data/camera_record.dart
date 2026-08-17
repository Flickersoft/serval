/// The Server's `Camera` entity, verbatim — the shape `/api/cameras` reads and writes.
///
/// Kept apart from [Camera](../models/camera.dart), which is the *view* model the wall and the
/// single-camera screen render: that one carries a tile placeholder, an online flag and a
/// recording flag, none of which exist on the wire. This one carries only what the Server stores,
/// because it has to survive a round trip.
///
/// **`PUT /api/cameras/{id}` replaces rather than merges** — "send every field you want kept",
/// as its own description says. So [toJson] writes the whole record, and the settings screen
/// edits a copy of what [fromJson] read rather than assembling a patch. That is also why
/// `onvifPassword` is held here in clear even though the form never displays it: dropping it on
/// read would delete the camera's credentials on the next save of any unrelated field.
///
/// Casing is camelCase here and snake_case in
/// [telemetry_documents.dart](telemetry_documents.dart). That is not an oversight on either side.
library;

/// What a stream is used for. Exactly one stream must carry `detect` and one `live`, and at most
/// one `record`; a stream may carry none at all, in which case it is kept and never pulled. The
/// Server rejects anything else, and [CameraRecord.roleProblem] mirrors that check so the form can
/// say so before saving.
enum StreamRole {
  record('record', 'Recording'),
  detect('detect', 'Watching for things'),
  live('live', 'Live view');

  const StreamRole(this.wireName, this.label);

  /// The value on the wire.
  final String wireName;

  /// The design's plain-language name for it. An installer reads "Recording", not "record".
  final String label;

  static StreamRole? fromWire(String value) {
    for (final role in values) {
      if (role.wireName == value) return role;
    }
    return null;
  }
}

/// An opt-in re-encode on the record stream. Absent means the camera's bits are archived exactly
/// as they arrive, which is the Server's default and the cheap path.
class TranscodeSettings {
  const TranscodeSettings({required this.codec, this.bitrate});

  factory TranscodeSettings.fromJson(Map<String, dynamic> json) =>
      TranscodeSettings(
        codec: (json['codec'] as String?) ?? 'h264',
        bitrate: json['bitrate'] as String?,
      );

  /// One of `h264`, `vp9`, `av1`. Validated server-side against the encoders this host's ffmpeg
  /// actually has, so an impossible request comes back as a 400 naming the missing encoder.
  final String codec;

  /// e.g. `4M`. Null falls back to the Server's `Ingest:Bitrate`.
  final String? bitrate;

  Map<String, dynamic> toJson() => {
    'codec': codec,
    if (bitrate != null && bitrate!.isNotEmpty) 'bitrate': bitrate,
  };

  TranscodeSettings copyWith({String? codec, String? bitrate}) =>
      TranscodeSettings(
        codec: codec ?? this.codec,
        bitrate: bitrate ?? this.bitrate,
      );
}

/// Per-camera overrides for how loud a sound has to be before Serval listens to it. Every field
/// is optional; null means the Server's own default.
///
/// These are per-camera because the right value is a property of the room. A quiet indoor camera
/// measured at a peak of 0.0124 RMS needed roughly 0.0015 to hear speech at all — at the shipped
/// default of 0.01 its gate admitted 3% of windows and threw away ten of every eleven things said
/// in front of it. The outdoor camera on the same Server is correct at the default and would run
/// its model on traffic noise continuously at 0.0015. No single number is right for both.
class AudioTuningSettings {
  const AudioTuningSettings({
    this.speechGateRmsThreshold,
    this.vadThreshold,
    this.soundGateRmsThreshold,
  });

  factory AudioTuningSettings.fromJson(Map<String, dynamic> json) =>
      AudioTuningSettings(
        speechGateRmsThreshold: (json['speechGateRmsThreshold'] as num?)
            ?.toDouble(),
        vadThreshold: (json['vadThreshold'] as num?)?.toDouble(),
        soundGateRmsThreshold: (json['soundGateRmsThreshold'] as num?)
            ?.toDouble(),
      );

  /// Below this level the audio is treated as silence and never reaches the speech detector.
  final double? speechGateRmsThreshold;

  /// How certain the detector must be that a window is speech. A probability, 0 to 1 exclusive.
  final double? vadThreshold;

  /// The same idea as [speechGateRmsThreshold], for the sound-tagging branch. Separate because
  /// the two are looking for different things — a glass break across a room, versus a voice near
  /// the camera.
  final double? soundGateRmsThreshold;

  /// Nothing overridden. The Server collapses this to no tuning at all on save, so the form does
  /// the same thing rather than reporting an unsaved change that would not survive the trip.
  bool get isEmpty =>
      speechGateRmsThreshold == null &&
      vadThreshold == null &&
      soundGateRmsThreshold == null;

  Map<String, dynamic> toJson() => {
    if (speechGateRmsThreshold != null)
      'speechGateRmsThreshold': speechGateRmsThreshold,
    if (vadThreshold != null) 'vadThreshold': vadThreshold,
    if (soundGateRmsThreshold != null)
      'soundGateRmsThreshold': soundGateRmsThreshold,
  };

  /// A clear flag per knob, for the same reason [CameraRecord.copyWith] has them: `null` in a
  /// named argument cannot be told apart from "not passed", and *Use the default* has to be able
  /// to blank one threshold while leaving the other two alone.
  AudioTuningSettings copyWith({
    double? speechGateRmsThreshold,
    double? vadThreshold,
    double? soundGateRmsThreshold,
    bool clearSpeechGate = false,
    bool clearVad = false,
    bool clearSoundGate = false,
  }) => AudioTuningSettings(
    speechGateRmsThreshold: clearSpeechGate
        ? null
        : speechGateRmsThreshold ?? this.speechGateRmsThreshold,
    vadThreshold: clearVad ? null : vadThreshold ?? this.vadThreshold,
    soundGateRmsThreshold: clearSoundGate
        ? null
        : soundGateRmsThreshold ?? this.soundGateRmsThreshold,
  );

  @override
  bool operator ==(Object other) =>
      other is AudioTuningSettings &&
      other.speechGateRmsThreshold == speechGateRmsThreshold &&
      other.vadThreshold == vadThreshold &&
      other.soundGateRmsThreshold == soundGateRmsThreshold;

  @override
  int get hashCode =>
      Object.hash(speechGateRmsThreshold, vadThreshold, soundGateRmsThreshold);
}

/// The marker meaning "this argument was not passed", so a `copyWith` can tell that apart from
/// being passed `null`.
///
/// [AudioTuningSettings.copyWith] solves the same problem with a `clear…` flag per field, which is
/// readable at three fields and unreadable at eleven — [DetectionTuningSettings] would need
/// twenty-two named arguments to say what these say with one each. Both spellings mean exactly the
/// same thing: omit to keep, pass null to fall back to the Server's default.
const Object _keep = _KeepSentinel();

class _KeepSentinel {
  const _KeepSentinel();
}

/// One region of a camera's view that detection ignores, as a polygon in fractions of the frame.
///
/// Drawn on its own screen — see `MaskEditorScreen` — because a polygon drawn at thumbnail size is
/// a polygon you will redraw. Carried whole on every save, because `PUT` replaces rather than
/// merges and a mask that isn't sent is a mask deleted.
class DetectionMaskSettings {
  const DetectionMaskSettings({required this.points, this.name, this.classes});

  factory DetectionMaskSettings.fromJson(Map<String, dynamic> json) =>
      DetectionMaskSettings(
        name: json['name'] as String?,
        points: [
          for (final point in (json['points'] as List<dynamic>? ?? const []))
            (point as num).toDouble(),
        ],
        classes: switch (json['classes']) {
          final List<dynamic> items => [for (final item in items) '$item'],
          _ => null,
        },
      );

  final String? name;

  /// Flat `x1, y1, x2, y2, …` in 0..1. Flat rather than a list of pairs because that is the shape
  /// the Server stores and binds from environment variables.
  final List<double> points;

  /// Which detection classes this shape silences, or null for every one of them.
  ///
  /// Null and empty mean the same thing and both mean *everything* — the reading the Server takes,
  /// and the only sensible one for "ignore this shape" with nothing narrowing it. A pavement mask
  /// naming `car` and `truck` still reports the person walking along it.
  final List<String>? classes;

  Map<String, dynamic> toJson() => {
    'name': name,
    'points': points,
    'classes': classes,
  };

  DetectionMaskSettings copyWith({
    String? name,
    List<double>? points,
    Object? classes = _keep,
  }) => DetectionMaskSettings(
    name: name ?? this.name,
    points: points ?? this.points,
    classes: identical(classes, _keep)
        ? this.classes
        : classes as List<String>?,
  );

  @override
  bool operator ==(Object other) =>
      other is DetectionMaskSettings &&
      other.name == name &&
      _sameDoubles(other.points, points) &&
      _sameStrings(other.classes, classes);

  @override
  int get hashCode => Object.hash(
    name,
    Object.hashAll(points),
    classes == null ? null : Object.hashAll(classes!),
  );
}

/// Per-camera overrides for what this camera looks for and what it treats as news. Every field is
/// optional; null means the Server's own default.
///
/// Per-camera because almost all of it is a fact about the view rather than about the software.
/// Which objects are worth an alert depends on what the camera points at; how long something must
/// be gone before returning counts as arriving is the difference between a driveway where cars park
/// and a hallway where they never do.
class DetectionTuningSettings {
  const DetectionTuningSettings({
    this.classes,
    this.describeClasses,
    this.scoreThreshold,
    this.minObjectFraction,
    this.trackConfirmSeconds,
    this.trackCoastSeconds,
    this.masks,
    this.alertClasses,
    this.alertMinConfidence,
    this.maxFps,
    this.minMovementFraction,
    this.absenceSeconds,
    this.noveltySeconds,
  });

  factory DetectionTuningSettings.fromJson(Map<String, dynamic> json) =>
      DetectionTuningSettings(
        classes: _strings(json['classes']),
        describeClasses: _strings(json['describeClasses']),
        scoreThreshold: (json['scoreThreshold'] as num?)?.toDouble(),
        minObjectFraction: (json['minObjectFraction'] as num?)?.toDouble(),
        trackConfirmSeconds: (json['trackConfirmSeconds'] as num?)?.toDouble(),
        trackCoastSeconds: (json['trackCoastSeconds'] as num?)?.toDouble(),
        masks: json['masks'] == null
            ? null
            : [
                for (final mask in json['masks'] as List<dynamic>)
                  DetectionMaskSettings.fromJson(mask as Map<String, dynamic>),
              ],
        alertClasses: _strings(json['alertClasses']),
        alertMinConfidence: (json['alertMinConfidence'] as num?)?.toDouble(),
        maxFps: (json['maxFps'] as num?)?.toDouble(),
        minMovementFraction: (json['minMovementFraction'] as num?)?.toDouble(),
        absenceSeconds: (json['absenceSeconds'] as num?)?.toDouble(),
        noveltySeconds: (json['noveltySeconds'] as num?)?.toDouble(),
      );

  /// Which of the model's classes this camera records at all.
  final List<String>? classes;

  /// Which of those are worth a written description. Usually much shorter.
  final List<String>? describeClasses;

  /// Which raise an alert rather than only a record.
  final List<String>? alertClasses;

  /// How sure the detector must be. Raise it on a view that produces confident nonsense.
  final double? scoreThreshold;

  /// How small a thing can be and still count, as a share of the frame's area. The one filter
  /// confidence cannot express, because crops make the model *more* sure of a speck.
  final double? minObjectFraction;

  /// The higher bar for claiming an alert.
  final double? alertMinConfidence;

  /// How long something must keep being seen before it is believed. The best knob for a
  /// camera that ghosts.
  final double? trackConfirmSeconds;

  /// How long it keeps being followed after the detector loses sight of it. Longer on a
  /// view with something to walk behind.
  final double? trackCoastSeconds;

  /// How often this camera is examined at all — the per-camera cost lever.
  final double? maxFps;

  /// How far something must shift, as a fraction of the frame, to count as moving.
  final double? minMovementFraction;

  /// How long something must be out of sight before it counts as gone.
  final double? absenceSeconds;

  /// How long it must have been gone before coming back counts as arriving.
  final double? noveltySeconds;

  /// Regions of the view to ignore. Carried but not edited — see [DetectionMaskSettings].
  final List<DetectionMaskSettings>? masks;

  bool get isEmpty =>
      classes == null &&
      describeClasses == null &&
      scoreThreshold == null &&
      minObjectFraction == null &&
      trackConfirmSeconds == null &&
      trackCoastSeconds == null &&
      masks == null &&
      alertClasses == null &&
      alertMinConfidence == null &&
      maxFps == null &&
      minMovementFraction == null &&
      absenceSeconds == null &&
      noveltySeconds == null;

  Map<String, dynamic> toJson() => {
    if (classes != null) 'classes': classes,
    if (describeClasses != null) 'describeClasses': describeClasses,
    if (scoreThreshold != null) 'scoreThreshold': scoreThreshold,
    if (minObjectFraction != null) 'minObjectFraction': minObjectFraction,
    if (trackConfirmSeconds != null) 'trackConfirmSeconds': trackConfirmSeconds,
    if (trackCoastSeconds != null) 'trackCoastSeconds': trackCoastSeconds,
    if (masks != null) 'masks': [for (final mask in masks!) mask.toJson()],
    if (alertClasses != null) 'alertClasses': alertClasses,
    if (alertMinConfidence != null) 'alertMinConfidence': alertMinConfidence,
    if (maxFps != null) 'maxFps': maxFps,
    if (minMovementFraction != null) 'minMovementFraction': minMovementFraction,
    if (absenceSeconds != null) 'absenceSeconds': absenceSeconds,
    if (noveltySeconds != null) 'noveltySeconds': noveltySeconds,
  };

  /// Omit an argument to keep it; pass null to fall back to the Server's default. See [_keep].
  DetectionTuningSettings copyWith({
    Object? classes = _keep,
    Object? describeClasses = _keep,
    Object? scoreThreshold = _keep,
    Object? minObjectFraction = _keep,
    Object? trackConfirmSeconds = _keep,
    Object? trackCoastSeconds = _keep,
    Object? masks = _keep,
    Object? alertClasses = _keep,
    Object? alertMinConfidence = _keep,
    Object? maxFps = _keep,
    Object? minMovementFraction = _keep,
    Object? absenceSeconds = _keep,
    Object? noveltySeconds = _keep,
  }) => DetectionTuningSettings(
    classes: _pick(classes, this.classes),
    describeClasses: _pick(describeClasses, this.describeClasses),
    scoreThreshold: _pick(scoreThreshold, this.scoreThreshold),
    minObjectFraction: _pick(minObjectFraction, this.minObjectFraction),
    trackConfirmSeconds: _pick(trackConfirmSeconds, this.trackConfirmSeconds),
    trackCoastSeconds: _pick(trackCoastSeconds, this.trackCoastSeconds),
    masks: _pick(masks, this.masks),
    alertClasses: _pick(alertClasses, this.alertClasses),
    alertMinConfidence: _pick(alertMinConfidence, this.alertMinConfidence),
    maxFps: _pick(maxFps, this.maxFps),
    minMovementFraction: _pick(minMovementFraction, this.minMovementFraction),
    absenceSeconds: _pick(absenceSeconds, this.absenceSeconds),
    noveltySeconds: _pick(noveltySeconds, this.noveltySeconds),
  );

  @override
  bool operator ==(Object other) =>
      other is DetectionTuningSettings &&
      _sameStrings(other.classes, classes) &&
      _sameStrings(other.describeClasses, describeClasses) &&
      _sameStrings(other.alertClasses, alertClasses) &&
      other.scoreThreshold == scoreThreshold &&
      other.minObjectFraction == minObjectFraction &&
      other.alertMinConfidence == alertMinConfidence &&
      other.trackConfirmSeconds == trackConfirmSeconds &&
      other.trackCoastSeconds == trackCoastSeconds &&
      other.maxFps == maxFps &&
      other.minMovementFraction == minMovementFraction &&
      other.absenceSeconds == absenceSeconds &&
      other.noveltySeconds == noveltySeconds &&
      _sameMasks(other.masks, masks);

  @override
  int get hashCode => Object.hash(
    classes == null ? null : Object.hashAll(classes!),
    describeClasses == null ? null : Object.hashAll(describeClasses!),
    alertClasses == null ? null : Object.hashAll(alertClasses!),
    scoreThreshold,
    minObjectFraction,
    alertMinConfidence,
    trackConfirmSeconds,
    trackCoastSeconds,
    maxFps,
    minMovementFraction,
    absenceSeconds,
    noveltySeconds,
    masks == null ? null : Object.hashAll(masks!),
  );
}

/// Per-camera overrides for which sounds this camera reports and which of them are alarming.
///
/// The most obviously per-camera settings in the whole system: a driveway wants vehicles and
/// glass, a nursery wants crying and a smoke alarm and emphatically not every passing car. One
/// server-wide list serves both badly, and the failure is asymmetric — the sounds that go
/// unreported are silent, while the ones that over-report teach people to ignore the alerts.
class SoundTuningSettings {
  const SoundTuningSettings({
    this.alertLabels,
    this.ignoredLabels,
    this.minConfidence,
    this.alertMinConfidence,
    this.cooldownSeconds,
    this.alertCooldownSeconds,
  });

  factory SoundTuningSettings.fromJson(Map<String, dynamic> json) =>
      SoundTuningSettings(
        alertLabels: _strings(json['alertLabels']),
        ignoredLabels: _strings(json['ignoredLabels']),
        minConfidence: (json['minConfidence'] as num?)?.toDouble(),
        alertMinConfidence: (json['alertMinConfidence'] as num?)?.toDouble(),
        cooldownSeconds: (json['cooldownSeconds'] as num?)?.toDouble(),
        alertCooldownSeconds: (json['alertCooldownSeconds'] as num?)
            ?.toDouble(),
      );

  /// Sounds that raise an alert here, spelled exactly as the model spells them.
  final List<String>? alertLabels;

  /// Sounds this camera never reports — "Speech" beside a television, "Vehicle" beside a road.
  final List<String>? ignoredLabels;

  final double? minConfidence;
  final double? alertMinConfidence;

  /// The minimum gap between two records of the same sound.
  final double? cooldownSeconds;

  final double? alertCooldownSeconds;

  bool get isEmpty =>
      alertLabels == null &&
      ignoredLabels == null &&
      minConfidence == null &&
      alertMinConfidence == null &&
      cooldownSeconds == null &&
      alertCooldownSeconds == null;

  Map<String, dynamic> toJson() => {
    if (alertLabels != null) 'alertLabels': alertLabels,
    if (ignoredLabels != null) 'ignoredLabels': ignoredLabels,
    if (minConfidence != null) 'minConfidence': minConfidence,
    if (alertMinConfidence != null) 'alertMinConfidence': alertMinConfidence,
    if (cooldownSeconds != null) 'cooldownSeconds': cooldownSeconds,
    if (alertCooldownSeconds != null)
      'alertCooldownSeconds': alertCooldownSeconds,
  };

  /// Omit an argument to keep it; pass null to fall back to the Server's default. See [_keep].
  SoundTuningSettings copyWith({
    Object? alertLabels = _keep,
    Object? ignoredLabels = _keep,
    Object? minConfidence = _keep,
    Object? alertMinConfidence = _keep,
    Object? cooldownSeconds = _keep,
    Object? alertCooldownSeconds = _keep,
  }) => SoundTuningSettings(
    alertLabels: _pick(alertLabels, this.alertLabels),
    ignoredLabels: _pick(ignoredLabels, this.ignoredLabels),
    minConfidence: _pick(minConfidence, this.minConfidence),
    alertMinConfidence: _pick(alertMinConfidence, this.alertMinConfidence),
    cooldownSeconds: _pick(cooldownSeconds, this.cooldownSeconds),
    alertCooldownSeconds: _pick(
      alertCooldownSeconds,
      this.alertCooldownSeconds,
    ),
  );

  @override
  bool operator ==(Object other) =>
      other is SoundTuningSettings &&
      _sameStrings(other.alertLabels, alertLabels) &&
      _sameStrings(other.ignoredLabels, ignoredLabels) &&
      other.minConfidence == minConfidence &&
      other.alertMinConfidence == alertMinConfidence &&
      other.cooldownSeconds == cooldownSeconds &&
      other.alertCooldownSeconds == alertCooldownSeconds;

  @override
  int get hashCode => Object.hash(
    alertLabels == null ? null : Object.hashAll(alertLabels!),
    ignoredLabels == null ? null : Object.hashAll(ignoredLabels!),
    minConfidence,
    alertMinConfidence,
    cooldownSeconds,
    alertCooldownSeconds,
  );
}

/// Per-camera overrides for the movement gate — how much of the picture has to change before
/// Serval treats it as something happening.
///
/// Only reached on a Server with object recognition switched off; the two never run together. On
/// such a Server this is the only thing deciding whether the description model runs at all, and its
/// right value is as local as the hearing thresholds': a camera facing a tree needs a higher
/// fraction than one facing a hallway.
class MotionTuningSettings {
  const MotionTuningSettings({
    this.pixelDelta,
    this.minChangedFraction,
    this.maxChangedFraction,
  });

  factory MotionTuningSettings.fromJson(Map<String, dynamic> json) =>
      MotionTuningSettings(
        pixelDelta: (json['pixelDelta'] as num?)?.toInt(),
        minChangedFraction: (json['minChangedFraction'] as num?)?.toDouble(),
        maxChangedFraction: (json['maxChangedFraction'] as num?)?.toDouble(),
      );

  /// How much one point of the picture must change in brightness to count.
  final int? pixelDelta;

  /// How much of the picture must change before it counts as movement.
  final double? minChangedFraction;

  /// The ceiling above which a change is the lights or night mode rather than movement.
  final double? maxChangedFraction;

  bool get isEmpty =>
      pixelDelta == null &&
      minChangedFraction == null &&
      maxChangedFraction == null;

  Map<String, dynamic> toJson() => {
    if (pixelDelta != null) 'pixelDelta': pixelDelta,
    if (minChangedFraction != null) 'minChangedFraction': minChangedFraction,
    if (maxChangedFraction != null) 'maxChangedFraction': maxChangedFraction,
  };

  /// Omit an argument to keep it; pass null to fall back to the Server's default. See [_keep].
  MotionTuningSettings copyWith({
    Object? pixelDelta = _keep,
    Object? minChangedFraction = _keep,
    Object? maxChangedFraction = _keep,
  }) => MotionTuningSettings(
    pixelDelta: _pick(pixelDelta, this.pixelDelta),
    minChangedFraction: _pick(minChangedFraction, this.minChangedFraction),
    maxChangedFraction: _pick(maxChangedFraction, this.maxChangedFraction),
  );

  /// Why the Server would reject this, or null when it would accept it.
  ///
  /// Movement is declared *between* the two fractions, so a minimum at or above the maximum is a
  /// gate that can never open — and a camera that reports nothing looks exactly like a camera
  /// watching a room where nothing happens. Mirrored here so the form can say so before saving.
  String? get problem {
    final low = minChangedFraction;
    final high = maxChangedFraction;
    if (low != null && high != null && low >= high) {
      return 'The smallest change has to be less than the largest, or this camera '
          'can never report movement.';
    }
    return null;
  }

  @override
  bool operator ==(Object other) =>
      other is MotionTuningSettings &&
      other.pixelDelta == pixelDelta &&
      other.minChangedFraction == minChangedFraction &&
      other.maxChangedFraction == maxChangedFraction;

  @override
  int get hashCode =>
      Object.hash(pixelDelta, minChangedFraction, maxChangedFraction);
}

/// Resolves one `copyWith` argument: the sentinel keeps [current], anything else replaces it.
T? _pick<T>(Object? passed, T? current) =>
    identical(passed, _keep) ? current : passed as T?;

List<String>? _strings(Object? value) => value == null
    ? null
    : [for (final item in value as List<dynamic>) item as String];

bool _sameStrings(List<String>? a, List<String>? b) {
  if (a == null || b == null) return a == b;
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

bool _sameDoubles(List<double> a, List<double> b) {
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

bool _sameMasks(
  List<DetectionMaskSettings>? a,
  List<DetectionMaskSettings>? b,
) {
  if (a == null || b == null) return a == b;
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

class CameraStreamRecord {
  const CameraStreamRecord({
    required this.name,
    required this.url,
    required this.roles,
    this.transcode,
  });

  factory CameraStreamRecord.fromJson(
    Map<String, dynamic> json,
  ) => CameraStreamRecord(
    name: (json['name'] as String?) ?? '',
    url: (json['url'] as String?) ?? '',
    roles: [
      // Null-aware element: a role this build does not know is dropped rather than
      // crashing the whole camera list.
      for (final role in (json['roles'] as List<dynamic>? ?? const []))
        ?StreamRole.fromWire(role as String),
    ],
    transcode: json['transcode'] == null
        ? null
        : TranscodeSettings.fromJson(json['transcode'] as Map<String, dynamic>),
  );

  final String name;

  /// `rtsp(s)`, `http(s)` (including HTTP-FLV), `rtmp(s)`, `srt`, or a local file path looped in
  /// realtime as a hardware-free test camera.
  final String url;

  final List<StreamRole> roles;
  final TranscodeSettings? transcode;

  Map<String, dynamic> toJson() => {
    'name': name,
    'url': url,
    'roles': [for (final role in roles) role.wireName],
    'transcode': transcode?.toJson(),
  };

  CameraStreamRecord copyWith({
    String? name,
    String? url,
    List<StreamRole>? roles,
    TranscodeSettings? transcode,
    bool clearTranscode = false,
  }) => CameraStreamRecord(
    name: name ?? this.name,
    url: url ?? this.url,
    roles: roles ?? this.roles,
    transcode: clearTranscode ? null : transcode ?? this.transcode,
  );

  /// The host the stream is pulled from, for the settings header. Null for a file source, which
  /// has no host at all.
  String? get host {
    final parsed = Uri.tryParse(url);
    return parsed == null || parsed.host.isEmpty ? null : parsed.host;
  }

  /// The URL with any password in its userinfo replaced by dots.
  ///
  /// Camera credentials live in the RTSP URL and the design shows the field masked with a reveal,
  /// so the masking has to happen on the string rather than on the widget: the URL is also
  /// copyable, and a copy button that yields dots is worse than useless.
  String get maskedUrl {
    final parsed = Uri.tryParse(url);
    if (parsed == null || !parsed.userInfo.contains(':')) return url;

    final user = parsed.userInfo.split(':').first;
    return url.replaceFirst(parsed.userInfo, '$user:${'•' * 8}');
  }
}

class CameraRecord {
  const CameraRecord({
    required this.id,
    required this.name,
    required this.streams,
    this.location,
    this.enabled = true,
    this.recording = true,
    this.retentionDays,
    this.onvifUrl,
    this.onvifUsername,
    this.onvifPassword,
    this.onvifProfileToken,
    this.twoWayAudio = false,
    this.recordAudio = false,
    this.playbackGainDb = 0,
    this.playbackGateRms,
    this.aiVision = false,
    this.aiAudio = false,
    this.audioTuning,
    this.detectionTuning,
    this.soundTuning,
    this.motionTuning,
  });

  factory CameraRecord.fromJson(Map<String, dynamic> json) => CameraRecord(
    id: (json['id'] as String?) ?? '',
    name: (json['name'] as String?) ?? '',
    location: json['location'] as String?,
    streams: [
      for (final stream in (json['streams'] as List<dynamic>? ?? const []))
        CameraStreamRecord.fromJson(stream as Map<String, dynamic>),
    ],
    enabled: (json['enabled'] as bool?) ?? true,
    recording: (json['recording'] as bool?) ?? true,
    retentionDays: (json['retentionDays'] as num?)?.toInt(),
    onvifUrl: json['onvifUrl'] as String?,
    onvifUsername: json['onvifUsername'] as String?,
    onvifPassword: json['onvifPassword'] as String?,
    onvifProfileToken: json['onvifProfileToken'] as String?,
    twoWayAudio: (json['twoWayAudio'] as bool?) ?? false,
    recordAudio: (json['recordAudio'] as bool?) ?? false,
    playbackGainDb: (json['playbackGainDb'] as num?)?.toDouble() ?? 0,
    playbackGateRms: (json['playbackGateRmsThreshold'] as num?)?.toDouble(),
    aiVision: (json['aiVision'] as bool?) ?? false,
    aiAudio: (json['aiAudio'] as bool?) ?? false,
    audioTuning: json['audioTuning'] == null
        ? null
        : AudioTuningSettings.fromJson(
            json['audioTuning'] as Map<String, dynamic>,
          ),
    detectionTuning: json['detectionTuning'] == null
        ? null
        : DetectionTuningSettings.fromJson(
            json['detectionTuning'] as Map<String, dynamic>,
          ),
    soundTuning: json['soundTuning'] == null
        ? null
        : SoundTuningSettings.fromJson(
            json['soundTuning'] as Map<String, dynamic>,
          ),
    motionTuning: json['motionTuning'] == null
        ? null
        : MotionTuningSettings.fromJson(
            json['motionTuning'] as Map<String, dynamic>,
          ),
  );

  /// A blank camera for the *Add* flow, pre-shaped the way a single-stream camera has to be:
  /// one stream carrying all three roles, since there is no fallback. It records, which is what
  /// almost every camera being added is for — and the switch to say otherwise is one section away.
  factory CameraRecord.blank() => const CameraRecord(
    id: '',
    name: '',
    streams: [
      CameraStreamRecord(
        name: 'main',
        url: '',
        roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
      ),
    ],
  );

  /// Client-supplied and constrained to `[A-Za-z0-9_-]+`, because it doubles as a directory name
  /// and a URL segment. Immutable once created — the URL is authoritative on `PUT`, so a
  /// changed id in a body cannot reassign it.
  final String id;

  final String name;

  /// Free text, and the design groups the camera list by it.
  final String? location;

  final List<CameraStreamRecord> streams;

  /// Registered but not ingested when false.
  final bool enabled;

  /// Whether footage is written to disk. True by default.
  ///
  /// Separate from the `record` role, and the pair is deliberate: the role says *which* stream
  /// would be written and survives this being switched off, so turning recording back on needs no
  /// second decision about where it goes. The Server rejects true with no `record` stream, so this
  /// is never on and inert — see [roleProblem], which mirrors that rule.
  ///
  /// Footage already on disk is untouched: it stays playable and ages out under [retentionDays].
  final bool recording;

  /// Per-camera override; null falls back to the Server's `Media:RetentionDays`.
  final int? retentionDays;

  /// The ONVIF *device service* URL, e.g. `http://192.168.1.50/onvif/device_service`. Its
  /// presence is the whole of what enables PTZ — the Server derives `ptzConfigured` from it.
  final String? onvifUrl;

  final String? onvifUsername;

  /// Returned in clear by `GET /api/cameras`. Held so a `PUT` can send it back unchanged; never
  /// rendered.
  final String? onvifPassword;

  /// Pins a media profile for PTZ. Null lets the Server discover the first PTZ-capable one.
  final String? onvifProfileToken;

  final bool twoWayAudio;
  final bool recordAudio;

  /// How far this camera's audio is lifted when listening, in dB; 0 plays it as recorded.
  final double playbackGainDb;

  /// The level below which this camera's audio is silence during playback, as the RMS of a 16 kHz
  /// window; null gates nothing. What keeps a large [playbackGainDb] from turning the codec's noise
  /// floor into hiss.
  ///
  /// Named for what it does rather than after the Server's `playbackGateRmsThreshold`, because the
  /// `Threshold` suffix reads as belonging with the three detector thresholds in [audioTuning] and
  /// this one has nothing to do with them.
  final double? playbackGateRms;

  final bool aiVision;
  final bool aiAudio;

  /// Per-camera hearing thresholds; null falls back to the Server's `Ai` defaults.
  final AudioTuningSettings? audioTuning;

  /// Per-camera overrides for what this camera looks for; null falls back to the Server's
  /// `Ai:Detection` defaults.
  final DetectionTuningSettings? detectionTuning;

  /// Per-camera overrides for which sounds it reports; null falls back to `Ai:Sound`.
  final SoundTuningSettings? soundTuning;

  /// Per-camera overrides for the movement gate; null falls back to `Ai:Motion`.
  final MotionTuningSettings? motionTuning;

  /// Derived server-side from [onvifUrl]; `ptzConfigured` on the wire is read-only, so it is
  /// recomputed here rather than parsed, and never sent.
  bool get ptzConfigured => (onvifUrl ?? '').trim().isNotEmpty;

  CameraStreamRecord? streamFor(StreamRole role) {
    for (final stream in streams) {
      if (stream.roles.contains(role)) return stream;
    }
    return null;
  }

  /// The host the settings header shows, taken from whichever stream records.
  String? get host =>
      streamFor(StreamRole.record)?.host ?? streams.firstOrNull?.host;

  /// Whether anything is written to disk for this camera.
  ///
  /// False is a supported configuration, not a half-finished one: the camera is still watched and
  /// still viewable live, it just keeps nothing. Everything that would otherwise claim footage —
  /// the REC dot, the "Recording" subtitle, the retention slider, the timeline — asks this first.
  ///
  /// Two ways to be false, collapsed here because nothing downstream distinguishes them: no stream
  /// carries the role, or [recording] is switched off over one that does. Only *Keeping footage*
  /// tells them apart, because only there does the difference change what to do about it.
  bool get records => recording && streamFor(StreamRole.record) != null;

  /// The name of the stream that would be written, whether or not [recording] is on. Null when no
  /// stream carries the role.
  String? get recordStreamName => streamFor(StreamRole.record)?.name;

  /// Why the Server would reject this camera's streams, or null when it would accept them.
  ///
  /// The same rules `CameraValidationException` enforces, checked here so *Save camera* can be
  /// disabled with the reason shown rather than round-tripping a request that 400s. This is exactly
  /// why the design makes roles chips instead of a multi-select — which stream does what is visible
  /// at a glance.
  String? get roleProblem {
    if (streams.isEmpty) return 'A camera needs at least one stream.';

    // A stream with no jobs is legal and inert — kept, never pulled — so only the address is
    // required of every stream. It is still needed on one nothing pulls: the stream is stored
    // whole, and an address blanked here is gone when the stream is put back to work.
    for (final stream in streams) {
      if (stream.url.trim().isEmpty) {
        final label = stream.name.isEmpty ? 'A stream' : '“${stream.name}”';
        return '$label has no address.';
      }
    }

    for (final role in StreamRole.values) {
      final count = streams.where((s) => s.roles.contains(role)).length;
      // Recording is the one job a camera may leave unassigned — it is the only one that costs
      // disk, and keeping nothing is a real thing to want. Two holders is still a mistake.
      if (count == 0 && role != StreamRole.record) {
        return 'No stream is set to “${role.label}”.';
      }
      if (count > 1) {
        return role == StreamRole.record
            ? 'Two streams are both set to “${role.label}” — it has to be one, or none.'
            : 'Two streams are both set to “${role.label}” — it has to be exactly one.';
      }
    }

    // Recording on with nothing to write would be a camera reporting that it keeps footage while
    // keeping none. Off is legal either way — that is the whole point of the switch.
    if (recording && streamFor(StreamRole.record) == null) {
      return 'Recording is on, but no stream is set to “${StreamRole.record.label}”.';
    }

    // Only the recorded stream is written to disk, so a transcode on a stream doing some *other*
    // job is rejected rather than ignored. A stream with no jobs keeps one it already had, inert,
    // so taking a stream out of service and putting it back does not lose the setting.
    for (final stream in streams) {
      if (stream.transcode != null &&
          stream.roles.isNotEmpty &&
          !stream.roles.contains(StreamRole.record)) {
        return '“${stream.name}” is set to re-encode, but only the recording stream can.';
      }
    }

    return null;
  }

  Map<String, dynamic> toJson() => {
    'id': id,
    'name': name,
    'location': location,
    'streams': [for (final stream in streams) stream.toJson()],
    'enabled': enabled,
    'recording': recording,
    'retentionDays': retentionDays,
    'onvifUrl': onvifUrl,
    'onvifUsername': onvifUsername,
    'onvifPassword': onvifPassword,
    'onvifProfileToken': onvifProfileToken,
    'twoWayAudio': twoWayAudio,
    'recordAudio': recordAudio,
    'playbackGainDb': playbackGainDb,
    'playbackGateRmsThreshold': playbackGateRms,
    'aiVision': aiVision,
    'aiAudio': aiAudio,
    // Sent even when null, and always sent. PUT replaces rather than merges, so omitting any of
    // these would wipe that camera's tuning on the next save of an unrelated field — the same trap
    // onvifPassword is held in clear to avoid, and one detectionTuning fell into: it was missing
    // from this class entirely, so every save from this screen silently deleted a camera's
    // classes, thresholds and masks.
    'audioTuning': audioTuning?.toJson(),
    'detectionTuning': detectionTuning?.toJson(),
    'soundTuning': soundTuning?.toJson(),
    'motionTuning': motionTuning?.toJson(),
    // ptzConfigured is deliberately absent: it is computed from onvifUrl and read-only on the
    // Server, so sending it would be a field that looks settable and is silently discarded.
  };

  /// Nullable fields need an explicit clear, since `null` in a named argument is
  /// indistinguishable from "not passed". Only the ones the settings form can actually blank
  /// carry a flag.
  CameraRecord copyWith({
    String? id,
    String? name,
    String? location,
    List<CameraStreamRecord>? streams,
    bool? enabled,
    bool? recording,
    int? retentionDays,
    String? onvifUrl,
    String? onvifUsername,
    String? onvifPassword,
    String? onvifProfileToken,
    bool? twoWayAudio,
    bool? recordAudio,
    double? playbackGainDb,
    double? playbackGateRms,
    bool? aiVision,
    bool? aiAudio,
    AudioTuningSettings? audioTuning,
    Object? detectionTuning = _keep,
    Object? soundTuning = _keep,
    Object? motionTuning = _keep,
    bool clearLocation = false,
    bool clearRetentionDays = false,
    bool clearOnvif = false,
    bool clearAudioTuning = false,
    bool clearPlaybackGate = false,
  }) => CameraRecord(
    id: id ?? this.id,
    name: name ?? this.name,
    location: clearLocation ? null : location ?? this.location,
    streams: streams ?? this.streams,
    enabled: enabled ?? this.enabled,
    recording: recording ?? this.recording,
    retentionDays: clearRetentionDays
        ? null
        : retentionDays ?? this.retentionDays,
    onvifUrl: clearOnvif ? null : onvifUrl ?? this.onvifUrl,
    onvifUsername: clearOnvif ? null : onvifUsername ?? this.onvifUsername,
    onvifPassword: clearOnvif ? null : onvifPassword ?? this.onvifPassword,
    onvifProfileToken: clearOnvif
        ? null
        : onvifProfileToken ?? this.onvifProfileToken,
    twoWayAudio: twoWayAudio ?? this.twoWayAudio,
    recordAudio: recordAudio ?? this.recordAudio,
    playbackGainDb: playbackGainDb ?? this.playbackGainDb,
    playbackGateRms: clearPlaybackGate
        ? null
        : playbackGateRms ?? this.playbackGateRms,
    aiVision: aiVision ?? this.aiVision,
    aiAudio: aiAudio ?? this.aiAudio,
    audioTuning: clearAudioTuning ? null : audioTuning ?? this.audioTuning,
    detectionTuning: _pick(detectionTuning, this.detectionTuning),
    soundTuning: _pick(soundTuning, this.soundTuning),
    motionTuning: _pick(motionTuning, this.motionTuning),
  );
}
