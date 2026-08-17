/// A list of words, edited as chips added one at a time.
///
/// The Server settings page and the per-camera page both hold lists of this kind — detection class
/// names on one, AudioSet labels on the other — and they are the same control because they are the
/// same problem: a label is spelled exactly as the model spells it. "Gunshot, gunfire" contains a
/// comma and "Civil defense siren" is not the same label as "Siren", so a label is taken whole.
/// Typing one and pressing enter adds that label, and nothing is ever split on punctuation.
library;

import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import 'dashed_border.dart';
import 'nocturne_editable_text.dart';
import 'nocturne_button.dart';
import 'nocturne_dialog.dart';
import 'nocturne_field.dart';

class LabelChipList extends StatefulWidget {
  const LabelChipList({
    super.key,
    required this.value,
    required this.onChanged,
    this.fallback = const [],
    this.showFallback = true,
  });

  final List<String> value;

  /// What is used while this list does not apply, drawn in the row the labels themselves would
  /// occupy — a setting quietly doing something has to say what, where the answer is looked for.
  final List<String> fallback;

  /// Whether the fallback may be drawn at all.
  ///
  /// The Server page has one empty state and leaves this on: an empty list there means the built-in
  /// set applies. A camera has two — it overrides nothing, or it overrides with nothing at all, and
  /// "record nothing" is a real instruction that must not be drawn as if the Server's list applied.
  /// That page passes false the moment an override exists, however few labels are left in it.
  final bool showFallback;

  final ValueChanged<List<String>> onChanged;

  @override
  State<LabelChipList> createState() => _LabelChipListState();
}

class _LabelChipListState extends State<LabelChipList> {
  final _controller = TextEditingController();
  final _focus = FocusNode();

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  void _add(String text) {
    final label = text.trim();
    _controller.clear();
    if (label.isEmpty || widget.value.contains(label)) return;
    widget.onChanged([...widget.value, label]);
  }

  void _remove(String label) =>
      widget.onChanged([...widget.value.where((item) => item != label)]);

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 6,
    runSpacing: 6,
    children: [
      for (final label in widget.value)
        _Chip(label: label, onRemove: () => _remove(label)),
      if (widget.showFallback && widget.value.isEmpty)
        for (final label in widget.fallback) _GhostChip(label: label),
      _AddChip(controller: _controller, focus: _focus, onSubmit: _add),
    ],
  );
}

/// Adds several labels at once, for the case someone has the model's own output to hand.
///
/// One label per line, never split on punctuation: "Gunshot, gunfire" is one AudioSet label and
/// splitting it on the comma would ask the detector for two things that do not exist.
///
/// Returns the labels of [current] with the pasted ones appended, or null when nothing was added —
/// so a caller can hand the result straight to its own `onChanged`.
Future<List<String>?> showPasteListDialog({
  required BuildContext context,
  required String label,
  required List<String> current,
}) async {
  final pasted = await showNocturneDialog<List<String>>(
    context: context,
    builder: (context) => _PasteListDialog(label: label),
  );
  if (pasted == null || pasted.isEmpty) return null;

  return [
    ...current,
    for (final item in pasted)
      if (!current.contains(item)) item,
  ];
}

class _Chip extends StatelessWidget {
  const _Chip({required this.label, required this.onRemove});

  final String label;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) => Container(
    height: 25,
    padding: const EdgeInsets.symmetric(horizontal: 8),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 5),
      borderRadius: BorderRadius.circular(6),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 14)),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(label, style: monoStyle(fontSize: 11.5, color: Nocturne.text)),
        const SizedBox(width: 6),
        MouseRegion(
          cursor: SystemMouseCursors.click,
          child: GestureDetector(
            onTap: onRemove,
            child: PhosphorIcon(
              PhosphorIconsRegular.x,
              size: 11,
              color: Nocturne.mix(Nocturne.text, 40),
            ),
          ),
        ),
      ],
    ),
  );
}

