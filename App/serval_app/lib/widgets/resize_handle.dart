import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';

/// The corner grab handle — two strokes, matching the tile's own radius.
///
/// Lives outside `CameraTile` because the wall places it: the tile knows
/// nothing about cell sizes, and an offline tile has no header or stack to
/// hang it from, yet still resizes.
class ResizeHandle extends StatelessWidget {
  const ResizeHandle({super.key, this.active = false});

  /// Brighter while the corner is actually being dragged.
  final bool active;

  @override
  Widget build(BuildContext context) => Container(
    width: 15,
    height: 15,
    decoration: BoxDecoration(
      border: Border(
        right: BorderSide(
          color: Nocturne.mix(Nocturne.text, active ? 70 : 45),
          width: 2,
        ),
        bottom: BorderSide(
          color: Nocturne.mix(Nocturne.text, active ? 70 : 45),
          width: 2,
        ),
      ),
      borderRadius: const BorderRadius.only(
        bottomRight: Radius.circular(Nocturne.radiusSm),
      ),
    ),
  );
}
