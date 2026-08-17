// Settings on a phone.
//
// Everything else in this suite runs at 1200px or wider, which is the whole point: those tests
// pin the desktop layout and this one pins the drill-down, at the 412x892 the design draws. The
// two must not be the same file, because "the columns are gone" and "the columns are there" are
// both correct answers depending only on the width.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/router/serval_router.dart';
import 'package:serval_app/screens/settings_index_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_settings_form.dart';
import 'package:serval_app/widgets/compact_app_bar.dart';
import 'package:serval_app/widgets/icon_rail.dart';
import 'package:serval_app/widgets/settings_nav.dart';

void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    // The design's phone. Above `Serval.compactWidth` none of this applies.
    view.physicalSize = const Size(412, 892);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  late GoRouter router;

  /// The real router over the sample repository, the way `router_test.dart` drives it — the
  /// drill-down navigates, so a harness that pumps one screen cannot see it move.
  Future<void> pumpAt(WidgetTester tester, String location) async {
    router = buildServalRouter(auth: null);
    addTearDown(router.dispose);
    router.go(location);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          repositoryProvider.overrideWithValue(const SampleServalRepository()),
          authProvider.overrideWithValue(null),
        ],
        child: MaterialApp.router(
          theme: buildServalTheme(),
          routerConfig: router,
          builder: (context, child) => DefaultTextStyle(
            style: Theme.of(context).textTheme.bodyMedium!,
            child: Scaffold(body: child!),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('the index', () {
    testWidgets('/settings is the drill-down index, not the status page', (
      tester,
    ) async {
      await pumpAt(tester, '/settings');

      expect(find.byType(SettingsIndexScreen), findsOneWidget);
      for (final section in [
        'Server status',
        'Cameras',
        'Server settings',
        'Users & access',
      ]) {
        expect(find.text(section), findsOneWidget);
      }

      // AGPL section 13's offer follows the interface, phone or not.
      expect(find.textContaining('Source ·'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('the rail and the sidebar are gone', (tester) async {
      await pumpAt(tester, '/settings');

      expect(find.byType(IconRail), findsNothing);

      // `SettingsNav` is still here — as the index itself, which is the point of the one list.
      // What must be gone is the 236px column form of it.
      final nav = tester.getSize(find.byType(SettingsNav));
      expect(nav.width, 412);
    });

    testWidgets('a section opens its page', (tester) async {
      await pumpAt(tester, '/settings');
      await tester.tap(find.text('Cameras'));
      await tester.pumpAndSettle();

      expect(router.state.uri.path, '/settings/cameras');
    });
  });

  // The wall's own gear is not exercised here: `/wall` does not lay out at this width at all —
  // its 376px activity column leaves the timeline track under a pixel wide and `_TrackGeometry`
  // throws on the clamp. That is true with or without this work, and the wall on a phone has not
  // been designed. The gear is wired to `/settings` and the index it opens is covered below.

  group('the camera registry', () {
    testWidgets('opens on the list rather than on a camera', (tester) async {
      await pumpAt(tester, '/settings/cameras');

      expect(find.text('Driveway'), findsOneWidget);
      expect(find.byType(CameraSettingsForm), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('picking a camera drills into it, and into the address', (
      tester,
    ) async {
      await pumpAt(tester, '/settings/cameras');
      await tester.tap(find.text('Driveway'));
      await tester.pumpAndSettle();

      expect(router.state.uri.queryParameters['camera'], 'driveway');
      expect(find.byType(CameraSettingsForm), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('?camera= lands straight on the editor', (tester) async {
      await pumpAt(tester, '/settings/cameras?camera=kitchen');

      expect(find.byType(CameraSettingsForm), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('the header action is behind the overflow', (tester) async {
      await pumpAt(tester, '/settings/cameras?camera=kitchen');

      // Not beside the title: it is destructive, and a button this wide would beat the name
      // down to nothing.
      expect(find.text('Remove camera'), findsNothing);

      await tester.tap(find.bySemanticsLabel('More'));
      await tester.pumpAndSettle();

      expect(find.text('Remove camera'), findsOneWidget);
    });

    testWidgets('the back arrow returns to the list', (tester) async {
      await pumpAt(tester, '/settings/cameras?camera=kitchen');
      await tester.tap(find.bySemanticsLabel('Back'));
      await tester.pumpAndSettle();

      expect(find.byType(CameraSettingsForm), findsNothing);
      expect(router.state.uri.queryParameters['camera'], isNull);
    });
  });

  group('the other pages', () {
    testWidgets('each carries a bar back to the index', (tester) async {
      for (final path in [
        '/settings/status',
        '/settings/server',
        '/settings/users',
      ]) {
        await pumpAt(tester, path);

        expect(find.byType(CompactAppBar), findsWidgets, reason: path);
        expect(tester.takeException(), isNull, reason: path);

        await tester.tap(find.bySemanticsLabel('Back').first);
        await tester.pumpAndSettle();
        expect(router.state.uri.path, '/settings', reason: path);
      }
    });

    testWidgets('server settings opens on the groups, not on one of them', (
      tester,
    ) async {
      await pumpAt(tester, '/settings/server');

      // A group's own cards only appear once a group has been picked.
      expect(find.text('Recording'), findsOneWidget);
      expect(find.text('Keep recordings for'), findsNothing);

      await tester.tap(find.text('Recording'));
      await tester.pumpAndSettle();

      expect(find.text('Keep recordings for'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}
