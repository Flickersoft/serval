import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../data/providers.dart';
import '../theme/serval_tokens.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/settings_nav.dart';

/// Design 7b's first screen: the settings sidebar as a screen of its own, with the wall behind the
/// back arrow.
///
/// It exists only below [Serval.compactWidth]. Above it the same four rows are a column beside the
/// page they open, so there is nothing to drill into and `/settings` lands on status instead.
class SettingsIndexScreen extends ConsumerWidget {
  const SettingsIndexScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => DecoratedBox(
    decoration: const BoxDecoration(color: Serval.panel),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        CompactAppBar(title: 'Settings', onBack: () => context.go('/wall')),
        Expanded(
          child: SettingsNav(
            compact: true,
            selected: SettingsSection.status,
            onSelect: (section) => context.go(settingsPathFor(section)),
            isAdmin: ref.watch(isAdminProvider),
            currentUsername: ref.watch(currentUsernameProvider),
            currentRole: ref.watch(currentRoleProvider),
            onLogout: ref.watch(logoutProvider),
          ),
        ),
      ],
    ),
  );
}
