import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/auth/auth_models.dart';
import '../data/auth/user_account.dart';
import '../data/providers.dart';
import '../data/serval_repository.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import '../widgets/compact_app_bar.dart';
import '../widgets/nocturne_button.dart';
import '../widgets/nocturne_dialog.dart';
import '../widgets/roster_widgets.dart';
import '../widgets/settings_nav.dart';
import '../widgets/user_dialogs.dart';
import '../widgets/user_editor_form.dart';
import 'cameras_screen.dart' show SaveFailureNote;

/// Accounts, and the editor for the one you pick — design 3b, reached the same way
/// [CamerasScreen] is: the rail's gear, or [SettingsNav]'s own switcher between the two.
///
/// Admin-only: the Server's `/api/auth/users` routes refuse anyone else, and [SettingsNav] hides
/// the tab that leads here unless the signed-in account is one.
class UsersScreen extends ConsumerStatefulWidget {
  const UsersScreen({super.key});

  @override
  ConsumerState<UsersScreen> createState() => _UsersScreenState();
}

class _UsersScreenState extends ConsumerState<UsersScreen> {
  final _search = TextEditingController();

  late final ServalRepository _repository = ref.read(repositoryProvider);

  /// Which account the person clicked, not which one is on screen.
  ///
  /// The resolved selection is derived per build instead of stored, so an account that is deleted
  /// — or a name typed into *Add* that never got created — falls back to the first in the list
  /// without anything having to notice and write the correction back.
  String? _selectedUsername;
  bool _adding = false;

  /// A failure from delete or set-password — both run here rather than inside
  /// [UserEditorForm], since neither is the form's own *Save*.
  Object? _actionError;

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  /// Re-reads the roster. There is no socket for accounts, so every mutation ends here.
  void _refresh() => ref.invalidate(usersProvider);

  void _startAdd() => setState(() {
    _adding = true;
    _actionError = null;
  });

  void _select(String username) => setState(() {
    _adding = false;
    _actionError = null;
    _selectedUsername = username;
  });

  /// Back to the roster: the drill-down's way out, and where a save lands.
  void _closeEditor() => setState(() {
    _adding = false;
    _actionError = null;
    _selectedUsername = null;
  });

  Future<void> _save(UserEditorSaveResult result) async {
    if (_adding) {
      await _repository.createUser(
        username: result.username,
        displayName: result.displayName,
        password: result.password!,
        role: result.role,
      );
    } else {
      await _repository.updateUser(result.username, role: result.role);
    }
    if (!mounted) return;

    // Point the selection at what was just saved *before* the re-read lands, so the derivation in
    // `build` picks it up as soon as the new roster arrives. Narrow, the save is the end of the
    // visit and the roster is where it returns to.
    if (isCompact(context)) {
      _closeEditor();
    } else {
      setState(() {
        _adding = false;
        _selectedUsername = result.username;
      });
    }
    _refresh();
  }

  Future<void> _delete(UserAccount account) async {
    final confirmed = await showNocturneDialog<bool>(
      context: context,
      builder: (context) =>
          ConfirmRemoveUserDialog(displayName: account.displayName),
    );
    if (confirmed != true || !mounted) return;

    try {
      await _repository.deleteUser(account.username);
      if (!mounted) return;
      _closeEditor();
      _refresh();
    } on Object catch (e) {
      if (mounted) setState(() => _actionError = e);
    }
  }

  Future<void> _setPassword(UserAccount account) async {
    final result = await showNocturneDialog<SetPasswordResult>(
      context: context,
      builder: (context) => SetPasswordDialog(displayName: account.displayName),
    );
    if (result == null || !mounted) return;

    try {
      await _repository.updateUser(
        account.username,
        password: result.password,
        signOutAllSessions: result.signOutAllSessions,
      );
      if (mounted) setState(() => _actionError = null);
    } on Object catch (e) {
      if (mounted) setState(() => _actionError = e);
    }
  }

