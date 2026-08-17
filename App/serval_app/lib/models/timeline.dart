import 'activity.dart';

/// Which slice of the past the scrubber shows.
///
/// A class rather than an enum because the three presets are not the whole story: [window] names
/// a specific period — last Tuesday between nine and eleven — which an enum cannot carry.
///
/// The distinction that matters everywhere else is [endingAt]. Null means *the window ends now*,
/// which is what the three presets are and what lets the track keep pace with the clock. A value
/// means the right edge is a fixed instant in the past, so the window no longer moves, nothing
/// needs re-fetching as time passes, and the header must stop saying "today".
class TimelineRange {
  const TimelineRange._({
    required this.key,
    required this.label,
    required this.duration,
    this.startingAt,
    this.endingAt,
  });

  /// A fixed period, `from` inclusive to `to` exclusive.
  ///
  /// Clamped to [maxSpan] rather than rejected: the Server validates no maximum on `/coverage`,
  /// `/recordings` or `/vod.m3u8`, so a fat-fingered month would be asked for verbatim.
  ///
  /// The right edge is what survives the clamp. A window is asked for because of something at one
  /// end of it, and it is the end you named last — the *To* — that a shortened window has to keep.
  factory TimelineRange.window({required DateTime from, required DateTime to}) {
    final span = to.difference(from);
    final clamped = span > maxSpan ? maxSpan : span;

    return TimelineRange._(
      // Carries the instants, so two different windows are two different cache entries.
      key: 'window/${from.toIso8601String()}/${to.toIso8601String()}',
      label: 'Custom',
      duration: clamped,
      endingAt: to,
    );
  }

  /// A window pinned at its left edge, whose right edge is the clock.
  ///
  /// The shape arriving from the feed needs, and the only one of the three that changes width: what
  /// happened five minutes ago has no thirty minutes after it to show yet, and a window drawn out
  /// to where thirty minutes *will* be is half a bar of empty future that never fills, because a
  /// window with a fixed right edge is never refetched. So this one starts as wide as there is
  /// footage for and grows with the clock until it reaches [span], after which it slides — an
  /// ordinary live window that happens to have been born somewhere.
  factory TimelineRange.since(
    DateTime start, {
    required Duration span,
  }) => TimelineRange._(
    // The start, so two arrivals are two cache entries. Stable across rebuilds, which is what
    // the repository's anchors and timelines are held under.
    key: 'since/${start.toIso8601String()}',
    label: 'Custom',
    duration: span,
    startingAt: start,
  );

  static const fiveMin = TimelineRange._(
    key: 'fiveMin',
    label: '5 min',
    duration: Duration(minutes: 5),
  );
  static const quarterHour = TimelineRange._(
    key: 'quarterHour',
    label: '15 min',
    duration: Duration(minutes: 15),
  );
  static const hour = TimelineRange._(
    key: 'hour',
    label: '1 h',
    duration: Duration(hours: 1),
  );
  static const sixHours = TimelineRange._(
    key: 'sixHours',
    label: '6 h',
    duration: Duration(hours: 6),
  );
  static const halfDay = TimelineRange._(
    key: 'halfDay',
    label: '12 h',
    duration: Duration(hours: 12),
  );
  static const day = TimelineRange._(
    key: 'day',
    label: '24 h',
    duration: Duration(hours: 24),
  );

  /// The fixed spans, in the order the range panel lists them — shortest first, ending at the
  /// ceiling [maxSpan] names.
  static const presets = [fiveMin, quarterHour, hour, sixHours, halfDay, day];

  /// The longest window that can be shown at once.
  ///
  /// A day of footage is already ~21,600 segment rows, and past that a wider window buys
  /// resolution nobody can see: a week across a 700 px track is about a pixel a minute, so an
  /// event and the ten minutes around it are the same mark. Asking for more holds here and says
  /// so — which is also the only guard there is, since the Server enforces no maximum of its own.
  static const maxSpan = Duration(hours: 24);

  /// Stable across rebuilds and unique per window — the repository caches timelines under it.
  final String key;

