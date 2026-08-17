import 'package:flutter/painting.dart';

/// What is known about reaching a camera right now.
///
/// Three states rather than a bool, and [connecting] is the one that earns the
/// enum: the Server publishes no status field, so everything here is derived
/// from whether that camera's frames are still arriving — and silence has two
/// completely different causes. A camera that has stopped sending is down. A
/// wall that has just started listening has heard nothing from *any* camera
/// yet, and saying six cameras are down on the strength of one socket that has
/// not finished opening is a claim nothing supports.
///
/// So [offline] is a failure state and nothing weaker: we have been listening
/// long enough that silence is the camera's own.
enum CameraConnection {
  /// Frames are arriving.
  online,

  /// Nothing has arrived yet, and not enough time has passed to call that a
  /// fault — a cold start, a reconnect, or the App coming back from the
  /// background.
  connecting,

  /// We have been listening and heard nothing. This one really is down.
  offline,
}

/// A camera, shaped after the Server's `Camera` entity so the wiring pass is a
/// swap rather than a rewrite.
///
/// The Server serializes that entity in camelCase — `id`, `twoWayAudio`,
/// `ptzConfigured` and so on. Telemetry documents use snake_case instead, so
/// resist giving these models one shared naming strategy.
class Camera {
  const Camera({
    required this.id,
    required this.name,
    this.enabled = true,
    this.twoWayAudio = false,
    this.recordAudio = false,
    this.playbackGainDb = 0,
    this.playbackGateRms,
    this.aiVision = false,
    this.aiAudio = false,
    this.ptzConfigured = false,
    this.connection = CameraConnection.connecting,
    this.isRecording = false,
    this.records = true,
    this.hasAudioActivity = false,
    this.needsAttention = false,
    this.resolutionLabel,
    required this.placeholder,
  });

  /// Client-supplied on the Server, constrained to `[A-Za-z0-9_-]+` because it
  /// doubles as a directory name and a URL segment.
  final String id;
  final String name;

  /// Registered but not ingested when false.
  final bool enabled;

  /// The talk-back opt-in. go2rtc only probes the camera's audio backchannel
  /// when this is set, so it gates the *Hold to talk* control.
  final bool twoWayAudio;
  final bool recordAudio;

  /// How far to lift this camera's audio when listening to it, in dB. 0 plays it
  /// as recorded.
  ///
  /// On the camera rather than on this machine because it describes the
  /// microphone, not the listener: cameras on one server can differ by 20 dB or
  /// more in how loudly they record the same room, so one number for all of them
  /// is wrong for at least one. The listening level — the volume slider's 0..1 —
  /// stays local, and the two multiply.
  final double playbackGainDb;

  /// The level below which this camera's audio is silence during playback, as the
  /// RMS of a 16 kHz window; null gates nothing.
  ///
  /// The companion to [playbackGainDb], and the reason a large gain is usable: a
  /// camera stream spends most of its life on its noise floor, so a gain big
  /// enough to make the quiet content audible also lifts the codec's own
  /// quantisation noise into hiss. Gating ahead of the gain keeps silence silent.
  ///
  /// Unrelated to the identically-shaped thresholds under `audioTuning` despite
  /// sharing their unit: this one feeds the speakers, those feed the detector.
  final double? playbackGateRms;

  final bool aiVision;
  final bool aiAudio;

  /// Computed Server-side from the presence of an ONVIF endpoint — so it says
  /// only that there is something to *ask*, not what the camera can do.
  ///
  /// It does not gate the pad: a fixed bullet camera with an ONVIF endpoint
  /// answers true here and has no pan, tilt or zoom at all. What the controls
  /// are drawn from is `ServalRepository.ptzProbeFor`, which asks the camera.
  final bool ptzConfigured;

  /// Derived from snapshot staleness on `WS /api/dashboard` rather than from a
  /// status field, because the Server exposes none — see [CameraConnection] for
  /// why silence needs three answers rather than two.
  ///
  /// [CameraConnection.online] is also the health signal: a frame arriving says
  /// this camera's pipeline is running, and says nothing about what that
  /// pipeline does with the frames.
  ///
  /// Defaults to [CameraConnection.connecting], which is what a camera nobody
  /// has heard from yet actually is.
  final CameraConnection connection;

