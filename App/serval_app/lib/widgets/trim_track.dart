import 'package:flutter/gestures.dart';
import 'package:flutter/widgets.dart';

import '../data/time_labels.dart';
import '../models/clip_selection.dart';
import '../models/timeline.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import '../theme/serval_tokens.dart';

/// The timeline you were scrubbing, turned into a trim track.
///
/// The zoom is what makes this work at all. On a twelve-hour track a pixel is about thirty-five
/// seconds, so a fifty-five second clip is two pixels wide and nobody can trim it; entering clip
/// mode re-scales to twelve minutes around where you were, which puts ticks on real minutes and
/// gives a handle somewhere to go. Everything else follows from that decision — the handles are
/// wide enough to grab, and the ±one-segment nudges cover the last second the mouse cannot.
///
/// Prop-driven like every widget here: it takes a [ClipSelection] and reports the one the gesture
/// implies. All the arithmetic — snapping, capping, refusing to cross — lives in that model, so
/// this file is only geometry and paint.
class TrimTrack extends StatelessWidget {
  const TrimTrack({
    super.key,
    required this.selection,
    required this.window,
    required this.marks,
    required this.onChanged,
    this.compact = false,
    this.max = const Duration(minutes: 30),
  });

  /// The range being trimmed.
  final ClipSelection selection;

  /// The slice of time the track covers — twelve minutes, or an hour.
  final CoverageSpan window;

  /// What happened in that slice, so the range can be set against the events rather than the clock.
  final List<TimelineMark> marks;

  final ValueChanged<ClipSelection> onChanged;

  /// A finger cannot land on a 14px handle, so under a thumb they widen to 22.
  final bool compact;

  final Duration max;

  double get _handleWidth => compact ? 22 : 14;

  static const _height = 80.0;
  static const _compactHeight = 78.0;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final width = constraints.maxWidth;
      final geometry = _TrimGeometry(window, width);

