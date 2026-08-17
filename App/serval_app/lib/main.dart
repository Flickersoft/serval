import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'data/auth/auth_controller.dart';
import 'data/auth/authenticated_client.dart';
import 'data/live_repository.dart';
import 'data/providers.dart';
import 'data/sample_repository.dart';
import 'data/serval_api.dart';
import 'data/serval_config.dart';
import 'data/serval_repository.dart';
import 'playback/vod_player.dart';
import 'push/push_client.dart';
import 'router/serval_router.dart';
import 'router/url_strategy.dart';
import 'theme/app_theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Here and nowhere else. The tests and the goldens build `ServalApp` directly, and
  // `dart.library.io` is true under `flutter test` — so the media_kit backend is compiled into
  // the test binary even though nothing there replays anything. Initialising from `ServalApp`
  // would load libmpv on every `flutter test` run, on a machine that need not have it.
  ensurePlaybackInitialized();

  // `/camera/front-door` rather than `/#/camera/front-door`. Off web this is a no-op — see
  // lib/router/url_strategy.dart. Before `runApp`, so the first route is parsed the right way.
  useServalUrlStrategy();

  final config = ServalConfig.fromEnvironment();
  final auth = AuthController(config: config);

  // Reads whatever is in secure storage and settles on a status for it, so a device with a
  // still-valid refresh token skips the login screen on this cold start. Awaited so the first frame
  // shows the right screen rather than flashing the login page in front of someone already signed
  // in — and it reaches the network in no case, precisely so that awaiting it here cannot hold the
  // app on a blank page while a Server finishes starting. See `AuthController.restore`.
  await auth.restore();

  final repository = LiveServalRepository(
    auth: auth,
    config: config,
    api: ServalApi(
      config: config,
      client: AuthenticatedClient(auth: auth),
    ),
  );

  // The repository is *not* started here, on either path. `_RepositoryStarter` starts it the moment
  // `auth` reports authenticated, which covers the cold start, the interactive login, and the
  // restore that completes a few seconds late — one owner, one start, and nothing before `runApp`
  // that waits on a Server.
  runApp(ServalApp(repository: repository, auth: auth));
}

/// Serval's client.
///
/// The screens read everything through a [ServalRepository]. `main` builds the Server-backed
/// [LiveServalRepository]; the default here stays [SampleServalRepository], which returns the
/// design's own content, so the widget tests and the goldens render the mock rather than
/// whatever a live NVR happens to be looking at.
///
/// [auth] is null in exactly the same cases [repository] is the sample one — the widget tests
/// and the goldens construct `const ServalApp()` with neither, and there is no login gate
/// without a session to gate on. That single fact is what `providers.dart`'s derived session
/// providers and the router's redirect both key off.
class ServalApp extends StatelessWidget {
  const ServalApp({
    super.key,
    this.repository = const SampleServalRepository(),
    this.auth,
  });

  final ServalRepository repository;
  final AuthController? auth;

  @override
  Widget build(BuildContext context) => ProviderScope(
    // Here rather than in `main`, and the overrides read from this widget's own fields rather than
    // from globals — so `const ServalApp()` keeps meaning exactly what it meant before. The widget
    // tests and the goldens pump this directly with the sample repository, and it reaches the
    // screens by the same path the live one does.
    overrides: [
      repositoryProvider.overrideWithValue(repository),
      authProvider.overrideWithValue(auth),
    ],
    child: _ServalMaterialApp(auth: auth),
  );
}

/// Holds the [GoRouter] for the life of the app.
///
/// Stateful only because a router must not be rebuilt: constructing a new one per build throws
/// away the history stack, which on web means the browser's back button stops agreeing with the
/// app.
class _ServalMaterialApp extends StatefulWidget {
  const _ServalMaterialApp({required this.auth});

  final AuthController? auth;

  @override
  State<_ServalMaterialApp> createState() => _ServalMaterialAppState();
}

class _ServalMaterialAppState extends State<_ServalMaterialApp> {
  late final GoRouter _router = buildServalRouter(auth: widget.auth);

  @override
  void initState() {
    super.initState();

    // A tapped notification on a tab that is already open arrives as a message from the service
    // worker rather than as a navigation — see `sw.js`, which prefers focusing this tab to opening
    // a second copy of a live-video app. Routing it has to happen here because this is where the
    // router lives; without it the tab would come to the front unchanged, which reads as the tap
    // having done nothing at all.
    //
    // A no-op off the web and on a browser with no push, so there is no platform branch here.
    PushClient.onNavigate(_router.go);
  }

