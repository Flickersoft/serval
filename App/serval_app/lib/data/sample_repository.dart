import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import '../models/activity.dart';
import '../models/alert.dart';
import '../models/camera.dart';
import '../models/cast_target.dart';
import '../models/clip_selection.dart';
import '../models/saved_clip.dart';
import '../models/config_backup.dart';
import '../models/conversation.dart';
import '../media/media_saver.dart';
import '../models/google_home.dart';
import '../models/ptz.dart';
import '../models/push.dart';
import '../models/server_settings.dart';
import '../models/system_stats.dart';
import '../models/user_preferences.dart';
import '../models/vitals_history.dart';
import '../models/timeline.dart';
import '../playback/playback_volume.dart';
import 'audio_levels_socket.dart';
import 'auth/auth_models.dart' show Role;
import 'auth/user_account.dart';
import 'camera_record.dart';
import 'serval_repository.dart';
import 'telemetry_documents.dart';

/// The push endpoint the sample's *this browser* device is keyed on.
///
/// Exposed because a device row is identified by the hash of its endpoint and nothing else: a test
/// standing in for the browser has to hand back a subscription this deployment already knows, or
/// the screen cannot mark which of the three chips you are sitting at.
const sampleThisBrowserEndpoint =
    'https://push.example/serval/sample-this-browser';

/// The design's own content, verbatim.
///
/// Every camera, event, transcript turn and tick below is what `Serval.dc.html`
/// shows, so the screens can be compared against the mock side by side. This is
/// what the widget tests and the goldens render, which is why it stays `const`
/// and stays free of anything that could vary between runs.
///
/// It satisfies the interface's listenable and command halves by doing nothing:
/// nothing here ever changes, and a sample repository has no Server to command.
/// See [LiveServalRepository](live_repository.dart) for the real one.
class SampleServalRepository implements ServalRepository {
  const SampleServalRepository();

  /// Never fires — the sample content is immutable, so a `ListenableBuilder`
  /// around it builds exactly once.
  @override
  Listenable get activityChanges => const UnchangingSlice();

  /// Nor this: the sample timeline is answered in full on the first read, with no fetch behind it
  /// for anything to wait on.
  @override
  Listenable get timelineChanges => const UnchangingSlice();

  /// Nor any of the rest. Every slice of a fixture repository is still, which is what makes it the
  /// seam the goldens are captured through: one build, and the same pixels every run.
  @override
  Listenable get registryChanges => const UnchangingSlice();

  @override
  Listenable get preferenceChanges => const UnchangingSlice();

  @override
  Listenable get alertChanges => const UnchangingSlice();

  @override
  Listenable get vitalsChanges => const UnchangingSlice();

  @override
  Listenable get deviceChanges => const UnchangingSlice();

  @override
  bool get connected => true;

  /// There is no Server behind this, so the single-camera view paints the design's placeholder
  /// instead of trying to open a peer connection.
  @override
  bool get canStreamLive => false;

  @override
  Uint8List? snapshotFor(String cameraId) => null;

  /// The moment the design's capture was taken — `Tue 30 Jul · 4:18:07 pm`, the time printed on
  /// the mock's clock pill.
  ///
  /// Fixed rather than `now`, and shared with [timelineFor], so the goldens render the same
  /// picture on every run: a scrubber anchored on the wall clock would move its tick labels every
  /// minute and its marks every second.
  static final capturedAt = DateTime(2024, 7, 30, 16, 18, 7);

  @override
  DateTime? pictureTakenAt(String cameraId) => capturedAt;

  /// A shared, permanently-null notifier: with no frames there is nothing to
  /// distinguish per camera, and one instance avoids allocating per tile.
  static final _noFrames = ValueNotifier<Uint8List?>(null);

  @override
  ValueListenable<Uint8List?> frameNotifier(String cameraId) => _noFrames;

  /// The registry as design 2a shows it: a two-stream camera with its roles split the way an
  /// installer actually would, a talk-back camera, one that has dropped out, and one switched
  /// off — so the settings screen has every list state to render, and the same grouping by
  /// place the design draws.
  static const _records = <CameraRecord>[
    CameraRecord(
      id: 'driveway',
      name: 'Driveway',
      location: 'Front yard',
      retentionDays: 7,
      recordAudio: true,
      aiVision: true,
      onvifUrl: 'http://192.168.1.222/onvif/device_service',
      onvifUsername: 'view',
      onvifPassword: 'not-a-real-password',
      streams: [
        CameraStreamRecord(
          name: 'main',
          url:
              'rtsp://view:not-a-real-password@192.168.1.222:554/h264Preview_01_main',
          roles: [StreamRole.record],
        ),
        CameraStreamRecord(
          name: 'sub',
          url:
              'rtsp://view:not-a-real-password@192.168.1.222:554/h264Preview_01_sub',
          roles: [StreamRole.detect, StreamRole.live],
        ),
      ],
    ),
    CameraRecord(
      id: 'front-door',
      name: 'Front door',
      location: 'Front yard',
      twoWayAudio: true,
      recordAudio: true,
      // A calibrated camera, so the demo shows the gain control holding a value rather than only
      // its off state. A doorbell is the realistic case for it: the microphone is small, it faces
      // away from whoever is talking, and it records well below the rest of a wall.
      playbackGainDb: 18,
      playbackGateRms: 0.0006,
      aiVision: true,
      aiAudio: true,
      onvifUrl: 'http://192.168.1.223/onvif/device_service',
      streams: [
        CameraStreamRecord(
          name: 'main',
          url: 'rtsp://192.168.1.223:554/stream',
          roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
        ),
      ],
    ),
    CameraRecord(
      id: 'side-path',
      name: 'Side path',
      location: 'Front yard',
      streams: [
        CameraStreamRecord(
          name: 'main',
          url: 'rtsp://192.168.1.224:554/stream',
          roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
        ),
      ],
    ),
    CameraRecord(
      id: 'back-yard',
      name: 'Back yard',
      location: 'Back garden',
      retentionDays: 14,
      aiVision: true,
      streams: [
        CameraStreamRecord(
          name: 'main',
          url: 'rtsp://192.168.1.225:554/stream',
          roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
        ),
      ],
    ),
    CameraRecord(
      id: 'garage',
      name: 'Garage',
      location: 'Back garden',
      retentionDays: 7,
      aiVision: true,
      streams: [
        CameraStreamRecord(
          name: 'main',
          url: 'rtsp://192.168.1.226:554/stream',
          roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
        ),
      ],
    ),
    CameraRecord(
      id: 'kitchen',
      name: 'Kitchen',
      location: 'Inside',
      enabled: false,
      recordAudio: true,
      aiAudio: true,
      streams: [
        CameraStreamRecord(
          name: 'main',
          url: 'rtsp://192.168.1.227:554/stream',
          roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
        ),
      ],
    ),
  ];

  @override
  List<CameraRecord> cameraRecords() => _records;

  @override
  CameraRecord? cameraRecordById(String id) {
    for (final record in _records) {
      if (record.id == id) return record;
    }
    return null;
  }

  /// True: the sample's arrangement is the one compiled into it, so there is nothing to read and
  /// no read to fail. Anything gated on this stays offered, which is what keeps the goldens showing
  /// the wall as an operator meets it rather than in a degraded state.
  @override
  bool get preferencesKnown => true;

  @override
  Future<void> saveWallLayout(List<TileLayout> layout) async {}

  // ------------------------------------------------------------- notifications
  //
  // The sample account is notified about everything, which is the default a real account starts
  // with — so the goldens show the notifications screen as somebody meets it rather than in some
  // configured state that would have to be maintained alongside the real defaults.