  /// What the range panel's span list shows. `Custom` for a [window], which the panel names by
  /// its day and the scrubber's header by its hours instead — see `rangeButtonLabel`.
  final String label;

  /// How wide the window is, or — for a [since] — the widest it is allowed to become.
  final Duration duration;

  /// The left edge while it is still pinned, or null for a window whose width alone decides it.
  final DateTime? startingAt;

  /// The right edge, or null when that edge is *now*.
  final DateTime? endingAt;

  /// Whether the window keeps pace with the clock.
  bool get live => endingAt == null;

  /// The right edge as of [now]. The presets slide; a [window] does not.
  DateTime endAt(DateTime now) => endingAt ?? now;

  /// The left edge as of [now] — [duration] back from the right one, except while a [since] is
  /// still narrower than that and holds at the instant it was opened on.
  DateTime startAt(DateTime now) {
    final slid = endAt(now).subtract(duration);
    final pinned = startingAt;

    return pinned == null || slid.isAfter(pinned) ? slid : pinned;
  }

  @override
  bool operator ==(Object other) => other is TimelineRange && other.key == key;

  @override
  int get hashCode => key.hashCode;

  @override
  String toString() => 'TimelineRange($key)';
}

/// Whether a mark on the scrubber is ordinary activity or something that
/// wanted your attention.
enum TimelineMarkKind { activity, alert }

/// One thing that happened, at the moment it happened.
///
/// Carries time, not geometry. The scrubber turns [at] into an x and [ran]
/// into a width — which is what lets a tap on the track become a `DateTime`
/// and therefore a seek. A mark that knew only where it painted could be
/// drawn, and nothing else.
class TimelineMark {
  const TimelineMark({
    required this.at,
    this.ran = Duration.zero,
    this.kind = TimelineMarkKind.activity,
    this.of = ActivityKind.scenes,
  });

  final DateTime at;

  /// How long the event ran — a scene's frame span, an utterance's duration.
  final Duration ran;

  final TimelineMarkKind kind;

  /// What happened, as opposed to [kind]'s whether it wanted you.
  ///
  /// The two are separate questions and the track needs both: severity decides
  /// how loudly a mark is drawn, category decides which hue it is drawn in, and
  /// an alert sound and an alert person are the same answer to the first
  /// question and different answers to the second.
  ///
  /// The same four readings the activity column filters by, so a band on the
  /// track and a facet in the column cannot mean different things by *Sounds*.
  final ActivityKind of;
}

/// A contiguous run of recorded footage.
///
/// One span from the Server's `GET /api/cameras/{id}/coverage`, which is one
/// ffmpeg session: the segment index merged, because unmerged it is one row
/// per four seconds and a day is tens of thousands of them.
class CoverageSpan {
  const CoverageSpan(this.from, this.to);

  final DateTime from;
  final DateTime to;

  Duration get duration => to.difference(from);

  bool contains(DateTime at) => !at.isBefore(from) && !at.isAfter(to);
}

/// How much track a row in the feed opens: the moment it names, and the half hour after it.
///
/// The window is the errand. Arriving because something happened, what you are about to do is
/// watch it and then watch what followed — so the moment belongs at the *left*, with the next half
/// hour laid out to the right of it to play through. Widening to fit the instant instead put it
/// against the right-hand edge of an hour, six hours or a day of track, where the thing you came
/// for is a few pixels wide and everything drawn beside it is the time before it, which you have
/// already decided you do not want.
const arrivalSpan = Duration(minutes: 30);

/// How much of [arrivalSpan] sits before the moment.
///
/// Not zero, for two reasons that are both about the playhead. On the edge exactly, its knob is
/// half outside the track and clipped; and a seek lands on the nearest *footage*, which across a
/// break in the recording can be earlier than the instant asked for — off the left edge of a window
/// pinned to it, where the playhead is not drawn at all. A minute is enough for both and still
/// leaves the moment plainly at the left.
const arrivalLeadIn = Duration(minutes: 1);

