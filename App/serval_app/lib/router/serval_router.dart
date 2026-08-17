import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../data/auth/auth_controller.dart';
import '../data/providers.dart';
import '../screens/account_screen.dart';
import '../screens/alert_screen.dart';
import '../screens/alerts_screen.dart';
import '../screens/camera_screen.dart';
import '../screens/cameras_screen.dart';
import '../screens/clip_screen.dart';
import '../screens/clips_screen.dart';
import '../screens/connecting_screen.dart';
import '../screens/login_screen.dart';
import '../screens/mask_editor_screen.dart';
import '../screens/notifications_screen.dart';
import '../screens/server_screen.dart';
import '../screens/settings_index_screen.dart';
import '../screens/settings_screen.dart';
import '../screens/users_screen.dart';
import '../screens/wall_screen.dart';
import '../theme/serval_tokens.dart';
import '../widgets/serval_shell.dart';
import '../widgets/settings_nav.dart';
import '../widgets/settings_shell.dart';

/// Where you can be, as addresses rather than as an enum.
///
/// ```
/// /login
/// ShellRoute  [the icon rail]
///   /wall
///   /clips        /clips/:id
///   /alerts       /alerts/:id
///   ShellRoute  [the settings sidebar]
///     /settings                          ← the drill-down index, narrow only
///     /settings/cameras   ?camera=<id>
///     /settings/server
///     /settings/status
///     /settings/notifications            ← the one page that is yours, not the deployment's
///     /settings/users
/// /camera/:id                            ← outside the shell, deliberately
/// ```
///
/// Web is the primary target, so every screen has to be linkable, survive a reload, and answer the
/// browser's back button — which is why the two things that could have been widget state live in the
/// address: which camera is open, and which camera the registry opens on. See `Docs/app-notes.md`.
///
/// The rail carries one destination because it cannot show a back stack. On web the browser's own
/// chrome shows it, so that constraint does not reach the address bar.
///
/// Every route uses [NoTransitionPage]. The app does not animate between screens — a slide would be
/// wrong for a desktop-shaped Nocturne layout — and it also keeps the goldens honest, since a
/// transition mid-settle is exactly the kind of thing that makes a capture flaky.
GoRouter buildServalRouter({required AuthController? auth}) => GoRouter(
  initialLocation: '/wall',

  // Re-runs [_redirect] when the session changes, which is what moves an expired session to the
  // login screen mid-app and what moves a fresh login off it.
  refreshListenable: auth,
  redirect: (context, state) => _redirect(auth: auth, state: state),
  routes: [
    GoRoute(
      path: '/login',
      pageBuilder: (context, state) => NoTransitionPage(
        // Only reachable when `auth` is non-null: [_redirect] never sends the sample path here.
        child: LoginScreen(auth: auth!),
      ),
    ),

    // A session that is still good, waiting on a Server that has not answered. Its own address
    // rather than a flag on `/login`, so the two cannot be confused by anything — the router, a
    // bookmark, or whoever is reading the screen. See [ConnectingScreen].
    GoRoute(
      path: '/connecting',
      pageBuilder: (context, state) =>
          NoTransitionPage(child: ConnectingScreen(auth: auth!)),
    ),

    // Outside the shell. See [ServalShell] for why the single-camera view has no rail.
    GoRoute(
      path: '/camera/:id',
      pageBuilder: (context, state) => NoTransitionPage(
        child: _CameraRoute(
          cameraId: state.pathParameters['id']!,
          // Where the recording should open, when arriving from a row in the feed.
          openAt: DateTime.tryParse(state.uri.queryParameters['at'] ?? ''),
        ),
      ),
    ),

    // Also outside both shells, and for the same reason the single-camera view is: design 9b
    // replaces the whole window — no rail, no settings sidebar, no camera list — because drawing a
    // polygon wants the frame at full size.
    //
    // Deliberately *not* a `SettingsSection`: that enum's spot is earned by being a sidebar
    // destination, and this is reached from the camera's *Masks & zones* section. Declaring it
    // inside `SettingsShell` would also light the wrong nav row, since that shell's switch falls
    // through to `SettingsSection.status` for a path it does not know.
    GoRoute(
      path: '/settings/cameras/masks',
      pageBuilder: (context, state) => NoTransitionPage(
        child: MaskEditorScreen(
          cameraId: state.uri.queryParameters['camera'] ?? '',
        ),
      ),
    ),

    ShellRoute(
      builder: (context, state, child) => ServalShell(child: child),
      routes: [
        GoRoute(
          path: '/wall',
          pageBuilder: (context, state) => NoTransitionPage(
            child: WallScreen(
              // In the URL rather than in `extra`, so the link survives a refresh and can be
              // shared — the same reason the settings routes carry their camera that way.
              onOpenCamera: (id, at) => context.go(
                at == null
                    ? '/camera/$id'
                    : '/camera/$id?at=${Uri.encodeComponent(at.toIso8601String())}',
              ),
            ),
          ),
        ),
        // Inside the rail shell, because saved clips are a peer of the wall rather than a
        // drill-down from it. On a phone the rail is absent and this is reached from the wall's
        // app bar instead — which is why it takes an [onBack] there and not here.
        GoRoute(
          path: '/clips',
          pageBuilder: (context, state) => NoTransitionPage(
            child: ClipsScreen(
              onBack: isCompact(context) ? () => context.go('/wall') : null,
              onOpenClip: (id) => context.go('/clips/$id'),
            ),
          ),
        ),

        GoRoute(
          path: '/clips/:id',
          pageBuilder: (context, state) => NoTransitionPage(
            child: ClipScreen(
              clipId: state.pathParameters['id']!,
              onBack: () => context.go('/clips'),
            ),
          ),
        ),

        // Alerts are the third peer of the wall — a queue rather than a library, which is why they
        // are a place and not a filter on one. Same shape as clips: the list here, one alert at
        // `/alerts/:id`, reached from the wall's app bar on a phone where there is no rail.
        GoRoute(
          path: '/alerts',
          pageBuilder: (context, state) => NoTransitionPage(
            child: AlertsScreen(
              onBack: isCompact(context) ? () => context.go('/wall') : null,
              onOpenAlert: isCompact(context)
                  ? (id) => context.go('/alerts/$id')
                  : null,
              onWatch: (cameraId, at) => context.go(
                '/camera/$cameraId?at=${Uri.encodeComponent(at.toIso8601String())}',
              ),
              // *What I'm alerted on* means the per-camera rules, which are the notifications
              // page. Not `/settings/server`: the "Notifications" group there is the deployment's
              // push plumbing, Admin-only, and says nothing about which cameras wake you.
              onOpenSettings: () =>
                  context.go(settingsPathFor(SettingsSection.notifications)),
            ),
          ),
        ),

        // Where a tapped notification lands, as well as where a row goes — one destination however
        // you arrive, which is why it takes an id rather than an alert.
        GoRoute(
          path: '/alerts/:id',
          pageBuilder: (context, state) => NoTransitionPage(
            child: _AlertRoute(alertId: state.pathParameters['id']!),
          ),
        ),

        ShellRoute(
          builder: (context, state, child) => SettingsShell(child: child),
          routes: [
            // The settings index — design 7b's first phone screen, and the address the wall's gear
            // opens below [Serval.compactWidth]. Wide, there is nothing to drill into, so it is the
            // fallback `SettingsShell` already says `/settings` means: what the machine is doing.
            GoRoute(
              path: '/settings',
              pageBuilder: (context, state) => NoTransitionPage(
                child: isCompact(context)
                    ? const SettingsIndexScreen()
                    : const ServerScreen(),
              ),
            ),
            GoRoute(
              path: '/settings/cameras',
              pageBuilder: (context, state) => NoTransitionPage(
                // Which camera the registry opens on. Set by the single-camera view's gear; absent
                // when the rail's gear asked for settings rather than for one camera's record.
                //
                // Also what the drill-down navigates by: picking a camera on a phone is a move to
                // a new address, which is what makes the back button walk editor → list.
                child: CamerasScreen(
                  initialCameraId: state.uri.queryParameters['camera'],
                  onSelectCamera: (id) => context.go(
                    id == null
                        ? '/settings/cameras'
                        : '/settings/cameras?camera=$id',
                  ),
                ),
              ),
            ),
            GoRoute(
              path: '/settings/server',
              pageBuilder: (context, state) =>
                  const NoTransitionPage(child: SettingsScreen()),
            ),
            // What the Server is doing, as against what it is told to do. This was `/settings/server`
            // while it was the only server-wide page; the settings that can be *changed* took that
            // path when they arrived, since that is the one people go looking for.
            GoRoute(
              path: '/settings/status',
              pageBuilder: (context, state) =>
                  const NoTransitionPage(child: ServerScreen()),
            ),
            // The two settings pages that belong to the person rather than the deployment, so
            // neither is gated on Admin the way `/settings/users` is: narrowing what reaches you is
            // the only control over your own notifications a Viewer has, and their own password is
            // theirs whatever their role.
            GoRoute(
              path: '/settings/notifications',
              pageBuilder: (context, state) =>
                  const NoTransitionPage(child: NotificationsScreen()),
            ),
            GoRoute(
              path: '/settings/account',
              pageBuilder: (context, state) =>
                  const NoTransitionPage(child: AccountScreen()),
            ),
            GoRoute(
              path: '/settings/users',
              pageBuilder: (context, state) =>
                  const NoTransitionPage(child: UsersScreen()),
            ),
          ],
        ),
      ],
    ),
  ],
);

