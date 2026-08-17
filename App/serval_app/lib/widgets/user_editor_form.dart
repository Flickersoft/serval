import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../data/auth/auth_models.dart';
import '../data/auth/user_account.dart';
import '../data/time_labels.dart';
import '../screens/cameras_screen.dart' show SaveFailureNote;
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_field.dart';

/// What [UserEditorForm.onSave] is asked to persist. [password] is only ever set while
/// [UserEditorForm.creating] — editing a password goes through [UserEditorForm.onSetPassword]
/// instead, as its own immediate action rather than something batched behind *Save*.
typedef UserEditorSaveResult = ({
  String username,
  String displayName,
  Role role,
  String? password,
});

/// The add/edit editor for one account — design 3b's own annotation is that adding and editing
/// are the same editor, adding just starts empty. Structured like `CameraSettingsForm`: a local
/// copy of what can change, one controller per editable field, and a footer that only appears
/// once there is something unsaved.
class UserEditorForm extends StatefulWidget {
  const UserEditorForm({
    super.key,
    required this.account,
    required this.creating,
    required this.existingUsernames,
    required this.onSave,
    this.onDelete,
    this.onSetPassword,
    this.onSignOutAllSessions,
    this.onDiscard,
    this.externalError,
  });

  /// Null while [creating] — there is nothing to edit yet.
  final UserAccount? account;

  final bool creating;

  /// Usernames already taken, lowercased — so a new account can't claim one the Server would
  /// reject anyway. Checked client-side only to save the round trip; the Server has the real say.
  final Set<String> existingUsernames;

  final Future<void> Function(UserEditorSaveResult) onSave;

  /// Opens the remove-account dialog. Null while [creating] — there is nothing to remove yet.
  final VoidCallback? onDelete;

  /// Opens the set-new-password dialog. Null while [creating], where the password is just the
  /// first field of this same form.
  final VoidCallback? onSetPassword;

  /// Opens the sign-out-everywhere dialog, which ends the account's sessions without changing its
  /// password. Null while [creating] — a new account has no sessions.
  final VoidCallback? onSignOutAllSessions;

  /// Abandons an account being added. Null while editing, where nothing needs abandoning.
  final VoidCallback? onDiscard;

  /// A failure from [onDelete], [onSetPassword] or [onSignOutAllSessions] — those run in the
  /// parent screen, not this form, so their errors have to be handed back in rather than caught
  /// here. Shown the same way a failed [onSave] is.
  final Object? externalError;

  @override
  State<UserEditorForm> createState() => _UserEditorFormState();
}

class _UserEditorFormState extends State<UserEditorForm> {
  late final TextEditingController _displayName;
  late final TextEditingController _username;
  late final TextEditingController _password;
  late Role _role;

  bool _saving = false;
  Object? _failure;

  @override
  void initState() {
    super.initState();
    final account = widget.account;
    _displayName = TextEditingController(text: account?.displayName ?? '');
    _username = TextEditingController(text: account?.username ?? '');
    _password = TextEditingController();
    _role = account?.role ?? Role.viewer;
  }

  @override
  void dispose() {
    _displayName.dispose();
    _username.dispose();
    _password.dispose();
    super.dispose();
  }

  bool get _usernameTaken => widget.creating
      ? widget.existingUsernames.contains(_username.text.trim().toLowerCase())
      : false;

  bool get _usernameValid =>
      _username.text.trim().isNotEmpty &&
      !_username.text.trim().contains(RegExp(r'\s'));

  bool get _roleChanged => !widget.creating && _role != widget.account!.role;

  bool get _canSave {
    if (widget.creating) {
      return _displayName.text.trim().isNotEmpty &&
          _usernameValid &&
          !_usernameTaken &&
          _password.text.length >= 8;
    }
    return _roleChanged;
  }

