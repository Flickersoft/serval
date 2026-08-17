/// What a camera falls back to, read off the Server's own catalogue.
///
/// Every tuning field on a camera overrides something the Server already has a name, an explanation
/// and a range for, so the camera form asks here and gets back the same [ServerSetting] the Server
/// settings page draws from. One catalogue, one label, one range, one sentence — a form carrying its
/// own copy drifts into allowing a range the Server rejects, and into calling one setting two things
/// on two pages.
///
/// Reduced from `GET /api/settings` rather than hard-coded. Everything degrades: a viewer who
/// cannot read the settings endpoint gets [ServerCameraDefaults.unknown], and every field falls
/// back to the wording and bounds baked into [CameraSetting] below. The form still works — it
/// simply cannot say what the Server's value is, so a field it does not override reads *not set*.
library;

import 'server_settings.dart';

/// The camera-overridable fields, each naming the catalogue entry it overrides.
///
/// The fallbacks are a last resort, for a Server whose settings could not be read; the catalogue
/// wins whenever it is there. A range here that disagrees with the Server's is a bug in this table,
/// not a second opinion.
enum CameraSetting {
  // What it looks for — `CameraDetectionTuning`.
  detectionClasses(
    'Serval:Ai:Detection:Classes',
    label: 'Record these objects',
    help:
        'Everything else this camera sees is ignored. Names come from the detection model — '
        'person, car, dog.',
    kind: SettingKind.textList,
  ),
  describeClasses(
    'Serval:Ai:Detection:DescribeClasses',
    label: 'Describe these objects',
    help:
        'Which of them are worth waking the description model for. Usually a shorter list: '
        'knowing a car has been on the drive since six is worth recording, and not worth seconds '
        'of work to be told about.',
    kind: SettingKind.textList,
  ),
  alertClasses(
    'Serval:Ai:Detection:AlertClasses',
    label: 'Alert on these objects',
    help:
        'Which raise an alert rather than only a record. A person in the garden at night and a '
        'person in the hallway are the same detection and not the same news.',
    kind: SettingKind.textList,
  ),
  scoreThreshold(
    'Serval:Ai:Detection:ScoreThreshold',
    label: 'How sure it must be',
    help:
        'Raise it on a camera whose view produces confident nonsense — foliage at range, '
        'reflections at night.',
    min: 0,
    max: 1,
  ),
  minObjectFraction(
    'Serval:Ai:Detection:MinObjectFraction',
    label: 'Ignore anything smaller than',
    help:
        'How small a thing can be and still count, as a share of the picture. Zero means no floor. '
        'The one job a confidence setting cannot do: with crops on, magnifying a few-pixel speck '
        'makes the model more sure of it, not less — so a lamp post gets called a person and no '
        'confidence floor separates it from the distant cars you wanted.',
    min: 0,
    max: 0.5,
  ),
  alertMinConfidence(
    'Serval:Ai:Detection:AlertMinConfidence',
    label: 'How sure it must be to alert',
    help:
        'Higher than the line above. A false alert costs trust in every alert after it.',
    min: 0,
    max: 1,
  ),
  trackConfirmSeconds(
    'Serval:Ai:Detection:Tracking:ConfirmSeconds',
    label: 'Believe it after',
    help:
        'The best setting for a camera that sees things that are not there. A ghost — a bush lit '
        'by infrared, read as a person — rarely repeats in the same place, while someone really '
        'standing there stays put. Costs this much delay before anything is reported.',
    min: 0,
    max: 30,
    unit: 'seconds',
  ),
  trackCoastSeconds(
    'Serval:Ai:Detection:Tracking:CoastSeconds',
    label: 'Keep following it for',
    help:
        'How long something is still counted as present after the detector stops finding it. This '
        'is what carries somebody behind a parked car and out the other side as one person rather '
        'than two, so a view with things to walk behind wants longer.',
    min: 0,
    max: 60,
    unit: 'seconds',
  ),
  maxFps(
    'Serval:Ai:Detection:MaxFps',
    label: 'How often to look for objects',
    help:
        'The cost lever. A busy drive can be worth looking at every frame while a spare room is '
        'worth one every ten seconds, and spending the same on both is what makes the total '
        'expensive.',
    min: 0,
    max: 30,
    unit: 'per second',
  ),
  minMovementFraction(
    'Serval:Ai:Detection:MinMovementFraction',
    label: 'Counts as moving at',
    help:
        'How fast something must travel to count, as a share of the picture each second. Depends '
        'on how much of the picture a person fills here, which is a fact about where this camera '
        'points.',
    min: 0,
    max: 1,
    unit: 'of the frame a second',
  ),
  absenceSeconds(
    'Serval:Ai:Detection:AbsenceSeconds',
    label: 'Consider it gone after',
    help:
        'How long something must be out of sight before this camera calls it gone. Too short and '
        'someone turning sideways ends one sighting and starts another.',
    min: 1,
    max: 3600,
    unit: 'seconds',
  ),
  noveltySeconds(
    'Serval:Ai:Detection:NoveltySeconds',
    label: 'Counts as arriving if gone for',
    help:
        'What separates an event from the furniture. A drive sees a parked car in every frame for '
        'hours; a living room sees a sofa forever. Neither is an arrival — but a second car pulling '
        'in beside the first one is, because this is asked of each object rather than of its kind.',
    min: 1,
    max: 86400,
    unit: 'seconds',
  ),

