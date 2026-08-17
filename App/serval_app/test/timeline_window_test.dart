import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/timeline.dart';

/// The arithmetic between an x on the scrubber and an instant in the recording.
///
/// Worth pinning on its own: an off-by-one here is not a rendering glitch, it is a seek to the
/// wrong hour — and it would look entirely plausible on screen.
void main() {
  final from = DateTime(2026, 7, 24, 4, 0);
  final to = DateTime(2026, 7, 24, 16, 0);

  TimelineWindow windowWith({List<CoverageSpan> coverage = const []}) =>
      TimelineWindow(from: from, to: to, coverage: coverage);

  group('positions', () {
    test('the edges are 0 and 1', () {
      final window = windowWith();
      expect(window.positionOf(from), 0);
      expect(window.positionOf(to), 1);
    });

    test('positionOf and timeAt round-trip across every range', () {
      for (final range in [
        ...TimelineRange.presets,
        TimelineRange.window(
          from: DateTime(2026, 7, 28, 21),
          to: DateTime(2026, 7, 28, 23),
        ),
      ]) {
        final window = TimelineWindow(
          from: to.subtract(range.duration),
          to: to,
        );

        for (final position in [0.0, 0.06, 0.5, 0.813, 1.0]) {
          expect(
            window.positionOf(window.timeAt(position)),
            closeTo(position, 1e-9),
            reason: 'round trip failed at $position over ${range.label}',
          );
        }
      }
    });

    test('an instant outside the window is not clamped onto an edge', () {
      // The scrubber drops what falls outside. Clamping here would instead pile every stale mark
      // onto the left edge, which reads as a burst of activity that did not happen.
      final window = windowWith();
      expect(
        window.positionOf(from.subtract(const Duration(hours: 6))),
        lessThan(0),
      );
      expect(
        window.positionOf(to.add(const Duration(hours: 6))),
        greaterThan(1),
      );
    });

    test('a zero-length window does not divide by zero', () {
      expect(TimelineWindow(from: from, to: from).positionOf(from), 0);
    });
  });

  group('coverage', () {
    final early = CoverageSpan(from, DateTime(2026, 7, 24, 8, 0));
    final late = CoverageSpan(DateTime(2026, 7, 24, 12, 0), to);
    final window = windowWith(coverage: [early, late]);

    test('an instant inside a span is covered and snaps to itself', () {
      final at = DateTime(2026, 7, 24, 6, 0);
      expect(window.covers(at), isTrue);
      expect(window.snap(at), at);
    });

    test('an instant in the hole snaps to the nearer edge', () {
      // 9 am is an hour past the end of the early span and three before the late one.
      expect(window.snap(DateTime(2026, 7, 24, 9, 0)), early.to);
      expect(window.snap(DateTime(2026, 7, 24, 11, 30)), late.from);
    });

    test('an exact tie in the hole goes forward', () {
      // 10 am is two hours from either side. Forward, because a gap is usually the tail of an
      // outage and the footage after it is the newer half.
      expect(window.snap(DateTime(2026, 7, 24, 10, 0)), late.from);
    });

    test('before all coverage snaps forward, after it snaps back', () {
      final later = windowWith(coverage: [late]);
      expect(later.snap(from), late.from);
      expect(later.snap(to.add(const Duration(hours: 1))), late.to);
    });

    test('a window with no coverage snaps nowhere', () {
      // Null is what stops the controller opening a playlist the Server answers with a 404.
      expect(windowWith().snap(DateTime(2026, 7, 24, 9, 0)), isNull);
      expect(windowWith().covers(DateTime(2026, 7, 24, 9, 0)), isFalse);
    });

    test('a span contains its own edges', () {
      expect(early.contains(early.from), isTrue);
      expect(early.contains(early.to), isTrue);
      expect(early.contains(early.to.add(const Duration(seconds: 1))), isFalse);
    });
  });

  group('union', () {
    TimelineWindow cameraWith(List<DateTime> at) => TimelineWindow(
      from: from,
      to: to,
      marks: [for (final instant in at) TimelineMark(at: instant)],
    );

    test('marks from several cameras come out in time order', () {
      // The property the scrubber's block merging rests on. Each camera's own list is sorted, and
      // the concatenation of sorted lists is not — so without the sort here every mark of the
      // second camera compares against the *last* block of the first, merges into it, and is never
      // drawn. On a live wall that is most of the day's activity silently missing.
      final merged = TimelineWindow.union([
        cameraWith([DateTime(2026, 7, 24, 14), DateTime(2026, 7, 24, 15)]),
        cameraWith([DateTime(2026, 7, 24, 5), DateTime(2026, 7, 24, 9)]),
      ]);

      expect(merged.marks.map((mark) => mark.at), [
        DateTime(2026, 7, 24, 5),
        DateTime(2026, 7, 24, 9),
        DateTime(2026, 7, 24, 14),
        DateTime(2026, 7, 24, 15),
      ]);
    });

    test('overlapping coverage merges rather than stacking', () {
      // Two cameras recording the same hour is one hour with footage. Left overlapping, the band
      // would be painted twice and read as denser than it is.
      final merged = TimelineWindow.union([
        windowWith(coverage: [CoverageSpan(from, DateTime(2026, 7, 24, 10))]),
        windowWith(coverage: [CoverageSpan(DateTime(2026, 7, 24, 8), to)]),
      ]);

      expect(merged.coverage, hasLength(1));
      expect(merged.coverage.single.from, from);
      expect(merged.coverage.single.to, to);
    });

    test('the union of nothing is an empty window, not a throw', () {
      // What a wall with no cameras draws.
      final merged = TimelineWindow.union(const []);
      expect(merged.marks, isEmpty);
      expect(merged.coverage, isEmpty);
      expect(merged.span, Duration.zero);
    });

    test('one camera still loading leaves the merge loading', () {
      expect(
        TimelineWindow.union([
          windowWith(),
          TimelineWindow(from: from, to: to, loading: true),
        ]).loading,
        isTrue,
      );
    });
  });
}
