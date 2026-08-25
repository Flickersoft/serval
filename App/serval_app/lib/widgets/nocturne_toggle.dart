import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../models/server_settings.dart';
import '../theme/nocturne.dart';
import 'settings_cards.dart';

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
///
/// **One of the three follows the Server, and says so.** A capability the camera holds itself is a
/// plain bool with two states; one that overrides a Server-wide switch has a third, *unset*, and a
/// two-state control cannot show the difference between a camera that chose *on* and one that is
/// only following. So [source] and [onReset] are optional: given them, the card grows the same chip
/// and reset link a `SettingCard` has, and [value] is the *effective* value — what the camera is
/// actually running on. Without them it is the flat switch it was, which is what the other two want.
class CapabilityCard extends StatelessWidget {
  const CapabilityCard({
    super.key,
    required this.icon,
    required this.title,
    required this.description,
    required this.value,
    this.onChanged,
    this.source,
    this.resetLabel,
    this.onReset,
  });

  final PhosphorIconData icon;
  final String title;
  final String description;

  /// What the camera is running on — its own choice, or the Server's behind it when [source] is
  /// [SettingSource.builtIn].
  final bool value;
  final ValueChanged<bool>? onChanged;

  /// Whether this camera set the value itself. Null draws no chip, for a capability with no Server
  /// switch behind it to inherit from.
  final SettingSource? source;

  /// *Use the default*, naming what it restores. Drawn only alongside [onReset].
  final String? resetLabel;
  final VoidCallback? onReset;

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

        // Below the description rather than up beside the switch, which is where the Server page
        // puts it. Three of these share a row at about 165px each, and the top line has already
        // spent 53 of that on the glyph and the switch — *using the default* does not fit in what
        // is left, and moving the switch down to make room would break the alignment across the
        // three that makes them readable as a group.
        if (source case final source?) ...[
          const SizedBox(height: 9),
          Wrap(
            spacing: 8,
            runSpacing: 4,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              SettingSourceChip(source: source),
              if (resetLabel != null && onReset != null)
                SettingsLinkText(resetLabel!, onTap: onReset!),
            ],
          ),
        ],
      ],
    ),
  );
}
