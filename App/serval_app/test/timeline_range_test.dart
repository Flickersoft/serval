// The scrubber's window, which is not always "the last N hours ending at this instant".
//
// A chosen period fixes the right edge, and three things downstream read that: the repository's
// cache key, whether the window is worth refetching, and whether the header may say "today". An
// error here does not throw — it quietly shows the wrong hours.
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/widgets/timeline_range_panel.dart';

void main() {
  final now = DateTime(2026, 8, 2, 14, 30);

  group('presets', () {
    test('end at now and keep pace with it', () {
      for (final preset in TimelineRange.presets) {
        expect(preset.live, isTrue);
        expect(preset.endingAt, isNull);
        expect(preset.endAt(now), now);
        expect(preset.startAt(now), now.subtract(preset.duration));
      }
    });

    test('are the spans the panel lists, shortest first', () {
      expect(TimelineRange.presets.map((r) => r.label), [
        '5 min',
        '15 min',
        '1 h',
        '6 h',
        '12 h',
        '24 h',
      ]);
    });

    test('end at the ceiling the panel claims', () {
      expect(TimelineRange.presets.last.duration, TimelineRange.maxSpan);
    });
  });

  group('a window pinned at its left edge', () {
    // What arriving from the feed opens: the moment, and the half hour after it as it happens.
    final start = DateTime(2026, 8, 2, 14, 20);
    final since = TimelineRange.since(start, span: const Duration(minutes: 30));

    test('keeps pace with the clock', () {
      // Live, so the repository refreshes its anchor and refetches it — which is the whole
      // difference from a chosen period, and what lets it grow at all.
      expect(since.live, isTrue);
      expect(since.endingAt, isNull);
      expect(since.endAt(now), now);
    });

    test('holds its left edge while it is still growing', () {
      expect(since.startAt(start.add(const Duration(minutes: 4))), start);
      expect(since.startAt(now), start);
      expect(now.difference(since.startAt(now)), const Duration(minutes: 10));
    });

    test('stops growing at its span and slides from there', () {
      final later = start.add(const Duration(hours: 2));

      expect(since.startAt(later), later.subtract(const Duration(minutes: 30)));
      expect(later.difference(since.startAt(later)), since.duration);
    });

    test('two arrivals are two cache entries, and one is stable', () {
      final other = TimelineRange.since(
        start.add(const Duration(minutes: 1)),
        span: const Duration(minutes: 30),
      );

      expect(since.key, isNot(other.key));
      expect(
        since,
        TimelineRange.since(start, span: const Duration(minutes: 30)),
      );
    });
  });

  group('a chosen period', () {
    final from = DateTime(2026, 7, 28, 21);
    final to = DateTime(2026, 7, 28, 23);
    final window = TimelineRange.window(from: from, to: to);

    test('fixes both edges, whatever the clock says', () {
      expect(window.live, isFalse);
      expect(window.endingAt, to);
      expect(window.endAt(now), to);
      expect(window.startAt(now), from);
      expect(window.duration, const Duration(hours: 2));
    });

    test('is none of the presets', () {
      expect(TimelineRange.presets, isNot(contains(window)));
    });

    test('two different periods are two different cache entries', () {
      final other = TimelineRange.window(
        from: DateTime(2026, 7, 27, 21),
        to: DateTime(2026, 7, 27, 23),
      );

      expect(window.key, isNot(other.key));
      expect(window, isNot(other));
      expect(window, TimelineRange.window(from: from, to: to));
    });

    test('holds at a day rather than being asked for verbatim', () {
      // The Server validates no maximum on /coverage, /recordings or /vod.m3u8, so a month would
      // be requested as a month — and a single day is already ~21,600 segments.
      final huge = TimelineRange.window(
        from: DateTime(2026, 1, 1),
        to: DateTime(2026, 8, 1),
      );

      expect(huge.duration, TimelineRange.maxSpan);
      expect(TimelineRange.maxSpan, const Duration(hours: 24));
    });

    test('what survives the clamp is the edge you named last', () {
      final huge = TimelineRange.window(
        from: DateTime(2026, 7, 1),
        to: DateTime(2026, 7, 28, 23),
      );

      expect(huge.endingAt, DateTime(2026, 7, 28, 23));
      expect(huge.startAt(now), DateTime(2026, 7, 27, 23));
    });
  });

  group('the range panel', () {
    /// The panel on a surface wide enough for its two columns, reporting everything it applies.
    Future<List<TimelineRange>> pump(
      WidgetTester tester, {
      TimelineRange range = TimelineRange.hour,
    }) async {
      final applied = <TimelineRange>[];

      await tester.binding.setSurfaceSize(const Size(900, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        MaterialApp(
          home: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 520,
              child: TimelineRangePanel(
                now: now,
                range: range,
                onChanged: applied.add,
                onDone: () {},
              ),
            ),
          ),
        ),
      );

      return applied;
    }

    /// The debounce the two time fields apply behind, so a half-typed time is never fetched.
    Future<void> settle(WidgetTester tester) async {
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pumpAndSettle();
    }

    /// What the two fields read, *From* then *To* — the panel's own account of what is chosen,
    /// which is the only place a choice is shown now that the spans never light.
    (String, String) times(WidgetTester tester) {
      final fields = tester
          .widgetList<EditableText>(find.byType(EditableText))
          .toList();
      return (fields.first.controller.text, fields.last.controller.text);
    }

    testWidgets('a span comes back live, ending at now', (tester) async {
      final applied = await pump(tester);

      await tester.tap(find.text('6 h'));
      await tester.pumpAndSettle();

      expect(applied, [TimelineRange.sixHours]);
      expect(applied.single.live, isTrue);
    });

    testWidgets('and shows itself on the day and the two times', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('6 h'));
      await tester.pumpAndSettle();

      expect(times(tester), ('08:30', '14:30'));
      expect(applied, [
        TimelineRange.sixHours,
      ], reason: 'showing a span as a window is not applying one');
    });

    testWidgets('a span crossing midnight sits on the day it starts', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('24 h'));
      await tester.pumpAndSettle();
      expect(times(tester), ('14:30', '14:30'));

      // 1 Aug 14:30 to 2 Aug 14:30 is already the ceiling, so an hour more of it drags both edges.
      await tester.tap(find.text('+1 h'));
      await tester.pumpAndSettle();

      expect(applied.last.startAt(now), DateTime(2026, 8, 1, 15, 30));
      expect(applied.last.endingAt, DateTime(2026, 8, 2, 15, 30));
    });

    testWidgets('the panel opens on the live range it was given', (
      tester,
    ) async {
      await pump(tester);

      expect(times(tester), ('13:30', '14:30'));
    });

    testWidgets('and on a chosen period as the hours it names', (tester) async {
      await pump(
        tester,
        range: TimelineRange.window(
          from: DateTime(2026, 7, 16, 21),
          to: DateTime(2026, 7, 16, 23),
        ),
      );

      expect(times(tester), ('21:00', '23:00'));
    });

    testWidgets('a day out of a span means the whole of that day', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('6 h'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();

      expect(times(tester), ('00:00', '24:00'));
      expect(applied.last.startAt(now), DateTime(2026, 8, 1));
      expect(applied.last.duration, TimelineRange.maxSpan);
    });

    testWidgets('but the times a span filled in can be built on', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('6 h'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('+1 h'));
      await tester.pumpAndSettle();

      expect(applied.last.startAt(now), DateTime(2026, 8, 2, 8, 30));
      expect(applied.last.endingAt, DateTime(2026, 8, 2, 15, 30));
    });

    testWidgets('offers a week of days, newest first', (tester) async {
      await pump(tester);

      expect(find.text('Today'), findsOneWidget);
      // 2 Aug 2026 is a Sunday, so the strip reads back through Mon 27.
      expect(find.text('Sat'), findsOneWidget);
      expect(find.text('Mon'), findsOneWidget);
      expect(find.text('27'), findsOneWidget);
    });

    testWidgets('a day out of a live range means the whole of that day', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();

      expect(applied.single.startAt(now), DateTime(2026, 8, 1));
      expect(applied.single.endingAt, DateTime(2026, 8, 2));
      expect(applied.single.duration, TimelineRange.maxSpan);
    });

    testWidgets('a day and two times become that window', (tester) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(EditableText).first, '21:00');
      await tester.enterText(find.byType(EditableText).last, '23:00');
      await settle(tester);

      expect(applied.last.startAt(now), DateTime(2026, 8, 1, 21));
      expect(applied.last.endingAt, DateTime(2026, 8, 1, 23));
    });

    testWidgets('an end before the start runs into the small hours', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(EditableText).first, '23:00');
      await tester.enterText(find.byType(EditableText).last, '01:00');
      await settle(tester);

      expect(applied.last.startAt(now), DateTime(2026, 8, 1, 23));
      expect(
        applied.last.endingAt,
        DateTime(2026, 8, 2, 1),
        reason: '23:00 to 01:00 is two hours, not a rejection',
      );
    });

    testWidgets('a time it cannot read applies nothing, and says so', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();
      final sofar = applied.length;

      await tester.enterText(find.byType(EditableText).first, 'half nine');
      await settle(tester);

      expect(find.text('Times read as 21:00 or 9:30.'), findsOneWidget);
      expect(applied.length, sofar);
    });

    testWidgets('a period that has not happened yet is refused', (
      tester,
    ) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Today'));
      await tester.pumpAndSettle();
      final sofar = applied.length;

      // Today at 22:00, against a 14:30 clock.
      await tester.enterText(find.byType(EditableText).first, '22:00');
      await tester.enterText(find.byType(EditableText).last, '23:00');
      await settle(tester);

      expect(find.text('That period has not happened yet.'), findsOneWidget);
      expect(applied.length, sofar);
    });

    testWidgets('whole day is the ceiling exactly', (tester) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(EditableText).first, '09:00');
      await tester.enterText(find.byType(EditableText).last, '11:00');
      await settle(tester);

      await tester.tap(find.text('whole day'));
      await tester.pumpAndSettle();

      expect(applied.last.duration, TimelineRange.maxSpan);
      expect(applied.last.startAt(now), DateTime(2026, 8, 1));
    });

    testWidgets('more than a day holds at a day and says so', (tester) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Sat'));
      await tester.pumpAndSettle();

      // 06:00 to 05:00 runs into the next day: 23 hours, then +1 h asks for 24 and lands on it.
      await tester.enterText(find.byType(EditableText).first, '06:00');
      await tester.enterText(find.byType(EditableText).last, '04:00');
      await settle(tester);
      expect(applied.last.duration, const Duration(hours: 22));

      await tester.tap(find.text('+1 h'));
      await tester.pumpAndSettle();
      expect(applied.last.duration, const Duration(hours: 23));

      await tester.tap(find.text('+1 h'));
      await tester.pumpAndSettle();
      expect(applied.last.duration, TimelineRange.maxSpan);

      await tester.tap(find.text('+1 h'));
      await tester.pumpAndSettle();
      expect(
        applied.last.duration,
        TimelineRange.maxSpan,
        reason: 'past the ceiling the far edge moves and the span does not',
      );
      expect(applied.last.endingAt, DateTime(2026, 8, 2, 7));
    });

    testWidgets('Earlier… swaps only the days for a month', (tester) async {
      await pump(tester);

      expect(find.text('UP TO NOW'), findsOneWidget);
      expect(find.text('A DAY'), findsOneWidget);

      await tester.tap(find.text('Earlier…'));
      await tester.pumpAndSettle();

      expect(find.text('A DAY · EARLIER'), findsOneWidget);
      expect(find.text('August 2026'), findsOneWidget);
      expect(
        find.text('UP TO NOW'),
        findsOneWidget,
        reason: 'the spans and the fields must not move under the pointer',
      );
      expect(find.text('From'), findsOneWidget);
    });

    testWidgets('the month reaches back and stops at today', (tester) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Earlier…'));
      await tester.pumpAndSettle();

      // Forward is already at the current month, so the arrow is dead.
      await tester.tap(find.byIcon(PhosphorIconsRegular.caretRight));
      await tester.pumpAndSettle();
      expect(find.text('August 2026'), findsOneWidget);

      await tester.tap(find.byIcon(PhosphorIconsRegular.caretLeft));
      await tester.pumpAndSettle();
      expect(find.text('July 2026'), findsOneWidget);

      await tester.tap(find.text('16'));
      await tester.pumpAndSettle();

      expect(applied.last.startAt(now), DateTime(2026, 7, 16));
    });

    testWidgets('a day that has not happened cannot be picked', (tester) async {
      final applied = await pump(tester);

      await tester.tap(find.text('Earlier…'));
      await tester.pumpAndSettle();

      // 2 Aug is the clock's day; 3 Aug has not happened.
      await tester.tap(find.text('3'));
      await tester.pumpAndSettle();

      expect(applied, isEmpty);
    });
  });
}
