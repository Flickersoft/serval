import 'dart:math' show max, min;

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart' show PointerDeviceKind;
import 'package:flutter/material.dart' show MaterialApp;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/theme/nocturne.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/replay_transport.dart';
import 'package:serval_app/widgets/timeline_range_panel.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// The scrubber as an input device.
///
/// No player anywhere in here: the gesture's whole job is turning an x into an instant, and that
/// is testable — and worth testing — without a decoder, a Server, or libmpv.
void main() {
  final from = DateTime(2026, 7, 24, 4, 0);
  final to = DateTime(2026, 7, 24, 16, 0);
  final midpoint = DateTime(2026, 7, 24, 10, 0);

  final window = TimelineWindow(
    from: from,
    to: to,
    coverage: [CoverageSpan(from, DateTime(2026, 7, 24, 12, 0))],
    marks: [TimelineMark(at: midpoint, ran: const Duration(seconds: 4))],
  );

  /// The scrubber at a known width, so an x maps to a predictable instant.
  Future<Rect> pump(
    WidgetTester tester, {
    bool live = true,
    ValueChanged<DateTime>? onSeek,
    ValueChanged<DateTime>? onScrub,
    VoidCallback? onBackToLive,
    ValueListenable<DateTime?>? playhead,
    List<TimelineLane> lanes = const [],
  }) async {
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 1000,
            child: TimelineScrubber(
              window: window,
              range: TimelineRange.halfDay,
              live: live,
              playhead: playhead,
              lanes: lanes,
              onSeek: onSeek,
              onScrub: onScrub,
              onBackToLive: onBackToLive,
            ),
          ),
        ),
      ),
    );

    return tester.getRect(find.byType(GestureDetector).last);
  }

  testWidgets(
    'a tap at the middle of the track seeks the middle of the window',
    (tester) async {
      DateTime? seeked;
      final track = await pump(tester, onSeek: (at) => seeked = at);

      await tester.tapAt(track.center);
      await tester.pump();

      expect(seeked, isNotNull);
      expect(
        seeked!.difference(midpoint).abs(),
        lessThan(const Duration(minutes: 1)),
        reason: 'the middle of a 12 h track is 10 am, not $seeked',
      );
    },
  );

  testWidgets('a tap at the left edge seeks the start of the window', (
    tester,
  ) async {
    DateTime? seeked;
    final track = await pump(tester, onSeek: (at) => seeked = at);

    await tester.tapAt(Offset(track.left + 1, track.center.dy));
    await tester.pump();

    expect(
      seeked!.difference(from).abs(),
      lessThan(const Duration(minutes: 2)),
    );
  });

  testWidgets('a drag scrubs continuously and seeks exactly once, on release', (
    tester,
  ) async {
    // This is what keeps a drag from putting a playlist request in flight per pointer sample.
    final scrubs = <DateTime>[];
    final seeks = <DateTime>[];
    final track = await pump(tester, onSeek: seeks.add, onScrub: scrubs.add);

    final gesture = await tester.startGesture(
      Offset(track.left + 100, track.center.dy),
    );
    for (var i = 0; i < 5; i++) {
      await gesture.moveBy(const Offset(80, 0));
      await tester.pump();
    }
    await gesture.up();
    await tester.pump();

    expect(
      scrubs.length,
      greaterThan(3),
      reason: 'the playhead should follow the finger',
    );
    expect(seeks, hasLength(1), reason: 'only the release opens a window');
    expect(seeks.single, scrubs.last);
  });

  testWidgets('a cancelled drag still seeks where it was left', (tester) async {
    // The pointer taken away mid-drag — off the edge of the window, or a touch the system
    // claimed. Dropping the seek here leaves the line stranded somewhere with nothing behind it,
    // which is the drag that reads as simply not working.
    final scrubs = <DateTime>[];
    final seeks = <DateTime>[];
    final track = await pump(tester, onSeek: seeks.add, onScrub: scrubs.add);

    final gesture = await tester.startGesture(
      Offset(track.left + 100, track.center.dy),
    );
    for (var i = 0; i < 3; i++) {
      await gesture.moveBy(const Offset(80, 0));
      await tester.pump();
    }
    await gesture.cancel();
    await tester.pump();

    expect(seeks, hasLength(1));
    expect(seeks.single, scrubs.last);
  });

  testWidgets(
    'coverage paints where there is footage and stops where there is not',
    (tester) async {
      final track = await pump(tester);

      // One span, ending two thirds of the way along: the window runs 4 am to 4 pm and the footage
      // stops at noon.
      final band = tester
          .widgetList<Positioned>(find.byType(Positioned))
          .where((p) => p.top == 0 && p.bottom == 0 && (p.width ?? 0) > 100);

      expect(band, hasLength(1));
      expect(band.single.left, 0);
      expect(band.single.width, closeTo(track.width * 8 / 12, 1));
    },
  );

  testWidgets('a burst of marks becomes one block, not a picket fence', (
    tester,
  ) async {
    // Measured against the live Server: a camera watching a driveway produced 114 motion scenes
    // in a day, most of them seconds apart. Over twelve hours a pixel is about forty seconds, so
    // drawn one by one they are a wall of identical hairlines that says "something happened"
    // everywhere and therefore nowhere.
    final burst = [
      for (var i = 0; i < 30; i++)
        TimelineMark(
          at: DateTime(2026, 7, 24, 9).add(Duration(seconds: 20 * i)),
        ),
      TimelineMark(at: DateTime(2026, 7, 24, 14)),
    ];

    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 1000,
            child: TimelineScrubber(
              window: TimelineWindow(from: from, to: to, marks: burst),
              range: TimelineRange.halfDay,
            ),
          ),
        ),
      ),
    );

    // Thirty marks spanning ten minutes plus one on its own: two blocks, not thirty-one ticks.
    final marks = tester
        .widgetList<Positioned>(find.byType(Positioned))
        .where(
          (p) =>
              p.top == 0 &&
              p.bottom == 0 &&
              (p.width ?? 0) > 2 &&
              (p.width ?? 0) < 100,
        );

    expect(marks, hasLength(2));
    expect(
      marks.first.width!,
      greaterThan(marks.last.width!),
      reason: 'the burst should be wider than the lone event',
    );
  });

  testWidgets('the wall track draws every camera, not just the first', (
    tester,
  ) async {
    // The merged track over a live wall. Each camera's marks are sorted, but the concatenation of
    // them is not — and the block merging only ever compares against the block it last opened. An
    // unsorted merge therefore folds the second camera's whole day into the first camera's last
    // block and draws one mark where there should be two.
    final merged = TimelineWindow.union([
      TimelineWindow(
        from: from,
        to: to,
        marks: [TimelineMark(at: DateTime(2026, 7, 24, 14))],
      ),
      TimelineWindow(
        from: from,
        to: to,
        marks: [TimelineMark(at: DateTime(2026, 7, 24, 6))],
      ),
    ]);

    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 1000,
            child: TimelineScrubber(
              window: merged,
              range: TimelineRange.halfDay,
            ),
          ),
        ),
      ),
    );

    final marks =
        tester
            .widgetList<Positioned>(find.byType(Positioned))
            .where(
              (p) =>
                  p.top == 0 &&
                  p.bottom == 0 &&
                  (p.width ?? 0) > 2 &&
                  (p.width ?? 0) < 100,
            )
            .toList()
          ..sort((a, b) => a.left!.compareTo(b.left!));

    expect(marks, hasLength(2), reason: 'one block per camera');

    // Two hours and ten hours into a twelve-hour window. Measured rather than assumed: the test
    // surface is narrower than the SizedBox asks for, so the track is not the width it requested.
    final width = tester.getSize(find.byType(TimelineScrubber)).width;
    expect(marks.first.left, closeTo(width * 2 / 12, 1));
    expect(marks.last.left, closeTo(width * 10 / 12, 1));
  });

  testWidgets('a loading window paints no coverage at all', (tester) async {
    // An empty track means "not known yet". Drawing nothing is the honest answer; drawing a hole
    // would claim the camera recorded nothing.
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 1000,
            child: TimelineScrubber(
              window: TimelineWindow(from: from, to: to, loading: true),
              range: TimelineRange.halfDay,
            ),
          ),
        ),
      ),
    );

    final wide = tester
        .widgetList<Positioned>(find.byType(Positioned))
        .where((p) => p.top == 0 && p.bottom == 0 && (p.width ?? 0) > 100);

    expect(wide, isEmpty);
  });

  testWidgets('the header says whether this is live, and offers the way back', (
    tester,
  ) async {
    await pump(tester);
    expect(find.text('Live'), findsOneWidget);
    expect(find.text('Back to live'), findsNothing);

    var backToLive = 0;
    await pump(tester, live: false, onBackToLive: () => backToLive++);
    expect(find.text('Replaying'), findsOneWidget);
    expect(find.text('Drag back to replay today'), findsNothing);

    await tester.tap(find.text('Back to live'));
    await tester.pump();
    expect(backToLive, 1);
  });

  group('the header while replaying', () {
    /// The header at a window's width, with everything replay puts in it.
    Future<void> pumpAt(WidgetTester tester, Size size) async {
      final view = tester.view;
      view.devicePixelRatio = 1.0;
      view.physicalSize = size;
      addTearDown(() {
        view.resetPhysicalSize();
        view.resetDevicePixelRatio();
      });

      await tester.pumpWidget(
        MaterialApp(
          debugShowCheckedModeBanner: false,
          home: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: size.width,
              child: TimelineScrubber(
                window: window,
                range: TimelineRange.hour,
                live: false,
                onRangeChanged: (_) {},
                onBackToLive: () {},
                transport: const ReplayTransport(
                  compact: true,
                  playing: true,
                  rate: 1,
                  rates: [1, 2, 4, 8],
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('gives the controls a line of their own on a phone', (
      tester,
    ) async {
      await pumpAt(tester, const Size(412, 892));

      expect(tester.takeException(), isNull);

      // The whole bug: the range control was the row's only flexible child, so it took the
      // shortfall alone and `scaleDown` shrank it toward nothing. 110px is its natural width.
      final range = tester.getRect(find.textContaining('Last').first);
      expect(range.width, greaterThan(30));

      // Two lines, not one — the transport sits below the control, not beside it.
      final transport = tester.getRect(find.byType(ReplayTransport));
      expect(transport.top, greaterThan(range.bottom));

      // And nothing was traded away to fit.
      expect(find.text('Replaying'), findsOneWidget);
      expect(find.text('Back to live'), findsOneWidget);
    });

    testWidgets('and keeps its single line on a desktop', (tester) async {
      await pumpAt(tester, const Size(1440, 900));

      expect(tester.takeException(), isNull);

      final range = tester.getRect(find.textContaining('Last').first);
      final transport = tester.getRect(find.byType(ReplayTransport));
      expect(transport.top, lessThan(range.bottom));
    });
  });

  testWidgets('the ticks are the window\'s own clock, not the range\'s', (
    tester,
  ) async {
    await pump(tester);

    // 4 am to 4 pm, stepping by three hours.
    expect(find.text('6 am'), findsOneWidget);
    expect(find.text('9 am'), findsOneWidget);
    expect(find.text('12 pm'), findsOneWidget);
    expect(find.text('3 pm'), findsOneWidget);
    expect(find.text('now'), findsOneWidget);
  });

  testWidgets('a chosen period ends where it ends, not at "now"', (
    tester,
  ) async {
    // The right edge must not read *now* in the recording hue whatever the window is. Reaching a
    // past day is an ordinary pick, and a track ending at 4 pm on the 24th is not the present.
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 1000,
            child: TimelineScrubber(
              window: window,
              range: TimelineRange.window(from: from, to: to),
            ),
          ),
        ),
      ),
    );

    expect(find.text('now'), findsNothing);
    expect(find.text('4 pm'), findsOneWidget);

    final live = tester
        .widgetList<ColoredBox>(find.byType(ColoredBox))
        .where((box) => box.color == Serval.recording);
    expect(
      live,
      isEmpty,
      reason: 'the recording hue is a claim about the present',
    );
  });

  testWidgets('without callbacks the scrubber is inert but still renders', (
    tester,
  ) async {
    // The sample repository has no Server to play from, which is what the goldens render.
    await pump(tester);
    final track = await pump(tester);

    await tester.tapAt(track.center);
    await tester.pump();
    expect(tester.takeException(), isNull);
  });

  group('choosing a range', () {
    /// The scrubber with its range button, on a surface big enough for the panel to open on.
    Future<TimelineRange? Function()> pumpButton(
      WidgetTester tester, {
      required TimelineRange range,
      bool live = true,
    }) async {
      TimelineRange? picked;

      await tester.binding.setSurfaceSize(const Size(1200, 800));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        MaterialApp(
          home: Align(
            alignment: Alignment.bottomLeft,
            child: SizedBox(
              width: 1000,
              child: TimelineScrubber(
                window: window,
                range: range,
                live: live,
                onRangeChanged: (r) => picked = r,
              ),
            ),
          ),
        ),
      );

      return () => picked;
    }

    testWidgets('the button says what is on the track', (tester) async {
      await pumpButton(tester, range: TimelineRange.hour);
      await tester.pumpAndSettle();

      expect(find.text('Last 1 h'), findsOneWidget);
      expect(find.byType(TimelineRangePanel), findsNothing);
    });

    testWidgets('the button opens the panel, and a span there applies', (
      tester,
    ) async {
      final picked = await pumpButton(tester, range: TimelineRange.hour);
      await tester.pumpAndSettle();

      await tester.tap(find.text('Last 1 h'));
      await tester.pumpAndSettle();
      expect(find.byType(TimelineRangePanel), findsOneWidget);

      await tester.tap(find.text('6 h'));
      await tester.pumpAndSettle();

      expect(picked(), TimelineRange.sixHours);
      expect(
        find.byType(TimelineRangePanel),
        findsOneWidget,
        reason: 'the panel applies as you touch it rather than closing on you',
      );
    });

    testWidgets('clicking away closes it, having changed nothing', (
      tester,
    ) async {
      final picked = await pumpButton(tester, range: TimelineRange.hour);
      await tester.pumpAndSettle();

      await tester.tap(find.text('Last 1 h'));
      await tester.pumpAndSettle();

      await tester.tapAt(const Offset(20, 20));
      await tester.pumpAndSettle();

      expect(find.byType(TimelineRangePanel), findsNothing);
      expect(picked(), isNull);
    });

    testWidgets('a chosen period names itself in the header, not "today"', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(1200, 800));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 1000,
              child: TimelineScrubber(
                window: window,
                range: TimelineRange.window(
                  from: DateTime(2026, 7, 28, 21),
                  to: DateTime(2026, 7, 28, 23),
                ),
                live: true,
              ),
            ),
          ),
        ),
      );

      expect(find.text('Drag back to replay today'), findsNothing);
      expect(
        find.text('Drag to replay 28 Jul, 9:00 pm to 11:00 pm'),
        findsOneWidget,
      );
    });
  });

  // What the track claims, in pixels.
  //
  // The failure these guard: marks merged into one block that inherits the most serious — or the
  // most specific — kind in it, so a camera with speech all evening chains into a single block and
  // one person in it paints hours of track orange. Every kind is its own layer, and the two
  // properties worth holding are that a layer is exactly as wide as the thing it describes, and
  // that no pixel is painted by two of them.
  group('the marks are drawn as one layer per kind', () {
    final seenAlert = Nocturne.mix(Serval.alert, 75);
    final heardAlert = Nocturne.mix(Serval.alertSound, 75);
    final soundColour = Nocturne.mix(Serval.markSound, 55);
    final objectColour = Nocturne.mix(Serval.markObject, 55);
    final speechColour = Nocturne.mix(Serval.markSpeech, 55);
    final sceneColour = Nocturne.mix(Serval.markScene, 55);

    final everyColour = [
      seenAlert,
      heardAlert,
      soundColour,
      objectColour,
      speechColour,
      sceneColour,
    ];

    // An hour across a thousand pixels: 3.6 s/px, which is the default range and the one the
    // arithmetic below is written against.
    final start = DateTime(2026, 7, 24, 9, 0);
    final end = start.add(const Duration(hours: 1));

    DateTime at(int second) => start.add(Duration(seconds: second));

    Future<void> pumpMarks(
      WidgetTester tester,
      List<TimelineMark> marks,
    ) async {
      // Wider than the track, so the SizedBox below really is a thousand pixels rather than being
      // clamped to the default 800 surface — every figure in this group is read off that width.
      await tester.binding.setSurfaceSize(const Size(1200, 800));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 1000,
              child: TimelineScrubber(
                window: TimelineWindow(from: start, to: end, marks: marks),
                range: TimelineRange.hour,
              ),
            ),
          ),
        ),
      );
    }

    List<Rect> rects(WidgetTester tester, Color colour) => [
      for (final box in tester.widgetList<ColoredBox>(find.byType(ColoredBox)))
        if (box.color == colour) tester.getRect(find.byWidget(box)),
    ];

    Future<List<Rect>> paint(
      WidgetTester tester,
      List<TimelineMark> marks,
      Color colour,
    ) async {
      await pumpMarks(tester, marks);
      return rects(tester, colour);
    }

    double total(List<Rect> rects) =>
        rects.fold(0.0, (sum, rect) => sum + rect.width);

    /// Thirty-one marks ten seconds apart — dense enough that each one's block reaches the next,
    /// so the whole run merges. This is the Family Room, where somebody talks every few seconds
    /// all evening.
    List<TimelineMark> chatter() => [
      for (int second = 600; second <= 900; second += 10)
        TimelineMark(at: at(second), of: ActivityKind.speech),
    ];

    testWidgets('one alert inside a busy run colours only itself', (
      tester,
    ) async {
      final marks = [
        ...chatter(),
        TimelineMark(
          at: at(750),
          ran: const Duration(seconds: 60),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
      ]..sort((a, b) => a.at.compareTo(b.at));

      await pumpMarks(tester, marks);
      final alert = rects(tester, seenAlert);
      final activity = rects(tester, speechColour);

      // The run spans 600 s to 900 s — 166.7 px to 250 px, plus the last mark's own 3 px — so it
      // is one merged block about 86 px wide. Merged as one layer, all of it is orange.
      expect(alert, hasLength(1));
      expect(
        alert.single.width,
        closeTo(16.7, 0.3),
        reason: '60 s at 3.6 s/px',
      );

      // The band is that run with the alert cut out of it, which is two pieces either side.
      expect(activity, hasLength(2));

      // Nothing is painted twice, and nothing in the run is left unpainted.
      expect(total(activity) + total(alert), closeTo(86.3, 0.5));
      for (final band in activity) {
        expect(band.overlaps(alert.single), isFalse);
      }
    });

    testWidgets('an isolated alert is drawn over bare track, not over band', (
      tester,
    ) async {
      final marks = [
        TimelineMark(
          at: at(1800),
          ran: const Duration(minutes: 2),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
      ];

      await pumpMarks(tester, marks);
      final alert = rects(tester, seenAlert);

      // Two minutes at 3.6 s/px. As a tick it would have been the 12 px ceiling, which is 43 s —
      // a third of the visit, in the wrong direction.
      expect(total(alert), closeTo(33.3, 0.5));

      // Nothing underneath it. Every layer fills with alpha rather than an opaque colour, so a
      // band left under an alert would blend through and the same alert would come out a
      // different orange depending on how busy the camera was around it.
      expect(rects(tester, objectColour), isEmpty);
    });

    // The bug behind this: a ten-second visit was painted across thirty seconds of track, so the
    // playhead dropped into the orange landed after the person had left and the stage came up
    // with no box on it — indistinguishable from a broken overlay.
    testWidgets('an alert covers the time it covers, and no more', (
      tester,
    ) async {
      final alert = await paint(tester, [
        TimelineMark(
          at: at(900),
          ran: const Duration(minutes: 5),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
      ], seenAlert);

      // 900 s in, five minutes long, on an hour across a thousand pixels.
      expect(alert.single.left, closeTo(250, 0.5));
      expect(alert.single.width, closeTo(83.3, 0.5));
    });

    testWidgets('an instant is still wide enough to see and to aim at', (
      tester,
    ) async {
      final alert = await paint(tester, [
        TimelineMark(
          at: at(900),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
      ], seenAlert);

      expect(alert.single.width, closeTo(3, 0.1));
    });

    testWidgets('a window with nothing worth alerting on draws no alert', (
      tester,
    ) async {
      await pumpMarks(tester, chatter());

      expect(rects(tester, seenAlert), isEmpty);
      expect(rects(tester, heardAlert), isEmpty);
    });

    // The whole point of the change: the bar answers "was that heard or seen" without a click.
    testWidgets('each kind is drawn in its own hue', (tester) async {
      // Far enough apart that nothing merges and nothing is cut.
      await pumpMarks(tester, [
        TimelineMark(at: at(300), of: ActivityKind.scenes),
        TimelineMark(at: at(900), of: ActivityKind.speech),
        TimelineMark(at: at(1500), of: ActivityKind.objects),
        TimelineMark(at: at(2100), of: ActivityKind.sounds),
      ]);

      expect(rects(tester, sceneColour), hasLength(1));
      expect(rects(tester, speechColour), hasLength(1));
      expect(rects(tester, objectColour), hasLength(1));
      expect(rects(tester, soundColour), hasLength(1));
    });

    testWidgets('an alert says whether it was heard or seen', (tester) async {
      await pumpMarks(tester, [
        TimelineMark(
          at: at(600),
          ran: const Duration(seconds: 30),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
        TimelineMark(
          at: at(2400),
          ran: const Duration(seconds: 30),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.sounds,
        ),
      ]);

      expect(rects(tester, seenAlert), hasLength(1));
      expect(rects(tester, heardAlert), hasLength(1));

      // Both still sized as alerts — 30 s at 3.6 s/px — rather than one of them falling back to
      // the band's tick.
      expect(rects(tester, seenAlert).single.width, closeTo(8.3, 0.3));
      expect(rects(tester, heardAlert).single.width, closeTo(8.3, 0.3));
    });

    // One arrival routinely produces a detection and a scene at the same instant, and a
    // conversation with a dog barking over it produces an utterance and a sound. Whichever kind
    // is more specific owns the pixel; the other is cut away rather than blended into it.
    testWidgets('the more specific kind owns a shared instant', (tester) async {
      await pumpMarks(tester, [
        TimelineMark(at: at(900), of: ActivityKind.scenes),
        TimelineMark(at: at(900), of: ActivityKind.objects),
      ]);

      expect(rects(tester, objectColour), hasLength(1));
      expect(rects(tester, sceneColour), isEmpty);

      await pumpMarks(tester, [
        TimelineMark(at: at(900), of: ActivityKind.objects),
        TimelineMark(at: at(900), of: ActivityKind.sounds),
      ]);

      expect(rects(tester, soundColour), hasLength(1));
      expect(rects(tester, objectColour), isEmpty);
    });

    // Six hues is more than the bar can explain on its own, and there is nowhere to put a legend.
    // The readout under the cursor is what teaches the palette, so it has to name the band rather
    // than only the clock.
    testWidgets('the readout names what is under the cursor', (tester) async {
      await pumpMarks(tester, [
        TimelineMark(
          at: at(900),
          ran: const Duration(seconds: 60),
          of: ActivityKind.sounds,
        ),
      ]);

      final mark = rects(tester, soundColour).single;

      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await mouse.addPointer(location: Offset.zero);
      addTearDown(mouse.removePointer);

      await mouse.moveTo(mark.center);
      await tester.pump();
      expect(find.textContaining('Sounds'), findsOneWidget);

      // Off the band and it is the clock alone — the label describes what is painted there, not
      // the nearest thing to it.
      await mouse.moveTo(Offset(mark.right + 60, mark.center.dy));
      await tester.pump();
      expect(find.textContaining('Sounds'), findsNothing);
    });

    // A layer is cut against every layer above it at once, not against one of them and then the
    // next — the failure that hides is a lower band surviving under the second-highest layer
    // because the cut only ever knew about the highest.
    testWidgets('a low band is cut by every layer over it', (tester) async {
      await pumpMarks(tester, [
        // A continuous run of descriptions, 600 s to 900 s.
        for (int second = 600; second <= 900; second += 10)
          TimelineMark(at: at(second), of: ActivityKind.scenes),
        // Two separated bursts over it, of different kinds.
        for (int second = 660; second <= 680; second += 10)
          TimelineMark(at: at(second), of: ActivityKind.objects),
        for (int second = 810; second <= 830; second += 10)
          TimelineMark(at: at(second), of: ActivityKind.sounds),
      ]);

      final scenes = rects(tester, sceneColour);
      final objects = rects(tester, objectColour);
      final sounds = rects(tester, soundColour);

      // Three fragments: before the objects, between them and the sounds, and after.
      expect(scenes, hasLength(3));
      expect(objects, hasLength(1));
      expect(sounds, hasLength(1));

      for (final scene in scenes) {
        expect(scene.overlaps(objects.single), isFalse);
        expect(scene.overlaps(sounds.single), isFalse);
      }
    });

    // The guarantee the two-layer track had, held across six. Every fill is `Nocturne.mix`, which
    // is alpha rather than an opaque blend, so a pixel painted twice comes out a colour that means
    // nothing — and means it only on the cameras busy enough to produce the overlap.
    testWidgets('no pixel is painted twice', (tester) async {
      // Every kind at once, severe and not, overlapping deliberately: a person at the door with a
      // description of them, someone talking, and a dog over the top of it.
      await pumpMarks(tester, [
        for (int second = 900; second <= 960; second += 10) ...[
          TimelineMark(at: at(second), of: ActivityKind.scenes),
          TimelineMark(at: at(second), of: ActivityKind.speech),
          TimelineMark(at: at(second), of: ActivityKind.objects),
          TimelineMark(at: at(second), of: ActivityKind.sounds),
        ],
        TimelineMark(
          at: at(930),
          ran: const Duration(seconds: 20),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.objects,
        ),
        TimelineMark(
          at: at(940),
          ran: const Duration(seconds: 20),
          kind: TimelineMarkKind.alert,
          of: ActivityKind.sounds,
        ),
      ]);

      final painted = [
        for (final colour in everyColour) ...rects(tester, colour),
      ];

      expect(painted, isNotEmpty);

      for (var i = 0; i < painted.length; i++) {
        for (var j = i + 1; j < painted.length; j++) {
          expect(
            painted[i].overlaps(painted[j]),
            isFalse,
            reason: '${painted[i]} overlaps ${painted[j]}',
          );
        }
      }

      // And the run is still continuous — cutting removed overlap, not coverage. 900 s to 960 s is
      // 250 px to 266.7 px, plus the last tick's own 3 px.
      final left = painted.map((rect) => rect.left).reduce(min);
      final right = painted.map((rect) => rect.right).reduce(max);
      expect(total(painted), closeTo(right - left, 0.5));
      expect(right - left, closeTo(19.7, 0.5));
    });
  });

  group('a wall of cameras', () {
    final lanes = [
      TimelineLane(label: 'Driveway', window: window),
      TimelineLane(label: 'A very long camera name', window: window),
    ];

    testWidgets('every bar starts at the same x, whatever its name', (
      tester,
    ) async {
      // The bug this pins: with the name laid *over* the bar, a long name covered more of its own
      // camera's day than a short one, and the bars appeared to start in different places.
      await pump(tester, live: false, lanes: lanes);

      final bars = tester
          .renderObjectList<RenderBox>(find.byType(ClipRRect))
          .map((box) => box.localToGlobal(Offset.zero).dx)
          .toSet();

      expect(bars, hasLength(1), reason: 'bars start at $bars');
    });

    testWidgets('a tap beside a name means the start of the window', (
      tester,
    ) async {
      // The gutter is not part of the timeline. Measured from the widget instead, every instant
      // would read earlier than it is by the width of the names.
      DateTime? seeked;
      final track = await pump(
        tester,
        live: false,
        lanes: lanes,
        onSeek: (at) => seeked = at,
      );

      await tester.tapAt(Offset(track.left + 4, track.top + 4));
      await tester.pump();

      expect(seeked, from);
    });

    testWidgets('a tap at the left of the bars is the start of the window', (
      tester,
    ) async {
      DateTime? seeked;
      await pump(
        tester,
        live: false,
        lanes: lanes,
        onSeek: (at) => seeked = at,
      );

      final bar = tester.getRect(find.byType(ClipRRect).first);
      await tester.tapAt(Offset(bar.left + 1, bar.center.dy));
      await tester.pump();

      expect(
        seeked!.difference(from).abs(),
        lessThan(const Duration(minutes: 2)),
      );
    });

    testWidgets('a tap at the right of the bars is the end of the window', (
      tester,
    ) async {
      DateTime? seeked;
      await pump(
        tester,
        live: false,
        lanes: lanes,
        onSeek: (at) => seeked = at,
      );

      final bar = tester.getRect(find.byType(ClipRRect).first);
      await tester.tapAt(Offset(bar.right - 1, bar.center.dy));
      await tester.pump();

      expect(
        seeked!.difference(to).abs(),
        lessThan(const Duration(minutes: 2)),
      );
    });
  });
}
