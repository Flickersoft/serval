import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// How a button carries weight. Nocturne outlines its primary action rather
/// than filling it — a flooded accent is the one thing the system forbids.
enum NocturneButtonVariant {
  /// A 1px accent outline on transparent, with an accent tint behind it.
  primary,

  /// A hairline in the divider color; the default action weight.
  secondary,

  /// No border at all — for controls that sit inside another frame.
  ghost,

  /// An outline in the recording hue, for the one action that removes something.
  ///
  /// Same construction as [primary] — an outline and a tint, never a fill — because a filled
  /// red button is both a flood the system forbids and an invitation to click the thing you
  /// least want clicked by accident.
  danger,
}

/// A button in the design system's idiom.
///
/// States come from the accent ramp exactly as the stylesheet defines them:
/// a hover tint, a pressed state one step further, and a 2px accent focus
/// ring with a 2px offset. Nothing here uses Material's fill or ripple.
class NocturneButton extends StatefulWidget {
  const NocturneButton({
    super.key,
    required this.label,
    this.icon,
    this.trailingIcon,
    this.onPressed,
    this.variant = NocturneButtonVariant.secondary,
    this.height = 34,
    this.fontSize = 13,
    this.horizontalPadding = 12,
    this.borderRadius = 7,
  });

  /// An icon-only button — the label is dropped and the box goes square.
  const NocturneButton.icon({
    super.key,
    required PhosphorIconData this.icon,
    this.onPressed,
    this.variant = NocturneButtonVariant.secondary,
    this.height = 34,
    this.borderRadius = 7,
  }) : label = '',
       trailingIcon = null,
       fontSize = 13,
       horizontalPadding = 12;

  final String label;
  final PhosphorIconData? icon;
  final PhosphorIconData? trailingIcon;
  final VoidCallback? onPressed;
  final NocturneButtonVariant variant;
  final double height;
  final double fontSize;
  final double horizontalPadding;
  final double borderRadius;

  bool get _isIconOnly => label.isEmpty;

  @override
  State<NocturneButton> createState() => _NocturneButtonState();
}

class _NocturneButtonState extends State<NocturneButton> {
  bool _hovered = false;
  bool _pressed = false;
  bool _focused = false;

  Color get _foreground => switch (widget.variant) {
    NocturneButtonVariant.primary => Nocturne.accent300,
    NocturneButtonVariant.danger => Serval.recordingText,
    _ => Nocturne.mix(Nocturne.text, 72),
  };

  Color get _border => switch (widget.variant) {
    NocturneButtonVariant.primary => Nocturne.mix(Nocturne.accent, 55),
    NocturneButtonVariant.danger => Nocturne.mix(Serval.recording, 45),
    NocturneButtonVariant.secondary => Nocturne.mix(Nocturne.text, 14),
    NocturneButtonVariant.ghost => const Color(0x00000000),
  };

  /// The tint behind the label. Rest for primary is the accent at 14%, and
  /// hover/press step through the ramp from there.
  Color get _fill {
    final base = switch (widget.variant) {
      NocturneButtonVariant.primary => Nocturne.accent,
      NocturneButtonVariant.danger => Serval.recording,
      _ => Nocturne.text,
    };
    final restPercent = widget.variant == NocturneButtonVariant.primary
        ? 14.0
        : 0.0;
    final percent = _pressed
        ? restPercent + 10
        : _hovered
        ? restPercent + 5
        : restPercent;
    return percent == 0 ? const Color(0x00000000) : Nocturne.mix(base, percent);
  }

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onPressed != null;

    Widget content = Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (widget.icon != null)
          PhosphorIcon(widget.icon!, size: 15, color: _foreground),
        if (!widget._isIconOnly) ...[
          if (widget.icon != null) const SizedBox(width: 7),
          // Flexible, because some labels are not written here: the alert card's *Watch it on
          // Front door* interpolates a camera name the operator chose, inside a column of fixed
          // width. Without this a long name is a layout error rather than an ellipsis — and a
          // button that overflows is one whose verb has been cut off.
          Flexible(
            child: Text(
              widget.label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: Nocturne.fontBody,
                fontSize: widget.fontSize,
                fontWeight: widget.variant == NocturneButtonVariant.primary
                    ? Nocturne.headingWeight
                    : FontWeight.w400,
                color: _foreground,
                height: 1.2,
              ),
            ),
          ),
        ],
        if (widget.trailingIcon != null) ...[
          const SizedBox(width: 6),
          PhosphorIcon(widget.trailingIcon!, size: 13, color: _foreground),
        ],
      ],
    );

    content = Container(
      height: widget.height,
      width: widget._isIconOnly ? widget.height : null,
      padding: widget._isIconOnly
          ? EdgeInsets.zero
          : EdgeInsets.symmetric(horizontal: widget.horizontalPadding),
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: _fill,
        borderRadius: BorderRadius.circular(widget.borderRadius),
        border: Border.all(color: _border),
      ),
      child: content,
    );

    // The system's keyboard focus: a 2px accent outline held 2px clear of the
    // control, never the platform's default ring.
    if (_focused) {
      content = Container(
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(widget.borderRadius + 2),
          border: Border.all(color: Nocturne.accent, width: 2),
        ),
        padding: const EdgeInsets.all(2),
        child: content,
      );
    }

    return Opacity(
      opacity: enabled ? 1 : 0.45,
      child: Focus(
        onFocusChange: (v) => setState(() => _focused = v),
        child: MouseRegion(
          cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
          onEnter: (_) => setState(() => _hovered = true),
          onExit: (_) => setState(() => _hovered = false),
          child: GestureDetector(
            onTapDown: enabled ? (_) => setState(() => _pressed = true) : null,
            onTapUp: enabled ? (_) => setState(() => _pressed = false) : null,
            onTapCancel: enabled
                ? () => setState(() => _pressed = false)
                : null,
            onTap: widget.onPressed,
            child: content,
          ),
        ),
      ),
    );
  }
}