/// The routes an Admin has and nobody else does.
///
/// Hiding these in the sidebar is presentation; this is what makes a typed URL land somewhere a
/// Viewer is allowed to be. Neither is the safeguard — the Server refuses the calls behind both —
/// but a page that renders and then fills with 403s is a worse answer than not opening it.
const _adminOnlyRoutes = {
  '/settings/users', // every /api/auth/users route is Admin-only
  '/settings/cameras/masks', // saves the camera, so it is a camera write
};

/// The login gate, and the role gate above it.
///
/// **Side-effect free on purpose.** `redirect` runs on every navigation and can run more than once
/// for one of them, so starting the repository from here would open sockets repeatedly.
/// `RepositoryStarter` in `main.dart` owns that instead.
String? _redirect({
  required AuthController? auth,
  required GoRouterState state,
}) {
  // No session to gate on: the widget tests and the goldens construct `ServalApp` with no auth at
  // all, and there is no login screen for them to be sent to. Same fact `authProvider` encodes.
  if (auth == null) return null;

  final signedIn = auth.isAuthenticated;
  final atLogin = state.matchedLocation == '/login';
  final atConnecting = state.matchedLocation == '/connecting';

  // Before the signed-in test, because it is the case that test gets wrong: a session being renewed
  // is not a session that ended, and answering it with a password prompt asks for something that
  // was never the problem. Held here until the retry behind it resolves one way or the other.
  //
  // Carrying the address across the wait rather than dropping it: a cold start lands here whenever
  // the stored access token is inside its refresh margin, which is most of them, and a tapped
  // notification arriving then would otherwise be answered with the wall.
  if (auth.isRestoring) {
    return atConnecting
        ? null
        : '/connecting?from=${Uri.encodeComponent(state.uri.toString())}';
  }

  if (!signedIn) return atLogin ? null : '/login';
  if (atLogin) return '/wall';

  // Back to whatever the wait interrupted. The wall for a plain launch, which is what leaving
  // `/connecting` has always meant; the alert for a launch that a notification asked for.
  if (atConnecting) return state.uri.queryParameters['from'] ?? '/wall';

  // Sent to the settings index rather than the wall: the destination was a settings page, and
  // landing somewhere adjacent reads as "not this one" where the wall would read as "signed out".
  if (!auth.isAdmin && _adminOnlyRoutes.contains(state.matchedLocation)) {
    return '/settings';
  }

  return null;
}

