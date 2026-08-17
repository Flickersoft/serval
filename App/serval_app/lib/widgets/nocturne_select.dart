import 'package:flutter/material.dart'
    show PopupMenuButton, PopupMenuEntry, PopupMenuItem, PopupMenuPosition;
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';
import 'nocturne_editable_text.dart';

/// Nocturne's select: the same 36px box as a field, with a caret on the right.
///
/// [NocturneSelect] is for a closed set — a codec, a role. For *Where it is*, which is free text
/// that usually repeats an existing value, see [NocturneCombo].
class NocturneSelect<T> extends StatelessWidget {
  const NocturneSelect({
    super.key,
    required this.label,
    required this.value,
    required this.options,
    required this.optionLabel,
    this.placeholder,
    this.onChanged,
    this.optionNote,
  });

  final String label;
  final T? value;
  final List<T> options;
  final String Function(T) optionLabel;

  /// What shows when [value] is null — "Pick automatically" in the design's PTZ profile field.
  final String? placeholder;

  final ValueChanged<T?>? onChanged;

  /// Why an option cannot be picked, or null for one that can.
  ///
  /// A noted option stays in the list, dimmed and inert, because a shorter list explains nothing:
  /// somebody looking for a device this build cannot run needs to be told that is what happened,
  /// not left to wonder whether they misremembered the name. An option that is *also* the current
  /// value still shows in the closed box, dimmed, for the same reason — that is a deployment whose
  /// choice is being ignored, and this is the only place it surfaces.
  final String? Function(T)? optionNote;

  @override
  Widget build(BuildContext context) {
    final current = value;
    final currentNote = current == null ? null : optionNote?.call(current);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // An empty label is a select drawn under a heading of its own — the settings cards —
        // where the box is the whole control.
        if (label.isNotEmpty) ...[
          Text(
            label,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 12.5,
              color: Nocturne.mix(Nocturne.text, 62),
            ),
          ),
          const SizedBox(height: 6),
        ],
        PopupMenuButton<T?>(
          enabled: onChanged != null,
          onSelected: onChanged,
          color: Nocturne.surface,
          position: PopupMenuPosition.under,
          itemBuilder: (context) => <PopupMenuEntry<T?>>[
            if (placeholder != null)
              PopupMenuItem<T?>(
                value: null,
                child: _item(placeholder!, current == null),
              ),
            for (final option in options)
              PopupMenuItem<T?>(
                value: option,
                enabled: optionNote?.call(option) == null,
                child: _item(
                  optionLabel(option),
                  option == current,
                  optionNote?.call(option),
                ),
              ),
          ],
          child: Container(
            height: 36,
            padding: const EdgeInsets.symmetric(horizontal: 11),
            decoration: BoxDecoration(
              color: Nocturne.mix(Nocturne.text, 3),
              borderRadius: BorderRadius.circular(7),
              border: Border.all(color: Nocturne.mix(Nocturne.text, 14)),
            ),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    current == null
                        ? (placeholder ?? '')
                        : optionLabel(current),
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 13.5,
                      // A placeholder is muted; a real value is not. Otherwise "Pick
                      // automatically" reads as a choice someone made. A value that is set but
                      // cannot run is muted too — it is a claim this host is not honouring, and
                      // reading it as black text would say the opposite.
                      color: current == null || currentNote != null
                          ? Nocturne.mix(Nocturne.text, 55)
                          : Nocturne.text,
                    ),
                  ),
                ),
                PhosphorIcon(
                  PhosphorIconsRegular.caretDown,
                  size: 13,
                  color: Nocturne.mix(Nocturne.text, 45),
                ),
              ],
            ),
          ),
        ),
        if (currentNote != null) ...[
          const SizedBox(height: 6),
          Text(
            currentNote,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.35,
              color: Serval.alertText,
            ),
          ),
        ],
      ],
    );
  }

  Widget _item(String text, bool selected, [String? note]) {
    final label = Text(
      text,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 13,
        color: note != null
            ? Nocturne.mix(Nocturne.text, 40)
            : selected
            ? Nocturne.accent300
            : Nocturne.text,
      ),
    );

    if (note == null) {
      return label;
    }

    // The reason under the name rather than beside it: these run to a sentence, and a row that
    // grows sideways would push the names of the working options out of alignment.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        label,
        const SizedBox(height: 2),
        SizedBox(
          width: 260,
          child: Text(
            note,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11,
              height: 1.3,
              color: Nocturne.mix(Nocturne.text, 32),
            ),
          ),
        ),
      ],
    );
  }
}

