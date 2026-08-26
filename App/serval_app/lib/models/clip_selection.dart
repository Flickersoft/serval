import 'timeline.dart';

/// The clip cap to work with until the Server has said what its own is.
///
/// A fallback, not the limit: the real figures are `Serval:Media:ClipMaxMinutes` and
/// `Serval:Media:ExportMaxMinutes`, and the trimmer is handed whichever applies. This is what the
/// arithmetic uses before that arrives or when the settings could not be read, and it is
/// deliberately the smaller of the two — offering a range the Server will refuse reads as a bug in
/// the trimmer, while offering less than it would allow reads as nothing at all.
const kClipMaxFallback = Duration(minutes: 30);

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
    this.coverage = const [],
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
    List<CoverageSpan> coverage = const [],
    Duration before = const Duration(seconds: 30),
    Duration after = const Duration(seconds: 30),
    Duration max = kClipMaxFallback,
  }) {
    final session = _footageAt(anchor, segments);
    if (session.isEmpty) return null;

    // Held inside the session before the window is measured, so an anchor past the live edge
    // yields the last minute that was recorded rather than a range that collapses to nothing.
    final held = anchor.isAfter(session.last.to) ? session.last.to : anchor;

    var from = _snapDown(held.subtract(before), session);
    var to = _snapUp(held.add(after), session);

    // Held inside what was actually recorded, where that is known.
    //
    // Snapping stopped being a fence so that a drag could reach past the segments that happened to
    // have been read — but opening the trimmer is the one moment a fence is right. A range that
    // begins before the camera did is not a range anybody asked for, and it would report a duration
    // longer than the file it produces. Coverage is the honest bound here: it says what exists,
    // where segments only say what was fetched.
    if (coverage.isNotEmpty) {
      if (from.isBefore(coverage.first.from)) from = coverage.first.from;
      if (to.isAfter(coverage.last.to)) to = coverage.last.to;
    }

    if (!to.isAfter(from)) return null;

    return ClipSelection(
      from: from,
      to: to,
      segments: session,
      coverage: coverage,
    )._capped(max, moving: ClipEnd.end);
  }

  final DateTime from;
  final DateTime to;

  /// The segments the handles may land on — all from one recording session.
  final List<RecordedSegment> segments;

  /// Where footage exists across the whole trimmable range, merged into one span per recording
  /// session.
  ///
  /// Separate from [segments] because the two answer different questions at different costs. This
  /// says *what could be exported* and is one to three rows for a day; segments say *where a handle
  /// snaps* and are one row per four seconds, so they are only read near the handles. Drawing the
  /// track from segments is what made a long range unreachable.
  final List<CoverageSpan> coverage;

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
      ClipSelection(
        from: from,
        to: to,
        segments: segments,
        coverage: coverage,
        active: end,
      );

  /// Moves one end to [at], snapped to a segment boundary.
  ///
  /// The ends cannot cross or meet: a zero-length clip is not a thing anyone wants and the Server
  /// refuses it, so the trimmer keeps at least one segment between them rather than letting a drag
  /// produce something that will be rejected on save.
  ClipSelection moveEnd(
    ClipEnd end,
    DateTime at, {
    Duration max = kClipMaxFallback,
  }) {
    if (segments.isEmpty) return this;

    if (end == ClipEnd.start) {
      final snapped = _snapDown(at, segments);
      final limited = snapped.isBefore(to) ? snapped : _stepFrom(to, -1);
      return ClipSelection(
        from: limited,
        to: to,
        segments: segments,
        coverage: coverage,
        active: end,
      )._capped(max, moving: ClipEnd.start);
    }

    final snapped = _snapUp(at, segments);
    final limited = snapped.isAfter(from) ? snapped : _stepFrom(from, 1);
    return ClipSelection(
      from: from,
      to: limited,
      segments: segments,
      coverage: coverage,
      active: end,
    )._capped(max, moving: ClipEnd.end);
  }

  /// Moves the live end by [steps] segments. Negative goes earlier.
  ClipSelection nudgeBy(
    int steps, {
    Duration max = kClipMaxFallback,
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
    Duration max = kClipMaxFallback,
  }) {
    if (segments.isEmpty) return this;

    final start = _snapDown(from, segments);
    var end = _snapUp(to, segments);
    if (!end.isAfter(start)) end = _stepFrom(start, 1);

    return ClipSelection(
      from: start,
      to: end,
      segments: segments,
      coverage: coverage,
      active: ClipEnd.end,
    )._capped(max, moving: ClipEnd.end);
  }

  /// A fresh range dragged out from [anchor] to [at].
  ///
  /// The operation the trimmer was missing. Moving an end only ever adjusts the range you already
  /// have, and the range you already have is about a minute — which at a twelve-hour zoom is a
  /// selection one pixel wide with both its handles on the same pixel. There is nothing to grab and
  /// no way to say which of the two you meant, so widening the track made a long clip *less*
  /// reachable rather than more. Dragging one out from scratch is how every timeline does this, and
  /// it is the only gesture that scales to a range far longer than what is currently selected.
  ///
  /// Direction decides which end stays live, so the picture follows the end you are pulling and a
  /// backwards drag is as good as a forwards one.
  ClipSelection dragFrom(
    DateTime anchor,
    DateTime at, {
    Duration max = kClipMaxFallback,
  }) {
    if (segments.isEmpty) return this;

    final forward = !at.isBefore(anchor);
    final moving = forward ? ClipEnd.end : ClipEnd.start;

    final start = _snapDown(forward ? anchor : at, segments);
    var end = _snapUp(forward ? at : anchor, segments);
    if (!end.isAfter(start)) end = _stepFrom(start, 1);

    return ClipSelection(
      from: start,
      to: end,
      segments: segments,
      coverage: coverage,
      active: moving,
    )._capped(max, moving: moving);
  }

  /// How long the exported file will actually play for.
  ///
  /// The recorded seconds inside the range rather than [span], and the two differ whenever the
  /// camera was not recording for all of it — the Server closes those gaps rather than filling
  /// them, so a range that spans an outage produces a shorter file than its ends suggest.
  ///
  /// From [coverage] rather than from [segments], because coverage is read for the whole trimmable
  /// range while segments are only read near the handles. Counting segments meant this quietly
  /// under-reported the moment a range reached past what had been fetched.
  Duration get recorded {
    var total = Duration.zero;

    for (final span in coverage) {
      final start = span.from.isAfter(from) ? span.from : from;
      final end = span.to.isBefore(to) ? span.to : to;
      if (end.isAfter(start)) total += end.difference(start);
    }

    return total;
  }

  /// Trims whichever end just moved until the range fits, so the cap can never be exceeded by a
  /// drag. The end being held keeps its position; the other one gives way.
  ClipSelection _capped(Duration max, {required ClipEnd moving}) {
    if (span <= max) return this;

    if (moving == ClipEnd.end) {
      return ClipSelection(
        from: _snapDown(to.subtract(max), segments),
        to: to,
        segments: segments,
        coverage: coverage,
        active: active,
      );
    }

    return ClipSelection(
      from: from,
      to: _snapUp(from.add(max), segments),
      segments: segments,
      coverage: coverage,
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

  /// All the recorded footage a handle may land on, given that [at] is somewhere in it.
  ///
  /// This used to return one recording session and nothing else, because one session was the most
  /// that could go in a single file. It is not any more — the Server joins the batches either side
  /// of a restart — and the filter had to go with it, or the trimmer would still stop dead at every
  /// reconnect while the export was perfectly capable of crossing one.
  ///
  /// What it still does is answer "is there any footage here at all", and it falls back to the last
  /// thing recorded when [at] is past the end of everything, which is the *live* case rather than an
  /// edge one: ffmpeg publishes a segment only once it is complete, so the newest one always ends a
  /// few seconds in the past while the clock does not. Requiring containment there meant pressing
  /// *Save clip* on a live camera answered "nothing was recorded here" every single time, on a
  /// camera that was recording.
  ///
  /// Unbounded rather than tolerant of a few seconds: a camera that has been down for an hour puts
  /// the live edge an hour back, and clipping the last thing it did record is what was asked for.
  /// Nothing is hidden by this — the trimmer opens showing the times it has chosen.
  static List<RecordedSegment> _footageAt(
    DateTime at,
    List<RecordedSegment> segments,
  ) {
    if (segments.isEmpty) return const [];

    final holding =
        segments.where((s) => s.contains(at)).firstOrNull ??
        segments.where((s) => !s.to.isAfter(at)).lastOrNull;

    if (holding == null) return const [];

    return [...segments]..sort((a, b) => a.from.compareTo(b.from));
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
  ///
  /// Falls back to [at] itself outside the segments that were read, and that fallback is the whole
  /// reason a long range is reachable. Snapping exists so a handle does not promise precision the
  /// export cannot deliver — the Server includes whole segments either way. It was never meant to
  /// be a fence, and returning the nearest loaded boundary instead made it one: a drag stopped dead
  /// at the edge of whatever the index happened to have been read for.
  static DateTime _snapDown(DateTime at, List<RecordedSegment> segments) {
    DateTime? best;
    for (final boundary in _boundaries(segments)) {
      if (!boundary.isAfter(at)) best = boundary;
    }
    return best ?? at;
  }

  /// The earliest boundary at or after [at] — where an end handle lands. Up, for the same reason,
  /// and falling back to [at] outside the loaded segments for the same reason again.
  static DateTime _snapUp(DateTime at, List<RecordedSegment> segments) {
    for (final boundary in _boundaries(segments)) {
      if (!boundary.isBefore(at)) return boundary;
    }
    return at;
  }
}

/// The width of time the trim track shows, and the steps it widens through.
///
/// The zoom is what makes trimming possible at all: on a twelve-hour track a pixel is about
/// thirty-five seconds, so a fifty-five second clip is two pixels and nobody can trim it. Twelve
/// minutes puts ticks on real minutes and gives a handle somewhere to go.
///
/// A ladder rather than one width, because a clip may run to twelve hours and that does not fit the
/// near view at all — and a handle can only be dragged inside the window the track draws, so the
/// widest step is the real ceiling on how long a clip can be. The wider steps stay coarse on
/// purpose, with the segment nudges doing the work a fingertip cannot.
class TrimZoom {
  const TrimZoom(this.span);

  static const near = TrimZoom(Duration(minutes: 12));
  static const far = TrimZoom(Duration(hours: 1));

  /// Every step the track can be drawn at, narrowest first.
  ///
  /// This used to stop at an hour, and the ceiling was invisible but absolute: a handle can only be
  /// dragged inside the window the track draws, so an hour-wide widest step meant an hour-long clip
  /// however high `Media:ExportMaxMinutes` was set. The ladder has to reach the cap or the cap is
  /// decoration.
  ///
  /// The steps get coarser as they widen because precision stops being the point: at twelve hours a
  /// pixel is about thirty seconds and nobody trims by eye, which is what the segment nudges are
  /// for. Choosing the range is the job at that width; placing it to the second is the job at
  /// twelve minutes.
  static const steps = <Duration>[
    Duration(minutes: 12),
    Duration(hours: 1),
    Duration(hours: 3),
    Duration(hours: 6),
    Duration(hours: 12),
  ];

  final Duration span;

  bool get isNear => span == near.span;

  /// Whether this is as wide as the track goes.
  bool get isWidest => span >= steps.last;

  /// The step that holds [selection] with room to work either side of it.
  static TrimZoom forSpan(Duration selection) {
    for (final step in steps) {
      if (selection * 1.5 <= step) return TrimZoom(step);
    }
    return TrimZoom(steps.last);
  }

  /// One step out, or this one if there is nowhere further to go.
  TrimZoom get wider {
    for (final step in steps) {
      if (step > span) return TrimZoom(step);
    }
    return this;
  }

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

  /// [current] shifted only as far as it must to keep [at] on the track.
  ///
  /// The alternative — recomputing the window from the selection every time it changes — moves
  /// *both* ends on screen whenever either one is dragged: the window centres on the range, so
  /// grabbing the end handle shifts the centre by half of what you moved and the start handle
  /// slides the other way under a finger that never touched it. It reads as the trimmer moving a
  /// handle you did not grab.
  ///
  /// A tenth of the track is kept either side, so the handle being dragged never sits against the
  /// very edge with nothing drawn beyond it — you can see where you are going before you get there.
  CoverageSpan windowKeeping(CoverageSpan current, DateTime at) {
    final margin = span * 0.1;

    if (at.isBefore(current.from.add(margin))) {
      final start = at.subtract(margin);
      return CoverageSpan(start, start.add(span));
    }

    if (at.isAfter(current.to.subtract(margin))) {
      final end = at.add(margin);
      return CoverageSpan(end.subtract(span), end);
    }

    return current;
  }
}