  /// The defaults: notified about everything, which is what a real account starts with.
  @override
  UserPreferences get notificationPreferences => const UserPreferences();

  /// Accepted and discarded, exactly as [saveWallLayout] is. This repository is `const` and holds
  /// no mutable state by design — the goldens want one fixed deployment, not one that drifts with
  /// whatever a test tapped.
  @override
  Future<void> saveNotificationPreferences({
    bool? enabled,
    List<CameraNotificationRule>? rules,
  }) async {}

  /// **Switched off, which is the state nearly every deployment is in** — and the more useful one
  /// to draw, because it is the state the card exists to explain. Turning it on needs a public
  /// HTTPS endpoint and a Nest Hub on the LAN; a sample that pretended to have both would show the
  /// one layout an operator is least likely to see.
  ///
  /// The sentence is the Server's own wording, copied rather than invented: the App renders
  /// whatever reason it is handed, so the sample has to prove that path rather than a local map.
  @override
  Future<GoogleHomeStatus> googleHomeStatus() async => const GoogleHomeStatus(
    effective: false,
    blocker: 'disabled',
    reason:
        'Serval:GoogleHome:Enabled is false, so the Google Home routes are closed. '
        'This is the default. See Docs/google-home.md for what it needs before turning it on.',
    publicBaseUrl: null,
    homeGraphKeyConfigured: false,
    castReceiverConfigured: false,
  );

  /// Nothing linked, matching the status above — a linked account on a disabled integration would
  /// be a state the Server cannot produce.
  @override
  Future<List<GoogleHomeLink>> googleHomeLinks() async => const [];

  @override
  Future<void> unlinkGoogleHome(String agentUserId) async {}

  /// A deployment with notifications on and a key that looks like a real one. The sample never
  /// reaches a browser's push machinery — `PushClient` is the stub under `flutter test` — so this
  /// only has to be well-formed enough for the screen to draw its enabled state.
  @override
  Future<PushConfig> pushConfig() async => const PushConfig(
    vapidPublicKey:
        'BJxKZ3qF7yQmVKcHnT2wLxRzYd8vP1sN4oE6aUgWbXhMqZrTiJlCyDnF3kSvA9uHpO0eRbGtXwYnMkQpLzVdCfI',
    enabled: true,
  );

  /// Three browsers, one of which has never been reached — the state the notifications screen's
  /// device band exists to show, and the one a deployment that is quietly not delivering is in.
  ///
  /// Fixed dates rather than offsets from now, unlike the rest of the sample's clocks: these are
  /// rendered as a date, and a date derived from *now* is a different string every day the goldens
  /// are captured.
  @override
  Future<List<PushDevice>> pushDevices() async => [
    PushDevice(
      id: pushDeviceIdFor(sampleThisBrowserEndpoint),
      label: 'Chrome on Linux',
      createdAt: DateTime.utc(2026, 6, 2),
      lastSuccessAt: DateTime.utc(2026, 8, 3, 8, 12),
    ),
    PushDevice(
      id: 'sample-iphone',
      label: 'Safari on iPhone',
      createdAt: DateTime.utc(2026, 7, 19),
    ),
    PushDevice(
      id: 'sample-windows',
      label: 'Firefox on Windows',
      createdAt: DateTime.utc(2026, 5, 11),
      lastSuccessAt: DateTime.utc(2026, 7, 28, 19, 40),
    ),
  ];

  @override
  Future<void> registerPushDevice({
    required String endpoint,
    required String p256dh,
    required String auth,
  }) async {}

  @override
  Future<void> unregisterPushDevice(String deviceId) async {}

  @override
  Future<PushTestResult> sendTestNotification() async =>
      const PushTestResult(devices: 1, accepted: 1);

  @override
  Future<void> ptzMove(
    String cameraId, {
    double pan = 0,
    double tilt = 0,
    double zoom = 0,
  }) async {}

  @override
  Future<void> ptzStop(String cameraId) async {}

  @override
  Future<void> ptzZoomTo(String cameraId, double position) async {}

  /// Null, like every other route-backed thing here: no Server, so no camera to ask. The slider
  /// falls back to dead reckoning, which is what the goldens should show — a sample build has no
  /// lens whose position could be reported.
  @override
  Future<ZoomPosition?> ptzZoomPosition(String cameraId) async => null;

  @override
  Future<void> ptzPreset(String cameraId, String preset) async {}

  @override
  Future<void> ptzHome(String cameraId) async {}

  /// False, and the buttons say so when pressed rather than being disabled.
  ///
  /// Disabling would drop them to 0.45 opacity in the goldens, making the design harness diverge
  /// from a mock in which they are live — and the honest answer is not "unavailable", it is "there
  /// is no Server behind this build".
  @override
  bool get canSaveMedia => false;

  @override
  Future<SavedMedia> saveSnapshot(String cameraId) => throw UnsupportedError(
    'The sample repository has no Server to save from.',
  );

