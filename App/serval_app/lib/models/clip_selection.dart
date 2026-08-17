import 'timeline.dart';

/// One recorded segment, as `GET /api/cameras/{id}/recordings` reports it.
///
/// The trim UI works in these rather than in seconds, because a segment is the smallest thing the
/// Server can copy without re-encoding. A handle between two of them is a handle promising a
/// precision the export cannot deliver.
class RecordedSegment {
  const RecordedSegment({
    required this.from,
    required this.duration,
    required this.initFileName,
  });

  factory RecordedSegment.fromJson(Map<String, dynamic> json) =>
      RecordedSegment(
        from: DateTime.parse(json['startedAt'] as String).toLocal(),
        duration: Duration(
          milliseconds: (((json['durationSeconds'] as num?) ?? 0) * 1000)
              .round(),
        ),
        initFileName: json['initFileName'] as String? ?? '',
      );

  final DateTime from;
  final Duration duration;

  /// Which recording session wrote it. Segments that do not share one cannot go in a single clip:
  /// an fMP4 segment is undecodable without the init it was written with.
  final String initFileName;

  DateTime get to => from.add(duration);

  bool contains(DateTime at) => !at.isBefore(from) && at.isBefore(to);
}

/// Which end of the range the user is working on.
///
/// Load-bearing on a phone, where a finger cannot land on a handle: the lit field is the end that
/// drags and the end the nudges move, so precision does not depend on a fingertip.
enum ClipEnd { start, end }

/// The state of the trimmer: a range, which end is live, and the segments it must land on.
///
/// Pure and immutable — every gesture produces a new one — so the arithmetic that decides what gets
/// saved is testable without a widget. Which matters more here than usual: getting it wrong saves
/// the wrong minute rather than failing visibly.
class ClipSelection {
  const ClipSelection({
    required this.from,
    required this.to,
    required this.segments,
    this.active = ClipEnd.end,
  });

  /// Opens a selection around [anchor], snapped to segments.
  ///
  /// Opens *selected* rather than empty, and symmetric, because the common case is "that, what just
  /// happened" — which is already the right answer when the mode opens. Returns null when the
  /// anchor is not inside any recorded segment: there is nothing there to trim.
  static ClipSelection? around(
    DateTime anchor, {
    required List<RecordedSegment> segments,
    Duration before = const Duration(seconds: 30),
    Duration after = const Duration(seconds: 30),
    Duration max = const Duration(minutes: 30),
  }) {
    final session = _sessionAt(anchor, segments);
    if (session.isEmpty) return null;

    // Held inside the session before the window is measured, so an anchor past the live edge
    // yields the last minute that was recorded rather than a range that collapses to nothing.
    final held = anchor.isAfter(session.last.to) ? session.last.to : anchor;

    final from = _snapDown(held.subtract(before), session);
    final to = _snapUp(held.add(after), session);
    if (from == null || to == null || !to.isAfter(from)) return null;

    return ClipSelection(
      from: from,
      to: to,
      segments: session,
    )._capped(max, moving: ClipEnd.end);
  }

  final DateTime from;
  final DateTime to;

  /// The segments the handles may land on — all from one recording session.
  final List<RecordedSegment> segments;

  final ClipEnd active;

  Duration get span => to.difference(from);

  /// The instant the live end is at, which is the frame the picture should be showing.
  DateTime get activeAt => active == ClipEnd.start ? from : to;

  /// How much one nudge moves an end: one segment.
  ///
  /// Read from the segments themselves rather than from the configured segment length, because
  /// under `-c:v copy` a segment is as long as the camera's GOP made it, not as long as the setting
  /// asked for. The label the screen shows is rendered from this, so it never promises a second the
  /// export cannot deliver.
  Duration get nudge {
    if (segments.isEmpty) return const Duration(seconds: 4);

    final middle = segments[segments.length ~/ 2].duration;
    return middle > Duration.zero ? middle : const Duration(seconds: 4);
  }