      return SizedBox(
        height: compact ? _compactHeight : _height,
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          dragStartBehavior: DragStartBehavior.down,

          // One detector over the whole track rather than one per handle, which is both simpler
          // and kinder: the end you grab is the nearer one, so a drag that starts a few pixels off
          // a 14px bar still moves the handle you were aiming at instead of nothing.
          onHorizontalDragStart: (details) => onChanged(
            selection.withActive(_nearest(geometry, details.localPosition.dx)),
          ),
          onHorizontalDragUpdate: (details) => onChanged(
            selection.moveEnd(
              selection.active,
              geometry.timeAt(details.localPosition.dx),
              max: max,
            ),
          ),
          child: DecoratedBox(
            decoration: BoxDecoration(
              color: Nocturne.mix(Nocturne.text, 5),
              borderRadius: BorderRadius.circular(8),
              border: Border.all(color: Nocturne.mix(Nocturne.text, 9)),
            ),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(8),
              child: Stack(
                children: [
                  for (final mark in marks) ..._markAt(mark, geometry),

                  // Outside the range, darkened rather than hidden. What is being cut still has to
                  // be readable — the whole reason the track is here is to decide where the cut
                  // goes, and that is decided by looking at what is either side of it.
                  _shade(0, geometry.xOf(selection.from)),
                  _shade(geometry.xOf(selection.to), width),

                  _band(geometry),
                  _label(geometry),

                  _handle(geometry, ClipEnd.start, width),
                  _handle(geometry, ClipEnd.end, width),

                  _ticks(geometry),
                ],
              ),
            ),
          ),
        ),
      );
    },
  );

  /// One event on the track, in the hue of what it was — the same six the scrubber uses.
  ///
  /// Drawn one by one rather than merged into layers: this track is a handful of marks across a
  /// clip rather than a day of them, so there is no burst to collapse and no pixel two of them
  /// have to share.
  List<Widget> _markAt(TimelineMark mark, _TrimGeometry geometry) {
    final left = geometry.xOf(mark.at);
    if (left < 0 || left > geometry.width) return const [];

    final width = mark.ran > Duration.zero
        ? (geometry.widthOf(mark.ran)).clamp(4.0, geometry.width)
        : 5.0;

    return [
      Positioned(
        left: left,
        top: 12,
        height: compact ? 36 : 34,
        width: width,
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: Nocturne.mix(
              Serval.markHue(
                mark.of,
                alert: mark.kind == TimelineMarkKind.alert,
              ),
              mark.kind == TimelineMarkKind.alert ? 72 : 50,
            ),
            borderRadius: BorderRadius.circular(2),
          ),
        ),
      ),
    ];
  }

  Widget _shade(double left, double right) => Positioned(
    left: left,
    width: (right - left).clamp(0.0, double.infinity),
    top: 0,
    bottom: 0,
    child: ColoredBox(color: Serval.overlay.withValues(alpha: 0.62)),
  );

  Widget _band(_TrimGeometry geometry) {
    final left = geometry.xOf(selection.from);
    final right = geometry.xOf(selection.to);

    return Positioned(
      left: left,
      width: (right - left).clamp(0.0, double.infinity),
      top: 0,
      bottom: 0,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.accent, 12),
          border: Border(
            top: BorderSide(color: Nocturne.accent, width: 2),
            bottom: BorderSide(color: Nocturne.accent, width: 2),
          ),
        ),
      ),
    );
  }

  /// How long the clip is, in the middle of what it covers.
  Widget _label(_TrimGeometry geometry) {
    final centre =
        (geometry.xOf(selection.from) + geometry.xOf(selection.to)) / 2;

    return Positioned(
      left: centre - 40,
      width: 80,
      top: compact ? 27 : 26,
      child: Center(
        child: Container(
          height: compact ? 23 : 24,
          padding: const EdgeInsets.symmetric(horizontal: 10),
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: Serval.overlay.withValues(alpha: 0.8),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: Nocturne.mix(Nocturne.accent, 45)),
          ),
          child: Text(
            clipSpokenLabel(selection.span),
            style: monoStyle(fontSize: 11, color: Nocturne.text),
          ),
        ),
      ),
    );
  }

  /// Which end a drag starting at [x] means.
  ///
  /// By distance rather than by hit-testing the bars, so the miss that a 14px target invites still
  /// does the obvious thing instead of nothing.
  ClipEnd _nearest(_TrimGeometry geometry, double x) =>
      (x - geometry.xOf(selection.from)).abs() <=
          (x - geometry.xOf(selection.to)).abs()
      ? ClipEnd.start
      : ClipEnd.end;

  /// A range end, drawn. The gesture belongs to the track above; this is the mark you aim at.
  Widget _handle(_TrimGeometry geometry, ClipEnd end, double width) {
    final at = end == ClipEnd.start ? selection.from : selection.to;
    final live = selection.active == end;

    return Positioned(
      left: (geometry.xOf(at) - _handleWidth / 2).clamp(
        0.0,
        width - _handleWidth,
      ),
      top: 0,
      bottom: 0,
      width: _handleWidth,
      child: IgnorePointer(
        child: Center(
          child: Container(
            width: _handleWidth,
            decoration: BoxDecoration(
              color: live
                  ? Nocturne.accent300
                  : Nocturne.mix(Nocturne.accent300, 85),
              borderRadius: BorderRadius.circular(compact ? 5 : 4),
              boxShadow: live
                  ? [
                      BoxShadow(
                        color: Nocturne.mix(Nocturne.accent, 30),
                        blurRadius: 0,
                        spreadRadius: compact ? 4 : 3,
                      ),
                    ]
                  : null,
            ),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              spacing: compact ? 3 : 2,
              children: [
                for (var i = 0; i < 2; i++)
                  Container(
                    width: compact ? 2 : 1.5,
                    height: compact ? 18 : 16,
                    decoration: BoxDecoration(
                      color: Serval.overlay.withValues(alpha: 0.6),
                      borderRadius: BorderRadius.circular(1),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  /// Minute labels along the bottom, at whole minutes rather than at even divisions of the width.
  Widget _ticks(_TrimGeometry geometry) => Positioned(
    left: 0,
    right: 0,
    bottom: 5,
    child: Padding(
      padding: EdgeInsets.symmetric(horizontal: compact ? 9 : 10),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          for (final at in geometry.tickTimes(compact ? 5 : 7))
            Text(
              clockLabel(at),
              style: monoStyle(
                fontSize: compact ? 9 : 9.5,
                color: Nocturne.mix(Nocturne.text, 35),
              ),
            ),
        ],
      ),
    ),
  );
}

/// Time to pixels across a fixed window. No merging and no coverage — the trim track is drawn over
/// one recording session, so every instant in it has footage by construction.
class _TrimGeometry {
  const _TrimGeometry(this.window, this.width);

  final CoverageSpan window;
  final double width;

  Duration get span => window.duration;

  double xOf(DateTime at) {
    if (span <= Duration.zero) return 0;

    final position =
        at.difference(window.from).inMicroseconds / span.inMicroseconds;
    return (position * width).clamp(0.0, width);
  }

  double widthOf(Duration duration) {
    if (span <= Duration.zero) return 0;
    return duration.inMicroseconds / span.inMicroseconds * width;
  }

  DateTime timeAt(double x) {
    if (width <= 0) return window.from;

    final position = (x / width).clamp(0.0, 1.0);
    return window.from.add(
      Duration(microseconds: (span.inMicroseconds * position).round()),
    );
  }

  /// [count] evenly spaced instants across the window, including both edges.
  List<DateTime> tickTimes(int count) => [
    for (var i = 0; i < count; i++)
      window.from.add(
        Duration(microseconds: (span.inMicroseconds * i / (count - 1)).round()),
      ),
  ];
}
