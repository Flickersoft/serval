// What the wall shows before a single camera has been registered, which is the first screen a new
// install renders and the one state the sample content never reaches.
//
// The blank grid it replaces was not a missing feature so much as a missing sentence: an empty
// Stack over an empty timeline, under a header still inviting a tap on tiles that were not there.
// What these pin is that the emptiness explains itself, that it offers the way out only to
// somebody the Server will let through, and that the wall's timeline names the wall rather than a
// camera the reader would have to go looking for.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/auth/auth_models.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// The sample content with nothing in its registry — see the same class in
/// `golden_capture_test.dart` for why the arrangement and the feed have to go too.
class _EmptyRegistry extends SampleServalRepository {
  const _EmptyRegistry();

  @override
  List<Camera> cameras() => const [];

  @override
  List<TileLayout> wallLayout() => const [];

  @override
  List<ActivityItem> activityFor({
    String? cameraId,
    DateTime? asOf,
    TimelineRange? range,
    bool includeAllDetections = false,
  }) => const [];
}

/// The sample content with every camera left registered but none of them recording, which is what
/// separates "nothing is kept" from "nothing is here".
class _NothingRecorded extends SampleServalRepository {
  const _NothingRecorded();

  @override
  List<Camera> cameras() => super
      .cameras()
      .map(
        (c) => Camera(
          id: c.id,
          name: c.name,
          connection: c.connection,
          records: false,
          placeholder: c.placeholder,
        ),
      )
      .toList();
}

/// A session that is signed in and is not an Admin, which is the half of the role split no sample
/// content carries — the sample path has no session at all and so reads as an Admin.
///
/// Every getter here is one the router's redirect or `isAdminProvider` reads; nothing touches the
/// network, and the repository beside it is still the sample one.
class _SignedInViewer extends AuthController {
  _SignedInViewer()
    : super(config: ServalConfig(baseUrl: Uri.parse('http://localhost:8080')));

  @override
  AuthStatus get status => AuthStatus.authenticated;

  @override
  bool get isAuthenticated => true;

  @override
  bool get isRestoring => false;

  @override
  Role get role => Role.viewer;
}

void main() {
  void sized(Size size) {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  group('a wall with no cameras', () {
    testWidgets('says so, and offers the page that fixes it', (tester) async {
      sized(const Size(1440, 900));
      await tester.pumpWidget(const ServalApp(repository: _EmptyRegistry()));
      await tester.pumpAndSettle();

      expect(find.text('No cameras yet'), findsOneWidget);
      expect(find.text('Add a camera'), findsOneWidget);
      expect(
        find.text('No cameras yet — add one under Settings → Cameras.'),
        findsOneWidget,
      );
    });

    // The bar spans nothing and its range control would be choosing between empty days, so it is
    // not drawn at all — which is also what keeps the single-camera sentence about footage off a
    // screen that has no camera to attach it to.
    testWidgets('draws no timeline at all', (tester) async {
      sized(const Size(1440, 900));
      await tester.pumpWidget(const ServalApp(repository: _EmptyRegistry()));
      await tester.pumpAndSettle();

      expect(find.byType(TimelineScrubber), findsNothing);
      expect(find.textContaining('Nothing is kept'), findsNothing);
    });

    testWidgets('offers nothing to arrange', (tester) async {
      sized(const Size(1440, 900));
      await tester.pumpWidget(const ServalApp(repository: _EmptyRegistry()));
      await tester.pumpAndSettle();

      expect(find.byTooltip('Nothing to arrange yet'), findsOneWidget);
      expect(find.byTooltip('Rearrange the wall'), findsNothing);
    });

    // Every camera write is Admin-only on the Server. Naming the action anyway would send a
    // viewer to a page that refuses them, having told them it would not.
    //
    // Through `ServalApp`'s own `auth`, not a `ProviderScope` wrapped around it: the scope
    // `ServalApp` builds is what supplies `repositoryProvider`, and an outer one takes over as
    // the root that the derived providers resolve against, leaving that override unreachable.
    testWidgets('keeps the button from anyone who cannot add a camera', (
      tester,
    ) async {
      sized(const Size(1440, 900));
      final auth = _SignedInViewer();
      addTearDown(auth.dispose);
      await tester.pumpWidget(
        ServalApp(repository: const _EmptyRegistry(), auth: auth),
      );
      await tester.pumpAndSettle();

      expect(find.text('No cameras yet'), findsOneWidget);
      expect(find.text('Add a camera'), findsNothing);
      expect(
        find.textContaining('An administrator can add them'),
        findsOneWidget,
      );
    });

    testWidgets('reaches the same state on a phone', (tester) async {
      sized(const Size(412, 892));
      await tester.pumpWidget(const ServalApp(repository: _EmptyRegistry()));
      await tester.pumpAndSettle();

      expect(find.text('No cameras yet'), findsOneWidget);
      expect(find.text('Add a camera'), findsOneWidget);
    });
  });

  // The guard the other direction: the empty state must not fire for a wall that has cameras,
  // including the case that looks most like emptiness from the timeline's side.
  testWidgets('a populated wall is untouched', (tester) async {
    sized(const Size(1440, 900));
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    expect(find.text('No cameras yet'), findsNothing);
    expect(find.byType(TimelineScrubber), findsOneWidget);
    expect(
      find.text('Every camera at once. Tap a tile to open it.'),
      findsOneWidget,
    );
  });

  // Six cameras, none of them recording, is not zero cameras — the bar stays, and what it says
  // names the wall. "This camera" here would send the reader hunting for which one.
  testWidgets('a wall that records nothing names the wall, not a camera', (
    tester,
  ) async {
    sized(const Size(1440, 900));
    await tester.pumpWidget(const ServalApp(repository: _NothingRecorded()));
    await tester.pumpAndSettle();

    expect(find.byType(TimelineScrubber), findsOneWidget);
    expect(
      find.text('Nothing is being recorded — there is no footage to replay'),
      findsOneWidget,
    );
    expect(find.textContaining('this camera'), findsNothing);
  });
}
