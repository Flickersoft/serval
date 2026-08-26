import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/clip_selection.dart';
import 'package:serval_app/models/timeline.dart';

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

    test('footage either side of a recording restart is offered', () {
      // This assertion used to be the opposite one. Segments from two ffmpeg runs could not go in
      // one file, so the far side of a restart had to be unreachable by dragging or the Server
      // would refuse the range after the trim. The Server joins them now, so refusing to offer
      // them would be the trimmer holding back footage the export can deliver.
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
        selection.segments.map((s) => s.initFileName).toSet(),
        {'init-a.mp4', 'init-b.mp4'},
      );
    });

    test('a range crossing a restart reaches into the second session', () {
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
      )!.moveEnd(ClipEnd.end, start.add(const Duration(minutes: 1, seconds: 20)));

      // Both sessions are reachable, which is what the joining is for.
      expect(
        selection.segments.map((s) => s.initFileName).toSet(),
        {'init-a.mp4', 'init-b.mp4'},
      );

      // Past the start of the second session, rather than stopping at the end of the first.
      expect(
        selection.to.isAfter(start.add(const Duration(minutes: 1))),
        isTrue,
      );
    });

    test('the gap between two sessions is not counted as recorded', () {
      // Session A runs 40s from the start, session B begins at 60s: twenty seconds in the middle
      // when nothing was recording. The finished file is shorter than its ends suggest by exactly
      // that, because the Server closes the gap rather than holding on a frozen frame.
      //
      // Measured from coverage, not from segments. Coverage is read across the whole trimmable
      // range while segments are only read near the handles, so counting segments under-reported
      // this the moment a range reached past what had been fetched.
      final coverage = [
        CoverageSpan(start, start.add(const Duration(seconds: 40))),
        CoverageSpan(
          start.add(const Duration(minutes: 1)),
          start.add(const Duration(minutes: 1, seconds: 40)),
        ),
      ];

      final selection = ClipSelection.around(
        start.add(const Duration(seconds: 20)),
        segments: [
          ...session(10),
          ...session(
            10,
            init: 'init-b.mp4',
            from: start.add(const Duration(minutes: 1)),
          ),
        ],
        coverage: coverage,
      )!.moveEnd(ClipEnd.end, start.add(const Duration(minutes: 1, seconds: 40)));

      expect(selection.from, start);
      expect(selection.recorded, lessThan(selection.span));
      expect(selection.span - selection.recorded, const Duration(seconds: 20));
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

    test('an end may be dragged past the segments that were read', () {
      // The opposite of what this asserted before, and the change is the point. Segments are read
      // near the handles so a handle can snap; they are not a fence. Clamping to the last one meant
      // a drag stopped dead at the edge of whatever the index happened to have been fetched for,
      // which is the "45 minute limit" that kept coming back — the Server includes whole segments
      // either way, so there was never anything to protect.
      final selection = ClipSelection.around(
        start.add(const Duration(seconds: 60)),
        segments: session(30),
      )!;

      final dragged = selection.moveEnd(
        ClipEnd.end,
        start.add(const Duration(hours: 2)),
      );

      expect(dragged.to, start.add(const Duration(hours: 2)));
    });

    test('an end still snaps while it is inside the segments that were read', () {
      // Snapping has not gone away; it just stops applying where there is nothing to snap to.
      final selection = ClipSelection.around(
        start.add(const Duration(seconds: 60)),
        segments: session(30),
      )!;

      final dragged = selection.moveEnd(
        ClipEnd.end,
        start.add(const Duration(seconds: 71)),
      );

      expect(dragged.to, start.add(const Duration(seconds: 72)));
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
    test('the drawn window is always exactly as wide as the zoom', () {
      // "Wider does nothing." The track draws the window, not the zoom, so a window left at its old
      // width means the control changes a label and nothing else. It also has to stay exactly the
      // zoom's width regardless of how little footage has been read: clamping it to the loaded
      // segments pinned a twelve-hour track to the forty-five minutes fetched on open.
      for (final step in TrimZoom.steps) {
        final zoom = TrimZoom(step);
        final window = zoom.windowFor(
          start.add(const Duration(minutes: 30)),
          start.add(const Duration(minutes: 31)),
        );

        expect(window.duration, step, reason: 'a $step track must draw $step');
      }
    });

    test('every step is reachable from the narrowest by stepping wider', () {
      // Four taps from 12 minutes to 12 hours. If any step returned itself the ladder would stall
      // partway and the widest one would be unreachable however many times it was pressed.
      var zoom = TrimZoom.near;
      var taps = 0;

      while (!zoom.isWidest && taps < 20) {
        final next = zoom.wider;
        expect(next.span, greaterThan(zoom.span), reason: 'step $taps did not widen');
        zoom = next;
        taps++;
      }

      expect(zoom.isWidest, isTrue);
      expect(zoom.span, const Duration(hours: 12));
    });
    test('the ladder reaches the longest clip the Server allows', () {
      // The bug this exists for: the widest step used to be one hour, and a handle can only be
      // dragged inside the window the track draws — so the trimmer refused to go past an hour no
      // matter what Media:ExportMaxMinutes said. The cap is only real if the track can show it.
      expect(TrimZoom.steps.last, greaterThanOrEqualTo(const Duration(hours: 12)));
    });

    test('a multi-hour selection gets a step that can hold it', () {
      for (final span in [
        const Duration(hours: 2),
        const Duration(hours: 4),
        const Duration(hours: 8),
        const Duration(hours: 12),
      ]) {
        expect(
          TrimZoom.forSpan(span).span,
          greaterThanOrEqualTo(span),
          reason: 'a $span selection needs a track at least that wide',
        );
      }
    });

    test('stepping wider walks the ladder and stops at the top', () {
      var zoom = TrimZoom.near;
      final seen = <Duration>[zoom.span];

      while (!zoom.isWidest) {
        zoom = zoom.wider;
        seen.add(zoom.span);
      }

      expect(seen, TrimZoom.steps);
      expect(zoom.wider.span, zoom.span, reason: 'the widest step stays put');
    });

    test('the window does not move while a handle is in the middle of it', () {
      // The other bug: a window recomputed from the selection recentres on every change, which
      // slides the end you are *not* dragging across the screen. Grab one handle, both move.
      const zoom = TrimZoom.near;
      final window = CoverageSpan(start, start.add(zoom.span));

      expect(
        zoom.windowKeeping(window, start.add(const Duration(minutes: 6))).from,
        window.from,
      );
    });

    test('the window pans only once a handle reaches its edge', () {
      const zoom = TrimZoom.near;
      final window = CoverageSpan(start, start.add(zoom.span));

      final panned = zoom.windowKeeping(
        window,
        start.add(const Duration(minutes: 11, seconds: 30)),
      );

      expect(panned.from.isAfter(window.from), isTrue);
      expect(panned.duration, zoom.span);
      expect(panned.to.isAfter(start.add(const Duration(minutes: 11, seconds: 30))), isTrue);
    });
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
