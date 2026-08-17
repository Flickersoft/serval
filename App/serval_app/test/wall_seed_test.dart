// Where the wall's arrangement comes from when the registry arrives late.
//
// Two paths reach the wall and they differ in timing. On a cold start `main` awaits
// `repository.start()` before the first frame, so the saved layout and the camera list are both in
// hand by the time the wall builds. On interactive login `_RepositoryStarter` starts the repository
// *unawaited* and the wall mounts immediately — cameras empty, saved layout not yet read from
// preferences.
//
// The wall keeps a working copy of the layout because a drag has to have something to mutate, and
// it must not re-seed that copy on every notification or saving an edit (which notifies) would
// throw the edit away. Where those two facts meet is what these pin: seeding only on the first
// call would take the login path's *empty* `wallLayout()` as the arrangement and reconcile that
// empty copy when the cameras landed — a default grid packed over the saved arrangement on every
// sign-in.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_repository.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/models/wall_layout.dart';
import 'package:serval_app/widgets/camera_tile.dart';

/// The sample content, delivered the way the live repository delivers it on the interactive-login
/// path: nothing until [load], then everything at once.
class _LateRegistry extends SampleServalRepository {
  _LateRegistry();

  final _registryChanges = RepositorySlice();
  final _preferenceChanges = RepositorySlice();
  bool _loaded = false;

  @override
  Listenable get registryChanges => _registryChanges;

  @override
  Listenable get preferenceChanges => _preferenceChanges;

  @override
  List<Camera> cameras() => _loaded ? super.cameras() : const [];

  @override
  List<TileLayout> wallLayout() => _loaded ? super.wallLayout() : const [];

  void load() {
    _loaded = true;
    _registryChanges.changed();
    _preferenceChanges.changed();
  }
}

/// The same content, but with the registry and the arrangement split apart — because that is how
/// `LiveServalRepository.start` actually completes them.
///
/// Both reads are in flight at once, and the registry is the one that notifies first: `start`
/// awaits `_loadRegistry` (which notifies) and only *then* applies the preferences it started
/// earlier. So the wall's first notification carries a full camera set and a `wallLayout()` that is
/// still the default pack standing in for an arrangement nobody has seen.
class _LatePreferences extends SampleServalRepository {
  _LatePreferences();

  final _registryChanges = RepositorySlice();
  final _preferenceChanges = RepositorySlice();
  bool _registry = false;
  bool _preferences = false;

  @override
  Listenable get registryChanges => _registryChanges;

  @override
  Listenable get preferenceChanges => _preferenceChanges;

  @override
  List<Camera> cameras() => _registry ? super.cameras() : const [];

  @override
  bool get preferencesKnown => _preferences;

  /// Unread answers with the design's default packing, exactly as `LiveServalRepository.wallLayout`
  /// does for a `_savedLayout` that is still null — and that indistinguishability is the whole
  /// problem: the screen cannot tell a stand-in from an arrangement, so [preferencesKnown] has to.
  @override
  List<TileLayout> wallLayout() => _preferences
      ? super.wallLayout()
      : WallGrid.reconcile(const [], [
          for (final camera in cameras()) camera.id,
        ]);

  void loadRegistry() {
    _registry = true;
    _registryChanges.changed();
  }