  // Motion detection — `CameraMotionTuning`.
  motionMinChangedFraction(
    'Serval:Ai:Motion:MinChangedFraction',
    label: 'Movement when this much has changed',
    help:
        'How much of the picture has to differ from the last frame before this camera looks '
        'harder. Lower on a narrow view where a person fills little of it.',
    min: 0,
    max: 1,
  ),
  motionMaxChangedFraction(
    'Serval:Ai:Motion:MaxChangedFraction',
    label: 'Ignore when more than this has changed',
    help:
        'Above this the whole scene changed — headlights, a light switch, the camera itself '
        'moving — which is not one thing arriving.',
    min: 0,
    max: 1,
  ),
  motionPixelDelta(
    'Serval:Ai:Motion:PixelDelta',
    label: 'Counts as changed if brightness shifts',
    help:
        'How different one pixel has to be to count as changed at all. Raise it on a noisy sensor '
        'or a camera that grades its own picture.',
    kind: SettingKind.integer,
    min: 0,
    max: 255,
  ),

  // Speech & transcription — `CameraAudioTuning`.
  speechGateRmsThreshold(
    'Serval:Ai:AudioGate:RmsThreshold',
    label: 'Counts as silence below',
    help:
        'Below this the transcriber is not woken at all. The meter above shows what this camera '
        'is actually hearing, so set it against the room rather than against a number.',
    min: 0,
    max: 1,
  ),
  vadThreshold(
    'Serval:Ai:Vad:Threshold',
    label: 'Speech confidence floor',
    help:
        'How sure it has to be that a sound is speech before transcribing it. Lower it in a room '
        'where people talk quietly; raise it where a television does.',
    min: 0,
    max: 1,
  ),

  // Sound recognition — `CameraSoundTuning`, plus that path's own gate.
  soundGateRmsThreshold(
    'Serval:Ai:Sound:Gate:RmsThreshold',
    label: 'Counts as silence below',
    help:
        'The same idea for sounds rather than speech: quieter than this and nothing is classified.',
    min: 0,
    max: 1,
  ),
  soundAlertLabels(
    'Serval:Ai:Sound:AlertLabels',
    label: 'Alert on these sounds',
    help:
        'Which sounds are worth an alert rather than only a note. Names come from the sound '
        'model — "Glass", "Dog", "Smoke detector, smoke alarm".',
    kind: SettingKind.textList,
  ),
  soundIgnoredLabels(
    'Serval:Ai:Sound:IgnoredLabels',
    label: 'Never report these sounds',
    help:
        'What this room always sounds like. A kitchen extractor or a fish tank is a sound the '
        'model is right about and nobody needs told.',
    kind: SettingKind.textList,
  ),
  soundMinConfidence(
    'Serval:Ai:Sound:MinConfidence',
    label: 'How sure it must be',
    help: 'Below this the sound is not written down at all.',
    min: 0,
    max: 1,
  ),
  soundAlertMinConfidence(
    'Serval:Ai:Sound:AlertMinConfidence',
    label: 'How sure it must be to alert',
    help: 'Higher than the line above, for the same reason it is on objects.',
    min: 0,
    max: 1,
  ),
  soundCooldownSeconds(
    'Serval:Ai:Sound:CooldownSeconds',
    label: 'Wait before reporting the same sound',
    help:
        'A dog barking for ten minutes is one thing happening. This is how long before it counts '
        'as happening again.',
    min: 0,
    max: 3600,
    unit: 'seconds',
  ),
  soundAlertCooldownSeconds(
    'Serval:Ai:Sound:AlertCooldownSeconds',
    label: 'Wait before alerting on the same sound',
    help: 'The same, for the ones that reach you rather than the log.',
    min: 0,
    max: 3600,
    unit: 'seconds',
  ),