  /// Ending an account's sessions without touching its password — for when the concern is where
  /// someone is signed in rather than what they know.
  Future<void> _signOutAllSessions(UserAccount account) async {
    final confirmed = await showNocturneDialog<bool>(
      context: context,
      builder: (context) =>
          ConfirmSignOutUserDialog(displayName: account.displayName),
    );
    if (confirmed != true || !mounted) return;

    try {
      await _repository.signOutUser(account.username);
      if (mounted) setState(() => _actionError = null);
    } on Object catch (e) {
      if (mounted) setState(() => _actionError = e);
    }
  }

  UserAccount? _accountByUsername(List<UserAccount> users, String? username) {
    for (final user in users) {
      if (user.username == username) return user;
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final currentUsername = ref.watch(currentUsernameProvider);

    // The rail and the settings sidebar are drawn by `ServalShell` and `SettingsShell` — this
    // screen is what goes to the right of both.
    return DecoratedBox(
      decoration: const BoxDecoration(color: Serval.panel),
      child: Row(
        children: [
          Expanded(
            child: ref
                .watch(usersProvider)
                .when(
                  loading: () => const _LoadState(error: null),
                  error: (error, _) =>
                      _LoadState(error: error, onRetry: _refresh),
                  data: (users) =>
                      _buildRoster(users, currentUsername: currentUsername),
                ),
          ),
        ],
      ),
    );
  }

  Widget _buildRoster(List<UserAccount> users, {String? currentUsername}) {
    final list = _UserList(
      users: users,
      search: _search,
      selectedUsername: _adding ? null : _selectedUsername,
      adding: _adding,
      currentUsername: currentUsername,
      onSelect: _select,
      onSearchChanged: () => setState(() {}),
      compact: isCompact(context),
    );

    if (isCompact(context)) {
      // The roster and the account are two screens, the way the camera registry's are: an editor
      // squeezed beside a 344px list has nothing left to be edited in.
      final open = _adding
          ? null
          : _accountByUsername(users, _selectedUsername);

      if (_adding || open != null) {
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            CompactAppBar(
              title: _adding ? 'New account' : open!.displayName,
              onBack: _closeEditor,
            ),
            Expanded(child: _buildEditor(users)),
          ],
        );
      }

      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _ContentHeader(count: users.length, onAdd: _startAdd, compact: true),
          Expanded(child: list),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _ContentHeader(count: users.length, onAdd: _startAdd),
        Expanded(
          child: Row(
            children: [
              list,
              Expanded(child: _buildEditor(users)),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildEditor(List<UserAccount> users) {
    if (_adding) {
      return UserEditorForm(
        key: const ValueKey('__draft__'),
        account: null,
        creating: true,
        existingUsernames: {for (final user in users) user.username},
        onSave: _save,
        onDiscard: () => setState(() => _adding = false),
      );
    }

    // Derived rather than stored: whatever was clicked, falling back to the first account. Storing
    // the fallback instead would have a re-fetch silently pin the selection to a stale username.
    final selected =
        _accountByUsername(users, _selectedUsername) ?? users.firstOrNull;
    if (selected == null) {
      return const EmptyRoster(
        icon: PhosphorIconsRegular.users,
        title: 'No accounts yet',
        body:
            'Add one and they can sign in with the username and password '
            'you set.',
      );
    }

    return UserEditorForm(
      key: ValueKey(selected.username),
      account: selected,
      creating: false,
      existingUsernames: const {},
      onSave: _save,
      onDelete: () => _delete(selected),
      onSetPassword: () => _setPassword(selected),
      onSignOutAllSessions: () => _signOutAllSessions(selected),
      externalError: _actionError,
    );
  }
}

class _ContentHeader extends StatelessWidget {
  const _ContentHeader({
    required this.count,
    required this.onAdd,
    this.compact = false,
  });

  final int count;
  final VoidCallback onAdd;

  /// The app bar form: the title on the bar with *Add user* as a glyph beside it, and the sentence
  /// on the band below.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final blurb =
        '$count ${count == 1 ? 'account' : 'accounts'} on this machine '
        '· anyone signing in uses one of them';

    if (compact) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          CompactAppBar(
            title: 'Users & access',
            onBack: () => context.go('/settings'),
            actions: [
              CompactBarAction(
                icon: PhosphorIconsRegular.userPlus,
                tooltip: 'Add user',
                onPressed: onAdd,
              ),
            ],
          ),
          CompactSubBar(
            children: [
              Text(
                blurb,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12,
                  height: 1.45,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
            ],
          ),
        ],
      );
    }

    return Container(
      padding: const EdgeInsets.fromLTRB(24, 18, 24, 16),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: Serval.hairline)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Users & access',
                style: TextStyle(
                  fontFamily: Nocturne.fontHeading,
                  fontSize: 20,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                  letterSpacing: -0.01 * 20,
                ),
              ),
              const SizedBox(height: 5),
              Text(
                blurb,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
            ],
          ),
          const Spacer(),
          NocturneButton(
            label: 'Add user',
            icon: PhosphorIconsRegular.userPlus,
            height: 34,
            fontSize: 13,
            onPressed: onAdd,
          ),
        ],
      ),
    );
  }
}