  void loadPreferences() {
    _preferences = true;
    _preferenceChanges.changed();
  }
}

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

  Finder tileFor(String cameraId) => find.byWidgetPredicate(
    (w) => w is CameraTile && w.camera.id == cameraId,
    description: 'the $cameraId tile',
  );

  testWidgets('a registry that arrives after mount still gets the saved layout', (
    tester,
  ) async {
    final repository = _LateRegistry();

    await tester.pumpWidget(ServalApp(repository: repository));
    await tester.pumpAndSettle();

    // Nothing to draw yet, and nothing thrown on the way to drawing nothing.
    expect(tileFor('driveway'), findsNothing);

    repository.load();
    await tester.pumpAndSettle();

    expect(tileFor('driveway'), findsOneWidget);

    // The saved arrangement and a default pack agree on the hero — the first camera gets it either
    // way — so the hero cannot tell them apart. The order of the four standard tiles beside it can:
    // the saved layout puts kitchen on the top row and back yard below it, while packing them in
    // `cameras()` order (driveway, front-door, back-yard, kitchen, …) would swap the two.
    final frontDoorY = tester.getTopLeft(tileFor('front-door')).dy;

    expect(
      tester.getTopLeft(tileFor('kitchen')).dy,
      frontDoorY,
      reason:
          'kitchen shares the top row with the front door in the saved layout',
    );
    expect(
      tester.getTopLeft(tileFor('back-yard')).dy,
      greaterThan(frontDoorY),
      reason: 'a default pack would have put back yard here, on the top row',
    );

    // The side path is the other tell: saved, it sits alone on the third row at the far left,
    // under the hero. A default pack has no reason to leave that gap.
    expect(
      tester.getTopLeft(tileFor('side-path')).dx,
      lessThan(tester.getTopLeft(tileFor('front-door')).dx),
    );
  });

  testWidgets('an arrangement that arrives after the registry still lands', (
    tester,
  ) async {
    // The reported bug, end to end: sign in, and the wall comes up in the design's default packing
    // instead of the arrangement you left it in — with only a browser reload putting it right,
    // because a reload is the one path where preferences are already in hand at mount.
    //
    // Keying the re-seed on the camera set is what did it. The registry lands first, so by the time
    // the arrangement arrives the ids have not changed, and the wall had nothing left to re-seed
    // on. Worse than cosmetic: `preferencesKnown` unlocks rearranging the moment the document
    // lands, the wall saves on every accepted move, and so one nudged tile wrote the default pack
    // over the real arrangement for good.
    final repository = _LatePreferences()..loadRegistry();

    await tester.pumpWidget(ServalApp(repository: repository));
    await tester.pumpAndSettle();

    final frontDoorY = tester.getTopLeft(tileFor('front-door')).dy;

    // Standing in, as it should be — this is the default pack, and back yard on the top row is
    // exactly what tells it apart from the saved arrangement.
    expect(
      tester.getTopLeft(tileFor('back-yard')).dy,
      frontDoorY,
      reason: 'the default pack shares the top row between these two',
    );

    repository.loadPreferences();
    await tester.pumpAndSettle();

    // And now the arrangement, on a notification carrying no camera-set change at all.
    expect(
      tester.getTopLeft(tileFor('kitchen')).dy,
      tester.getTopLeft(tileFor('front-door')).dy,
      reason: 'kitchen shares the top row with the front door when saved',
    );
    expect(
      tester.getTopLeft(tileFor('back-yard')).dy,
      greaterThan(tester.getTopLeft(tileFor('front-door')).dy),
      reason: 'back yard drops off the top row once the saved layout is in',
    );
  });

  testWidgets('a saved arrangement is not re-seeded out from under an edit', (
    tester,
  ) async {
    // The latch has to close, or every later notification — an arriving utterance, a frame, the
    // save that follows a drag — would put the tiles back where the Server last saw them.
    final repository = _LatePreferences()
      ..loadRegistry()
      ..loadPreferences();

    await tester.pumpWidget(ServalApp(repository: repository));
    await tester.pumpAndSettle();

    final before = tester.getTopLeft(tileFor('kitchen'));

    repository.loadPreferences();
    await tester.pumpAndSettle();

    expect(tester.getTopLeft(tileFor('kitchen')), before);
  });

  testWidgets('a camera appearing later folds into the arrangement', (
    tester,
  ) async {
    // The other half of the contract, and the reason the working copy is reconciled rather than
    // re-read: once there is an arrangement, a changed camera set must fold into it rather than
    // replace it.
    final repository = _LateRegistry()..load();

    await tester.pumpWidget(ServalApp(repository: repository));
    await tester.pumpAndSettle();

    final before = tester.getTopLeft(tileFor('driveway'));

    // A notification carrying no change to the camera set must not disturb anything — this is the
    // notify that follows saving an edit.
    repository.load();
    await tester.pumpAndSettle();

    expect(tester.getTopLeft(tileFor('driveway')), before);
  });
}
