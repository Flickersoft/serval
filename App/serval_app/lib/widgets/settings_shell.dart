import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../data/providers.dart';
import '../theme/serval_tokens.dart';
import 'settings_nav.dart';

/// The 236px settings sidebar, shared by the camera registry, *Server settings*, *Server status* and
/// *Users & access*.
///
/// Which row is lit comes off the address, the same way [ServalShell]'s rail selection does, so
/// there is one place that decides it.
///
/// Below [Serval.compactWidth] the page has the screen to itself and the sidebar is a screen of its
/// own — `SettingsIndexScreen`, which every page here carries a back arrow to.
class SettingsShell extends ConsumerWidget {
  const SettingsShell({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (isCompact(context)) return child;

    // Status is the fallback rather than a fourth case: `/settings` with no page named is what the
    // machine is doing, which is the page that changes nothing and every account can reach.
    final selected = switch (GoRouterState.of(context).uri.path) {
      '/settings/users' => SettingsSection.users,
      '/settings/server' => SettingsSection.server,
      '/settings/cameras' => SettingsSection.cameras,
      '/settings/notifications' => SettingsSection.notifications,
      '/settings/account' => SettingsSection.account,
      _ => SettingsSection.status,
    };

    return Row(
      children: [
        SettingsNav(
          selected: selected,
          onSelect: (section) => context.go(settingsPathFor(section)),
          isAdmin: ref.watch(isAdminProvider),
          currentUsername: ref.watch(currentUsernameProvider),
          currentRole: ref.watch(currentRoleProvider),
          onLogout: ref.watch(logoutProvider),
        ),
        Expanded(child: child),
      ],
    );
  }
}