  /// Exact rather than inferred, even though no per-camera recording state is
  /// published: one ffmpeg per camera produces the snapshots *and* the segments
  /// from a single connection, so a fresh frame is evidence the writer is up.
  ///
  /// [CameraConnection.online] and [records] together — the pipeline is alive
  /// *and* it was asked to write. False whenever [records] is: a camera set to
  /// keep nothing is never recording, however fresh its frames are.
  final bool isRecording;

  /// Whether this camera is *configured* to record, from its `record` stream
  /// role.
  ///
  /// Distinct from [isRecording], and the distinction is the point: false here
  /// means recording was never asked for, false there can also mean a recorder
  /// that has stopped. "Nothing is kept" and "the recorder fell over" want
  /// opposite reactions from whoever is reading the screen.
  final bool records;

  /// Derived from live `utterance` telemetry on `WS /api/events` — a camera
  /// that has heard speech in the last few seconds shows the waveform mark.
  final bool hasAudioActivity;

  /// An alert **sound** on `WS /api/events` in the last few seconds.
  ///
  /// Sounds only, which is narrower than the alerts the rest of the UI counts:
  /// a detection carries `is_alert` too and lights the activity card and the
  /// scrubber, but not the tile it happened on.
  final bool needsAttention;

  /// STUB: the camera model carries no resolution, and the snapshot cannot
  /// stand in — `Ingest:SnapshotMaxMegapixels` fits it to a pixel budget, so a
  /// decoded frame reports the thumbnail's size. It would have to join the DTO.
  final String? resolutionLabel;

  /// How this camera's tile paints until a real frame arrives.
  final TilePlaceholder placeholder;

  /// The centered label the design paints over a tile with no frame yet —
  /// `DRIVEWAY · 1080p`, `BACK YARD · NIGHT`.
  String get placeholderLabel {
    final upper = name.toUpperCase();
    return resolutionLabel == null ? upper : '$upper · $resolutionLabel';
  }
}

/// The diagonal-stripe fill a tile shows before its first frame.
///
/// The design gives every camera its own two-tone stripe and a colored bloom,
/// so a wall of placeholder tiles still reads as six distinct cameras. The
/// wiring pass replaces only the tile body, so this survives as the loading
/// and reconnecting state.
class TilePlaceholder {
  const TilePlaceholder({
    required this.stripeLight,
    required this.stripeDark,
    required this.bloom,
  });

  /// A stable placeholder for a real camera, picked from the design's own five.
  ///
  /// The design hand-picks a stripe and bloom per camera; a registry read gets a list of ids
  /// instead. Hashing the id preserves what the choice was actually for — that a wall of tiles
  /// with no frames yet still reads as several distinct cameras — while staying stable across
  /// runs, so a camera does not change colour when the Server reorders its list.
  factory TilePlaceholder.forCameraId(String cameraId) {
    // FNV-1a rather than String.hashCode: Dart's is seeded per isolate, so it would give a
    // camera a different colour on every launch.
    var hash = 0x811c9dc5;
    for (final unit in cameraId.codeUnits) {
      hash = ((hash ^ unit) * 0x01000193) & 0xFFFFFFFF;
    }
    return _palette[hash % _palette.length];
  }

  static const _palette = [
    TilePlaceholder(
      stripeLight: Color(0xFF1E2131),
      stripeDark: Color(0xFF181B28),
      bloom: Color(0x129184D9),
    ),
    TilePlaceholder(
      stripeLight: Color(0xFF22212C),
      stripeDark: Color(0xFF1B1A24),
      bloom: Color(0x17E0955F),
    ),
    TilePlaceholder(
      stripeLight: Color(0xFF1A2028),
      stripeDark: Color(0xFF161B22),
      bloom: Color(0x0FB4DCD2),
    ),
    TilePlaceholder(
      stripeLight: Color(0xFF20222E),
      stripeDark: Color(0xFF1A1C25),
      bloom: Color(0x0DE9E9ED),
    ),
    TilePlaceholder(
      stripeLight: Color(0xFF1E2131),
      stripeDark: Color(0xFF181B28),
      bloom: Color(0x0D9184D9),
    ),
  ];

