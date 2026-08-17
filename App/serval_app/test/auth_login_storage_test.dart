// Signing in must not depend on the session reaching disk.
//
// The bug these pin: on a web build served over plain HTTP, `flutter_secure_storage_web` throws
// `UnsupportedError` (it refuses any origin that is not a secure context — `localhost` is one,
// a plain `http://` origin is not). `AuthController.login` awaited that write inside its own
// try/catch, so a login that had already returned 200 with a valid token was reported to the user
// as "Could not reach the Server." — the one thing it definitively was not, since `UnsupportedError`
// is an `Error` rather than an `Exception` and fell through to the network-flavoured fallback text.
//
// Nothing here needs a browser: an `Error` thrown from the store reproduces it exactly, and
// `TokenStore`'s own fallback is exercised through a secure storage that always throws.
import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:serval_app/data/auth/auth_api.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/auth/auth_models.dart';
import 'package:serval_app/data/auth/token_store.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://serval.test:8080'));

  /// The body `POST /api/auth/login` answers with — the 200 that was already arriving when the
  /// App claimed it could not reach the Server.
  String loginBody() => jsonEncode({
    'accessToken': 'access-token',
    'accessTokenExpiresAt': DateTime.now()
        .add(const Duration(minutes: 10))
        .toIso8601String(),
    'refreshToken': 'refresh-token',
    'refreshTokenExpiresAt': DateTime.now()
        .add(const Duration(days: 30))
        .toIso8601String(),
    'username': 'admin',
    'role': 'admin',
  });

  AuthApi apiReturningLogin() => AuthApi(
    config: config,
    client: MockClient(
      (request) async => http.Response(
        loginBody(),
        200,
        headers: const {'content-type': 'application/json'},
      ),
    ),
  );

  AuthSession session() =>
      AuthSession.fromJson(jsonDecode(loginBody()) as Map<String, dynamic>);

  /// A stored session with the two expiries set where the case under test needs them, since what
  /// `restore` does is almost entirely a function of those two clocks.
  AuthSession stored({
    required Duration accessIn,
    Duration refreshIn = const Duration(days: 30),
    String refreshToken = 'token-a',
  }) => AuthSession(
    accessToken: 'access-token',
    accessTokenExpiresAt: DateTime.now().add(accessIn),
    refreshToken: refreshToken,
    refreshTokenExpiresAt: DateTime.now().add(refreshIn),
    username: 'admin',
    role: Role.admin,
  );

  String refreshBody({String refreshToken = 'token-next'}) => jsonEncode({
    'accessToken': 'access-next',
    'accessTokenExpiresAt': DateTime.now()
        .add(const Duration(minutes: 10))
        .toIso8601String(),
    'refreshToken': refreshToken,
    'refreshTokenExpiresAt': DateTime.now()
        .add(const Duration(days: 30))
        .toIso8601String(),
    'username': 'admin',
    'role': 'admin',
  });

  http.Response okJson(String body) => http.Response(
    body,
    200,
    headers: const {'content-type': 'application/json'},
  );

  group('AuthController', () {
    test('login succeeds when the store cannot persist the session', () async {
      final auth = AuthController(
        config: config,
        api: apiReturningLogin(),
        store: _ThrowingStore(),
      );
      addTearDown(auth.dispose);

      final result = await auth.login('admin', 'admin12345');

      expect(result, isTrue);
      expect(auth.status, AuthStatus.authenticated);
      expect(auth.isAuthenticated, isTrue);
      expect(auth.username, 'admin');
      expect(auth.error, isNull);
    });

    test(
      'restore lands on unauthenticated when the store cannot be read',
      () async {
        final auth = AuthController(
          config: config,
          api: apiReturningLogin(),
          store: _ThrowingStore(),
        );
        addTearDown(auth.dispose);

        // `main` awaits this before `runApp`, so throwing here is a blank app, not a login screen.
        await expectLater(auth.restore(), completes);
        expect(auth.status, AuthStatus.unauthenticated);
        expect(auth.isAuthenticated, isFalse);
      },
    );

    test('a failed login still reports the Server\'s own wording', () async {
      final auth = AuthController(
        config: config,
        api: AuthApi(
          config: config,
          client: MockClient(
            (request) async => http.Response(
              jsonEncode({'error': 'Incorrect username or password.'}),
              401,
              headers: const {'content-type': 'application/json'},
            ),
          ),
        ),
        store: _ThrowingStore(),
      );
      addTearDown(auth.dispose);

      final result = await auth.login('admin', 'wrong');

      expect(result, isFalse);
      expect(auth.status, AuthStatus.unauthenticated);
      // The point of the assertion: a real failure keeps its own message rather than being
      // swallowed by the non-Exception fallback, which claims the Server was unreachable.
      expect(auth.error, contains('Incorrect username or password.'));
      expect(auth.error, isNot(contains('Could not reach')));
    });
  });

  // What a cold start does with a session it found in storage.
  //
  // The bug these pin is the one that put people back on the login screen for no reason:
  // `restore` refreshed unconditionally and caught *everything* in one arm, so a
  // `ClientException` from a Server that was still starting cleared the store exactly as a 401
  // would — throwing away a thirty-day credential over a blip, and asking for a password that was
  // never the problem. Reaching the Server at all on this path is now the exception rather than
  // the rule, and only the Server saying no ends a session.
  group('AuthController.restore', () {
    /// The controller, its store, and every refresh the Server was asked for.
    ({AuthController auth, _ScriptedStore store, List<String> asked}) subject({
      required List<AuthSession?> reads,
      required Future<http.Response> Function(String refreshToken, int attempt)
      onRefresh,
    }) {
      final asked = <String>[];
      final store = _ScriptedStore(reads);
      final auth = AuthController(
        config: config,
        store: store,
        api: AuthApi(
          config: config,
          client: MockClient((request) async {
            final token =
                (jsonDecode(request.body)
                        as Map<String, dynamic>)['refreshToken']
                    as String;
            asked.add(token);
            return onRefresh(token, asked.length - 1);
          }),
        ),
      );
      addTearDown(auth.dispose);
      return (auth: auth, store: store, asked: asked);
    }

    test('a still-good access token never touches the network', () async {
      final it = subject(
        reads: [stored(accessIn: const Duration(minutes: 10))],
        onRefresh: (_, _) async => fail('restore asked the Server for nothing'),
      );

      await it.auth.restore();

      expect(it.auth.status, AuthStatus.authenticated);
      // The whole point: a reload landing inside the access token's life is a cold start that does
      // not depend on the Server being up at all. `ensureFreshAccessToken` renews it later.
      expect(it.asked, isEmpty);
      expect(it.store.clears, 0);
    });

    test('an expired refresh token ends the session without asking', () async {
      final it = subject(
        reads: [
          stored(
            accessIn: const Duration(minutes: -30),
            refreshIn: const Duration(days: -1),
          ),
        ],
        onRefresh: (_, _) async => fail('nothing can be traded for this'),
      );

      await it.auth.restore();

      expect(it.auth.status, AuthStatus.unauthenticated);
      expect(it.asked, isEmpty);
      expect(it.store.clears, 1);
    });

    test('a Server that cannot be reached keeps the session', () async {
      final it = subject(
        reads: [stored(accessIn: const Duration(minutes: -1))],
        onRefresh: (_, _) async =>
            throw http.ClientException('connection refused'),
      );

      await it.auth.restore();
      await pumpEventQueue();

      // Not `unauthenticated`, and above all not cleared — this is the whole fix. The session is
      // still good; the Server is the thing that is not there.
      expect(it.auth.status, AuthStatus.restoring);
      expect(it.auth.isRestoring, isTrue);
      expect(it.store.clears, 0);
    });

    test('a Server that says no ends the session', () async {
      final it = subject(
        reads: [stored(accessIn: const Duration(minutes: -1))],
        onRefresh: (_, _) async => http.Response('{}', 401),
      );

      await it.auth.restore();
      await pumpEventQueue();

      expect(it.auth.status, AuthStatus.unauthenticated);
      expect(it.store.clears, 1);
    });

    test('the retry settles once the Server answers', () async {
      final it = subject(
        reads: [stored(accessIn: const Duration(minutes: -1))],
        onRefresh: (_, attempt) async => attempt == 0
            ? throw http.ClientException('connection refused')
            : okJson(refreshBody()),
      );

      await it.auth.restore();
      await pumpEventQueue();
      expect(it.auth.status, AuthStatus.restoring);
      expect(it.auth.restoreAttempt, greaterThan(0));

      // Past the first backoff step, which matches DashboardSocket's minimum.
      await Future<void>.delayed(const Duration(milliseconds: 1400));

      expect(it.auth.status, AuthStatus.authenticated);
      expect(it.auth.restoreAttempt, 0);
      expect(it.store.clears, 0);
    });

    // Two tabs on one machine share a TokenStore. Rotation invalidates the token it was handed, and
    // presenting a rotated one is what the Server reads as theft — `AuthEndpoints.RefreshAsync`
    // revokes the whole *family* for it, so the tab that lost a startup race used to sign out the
    // tab that won.
    test(
      'a token a sibling has already rotated is adopted, not replayed',
      () async {
        final it = subject(
          // Read once by `restore`, and again by the refresh — by which point the sibling has written.
          reads: [
            stored(accessIn: const Duration(minutes: -1)),
            stored(
              accessIn: const Duration(minutes: -1),
              refreshToken: 'token-b',
            ),
          ],
          onRefresh: (token, _) async => okJson(refreshBody()),
        );

        await it.auth.restore();
        await pumpEventQueue();

        expect(it.asked, ['token-b']);
        expect(it.asked, isNot(contains('token-a')));
        expect(it.auth.status, AuthStatus.authenticated);
      },
    );

    test('a rejection is retried against what storage holds now', () async {
      var rotated = false;
      final it = subject(
        // Storage still agrees when the request goes out, and has moved on by the time the 401
        // comes back — the sibling rotated mid-flight.
        reads: [
          stored(accessIn: const Duration(minutes: -1)),
          stored(accessIn: const Duration(minutes: -1)),
          stored(
            accessIn: const Duration(minutes: -1),
            refreshToken: 'token-b',
          ),
        ],
        onRefresh: (token, _) async {
          if (token == 'token-a') {
            rotated = true;
            return http.Response('{}', 401);
          }
          return okJson(refreshBody());
        },
      );

      await it.auth.restore();
      await pumpEventQueue();

      expect(rotated, isTrue);
      expect(it.asked, ['token-a', 'token-b']);
      expect(it.auth.status, AuthStatus.authenticated);
      // The tab that lost the race must not take the winner's session down with it.
      expect(it.store.clears, 0);
    });
  });

  // Storage is a sibling tab's channel, and these are the two cases where what is in it is not a
  // sibling's to lend. Adopting either would be worse than ignoring it: one trades a working token
  // for a dead one, the other changes who is signed in.
  group('what storage is not allowed to answer', () {
    /// A controller signed in by password, over a store that already holds [holding].
    Future<({AuthController auth, List<String> asked})> signedIn({
      required AuthSession? holding,
      required bool remember,
      String username = 'admin',
    }) async {
      final asked = <String>[];
      final auth = AuthController(
        config: config,
        store: _ScriptedStore([holding]),
        api: AuthApi(
          config: config,
          client: MockClient((request) async {
            final body = jsonDecode(request.body) as Map<String, dynamic>;
            if (request.url.path != '/api/auth/refresh') {
              return okJson(refreshBody(refreshToken: 'token-from-login'));
            }
            asked.add(body['refreshToken'] as String);
            return okJson(refreshBody(refreshToken: 'token-rotated'));
          }),
        ),
      );
      addTearDown(auth.dispose);

      await auth.login(username, 'password', remember: remember);
      return (auth: auth, asked: asked);
    }

    test('a session that was never written does not read storage', () async {
      // "Stay signed in" unchecked. Whatever is in the store is some earlier run's, so it is older
      // than what we hold rather than newer — and a spent token is what trips family revocation.
      final it = await signedIn(
        holding: stored(
          accessIn: const Duration(minutes: 10),
          refreshToken: 'stale-from-last-run',
        ),
        remember: false,
      );

      await it.auth.ensureFreshAccessToken(force: true);

      expect(it.asked, ['token-from-login']);
      expect(it.asked, isNot(contains('stale-from-last-run')));
    });

    test('another account in storage is not adopted', () async {
      // A sibling tab signed in as somebody else is not a rotation of this session. Picking its
      // token up would quietly change who is logged in here.
      final it = await signedIn(
        holding: AuthSession(
          accessToken: 'their-access',
          accessTokenExpiresAt: DateTime.now().add(const Duration(minutes: 10)),
          refreshToken: 'their-token',
          refreshTokenExpiresAt: DateTime.now().add(const Duration(days: 30)),
          username: 'someone-else',
          role: Role.viewer,
        ),
        remember: true,
      );

      await it.auth.ensureFreshAccessToken(force: true);

      expect(it.asked, ['token-from-login']);
      expect(it.asked, isNot(contains('their-token')));
      expect(it.auth.username, 'admin');
    });
  });

  // The other half of the same distinction, mid-session rather than at boot.
  group('AuthController.ensureFreshAccessToken', () {
    AuthController authWith(
      Future<http.Response> Function() onRefresh,
      _ScriptedStore store,
    ) {
      final auth = AuthController(
        config: config,
        store: store,
        api: AuthApi(
          config: config,
          client: MockClient((_) async => onRefresh()),
        ),
      );
      addTearDown(auth.dispose);
      return auth;
    }

    test('a rejection ends the session, so the router can react', () async {
      // Nothing used to notify here, so `refreshListenable` never fired and the app sat on stale
      // tiles while every request behind it 401'd.
      final store = _ScriptedStore([
        stored(accessIn: const Duration(minutes: 10)),
      ]);
      final auth = authWith(() async => http.Response('{}', 401), store);

      await auth.restore();
      expect(auth.status, AuthStatus.authenticated);

      expect(await auth.ensureFreshAccessToken(force: true), isNull);
      expect(auth.status, AuthStatus.unauthenticated);
      expect(store.clears, 1);
    });

    test('a network failure does not', () async {
      final store = _ScriptedStore([
        stored(accessIn: const Duration(minutes: 10)),
      ]);
      final auth = authWith(
        () async => throw http.ClientException('connection reset'),
        store,
      );

      await auth.restore();

      expect(await auth.ensureFreshAccessToken(force: true), isNull);
      // The sockets are already retrying on their own backoff. Signing somebody out because the
      // Wi-Fi dropped for a second is the mistake `restore` used to make.
      expect(auth.status, AuthStatus.authenticated);
      expect(store.clears, 0);
    });
  });

  group('TokenStore', () {
    setUp(() => SharedPreferences.setMockInitialValues({}));

    test(
      'falls back to plain preferences when secure storage throws',
      () async {
        final store = TokenStore(storage: _UnsupportedSecureStorage());

        await store.save(session());
        final restored = await store.read();

        expect(restored, isNotNull);
        expect(restored!.username, 'admin');
        expect(restored.accessToken, 'access-token');
        expect(restored.role, Role.admin);
      },
    );

    test('clear empties the fallback too', () async {
      final store = TokenStore(storage: _UnsupportedSecureStorage());

      await store.save(session());
      await store.clear();

      expect(await store.read(), isNull);
    });
  });
}