/// One alert, at whichever width the link was opened at.
///
/// The address means the same thing everywhere; what it draws does not. A phone gets [AlertScreen],
/// which is a screen built for 412px — a 232px stage, full-width buttons, the class in the corner of
/// the frame. Wide, that same screen is a stage nearly two thousand pixels across and still 232 tall,
/// and nothing here crops a frame to fill one — so an 8:1 hole gets some 412px of picture marooned
/// in a field of ground, and a notification tapped on a desktop opened onto a stamp of one doorbell.
///
/// So wide it opens the queue with that alert selected instead — the detail column beside the list,
/// which is where an alert is read at that width and exactly what clicking its row gives you. The
/// switch lives in a widget rather than in `pageBuilder` so that dragging the window across
/// [Serval.compactWidth] rebuilds it, the same reason `_CameraRoute` is one.
class _AlertRoute extends StatelessWidget {
  const _AlertRoute({required this.alertId});

  final String alertId;

  @override
  Widget build(BuildContext context) => isCompact(context)
      ? AlertScreen(
          alertId: alertId,
          onBack: () => context.go('/alerts'),
          onWatch: _watch(context),
        )
      : AlertsScreen(
          initialAlertId: alertId,
          onWatch: _watch(context),
          onOpenSettings: () =>
              context.go(settingsPathFor(SettingsSection.notifications)),
        );

