import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';
import 'nocturne_editable_text.dart';

/// Shared furniture for the two roster screens (cameras, users): the accent
/// "new entry" row, the sidebar's explanatory note, the search field, and the
/// empty state. One rendering, so the two screens cannot drift apart.

/// The accent-tinted row a roster shows while a new entry is being drafted.
class DraftRow extends StatelessWidget {
  const DraftRow({
    super.key,
    required this.icon,
    required this.label,
    this.margin = EdgeInsets.zero,
  });

  final PhosphorIconData icon;
  final String label;
  final EdgeInsets margin;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.all(10),
    margin: margin,
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.accent, 16),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: Nocturne.mix(Nocturne.accent, 40)),
    ),
    child: Row(
      children: [
        PhosphorIcon(icon, size: 14, color: Nocturne.accent300),
        const SizedBox(width: 10),
        Text(
          label,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13.5,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.accent300,
          ),
        ),
      ],
    ),
  );
}

/// A quiet explanatory paragraph under a roster's list.
class EmptyNote extends StatelessWidget {
  const EmptyNote(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(8, 14, 8, 8),
    child: Text(
      text,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: 12.5,
        height: 1.5,
        color: Nocturne.mix(Nocturne.text, 45),
      ),
    ),
  );
}

/// The roster's search field.
class SearchBox extends StatefulWidget {
  const SearchBox({
    super.key,
    required this.controller,
    required this.onChanged,
    required this.placeholder,
    this.height = 32,
  });

  final TextEditingController controller;
  final VoidCallback onChanged;
  final String placeholder;

  /// A thumb's target when the field is the phone's, the column's own 32 otherwise.
  final double height;

  @override
  State<SearchBox> createState() => _SearchBoxState();
}

class _SearchBoxState extends State<SearchBox> {
  /// Owned rather than built in `build`: a node made fresh each frame is a different node every
  /// time the query changes, so the caret would be taken away by the keystroke that moved it.
  final _focus = FocusNode();

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Container(
    height: widget.height,
    padding: const EdgeInsets.symmetric(horizontal: 11),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(7),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 12)),
    ),
    child: Row(
      children: [
        PhosphorIcon(
          PhosphorIconsRegular.magnifyingGlass,
          size: 14,
          color: Nocturne.mix(Nocturne.text, 42),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Stack(
            alignment: Alignment.centerLeft,
            children: [
              if (widget.controller.text.isEmpty)
                Text(
                  widget.placeholder,
                  style: TextStyle(
                    fontFamily: Nocturne.fontBody,
                    fontSize: 12.5,
                    color: Nocturne.mix(Nocturne.text, 42),
                  ),
                ),
              NocturneEditableText(
                controller: widget.controller,
                focusNode: _focus,
                style: const TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12.5,
                  color: Nocturne.text,
                ),
                onChanged: (_) => widget.onChanged(),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

/// What a roster shows when it has no entries at all.
class EmptyRoster extends StatelessWidget {
  const EmptyRoster({
    super.key,
    required this.icon,
    required this.title,
    required this.body,
    this.action,
  });

  final PhosphorIconData icon;
  final String title;
  final String body;

  /// The one thing to do about being empty, or null where the roster is read from a screen that
  /// cannot add to it. Optional because most rosters carry their add button in the list column
  /// beside them, and only reach this widget to explain the blank space.
  final Widget? action;

  @override
  Widget build(BuildContext context) => Center(
    child: ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 420),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          PhosphorIcon(icon, size: 28, color: Nocturne.mix(Nocturne.text, 25)),
          const SizedBox(height: 14),
          Text(
            title,
            style: const TextStyle(
              fontFamily: Nocturne.fontHeading,
              fontSize: 17,
              fontWeight: Nocturne.headingWeight,
              color: Nocturne.text,
            ),
          ),
          const SizedBox(height: 6),
          Text(
            body,
            textAlign: TextAlign.center,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 13,
              height: 1.55,
              color: Nocturne.mix(Nocturne.text, 50),
            ),
          ),
          if (action != null) ...[const SizedBox(height: 18), action!],
        ],
      ),
    ),
  );
}
