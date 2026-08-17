import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../data/auth/auth_models.dart';
import '../data/providers.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_field.dart';
import '../widgets/nocturne_toggle.dart';

/// This account, as against this machine — *Settings → Account*.
///
/// **Every route behind this page already existed; none of it had a screen.** `PUT
/// /api/auth/password` has been there since the auth endpoints were written, and the only way to
/// change your own password was to ask an Admin to set one, which tells them what it is. A Viewer
/// had no page in settings they could write to at all.
///
/// Sibling to [NotificationsScreen](notifications_screen.dart) and gated the same way — which is to
/// say not at all. Your own name, your own password and your own sessions are yours whatever your
/// role, and an Admin-only account page would lock every Viewer out of their own credentials.
///
/// Deliberately **not** where an Admin changes somebody *else's* password: that is *Users & access*,
/// which asks for no current password because an Admin does not have one to give. The difference is
/// the whole reason this route verifies the old password and that one does not.
class AccountScreen extends ConsumerStatefulWidget {
  const AccountScreen({super.key});

  @override
  ConsumerState<AccountScreen> createState() => _AccountScreenState();
}

class _AccountScreenState extends ConsumerState<AccountScreen> {
  final _current = TextEditingController();
  final _fresh = TextEditingController();
  final _confirm = TextEditingController();

  /// On by default, and the Server's own default is the opposite.
  ///
  /// Changing a password because somebody may know it is the common case, and leaving their other
  /// sessions alive is exactly the thing that would make the change pointless. The one reason to
  /// turn it off — a routine rotation on a machine you are also signed in on elsewhere — is the
  /// rarer one, so it is the one that costs a click.
  bool _signOutEverywhere = true;

  bool _saving = false;
  String? _failure;
  bool _done = false;

  @override
  void dispose() {
    _current.dispose();
    _fresh.dispose();
    _confirm.dispose();
    super.dispose();
  }

