/// The system's broken outline: a rounded rectangle drawn in 4px dashes on a 7px stride.
///
/// What a dash means depends on where it is drawn, and both readings are in use: on a chip it is a
/// label that is not really there — the built-in set, or the place the next one will go — and on
/// the *set by this deployment* pill it is the one state the page cannot take off. Neither needs a
/// second colour to say so.
library;

import 'dart:math' as math;

import 'package:flutter/widgets.dart';

class DashedBorder extends CustomPainter {
  const DashedBorder({required this.color, this.radius = 6});

  final Color color;

  /// Matched to the solid radius of whatever it outlines, so the two sit in a row together.
  final double radius;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1;

    canvas.drawPath(
      dashedRRectPath(Offset.zero & size, radius, dash: 4, gap: 3),
      paint,
    );
  }

  @override
  bool shouldRepaint(DashedBorder old) =>
      old.color != color || old.radius != radius;
}

/// The dashes of a rounded rectangle, as one path to stroke: [dash] long, [gap] apart.
///
/// Every dashed outline in the App is drawn from this, and none of them walks a [PathMetric] with
/// `extractPath` to get there. On the web each `extractPath` call leaves a native path behind in
/// CanvasKit's heap that nothing ever frees, and a painter makes one call per dash per paint — so
/// an outline that repaints with its surroundings, as an offline tile does on a wall of live ones,
/// fills that heap in a few hours and takes the page with it. Nothing here touches the metrics at
/// all: a rounded rectangle is four lines and four quarter circles, and those are laid out directly.
///
/// The dashes fall where walking `Path()..addRRect(...)` would put them: that path starts at the
/// bottom of the left edge and runs clockwise, and so does this. Round a corner the two part by a
/// fraction of a pixel, and this is the truer of them — a metric measures a curve by flattening
/// it, so its dashes run slightly long there.
Path dashedRRectPath(
  Rect rect,
  double radius, {
  required double dash,
  required double gap,
}) {
  final path = Path();
  if (rect.isEmpty || dash <= 0 || gap < 0) return path;

  // A radius larger than the rectangle can hold is scaled down to fit, as `RRect` itself does.
  final r = math.max(0.0, math.min(radius, rect.shortestSide / 2));
  final vertical = rect.height - 2 * r;
  final horizontal = rect.width - 2 * r;
  final corner = r * math.pi / 2;

  final legs = <_Leg>[
    _Line(rect.bottomLeft.translate(0, -r), const Offset(0, -1), vertical),
    _Arc(rect.topLeft.translate(r, r), r, math.pi, corner),
    _Line(rect.topLeft.translate(r, 0), const Offset(1, 0), horizontal),
    _Arc(rect.topRight.translate(-r, r), r, math.pi * 3 / 2, corner),
    _Line(rect.topRight.translate(0, r), const Offset(0, 1), vertical),
    _Arc(rect.bottomRight.translate(-r, -r), r, 0, corner),
    _Line(rect.bottomRight.translate(-r, 0), const Offset(-1, 0), horizontal),
    _Arc(rect.bottomLeft.translate(r, -r), r, math.pi / 2, corner),
  ];

  final perimeter = 2 * (vertical + horizontal) + 4 * corner;

  // The legs are walked once, alongside the dashes: `leg` and `legStart` only ever move forward,
  // because each dash begins after the one before it.
  var leg = 0;
  var legStart = 0.0;

  for (var start = 0.0; start < perimeter; start += dash + gap) {
    final end = math.min(start + dash, perimeter);

    while (leg < legs.length - 1 && legStart + legs[leg].length <= start) {
      legStart += legs[leg].length;
      leg++;
    }

    // A dash that crosses a corner is drawn as one contour over two legs, or over three where the
    // corner is shorter than the dash, so the stroke carries round the bend unbroken.
    var at = leg;
    var atStart = legStart;
    var begun = false;
    while (at < legs.length && atStart < end) {
      final from = math.max(start, atStart) - atStart;
      final to = math.min(end, atStart + legs[at].length) - atStart;
      if (to > from) {
        legs[at].addTo(path, from, to, moveTo: !begun);
        begun = true;
      }
      atStart += legs[at].length;
      at++;
    }
  }

  return path;
}

/// One straight side or one corner of the outline, [length] long.
sealed class _Leg {
  const _Leg(this.length);

  final double length;

  /// Adds the stretch between [from] and [to], both measured along this leg from its start.
  void addTo(Path path, double from, double to, {required bool moveTo});
}

final class _Line extends _Leg {
  const _Line(this.start, this.direction, super.length);

  final Offset start;
  final Offset direction;

  @override
  void addTo(Path path, double from, double to, {required bool moveTo}) {
    if (moveTo) {
      final begin = start + direction * from;
      path.moveTo(begin.dx, begin.dy);
    }
    final finish = start + direction * to;
    path.lineTo(finish.dx, finish.dy);
  }
}

final class _Arc extends _Leg {
  const _Arc(this.centre, this.radius, this.startAngle, super.length);

  final Offset centre;
  final double radius;
  final double startAngle;

  @override
  void addTo(Path path, double from, double to, {required bool moveTo}) {
    path.arcTo(
      Rect.fromCircle(center: centre, radius: radius),
      startAngle + from / radius,
      (to - from) / radius,
      moveTo,
    );
  }
}