  void Function(String, DateTime) _watch(BuildContext context) =>
      (cameraId, at) => context.go(
        '/camera/$cameraId?at=${Uri.encodeComponent(at.toIso8601String())}',
      );
}

/// Resolves `/camera/:id` against the registry, and falls back to the wall for an id that is not
/// in it — a camera deleted from the settings screen, or a stale bookmark.
///
/// The fallback has to distinguish *no such camera* from *the registry has not loaded yet*, and
/// that is not pedantry: on the interactive-login path the repository starts unawaited, so the
/// first build of a deep link genuinely has an empty registry. Bouncing then would make
/// `/camera/front-door` unopenable by anyone who had just signed in — the exact case a shared link
/// is for. So an empty registry waits, and only a populated one that lacks the id gives up.
class _CameraRoute extends ConsumerWidget {
  const _CameraRoute({required this.cameraId, this.openAt});

  final String cameraId;
  final DateTime? openAt;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final repository = ref.watch(repositoryProvider);

    // The registry slice, which is all this builder reads. On the whole repository it was the worst
    // subscription in the app: it wraps the entire camera screen, so every detection heartbeat —
    // twice a second, per camera in view — rebuilt the picture, the transport, the controls and the
    // tray to re-answer a question whose answer only changes when a camera is added, edited,
    // deleted, or goes stale.
    return ListenableBuilder(
      listenable: repository.registryChanges,
      builder: (context, _) {
        final camera = repository.cameraById(cameraId);
        if (camera != null) {
          // Its own `Scaffold`, because this route sits outside `ServalShell` and so does not
          // inherit that one.
          return Scaffold(
            body: CameraScreen(
              camera: camera,
              openAt: openAt,
              onBack: () => context.go('/wall'),
              onOpenSettings: () =>
                  context.go('/settings/cameras?camera=$cameraId'),
            ),
          );
        }

        if (repository.cameras().isEmpty) {
          // Still loading. Nothing to draw a spinner over — the wall behind this is not built yet
          // either — so this holds the frame rather than flashing a screen it is about to leave.
          return const SizedBox.shrink();
        }

        // A real miss. Redirecting from `build` is not allowed, so this defers to the next frame.
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (context.mounted) context.go('/wall');
        });
        return const SizedBox.shrink();
      },
    );
  }
}