  // Recording — a plain field on the camera rather than a tuning bag.
  retentionDays(
    'Serval:Media:RetentionDays',
    label: 'Keep recordings for',
    help:
        'How far back you can go on this camera. Longer costs disk in direct proportion, which '
        'the estimate under the track spends for you.',
    kind: SettingKind.integer,
    min: 1,
    max: 365,
    unit: 'days',
  );

  const CameraSetting(
    this.key, {
    required this.label,
    required this.help,
    this.kind = SettingKind.number,
    this.min,
    this.max,
    this.unit,
  });

  /// The catalogue path this overrides.
  final String key;

  /// What to call it when the catalogue could not be read.
  final String label;
  final String help;
  final SettingKind kind;
  final double? min;
  final double? max;
  final String? unit;

  /// This field with nothing known about it but its own fallbacks, and no value in force.
  ServerSetting get _bare => ServerSetting(
    key: key,
    group: '',
    label: label,
    help: help,
    kind: kind,
    source: SettingSource.builtIn,
    restartRequired: false,
    min: min,
    max: max,
    unit: unit,
  );
}

class ServerCameraDefaults {
  const ServerCameraDefaults({
    this.catalogue = const {},
    this.pushCooldownSeconds,
  });

  /// What to use when the Server's settings could not be read. Every field falls back to
  /// [CameraSetting]'s own wording, and none of them can say what the Server holds.
  static const unknown = ServerCameraDefaults();

  factory ServerCameraDefaults.from(ServerSettings settings) =>
      ServerCameraDefaults(
        catalogue: {
          for (final field in CameraSetting.values)
            if (settings[field.key] case final setting when setting != null)
              field: setting,
        },
        pushCooldownSeconds: settings['Serval:Push:CooldownSeconds']?.asNumber
            ?.round(),
      );

  /// The catalogue entry behind each field, for the fields the Server actually listed.
  final Map<CameraSetting, ServerSetting> catalogue;

  /// The deployment's wait between two notifications about the same thing, which a person's own
  /// per-camera rule inherits when it says nothing.
  ///
  /// Deliberately not a [CameraSetting], and the exception is worth the paragraph. Every entry in
  /// that enum is a field *on a camera* overriding something the Server has a name for; this one is
  /// overridden by a field on a person's notification rule instead, and filing it there would make
  /// the enum's own doc comment false. It lives here rather than in a second bag because it arrives
  /// in the same catalogue response and is wanted by the same screen.
  ///
  /// Null when the catalogue could not be read, which the notifications card draws as a bare
  /// *Default* rather than guessing a number at somebody.
  final int? pushCooldownSeconds;

  /// The descriptor to draw a card from: the Server's if it listed one, otherwise the field's own
  /// fallbacks. Never null, so the form has no branch in it.
  ServerSetting operator [](CameraSetting field) =>
      catalogue[field] ?? field._bare;

  /// The Server's value in force, or null where it is unknown. This is what a camera that
  /// overrides nothing is actually running on, so it is what such a card reads out.
  Object? valueOf(CameraSetting field) => catalogue[field]?.value;

  /// What a list falls back on: the list someone set on the Server, or the built-in one it resolves
  /// to while that is empty — the same resolution the settings page draws in a chip row.
  List<String> listFor(CameraSetting field) {
    final setting = catalogue[field];
    if (setting == null) return const [];
    return setting.asList.isEmpty ? setting.listFallback : setting.asList;
  }
}
