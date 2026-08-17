/// The system's broken outline: a rounded rectangle drawn in 4px dashes on a 7px stride.
///
/// What a dash means depends on where it is drawn, and both readings are in use: on a chip it is a
/// label that is not really there — the built-in set, or the place the next one will go — and on
/// the *set by this deployment* pill it is the one state the page cannot take off. Neither needs a
/// second colour to say so.
library;

import 'package:flutter/widgets.dart';

class DashedBorder extends CustomPainter {
  const DashedBorder({required this.color, this.radius = 6});

  final Color color;

  /// Matched to the solid radius of whatever it outlines, so the two sit in a row together.
  final double radius;

  @override
  void paint(Canvas canvas, Size size) {
    final path = Path()
      ..addRRect(
        RRect.fromRectAndRadius(Offset.zero & size, Radius.circular(radius)),
      );
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1;

    for (final metric in path.computeMetrics()) {
      var start = 0.0;
      while (start < metric.length) {
        final end = (start + 4).clamp(0.0, metric.length);
        canvas.drawPath(metric.extractPath(start, end), paint);
        start += 7;
      }
    }
  }

  @override
  bool shouldRepaint(DashedBorder old) =>
      old.color != color || old.radius != radius;
}
