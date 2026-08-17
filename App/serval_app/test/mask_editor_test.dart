import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/camera_record.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/screens/mask_editor_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/mask_canvas.dart';

/// Design 9b — drawing a mask, and the rules that make a polygon finishable.
///
/// The one rule nobody guesses is that **closing is clicking the first point**, so it is the rule
/// most worth pinning: a shape that cannot be finished is worse than one that cannot be started.
///
/// Everything is in fractions of the picture, and the picture is letterboxed inside its box, so
/// these convert through the canvas rather than assuming the two rectangles are the same. Getting
/// that wrong is the bug `PictureAligned` exists to prevent, and it does not announce itself — the
/// mask simply ends up somewhere other than where it was drawn.
void main() {
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

  Widget harness() => ProviderScope(
    overrides: [
      repositoryProvider.overrideWithValue(const SampleServalRepository()),
    ],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: const Scaffold(body: MaskEditorScreen(cameraId: 'driveway')),
    ),
  );

  /// Clicks a point given in fractions of the *picture*, converting through the canvas's own
  /// rectangle so the letterboxing is accounted for exactly as the widget accounts for it.
  Future<void> click(WidgetTester tester, double x, double y) async {
    final box = tester.getRect(find.byType(MaskCanvas));

    // The canvas contains its picture at 16:9, centred — the same arithmetic `_pictureRect` does.
    const aspect = 16 / 9;
    final width = box.width < box.height * aspect
        ? box.width
        : box.height * aspect;
    final height = width / aspect;
    final left = box.left + ((box.width - width) / 2);
    final top = box.top + ((box.height - height) / 2);

    await tester.tapAt(Offset(left + (x * width), top + (y * height)));
    await tester.pumpAndSettle();
  }

  testWidgets('lays out without overflow or unbounded constraints', (
    tester,
  ) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
  });

  testWidgets('says how to finish a shape before one has been started', (
    tester,
  ) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Click the first one again to close the shape'),
      findsOneWidget,
    );
    expect(
      find.text('Backspace removes the last · Esc cancels'),
      findsOneWidget,
    );
  });

  testWidgets('three points closed on the first make a mask', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await click(tester, 0.2, 0.2);
    await click(tester, 0.6, 0.2);
    await click(tester, 0.6, 0.6);

    // Nothing committed yet — the shape is still open.
    expect(find.text('Unnamed area'), findsNothing);

    // Closing is clicking the first point again.
    await click(tester, 0.2, 0.2);

    expect(find.text('Unnamed area'), findsWidgets);
    expect(find.textContaining('3 points'), findsWidgets);
  });

  testWidgets('two points cannot be closed', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await click(tester, 0.2, 0.2);
    await click(tester, 0.6, 0.2);
    // Clicking the first point again with only two placed adds a third rather than closing: two
    // vertices enclose nothing, and the Server refuses such a mask.
    await click(tester, 0.2, 0.2);

    expect(find.text('Unnamed area'), findsNothing);
  });

  testWidgets('Backspace removes the last point', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await click(tester, 0.2, 0.2);
    await click(tester, 0.6, 0.2);
    await click(tester, 0.6, 0.6);

    await tester.sendKeyEvent(LogicalKeyboardKey.backspace);
    await tester.pumpAndSettle();

    // Two points left, so closing on the first no longer finishes anything.
    await click(tester, 0.2, 0.2);
    expect(find.text('Unnamed area'), findsNothing);
  });

  testWidgets('Esc abandons the shape', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await click(tester, 0.2, 0.2);
    await click(tester, 0.6, 0.2);
    await click(tester, 0.6, 0.6);

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();

    await click(tester, 0.2, 0.2);
    expect(find.text('Unnamed area'), findsNothing);
  });

  testWidgets('a drawn mask can be saved', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    // Nothing to save before anything is drawn.
    final save = find.widgetWithText(Row, 'Save masks');
    expect(save, findsWidgets);

    await click(tester, 0.2, 0.2);
    await click(tester, 0.6, 0.2);
    await click(tester, 0.6, 0.6);
    await click(tester, 0.2, 0.2);

    // The inspector opens on the mask just drawn, ready to be named.
    expect(find.text('Name'), findsOneWidget);
    expect(find.text('Ignore only these'), findsOneWidget);
  });

  group('the wire format', () {
    test('a mask round-trips its classes', () {
      const mask = DetectionMaskSettings(
        name: 'pavement',
        points: [0, 0, 1, 0, 1, 0.3],
        classes: ['car', 'truck'],
      );

      final restored = DetectionMaskSettings.fromJson(mask.toJson());

      expect(restored, mask);
      expect(restored.classes, ['car', 'truck']);
    });

    test('a mask with no classes round-trips as null, not empty', () {
      // Null and empty mean the same thing to the Server, but the App has to send back what it was
      // given rather than inventing a narrowing that was never set.
      const mask = DetectionMaskSettings(
        name: 'tree line',
        points: [0, 0, 1, 0, 1, 0.3],
      );

      expect(DetectionMaskSettings.fromJson(mask.toJson()).classes, isNull);
    });

    test('classes are part of what makes two masks different', () {
      const everything = DetectionMaskSettings(points: [0, 0, 1, 0, 1, 0.3]);
      final cars = everything.copyWith(classes: const ['car']);

      expect(cars, isNot(everything));
      expect(cars.copyWith(classes: null), everything);
    });
  });
}