  /// Why the form cannot be submitted yet, or null when it can.
  ///
  /// Said as a sentence rather than by grinding the button out silently: a disabled control with no
  /// reason beside it is the same dead end as a rejected save with no message.
  String? get _blocked {
    if (_current.text.isEmpty) return 'Enter your current password.';
    if (_fresh.text.length < 8) {
      return 'The new password must be at least 8 characters.';
    }
    if (_fresh.text != _confirm.text) return 'The two new passwords differ.';
    if (_fresh.text == _current.text) {
      return 'The new password is the same as the current one.';
    }
    return null;
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _failure = null;
      _done = false;
    });

    try {
      await ref
          .read(repositoryProvider)
          .changeOwnPassword(
            currentPassword: _current.text,
            newPassword: _fresh.text,
            signOutAllSessions: _signOutEverywhere,
          );

      if (!mounted) return;
      setState(() {
        _saving = false;
        _done = true;
        _current.clear();
        _fresh.clear();
        _confirm.clear();
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _saving = false;
        // The Server answers 401 for a wrong current password and says nothing more, on purpose —
        // so this names the likely cause rather than printing a bare status line.
        _failure = '$error'.contains('401')
            ? 'That is not your current password.'
            : 'Could not change the password. $error';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final compact = isCompact(context);
    final username = ref.watch(currentUsernameProvider);
    final role = ref.watch(currentRoleProvider);
    final logout = ref.watch(logoutProvider);

    final body = ListView(
      padding: EdgeInsets.fromLTRB(
        compact ? 18 : 28,
        compact ? 18 : 22,
        compact ? 18 : 28,
        28,
      ),
      children: [
        if (!compact) ...[
          const _PageHeading(),
          const SizedBox(height: 20),
          const SettingsDivider(),
          const SizedBox(height: 20),
        ],
        _profile(username, role),
        const SizedBox(height: 20),
        const SettingsDivider(),
        const SizedBox(height: 20),
        _password,
        const SizedBox(height: 20),
        const SettingsDivider(),
        const SizedBox(height: 20),
        _sessions(logout),
      ],
    );

    if (!compact)
      return DecoratedBox(
        decoration: const BoxDecoration(color: Serval.panel),
        child: body,
      );

    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          CompactAppBar(
            title: 'Account',
            onBack: () => context.go('/settings'),
          ),
          Expanded(child: body),
        ],
      ),
    );
  }

  /// Who you are signed in as, read-only.
  ///
  /// **Read-only on purpose, and the username doubly so.** An account's username is its id on the
  /// Server — `Auth.User.Id` is the lowercased form of it, and the preferences document is keyed by
  /// that — so renaming one is not an edit, it is a migration. The display name is editable by an
  /// Admin on *Users & access*; offering a field here that silently needs a role you may not have
  /// would be worse than not offering it.
  Widget _profile(String? username, Role role) => SettingsSection(
    title: 'Profile',
    blurb: const Text(
      'Who you are signed in as. Your username is also this account’s id on the Server, so it is '
      'fixed once the account exists — an admin can change the name shown around the app.',
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        NocturneReadOnlyField(
          label: 'Username',
          value: username ?? 'not signed in',
          mono: true,
        ),
        const SizedBox(height: 12),
        NocturneReadOnlyField(
          label: 'Role',
          value: role == Role.admin ? 'Admin' : 'View only',
          note: role == Role.admin
              ? 'Can add and remove cameras, change settings, delete recordings and manage '
                    'accounts.'
              : 'Can watch live, look back, see alerts, talk back and move a camera. Cannot change '
                    'how the system is set up.',
        ),
      ],
    ),
  );

  Widget get _password => SettingsSection(
    title: 'Password',
    blurb: const Text(
      'Your current password is asked for even though you are already signed in: being signed in '
      'proves the session was opened once, not that whoever is holding it now knows the password.',
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        NocturneField(
          label: 'Current password',
          controller: _current,
          obscure: true,
          enabled: !_saving,
          onChanged: (_) => setState(() => _done = false),
        ),
        const SizedBox(height: 12),
        NocturneField(
          label: 'New password',
          controller: _fresh,
          hint: 'At least 8 characters',
          obscure: true,
          enabled: !_saving,
          onChanged: (_) => setState(() => _done = false),
        ),
        const SizedBox(height: 12),
        NocturneField(
          label: 'Confirm new password',
          controller: _confirm,
          obscure: true,
          enabled: !_saving,
          onChanged: (_) => setState(() => _done = false),
        ),
        const SizedBox(height: 16),
        ToggleRow(
          title: 'Sign out everywhere else',
          description: _signOutEverywhere
              ? 'Every other browser and phone signed in as you has to sign in again. Leave this '
                    'on if anybody may know the old password.'
              : 'Other sessions stay signed in with the old password still working for them — '
                    'which is the thing a password change is usually meant to stop.',
          value: _signOutEverywhere,
          onChanged: _saving
              ? null
              : (value) => setState(() => _signOutEverywhere = value),
        ),
        const SizedBox(height: 16),
        Row(
          children: [
            NocturneButton(
              label: _saving ? 'Changing…' : 'Change password',
              variant: NocturneButtonVariant.primary,
              onPressed: _saving || _blocked != null ? null : _save,
            ),
            const SizedBox(width: 12),
            Flexible(child: _passwordNote),
          ],
        ),
      ],
    ),
  );

  Widget get _passwordNote {
    final (text, colour) = switch ((_failure, _done, _blocked)) {
      (final String failure, _, _) => (failure, Serval.alertText),
      (_, true, _) => ('Password changed.', Serval.healthyText),
      // Silent until something has been typed: a form that opens already complaining is a form
      // that has been ignored by the time it has something to say.
      (_, _, final String reason) when _current.text.isNotEmpty => (
        reason,
        Nocturne.mix(Nocturne.text, 50),
      ),
      _ => (null, Nocturne.text),
    };

    if (text == null) return const SizedBox.shrink();

    return Text(
      text,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12,
        height: 1.4,
        color: colour,
      ),
    );
  }

  /// Signing out of this browser.
  ///
  /// **There is no *sign out everywhere* button here, and that is a gap rather than a decision.**
  /// The only route that ends every session for an account is `POST /api/auth/users/{username}
  /// /sign-out`, which is Admin-only — so a Viewer pressing it would get a 403, and offering a
  /// control that works for one role and not another is worse than not offering it. The password
  /// form above reaches the same end for the case that matters, because it can pass
  /// `signOutAllSessions` on a route that takes any role.
  Widget _sessions(VoidCallback? logout) => SettingsSection(
    title: 'Sessions',
    blurb: Text(
      'Signing out clears this browser only. To end every other session, change your password '
      'above with ${_signOutEverywhere ? 'the switch left on' : 'the switch turned on'}.',
    ),
    child: Row(
      children: [
        NocturneButton(label: 'Sign out', onPressed: logout),
        if (logout == null) ...[
          const SizedBox(width: 12),
          Flexible(
            child: Text(
              'No session to sign out of.',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12,
                color: Nocturne.mix(Nocturne.text, 45),
              ),
            ),
          ),
        ],
      ],
    ),
  );
}

class _PageHeading extends StatelessWidget {
  const _PageHeading();

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      const Text(
        'Account',
        style: TextStyle(
          fontFamily: Nocturne.fontHeading,
          fontSize: 19,
          fontWeight: Nocturne.headingWeight,
          letterSpacing: -0.19,
          color: Nocturne.text,
        ),
      ),
      const SizedBox(height: 3),
      Text(
        'Yours alone · nothing here changes what anyone else signed in sees',
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 50),
        ),
      ),
    ],
  );
}
