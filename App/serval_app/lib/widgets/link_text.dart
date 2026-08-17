import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';

/// An inline text action — accent-colored, no chrome. Null [onTap] renders it
/// as quiet text rather than a link, for the states where the action is not
/// currently available.
class LinkText extends StatelessWidget {
  const LinkText(
    this.label, {
    super.key,
    required this.onTap,
    this.fontSize = 12,
  });

  final String label;
  final void Function()? onTap;
  final double fontSize;

  @override
  Widget build(BuildContext context) {
    final text = Text(
      label,
      style: TextStyle(
        fontFamily: Nocturne.fontBody,
        fontSize: fontSize,
        color: onTap == null
            ? Nocturne.mix(Nocturne.text, 35)
            : Nocturne.accent400,
      ),
    );

    if (onTap == null) return text;

    return Semantics(
      button: true,
      label: label,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(onTap: onTap, child: text),
      ),
    );
  }
}
