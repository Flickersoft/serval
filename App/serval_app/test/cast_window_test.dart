// Which stretch of recording a cast covers, and where its clock starts.
//
// Both answers matter for the same reason and it is not obvious from either name: a seek on the
// television is sent as an offset in seconds, computed here as `at - window.from`. If the window
// is narrower than the scrubber, a click on the bar lands outside it and costs a re-cast — a
// second or two of black screen. If `window.from` is not where the footage actually begins, every
// seek in the session misses by the difference.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/cast_target.dart';
import 'package:serval_app/models/timeline.dart';

/// A timeline with continuous footage across the whole of it, which is the ordinary case.
TimelineWindow _covered(DateTime from, DateTime to) =>
    TimelineWindow(from: from, to: to, coverage: [CoverageSpan(from, to)]);

void main() {
  final noon = DateTime(2026, 8, 21, 12);

  group('the window covers what the scrubber shows', () {
    test('a short range is cast whole, whatever the playhead is doing', () {
      final timeline = _covered(noon, noon.add(const Duration(hours: 1)));

      final window = CastWindow.around(
        noon.add(const Duration(minutes: 5)),
        timeline,
      );

      expect(window.from, noon);
      expect(window.to, timeline.to);

      // The point of all of it: the far end of the bar is a seek, not a new cast.
      expect(window.covers(timeline.to), isTrue);
      expect(window.offsetOf(timeline.to), const Duration(hours: 1));
    });

    test('a day is cut to six hours around the playhead', () {
      final timeline = _covered(noon, noon.add(const Duration(hours: 24)));
      final at = noon.add(const Duration(hours: 12));

      final window = CastWindow.around(at, timeline);

      expect(window.to.difference(window.from), CastWindow.maxSpan);
      expect(window.from, at.subtract(const Duration(hours: 3)));
      expect(window.to, at.add(const Duration(hours: 3)));
    });

    // Half a window is what centring naively would give, and it would halve the reach of every
    // seek for a viewer watching the beginning or the end of a long day — which is most of them.
    test('a playhead at the edge still gets a full span', () {
      final timeline = _covered(noon, noon.add(const Duration(hours: 24)));

      final atStart = CastWindow.around(noon, timeline);
      expect(atStart.from, noon);
      expect(atStart.to.difference(atStart.from), CastWindow.maxSpan);

      final atEnd = CastWindow.around(timeline.to, timeline);
      expect(atEnd.to, timeline.to);
      expect(atEnd.to.difference(atEnd.from), CastWindow.maxSpan);
    });
  });

  group('the clock starts at the footage', () {
    /// **The failure this guards.** The cast playlist's zero is its first segment. Open a window on
    /// a camera that was switched off until 3 am and the first segment is at 3 am — so a seek
    /// measured from midnight would be sent three hours short, every time, for the whole session.
    test('a window that opens on a gap starts where recording resumed', () {
      final resumed = noon.add(const Duration(hours: 2));
      final timeline = TimelineWindow(
        from: noon,
        to: noon.add(const Duration(hours: 4)),
        coverage: [CoverageSpan(resumed, noon.add(const Duration(hours: 4)))],
      );

      final window = CastWindow.around(resumed, timeline);

      expect(window.from, resumed);
      expect(window.offsetOf(resumed), Duration.zero);
    });

    // A gap in the middle is spanned by the playlist at wall-clock length, so it needs no
    // correction — and applying one would break the far commoner case of footage either side.
    test('a gap in the middle does not move the start', () {
      final timeline = TimelineWindow(
        from: noon,
        to: noon.add(const Duration(hours: 4)),
        coverage: [
          CoverageSpan(noon, noon.add(const Duration(hours: 1))),
          CoverageSpan(
            noon.add(const Duration(hours: 3)),
            noon.add(const Duration(hours: 4)),
          ),
        ],
      );

      final window = CastWindow.around(noon, timeline);

      expect(window.from, noon);
    });

    // Coverage the App has not fetched yet is not evidence of a gap. Casting from the window's own
    // edge is the same thing every version before this did, and it is right whenever there is
    // footage there.
    test('an unknown coverage leaves the window alone', () {
      final timeline = TimelineWindow(
        from: noon,
        to: noon.add(const Duration(hours: 1)),
        loading: true,
      );

      expect(CastWindow.around(noon, timeline).from, noon);
    });
  });
}
