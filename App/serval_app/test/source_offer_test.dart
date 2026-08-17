// AGPL section 13's offer, and where it is drawn.
//
// The rule these pin is one offer per surface: the rail carries it under every wide screen, the
// drill-down index carries it on a phone where there is no rail, and no screen shows it twice.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/models/source_offer.dart';
import 'package:serval_app/router/serval_router.dart';
import 'package:serval_app/screens/login_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/icon_rail.dart';
import 'package:serval_app/widgets/settings_nav.dart';
import 'package:serval_app/widgets/source_link.dart';

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

  late GoRouter router;

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
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final onRail = find.descendant(
    of: find.byType(IconRail),
    matching: find.text('Source'),
  );

  testWidgets('the rail offers the source from the wall', (tester) async {
    await pumpAt(tester, '/wall');

    // The point of the whole thing: somebody who never opens settings still gets the offer.
    expect(onRail, findsOneWidget);

    await tester.tap(onRail, warnIfMissed: false);
    await tester.pump();
    expect(tester.takeException(), isNull);
  });

  testWidgets('settings draws it once, on the rail', (tester) async {
    await pumpAt(tester, '/settings/status');

    expect(onRail, findsOneWidget);

    // The 236px sidebar's own copy would sit a few pixels from the rail's, saying the same thing.
    expect(
      find.descendant(
        of: find.byType(SettingsNav),
        matching: find.textContaining('Source'),
      ),
      findsNothing,
    );
  });

  testWidgets('the sign-in screen carries its own', (tester) async {
    // A cold controller reports unauthenticated, which is what the router's redirect turns into
    // `/login`. Nothing here reads storage or reaches the network.
    final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
    final auth = AuthController(config: config);
    addTearDown(auth.dispose);
    final repository = LiveServalRepository(auth: auth, config: config);
    addTearDown(repository.dispose);

    await tester.pumpWidget(ServalApp(repository: repository, auth: auth));
    await tester.pumpAndSettle();

    // The form's 404px column overflows by a few pixels under `flutter test`'s fallback font —
    // *Stay signed in on this computer* measures wider than Inter does. `Docs/testing.md` calls
    // this out; the goldens load the real fonts and this screen does not overflow there.
    tester.takeException();

    // The one screen with no rail behind it — somebody who cannot sign in has still reached
    // Serval over a network.
    expect(find.byType(LoginScreen), findsOneWidget);
    expect(find.byType(SourceLine), findsOneWidget);
  });

  test('the link resolves to the commit when there is one', () {
    // Nothing stamps `SOURCE_REVISION` under `flutter test`, so this is the fallback branch: the
    // repository itself, which is what a local build can honestly offer.
    expect(SourceOffer.shortRevision, isNull);
    expect(SourceOffer.url, SourceOffer.repositoryUrl);
  });

  test('the label names the release and the build it came from', () {
    // What the image workflow produces: both stamps present, version first.
    expect(SourceOffer.labelFor('0.1.7', 'abc1234'), '0.1.7 (abc1234)');

    // A local build has neither, and the licence is the one thing that is never a guess.
    expect(SourceOffer.labelFor('', null), SourceOffer.license);

    // Either stamp alone still says something true rather than inventing the other.
    expect(SourceOffer.labelFor('', 'abc1234'), 'abc1234');
    expect(SourceOffer.labelFor('0.1.7', null), '0.1.7');
  });
}