  /// What a camera that has dropped out paints: flat, no stripes, no bloom, so an absent feed
  /// never reads as a dark one.
  static const offline = TilePlaceholder(
    stripeLight: Color(0xFF14161F),
    stripeDark: Color(0xFF14161F),
    bloom: Color(0x00000000),
  );

  final Color stripeLight;
  final Color stripeDark;

  /// The radial bloom laid over the stripes, centered a little above middle.
  final Color bloom;
}

/// Where a tile sits on the wall's grid, and how much of it the tile keeps.
///
/// The design's rule is that tiles keep their own size and shape — a driveway
/// worth watching stays 2x2 while a garage stays 1x1 — so position and span
/// are data, not layout code.
class TileLayout {
  const TileLayout({
    required this.cameraId,
    required this.column,
    required this.row,
    this.columnSpan = 1,
    this.rowSpan = 1,
  });

  final String cameraId;

  /// Zero-based grid position.
  final int column;
  final int row;
  final int columnSpan;
  final int rowSpan;

  TileLayout copyWith({int? column, int? row, int? columnSpan, int? rowSpan}) =>
      TileLayout(
        cameraId: cameraId,
        column: column ?? this.column,
        row: row ?? this.row,
        columnSpan: columnSpan ?? this.columnSpan,
        rowSpan: rowSpan ?? this.rowSpan,
      );

  /// By value, so `listEquals` can answer whether a drag actually changed
  /// anything and so a failing layout test prints the two arrangements rather
  /// than two instance hashes.
  @override
  bool operator ==(Object other) =>
      other is TileLayout &&
      other.cameraId == cameraId &&
      other.column == column &&
      other.row == row &&
      other.columnSpan == columnSpan &&
      other.rowSpan == rowSpan;

  @override
  int get hashCode => Object.hash(cameraId, column, row, columnSpan, rowSpan);

  @override
  String toString() =>
      'TileLayout($cameraId at $column,$row size ${columnSpan}x$rowSpan)';
}

/// A detection drawn over the video, as fractions of the frame.
///
/// Built from a `DetectionDocument`'s geometry — see `DetectionDocument.overlays`.
/// One episode yields as many of these as there were objects of its class, since
/// three people in shot is one episode and three boxes. Scene descriptions still
/// carry no geometry, and never will: they are prose about a moment, not a
/// measurement of one.
class DetectionBox {
  const DetectionBox({
    required this.label,
    required this.confidence,
    required this.rect,
    this.isAlert = true,
    this.isStale = false,
  });

  final String label;

  /// Whether the episode this came from was a claim that someone should look.
  ///
  /// Carried on the box because it is what picks its colour: an alert is drawn in `Serval.alert`,
  /// anything else in the Nocturne accent. Only a screen asking for the wide overlay produces the
  /// second kind, so it defaults to true — every box that reaches the screen without one being
  /// asked for is an alert.
  final bool isAlert;

  /// Whether this is the last position known rather than one measured at the instant on screen.
  ///
  /// True inside a gap: the episode covers this moment and the object was not seen in it. Drawn
  /// as a dotted outline instead of a solid one, which is what lets the overlay show where
  /// something was without claiming to have just seen it there — the alternative was a box that
  /// blinked out while the activity row still read "still there".
  ///
  /// Independent of [isAlert]. A dotted box keeps its colour, because how sure we are of a
  /// position and whether the thing in it matters are different questions.
  final bool isStale;

  /// How well *this* object was seen, not how well the episode's best was. Three
  /// people in one episode are three boxes with three scores; writing the
  /// episode's peak over each would be a measurement of one of them presented as
  /// a measurement of all three.
  final double confidence;

  /// Left/top/width/height as fractions of the frame, so the box tracks the
  /// video however it is scaled.
  final Rect rect;

  /// The label, and how well it was seen — but only while that is a reading of the frame you are
  /// looking at. A stale box's confidence was measured somewhere else in time and would argue
  /// against the dotted outline drawn around it, so it says the label alone.
  String get caption => isStale
      ? label.toUpperCase()
      : '${label.toUpperCase()} · ${confidence.toStringAsFixed(2)}';
}
