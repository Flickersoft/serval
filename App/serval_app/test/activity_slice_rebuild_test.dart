// Who wakes up when the feed moves.
//
// The repository used to be one `ChangeNotifier` for everything it holds, and both screens
// subscribed to it at their root. A detection episode publishes a position heartbeat twice a second
// per camera for as long as the thing is in view, so on a phone with movement on three cameras the
// whole tree — picture, transport, tiles, tray — was rebuilt six times a second to redraw a feed.
//
// `ServalRepository.activityChanges` is that signal split off, and `activityRevisionProvider` is
// what a widget watches to get it. What is pinned here is the half of the split that is easy to
// lose: that the things which do *not* read the pool are not rebuilt by it. Firing the slice and
// checking the feed updated would pass just as well with the old whole-tree notification.
//
// The probe is widget identity. Flutter constructs a new widget instance every time a builder runs,
// so a subtree that was not rebuilt hands back the object it handed back before, and `identical` is
// the question "did this build again" asked without counting anything.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_repository.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/activity_column.dart';
import 'package:serval_app/widgets/activity_panel.dart';
import 'package:serval_app/widgets/camera_tile.dart';
import 'package:serval_app/widgets/compact_app_bar.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// The sample content, with a feed that can be said to have moved.
///
/// The sample repository's own slices never fire — its fixtures are `const` — which is right for it
/// and useless here. This one gives the activity slice a real notifier so a test can push the thing
/// that used to rebuild everything.
class _FiringRepository extends SampleServalRepository {
  _FiringRepository();

  final _activity = RepositorySlice();

  @override
  Listenable get activityChanges => _activity;

  /// What a detection heartbeat does to the repository, with the fetching left out.
  void detectionArrived() => _activity.changed();
}

void main() {
  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  Widget harness(_FiringRepository repository, Widget child) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: child),
    ),
  );

  /// The single widget of type [T], as an object to compare against itself later.
  Widget only(WidgetTester tester, Type type) =>
      tester.widget(find.byType(type).first);

  group('a detection arriving', () {
    testWidgets('rebuilds the camera screen feed and nothing above it', (
      tester,
    ) async {
      final repository = _FiringRepository();
      sizeTo(tester, const Size(412, 892));

      final camera = repository.cameras().firstWhere(
        (c) => c.id == 'front-door',
      );
      await tester.pumpWidget(
        harness(repository, CameraScreen(camera: camera, onBack: () {})),
      );
      await tester.pumpAndSettle();

      final feedBefore = only(tester, CameraActivity);
      final scrubberBefore = only(tester, TimelineScrubber);
      final barBefore = only(tester, CompactAppBar);

      repository.detectionArrived();
      await tester.pump();

      // The feed heard it.
      expect(
        identical(only(tester, CameraActivity), feedBefore),
        isFalse,
        reason: 'the feed did not rebuild, so the slice is not reaching it',
      );

      // So did the track. Its marks are the same documents the column lists, so a detection that
      // reaches one and not the other leaves the two disagreeing about the same window — which is
      // what happened when the feed was moved onto its own slice and the track was left behind on
      // a notification that no longer fired.
      expect(
        identical(only(tester, TimelineScrubber), scrubberBefore),
        isFalse,
        reason: 'the track did not rebuild, so live marks stop appearing on it',
      );

      // The app bar did not. This is the assertion that fails if anything puts a
      // `ref.watch(activityRevisionProvider)` back at the top of the screen, or restores the
      // whole-repository notification on the document path.
      //
      // The scrubber is deliberately *not* the probe here, tempting though it looks: its marks are
      // the feed drawn along a line, so it is a feed reader and is meant to rebuild. What saves it
      // from costing anything is `timelineFor` handing back an identical window when no mark moved.
      expect(
        identical(only(tester, CompactAppBar), barBefore),
        isTrue,
        reason: 'the app bar rebuilt, so more than the feed is listening',
      );
    });

    testWidgets('rebuilds the wall feed and leaves the tiles alone', (
      tester,
    ) async {
      final repository = _FiringRepository();
      sizeTo(tester, const Size(1440, 900));

      await tester.pumpWidget(
        harness(repository, WallScreen(onOpenCamera: (_, _) {})),
      );
      await tester.pumpAndSettle();

      final feedBefore = only(tester, ActivityColumn);
      final tileBefore = only(tester, CameraTile);

      repository.detectionArrived();
      await tester.pump();

      expect(
        identical(only(tester, ActivityColumn), feedBefore),
        isFalse,
        reason: 'the column did not rebuild, so the slice is not reaching it',
      );

      // The wall is the one that used to be worst: `_onRepositoryChanged` called `setState` with
      // no key, so a heartbeat relaid out every tile on the wall.
      expect(
        identical(only(tester, CameraTile), tileBefore),
        isTrue,
        reason: 'a tile rebuilt, so the wall is still listening too widely',
      );
    });
  });
}
