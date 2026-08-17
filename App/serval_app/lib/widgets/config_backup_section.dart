import 'package:flutter/widgets.dart';

import '../data/time_labels.dart';
import '../models/config_backup.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_dialog.dart';
import 'nocturne_field.dart';
import 'storage_bar.dart';

/// The one part of the *Server status* page that does something: take the configuration out to a
/// file, and put one back.
///
/// **Why it is here at all**, on a page whose own doc comment says nothing on it is a setting.
/// Everything a backup covers lives on some other page — cameras here, settings there, accounts
/// somewhere else — so filing it under any one of them would claim it was about that one. It is
/// about the Server, which is what this page is, and it sits at the bottom because it is the last
/// thing you reach and it carries a warning worth reading on the way past.
///
/// Prop-driven like everything else under `widgets/`: the screen owns the busy and status strings
/// and passes them down, so the whole section can be pumped into a widget test in any state.
class ConfigBackupSection extends StatelessWidget {
  const ConfigBackupSection({
    super.key,
    required this.onBackup,
    required this.onRestore,
    this.busy,
    this.status,
    this.error,
  });

  final VoidCallback onBackup;
  final VoidCallback onRestore;

  /// What is happening right now — `Preparing the file…`, `Restoring…`. Null when idle, which is
  /// also what re-enables the two buttons.
  final String? busy;

  /// The last thing that finished, in the words the platform can honestly use: on desktop the file
  /// and where it went, on web the file alone, because the browser chose the directory and will not
  /// say which.
  final String? status;

  final String? error;

  bool get _idle => busy == null;

  @override
  Widget build(BuildContext context) => SettingsSection(
    title: 'Configuration backup',
    blurb: Text(
      'Cameras, server settings, accounts and preferences in one file. '
      'Recorded footage, detections and telemetry are not in it.',
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12.5,
        height: 1.45,
        color: Nocturne.mix(Nocturne.text, 50),
      ),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // The page's one kind of warning box, spending Serval.alert. Read before the button is
        // reached, which is why it is here rather than in the section's `trailing` slot.
        const VitalsAlertStrip(
          message:
              'The file holds your camera passwords and every account’s password '
              'hash in plain text. Keep it where you keep those passwords.',
          critical: false,
          boxed: true,
          padding: EdgeInsets.symmetric(horizontal: 14, vertical: 13),
        ),
        const SizedBox(height: 14),
        // A Row rather than a Wrap, though the pair would fit either way. A Wrap hands its children
        // the full width as a loose constraint, and a NocturneButton is a Container with an
        // alignment — which expands to fill a loose constraint rather than shrink-wrapping. The two
        // buttons come out full width, stacked. A Row's unbounded main axis is what makes them size
        // to their labels, which is the same reason the settings save bar is built this way.
        Row(
          mainAxisAlignment: MainAxisAlignment.start,
          children: [
            NocturneButton(
              label: 'Download backup',
              variant: NocturneButtonVariant.primary,
              // Disabled while something is in flight rather than hidden — a button that is not
              // there reads as a bug, one that is there and inert reads as a condition unmet.
              onPressed: _idle ? onBackup : null,
            ),
            const SizedBox(width: 10),
            NocturneButton(
              label: 'Restore from file…',
              onPressed: _idle ? onRestore : null,
            ),
          ],
        ),
        if (busy != null) ...[
          const SizedBox(height: 12),
          Text(
            busy!,
            style: monoStyle(
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ] else if (error != null) ...[
          const SizedBox(height: 12),
          Text(
            error!,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.4,
              color: Serval.alertText,
            ),
          ),
        ] else if (status != null) ...[
          const SizedBox(height: 12),
          Text(
            status!,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              height: 1.4,
              color: Nocturne.mix(Nocturne.text, 50),
            ),
          ),
        ],
      ],
    ),
  );
}

/// Said before the file is written, because afterwards it is too late to un-write it.
///
/// Primary rather than danger: nothing here is destroyed, and the risk is entirely in what happens
/// to the file next. Spending the danger variant on it would file this alongside deleting a camera,
/// which is a different kind of mistake with a different kind of recovery.
class ConfirmBackupDialog extends StatelessWidget {
  const ConfirmBackupDialog({super.key});

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Download the configuration?',
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _Paragraph(
          'This file holds every camera’s ONVIF password, any user:password inside '
          'a stream URL, and every account’s password hash. Anyone who opens it can '
          'sign in to your cameras, and can work on your account passwords at their '
          'leisure with no lockout to stop them.',
        ),
        const SizedBox(height: 12),
        _Paragraph(
          'It contains no recorded footage, no detections and no telemetry.',
        ),
        const SizedBox(height: 12),
        _Paragraph(
          'Keep it where you keep the passwords themselves. Not e-mail, not a '
          'shared drive, not a repository.',
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
        label: 'Download anyway',
        onPressed: () => Navigator.of(context).pop(true),
      ),
    ],
  );
}

