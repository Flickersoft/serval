import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';

/// The 38x22 switch the settings screen uses everywhere a camera has a capability on or off.
///
/// Deliberately not Material's `Switch`: that one is a filled track in the theme's primary
/// colour, and this system's whole posture is that saturation is presence — a line, a dot, a
/// tint — never a flood. On is an accent tint behind an accent-light knob; off is the neutral
/// ramp at the step everything else unpressed sits on.
class NocturneToggle extends StatefulWidget {
  const NocturneToggle({
    super.key,
    required this.value,
    this.onChanged,
    this.compact = false,
  });

  final bool value;

  /// Null disables it, at the system's 45% for a disabled control.
  final ValueChanged<bool>? onChanged;

  /// The smaller 32x19 variant the design uses inline beside *Re-encode*, where the switch sits
  /// in a row of 12px labels rather than standing on its own.
  final bool compact;

  @override
  State<NocturneToggle> createState() => _NocturneToggleState();
}

class _NocturneToggleState extends State<NocturneToggle> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onChanged != null;
    final width = widget.compact ? 32.0 : 38.0;
    final height = widget.compact ? 19.0 : 22.0;
    final knob = widget.compact ? 13.0 : 16.0;

    return Opacity(
      opacity: enabled ? 1 : 0.45,
      child: MouseRegion(
        cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTap: enabled ? () => widget.onChanged!(!widget.value) : null,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 120),
            width: width,
            height: height,
            padding: const EdgeInsets.symmetric(horizontal: 3),
            alignment: widget.value
                ? Alignment.centerRight
                : Alignment.centerLeft,
            decoration: BoxDecoration(
              color: widget.value
                  ? Nocturne.mix(Nocturne.accent, 45)
                  : Nocturne.mix(Nocturne.text, _hovered && enabled ? 14 : 10),
              borderRadius: BorderRadius.circular(height / 2),
              border: Border.all(
                color: widget.value
                    ? Nocturne.mix(Nocturne.accent, 70)
                    : Nocturne.mix(Nocturne.text, 16),
              ),
            ),
            child: Container(
              width: knob,
              height: knob,
              decoration: BoxDecoration(
                color: widget.value
                    ? Nocturne.accent300
                    : Nocturne.mix(Nocturne.text, 45),
                shape: BoxShape.circle,
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// A toggle with its own bordered row, a title and the sentence explaining the consequence.
///
/// The design writes those sentences as consequences rather than definitions — "Turn it off to
/// stop recording and hide it from the wall. Old footage is kept." — which is the part someone
/// needs before flipping a switch on their own house.
class ToggleRow extends StatelessWidget {
  const ToggleRow({
    super.key,
    required this.title,
    required this.description,
    required this.value,
    this.onChanged,
  });

  final String title;
  final String description;
  final bool value;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 11),
    decoration: BoxDecoration(
      color: Nocturne.mix(Nocturne.text, 3),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
    ),
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                title,
                style: const TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13.5,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                description,
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 12,
                  height: 1.4,
                  color: Nocturne.mix(Nocturne.text, 50),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(width: 12),
        NocturneToggle(value: value, onChanged: onChanged),
      ],
    ),
  );
}

/// One of the design's three *What Serval notices* cards: an icon, a name, a toggle, and one
/// line saying what turning it on actually produces.
///
/// The card tints when it is on, which is what makes the three readable as a group at a glance —
/// you can see which of a camera's senses are awake without reading any of the labels.
class CapabilityCard extends StatelessWidget {
  const CapabilityCard({
    super.key,
    required this.icon,
    required this.title,
    required this.description,
    required this.value,
    this.onChanged,
  });

  final PhosphorIconData icon;
  final String title;
  final String description;
  final bool value;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.all(11),
    decoration: BoxDecoration(
      color: value
          ? Nocturne.mix(Nocturne.accent, 9)
          : Nocturne.mix(Nocturne.text, 3),
      borderRadius: BorderRadius.circular(8),
      border: Border.all(
        color: value
            ? Nocturne.mix(Nocturne.accent, 35)
            : Nocturne.mix(Nocturne.text, 10),
      ),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // The glyph and the switch share the top line; the title gets the card's full width
        // below them. The design draws all three on one line, which does not survive contact
        // with real text — three of these share a row, so each is about 165px wide, and once
        // the icon and a 38px switch have taken their share "Describe what it sees" has ~74px
        // left. Every arrangement that keeps them on one line truncates it to nothing.
        //
        // Putting the switches on their own line also aligns them across the three cards, which
        // is what makes "which of this camera's senses are awake" a glance rather than a read.
        Row(
          children: [
            PhosphorIcon(
              icon,
              size: 15,
              color: value
                  ? Nocturne.accent400
                  : Nocturne.mix(Nocturne.text, 60),
            ),
            const Spacer(),
            NocturneToggle(value: value, onChanged: onChanged),
          ],
        ),
        const SizedBox(height: 8),
        Text(
          title,
          style: const TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 13.5,
            fontWeight: Nocturne.headingWeight,
            color: Nocturne.text,
            height: 1.3,
          ),
        ),
        const SizedBox(height: 5),
        Text(
          description,
          style: TextStyle(
            fontFamily: Nocturne.fontBody,
            fontSize: 12,
            height: 1.45,
            color: Nocturne.mix(Nocturne.text, 60),
          ),
        ),
      ],
    ),
  );
}