/// A text field that also offers the values already in use.
///
/// *Where it is* is free text on the Server — `location` is an unconstrained string, and the
/// registry has no list of places. But it is also what the camera list groups by, so a typo
/// silently creates a second group with one camera in it. Offering the existing locations makes
/// the common case a click and leaves a new one typeable.
class NocturneCombo extends StatefulWidget {
  const NocturneCombo({
    super.key,
    required this.label,
    required this.controller,
    required this.suggestions,
    this.placeholder,
    this.onChanged,
  });

  final String label;
  final TextEditingController controller;

  /// The distinct values already in use elsewhere in the registry.
  final List<String> suggestions;

  final String? placeholder;
  final ValueChanged<String>? onChanged;

  @override
  State<NocturneCombo> createState() => _NocturneComboState();
}

class _NocturneComboState extends State<NocturneCombo> {
  final _focus = FocusNode();

  @override
  void initState() {
    super.initState();
    _focus.addListener(() => setState(() {}));
  }

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  void _pick(String value) {
    widget.controller.text = value;
    widget.onChanged?.call(value);
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        widget.label,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 12.5,
          color: Nocturne.mix(Nocturne.text, 62),
        ),
      ),
      const SizedBox(height: 6),
      Container(
        height: 36,
        padding: const EdgeInsets.only(left: 11, right: 4),
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.text, 3),
          borderRadius: BorderRadius.circular(7),
          border: Border.all(
            color: _focus.hasFocus
                ? Nocturne.mix(Nocturne.accent, 65)
                : Nocturne.mix(Nocturne.text, 14),
          ),
        ),
        child: Row(
          children: [
            Expanded(
              child: Stack(
                alignment: Alignment.centerLeft,
                children: [
                  if (widget.controller.text.isEmpty &&
                      widget.placeholder != null)
                    Text(
                      widget.placeholder!,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 13.5,
                        color: Nocturne.mix(Nocturne.text, 38),
                      ),
                    ),
                  NocturneEditableText(
                    controller: widget.controller,
                    focusNode: _focus,
                    style: const TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 13.5,
                      color: Nocturne.text,
                    ),
                    onChanged: (value) {
                      setState(
                        () {},
                      ); // the placeholder appears and disappears with the text
                      widget.onChanged?.call(value);
                    },
                  ),
                ],
              ),
            ),
            if (widget.suggestions.isNotEmpty)
              PopupMenuButton<String>(
                onSelected: _pick,
                color: Nocturne.surface,
                position: PopupMenuPosition.under,
                tooltip: 'Places already in use',
                itemBuilder: (context) => [
                  for (final suggestion in widget.suggestions)
                    PopupMenuItem<String>(
                      value: suggestion,
                      child: Text(
                        suggestion,
                        style: const TextStyle(
                          fontFamily: Nocturne.fontBody,
                          fontSize: 13,
                          color: Nocturne.text,
                        ),
                      ),
                    ),
                ],
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 7),
                  child: PhosphorIcon(
                    PhosphorIconsRegular.caretDown,
                    size: 13,
                    color: Nocturne.mix(Nocturne.text, 45),
                  ),
                ),
              ),
          ],
        ),
      ),
    ],
  );
}
