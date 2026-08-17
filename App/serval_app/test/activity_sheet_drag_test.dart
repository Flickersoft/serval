// The tray, dragged while the screen around it is rebuilding.
//
// This is the case the rest of the tray suites miss, because they drag with `tester.dragFrom` and
// nothing else happens in between. On a phone plenty else happens: an alert lands, a detection
// heartbeat arrives at 2Hz, the playhead ticks once a second, the head remeasures, the phone turns.
// Every one of those rebuilds the sheet, and the re-target in `ActivitySheet.build` used to read a
// gesture in progress as a tray knocked off its detent and put it back — a frame after every move,
// so the tray slid to the floor under a finger that was pulling it up.
//
// What is pinned here is that a rebuild mid-drag changes nothing. The drag owns the height until it
// ends. The re-target is deliberately left free to run when no finger is down, which is what the
// resize tests in `camera_compact_test` cover.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/activity_sheet.dart';

void main() {
  const repository = SampleServalRepository();

  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  Camera cameraNamed(String id) =>
      repository.cameras().firstWhere((c) => c.id == id);

  Widget harness(Widget child) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: child),
    ),
  );

  /// The tray's own box — the rounded surface, not the full-height stack it sits in.
  Rect trayBox(WidgetTester tester) => tester.getRect(
    find
        .descendant(
          of: find.byType(ActivitySheet),
          matching: find.byType(ClipRRect),
        )
        .first,
  );

  double trayHeight(WidgetTester tester) => trayBox(tester).height;

  /// What an arriving alert or detection does to the tray, reduced to its one relevant effect:
  /// something above the sheet rebuilds, so the sheet is rebuilt too.
  ///
  /// Driven directly rather than through the repository because the sample one is immutable — and
  /// the trigger is not the point. Any rebuild at all used to be enough.
  void rebuildAbove(WidgetTester tester, Type screen) =>
      tester.element(find.byType(screen)).markNeedsBuild();

  /// Drags the grabber up in steps, rebuilding the screen between each one.
  ///
  /// Returns the height after every step, so a test can say where it went wrong rather than only
  /// that it did.
  /// The first move of a gesture is spent winning the arena and moves the tray by nothing, so a
  /// drag of [steps] is worth [steps] - 1 of them.
  double dragged({required int steps, required double each}) =>
      (steps - 1) * each;

  Future<List<double>> dragUpWhileRebuilding(
    WidgetTester tester,
    Type screen, {
    int steps = 4,
    double each = 40,
  }) async {
    final box = trayBox(tester);
    final gesture = await tester.startGesture(
      Offset(box.center.dx, box.top + 9),
    );
    final heights = <double>[];

    for (var i = 0; i < steps; i++) {
      await gesture.moveBy(Offset(0, -each));
      await tester.pump();

      rebuildAbove(tester, screen);
      // Two pumps: one to build, one for the post-frame callback the build used to schedule.
      await tester.pump();
      await tester.pump();

      heights.add(trayHeight(tester));
    }

    await gesture.up();
    await tester.pumpAndSettle();
    return heights;
  }

  group('a tray dragged while the screen rebuilds', () {
    testWidgets('keeps the height the finger gave it, on a camera', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(
        harness(CameraScreen(camera: cameraNamed('front-door'), onBack: () {})),
      );
      await tester.pumpAndSettle();

      final resting = trayHeight(tester);
      final heights = await dragUpWhileRebuilding(tester, CameraScreen);

      // Every step is taller than the one before it. Before the fix the rebuild put the tray back
      // on its detent and the next step measured from there again, so this list was flat.
      for (var i = 2; i < heights.length; i++) {
        expect(
          heights[i],
          greaterThan(heights[i - 1]),
          reason:
              'step $i lost the drag to a rebuild: $heights, resting $resting',
        );
      }

      // Exactly what was dragged, and not merely most of it: a rebuild that ate one frame's delta
      // and let the rest through would still climb, so *some* growth is too weak a claim to pin
      // this on.
      expect(heights.last - resting, closeTo(dragged(steps: 4, each: 40), 0.5));
    });

    testWidgets('and on the wall, which has no sheet controller', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(harness(WallScreen(onOpenCamera: (_, _) {})));
      await tester.pumpAndSettle();

      final resting = trayHeight(tester);
      final heights = await dragUpWhileRebuilding(tester, WallScreen);

      // The wall is the harder case: with no controller to compare detents against, `resized` was
      // `null != detents` — true forever — so the re-target fired on every rebuild it ever had,
      // whether or not anything had resized.
      for (var i = 2; i < heights.length; i++) {
        expect(
          heights[i],
          greaterThan(heights[i - 1]),
          reason:
              'step $i lost the drag to a rebuild: $heights, resting $resting',
        );
      }

      expect(heights.last - resting, closeTo(dragged(steps: 4, each: 40), 0.5));
    });

    testWidgets('and lets go of the correction once the finger comes off', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(
        harness(CameraScreen(camera: cameraNamed('front-door'), onBack: () {})),
      );
      await tester.pumpAndSettle();

      final resting = trayHeight(tester);
      final heights = await dragUpWhileRebuilding(tester, CameraScreen);
      final settled = trayHeight(tester);

      // The guard holds the correction off for the gesture, not for good. Released a third of the
      // way to a `raised` that is the whole ceiling here, the nearest detent is still the one it
      // started from — so the snap ran, which is the proof the flag was cleared.
      expect(settled, isNot(closeTo(heights.last, 0.5)));
      expect(settled, closeTo(resting, 0.5));

      rebuildAbove(tester, CameraScreen);
      await tester.pump();
      await tester.pump();

      // And a rebuild arriving after it has settled moves nothing, because there is now nothing to
      // correct — the tray is on its detent.
      expect(trayHeight(tester), closeTo(settled, 0.5));
    });
  });
}
