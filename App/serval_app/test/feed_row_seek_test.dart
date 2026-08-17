// Clicking a row on the single-camera view, where there is nowhere to route to.
//
// The wall's column answers a click by opening a camera; here you are already on it, so the panel
// hands back an instant and the screen seeks the picture that is already up. Same rule for *when*
// — see `replayStartFor` — and a different thing done with it, which is exactly the pair worth
// pinning: the day these two disagree, one of them is silently wrong.
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/widgets/activity_panel.dart';

ActivityItem _row(DateTime at) => ActivityItem(
  id: 'r1',
  kind: TelemetryKind.sound,
  cameraId: 'front-door',
  cameraName: 'Front door',
  at: at,
  timeLabel: 'heard earlier',
  text: 'Glass heard',
  icon: ActivityIcon.alarm,
  label: 'Glass',
);

Widget _app(List<ActivityItem> items, {ValueChanged<DateTime?>? onSeek}) =>
    Directionality(
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
              onSeek: onSeek,
            ),
          ),
        ),
      ),
    );

void main() {
  setUp(() => TestWidgetsFlutterBinding.ensureInitialized());

  testWidgets('a row takes the picture to its moment', (tester) async {
    await tester.binding.setSurfaceSize(const Size(500, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final at = DateTime.now().subtract(const Duration(minutes: 12));
    var fired = false;
    DateTime? sought;

    await tester.pumpWidget(
      _app(
        [_row(at)],
        onSeek: (instant) {
          fired = true;
          sought = instant;
        },
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Glass heard'));
    await tester.pumpAndSettle();

    expect(fired, isTrue);
    expect(sought, at.subtract(feedLeadIn));
  });

  testWidgets('a row at the live edge asks for the live view', (tester) async {
    await tester.binding.setSurfaceSize(const Size(500, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    var fired = false;
    DateTime? sought;

    await tester.pumpWidget(
      _app(
        [_row(DateTime.now())],
        onSeek: (instant) {
          fired = true;
          sought = instant;
        },
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Glass heard'));
    await tester.pumpAndSettle();

    expect(fired, isTrue);
    expect(
      sought,
      isNull,
      reason: 'null is the screen going back to live, not a seek to nowhere',
    );
  });

  testWidgets('a camera that keeps nothing has inert rows', (tester) async {
    // The screen passes no callback at all when the camera records nothing, and the rows have to
    // go quiet rather than draw a hover and swallow the click.
    await tester.binding.setSurfaceSize(const Size(500, 600));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      _app([_row(DateTime.now().subtract(const Duration(minutes: 12)))]),
    );
    await tester.pumpAndSettle();

    expect(find.text('Glass heard'), findsOneWidget);
    expect(
      find.ancestor(
        of: find.text('Glass heard'),
        matching: find.byType(GestureDetector),
      ),
      findsNothing,
      reason: 'no tap target around a row with no footage behind it',
    );

    // And the tap itself lands on nothing rather than raising anything.
    await tester.tap(find.text('Glass heard'));
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
  });
}