/// The range a camera should open on to be able to reach [at].
///
/// The single-camera view defaults to an hour, because what you have come to find is nearly always
/// in the last few minutes. Arriving from a row in the feed breaks that assumption: the row can be
/// any age, and a track that does not reach the instant asked for cannot be seeked to it — the seek
/// would snap to the nearest footage the track *does* hold, which is the wrong moment delivered
/// with no sign that it is wrong.
///
/// Null — a live open — leaves the default alone, and so does an instant inside [feedLiveEdge]:
/// there is nothing recorded to go back to, the screen opens live, and a bar half a minute wide is
/// a worse answer than the default for a picture that is not replaying at all.
TimelineRange rangeForOpenAt(DateTime? at, {DateTime? now}) {
  if (at == null) return TimelineRange.hour;

  final reference = now ?? DateTime.now();
  if (reference.difference(at) < feedLiveEdge) return TimelineRange.hour;

  final from = at.subtract(arrivalLeadIn);
  final to = from.add(arrivalSpan);

  // The half hour after it has not happened yet, so the right edge is the clock and the window
  // grows into it — see [TimelineRange.since].
  return to.isAfter(reference)
      ? TimelineRange.since(from, span: arrivalSpan)
      : TimelineRange.window(from: from, to: to);
}

/// A few seconds of lead-in, so a row lands before what it describes rather than in the middle of
/// it.
///
/// A detection is stamped when the gate fired, which is already a moment *into* whatever caused it
/// — so seeking to it exactly joins the event underway. Five seconds is the difference between
/// watching someone walk up and finding them already at the door.
const feedLeadIn = Duration(seconds: 5);

/// Closer to now than this and there is nothing recorded to go back to.
///
/// The same boundary [ReplayController.liveEdge] applies, deliberately: a seek inside it hands
/// straight back to the live view, so a row that asked for one would arrive live anyway — with a
/// round trip and a flicker on the way.
const feedLiveEdge = Duration(seconds: 30);

/// Where playback should start for a row in the feed: [feedLeadIn] before the instant it names, or
/// null for one recent enough that the live view is what you meant.
///
/// This is the whole rule behind clicking a row, and it lives here so the wall and the
/// single-camera panel cannot answer it differently — one of them routing to a camera and the
/// other seeking the picture already on screen is a difference in *what to do*, not in *when*.
DateTime? replayStartFor(DateTime at, {DateTime? now}) {
  final reference = now ?? DateTime.now();
  if (reference.difference(at) < feedLiveEdge) return null;

  return at.subtract(feedLeadIn);
}

/// One camera's row on a wall's scrubber: its own day, and whose day it is.
///
/// The name travels with the window because on a wall the two are useless apart. A stack of bars
/// showing that *something* was recording says less than the single merged track it replaced;
/// what makes it worth the vertical space is knowing which bar is which camera.
class TimelineLane {
  const TimelineLane({required this.label, required this.window});

  final String label;
  final TimelineWindow window;
}

/// Everything the scrubber draws, over one window of wall clock.
///
/// One object rather than three separate reads because the three have to
/// agree. A mark positioned against a different window than the coverage
/// underneath it lands in the wrong place, and nothing would say so.
class TimelineWindow {
  const TimelineWindow({
    required this.from,
    required this.to,
    this.coverage = const [],
    this.marks = const [],
    this.loading = false,
  });

  /// The left edge of the track.
  final DateTime from;

  /// The right edge — "now", as of when this window was fetched.
  final DateTime to;

  final List<CoverageSpan> coverage;

  /// Ascending by [TimelineMark.at], and every producer owes that.
  ///
  /// The scrubber merges neighbouring marks into drawable blocks by walking them once and
  /// comparing each against the block it last opened, so a mark arriving out of order is not
  /// mis-drawn — it is folded into a block to its right and disappears.
  final List<TimelineMark> marks;

  /// Coverage has been asked for and has not arrived. Drawn as an empty track
  /// rather than as "nothing was recorded", which is a different statement.
  final bool loading;

