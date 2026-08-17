import 'package:flutter/material.dart' show Scaffold;
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/providers.dart';
import '../theme/serval_tokens.dart';
import 'icon_rail.dart';

/// The 64px rail, and everything that sits to the right of it.
///
/// One `ShellRoute` rather than a copy per screen, with the lit item derived from the address
/// rather than passed in.
///
/// `/camera/:id` is deliberately **not** under this shell. The single-camera view is a focused
/// screen: its top bar carries a labelled *All cameras* button, which says more at that moment than
/// an unlabelled glyph, and lighting the rail's *Live view* while you are watching a live view
/// would be actively confusing.
///
/// Below [Serval.compactWidth] there is no rail at all. Design 7b weighed a bottom tab bar against
/// this and chose this: the wall is home and its own header carries the gear, so the rail's two
/// jobs are done by one button and the screen that is shortest of room gets the 64px back. Sign-out
/// goes with the gear, into the settings index's account footer.
class ServalShell extends ConsumerWidget {
  const ServalShell({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final location = GoRouterState.of(context).uri.path;
    final onWall = location == '/wall';
    final onAlerts = location.startsWith('/alerts');
    final onClips = location.startsWith('/clips');

    final repository = ref.watch(repositoryProvider);

    // Gives the rail's ink responses a `Material` ancestor. It sits *inside* the shell rather
    // than up in `MaterialApp`'s builder, because the login screen deliberately has no Scaffold.
    return Scaffold(
      body: DecoratedBox(
        decoration: const BoxDecoration(color: Serval.panel),
        child: Row(
          children: [
            // Subscribed rather than merely read. `ref.watch` hands back the repository, whose
            // identity never changes, so on its own it would light the alert dot exactly once —
            // whatever the count happened to be when this shell was first built. The dot has to
            // move when the live feed carries an alert, and that arrives as a notification.
            //
            // The alert slice and not the repository: the rail is under every wide screen, so a
            // listener here on everything the repository holds was a rail relaid out at whatever
            // rate the busiest camera in the house was publishing detections.
            if (!isCompact(context))
              ListenableBuilder(
                listenable: repository.alertChanges,
                builder: (context, _) => _rail(
                  context,
                  ref,
                  unread: repository.unreadAlerts,
                  onWall: onWall,
                  onAlerts: onAlerts,
                  onClips: onClips,
                ),
              ),
            Expanded(child: child),
          ],
        ),
      ),
    );
  }

  Widget _rail(
    BuildContext context,
    WidgetRef ref, {
    required int unread,
    required bool onWall,
    required bool onAlerts,
    required bool onClips,
  }) => IconRail(
    // Three destinations: the wall, the alerts, and the clips. Each is a place rather than an
    // option — alerts are a queue that arrives whether you asked or not, and saved clips are the
    // only footage that does not roll off. Alerts sit next to the wall because that is the order
    // they are reached in: something happened, then you went and looked, then you kept it.
    selectedIndex: switch ((onWall, onAlerts, onClips)) {
      (true, _, _) => 0,
      (_, true, _) => 1,
      (_, _, true) => 2,
      _ => -1,
    },
    items: [
      RailItem(
        icon: onWall
            ? PhosphorIconsFill.broadcast
            : PhosphorIconsRegular.broadcast,
        tooltip: 'Live view',
      ),
      RailItem(
        icon: onAlerts ? PhosphorIconsFill.bell : PhosphorIconsRegular.bell,
        tooltip: 'Alerts',
        // A dot, not a number. At 64px a count is unreadable, and the question the rail answers
        // is "is there anything" — the header says how much.
        badge: unread > 0,
      ),
      RailItem(
        icon: onClips
            ? PhosphorIconsFill.filmStrip
            : PhosphorIconsRegular.filmStrip,
        tooltip: 'Saved clips',
      ),
    ],
    onSelected: (index) => context.go(switch (index) {
      0 => '/wall',
      1 => '/alerts',
      _ => '/clips',
    }),
    settingsSelected: !onWall && !onAlerts && !onClips,
    onSettings: () => context.go('/settings/status'),
    onLogout: ref.watch(logoutProvider),
  );
}