  @override
  void dispose() {
    _router.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => MaterialApp.router(
    title: 'Serval',
    debugShowCheckedModeBanner: false,
    theme: buildServalTheme(),
    routerConfig: _router,
    // Anything that does not sit under a `Material` — the login screen, and every dialog route —
    // otherwise inherits `WidgetsApp`'s fallback `_errorTextStyle`, whose yellow double underline
    // survives a `TextStyle` that only sets family, size and colour, which is all the screens
    // here ever set. `ServalShell` supplies a `Scaffold` for the signed-in screens; this supplies
    // the text style for everything else. Above the Navigator, so the dialog routes are covered.
    //
    // Deliberately *not* a `Scaffold` here: the login screen sits outside one, which
    // `golden_capture_test`'s 3a capture depends on.
    builder: (context, child) => DefaultTextStyle(
      style: Theme.of(context).textTheme.bodyMedium!,
      child: _RepositoryStarter(child: child!),
    ),
  );
}

/// Starts the repository the moment the session becomes usable, whichever way it got there — a
/// stored token that was still good at cold start, an interactive login, or a restore that reached
/// the Server a few seconds after the first frame.
///
/// The sole owner of that call — a second caller would run `start()` twice per launch: two of
/// every startup read, a duplicate listener on each socket, and an orphaned sweep timer that
/// `dispose` cannot reach.
///
/// Separate from the router on purpose. Deciding *which* screen to show is the router's redirect;
/// opening two sockets is a side effect, and a `redirect` callback runs on every navigation —
/// sometimes more than once for one — so it is the wrong place to do anything that is not a pure
/// function of the address.
class _RepositoryStarter extends ConsumerStatefulWidget {
  const _RepositoryStarter({required this.child});

  final Widget child;

  @override
  ConsumerState<_RepositoryStarter> createState() => _RepositoryStarterState();
}

class _RepositoryStarterState extends ConsumerState<_RepositoryStarter> {
  bool _started = false;
  AuthController? _auth;

  /// Null off the live repository, so `flutter test` — which pumps `ServalApp` with the sample one
  /// — constructs nothing new and observes nothing.
  AppLifecycleListener? _lifecycle;

  @override
  void initState() {
    super.initState();
    _auth = ref.read(authProvider);
    _auth?.addListener(_onAuthChanged);
    _onAuthChanged();

    if (ref.read(repositoryProvider) is LiveServalRepository) {
      // `onShow`, and deliberately not `onResume`. Both fire on the way back, but `onResume` also
      // fires when the window merely regains input focus — and a second monitor showing the wall
      // while you work elsewhere is the case this must leave alone. `onShow` is the hidden→visible
      // edge alone, which is what `document.visibilityState` means, and it buys that rule with no
      // bookkeeping of our own.
      _lifecycle = AppLifecycleListener(onShow: _onShown);
    }
  }

  @override
  void dispose() {
    _lifecycle?.dispose();
    _auth?.removeListener(_onAuthChanged);
    super.dispose();
  }

  void _onShown() {
    // Only for a session that is actually running. Coming back to a tab sitting on `/login` must
    // not raise sockets, and `_started` is the same flag both edges below turn on.
    if (!_started) return;

    final repository = ref.read(repositoryProvider);
    if (repository is! LiveServalRepository) return;

    repository.resumeLive();
  }

  void _onAuthChanged() {
    final auth = _auth;
    if (auth == null) return;

    // Safe: `auth` is only ever non-null alongside a LiveServalRepository — see ServalApp's doc
    // comment — and `start`/`stop` are specific to that implementation.
    final repository = ref.read(repositoryProvider);
    if (repository is! LiveServalRepository) return;

    if (auth.isAuthenticated) {
      // Once per session, not once per app: `authenticated` is notified again by every successful
      // token refresh, and starting on each of those would open a second pair of sockets every ten
      // minutes.
      if (_started) return;
      _started = true;
      unawaited(repository.start());
      return;
    }

    // Both edges, because the repository outlives the session. Signing out leaves the app running
    // on `/login`, and everything `start()` filled in — the registry, the feed, the arrangement,
    // the vitals — would otherwise still be sitting there for whoever signs in next. Reached on a
    // sign-out and on a session the Server has rejected, which are the only two ways to leave
    // `authenticated`; `restoring` and `authenticating` never got here with `_started` true.
    if (!_started) return;
    _started = false;
    repository.stop();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
