// The column draws a bounded number of rows, and the counts stay whole.
//
// Both halves matter and they pull opposite ways. The drawing is bounded because the feed lays out
// eagerly and collapsing the panel rebuilds every row on the next expand. The counts are *not*
// bounded, because a capped pool is what made the filter panel read 0 for a kind the house had
// produced all day — leaving nothing to filter to, which is the one thing the panel is for.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/widgets/activity_column.dart';

void main() {
  final now = DateTime(2026, 8, 5, 18);

  ActivityItem item(int i, {bool speech = false}) => ActivityItem(
    id: 'a$i',
    kind: speech ? TelemetryKind.utterance : TelemetryKind.scene,
    cameraId: speech ? 'kitchen' : 'front-door',
    cameraName: speech ? 'Kitchen' : 'Front door',
    at: now.subtract(Duration(minutes: i)),
    timeLabel: '$i min ago',
    text: 'event number $i',
    icon: ActivityIcon.scene,
    isSpeech: speech,
  );

  /// [count] rows, newest first, as the repository hands them over.
  List<ActivityItem> feed(int count) => [
    for (var i = 0; i < count; i++) item(i),
  ];

  Widget host(List<ActivityItem> items) => Directionality(
    textDirection: TextDirection.ltr,
    child: Align(
      alignment: Alignment.topLeft,
      child: SizedBox(
        width: 376,
        height: 800,
        child: ActivityFeed(items: items),
      ),
    ),
  );

  testWidgets('a short feed draws every row and says nothing', (tester) async {
    await tester.pumpWidget(host(feed(12)));
    await tester.pumpAndSettle();

    expect(find.textContaining('event number'), findsNWidgets(12));
    expect(find.textContaining('Showing the newest'), findsNothing);
  });

  testWidgets('a long feed draws the cap and no more', (tester) async {
    await tester.pumpWidget(host(feed(ActivityFeed.maxRows + 40)));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('event number'),
      findsNWidgets(ActivityFeed.maxRows),
    );
  });

  testWidgets('the rows it keeps are the newest ones', (tester) async {
    // Off the oldest end, always. The top of the column is what you came to read.
    await tester.pumpWidget(host(feed(ActivityFeed.maxRows + 40)));
    await tester.pumpAndSettle();

    expect(find.textContaining('event number 0'), findsOneWidget);
    expect(
      find.textContaining('event number ${ActivityFeed.maxRows + 20}'),
      findsNothing,
    );
  });

  testWidgets('a capped feed says how many matched, not how many drew', (
    tester,
  ) async {
    final total = ActivityFeed.maxRows + 40;
    await tester.pumpWidget(host(feed(total)));
    await tester.pumpAndSettle();

    expect(
      find.textContaining(
        'Showing the newest ${ActivityFeed.maxRows} of $total',
      ),
      findsOneWidget,
    );
  });

  test('the counts are taken off the pool, never off what is drawn', () {
    // The regression this whole cap is designed around. `ActivityFacets` is built by the screen
    // over the full pool and handed to the column; the cap lives inside the feed's own build and
    // cannot reach it. A panel reporting only the visible slice would be useless for deciding what
    // to filter *to*.
    final pool = [
      for (var i = 0; i < ActivityFeed.maxRows + 40; i++)
        item(i, speech: i.isEven),
    ];

    final facets = ActivityFacets.of(pool);

    expect(facets.total, ActivityFeed.maxRows + 40);
    expect(
      facets.kinds[ActivityKind.speech],
      greaterThan(ActivityFeed.maxRows ~/ 2),
      reason: 'speech past the cut still counts towards the facet',
    );
    expect(facets.cameras['kitchen'], isNot(0));
  });
}
