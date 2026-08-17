// The feed reads newest-first, which means every new row is inserted *above* what you are
// reading. Without holding the scroll offset that silently walks the row under your eyes off the
// bottom of the panel — a bug you only see on a camera that is actually busy, which is to say
// never in a golden and never in the sample data.
//
// This was the transcript panel's test before the two tabs merged. The panel changed; the hazard
// did not, because both were newest-first lists behind a TopAnchoredScrollView.
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/widgets/activity_panel.dart';
import 'package:serval_app/widgets/top_anchored_scroll_view.dart';

ActivityItem _item(int i) => ActivityItem(
  id: 'i$i',
  kind: TelemetryKind.scene,
  cameraId: 'front-door',
  cameraName: 'Front door',
  at: DateTime(2026, 8, 5, 16, i),
  timeLabel: '4:${i.toString().padLeft(2, '0')} pm',
  text: 'Row number $i, long enough to take a line or two of the panel.',
  icon: ActivityIcon.person,
);

/// Newest first, as the repository returns them.
List<ActivityItem> _feed(int newest) => [
  for (var i = newest; i >= 1; i--) _item(i),
];

Widget _app(List<ActivityItem> items) => Directionality(
  textDirection: TextDirection.ltr,
  child: MediaQuery(
    data: const MediaQueryData(),
    child: Align(
      alignment: Alignment.topLeft,
      child: SizedBox(
        height: 600,
        child: ActivityPanel(
          activity: items,
          filter: ActivityFilter.none,
          facets: ActivityFacets.of(items),
        ),
      ),
    ),
  ),
);

/// The topmost row currently painted, and where it sits.
(int, double) _visibleRow(WidgetTester tester) {
  for (var i = 60; i >= 1; i--) {
    final finder = find.text(
      'Row number $i, long enough to take a line or two of the panel.',
    );
    if (finder.evaluate().isNotEmpty) return (i, tester.getTopLeft(finder).dy);
  }
  fail('no row was on screen');
}

void main() {
  setUp(() => TestWidgetsFlutterBinding.ensureInitialized());

  testWidgets('a new row does not move the row you are reading', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(500, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(_app(_feed(20)));
    await tester.drag(
      find.byType(SingleChildScrollView),
      const Offset(0, -400),
    );
    await tester.pumpAndSettle();

    final (marker, before) = _visibleRow(tester);

    // Two rows arrive at the top.
    await tester.pumpWidget(_app([_item(22), _item(21), ..._feed(20)]));
    await tester.pump();
    await tester.pump();

    final finder = find.text(
      'Row number $marker, long enough to take a line or two of the panel.',
    );
    expect(
      finder,
      findsOneWidget,
      reason: 'the row being read should still be on screen',
    );
    expect(
      tester.getTopLeft(finder).dy,
      moreOrLessEquals(before, epsilon: 0.5),
      reason: 'and should not have moved',
    );
  });

  testWidgets('at the top, a new row simply appears at the top', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(500, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(_app(_feed(20)));
    await tester.pumpAndSettle();

    // Not scrolled: row 20 is the newest and is what you are looking at.
    expect(_visibleRow(tester).$1, 20);

    await tester.pumpWidget(_app([_item(21), ..._feed(20)]));
    await tester.pump();
    await tester.pump();

    expect(
      _visibleRow(tester).$1,
      21,
      reason: 'the newest row should be the one on screen, without scrolling',
    );
    // Scoped to the feed: the header's search field is an [EditableText], which carries a
    // Scrollable of its own, so the bare type finder now matches two.
    final position = tester
        .state<ScrollableState>(
          find.descendant(
            of: find.byType(TopAnchoredScrollView),
            matching: find.byType(Scrollable),
          ),
        )
        .position;
    expect(
      position.maxScrollExtent,
      greaterThan(0),
      reason: 'sanity: the feed overflows, so staying put was a choice',
    );
    expect(position.pixels, 0, reason: 'and the panel is still at the top');
  });
}
