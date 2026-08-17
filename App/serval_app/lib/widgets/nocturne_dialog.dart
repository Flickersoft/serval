import 'package:flutter/material.dart' show showDialog;
import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_button.dart';
import 'nocturne_field.dart';

/// Nocturne's `.dialog-backdrop` + `.dialog`: a panel at the top elevation over a dimmed ground.
class NocturneDialog extends StatelessWidget {
  const NocturneDialog({
    super.key,
    required this.title,
    required this.body,
    required this.actions,
    this.width = 460,
  });

  final String title;
  final Widget body;
  final List<Widget> actions;
  final double width;

  @override
  Widget build(BuildContext context) => Center(
    child: Container(
      width: width,
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: Serval.rail,
        borderRadius: BorderRadius.circular(Nocturne.radiusLg),
        border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
        // The system's top elevation: an edge plus ambient darkness, never stacked shadows.
        boxShadow: const [
          BoxShadow(
            color: Color(0xA6000000),
            blurRadius: 40,
            offset: Offset(0, 16),
          ),
        ],
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            title,
            style: const TextStyle(
              fontFamily: Nocturne.fontHeading,
              fontSize: 17,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
          const SizedBox(height: 12),
          body,
          const SizedBox(height: 20),
          Row(
            mainAxisAlignment: MainAxisAlignment.end,
            children: [
              for (final action in actions) ...[
                if (action != actions.first) const SizedBox(width: 8),
                action,
              ],
            ],
          ),
        ],
      ),
    ),
  );
}

Future<T?> showNocturneDialog<T>({
  required BuildContext context,
  required WidgetBuilder builder,
}) => showDialog<T>(
  context: context,
  barrierColor: const Color(0xB3000000),
  builder: builder,
);

/// Deleting a camera, behind its own name.
///
/// A modal with a *Delete* button is one misplaced click from unregistering a camera; typing the
/// name is the cheapest thing that makes the action deliberate. It matters more here than usual
/// because the registry carries no undo. Being Admin-only is not the safeguard — an Admin is
/// exactly who is standing here.
class ConfirmDeleteDialog extends StatefulWidget {
  const ConfirmDeleteDialog({
    super.key,
    required this.cameraName,
    required this.retentionNote,
  });

  final String cameraName;

  /// What happens to the footage, in the Server's actual terms.
  final String retentionNote;

  @override
  State<ConfirmDeleteDialog> createState() => _ConfirmDeleteDialogState();
}

class _ConfirmDeleteDialogState extends State<ConfirmDeleteDialog> {
  final _typed = TextEditingController();

  @override
  void dispose() {
    _typed.dispose();
    super.dispose();
  }

  bool get _matches =>
      _typed.text.trim().toLowerCase() ==
      widget.cameraName.trim().toLowerCase();

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Remove ${widget.cameraName}?',
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'Serval stops pulling this camera and forgets its settings. '
          '${widget.retentionNote}',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            height: 1.5,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
        ),
        const SizedBox(height: 16),
        NocturneField(
          label: 'Type “${widget.cameraName}” to confirm',
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
        label: 'Remove camera',
        variant: NocturneButtonVariant.danger,
        // Disabled until the name matches, rather than absent — a button that is not there yet
        // reads as a bug, one that is there and inert reads as a condition unmet.
        onPressed: _matches ? () => Navigator.of(context).pop(true) : null,
      ),
    ],
  );
}