/// Storage whose reads are scripted, so a sibling tab writing between two of them can be expressed.
///
/// Entries are consumed one per [read], and the last one stands for every read after it — "storage
/// held A, then B from here on" is the shape every one of these cases needs. Counts clears rather
/// than acting on them: whether the refresh token survives is the assertion in most of these.
class _ScriptedStore extends TokenStore {
  _ScriptedStore(this._reads)
    : super(storage: const _UnsupportedSecureStorage());

  final List<AuthSession?> _reads;
  int _index = 0;

  int clears = 0;
  AuthSession? saved;

  @override
  Future<AuthSession?> read() async {
    final value = _reads[_index < _reads.length ? _index : _reads.length - 1];
    _index++;
    return value;
  }

  @override
  Future<void> save(AuthSession session) async => saved = session;

  @override
  Future<void> clear() async => clears++;
}

/// A [TokenStore] that fails the way the web implementation does on an insecure origin. Subclassed
/// rather than mocked so the type the controller actually depends on is the one under test.
class _ThrowingStore extends TokenStore {
  _ThrowingStore() : super(storage: _UnsupportedSecureStorage());

  @override
  Future<void> save(AuthSession session) async => throw UnsupportedError(
    'FlutterSecureStorageWeb only works in secure contexts',
  );

  @override
  Future<AuthSession?> read() async => throw UnsupportedError(
    'FlutterSecureStorageWeb only works in secure contexts',
  );

  @override
  Future<void> clear() async {}
}

/// Stands in for `FlutterSecureStorageWeb` on a non-secure origin: every entry point that touches
/// Web Crypto throws, and `delete` — which is crypto-free there — does not.
class _UnsupportedSecureStorage extends FlutterSecureStorage {
  const _UnsupportedSecureStorage();

  static Never _refuse() => throw UnsupportedError(
    'FlutterSecureStorageWeb only works in secure contexts',
  );

  @override
  Future<void> write({
    required String key,
    required String? value,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => _refuse();

  @override
  Future<String?> read({
    required String key,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => _refuse();

  @override
  Future<void> delete({
    required String key,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {}
}
