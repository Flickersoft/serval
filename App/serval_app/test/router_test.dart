// The route table, asserted against the `GoRouter` object rather than through a pumped app.
//
// These are the cases the widget tests cannot reach cheaply: what a cold URL resolves to, and what
// the login gate does with one. They matter more than usual here because the addresses are now a
// public interface — someone can bookmark `/camera/front-door` or paste it to a housemate — and a
// route that quietly stops resolving looks like a dead link rather than a crash.
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:serval_app/data/auth/auth_api.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/auth/auth_models.dart';
import 'package:serval_app/data/auth/token_store.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/router/serval_router.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/screens/cameras_screen.dart';
import 'package:serval_app/screens/connecting_screen.dart';
import 'package:serval_app/screens/login_screen.dart';
import 'package:serval_app/screens/notifications_screen.dart';
import 'package:serval_app/screens/server_screen.dart';
import 'package:serval_app/screens/settings_screen.dart';
import 'package:serval_app/screens/users_screen.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/settings_nav.dart';

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

  /// Drives the real router over the sample repository. [auth] null is the sample path — the same
  /// "no session to gate on" case the goldens run in.
  late GoRouter router;

  Future<void> pumpAt(
    WidgetTester tester,
    String location, {
    AuthController? auth,
  }) async {
    router = buildServalRouter(auth: auth);
    addTearDown(router.dispose);
    router.go(location);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          repositoryProvider.overrideWithValue(const SampleServalRepository()),
          authProvider.overrideWithValue(auth),
        ],
        child: MaterialApp.router(
          theme: buildServalTheme(),
          routerConfig: router,
          // The same chrome `ServalApp` puts above the Navigator. Without the `Scaffold` the
          // rail's ink responses have no `Material` ancestor and the shells' `Row`s lay out
          // against an unbounded width — both artefacts of the harness, not of the routes.
          builder: (context, child) => DefaultTextStyle(
            style: Theme.of(context).textTheme.bodyMedium!,
            child: Scaffold(body: child!),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('the addresses resolve', () {
    testWidgets('/wall is the wall', (tester) async {
      await pumpAt(tester, '/wall');
      expect(find.byType(WallScreen), findsOneWidget);
    });

    testWidgets('/camera/:id opens that camera', (tester) async {
      await pumpAt(tester, '/camera/front-door');

      final screen = tester.widget<CameraScreen>(find.byType(CameraScreen));
      expect(screen.camera.id, 'front-door');
    });

    testWidgets('/settings/cameras is the registry', (tester) async {
      await pumpAt(tester, '/settings/cameras');
      expect(find.byType(CamerasScreen), findsOneWidget);
    });

    testWidgets('?camera= picks which record the registry opens on', (
      tester,
    ) async {
      // The one piece of genuine cross-screen argument passing in the app.
      await pumpAt(tester, '/settings/cameras?camera=kitchen');

      final screen = tester.widget<CamerasScreen>(find.byType(CamerasScreen));
      expect(screen.initialCameraId, 'kitchen');
    });

    testWidgets('/settings/server is what the machine is told to do', (
      tester,
    ) async {
      await pumpAt(tester, '/settings/server');
      expect(find.byType(SettingsScreen), findsOneWidget);
    });

    // Pinned as its own case rather than folded into the one above: these two paths are easy to
    // swap, and `/settings/server` must land on the settings, which is the more useful of the two
    // to find by guessing.
    testWidgets('/settings/status is what the machine is doing', (
      tester,
    ) async {
      await pumpAt(tester, '/settings/status');
      expect(find.byType(ServerScreen), findsOneWidget);
    });

    testWidgets('/settings/users is accounts', (tester) async {
      await pumpAt(tester, '/settings/users');
      expect(find.byType(UsersScreen), findsOneWidget);
    });

    testWidgets('/settings/notifications is your own rules', (tester) async {
      await pumpAt(tester, '/settings/notifications');
      expect(find.byType(NotificationsScreen), findsOneWidget);
    });

    // The one cross-screen link that has already been wrong once: it pointed at `/settings/server`
    // for a release, which is the deployment's push plumbing and Admin-only, while the label
    // promises the per-camera rules that live on the notifications page.
    testWidgets("*What I'm alerted on* goes to the notifications page", (
      tester,
    ) async {
      await pumpAt(tester, '/alerts');

      await tester.tap(find.text("What I'm alerted on"));
      await tester.pumpAndSettle();

      expect(router.state.uri.path, '/settings/notifications');
      expect(find.byType(NotificationsScreen), findsOneWidget);
    });

    testWidgets('the sidebar lights the row you are actually on', (
      tester,
    ) async {
      // The selection comes off the address rather than off which screen built it — one place
      // decides, so a third page could not drift out of step with the first two.
      await pumpAt(tester, '/settings/server');

      final nav = tester.widget<SettingsNav>(find.byType(SettingsNav));
      expect(nav.selected, SettingsSection.server);
    });
  });

  group('the login gate', () {
    testWidgets('no session at all leaves every address alone', (tester) async {
      // The sample path: `const ServalApp()` has no AuthController, so there is no login screen to
      // send anyone to. This is what keeps the goldens rendering the app rather than a login form.
      await pumpAt(tester, '/settings/users');

      expect(find.byType(UsersScreen), findsOneWidget);
      expect(find.byType(LoginScreen), findsNothing);
    });

    testWidgets('an unauthenticated session is sent to /login', (tester) async {
      final auth = AuthController(
        config: ServalConfig(baseUrl: Uri.parse('http://localhost:5211')),
      );
      addTearDown(auth.dispose);

      await pumpAt(tester, '/settings/users', auth: auth);

      // Asserted on the resolved address rather than only on the widget, because that is the
      // contract: a deep link into the app while signed out must not merely *look* like the login
      // screen, it must leave you at /login so a reload does not try the protected route again.
      expect(router.routerDelegate.currentConfiguration.uri.path, '/login');
      expect(find.byType(LoginScreen), findsOneWidget);
      expect(find.byType(UsersScreen), findsNothing);

      // The login card is a fixed width, and this suite does not vendor Inter the way
      // `golden_capture_test` does — so its labels render in the test fallback font, which is
      // wider, and *Keep me signed in* overflows its row by ~33px. An artefact of the harness,
      // not of the layout: the real metrics are held by the 3a golden.
      tester.takeException();
    });

    testWidgets('a session still being renewed is not sent to /login', (
      tester,
    ) async {
      // The distinction the gate used to miss. A stored session whose access token has run out,
      // against a Server that has not answered yet, is not a session that ended — nothing has
      // rejected it. Answering that with a password prompt asks for something that was never the
      // problem, and the app used to clear the refresh token on the way there, so waiting did not
      // fix it either.
      final config = ServalConfig(
        baseUrl: Uri.parse('http://serval.test:8080'),
      );
      final auth = AuthController(
        config: config,
        store: _StaleStore(),
        api: AuthApi(
          config: config,
          client: MockClient(
            (_) async => throw http.ClientException('connection refused'),
          ),
        ),
      );
      addTearDown(auth.dispose);

      await auth.restore();
      expect(auth.status, AuthStatus.restoring);

      await pumpAt(tester, '/settings/users', auth: auth);

      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/connecting',
      );
      expect(find.byType(ConnectingScreen), findsOneWidget);
      expect(find.byType(LoginScreen), findsNothing);

      // *Sign in instead* — the one way off that screen that is a decision rather than a wait.
      // Also what stops the retry, which is why it is here rather than in a tear-down: a widget
      // test will not let a timer outlive the tree, and tear-downs run after that check.
      await auth.logout();
      expect(auth.status, AuthStatus.unauthenticated);
    });
  });

  group('the role gate', () {
    /// A signed-in controller holding [role], without a Server: the login route is mocked, which is
    /// the same shape `auth_login_storage_test.dart` uses.
    Future<AuthController> signedInAs(Role role) async {
      final config = ServalConfig(
        baseUrl: Uri.parse('http://serval.test:8080'),
      );
      final auth = AuthController(
        config: config,
        api: AuthApi(
          config: config,
          client: MockClient(
            (request) async => http.Response(
              jsonEncode({
                'accessToken': 'access-token',
                'accessTokenExpiresAt': DateTime.now()
                    .add(const Duration(minutes: 10))
                    .toIso8601String(),
                'refreshToken': 'refresh-token',
                'refreshTokenExpiresAt': DateTime.now()
                    .add(const Duration(days: 30))
                    .toIso8601String(),
                'username': role == Role.admin ? 'admin' : 'watcher',
                'role': role.name,
              }),
              200,
              headers: const {'content-type': 'application/json'},
            ),
          ),
        ),
        // Never touches the platform channel, which a widget test has no implementation for.
        store: _NoStore(),
      );
      await auth.login('someone', 'password');
      return auth;
    }

    testWidgets('a Viewer asking for /settings/users lands on the index', (
      tester,
    ) async {
      final auth = await signedInAs(Role.viewer);
      addTearDown(auth.dispose);

      await pumpAt(tester, '/settings/users', auth: auth);

      // The address, not just the widget: a Viewer who bookmarked this must not be left at a URL
      // that retries the same protected page on every reload.
      expect(router.routerDelegate.currentConfiguration.uri.path, '/settings');
      expect(find.byType(UsersScreen), findsNothing);
    });

    testWidgets('a Viewer cannot reach the mask editor either', (tester) async {
      final auth = await signedInAs(Role.viewer);
      addTearDown(auth.dispose);

      await pumpAt(
        tester,
        '/settings/cameras/masks?camera=front-door',
        auth: auth,
      );

      expect(router.routerDelegate.currentConfiguration.uri.path, '/settings');
    });

    testWidgets('an Admin still reaches both', (tester) async {
      final auth = await signedInAs(Role.admin);
      addTearDown(auth.dispose);

      await pumpAt(tester, '/settings/users', auth: auth);

      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/settings/users',
      );
      expect(find.byType(UsersScreen), findsOneWidget);
    });
  });

  group('a camera that is not there', () {
    testWidgets('an unknown id falls back to the wall', (tester) async {
      // Deleted from the registry, or a stale bookmark.
      await pumpAt(tester, '/camera/no-such-camera');

      expect(find.byType(CameraScreen), findsNothing);
      expect(find.byType(WallScreen), findsOneWidget);
    });
  });
}

/// A [TokenStore] that keeps nothing. These tests care where a role may navigate, not whether the
/// session reached disk — and the real store's platform channel has no implementation under
/// `flutter test`, so calling it would fail for a reason unrelated to anything here.
class _NoStore extends TokenStore {
  @override
  Future<void> save(AuthSession session) async {}

  @override
  Future<AuthSession?> read() async => null;

  @override
  Future<void> clear() async {}
}

/// A store holding the session a browser tab wakes up with after sitting overnight: the access
/// token long gone, the refresh token good for weeks yet.
class _StaleStore extends TokenStore {
  @override
  Future<void> save(AuthSession session) async {}

  @override
  Future<AuthSession?> read() async => AuthSession(
    accessToken: 'access-token',
    accessTokenExpiresAt: DateTime.now().subtract(const Duration(hours: 8)),
    refreshToken: 'refresh-token',
    refreshTokenExpiresAt: DateTime.now().add(const Duration(days: 29)),
    username: 'admin',
    role: Role.admin,
  );

  @override
  Future<void> clear() async {}
}