  @override
  Future<SavedMedia> saveClip(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    void Function(int bytes)? onBytes,
  }) => throw UnsupportedError(
    'The sample repository has no Server to save from.',
  );

  // ------------------------------------------------------------- saved clips
  //
  // Real content rather than empty lists, because this is what the goldens and most of the widget
  // tests render 13a, 13b and 13c from. The fourteen are the design's own, dated against
  // [capturedAt] so the date grouping lands in the buckets the mock draws — a list anchored on the
  // wall clock would slide out of "This week" the week after it was written.

  /// Four-second segments covering whatever window is asked for, so clip mode has boundaries to
  /// snap to wherever the harness happens to be pointed.
  ///
  /// Generated against the request rather than anchored on [capturedAt], unlike everything else
  /// here. The other sample content is fixed so the goldens do not move; this cannot be, because
  /// the trimmer opens around the *playhead* — live, that is the wall clock, and a fixed session
  /// two years in the past would have the harness answer "nothing was recorded here" for the one
  /// screen it exists to show.
  ///
  /// One session, because that is what the trimmer is allowed to work in — a sample spanning two
  /// would exercise a path the Server refuses.
  @override
  Future<List<RecordedSegment>> segmentsFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    const length = Duration(seconds: 4);

    // Aligned to a whole four seconds since the epoch, so the boundaries a handle lands on are the
    // same ones on every rebuild rather than moving with when the request was made.
    final start = DateTime.fromMillisecondsSinceEpoch(
      (from.millisecondsSinceEpoch ~/ length.inMilliseconds) *
          length.inMilliseconds,
    );

    final count = (to.difference(start).inMilliseconds / length.inMilliseconds)
        .ceil();

    return [
      for (var i = 0; i < count.clamp(0, 5400); i++)
        RecordedSegment(
          from: start.add(length * i),
          duration: length,
          initFileName: 'init-sample.mp4',
        ),
    ];
  }

  @override
  Future<List<SavedClip>> savedClips({String? query, String? cameraId}) async {
    final needle = query?.trim().toLowerCase();

    return [
      for (final clip in _sampleClips)
        if ((cameraId == null ||
                cameraId.isEmpty ||
                clip.cameraId == cameraId) &&
            (needle == null ||
                needle.isEmpty ||
                clip.name.toLowerCase().contains(needle) ||
                (clip.summary ?? '').toLowerCase().contains(needle)))
          clip,
    ];
  }

  @override
  Future<SavedClipDetail> savedClip(String id) async {
    final clip = _sampleClips.firstWhere(
      (c) => c.id == id,
      orElse: () => _sampleClips.first,
    );

    // The three lines 13a and 13c print under "Said in it", at the offsets they are drawn at.
    return SavedClipDetail(
      clip: clip,
      speech: clip.id != 'clip-1'
          ? const []
          : [
              ClipSpeechLine(
                at: clip.from.add(const Duration(seconds: 6)),
                offset: const Duration(seconds: 6),
                text: 'Hello? Delivery for number twelve.',
                speaker: 'At the door',
              ),
              ClipSpeechLine(
                at: clip.from.add(const Duration(seconds: 29)),
                offset: const Duration(seconds: 29),
                text: 'Hi! Could you leave it behind the planter?',
                speaker: 'You',
              ),
              ClipSpeechLine(
                at: clip.from.add(const Duration(seconds: 50)),
                offset: const Duration(seconds: 50),
                text: 'Behind the planter, no problem.',
                speaker: 'At the door',
              ),
            ],
    );
  }

  @override
  Future<SavedClip> keepClip({
    required String cameraId,
    required DateTime from,
    required DateTime to,
    required String name,
  }) => throw UnsupportedError(
    'The sample repository has no Server to keep a clip on.',
  );

  @override
  Future<ClipProgress> clipProgress(String id) async =>
      const ClipProgress(state: ClipState.ready);

  @override
  Future<void> renameClip(String id, String name) async {}

  @override
  Future<void> deleteClip(String id) async {}

  /// No server, so no receiver to launch and nothing for one to fetch — the button stays absent in
  /// samples, the same posture [canStreamLive] takes on the wall.
  @override
  Future<String?> castReceiverAppId() async => null;

  @override
  Future<Uri?> castVodUrl(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    required DateTime at,
  }) async => null;

  @override
  Future<CastTarget?> castTarget(String cameraId) async => null;

  /// Null, so the sample clip screens draw their placeholder rather than reaching for a Server —
  /// the same posture [canStreamLive] takes on the wall.
  @override
  Future<Uri?> savedClipUrl(String id) async => null;

  @override
  Future<Map<String, Uri>> clipPosterUrls(List<String> ids) async => const {};

  // --------------------------------------------------------------------- alerts

  @override
  int get unreadAlerts => _sampleAlerts.where((a) => !a.read).length;

  @override
  Future<AlertQueue> alerts({String? cameraId, DateTime? before}) async {
    final queue = [
      for (final alert in _sampleAlerts)
        if ((cameraId == null ||
                cameraId.isEmpty ||
                alert.cameraId == cameraId) &&
            (before == null || alert.at.isBefore(before)))
          alert,
    ];

    return AlertQueue(items: queue, unread: queue.where((a) => !a.read).length);
  }

  @override
  Future<Alert> alert(String id) async => _sampleAlerts.firstWhere(
    (a) => a.id == id,
    orElse: () => _sampleAlerts.first,
  );

  // Accepted and forgotten, like renaming a clip. The screens take these away from the list
  // themselves the moment they are tapped — the fixtures being immutable is what keeps the goldens
  // rendering the same queue every run.

  @override
  Future<void> markAlertRead(String id) async {}

  @override
  Future<void> dismissAlert(String id) async {}

  @override
  Future<int> dismissAllAlerts({String? cameraId}) async =>
      _sampleAlerts.length;

  /// Null, so the alert cards draw their still rather than reaching for a Server — the same
  /// posture [savedClipUrl] takes.
  @override
  Future<Uri?> alertClipUrl(String id) async => null;

  @override
  Future<Map<String, Uri>> alertPosterUrls(List<Alert> alerts) async =>
      const {};

  /// Empty, for the reason [UnchangingSlice] exists: the fixtures are `const` and every one of them
  /// is already settled, so nothing will ever arrive to say an alert moved.
  @override
  Stream<Alert> get alertUpdates => const Stream.empty();

  @override
  Future<SavedMedia> downloadSavedClip(
    String id, {
    void Function(int bytes)? onBytes,
  }) => throw UnsupportedError(
    'The sample repository has no Server to save from.',
  );

  /// True, so the design harness draws *Share* beside *Save to phone* as 13c does.
  ///
  /// Unlike [canSaveMedia], which is false: that one governs whether a press can succeed, and this
  /// governs whether the button exists at all. A sample build that hid Share would be a harness
  /// that cannot show the screen it exists to show.
  @override
  bool get canShare => true;

  @override
  Future<void> shareSavedClip(String id) => throw UnsupportedError(
    'The sample repository has no Server to share from.',
  );

  /// The queue 14a draws: four unread from today, three already seen from yesterday.
  ///
  /// **The one fixture anchored on the real date rather than on [capturedAt]**, and the exception
  /// is what makes it deterministic rather than what breaks it. A queue is grouped by day and
  /// labelled *Today* and *Yesterday*, so a fixed date would render "July 2024" — the same picture
  /// every run, but not the picture the design is of. Taking today's date with fixed clock times
  /// gives both: the headings are always Today and Yesterday, and every time on screen is a
  /// constant. Nothing here reads a wall clock the way the scrubber does.
  ///
  /// The side-path row is the one that matters most to keep: it is an alert on a camera that was
  /// not recording, which is the case the preview clip exists for — it plays like any other, and
  /// only the offer to open the recording is missing.
  static List<Alert> get _sampleAlerts => _buildSampleAlerts();

  static List<Alert> _buildSampleAlerts() {
    final now = DateTime.now();

    DateTime today(int hour, int minute, int second) =>
        DateTime(now.year, now.month, now.day, hour, minute, second);

    Alert alert(
      String id,
      String cameraId,
      String cameraName,
      String label,
      String title,
      DateTime at, {
      bool read = false,
      bool recorded = true,
      AlertKind kind = AlertKind.object,
      DetectionBounds? box,
    }) => Alert(
      id: id,
      cameraId: cameraId,
      cameraName: cameraName,
      kind: kind,
      at: at,
      peakAt: at.add(const Duration(seconds: 4)),
      label: label,
      title: title,
      box: box,
      read: read,
      clipState: AlertClipState.ready,
      recorded: recorded,
      clipSeconds: 20,
    );

    return [
      alert(
        'alert-1',
        'front-door',
        'Front door',
        'person',
        'Person at Front door',
        today(8, 12, 4),
        box: const DetectionBounds(
          x: 0.38,
          y: 0.20,
          width: 0.22,
          height: 0.56,
          score: 0.91,
        ),
      ),
      alert(
        'alert-2',
        'driveway',
        'Driveway',
        'car',
        'Car at Driveway',
        today(7, 46, 22),
        box: const DetectionBounds(
          x: 0.24,
          y: 0.38,
          width: 0.44,
          height: 0.34,
          score: 0.88,
        ),
      ),
      alert(
        'alert-3',
        'back-yard',
        'Back yard',
        'Glass',
        'Glass heard at Back yard',
        today(7, 31, 9),
        kind: AlertKind.sound,
      ),
      alert(
        'alert-4',
        'side-path',
        'Side path',
        'person',
        'Person at Side path',
        today(6, 58, 31),
        recorded: false,
        box: const DetectionBounds(
          x: 0.39,
          y: 0.20,
          width: 0.22,
          height: 0.56,
          score: 0.74,
        ),
      ),
      alert(
        'alert-5',
        'back-yard',
        'Back yard',
        'cat',
        'Cat at Back yard',
        today(23, 27, 12).subtract(const Duration(days: 1)),
        read: true,
        box: const DetectionBounds(
          x: 0.52,
          y: 0.55,
          width: 0.18,
          height: 0.24,
          score: 0.69,
        ),
      ),
      alert(
        'alert-6',
        'driveway',
        'Driveway',
        'car',
        'Car at Driveway',
        today(21, 4, 40).subtract(const Duration(days: 1)),
        read: true,
      ),
      alert(
        'alert-7',
        'front-door',
        'Front door',
        'person',
        'Person at Front door',
        today(18, 40, 2).subtract(const Duration(days: 1)),
        read: true,
      ),
    ];
  }

  static final List<SavedClip> _sampleClips = _buildSampleClips();

  static List<SavedClip> _buildSampleClips() {
    final tuesday = DateTime(
      capturedAt.year,
      capturedAt.month,
      capturedAt.day,
      16,
      3,
      12,
    );

    SavedClip clip(
      String id,
      String camera,
      String cameraName,
      String name,
      DateTime from,
      Duration length,
      int megabytes, {
      String? summary,
    }) => SavedClip(
      id: id,
      cameraId: camera,
      cameraName: cameraName,
      name: name,
      savedBy: 'jeremiah',
      from: from,
      to: from.add(length),
      savedAt: from.add(length),
      duration: length,
      // Decimal, like `formatBytes` — so 84 here reads as the design's "84 MB" rather than as the
      // 88 a binary megabyte would turn it into.
      sizeBytes: megabytes * 1000 * 1000,
      summary: summary,
    );

    return [
      clip(
        'clip-1',
        'front-door',
        'Front door',
        'Parcel behind the planter',
        tuesday,
        const Duration(seconds: 55),
        84,
        summary:
            'A courier walks up with a parcel, speaks twice, and sets it down behind the '
            'planter before leaving frame.',
      ),
      clip(
        'clip-2',
        'back-yard',
        'Back yard',
        'Someone at the gate, 2 am',
        tuesday.subtract(const Duration(days: 1, hours: 14)),
        const Duration(minutes: 2, seconds: 10),
        198,
      ),
      clip(
        'clip-3',
        'driveway',
        'Driveway',
        'Van reversing into the drive',
        tuesday.subtract(const Duration(days: 2, hours: 5)),
        const Duration(seconds: 18),
        27,
      ),
      clip(
        'clip-4',
        'back-yard',
        'Back yard',
        'Cat on the bins again',
        tuesday.subtract(const Duration(days: 8)),
        const Duration(minutes: 1, seconds: 4),
        96,
      ),
      clip(
        'clip-5',
        'back-yard',
        'Back yard',
        'Fence panel came down',
        tuesday.subtract(const Duration(days: 11)),
        const Duration(minutes: 3, seconds: 41),
        331,
      ),
      clip(
        'clip-6',
        'front-door',
        'Front door',
        "Meter reader, said he'd come back",
        tuesday.subtract(const Duration(days: 14)),
        const Duration(seconds: 32),
        48,
      ),
      clip(
        'clip-7',
        'driveway',
        'Driveway',
        'Kids back from school',
        tuesday.subtract(const Duration(days: 15)),
        const Duration(seconds: 47),
        71,
      ),
      clip(
        'clip-8',
        'driveway',
        'Driveway',
        'Bin lorry blocked the drive',
        tuesday.subtract(const Duration(days: 18)),
        const Duration(minutes: 1, seconds: 52),
        168,
      ),
      clip(
        'clip-9',
        'kitchen',
        'Kitchen',
        'Window left open overnight',
        tuesday.subtract(const Duration(days: 19)),
        const Duration(seconds: 24),
        36,
      ),
      clip(
        'clip-10',
        'driveway',
        'Driveway',
        'Two people looking at the cars',
        tuesday.subtract(const Duration(days: 21)),
        const Duration(minutes: 2, seconds: 36),
        234,
      ),
      clip(
        'clip-11',
        'front-door',
        'Front door',
        'Parcel taken, not by us',
        tuesday.subtract(const Duration(days: 24)),
        const Duration(seconds: 39),
        59,
      ),
      clip(
        'clip-12',
        'garage',
        'Garage',
        'Light on all night',
        tuesday.subtract(const Duration(days: 28)),
        const Duration(minutes: 1, seconds: 12),
        108,
      ),
      clip(
        'clip-13',
        'side-path',
        'Side path',
        'Someone came down the side',
        tuesday.subtract(const Duration(days: 33)),
        const Duration(seconds: 51),
        77,
      ),
      clip(
        'clip-14',
        'front-door',
        'Front door',
        'Wrong house, twice',
        tuesday.subtract(const Duration(days: 40)),
        const Duration(seconds: 28),
        42,
      ),
    ];
  }

  @override
  Future<SavedMedia> saveConfigBackup() => throw UnsupportedError(
    'The sample repository has no Server configuration to back up.',
  );

  @override
  Future<ConfigRestoreResult> restoreConfigBackup(List<int> bytes) =>
      throw UnsupportedError(
        'The sample repository has no Server configuration to restore into.',
      );

  /// The design's own camera, and only that one: `front-door` is the single sample camera with
  /// `ptzConfigured`, so it is the only one that would have anything to report.
  ///
  /// Deliberately zoom-less and home-less, matching the real pan/tilt camera this was checked
  /// against — which keeps the harness honest about the case the probe exists for rather than
  /// showing every control at once.
  @override
  PtzProbe ptzProbeFor(String cameraId) => cameraId == 'front-door'
      ? const PtzKnown(
          panTilt: true,
          zoom: false,
          home: false,
          maximumPresets: 64,
          presets: [PtzPreset('1', 'Gate'), PtzPreset('2', 'Street')],
        )
      : const PtzNotConfigured();

  /// The two sample cameras that have an ONVIF endpoint, and only those — the rest have nothing
  /// to ask, exactly as against a Server.
  ///
  /// `driveway` carries the design's own subtitle, since that is the camera the mock's
  /// `Reolink RLC-810A · firmware 3.1.0.956` line belongs to. `front-door` reports no make at
  /// all, which is not laziness: Reolink's E1 Pro firmware answers the literal string
  /// `Manufacturer`, observed on the live NVR, so the harness renders that case too.
  @override
  DeviceInformation? deviceInformationFor(String cameraId) =>
      switch (cameraId) {
        'driveway' => const DeviceInformation(
          manufacturer: 'Reolink',
          model: 'RLC-810A',
          firmwareVersion: '3.1.0.956',
          serialNumber: '00000000000000',
        ),
        'front-door' => const DeviceInformation(
          manufacturer: 'Manufacturer',
          model: 'E1 Pro',
          firmwareVersion: '3.1.0.3149',
        ),
        _ => null,
      };

  /// A healthy server, so the design's `1.8 TB of 4 TB` renders from real fields rather than from
  /// a hardcoded string — and, deliberately, **with no alerts**. A golden that ships with a
  /// permanent warning strip across the wall is a golden nobody looks at twice, and an untroubled
  /// sample system is the honest state to draw, not a convenient one.
  ///
  /// Not `const`, for the same reason [_users] is not: [DateTime] has no const constructor. The
  /// oldest segment is stamped relative to load rather than to a fixed date so the span always
  /// renders as exactly "7 days" — a fixed date in the past would grow by a day every day and take
  /// the goldens with it.
  static final _stats = () {
    final oldest = DateTime.now().subtract(const Duration(days: 7));

    CameraDiskUsage usage(String? id, String label, int gigabytes) =>
        CameraDiskUsage(
          cameraId: id,
          label: label,
          bytes: gigabytes * 1000000000,
          fileCount: gigabytes * 360,
          oldestSegmentAt: id == null ? null : oldest,
          retentionDays: id == null ? null : 7,
          bytesPerDay: id == null ? null : gigabytes * 1000000000 / 7,
        );

    return SystemStats(
      sampledAt: DateTime.now(),
      processUptimeSeconds: const Duration(
        days: 4,
        hours: 6,
      ).inSeconds.toDouble(),
      cpu: const CpuStats(
        containerPercent: 34,
        hostPercent: 41,
        cores: 8,
        loadAverage: [2.1, 1.8, 1.6],
      ),
      memory: const MemoryStats(
        usedBytes: 2617245696,
        limitBytes: 8589934592,
        percent: 30.5,
      ),
      gpu: const GpuStats(
        busyPercent: 6,
        driver: 'amdgpu',
        renderNode: 'renderD128',
        vramUsedBytes: 268435456,
        vramTotalBytes: 2147483648,
        hostWide: true,
      ),
      disk: DiskStats(
        mountPoint: '/media',
        totalBytes: 4000000000000,
        freeBytes: 2200000000000,
        usedBytes: 1800000000000,
        mediaBytes: 1740000000000,
        scannedAt: DateTime.now(),
        scanSeconds: 4.8,
        cameras: [
          usage('driveway', 'Driveway', 412),
          usage('front-door', 'Front door', 388),
          usage('back-yard', 'Back yard', 341),
          usage('kitchen', 'Kitchen', 274),
          usage('garage', 'Garage', 186),
          usage('side-path', 'Side path', 132),
          usage(null, 'Conversation audio', 7),
        ],
      ),
      // Two Coral Edge TPUs split across USB generations, which is the deployment this meter was
      // built for and the one worth having in the reference screenshots. The figures are consistent
      // with each other and with detection below on purpose: 5.2 + 2.6 is the 7.8 a second being
      // examined, and each device's busy share is its own rate times its own latency. The measured
      // per-inference times are the real ones — 15.8 ms on USB 3 against 33.4 ms on USB 2.
      accelerator: const AcceleratorStats(
        label: 'Edge TPU',
        busyPercent: 8,
        inferencesPerSecond: 7.8,
        declinedPerSecond: 0,
        devices: [
          AcceleratorDeviceStats(
            name: '2-2',
            healthy: true,
            link: 'USB 3',
            busyPercent: 8,
            inferencesPerSecond: 5.2,
            meanLatencyMs: 15.8,
          ),
          AcceleratorDeviceStats(
            name: '1-1',
            healthy: true,
            link: 'USB 2',
            busyPercent: 9,
            inferencesPerSecond: 2.6,
            meanLatencyMs: 33.4,
          ),
        ],
      ),
      // A host comfortably keeping up: everything movement suggested looking at was looked at.
      // Deliberately not a degraded one — the sample content is what the goldens render, and a
      // permanent warning state in the reference screenshots would stop reading as a warning.
      detection: const DetectionStats(
        budgetPerSecond: 22.4,
        cameras: 6,
        backend: 'edgetpu/ssdlite_mobiledet',
        lanes: 2,
        healthyLanes: 2,
        examinedPerSecond: 7.8,
        shedPerSecond: 0,
        droppedFramesPerSecond: 0,
        coverage: 1,
      ),
    );
  }();

  @override
  SystemStats? systemStats() => _stats;

  /// Null, like every other route-backed thing here: the sample repository has no Server, so the
  /// meters render without sparklines rather than with invented ones. That is also what keeps the
  /// goldens stable — a chart seeded from `Random` would differ on every capture.
  @override
  VitalsHistory? vitalsHistory() => null;

  @override
  Future<String> openWebRtc(String cameraId, String offer) =>
      throw UnsupportedError(
        'The sample repository has no Server to signal to.',
      );

  @override
  Future<void> createCamera(CameraRecord camera) async {}

  @override
  Future<void> updateCamera(CameraRecord camera) async {}

  @override
  Future<void> deleteCamera(String id) async {}

  /// The design's own household, from 3b: two Admins and two View-only accounts, one of them
  /// switched off. Not `const` — [UserAccount.createdAt] is a `DateTime`, which has no const
  /// constructor — but still fixed content, same as everything else here.
  static final _users = [
    UserAccount(
      username: 'kim',
      displayName: 'Kim',
      role: Role.admin,
      createdAt: DateTime.utc(2024, 1, 12),
    ),
    UserAccount(
      username: 'alex',
      displayName: 'Alex',
      role: Role.admin,
      createdAt: DateTime.utc(2024, 1, 12),
    ),
    UserAccount(
      username: 'sam',
      displayName: 'Sam',
      role: Role.viewer,
      createdAt: DateTime.utc(2024, 3, 4),
    ),
    UserAccount(
      username: 'dad',
      displayName: 'Dad',
      role: Role.viewer,
      createdAt: DateTime.utc(2024, 3, 4),
    ),
  ];

  @override
  Future<List<UserAccount>> listUsers() async => _users;

  @override
  Future<UserAccount> createUser({
    required String username,
    required String displayName,
    required String password,
    required Role role,
  }) async => UserAccount(
    username: username,
    displayName: displayName,
    role: role,
    createdAt: DateTime.now(),
  );

  @override
  Future<UserAccount> updateUser(
    String username, {
    Role? role,
    String? password,
    bool signOutAllSessions = false,
  }) async {
    final existing = _users.firstWhere((user) => user.username == username);
    return UserAccount(
      username: existing.username,
      displayName: existing.displayName,
      role: role ?? existing.role,
      createdAt: existing.createdAt,
    );
  }

  @override
  Future<void> signOutUser(String username) async {}

  @override
  Future<void> changeOwnPassword({
    required String currentPassword,
    required String newPassword,
    bool signOutAllSessions = false,
  }) async {}

  @override
  Future<void> deleteUser(String username) async {}

  /// A handful of real settings, one of each shape the page has to draw and one of each source,
  /// so the goldens cover a changed value beside an untouched one and a restart-gated field beside
  /// an immediate one. Not the whole catalogue: that lives on the Server and runs to a hundred-odd
  /// entries, and a copy here would be a second place to keep the help text correct.
  ///
  /// **Group names and labels must match `SettingsCatalog` even though the values do not have to.**
  /// This is what the goldens draw, so a stale copy here shows the old vocabulary on every captured
  /// screen while the running app shows the new one — which is exactly what happened when the
  /// groups were renamed and this was missed.
  ///
  /// One entry is marked [ServerSetting.advanced] on purpose, so the *Advanced* rule that separates
  /// the two bands is in a golden rather than only in a widget test.
  static final _settings = ServerSettings(
    groups: const [
      'Recording',
      'Live view',
      'Objects & alerts',
      'Sound recognition',
      'Notifications',
    ],
    restartRequired: true,
    updatedAt: DateTime.utc(2024, 3, 9, 18, 40),
    updatedBy: 'jeremiah',
    settings: [
      const ServerSetting(
        key: 'Serval:Media:RetentionDays',
        group: 'Recording',
        label: 'Keep recordings for',
        help:
            'How long footage is kept before the sweep deletes it. This is the default — a '
            'camera can keep its own footage for longer or shorter in its settings.',
        kind: SettingKind.integer,
        source: SettingSource.user,
        restartRequired: false,
        value: 14,
        defaultValue: 7,
        min: 1,
        max: 365,
        unit: 'days',
      ),
      // The one advanced entry, so a golden captures the rule that separates the two bands.
      const ServerSetting(
        key: 'Serval:Ingest:SegmentSeconds',
        group: 'Recording',
        label: 'Length of each recording file',
        help:
            'Recordings are written in chunks of this length. It is the granularity at which '
            'playback seeks and retention prunes.',
        kind: SettingKind.number,
        source: SettingSource.builtIn,
        restartRequired: false,
        value: 4.0,
        defaultValue: 4.0,
        min: 1,
        max: 60,
        unit: 'seconds',
        advanced: true,
      ),
      const ServerSetting(
        key: 'Serval:Ingest:SnapshotFps',
        group: 'Live view',
        label: 'How often wall pictures update',
        help:
            'How often each camera produces the still image the dashboard wall shows. One per '
            'second is plenty; raising it costs a JPEG encode per camera per frame.',
        kind: SettingKind.number,
        source: SettingSource.deployment,
        restartRequired: false,
        value: 1.0,
        defaultValue: 1.0,
        min: 0.1,
        max: 10,
        unit: 'per second',
      ),
      // Here because the notifications screen reads it rather than because the settings screen
      // draws it: each camera's card resolves *Default* against this number, and without it every
      // card on that page falls back to a bare *Default* — which is the state a catalogue that
      // could not be read produces, not the ordinary one worth picturing.
      const ServerSetting(
        key: 'Serval:Push:CooldownSeconds',
        group: 'Notifications',
        label: 'Wait before notifying about the same thing',
        help:
            'How long a camera is left alone after it has interrupted somebody about one thing, '
            'before it may interrupt them about that same thing again. Only the notification is '
            'held back — every alert still lands in the queue.',
        kind: SettingKind.integer,
        source: SettingSource.builtIn,
        restartRequired: false,
        value: 120,
        defaultValue: 120,
        min: 0,
        max: 3600,
        unit: 'seconds',
      ),
      // Widened past the built-in `person`, the way a household actually sets this up — and the
      // reason the notifications screen has an object row to draw at all: what a camera can raise
      // an object alert for is this list, or the camera's own override of it.
      const ServerSetting(
        key: 'Serval:Ai:Detection:AlertClasses',
        group: 'Objects & alerts',
        label: 'Alert on these objects',
        help:
            'Which objects raise an alert rather than just a record, held to the higher '
            'confidence below.',
        kind: SettingKind.textList,
        source: SettingSource.user,
        restartRequired: false,
        value: ['person', 'car', 'dog'],
        defaultValue: <String>[],
        fallback: ['person'],
      ),
      const ServerSetting(
        key: 'Serval:Ai:Sound:Enabled',
        group: 'Sound recognition',
        label: 'Listen for sounds',
        help:
            'Identifies non-speech sounds — glass, alarms, dogs, vehicles. Runs alongside speech '
            'rather than behind it.',
        kind: SettingKind.boolean,
        source: SettingSource.user,
        restartRequired: true,
        value: true,
        defaultValue: false,
      ),
      const ServerSetting(
        key: 'Serval:Ai:Sound:AlertLabels',
        group: 'Sound recognition',
        label: 'Alert on these sounds',
        help:
            'Sounds worth raising an alert for, spelled exactly as the model spells them. Prefer '
            'the general name over the specific one.',
        kind: SettingKind.textList,
        source: SettingSource.builtIn,
        restartRequired: false,
        value: <String>[],
        defaultValue: <String>[],
        fallback: ['Glass', 'Shatter', 'Gunshot, gunfire', 'Fire alarm'],
      ),
      const ServerSetting(
        key: 'Serval:Ai:Sound:MinConfidence',
        group: 'Sound recognition',
        label: 'How sure it must be',
        help:
            'How sure the model must be before writing a sound down. Low on purpose — a wrong '
            'label here costs one row in a feed.',
        kind: SettingKind.number,
        source: SettingSource.builtIn,
        restartRequired: false,
        value: 0.35,
        defaultValue: 0.35,
        min: 0,
        max: 1,
      ),
    ],
  );

  @override
  Future<ServerSettings> settings() async => _settings;

  /// Accepts and reports back unchanged. A sample repository has no Server to tell, and a screen
  /// that appeared to save would be showing something this build cannot do.
  @override
  Future<ServerSettings> updateSettings(Map<String, Object?> changes) async =>
      _settings;

  static const _driveway = Camera(
    id: 'driveway',
    name: 'Driveway',
    aiVision: true,
    recordAudio: true,
    connection: CameraConnection.online,
    isRecording: true,
    resolutionLabel: '1080p',
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF1E2131),
      stripeDark: Color(0xFF181B28),
      bloom: Color(0x129184D9),
    ),
  );

  static const _frontDoor = Camera(
    id: 'front-door',
    name: 'Front door',
    aiVision: true,
    aiAudio: true,
    recordAudio: true,
    twoWayAudio: true,
    // Matches this camera's record in `_records`, so the player's gain control shows the same value
    // the settings form does.
    playbackGainDb: 18,
    playbackGateRms: 0.0006,
    ptzConfigured: true,
    connection: CameraConnection.online,
    isRecording: true,
    needsAttention: true,
    resolutionLabel: '2K',
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF22212C),
      stripeDark: Color(0xFF1B1A24),
      bloom: Color(0x17E0955F),
    ),
  );

  static const _backYard = Camera(
    id: 'back-yard',
    name: 'Back yard',
    aiVision: true,
    resolutionLabel: 'NIGHT',
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF1A2028),
      stripeDark: Color(0xFF161B22),
      bloom: Color(0x0FB4DCD2),
    ),
  );

  static const _kitchen = Camera(
    id: 'kitchen',
    name: 'Kitchen',
    aiAudio: true,
    recordAudio: true,
    hasAudioActivity: true,
    resolutionLabel: '1080p',
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF20222E),
      stripeDark: Color(0xFF1A1C25),
      bloom: Color(0x0DE9E9ED),
    ),
  );

  static const _garage = Camera(
    id: 'garage',
    name: 'Garage',
    aiVision: true,
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF1E2131),
      stripeDark: Color(0xFF181B28),
      bloom: Color(0x0D9184D9),
    ),
  );

  static const _sidePath = Camera(
    id: 'side-path',
    name: 'Side path',
    connection: CameraConnection.offline,
    placeholder: TilePlaceholder(
      stripeLight: Color(0xFF14161F),
      stripeDark: Color(0xFF14161F),
      bloom: Color(0x00000000),
    ),
  );

  @override
  List<Camera> cameras() => const [
    _driveway,
    _frontDoor,
    _backYard,
    _kitchen,
    _garage,
    _sidePath,
  ];

  @override
  Camera? cameraById(String id) {
    for (final camera in cameras()) {
      if (camera.id == id) return camera;
    }
    return null;
  }

  /// Twenty-four columns wide, in `WallGrid`'s coordinates — so a standard tile
  /// is 6x2 and the hero twice that on each axis. The driveway is the hero —
  /// half the width and two standard tiles' height, a quarter of the visible
  /// wall — with four standard tiles beside it and the offline side path below.
  ///
  /// The third row of tiles is deliberately left mostly empty: it is the free
  /// space the rearrange tests drag into, and it is what the wall actually looks
  /// like when tiles are not obliged to tile a fixed grid exactly.
  @override
  List<TileLayout> wallLayout() => const [
    TileLayout(
      cameraId: 'driveway',
      column: 0,
      row: 0,
      columnSpan: 12,
      rowSpan: 4,
    ),
    TileLayout(
      cameraId: 'front-door',
      column: 12,
      row: 0,
      columnSpan: 6,
      rowSpan: 2,
    ),
    TileLayout(
      cameraId: 'kitchen',
      column: 18,
      row: 0,
      columnSpan: 6,
      rowSpan: 2,
    ),
    TileLayout(
      cameraId: 'back-yard',
      column: 12,
      row: 2,
      columnSpan: 6,
      rowSpan: 2,
    ),
    TileLayout(
      cameraId: 'garage',
      column: 18,
      row: 2,
      columnSpan: 6,
      rowSpan: 2,
    ),
    TileLayout(
      cameraId: 'side-path',
      column: 0,
      row: 4,
      columnSpan: 6,
      rowSpan: 2,
    ),
  ];

  /// Not `const`: the rows carry real instants now, so that *Open camera* has somewhere to seek
  /// and the column still splits into its two sections wherever the sample is opened.
  static final _activity = <ActivityItem>[
    ActivityItem(
      id: 'a1',
      kind: TelemetryKind.scene,
      cameraId: 'front-door',
      cameraName: 'Front door',
      at: _sampleAt(minutes: 0),
      timeLabel: 'now',
      text: 'A courier is at the door holding a small parcel.',
      icon: ActivityIcon.person,
      isRecent: true,
    ),
    // One of the two shapes that can raise an alert — a sound whose label the operator listed,
    // or a detection of a class they listed. A scene never can, however alarming its prose, and a
    // harness that showed one would be drawing a state the live app cannot reach.
    ActivityItem(
      id: 'a1b',
      kind: TelemetryKind.sound,
      cameraId: 'front-door',
      cameraName: 'Front door',
      at: _sampleAt(minutes: 0),
      timeLabel: 'now',
      text: 'Glass heard',
      icon: ActivityIcon.alarm,
      label: 'Glass',
      isAlert: true,
      isRecent: true,
    ),
    // Still present, which is the one thing a detection row says that no other record type can.
    ActivityItem(
      id: 'a1c',
      kind: TelemetryKind.detection,
      cameraId: 'front-door',
      cameraName: 'Front door',
      at: _sampleAt(minutes: 0),
      timeLabel: 'now',
      text: 'Person, still there',
      icon: ActivityIcon.person,
      label: 'person',
      isAlert: true,
      isRecent: true,
    ),
    ActivityItem(
      id: 'a2',
      kind: TelemetryKind.utterance,
      cameraId: 'front-door',
      cameraName: 'Front door',
      // An utterance with no diarization behind it, which is the fallback the
      // live repository writes rather than leaving the slot empty.
      speaker: 'At the camera',
      at: _sampleAt(minutes: 0),
      timeLabel: 'heard just now',
      text: "“Hi — I've got a package for you. I'll leave it by the step.”",
      icon: ActivityIcon.speech,
      // A face with no bubble beside it, which is the commonest live shape: one
      // voice in the conversation, so there is nothing to number it against.
      emotion: ActivityEmotion.happy,
      isSpeech: true,
      isRecent: true,
    ),
    // The doorstep exchange after it has settled into a transcript — the shape
    // the single-camera panel spends most of its time showing.
    ActivityItem(
      id: 'a2b',
      kind: TelemetryKind.conversationTranscript,
      cameraId: 'front-door',
      cameraName: 'Front door',
      speaker: '2 speakers',
      at: _sampleClock(16, 18),
      timeLabel: '4:18 pm',
      // The same words twice, on purpose: `text` is the flowing fallback for a
      // transcript that arrives with no turns, and it is what the flat search
      // reads. `turns` is what the row actually draws.
      text:
          '“Hello? Delivery for number twelve.” · '
          '“Could you leave it behind the planter?”',
      turns: [
        ActivityTurn(
          text: '“Hello? Delivery for number twelve.”',
          speakerNumber: 1,
        ),
        // No emotion on the second turn deliberately. That is the honest common
        // case — a conversation reprocessed before the field existed, or one
        // whose audio gave the analyzer nothing to commit to — and the golden
        // should show a partly attributed row rather than a tidy one.
        ActivityTurn(
          text: '“Could you leave it behind the planter?”',
          speakerNumber: 2,
        ),
      ],
      icon: ActivityIcon.speech,
      isSpeech: true,
    ),
    ActivityItem(
      id: 'a3',
      kind: TelemetryKind.conversationTranscript,
      cameraId: 'kitchen',
      cameraName: 'Kitchen',
      // Two voices in one row, so the heading counts them rather than naming
      // one — which of them said what is the bubbles' job, on the turns below.
      speaker: '2 speakers',
      at: _sampleAt(minutes: 1),
      timeLabel: '1 min ago',
      text: '“Did you feed the dog yet?” · “Twice, actually.”',
      turns: [
        ActivityTurn(text: '“Did you feed the dog yet?”', speakerNumber: 1),
        ActivityTurn(
          text: '“Twice, actually.”',
          speakerNumber: 2,
          emotion: ActivityEmotion.happy,
        ),
      ],
      icon: ActivityIcon.speech,
      isSpeech: true,
      isRecent: true,
    ),
    ActivityItem(
      id: 'a4',
      kind: TelemetryKind.scene,
      cameraId: 'driveway',
      cameraName: 'Driveway',
      at: _sampleClock(16, 12),
      timeLabel: '4:12 pm',
      text: "A silver car pulled in and someone got out of the driver's side.",
      icon: ActivityIcon.car,
    ),
    // An ordinary sound, beside the scene it happened during — the two are separate records that
    // the client correlates by time, not one enriched with the other.
    ActivityItem(
      id: 'a4b',
      kind: TelemetryKind.sound,
      cameraId: 'driveway',
      cameraName: 'Driveway',
      at: _sampleClock(16, 11),
      timeLabel: '4:11 pm',
      text: 'Vehicle horn heard',
      icon: ActivityIcon.car,
      // The whole AudioSet phrase, comma and all — the row reads the first
      // synonym, the filter reads the class.
      label: 'Vehicle horn, car horn, honking',
    ),
    ActivityItem(
      id: 'a5',
      kind: TelemetryKind.scene,
      cameraId: 'back-yard',
      cameraName: 'Back yard',
      at: _sampleClock(15, 58),
      timeLabel: '3:58 pm',
      text: 'A cat crossed the lawn from left to right.',
      icon: ActivityIcon.cat,
    ),
    ActivityItem(
      id: 'a6',
      kind: TelemetryKind.scene,
      cameraId: 'side-path',
      cameraName: 'Side path',
      at: _sampleClock(15, 44),
      timeLabel: '3:44 pm',
      text: 'Lost connection. Serval will keep trying.',
      icon: ActivityIcon.connectionLost,
    ),
    ActivityItem(
      id: 'a7',
      kind: TelemetryKind.scene,
      cameraId: 'garage',
      cameraName: 'Garage',
      at: _sampleClock(18, 40, daysAgo: 1),
      timeLabel: '6:40 pm yesterday',
      text: 'The door closed and nothing has moved since.',
      icon: ActivityIcon.garage,
    ),
  ];

  /// [range] is ignored for the same reason [asOf] is: these are composed rows anchored to the
  /// design's own capture rather than a document feed, so scoping them to a window would empty the
  /// sample column for most of the day and leave the design demonstrating nothing.
  ///
  /// [includeAllDetections] is ignored because every composed row here is already an alert, so
  /// widening the overlay has nothing further to admit.
  @override
  List<ActivityItem> activityFor({
    String? cameraId,
    DateTime? asOf,
    TimelineRange? range,
    bool includeAllDetections = false,
  }) => cameraId == null
      ? _activity
      : _activity.where((i) => i.cameraId == cameraId).toList();

  /// Never truncated: these rows are the whole of what this repository has, so there is no read
  /// behind them that could have stopped short.
  @override
  DateTime? feedHorizon({String? cameraId}) => null;

  @override
  SceneSummary? summaryFor(
    String cameraId, {
    DateTime? asOf,
    TimelineRange? range,
  }) {
    if (cameraId != 'front-door') return null;
    return const SceneSummary(
      text:
          'A courier in a dark jacket arrived on foot, rang once and is '
          'waiting with a small parcel. No vehicle in shot.',
    );
  }

  /// Two people, because one episode carrying several boxes is the case the real
  /// pipeline produces and the one worth being able to look at. Different scores,
  /// because each box carries its own and the second is the half-seen one.
  ///
  /// [includeAllDetections] changes nothing here: both boxes are alerts, so the
  /// goldens show the same picture either way and the control beside *Subtitles*
  /// is what the flag is worth pinning. What the accent colour looks like is
  /// asserted in `detection_overlay_test.dart` rather than eyeballed.
  @override
  List<DetectionBox> detectionsFor(
    String cameraId, {
    bool includeAllDetections = false,
  }) {
    if (cameraId != 'front-door') return const [];
    return const [
      DetectionBox(
        label: 'person',
        confidence: 0.94,
        rect: Rect.fromLTWH(0.33, 0.24, 0.20, 0.48),
      ),
      DetectionBox(
        label: 'person',
        confidence: 0.71,
        rect: Rect.fromLTWH(0.63, 0.30, 0.16, 0.40),
      ),
    ];
  }

  /// The same boxes the live view gets, drifting with the playhead so the design
  /// build shows boxes that move rather than ones pinned to the mock. They drift
  /// at different rates, which is what makes two of them worth drawing.
  @override
  List<DetectionBox> detectionsAt(
    String cameraId,
    DateTime when, {
    bool includeAllDetections = false,
  }) {
    if (cameraId != 'front-door') return const [];
    final drift = 0.002 * (when.second % 30);
    return [
      DetectionBox(
        label: 'person',
        confidence: 0.94,
        rect: Rect.fromLTWH(0.33 + drift, 0.24, 0.20, 0.48),
      ),
      DetectionBox(
        label: 'person',
        confidence: 0.71,
        rect: Rect.fromLTWH(0.63 - (drift / 2), 0.30, 0.16, 0.40),
      ),
    ];
  }

  /// A no-op: the sample data is already all in memory, so there is nothing to load.
  @override
  Future<void> ensureReplayDetections(
    String cameraId,
    DateTime from,
    DateTime to,
  ) async {}

  @override
  String? liveCaptionFor(String cameraId) => cameraId == 'front-door'
      ? "“Hi — I've got a package for you. I'll leave it by the step.”"
      : null;

  /// The design's scrubber, as times rather than as fractions.
  ///
  /// The seven marks sit where the mock puts them, and their durations are what the mock's tick
  /// widths implied. Those durations are read two different ways on the track: the band sizes a
  /// mark as a fixed tick, while the two alerts are drawn across the time they cover, so at the
  /// hour range their few seconds are near the 3 px floor and they are narrower here than the mock
  /// draws them. The coverage carries a deliberate hole just past three-quarters, which is the only
  /// way the golden shows what a gap looks like — against a healthy camera it would be a solid bar
  /// and a break would never be reviewed.
  @override
  TimelineWindow timelineFor(
    String cameraId,
    TimelineRange range, {
    bool includeAllDetections = false,
  }) {
    // `capturedAt` rather than the clock, so the golden is stable — and a chosen period is
    // honoured, so the sample harness can show one without a Server.
    //
    // A range pinned on the real clock — what a row in the feed opens, see [TimelineRange.since] —
    // starts *after* this frozen one, and a window with its edges the wrong way round draws
    // nothing at all. Its width is the part that still means something here, so that is what the
    // fixture keeps.
    final to = range.endAt(capturedAt);
    final pinned = range.startAt(capturedAt);
    final from = pinned.isBefore(to) ? pinned : to.subtract(range.duration);
    final span = to.difference(from);
    DateTime at(double position) => from.add(span * position);

    return TimelineWindow(
      from: from,
      to: to,
      coverage: [CoverageSpan(from, at(0.71)), CoverageSpan(at(0.78), to)],
      // All six of the track's readings, so the design harness and the goldens show every hue and
      // the two of them that are hardest to tell apart sit near each other.
      marks: [
        TimelineMark(at: at(0.06), of: ActivityKind.scenes),
        TimelineMark(
          at: at(0.19),
          ran: const Duration(seconds: 3),
          of: ActivityKind.speech,
        ),
        TimelineMark(
          at: at(0.34),
          ran: const Duration(seconds: 1),
          of: ActivityKind.sounds,
        ),
        TimelineMark(
          at: at(0.52),
          ran: const Duration(seconds: 6),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
        TimelineMark(at: at(0.67), of: ActivityKind.objects),
        TimelineMark(
          at: at(0.81),
          ran: const Duration(seconds: 2),
          of: ActivityKind.speech,
        ),
        TimelineMark(
          at: at(0.94),
          ran: const Duration(seconds: 8),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.sounds,
        ),
      ],
    );
  }

  /// Null — there is no Server to play a recording from, so the stage paints the design's
  /// placeholder instead of building a player. This is what keeps `flutter test` and the goldens
  /// off libmpv, the same job [canStreamLive] does for the WebRTC plugin.
  @override
  Uri? vodUrlFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) => null;

  /// Zero, for the same reason [vodUrlFor] is null: no Server, and so no playlist to be offset
  /// from.
  @override
  Future<Duration> vodStartOffsetFor(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async => Duration.zero;

  /// Null, for the same reason [vodUrlFor] is: no Server, nothing to scope access to.
  @override
  Future<String?> mintStreamToken() async => null;

  /// Null, for the same reason [vodUrlFor] is: no Server, so no socket. The meter renders its
  /// "no live level" state, which keeps the goldens stable and the layout from jumping when a
  /// real Server does appear.
  @override
  AudioLevelFeed? watchAudioLevels(String cameraId) => null;

  /// A shared notifier parked at unity, which is [unityTravel] on the control rather than the top of
  /// it. `static` because this class has a `const` constructor — the same reason [_noFrames] is — so
  /// it cannot carry instance state.
  static final _volume = ValueNotifier<double>(unityTravel);

  /// The same notifier for every camera, so the goldens draw one knob position rather than depending
  /// on which camera a screen happened to open.
  @override
  ValueListenable<double> playbackVolumeFor(String cameraId) => _volume;

  /// A no-op: there is nothing here to be loud, and the goldens must not depend on a stored
  /// preference. Matches [saveWallLayout].
  @override
  void setPlaybackVolume(String cameraId, double travel) {}

  /// Expanded to begin with, which is the state the design draws and so the one the goldens
  /// capture. `static` for the same reason [_volume] is.
  static final _activityCollapsed = ValueNotifier<bool>(false);

  @override
  ValueListenable<bool> get activityPanelCollapsed => _activityCollapsed;

  /// Honoured rather than no-opped, unlike [setPlaybackVolume]: a chevron that does nothing when
  /// clicked is a broken control, and there is no audio here for the volume to be wrong about.
  /// Only the *write* is missing — nothing is persisted, so a restart is expanded again.
  ///
  /// Being `static`, this outlives an instance: a test that collapses the panel has to put it
  /// back, or the next test in the file inherits it.
  @override
  void setActivityPanelCollapsed(bool collapsed) =>
      _activityCollapsed.value = collapsed;
}

/// Instants for the design's own feed rows, so each carries the time its label already claims.
///
/// The mock's capture is stamped 4:18 pm and its labels are read against that. These are anchored
/// on today instead, so the column still splits into *Right now* and *Earlier today* wherever the
/// sample is opened — and so *Open camera* has something to seek to.
DateTime _sampleAt({int minutes = 0}) =>
    DateTime.now().subtract(Duration(minutes: minutes));

DateTime _sampleClock(int hour, int minute, {int daysAgo = 0}) {
  final today = DateTime.now();
  return DateTime(today.year, today.month, today.day - daysAgo, hour, minute);
}
