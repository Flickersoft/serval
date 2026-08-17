import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/widgets/camera_tile.dart';

/// The wall's edit mode, driven through the real app.
///
/// The arithmetic of a drop lives in `wall_layout_test.dart`; what is pinned
/// here is that the wall does not open in edit mode, and that a drag on the
/// screen actually reaches that arithmetic.
void main() {
  // The design is drawn at 1440x900, and the rail, activity column and detail
  // panel hold fixed widths at that size.
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1440, 900);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  Finder tileFor(String cameraId) => find.byWidgetPredicate(
    (w) => w is CameraTile && w.camera.id == cameraId,
    description: 'the $cameraId tile',
  );

  // The control is a glyph, so its tooltip is the only text it has.
  final rearrangeButton = find.byTooltip('Rearrange the wall');
  final doneButton = find.byTooltip('Done rearranging');

  /// One column and one row in pixels, read off the wall rather than
  /// recomputed — the sample layout puts front door and kitchen three columns
  /// apart on row 0, and back yard one row below the front door.
  (double, double) stepsOf(WidgetTester tester) {
    final frontDoor = tester.getTopLeft(tileFor('front-door'));
    return (
      (tester.getTopLeft(tileFor('kitchen')).dx - frontDoor.dx) / 3,
      tester.getTopLeft(tileFor('back-yard')).dy - frontDoor.dy,
    );
  }

  /// The middle of a tile's drag grip. A move starts here and nowhere else —
  /// the body of a tile is not a drag surface.
  Offset gripOf(WidgetTester tester, String cameraId) =>
      tester.getTopLeft(tileFor(cameraId)) + const Offset(18, 18);

  testWidgets('the wall opens settled, not in edit mode', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    expect(rearrangeButton, findsOneWidget);
    expect(doneButton, findsNothing);

    expect(
      find.byIcon(PhosphorIconsRegular.dotsSixVertical),
      findsNothing,
      reason: 'no tile should be wearing a drag grip on load',
    );

    // An arrangement is written as it is made; there is no save step, and a
    // button offering one would be lying about where the layout lives.
    expect(find.text('Save layout'), findsNothing);
  });

  testWidgets('the rearrange glyph turns the grips on, and back off', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(rearrangeButton);
    await tester.pumpAndSettle();

    expect(doneButton, findsOneWidget);
    expect(find.byIcon(PhosphorIconsRegular.dotsSixVertical), findsWidgets);

    // And back again.
    await tester.tap(doneButton);
    await tester.pumpAndSettle();
    expect(rearrangeButton, findsOneWidget);
    expect(find.byIcon(PhosphorIconsRegular.dotsSixVertical), findsNothing);
  });

  testWidgets('a tile dragged into free space lands there', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(rearrangeButton);
    await tester.pumpAndSettle();

    final (_, stepY) = stepsOf(tester);
    final before = tester.getTopLeft(tileFor('garage'));

    // The garage sits at the right of the second row; the third row below it is
    // empty, which is what the sample layout leaves free on purpose.
    await tester.dragFrom(gripOf(tester, 'garage'), Offset(0, stepY));
    await tester.pumpAndSettle();

    final after = tester.getTopLeft(tileFor('garage'));
    expect(after.dx, moreOrLessEquals(before.dx, epsilon: 0.5));
    expect(after.dy, moreOrLessEquals(before.dy + stepY, epsilon: 0.5));
  });

  testWidgets('a drop onto a tile too big to mirror still lands', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(rearrangeButton);
    await tester.pumpAndSettle();

    final (stepX, _) = stepsOf(tester);
    final drivewayWas = tester.getRect(tileFor('driveway'));

    // Onto the driveway, which is the six-wide hero. Mirroring it into the
    // garage's single cell is impossible, so it is re-placed rather than the
    // drop being thrown away.
    await tester.dragFrom(gripOf(tester, 'garage'), Offset(-9 * stepX, 0));
    await tester.pumpAndSettle();

    expect(
      tester.getTopLeft(tileFor('garage')).dx,
      moreOrLessEquals(drivewayWas.left, epsilon: 0.5),
      reason: 'the garage took the column the driveway was in',
    );

    final drivewayNow = tester.getRect(tileFor('driveway'));
    expect(
      drivewayNow.size.width,
      moreOrLessEquals(drivewayWas.size.width, epsilon: 0.5),
      reason: 're-placed, not resized',
    );
    expect(drivewayNow.overlaps(tester.getRect(tileFor('garage'))), isFalse);
  });

  testWidgets('a two-row tile can reach the first empty row', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(rearrangeButton);
    await tester.pumpAndSettle();

    final (_, stepY) = stepsOf(tester);
    final before = tester.getTopLeft(tileFor('driveway'));

    // The driveway is the two-row hero, and row 3 — below the offline side path
    // — is the first empty row. Clamping a drop by the tile's bottom edge used
    // to stop it a row short, so a tall tile could never be put below
    // everything else.
    await tester.dragFrom(gripOf(tester, 'driveway'), Offset(0, 3 * stepY));
    await tester.pumpAndSettle();

    final after = tester.getTopLeft(tileFor('driveway'));
    expect(after.dx, moreOrLessEquals(before.dx, epsilon: 0.5));
    expect(after.dy, moreOrLessEquals(before.dy + 3 * stepY, epsilon: 0.5));
  });

  testWidgets('dragging the body of a tile moves nothing', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.tap(rearrangeButton);
    await tester.pumpAndSettle();

    final (_, stepY) = stepsOf(tester);
    final before = tester.getTopLeft(tileFor('garage'));

    // The same drag that works from the grip. Only the grip starts a move, so
    // the picture itself stays a picture.
    await tester.drag(tileFor('garage'), Offset(0, stepY));
    await tester.pumpAndSettle();

    expect(tester.getTopLeft(tileFor('garage')), before);
  });

  testWidgets('tapping a tile opens it only when the wall is settled', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    await tester.tap(tileFor('garage'));
    await tester.pumpAndSettle();
    expect(
      find.text('Home · live view'),
      findsNothing,
      reason: 'a tap on a settled wall opens the single-camera view',
    );
  });
}
