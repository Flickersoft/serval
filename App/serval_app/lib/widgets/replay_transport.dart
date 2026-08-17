import 'package:flutter/material.dart' show Tooltip;
import 'package:flutter/widgets.dart';
import 'package:phosphor_icons/phosphor_icons.dart';

import '../theme/nocturne.dart';
import 'nocturne_button.dart';
import 'segmented_control.dart';

/// Play, pause and speed, for whichever screen is replaying.
///
/// Shared by the wall and the single camera deliberately. Dragging the scrubber is how you *search*
/// footage, and it works the same on both; this is how you *watch* it, and a wall that could run on
/// its own at 4x while a single camera could not would be two products in one app.
class ReplayTransport extends StatelessWidget {
  const ReplayTransport({
    super.key,
    required this.playing,
    required this.rate,
    required this.rates,
    this.onPlayPause,
    this.onRateChanged,
    this.compact = false,
  });

  final bool playing;
  final int rate;
  final List<int> rates;
  final VoidCallback? onPlayPause;
  final ValueChanged<int>? onRateChanged;

  /// Phone metrics for the speed segments.
  ///
  /// 24px beside a scrubber is a control the eye reads and the mouse hits; it is under half the
  /// 44px the rest of the compact design aims at, and four of them in a row are four chances to
  /// hit the wrong one. The row this sits in has the width to spare on a phone — see the second
  /// row [TimelineScrubber] gives it there.
  final bool compact;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Tooltip(
        message: playing ? 'Pause' : 'Play',
        child: NocturneButton.icon(
          icon: playing ? PhosphorIconsFill.pause : PhosphorIconsFill.play,
          variant: NocturneButtonVariant.ghost,
          onPressed: onPlayPause,
        ),
      ),
      const SizedBox(width: 8),
      SegmentedControl(
        segments: [for (final rate in rates) Segment('$rate×')],
        selectedIndex: rates.indexOf(rate),
        height: compact ? 34 : 24,
        fontSize: compact ? 13 : 12,
        horizontalPadding: compact ? 11 : 8,
        borderRadius: Nocturne.radiusSm + 1,
        onChanged: onRateChanged == null
            ? null
            : (index) => onRateChanged!(rates[index]),
      ),
    ],
  );
}