  Future<void> _save() async {
    if (!_canSave || _saving) return;
    setState(() {
      _saving = true;
      _failure = null;
    });
    try {
      await widget.onSave((
        username: widget.creating
            ? _username.text.trim()
            : widget.account!.username,
        displayName: widget.creating
            ? _displayName.text.trim()
            : widget.account!.displayName,
        role: _role,
        password: widget.creating ? _password.text : null,
      ));
    } on Object catch (e) {
      if (mounted) setState(() => _failure = e);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Expanded(
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(24, 18, 24, 24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _Header(
                displayName: widget.creating
                    ? (_displayName.text.trim().isEmpty
                          ? 'New user'
                          : _displayName.text.trim())
                    : widget.account!.displayName,
                addedOn: widget.creating ? null : widget.account!.createdAt,
                onDelete: widget.onDelete,
              ),
              const SizedBox(height: 16),
              const SettingsDivider(),
              const SizedBox(height: 20),
              SettingsSection(
                title: 'The basics',
                blurb: const Text(
                  'The name shown around the app, and the username typed '
                  'at sign-in.',
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: widget.creating
                          ? NocturneField(
                              label: 'Name',
                              controller: _displayName,
                              hint: 'Their name',
                              onChanged: (_) => setState(() {}),
                            )
                          : NocturneReadOnlyField(
                              label: 'Name',
                              value: widget.account!.displayName,
                            ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: widget.creating
                          ? NocturneField(
                              label: 'Username',
                              controller: _username,
                              hint: 'lowercase, no spaces',
                              mono: true,
                              onChanged: (_) => setState(() {}),
                            )
                          : NocturneReadOnlyField(
                              label: 'Username',
                              value: widget.account!.username,
                              mono: true,
                            ),
                    ),
                  ],
                ),
              ),
              if (widget.creating && _usernameTaken) ...[
                const SizedBox(height: 8),
                Text(
                  'A user “${_username.text.trim()}” already exists.',
                  style: const TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12,
                    color: Serval.recordingText,
                  ),
                ),
              ],
              const SizedBox(height: 20),
              const SettingsDivider(),
              const SizedBox(height: 20),
              SettingsSection(
                title: 'What they can do',
                blurb: const Text(
                  'Two roles, nothing in between. Both can watch live, look '
                  'back, talk back and move a camera — Admin also changes '
                  'how the system is set up.',
                ),
                child: _RolePicker(
                  role: _role,
                  onChanged: (role) => setState(() => _role = role),
                ),
              ),
              if (!widget.creating) ...[
                const SizedBox(height: 20),
                const SettingsDivider(),
                const SizedBox(height: 20),
                SettingsSection(
                  title: 'Password',
                  blurb: const Text(
                    "You can't read the old one. Setting a new one signs "
                    'them out everywhere.',
                  ),
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 13,
                      vertical: 11,
                    ),
                    decoration: BoxDecoration(
                      borderRadius: BorderRadius.circular(8),
                      border: Border.all(
                        color: Nocturne.mix(Nocturne.text, 10),
                      ),
                      color: Nocturne.mix(Nocturne.text, 3),
                    ),
                    child: Row(
                      children: [
                        Expanded(
                          child: Text(
                            'Password set at creation',
                            style: TextStyle(
                              fontFamily: Nocturne.fontBody,
                              fontSize: 13,
                              color: Nocturne.mix(Nocturne.text, 72),
                            ),
                          ),
                        ),
                        // Sits beside the password action rather than under Remove: both are
                        // about an account that may be in the wrong hands, and the milder answer
                        // should be the one already in view when you reach for the other.
                        NocturneButton(
                          label: 'Sign out everywhere',
                          icon: PhosphorIconsRegular.signOut,
                          variant: NocturneButtonVariant.ghost,
                          height: 32,
                          fontSize: 12.5,
                          onPressed: widget.onSignOutAllSessions,
                        ),
                        const SizedBox(width: 8),
                        NocturneButton(
                          label: 'Set a new password',
                          icon: PhosphorIconsRegular.key,
                          height: 32,
                          fontSize: 12.5,
                          onPressed: widget.onSetPassword,
                        ),
                      ],
                    ),
                  ),
                ),
              ] else ...[
                const SizedBox(height: 20),
                const SettingsDivider(),
                const SizedBox(height: 20),
                SettingsSection(
                  title: 'Password',
                  blurb: const Text('At least 8 characters.'),
                  child: NocturneField(
                    label: 'Password',
                    controller: _password,
                    obscure: true,
                    hint: 'At least 8 characters',
                    onChanged: (_) => setState(() {}),
                  ),
                ),
              ],
              if ((_failure ?? widget.externalError) != null) ...[
                const SizedBox(height: 16),
                SaveFailureNote(error: (_failure ?? widget.externalError)!),
              ],
            ],
          ),
        ),
      ),
      if (widget.creating || _roleChanged)
        _Footer(
          creating: widget.creating,
          saving: _saving,
          canSave: _canSave,
          onSave: _save,
          onDiscard: widget.creating ? widget.onDiscard : null,
        ),
    ],
  );
}

class _Header extends StatelessWidget {
  const _Header({
    required this.displayName,
    required this.addedOn,
    this.onDelete,
  });

  final String displayName;
  final DateTime? addedOn;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      Container(
        width: 42,
        height: 42,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          color: Nocturne.mix(Nocturne.text, 8),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 16)),
        ),
        child: Text(
          displayName.isEmpty ? '?' : displayName[0].toUpperCase(),
          style: const TextStyle(
            fontFamily: Nocturne.fontHeading,
            fontSize: 16,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.text,
          ),
        ),
      ),
      const SizedBox(width: 12),
      Expanded(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              displayName,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontFamily: Nocturne.fontHeading,
                fontSize: 17,
                fontWeight: Nocturne.headingWeight,
                color: Nocturne.text,
              ),
            ),
            if (addedOn != null)
              Text(
                'Added ${dayLabel(addedOn!)}',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
          ],
        ),
      ),
      if (onDelete != null)
        NocturneButton(
          label: 'Remove user',
          icon: PhosphorIconsRegular.trash,
          variant: NocturneButtonVariant.danger,
          height: 34,
          fontSize: 13,
          onPressed: onDelete,
        ),
    ],
  );
}

