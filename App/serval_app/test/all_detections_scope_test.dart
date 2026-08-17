// Where the *All detections* switch lives, and therefore what it can reach.
//
// It is a troubleshooting instrument for the camera in front of you — "what is the detector
// actually seeing here" — and it used to be a preference: per camera, per account, stored in Mongo
// and synced to every device. Two things followed from that which nobody asked for. It survived
// leaving the screen, so a switch flipped once to look at a misbehaving drive was still on a week
// later. And the wall read the same repository state, so turning it on for one camera changed a
// screen that has no such control on it.
//
// Both are properties of *where the flag is held*, so that is what these pin: it is a field on the
// route, passed into each read, and there is nothing in the repository for another screen to find.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/talk_controls.dart';

void main() {
  const repository = SampleServalRepository();

  Camera cameraNamed(String id) =>
      repository.cameras().firstWhere((c) => c.id == id);

  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  /// The desktop composition, which is the one that draws the control in a labelled row.
  Widget app({required Widget home}) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: home),
    ),
  );

  Finder toggle() => find.ancestor(
    of: find.text('All detections'),
    matching: find.byType(VideoToggle),
  );

  bool isOn(WidgetTester tester) => tester.widget<VideoToggle>(toggle()).active;

  testWidgets('starts off, and the press turns it on', (tester) async {
    sizeTo(tester, const Size(1440, 900));

    await tester.pumpWidget(
      app(
        home: CameraScreen(camera: cameraNamed('front-door'), onBack: () {}),
      ),
    );
    await tester.pump();

    expect(isOn(tester), isFalse);

    await tester.tap(toggle());
    await tester.pump();

    expect(isOn(tester), isTrue);
  });

  testWidgets('leaving the screen clears it', (tester) async {
    // The whole reason it stopped being a preference. It is held on the route's State, so this is
    // really an assertion that nothing outlives the route — but that is exactly what regressed
    // before, by someone reaching for the repository to hold it.
    sizeTo(tester, const Size(1440, 900));

    final navigator = GlobalKey<NavigatorState>();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [repositoryProvider.overrideWithValue(repository)],
        child: MaterialApp(
          debugShowCheckedModeBanner: false,
          theme: buildServalTheme(),
          navigatorKey: navigator,
          home: Scaffold(
            body: Builder(
              builder: (context) => ElevatedButton(
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute<void>(
                    builder: (context) => Scaffold(
                      body: CameraScreen(
                        camera: cameraNamed('front-door'),
                        onBack: () => Navigator.of(context).pop(),
                      ),
                    ),
                  ),
                ),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );

    Future<void> openTheCamera() async {
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
    }

    await openTheCamera();
    await tester.tap(toggle());
    await tester.pump();
    expect(isOn(tester), isTrue);

    navigator.currentState!.pop();
    await tester.pumpAndSettle();

    await openTheCamera();
    expect(
      isOn(tester),
      isFalse,
      reason: 'coming back to the camera should be the ordinary view again',
    );
  });

  testWidgets('the repository holds nothing for another screen to read', (
    tester,
  ) async {
    // The wall's guarantee, asserted where it can actually be broken. Every repository read the
    // toggle feeds takes it as an argument with a false default, so a screen that does not mention
    // it — the wall does not — cannot be widened by one that does.
    sizeTo(tester, const Size(1440, 900));

    await tester.pumpWidget(
      app(
        home: CameraScreen(camera: cameraNamed('front-door'), onBack: () {}),
      ),
    );
    await tester.pump();

    await tester.tap(toggle());
    await tester.pump();
    expect(isOn(tester), isTrue);

    // Read the way the wall reads it: no flag, same instant, same repository.
    expect(repository.detectionsFor('front-door').length, 2);
    expect(repository.activityFor().isNotEmpty, isTrue);
  });

  testWidgets('the phone reaches it through the immersive controls', (
    tester,
  ) async {
    // Portrait has no control row of its own, so the landscape/immersive chrome is the way in. Its
    // toggle is the same flag, not a second one.
    sizeTo(tester, const Size(844, 390));

    await tester.pumpWidget(
      app(
        home: CameraScreen(camera: cameraNamed('front-door'), onBack: () {}),
      ),
    );
    await tester.pump();

    final overlay = find.byWidgetPredicate(
      (widget) =>
          widget is Tooltip &&
          widget.message == 'All detections' &&
          widget.child != null,
    );

    expect(overlay, findsOneWidget);
  });
}
