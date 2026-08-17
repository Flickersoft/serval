import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/clip_selection.dart';

/// The trimmer's arithmetic.
///
/// Every one of these decides what ends up in a saved file rather than what a screen looks like, so
/// getting one wrong keeps the wrong minute — which nobody notices until the clip is the only copy
/// left. The rule underneath all of them is that a handle may only land on a segment boundary,
/// because a segment is the smallest thing the Server can copy without re-encoding.
void main() {
  final start = DateTime(2026, 8, 9, 16, 0);

  /// A session of four-second segments, [count] of them from [start].
  List<RecordedSegment> session(
    int count, {
    String init = 'init-a.mp4',
    DateTime? from,
  }) => [
    for (var i = 0; i < count; i++)
      RecordedSegment(
        from: (from ?? start).add(Duration(seconds: i * 4)),
        duration: const Duration(seconds: 4),
        initFileName: init,
      ),
  ];

  group('opening', () {
    test('opens selected, covering at least the thirty seconds either side', () {
      // Both ends snap outward, so the range is never *less* than what was asked for — 90s snaps
      // back to 88 and 150s forward to 152, giving 64 seconds rather than exactly 60.
      final segments = session(60);
      final anchor = start.add(const Duration(minutes: 2));
      final selection = ClipSelection.around(anchor, segments: segments)!;

      expect(selection.from, start.add(const Duration(seconds: 88)));
      expect(selection.to, start.add(const Duration(seconds: 152)));
      expect(selection.span, greaterThanOrEqualTo(const Duration(seconds: 60)));
      expect(selection.span, lessThan(const Duration(seconds: 70)));
    });

    test('both ends land on segment boundaries', () {
      // The anchor is deliberately mid-segment. A handle between two segments would promise a
      // precision the export cannot deliver.
      final segments = session(60);
      final selection = ClipSelection.around(
        start.add(const Duration(seconds: 122)),
        segments: segments,
      )!;

      expect(selection.from.difference(start).inSeconds % 4, 0);
      expect(selection.to.difference(start).inSeconds % 4, 0);
    });

    test('the start never moves past the moment asked for', () {
      // Snapping down rather than to the nearest: a second of extra lead-in is invisible, losing
      // the first second of the thing being kept is the whole point of the clip.
      final segments = session(60);
      final anchor = start.add(const Duration(seconds: 122));
      final selection = ClipSelection.around(anchor, segments: segments)!;

      expect(
        selection.from.isBefore(anchor.subtract(const Duration(seconds: 30))) ||
            selection.from == anchor.subtract(const Duration(seconds: 30)),
        isTrue,
      );
    });

    test('at the live edge the range runs entirely backwards', () {
      // The future has not been recorded, so a symmetric window around "now" would ask for
      // footage that does not exist — the camera screen passes after: zero for this reason.
      final segments = session(60);
      final liveEdge = segments.last.to;

      final selection = ClipSelection.around(
        liveEdge.subtract(const Duration(seconds: 1)),
        segments: segments,
        before: const Duration(seconds: 60),
        after: Duration.zero,
      )!;

      expect(selection.to, liveEdge);
      expect(selection.from, liveEdge.subtract(const Duration(seconds: 64)));
    });

    test('an anchor past the live edge falls back to the last thing recorded', () {
      // The live case, not an edge one: ffmpeg publishes a segment only once it is complete, so
      // "now" is always a few seconds past the newest one. Requiring containment made *Save clip*
      // on a live camera answer "nothing was recorded here" every time.
      final segments = session(60);
      final selection = ClipSelection.around(
        segments.last.to.add(const Duration(seconds: 3)),
        segments: segments,
        before: const Duration(seconds: 60),
        after: Duration.zero,
      );

      expect(selection, isNotNull);
      expect(selection!.to, segments.last.to);
    });

    test('an anchor long past the end still gives the last thing recorded', () {
      // A camera that stopped hours ago. The trimmer opens on what it did record rather than
      // refusing — nothing is hidden, since the times it chose are on screen.
      final segments = session(10);
      final selection = ClipSelection.around(
        start.add(const Duration(hours: 5)),
        segments: segments,
        before: const Duration(seconds: 20),
        after: Duration.zero,
      )!;

      expect(selection.to, segments.last.to);
      expect(selection.span, const Duration(seconds: 20));
    });

    test('an anchor before anything was recorded has nothing to trim', () {
      expect(
        ClipSelection.around(
          start.subtract(const Duration(hours: 5)),
          segments: session(10),
        ),
        isNull,
      );
    });

    test('only the session holding the anchor is offered', () {
      // Segments from two ffmpeg runs cannot go in one file, so the far side must not be reachable
      // by dragging — the Server would refuse the range, and it would refuse it after the trim.
      final segments = [
        ...session(10),
        ...session(
          10,
          init: 'init-b.mp4',
          from: start.add(const Duration(minutes: 1)),
        ),
      ];

      final selection = ClipSelection.around(
        start.add(const Duration(seconds: 20)),
        segments: segments,
      )!;

      expect(
        selection.segments.every((s) => s.initFileName == 'init-a.mp4'),
        isTrue,
      );
      expect(selection.isOneSession, isTrue);
    });
  });

  group('moving an end', () {
    test('a drag snaps to the nearest boundary', () {
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 2)),
        segments: session(60),
      )!;
      final moved = selection.moveEnd(
        ClipEnd.end,
        start.add(const Duration(seconds: 183)),
      );

      expect(moved.to, start.add(const Duration(seconds: 184)));
    });

    test('a nudge moves one segment', () {
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 2)),
        segments: session(60),
      )!;

      expect(selection.nudge, const Duration(seconds: 4));
      expect(
        selection.nudgeBy(1).to,
        selection.to.add(const Duration(seconds: 4)),
      );
      expect(
        selection.nudgeBy(-1).to,
        selection.to.subtract(const Duration(seconds: 4)),
      );
    });

    test('the nudge is read from the segments, not from the setting', () {
      // Under -c:v copy a segment is as long as the camera's GOP made it. The screen renders its
      // caption from this, so it never promises a second the export cannot deliver.
      final segments = [
        for (var i = 0; i < 30; i++)
          RecordedSegment(
            from: start.add(Duration(seconds: i * 6)),
            duration: const Duration(seconds: 6),
            initFileName: 'init-a.mp4',
          ),
      ];

      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 1)),
        segments: segments,
      )!;

      expect(selection.nudge, const Duration(seconds: 6));
    });

    test('nudges move whichever end was last touched', () {
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 2)),
        segments: session(60),
      )!;
      final holdingStart = selection.withActive(ClipEnd.start);

      expect(
        holdingStart.nudgeBy(-1).from,
        selection.from.subtract(const Duration(seconds: 4)),
      );
      expect(holdingStart.nudgeBy(-1).to, selection.to);
    });

    test('the ends cannot cross or meet', () {
      // A zero-length clip is refused by the Server, so the trimmer must not be able to produce
      // one — a drag that would is held at one segment instead.
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 2)),
        segments: session(60),
      )!;
      final crossed = selection.moveEnd(ClipEnd.end, start);

      expect(crossed.to.isAfter(crossed.from), isTrue);
      expect(crossed.span, greaterThanOrEqualTo(const Duration(seconds: 4)));
    });

    test('an end cannot leave the recorded session', () {
      final selection = ClipSelection.around(
        start.add(const Duration(seconds: 60)),
        segments: session(30),
      )!;
      final dragged = selection.moveEnd(
        ClipEnd.end,
        start.add(const Duration(hours: 2)),
      );

      expect(dragged.to, start.add(const Duration(seconds: 120)));
    });
  });

  group('the cap', () {
    test('a drag past the cap trims the other end rather than being refused', () {
      // Refusing the drag would leave a handle that stops following the finger with no explanation.
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 30)),
        segments: session(1200),
      )!;
      final long = selection.moveEnd(
        ClipEnd.end,
        selection.from.add(const Duration(minutes: 45)),
        max: const Duration(minutes: 30),
      );

      expect(long.span, lessThanOrEqualTo(const Duration(minutes: 30)));
    });

    test('the end being held keeps its position', () {
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 30)),
        segments: session(1200),
      )!;
      final target = selection.from.add(const Duration(minutes: 45));
      final long = selection.moveEnd(
        ClipEnd.end,
        target,
        max: const Duration(minutes: 30),
      );

      expect(long.to, target);
      expect(long.from, target.subtract(const Duration(minutes: 30)));
    });

    test('exactly the cap is allowed', () {
      // An off-by-one here is a range the trimmer offers and the Server then refuses, which reads
      // as a bug in the trimmer.
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 30)),
        segments: session(1200),
      )!;
      final capped = selection.moveEnd(
        ClipEnd.end,
        selection.from.add(const Duration(minutes: 30)),
        max: const Duration(minutes: 30),
      );

      expect(capped.span, const Duration(minutes: 30));
    });
  });

  group('whole event', () {
    test('snaps the range to what Serval saw', () {
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 2)),
        segments: session(60),
      )!;
      final snapped = selection.snapTo(
        start.add(const Duration(seconds: 61)),
        start.add(const Duration(seconds: 119)),
      );

      expect(snapped.from, start.add(const Duration(seconds: 60)));
      expect(snapped.to, start.add(const Duration(seconds: 120)));
    });

    test('an episode longer than the cap is trimmed to it', () {
      final selection = ClipSelection.around(
        start.add(const Duration(minutes: 30)),
        segments: session(1200),
      )!;
      final snapped = selection.snapTo(
        start,
        start.add(const Duration(minutes: 50)),
        max: const Duration(minutes: 30),
      );

      expect(snapped.span, lessThanOrEqualTo(const Duration(minutes: 30)));
    });
  });

  group('zoom', () {
    test('a short clip gets the near step, a long one the far step', () {
      expect(TrimZoom.forSpan(const Duration(seconds: 55)).isNear, isTrue);
      expect(TrimZoom.forSpan(const Duration(minutes: 25)).isNear, isFalse);
    });

    test('a selection that would outgrow the near track widens it', () {
      // Twelve minutes cannot express a twenty-five minute selection, and a handle off the end of
      // the track is a handle nobody can reach.
      expect(
        TrimZoom.forSpan(const Duration(minutes: 10)).span,
        const Duration(hours: 1),
      );
    });

    test('the window is centred on the selection', () {
      final window = TrimZoom.near.windowFor(
        start.add(const Duration(minutes: 30)),
        start.add(const Duration(minutes: 31)),
      );

      expect(window.duration, const Duration(minutes: 12));
      expect(window.from, start.add(const Duration(minutes: 24, seconds: 30)));
    });

    test('the window is held inside what was recorded', () {
      final window = TrimZoom.near.windowFor(
        start,
        start.add(const Duration(minutes: 1)),
        earliest: start,
      );

      expect(window.from, start);
      expect(window.duration, const Duration(minutes: 12));
    });
  });
}