  ClipSelection withActive(ClipEnd end) =>
      ClipSelection(from: from, to: to, segments: segments, active: end);

  /// Moves one end to [at], snapped to a segment boundary.
  ///
  /// The ends cannot cross or meet: a zero-length clip is not a thing anyone wants and the Server
  /// refuses it, so the trimmer keeps at least one segment between them rather than letting a drag
  /// produce something that will be rejected on save.
  ClipSelection moveEnd(
    ClipEnd end,
    DateTime at, {
    Duration max = const Duration(minutes: 30),
  }) {
    if (segments.isEmpty) return this;

    if (end == ClipEnd.start) {
      final snapped = _snapDown(at, segments) ?? segments.first.from;
      final limited = snapped.isBefore(to) ? snapped : _stepFrom(to, -1);
      return ClipSelection(
        from: limited,
        to: to,
        segments: segments,
        active: end,
      )._capped(max, moving: ClipEnd.start);
    }

    final snapped = _snapUp(at, segments) ?? segments.last.to;
    final limited = snapped.isAfter(from) ? snapped : _stepFrom(from, 1);
    return ClipSelection(
      from: from,
      to: limited,
      segments: segments,
      active: end,
    )._capped(max, moving: ClipEnd.end);
  }

  /// Moves the live end by [steps] segments. Negative goes earlier.
  ClipSelection nudgeBy(
    int steps, {
    Duration max = const Duration(minutes: 30),
  }) {
    final at = _stepFrom(activeAt, steps);
    return moveEnd(active, at, max: max);
  }

  /// Snaps the range to [mark]'s own span — 12c's *Whole event*.
  ///
  /// What Serval saw, rather than what the scrubber happened to be showing: the courier arriving to
  /// the courier leaving. Clamped to the session and the cap like any other move.
  ClipSelection snapTo(
    DateTime from,
    DateTime to, {
    Duration max = const Duration(minutes: 30),
  }) {
    if (segments.isEmpty) return this;

    final start = _snapDown(from, segments) ?? segments.first.from;
    var end = _snapUp(to, segments) ?? segments.last.to;
    if (!end.isAfter(start)) end = _stepFrom(start, 1);

    return ClipSelection(
      from: start,
      to: end,
      segments: segments,
      active: ClipEnd.end,
    )._capped(max, moving: ClipEnd.end);
  }

  /// Whether the whole selection lies in one recording session.
  ///
  /// Should always be true — [segments] is one session by construction — but the Server refuses a
  /// range that is not, so the screen checks rather than trusts.
  bool get isOneSession {
    if (segments.isEmpty) return false;

    final init = segments.first.initFileName;
    return segments.every((s) => s.initFileName == init);
  }

  /// Trims whichever end just moved until the range fits, so the cap can never be exceeded by a
  /// drag. The end being held keeps its position; the other one gives way.
  ClipSelection _capped(Duration max, {required ClipEnd moving}) {
    if (span <= max) return this;

    if (moving == ClipEnd.end) {
      return ClipSelection(
        from: _snapDown(to.subtract(max), segments) ?? segments.first.from,
        to: to,
        segments: segments,
        active: active,
      );
    }

    return ClipSelection(
      from: from,
      to: _snapUp(from.add(max), segments) ?? segments.last.to,
      segments: segments,
      active: active,
    );
  }

  /// [steps] segment boundaries away from [at], staying inside the session.
  DateTime _stepFrom(DateTime at, int steps) {
    final boundaries = _boundaries(segments);
    if (boundaries.isEmpty) return at;

    var index = boundaries.indexWhere((b) => !b.isBefore(at));
    if (index < 0) index = boundaries.length - 1;

    final moved = (index + steps).clamp(0, boundaries.length - 1);
    return boundaries[moved];
  }