/// The 344px list: search, and every account grouped Admins / View only.
class _UserList extends StatelessWidget {
  const _UserList({
    required this.users,
    required this.search,
    required this.selectedUsername,
    required this.adding,
    required this.currentUsername,
    required this.onSelect,
    required this.onSearchChanged,
    this.compact = false,
  });

  final List<UserAccount> users;
  final TextEditingController search;
  final String? selectedUsername;
  final bool adding;
  final String? currentUsername;
  final ValueChanged<String> onSelect;
  final VoidCallback onSearchChanged;

  /// Takes the whole screen, with the editor a screen after it rather than a pane beside it.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final query = search.text.trim().toLowerCase();
    final matching = [
      for (final user in users)
        if (query.isEmpty ||
            user.displayName.toLowerCase().contains(query) ||
            user.username.toLowerCase().contains(query))
          user,
    ];

    final admins = [
      for (final user in matching)
        if (user.role == Role.admin) user,
    ];
    final viewers = [
      for (final user in matching)
        if (user.role == Role.viewer) user,
    ];

    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: compact
              ? const EdgeInsets.fromLTRB(18, 12, 18, 12)
              : const EdgeInsets.all(16),
          child: SearchBox(
            controller: search,
            onChanged: onSearchChanged,
            placeholder: 'Search people',
          ),
        ),
        Expanded(
          child: ListView(
            padding: compact
                ? const EdgeInsets.fromLTRB(8, 0, 8, 10)
                : const EdgeInsets.fromLTRB(10, 0, 10, 10),
            children: [
              if (adding)
                const DraftRow(
                  icon: PhosphorIconsRegular.userPlus,
                  label: 'New user',
                  margin: EdgeInsets.only(bottom: 6),
                ),
              if (matching.isEmpty)
                EmptyNote(
                  query.isEmpty
                      ? 'No accounts yet.'
                      : 'Nothing matches “${search.text.trim()}”.',
                ),
              if (admins.isNotEmpty) ...[
                _GroupHeading('Admins · ${admins.length}'),
                for (final user in admins)
                  _UserRow(
                    account: user,
                    isSelf: user.username == currentUsername,
                    selected: !adding && user.username == selectedUsername,
                    onTap: () => onSelect(user.username),
                  ),
              ],
              if (viewers.isNotEmpty) ...[
                _GroupHeading('View only · ${viewers.length}'),
                for (final user in viewers)
                  _UserRow(
                    account: user,
                    isSelf: user.username == currentUsername,
                    selected: !adding && user.username == selectedUsername,
                    onTap: () => onSelect(user.username),
                  ),
              ],
            ],
          ),
        ),
      ],
    );

    if (compact) return body;

    return Container(
      width: 344,
      decoration: BoxDecoration(
        border: Border(right: BorderSide(color: Serval.hairline)),
      ),
      child: body,
    );
  }
}

