// Pinch-to-zoom, wired into the single-camera screen.
//
// `picture_zoom_test.dart` pins what the transform does to the picture and the boxes over it. This
// pins that the picture on this screen is actually inside one — at all three sizes, because the
// three layouts are three separate compositions and a wrapper is easy to add to two of them — and
// that the chrome floating above it still gets its own gestures.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/picture_zoom.dart';
import 'package:serval_app/widgets/ptz_pad.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

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

  Widget camera(String id) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(
        body: CameraScreen(camera: cameraNamed(id), onBack: () {}),
      ),
    ),
  );

  /// Two fingers moving apart on the picture.
  ///
  /// A third of the way down rather than the middle of it: on a phone turned on its side the
  /// controls reach most of the way up a 412px screen, and a pinch begun on the scrubber or the
  /// caption is that control's gesture rather than the picture's. Clear of the pills along the top
  /// too.
  Future<void> pinch(WidgetTester tester, double factor) async {
    final picture = tester.getRect(find.byType(PictureZoom));
    final centre =
        picture.topLeft + Offset(picture.width / 2, picture.height / 3);
    const reach = Offset(40, 0);

    final left = await tester.startGesture(centre - reach);
    final right = await tester.startGesture(centre + reach);

    for (var step = 1; step <= 8; step++) {
      final at = reach * (1 + (factor - 1) * step / 8);
      await left.moveTo(centre - at);
      await right.moveTo(centre + at);
      await tester.pump();
    }

    await left.up();
    await right.up();
    await tester.pumpAndSettle();
  }

  // 412x892 is the phone upright, 892x412 the same phone turned, and 1440x900 the desktop this
  // must reach as well — the three compositions from `camera_compact_test.dart`.
  for (final (label, size) in const [
    ('the phone held upright', Size(412, 892)),
    ('the phone turned', Size(892, 412)),
    ('the desktop', Size(1440, 900)),
  ]) {
    testWidgets('$label can zoom the picture', (tester) async {
      sizeTo(tester, size);
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(find.byType(PictureZoom), findsOneWidget);

      await pinch(tester, 2);

      // The readout is the only outward sign that the scene on screen is not the whole scene, so
      // its absence would be the failure that matters rather than a missing transform.
      expect(find.textContaining('×'), findsOneWidget);
    });
  }

  testWidgets('a zoom does not follow you to the next camera', (tester) async {
    sizeTo(tester, const Size(1440, 900));
    await tester.pumpWidget(camera('front-door'));
    await tester.pumpAndSettle();

    await pinch(tester, 2);
    expect(find.textContaining('×'), findsOneWidget);

    // The screen is reused across a switch — only the camera changes — so nothing tears the
    // transform down on its own. Another camera pointing somewhere else at whatever magnification
    // the last one was left at is a scene nobody chose.
    await tester.pumpWidget(camera('driveway'));
    await tester.pumpAndSettle();

    expect(find.textContaining('×'), findsNothing);
  });

  testWidgets('the chrome over the picture keeps its own gestures', (
    tester,
  ) async {
    sizeTo(tester, const Size(412, 892));
    await tester.pumpWidget(camera('front-door'));
    await tester.pumpAndSettle();

    // The corner sits directly on the band. A tap that reached the picture underneath it instead
    // would be a phone with no way to make the picture bigger.
    await tester.tap(find.bySemanticsLabel('Fill the screen'));
    await tester.pumpAndSettle();
    expect(
      find.byType(PtzPad),
      findsOneWidget,
      reason: 'not the immersive stage',
    );

    // And the scrubber, which is the sharper case: it drags horizontally across the bottom of a
    // picture that now drags too. A scrub that panned the scene instead would strand you in live.
    Matrix4 matrix() => tester
        .widget<InteractiveViewer>(find.byType(InteractiveViewer))
        .transformationController!
        .value;

    await tester.drag(find.byType(TimelineScrubber), const Offset(-80, 0));
    await tester.pumpAndSettle();

    expect(matrix(), Matrix4.identity());
    expect(tester.takeException(), isNull);
  });
}
