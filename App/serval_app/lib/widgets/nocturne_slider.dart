import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// The retention track: a 4px rail, an accent fill and a 15px knob ringed in the ground.
///
/// Not Material's `Slider`, for the usual reason — its thumb carries an overlay and a ripple in
/// the theme's primary colour, and this system's controls are lines and tints.
///
/// The fill and the knob are placed by fraction, and a drag reads the width off the slider's own
/// render box, so nothing here lays out from its constraints. That is what lets a slider sit in a
/// row of cards drawn to a common height: measuring those children is impossible through a widget
/// that only knows its size once it has been given one.
class NocturneSlider extends StatelessWidget {
  const NocturneSlider({
    super.key,
    required this.value,
    required this.min,
    required this.max,
    this.onChanged,
  });

  final double value;
  final double min;
  final double max;
  final ValueChanged<double>? onChanged;

  static const _knob = 15.0;

  @override
  Widget build(BuildContext context) {
    final fraction = ((value - min) / (max - min)).clamp(0.0, 1.0);

    void report(BuildContext box, Offset local) {
      final width = (box.findRenderObject() as RenderBox?)?.size.width ?? 0;
      if (onChanged == null || width <= 0) return;
      final picked = (local.dx / width).clamp(0.0, 1.0);
      onChanged!(min + picked * (max - min));
    }

    return MouseRegion(
      cursor: onChanged == null
          ? SystemMouseCursors.basic
          : SystemMouseCursors.click,
      child: Builder(
        builder: (context) => GestureDetector(
          onTapDown: (details) => report(context, details.localPosition),
          onHorizontalDragUpdate: (details) =>
              report(context, details.localPosition),
          // Behavior.opaque so the whole 22px band is grabbable, not just the 4px rail — the
          // rail is a drawing, the target is the row.
          behavior: HitTestBehavior.opaque,
          child: SizedBox(
            height: 22,
            child: Stack(
              alignment: Alignment.centerLeft,
              clipBehavior: Clip.none,
              children: [
                Container(
                  height: 4,
                  decoration: BoxDecoration(
                    color: Nocturne.mix(Nocturne.text, 10),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
                FractionallySizedBox(
                  widthFactor: fraction,
                  child: Container(
                    height: 4,
                    decoration: BoxDecoration(
                      color: Nocturne.accent,
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                ),
                Align(
                  // -1 puts the knob's left edge on the rail's and 1 its right edge on the far
                  // end, so it rides the fill without hanging off either end.
                  alignment: Alignment(fraction * 2 - 1, 0),
                  child: Container(
                    width: _knob,
                    height: _knob,
                    decoration: BoxDecoration(
                      color: Nocturne.accent300,
                      shape: BoxShape.circle,
                      // The ring is the ground, not a stroke — the knob reads as punched out of
                      // the rail rather than sitting on top of it.
                      border: Border.all(color: Serval.panel, width: 2),
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