/// One entry of the list that applies instead, standing where a chosen one would.
///
/// Dashed and unremovable, the same way [_AddChip] is dashed: in this control a dashed outline is a
/// label that is not really there. Drawn only while nothing has been chosen, so what is actually
/// being looked for is read off the row rather than out of a sentence below it — and the moment
/// someone types a label of their own, these go, because the set they came from stops applying
/// whole.
class _GhostChip extends StatelessWidget {
  const _GhostChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => CustomPaint(
    painter: DashedBorder(color: Nocturne.mix(Nocturne.text, 18)),
    // Sized to its label exactly the way _Chip is, down to carrying no alignment: a Container
    // given one expands to its parent, and inside a Wrap that makes each chip a full-width bar.
    child: Container(
      height: 25,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            label,
            style: monoStyle(
              fontSize: 11.5,
              color: Nocturne.mix(Nocturne.text, 38),
            ),
          ),
        ],
      ),
    ),
  );
}

/// The chip you type into. Dashed, because it is the place a label is not yet.
class _AddChip extends StatelessWidget {
  const _AddChip({
    required this.controller,
    required this.focus,
    required this.onSubmit,
  });

  final TextEditingController controller;
  final FocusNode focus;
  final ValueChanged<String> onSubmit;

  @override
  Widget build(BuildContext context) => CustomPaint(
    painter: DashedBorder(color: Nocturne.mix(Nocturne.accent, 55)),
    child: SizedBox(
      height: 25,
      // Wide enough for the whole invitation on one line: the chip is 25px tall and a hint that
      // wraps would be cut in half by it.
      width: 218,
      child: MouseRegion(
        cursor: SystemMouseCursors.text,
        child: GestureDetector(
          onTap: focus.requestFocus,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 9),
            child: Row(
              children: [
                PhosphorIcon(
                  PhosphorIconsRegular.plus,
                  size: 11,
                  color: Nocturne.accent400,
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: Stack(
                    alignment: Alignment.centerLeft,
                    children: [
                      if (controller.text.isEmpty)
                        Text(
                          'type a label, press enter',
                          maxLines: 1,
                          softWrap: false,
                          overflow: TextOverflow.clip,
                          style: monoStyle(
                            fontSize: 11.5,
                            color: Nocturne.mix(Nocturne.text, 42),
                          ),
                        ),
                      NocturneEditableText(
                        controller: controller,
                        focusNode: focus,
                        style: monoStyle(fontSize: 11.5, color: Nocturne.text),
                        onSubmitted: onSubmit,
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}

/// Several labels at once, one per line — the case a text box was really serving.
class _PasteListDialog extends StatefulWidget {
  const _PasteListDialog({required this.label});

  final String label;

  @override
  State<_PasteListDialog> createState() => _PasteListDialogState();
}

class _PasteListDialogState extends State<_PasteListDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  List<String> get _lines => [
    for (final line in _controller.text.split('\n'))
      if (line.trim().isNotEmpty) line.trim(),
  ];

  @override
  Widget build(BuildContext context) => NocturneDialog(
    title: 'Add to ${widget.label}',
    body: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'One label per line, exactly as the model spells it. Nothing is split on punctuation, '
          'so a label that contains a comma stays one label.',
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13,
            height: 1.5,
            color: Nocturne.mix(Nocturne.text, 62),
          ),
        ),
        const SizedBox(height: 16),
        NocturneField(
          label: 'Labels',
          controller: _controller,
          mono: true,
          lines: 6,
          onChanged: (_) => setState(() {}),
        ),
      ],
    ),
    actions: [
      NocturneButton(
        label: 'Cancel',
        onPressed: () => Navigator.of(context).pop(),
      ),
      NocturneButton(
        label: _lines.length == 1
            ? 'Add 1 label'
            : 'Add ${_lines.length} labels',
        variant: NocturneButtonVariant.primary,
        onPressed: _lines.isEmpty
            ? null
            : () => Navigator.of(context).pop(_lines),
      ),
    ],
  );
}