/// The two-card Admin / View-only choice — a radio pair rather than a dropdown, so what each
/// role gives up is visible at the moment it's chosen, per the design's own annotation.
class _RolePicker extends StatelessWidget {
  const _RolePicker({required this.role, required this.onChanged});

  final Role role;
  final ValueChanged<Role> onChanged;

  static const _adminCan = [
    'Add, edit and remove cameras',
    'Change settings',
    'Delete recordings',
    'Manage people, including this page',
  ];

  static const _viewerCan = [
    'Watch live and look back',
    'See alerts and descriptions',
    'Talk back and move a camera',
  ];

  static const _viewerCannot = [
    'No adding, editing or removing cameras',
    'No settings, no deleting footage',
  ];

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Expanded(
        child: _RoleCard(
          label: 'Admin',
          icon: PhosphorIconsRegular.shieldCheck,
          selected: role == Role.admin,
          can: _adminCan,
          cannot: const [],
          onTap: () => onChanged(Role.admin),
        ),
      ),
      const SizedBox(width: 12),
      Expanded(
        child: _RoleCard(
          label: 'View only',
          icon: PhosphorIconsFill.eye,
          selected: role == Role.viewer,
          can: _viewerCan,
          cannot: _viewerCannot,
          onTap: () => onChanged(Role.viewer),
        ),
      ),
    ],
  );
}

class _RoleCard extends StatelessWidget {
  const _RoleCard({
    required this.label,
    required this.icon,
    required this.selected,
    required this.can,
    required this.cannot,
    required this.onTap,
  });

  final String label;
  final PhosphorIconData icon;
  final bool selected;
  final List<String> can;
  final List<String> cannot;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => MouseRegion(
    cursor: SystemMouseCursors.click,
    child: GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.all(13),
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(8),
          border: Border.all(
            color: selected
                ? Nocturne.mix(Nocturne.accent, 45)
                : Nocturne.mix(Nocturne.text, 10),
          ),
          color: selected
              ? Nocturne.mix(Nocturne.accent, 10)
              : Nocturne.mix(Nocturne.text, 3),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  width: 17,
                  height: 17,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: selected ? Nocturne.mix(Nocturne.accent, 20) : null,
                    border: Border.all(
                      color: selected
                          ? Nocturne.mix(Nocturne.accent, 80)
                          : Nocturne.mix(Nocturne.text, 28),
                    ),
                  ),
                  child: selected
                      ? Container(
                          width: 7,
                          height: 7,
                          decoration: const BoxDecoration(
                            shape: BoxShape.circle,
                            color: Nocturne.accent300,
                          ),
                        )
                      : null,
                ),
                const SizedBox(width: 9),
                Text(
                  label,
                  style: const TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 13.5,
                    fontWeight: Nocturne.headingWeight,
                    color: Nocturne.text,
                  ),
                ),
                const Spacer(),
                PhosphorIcon(
                  icon,
                  size: 16,
                  color: selected
                      ? Nocturne.accent400
                      : Nocturne.mix(Nocturne.text, 40),
                ),
              ],
            ),
            const SizedBox(height: 5),
            Padding(
              padding: const EdgeInsets.only(left: 26),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  for (final line in can) _RoleBullet(text: line, has: true),
                  for (final line in cannot)
                    _RoleBullet(text: line, has: false),
                ],
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _RoleBullet extends StatelessWidget {
  const _RoleBullet({required this.text, required this.has});

  final String text;
  final bool has;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 5),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 1),
          child: PhosphorIcon(
            has ? PhosphorIconsRegular.check : PhosphorIconsRegular.x,
            size: 13,
            color: has ? Serval.healthy : Nocturne.mix(Nocturne.text, 35),
          ),
        ),
        const SizedBox(width: 7),
        Expanded(
          child: Text(
            text,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12,
              height: 1.4,
              color: has
                  ? Nocturne.mix(Nocturne.text, 60)
                  : Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ),
      ],
    ),
  );
}

class _Footer extends StatelessWidget {
  const _Footer({
    required this.creating,
    required this.saving,
    required this.canSave,
    required this.onSave,
    this.onDiscard,
  });

  final bool creating;
  final bool saving;
  final bool canSave;
  final VoidCallback onSave;
  final VoidCallback? onDiscard;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 14),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(top: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        if (!creating)
          Expanded(
            child: Row(
              children: [
                PhosphorIcon(
                  PhosphorIconsFill.circle,
                  size: 8,
                  color: Serval.alert,
                ),
                const SizedBox(width: 7),
                const Expanded(
                  child: Text(
                    'One change not saved yet',
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 12.5,
                      color: Serval.alertText,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
          )
        else
          const Spacer(),
        if (onDiscard != null) ...[
          NocturneButton(
            label: 'Discard',
            variant: NocturneButtonVariant.ghost,
            onPressed: saving ? null : onDiscard,
          ),
          const SizedBox(width: 8),
        ],
        NocturneButton(
          label: saving
              ? 'Saving…'
              : creating
              ? 'Add user'
              : 'Save user',
          icon: PhosphorIconsRegular.check,
          variant: NocturneButtonVariant.primary,
          onPressed: canSave && !saving ? onSave : null,
        ),
      ],
    ),
  );
}