  /// The one recording session containing [at] — the most that can go in a single clip.
  ///
  /// Falls back to the newest session when [at] is past the end of everything recorded, which is
  /// the *live* case rather than an edge one: ffmpeg publishes a segment only once it is complete,
  /// so the newest one always ends a few seconds in the past while the clock does not. Requiring
  /// containment there meant pressing *Save clip* on a live camera answered "nothing was recorded
  /// here" every single time, on a camera that was recording.
  ///
  /// Unbounded rather than tolerant of a few seconds: a camera that has been down for an hour puts
  /// the live edge an hour back, and clipping the last thing it did record is what was asked for.
  /// Nothing is hidden by this — the trimmer opens showing the times it has chosen.
  static List<RecordedSegment> _sessionAt(
    DateTime at,
    List<RecordedSegment> segments,
  ) {
    if (segments.isEmpty) return const [];

    final holding =
        segments.where((s) => s.contains(at)).firstOrNull ??
        segments.where((s) => !s.to.isAfter(at)).lastOrNull;

    if (holding == null) return const [];

    return [...segments.where((s) => s.initFileName == holding.initFileName)]
      ..sort((a, b) => a.from.compareTo(b.from));
  }

  /// Every instant a handle may occupy: each segment's start, plus the end of the last one.
  static List<DateTime> _boundaries(List<RecordedSegment> segments) => [
    for (final segment in segments) segment.from,
    if (segments.isNotEmpty) segments.last.to,
  ];

  /// The latest boundary at or before [at] — where a start handle lands.
  ///
  /// Down rather than to the nearest, so a start never moves *past* the moment asked for. Losing a
  /// second of lead-in is invisible; losing the first second of the thing being kept is the whole
  /// point of the clip.
  static DateTime? _snapDown(DateTime at, List<RecordedSegment> segments) {
    DateTime? best;
    for (final boundary in _boundaries(segments)) {
      if (!boundary.isAfter(at)) best = boundary;
    }
    return best ?? _boundaries(segments).firstOrNull;
  }

  /// The earliest boundary at or after [at] — where an end handle lands. Up, for the same reason.
  static DateTime? _snapUp(DateTime at, List<RecordedSegment> segments) {
    for (final boundary in _boundaries(segments)) {
      if (!boundary.isBefore(at)) return boundary;
    }
    return _boundaries(segments).lastOrNull;
  }
}

/// The twelve minutes the trim track shows, and the hour it steps out to.
///
/// The zoom is what makes trimming possible at all: on a twelve-hour track a pixel is about
/// thirty-five seconds, so a fifty-five second clip is two pixels and nobody can trim it. Twelve
/// minutes puts ticks on real minutes and gives a handle somewhere to go.
///
/// Two steps rather than one because a clip may run to half an hour, which does not fit the near
/// view — and the far view is still fine enough that a handle is draggable, with the segment nudges
/// doing the work a fingertip cannot.
class TrimZoom {
  const TrimZoom(this.span);

  static const near = TrimZoom(Duration(minutes: 12));
  static const far = TrimZoom(Duration(hours: 1));

  final Duration span;

  bool get isNear => span == near.span;

  /// The step that holds [selection] with room to work either side of it.
  static TrimZoom forSpan(Duration selection) =>
      selection * 1.5 > near.span ? far : near;

  /// The window to draw, centred on the selection but held inside what was recorded.
  CoverageSpan windowFor(
    DateTime from,
    DateTime to, {
    DateTime? earliest,
    DateTime? latest,
  }) {
    final centre = from.add(to.difference(from) ~/ 2);
    var start = centre.subtract(span ~/ 2);
    var end = start.add(span);

    if (earliest != null && start.isBefore(earliest)) {
      start = earliest;
      end = start.add(span);
    }

    if (latest != null && end.isAfter(latest)) {
      end = latest;
      start = end.subtract(span);
      if (earliest != null && start.isBefore(earliest)) start = earliest;
    }

    return CoverageSpan(start, end);
  }
}