class _GroupHeading extends StatelessWidget {
  const _GroupHeading(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(8, 12, 8, 6),
    child: Text(
      text.toUpperCase(),
      style: TextStyle(
        fontFamily: Nocturne.fontMono,
        fontSize: 10,
        letterSpacing: 0.14 * 10,
        color: Nocturne.mix(Nocturne.text, 35),
      ),
    ),
  );
}

class _UserRow extends StatefulWidget {
  const _UserRow({
    required this.account,
    required this.isSelf,
    required this.selected,
    required this.onTap,
  });

  final UserAccount account;
  final bool isSelf;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_UserRow> createState() => _UserRowState();
}

class _UserRowState extends State<_UserRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final account = widget.account;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: widget.selected
                ? Nocturne.mix(Nocturne.accent, 16)
                : _hovered
                ? Nocturne.mix(Nocturne.text, 5)
                : null,
            borderRadius: BorderRadius.circular(8),
            border: widget.selected
                ? Border.all(color: Nocturne.mix(Nocturne.accent, 40))
                : Border.all(color: const Color(0x00000000)),
          ),
          child: Row(
            children: [
              Container(
                width: 32,
                height: 32,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: account.role == Role.admin
                      ? Nocturne.mix(Nocturne.accent, 20)
                      : Nocturne.mix(Nocturne.text, 8),
                  border: Border.all(
                    color: account.role == Role.admin
                        ? Nocturne.mix(Nocturne.accent, 40)
                        : Nocturne.mix(Nocturne.text, 16),
                  ),
                ),
                child: Text(
                  account.displayName.isEmpty
                      ? '?'
                      : account.displayName[0].toUpperCase(),
                  style: TextStyle(
                    fontFamily: Nocturne.fontHeading,
                    fontSize: 13,
                    fontWeight: Nocturne.headingWeight,
                    color: account.role == Role.admin
                        ? Nocturne.accent300
                        : Nocturne.mix(Nocturne.text, 80),
                  ),
                ),
              ),
              const SizedBox(width: 11),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            account.displayName,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              fontFamily: Nocturne.fontBody,
                              fontSize: 13.5,
                              fontWeight: Nocturne.headingWeight,
                              color: Nocturne.mix(Nocturne.text, 90),
                            ),
                          ),
                        ),
                        if (widget.isSelf) ...[
                          const SizedBox(width: 7),
                          Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 6,
                              vertical: 1,
                            ),
                            decoration: BoxDecoration(
                              borderRadius: BorderRadius.circular(5),
                              border: Border.all(
                                color: Nocturne.mix(Nocturne.text, 12),
                              ),
                            ),
                            child: Text(
                              'you',
                              style: TextStyle(
                                fontFamily: Nocturne.fontBody,
                                fontSize: 11,
                                color: Nocturne.mix(Nocturne.text, 45),
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),
                    Text(
                      account.username,
                      style: TextStyle(
                        fontFamily: Nocturne.fontMono,
                        fontSize: 11.5,
                        color: Nocturne.mix(Nocturne.text, 50),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _LoadState extends StatelessWidget {
  const _LoadState({required this.error, this.onRetry});

  final Object? error;

  /// Only supplied alongside an [error] — there is nothing to retry while the first read is still
  /// in flight.
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 420),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (error == null)
            Text(
              'Loading accounts…',
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 13,
                color: Nocturne.mix(Nocturne.text, 50),
              ),
            )
          else ...[
            SaveFailureNote(error: error!),
            const SizedBox(height: 14),
            NocturneButton(label: 'Try again', onPressed: onRetry),
          ],
        ],
      ),
    ),
  );
}