  /// One window speaking for several cameras, for a scrubber above a wall of them.
  ///
  /// Coverage is merged rather than concatenated: two cameras recording the same hour is one hour
  /// with footage, and left overlapping it would paint the band twice and read as denser than it
  /// is. Merged with the same one-second tolerance the Server joins its own spans with, so two
  /// cameras whose sessions started a moment apart do not read as a gap between them.
  ///
  /// Marks are concatenated rather than merged — they are separate events that happened to
  /// separate cameras, and the scrubber's own block merging is what decides how they draw — but
  /// they are re-sorted afterwards. Each camera's list arrives ascending and the concatenation of
  /// several ascending lists is not, and [marks] documents why that matters: unsorted, every mark
  /// of the second camera folds into the last block of the first and never reaches the track.
  ///
  /// The union of nothing is an empty window ending now — which is what a wall with no cameras
  /// should draw, rather than throwing.
  factory TimelineWindow.union(Iterable<TimelineWindow> windows) {
    final all = windows.toList();
    if (all.isEmpty) {
      final now = DateTime.now();
      return TimelineWindow(from: now, to: now);
    }

    final spans = [for (final window in all) ...window.coverage]
      ..sort((a, b) => a.from.compareTo(b.from));

    const tolerance = Duration(seconds: 1);
    final merged = <CoverageSpan>[];
    for (final span in spans) {
      final last = merged.isEmpty ? null : merged.last;
      if (last != null && !span.from.isAfter(last.to.add(tolerance))) {
        // Not unconditionally `span.to` — a short span nested inside a longer one must not pull
        // the run's end backwards.
        if (span.to.isAfter(last.to)) {
          merged[merged.length - 1] = CoverageSpan(last.from, span.to);
        }
      } else {
        merged.add(span);
      }
    }

    // The first window's edges speak for all of them because every camera on a range shares one
    // anchor — see `_anchorFor` in the live repository. Were they anchored per camera this would be
    // an arbitrary pick, and the marks of the others would be positioned against edges that were
    // never theirs.
    return TimelineWindow(
      from: all.first.from,
      to: all.first.to,
      coverage: merged,
      marks: [for (final window in all) ...window.marks]
        ..sort((a, b) => a.at.compareTo(b.at)),
      loading: all.any((window) => window.loading),
    );
  }

  Duration get span => to.difference(from);

  /// Where [at] sits along the track, 0 at [from] and 1 at [to]. Unclamped, so
  /// a caller can drop what falls outside rather than pile it on an edge.
  double positionOf(DateTime at) {
    final total = span.inMicroseconds;
    if (total <= 0) return 0;
    return at.difference(from).inMicroseconds / total;
  }

  /// The inverse of [positionOf] — what a tap on the track means.
  DateTime timeAt(double position) => from.add(
    Duration(microseconds: (span.inMicroseconds * position).round()),
  );

  bool covers(DateTime at) => coverage.any((span) => span.contains(at));

  /// Where the next run of footage begins after [at], or null when nothing follows it.
  ///
  /// What replay needs at the end of a run, and a different question from [snap]: that one finds
  /// the nearest footage in either direction, which is right for a tap and wrong for playback,
  /// because playback only ever goes forwards.
  DateTime? nextRunAfter(DateTime at) {
    DateTime? best;
    for (final span in coverage) {
      if (span.from.isAfter(at) && (best == null || span.from.isBefore(best))) {
        best = span.from;
      }
    }

    return best;
  }

  /// The nearest instant to [at] that has footage, or null when this window
  /// has none at all.
  ///
  /// A tap in a hole snaps here rather than opening a playlist the Server
  /// answers with a 404 — "nothing was recorded then" must not read on screen
  /// as "playback is broken". Ties go forward: a gap is usually the tail of an
  /// outage, and the footage after it is the newer, more interesting side.
  DateTime? snap(DateTime at) {
    if (coverage.isEmpty) return null;

    DateTime? best;
    Duration? bestGap;

    for (final span in coverage) {
      if (span.contains(at)) return at;

      final candidate = at.isBefore(span.from) ? span.from : span.to;
      final gap = candidate.difference(at).abs();

      if (bestGap == null ||
          gap < bestGap ||
          (gap == bestGap && candidate.isAfter(at))) {
        best = candidate;
        bestGap = gap;
      }
    }

    return best;
  }
}
