import 'package:flutter/widgets.dart';

import '../theme/app_theme.dart';

/// The uppercase mono label that opens a section — `RIGHT NOW`,
/// `EARLIER TODAY`, `LIVE TRANSCRIPT`.
///
/// Hierarchy in this system is size and space, so the kicker is set small and
/// wide-tracked rather than bold.
class SectionKicker extends StatelessWidget {
  const SectionKicker(
    this.label, {
    super.key,
    this.padding = EdgeInsets.zero,
    this.color,
  });

  final String label;
  final EdgeInsets padding;
  final Color? color;

  @override
  Widget build(BuildContext context) => Padding(
    padding: padding,
    child: Text(label.toUpperCase(), style: kickerStyle(color: color)),
  );
}
