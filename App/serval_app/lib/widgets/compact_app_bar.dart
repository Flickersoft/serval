import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// The 56px bar at the top of every drill-down screen: a way back, what you are looking at, and
/// the one or two things you can do to it.
///
/// Design 7b puts the same bar on the settings index, the camera list and the camera editor, so it
/// is written once. The targets are 44px square — the switch inside a toggle row is 24px and would
/// be a poor target on its own, and the same figure decides these.
///
/// The title is the only thing that shrinks. It ellipsizes; the actions keep their room, because a
/// clipped glyph is not a smaller control, it is a broken one.
class CompactAppBar extends StatelessWidget {
  const CompactAppBar({
    super.key,
    required this.title,
    this.subtitle,
    this.onBack,
    this.actions = const [],
    this.trailing,
    this.backIcon = PhosphorIconsRegular.arrowLeft,
    this.backTooltip = 'Back',
  });

  final String title;

  /// A second line under the title, inside the same 56px.
  ///
  /// For a standing fact about what the title names — "5 of 6 cameras live" —
  /// rather than for a sentence. Anything longer belongs in a [CompactSubBar],
  /// which is a band of its own and can hold one.
  final String? subtitle;

  /// Null draws no arrow and leaves the title against the left edge — a screen nothing came before.
  final VoidCallback? onBack;

  /// What the back control looks like.
  ///
  /// An arrow by default, because the usual case is going back up a stack. A mode that is entered
  /// and abandoned rather than navigated into takes an X instead — leaving a trimmer discards a
  /// range rather than returning anywhere, and an arrow would promise the wrong thing.
  final PhosphorIconData backIcon;

  /// What the back control is called, for a screen reader and a long press.
  final String backTooltip;

  /// Drawn at the far end in order. Use [CompactBarAction] so they share the target size.
  final List<Widget> actions;

  /// Rides beside the title rather than at the end of the bar: a badge that qualifies what you are
  /// looking at, not something to press. It takes its room before the title's, and the title
  /// ellipsizes around it.
  final Widget? trailing;

  /// Named because the wall measures against it: a sheet that floats over the bar has to know
  /// where the tiles under it begin.
  static const height = 56.0;

  @override
  Widget build(BuildContext context) => Container(
    height: height,
    padding: const EdgeInsets.symmetric(horizontal: 6),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Row(
      children: [
        if (onBack != null)
          CompactBarAction(
            icon: backIcon,
            tooltip: backTooltip,
            onPressed: onBack,
          )
        else
          const SizedBox(width: 10),
        Expanded(
          child: Padding(
            padding: EdgeInsets.only(left: onBack == null ? 0 : 4),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Flexible(
                      child: Text(
                        title,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          fontFamily: Nocturne.fontHeading,
                          fontSize: 19,
                          fontWeight: Nocturne.headingWeight,
                          color: Nocturne.text,
                          letterSpacing: -0.01 * 19,
                        ),
                      ),
                    ),
                    if (trailing != null) ...[
                      const SizedBox(width: 8),
                      trailing!,
                    ],
                  ],
                ),
                if (subtitle case final subtitle?)
                  Text(
                    subtitle,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontFamily: Nocturne.fontBody,
                      fontSize: 12,
                      color: Nocturne.mix(Nocturne.text, 50),
                    ),
                  ),
              ],
            ),
          ),
        ),
        ...actions,
      ],
    ),
  );
}

/// One glyph in a [CompactAppBar], at the bar's own target size.
class CompactBarAction extends StatefulWidget {
  const CompactBarAction({
    super.key,
    required this.icon,
    required this.tooltip,
    this.onPressed,
    this.selected = false,
    this.badge = false,
  });

  final PhosphorIconData icon;

  /// Also the semantic label: at this size the glyph is the only thing on screen naming the action.
  final String tooltip;

  final VoidCallback? onPressed;

  /// Tints the target, for a control that is holding something open — the list's search field.
  final bool selected;

  /// A small accent dot over the glyph — something is waiting behind this. The same mark the rail
  /// carries, and for the same reason: at 44px a count is unreadable, and the question is only
  /// whether there is anything.
  final bool badge;

  @override
  State<CompactBarAction> createState() => _CompactBarActionState();
}

class _CompactBarActionState extends State<CompactBarAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onPressed != null;

    return Semantics(
      button: true,
      enabled: enabled,
      label: widget.tooltip,
      child: MouseRegion(
        cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTap: widget.onPressed,
          behavior: HitTestBehavior.opaque,
          child: Container(
            width: 44,
            height: 44,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(8),
              color: widget.selected
                  ? Nocturne.mix(Nocturne.accent, 16)
                  : _hovered && enabled
                  ? Nocturne.mix(Nocturne.text, 5)
                  : null,
            ),
            child: Stack(
              clipBehavior: Clip.none,
              children: [
                PhosphorIcon(
                  widget.icon,
                  size: 20,
                  color: widget.selected
                      ? Nocturne.accent300
                      : Nocturne.mix(Nocturne.text, enabled ? 72 : 30),
                ),
                if (widget.badge)
                  Positioned(
                    top: -1,
                    right: -3,
                    child: Container(
                      width: 6,
                      height: 6,
                      decoration: const BoxDecoration(
                        color: Serval.alert,
                        shape: BoxShape.circle,
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The band under a [CompactAppBar] that carries what the desktop header's subtitle line says.
///
/// The bar holds one line and the title takes it, so everything the wide header put beneath the
/// title moves here: a sentence, or a row of pills and then a sentence.
class CompactSubBar extends StatelessWidget {
  const CompactSubBar({super.key, required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    padding: const EdgeInsets.fromLTRB(18, 12, 18, 13),
    decoration: BoxDecoration(
      color: Serval.rail,
      border: Border(bottom: BorderSide(color: Serval.hairline)),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: children,
    ),
  );
}
