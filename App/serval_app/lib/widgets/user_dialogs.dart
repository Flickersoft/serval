import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_dialog.dart';
import 'nocturne_field.dart';
import 'nocturne_toggle.dart';

/// Removing an account, behind its own name.
///
/// Same type-to-confirm shape as [ConfirmDeleteDialog] in `nocturne_dialog.dart`, but its own
/// widget rather than that one reused: the copy is specific to an account, not a camera —
/// removing a user never touches anything they recorded, which is worth saying explicitly, since
/// "remove" reads as "erase" otherwise.
class ConfirmRemoveUserDialog extends StatefulWidget {
  const ConfirmRemoveUserDialog({super.key, required this.displayName});

  final String displayName;

  @override
  State<ConfirmRemoveUserDialog> createState() =>
      _ConfirmRemoveUserDialogState();
}

class _ConfirmRemoveUserDialogState extends State<ConfirmRemoveUserDialog> {
  final _typed = TextEditingController();

  @override
  void dispose() {
    _typed.dispose();
    super.dispose();
  }

  bool get _matches =>
      _typed.text.trim().toLowerCase() ==
      widget.displayName.trim().toLowerCase();

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Remove ${widget.displayName}?',
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          "${widget.displayName} can no longer sign in. This won't touch "
          "anything they've recorded — footage belongs to the house, not "
          'the account.',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            height: 1.5,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
        ),
        const SizedBox(height: 16),
        NocturneField(
          label: 'Type “${widget.displayName}” to confirm',
          controller: _typed,
          onChanged: (_) => setState(() {}),
        ),
      ],
    ),
    actions: [
      NocturneButton(
        label: 'Cancel',
        variant: NocturneButtonVariant.ghost,
        onPressed: () => Navigator.of(context).pop(false),
      ),
      NocturneButton(
        label: 'Remove user',
        variant: NocturneButtonVariant.danger,
        onPressed: _matches ? () => Navigator.of(context).pop(true) : null,
      ),
    ],
  );
}

/// What [SetPasswordDialog] returns: the new password, and whether to end the account's existing
/// sessions at the same time.
///
/// The two travel together because the Server applies them in one request — a password change that
/// also revokes is atomic there, and splitting it here would let one half succeed.
class SetPasswordResult {
  const SetPasswordResult({
    required this.password,
    required this.signOutAllSessions,
  });

  final String password;
  final bool signOutAllSessions;
}

/// Ending an account's sessions, which is recoverable — they sign in again with the password they
/// already have — so it confirms with a button rather than by typing the name back.
class ConfirmSignOutUserDialog extends StatelessWidget {
  const ConfirmSignOutUserDialog({super.key, required this.displayName});

  final String displayName;

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Sign out $displayName everywhere?',
    body: Text(
      'Every device $displayName is signed in on has to sign in again. Their '
      'password does not change, so this ends the sessions rather than the '
      'access — reach for it when you know where they are signed in and would '
      'rather they were not.',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 13,
        height: 1.5,
        color: Nocturne.mix(Nocturne.text, 62),
      ),
    ),
    actions: [
      NocturneButton(
        label: 'Cancel',
        variant: NocturneButtonVariant.ghost,
        onPressed: () => Navigator.of(context).pop(false),
      ),
      NocturneButton(
        label: 'Sign out everywhere',
        variant: NocturneButtonVariant.primary,
        onPressed: () => Navigator.of(context).pop(true),
      ),
    ],
  );
}

/// You can't read the old password back, so setting a new one is its own small form rather than
/// a field on the main editor — the design's own reasoning for keeping it a separate action.
///
/// Purely a form: it validates and returns a [SetPasswordResult] (`Navigator.pop(result)`), it does
/// not call the Server itself — the caller submits it the same way the rest of
/// `UserEditorForm`'s saves are submitted, so one place shows the Server's own error text.
class SetPasswordDialog extends StatefulWidget {
  const SetPasswordDialog({super.key, required this.displayName});

  final String displayName;

  @override
  State<SetPasswordDialog> createState() => _SetPasswordDialogState();
}

class _SetPasswordDialogState extends State<SetPasswordDialog> {
  final _password = TextEditingController();
  final _confirm = TextEditingController();

  /// Defaults to on. Someone is usually here because the old password is no longer trusted, and
  /// leaving the sessions that knew it running is the case this option exists to close.
  bool _signOutAll = true;

  @override
  void dispose() {
    _password.dispose();
    _confirm.dispose();
    super.dispose();
  }

  /// Matches the Server's own `UserRepository.Validate` — 8 characters is the actual rule; saying
  /// so here is cheaper than a round trip that says the same thing back.
  String? get _problem {
    if (_password.text.length < 8) {
      return 'Password must be at least 8 characters.';
    }
    if (_confirm.text != _password.text) return "Passwords don't match.";
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final problem = _password.text.isEmpty && _confirm.text.isEmpty
        ? null
        : _problem;

    return NocturneDialog(
      title: 'Set a new password for ${widget.displayName}',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          NocturneField(
            label: 'New password',
            controller: _password,
            obscure: true,
            onChanged: (_) => setState(() {}),
          ),
          const SizedBox(height: 12),
          NocturneField(
            label: 'Confirm password',
            controller: _confirm,
            obscure: true,
            onChanged: (_) => setState(() {}),
          ),
          const SizedBox(height: 16),
          _SignOutAllOption(
            value: _signOutAll,
            onChanged: (value) => setState(() => _signOutAll = value),
            description: _signOutAll
                ? 'Every device ${widget.displayName} is signed in on has to sign in '
                      'again with the new password.'
                : 'Devices ${widget.displayName} is already signed in on stay signed '
                      'in, for up to 30 days, without ever using the new password.',
          ),
          if (problem != null) ...[
            const SizedBox(height: 10),
            Text(
              problem,
              style: const TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: 12,
                color: Serval.recordingText,
              ),
            ),
          ],
        ],
      ),
      actions: [
        NocturneButton(
          label: 'Cancel',
          variant: NocturneButtonVariant.ghost,
          onPressed: () => Navigator.of(context).pop(),
        ),
        NocturneButton(
          label: 'Set new password',
          variant: NocturneButtonVariant.primary,
          onPressed: _problem == null
              ? () => Navigator.of(context).pop(
                  SetPasswordResult(
                    password: _password.text,
                    signOutAllSessions: _signOutAll,
                  ),
                )
              : null,
        ),
      ],
    );
  }
}

/// The "sign out everywhere too" switch, with a line underneath saying what the current position
/// actually means.
///
/// The consequence is spelled out rather than left to the label because this is the one control
/// here whose off position is the surprising one: a password change that leaves the old sessions
/// running is not what "changed the password" sounds like it did.
class _SignOutAllOption extends StatelessWidget {
  const _SignOutAllOption({
    required this.value,
    required this.onChanged,
    required this.description,
  });

  final bool value;
  final ValueChanged<bool> onChanged;
  final String description;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'Sign out all sessions',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13,
                  color: Nocturne.text,
                ),
              ),
            ),
            NocturneToggle(value: value, onChanged: onChanged, compact: true),
          ],
        ),
        const SizedBox(height: 6),
        Text(
          description,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12,
            height: 1.5,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
        ),
      ],
    );
  }
}