/// Said after the file is picked, so it can name what is about to happen rather than describing
/// restores in general.
///
/// No type-to-confirm. [ConfirmDeleteDialog] earns its typed name because the registry carries no
/// undo; a restore deletes nothing and is reversible by restoring the previous file, and asking
/// somebody to type a name for a bounded action only teaches them to type names.
class ConfirmRestoreDialog extends StatelessWidget {
  const ConfirmRestoreDialog({
    super.key,
    required this.fileName,
    required this.summary,
  });

  final String fileName;
  final ConfigBackupSummary summary;

  @override
  Widget build(BuildContext context) {
    final taken = summary.createdAt;
    final by = summary.createdBy;

    return NocturneDialog(
      title: 'Restore from $fileName?',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (taken != null)
            _Paragraph(
              by == null
                  ? 'Taken ${dayLabel(taken)} at ${clockLabel(taken)}.'
                  : 'Taken ${dayLabel(taken)} at ${clockLabel(taken)}, by $by.',
            ),
          if (taken != null) const SizedBox(height: 12),
          _Paragraph('${summary.contents} will be created or overwritten.'),
          const SizedBox(height: 12),
          _Paragraph(
            'Nothing is deleted. A camera, setting or account this Server has that '
            'the file does not name is left exactly as it is. Anything the file '
            'does name is replaced whole, not merged field by field.',
          ),
          const SizedBox(height: 12),
          _Paragraph(
            'Accounts whose password the file changes will be signed out of every '
            'device. Your own account’s role and password are left alone.',
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
          label: 'Restore',
          onPressed: () => Navigator.of(context).pop(true),
        ),
      ],
    );
  }
}

/// What the restore came to, section by section, with the Server's own words for anything it
/// refused.
///
/// The outcome is in the title rather than only in the body: a restore with two skips is a
/// different event from a clean one, and somebody who closes the dialog on the strength of the
/// heading should not have been misled by it.
class RestoreResultDialog extends StatelessWidget {
  const RestoreResultDialog({super.key, required this.result});

  final ConfigRestoreResult result;

  @override
  Widget build(BuildContext context) {
    final skips = result.skipped;

    return NocturneDialog(
      width: 520,
      title: skips.isEmpty
          ? 'Restored'
          : 'Restored, with ${skips.length} skipped',
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          for (final section in result.sections) ...[
            _SectionRow(section: section),
            if (section != result.sections.last) const SizedBox(height: 8),
          ],
          if (skips.isNotEmpty) ...[
            const SizedBox(height: 16),
            const SettingsDivider(),
            const SizedBox(height: 14),
            ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 220),
              child: SingleChildScrollView(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    for (final skip in skips) ...[
                      _Skip(skip: skip),
                      if (skip != skips.last) const SizedBox(height: 12),
                    ],
                  ],
                ),
              ),
            ),
          ],
          if (result.notes.isNotEmpty) ...[
            const SizedBox(height: 16),
            for (final note in result.notes) ...[
              _Paragraph(note),
              if (note != result.notes.last) const SizedBox(height: 8),
            ],
          ],
        ],
      ),
      actions: [
        NocturneButton(
          label: 'Close',
          variant: NocturneButtonVariant.secondary,
          onPressed: () => Navigator.of(context).pop(),
        ),
      ],
    );
  }
}

class _SectionRow extends StatelessWidget {
  const _SectionRow({required this.section});

  final RestoreSection section;

  @override
  Widget build(BuildContext context) => Row(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      SizedBox(
        width: 108,
        child: Text(
          section.label,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            color: Nocturne.mix(Nocturne.text, 72),
          ),
        ),
      ),
      Expanded(
        child: Text(
          section.summary,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            // A section with nothing to do is stated more quietly than one that changed something,
            // so the rows that matter are the ones the eye lands on.
            color: Nocturne.mix(Nocturne.text, section.isEmpty ? 40 : 62),
          ),
        ),
      ),
    ],
  );
}

class _Skip extends StatelessWidget {
  const _Skip({required this.skip});

  final RestoreSkip skip;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        skip.item,
        style: monoStyle(
          fontSize: 11.5,
          color: Nocturne.mix(Nocturne.text, 70),
        ),
      ),
      const SizedBox(height: 3),
      // Verbatim. The Server writes these for the person who caused them — a refused transcode
      // names the encoder and the fix — and anything said here instead would be vaguer.
      Text(
        skip.reason,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          height: 1.45,
          color: Nocturne.mix(Nocturne.text, 55),
        ),
      ),
    ],
  );
}

class _Paragraph extends StatelessWidget {
  const _Paragraph(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Text(
    text,
    style: TextStyle(
      fontFamily: Nocturne.fontBody,
      fontSize: 13,
      height: 1.5,
      color: Nocturne.mix(Nocturne.text, 62),
    ),
  );
}
