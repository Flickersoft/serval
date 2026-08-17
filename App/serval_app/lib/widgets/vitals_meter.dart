import 'dart:math' as math;

import 'package:flutter/widgets.dart';

import '../data/byte_labels.dart';
import '../models/vitals_history.dart';
import '../theme/app_theme.dart';
import '../theme/nocturne.dart';
import 'vitals_sparkline.dart';

/// One ratio against a limit — processor, memory, GPU — with its recent history under it.
///
/// A bar rather than a dial, and the three stacked in a column rather than in a row: stacking is
/// what lets them be read against each other, and a memory line climbing while the processor stays
/// flat is invisible when the two charts sit side by side at a third of the width each.
///
/// The sparkline plots measurements, never a trend inferred from a single sample — the Server
/// retains about an hour of samples and serves them from `GET /api/system/stats/history`. Nothing
/// here compares against a period the Server did not record; see `Docs/app-notes.md`.
///
/// [history] is optional, and absent means no sparkline rather than an empty one.
///
/// **A null [percent] is not a zero.** The card turns to a dashed border, the track fills with
/// hatching, and the figure reads *not reported*. A host whose GPU driver publishes nothing and a
/// genuinely idle GPU are indistinguishable once a null becomes 0, and only one of them is
/// something the Server measured — the hatching is what carries that at a glance, since an empty
/// track and a track at 0% look the same from across the room.
///
/// Status hues ([Serval.alert], [Serval.recording]) are not used here at all: they are reserved for
/// the alert row, and a meter turning red on its own would spend them on a number that is usually
/// fine.
class VitalsMeter extends StatelessWidget {
  const VitalsMeter({
    super.key,
    required this.label,
    required this.percent,
    this.figure,
    this.caption,
    this.unavailableReason,
    this.history,
  });

  /// What is being measured — *Processor*, *Memory*.
  final String label;

  /// 0–100, or null when this host cannot say.
  final double? percent;

  /// A second figure beside the label, where the percentage is not the whole answer — memory says
  /// "2.6 GB of 8.6 GB" next to its 30%. It sits beside the label rather than replacing the
  /// percentage, because the two answer different questions and the bar is drawn from the latter.
  final String? figure;

  /// One line under the bar. The place the page explains itself: what the number covers, or why an
  /// idle reading is the correct one.
  final String? caption;

  /// Why there is no figure. Drawn *with* [caption] rather than instead of it — a GPU whose driver
  /// publishes no utilisation still reports its video memory, and that figure survives the missing
  /// one.
  final String? unavailableReason;

  /// This meter's series over the retained window, or null where there is none to draw — no Server
  /// behind the repository, retention switched off, or a host that cannot measure this figure at
  /// all. Null draws no sparkline rather than an empty one, so the card keeps its former height
  /// wherever there is nothing to say.
  final VitalsSeries? history;

  bool get _available => unavailableReason == null && percent != null;

  @override
  Widget build(BuildContext context) {
    final note = [?unavailableReason, ?caption].join(' ');

    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.baseline,
          textBaseline: TextBaseline.alphabetic,
          children: [
            // The label and its second figure share whatever the percentage does not need, rather
            // than competing with a spacer for it — a `Spacer` here takes the row's slack first
            // and ellipsises "2.6 GB of 8.6 GB" down to nothing on a column this narrow.
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.baseline,
                textBaseline: TextBaseline.alphabetic,
                children: [
                  Flexible(
                    child: Text(
                      label,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: Nocturne.fontBody,
                        fontSize: 12.5,
                        color: Nocturne.mix(
                          Nocturne.text,
                          _available ? 72 : 62,
                        ),
                      ),
                    ),
                  ),
                  if (figure != null) ...[
                    const SizedBox(width: 8),
                    Flexible(
                      child: Text(
                        figure!,
                        overflow: TextOverflow.ellipsis,
                        style: monoStyle(
                          fontSize: 11.5,
                          color: Nocturne.mix(Nocturne.text, 50),
                        ),
                      ),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(width: 10),
            if (_available)
              Text(
                // Numbers wear a text token, never the fill's own colour. A percentage printed in
                // the bar's hue is the most common way a page like this stops being readable.
                formatPercent(percent),
                style: const TextStyle(
                  fontFamily: Nocturne.fontHeading,
                  fontSize: 19,
                  fontWeight: Nocturne.headingWeight,
                  color: Nocturne.text,
                ),
              )
            else
              Text(
                // Words, not the em dash the rest of the app uses for a missing figure. This is
                // the one place the distinction *is* the content, and "not reported" says it in
                // the terms the caption underneath goes on to explain.
                'not reported',
                style: TextStyle(
                  fontFamily: Nocturne.fontBody,
                  fontSize: 13,
                  color: Nocturne.mix(Nocturne.text, 45),
                ),
              ),
          ],
        ),
        const SizedBox(height: 8),
        MeterTrack(
          percent: _available ? percent : null,
          unavailable: !_available,
        ),
        if (history case final series?) ...[
          const SizedBox(height: 8),
          VitalsSparkline(series: series),
          const SizedBox(height: 6),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(_windowLabel(series.windowMinutes), style: _axisStyle),
              Text('now', style: _axisStyle),
            ],
          ),
        ],
        if (note.isNotEmpty) ...[
          const SizedBox(height: 8),
          Text(
            note,
            style: TextStyle(
              fontFamily: Nocturne.fontBody,
              fontSize: 11.5,
              height: 1.45,
              color: Nocturne.mix(Nocturne.text, 45),
            ),
          ),
        ],
      ],
    );

    final padded = Padding(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 13),
      child: body,
    );

    // The dashed card and the solid one are the same geometry, so a meter that stops reporting
    // does not move the two below it.
    if (_available) {
      return DecoratedBox(
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.text, 3),
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: Nocturne.mix(Nocturne.text, 10)),
        ),
        child: padded,
      );
    }

    return CustomPaint(
      painter: _DashedCard(color: Nocturne.mix(Nocturne.text, 14)),
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: Nocturne.mix(Nocturne.text, 2),
          borderRadius: BorderRadius.circular(8),
        ),
        child: padded,
      ),
    );
  }

  /// The x-axis' far end, from the window the Server actually retained rather than a fixed word —
  /// `Serval:Vitals:HistoryMinutes` is a setting, and an hour is only its default.
  static String _windowLabel(double minutes) {
    if (minutes >= 120) return 'last ${(minutes / 60).round()} hours';
    if (minutes >= 60) return 'last hour';
    return 'last ${minutes.round()} minutes';
  }

  static final _axisStyle = TextStyle(
    fontFamily: Nocturne.fontBody,
    fontSize: 11,
    color: Nocturne.mix(Nocturne.text, 32),
  );
}

/// The bar itself, without the label — so the per-camera storage list can use the same geometry
/// for its rows without inheriting a heading it does not want.
class MeterTrack extends StatelessWidget {
  const MeterTrack({
    super.key,
    required this.percent,
    this.height = 6,
    this.unavailable = false,
  });

  final double? percent;
  final double height;

  /// Fills the track with hatching instead of leaving it empty. Reserved for a figure the host
  /// does not publish — an empty track reads as zero, and hatching cannot.
  final bool unavailable;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(height / 2),
    child: SizedBox(
      height: height,
      child: unavailable
          ? CustomPaint(
              painter: _Hatching(color: Nocturne.mix(Nocturne.text, 9)),
              size: Size.infinite,
            )
          : ColoredBox(
              color: Nocturne.mix(Nocturne.accent, 12),
              child: percent == null
                  ? null
                  : FractionallySizedBox(
                      alignment: Alignment.centerLeft,
                      // Clamped rather than trusted: the Server clamps too, but a bar drawn past
                      // its own track is a rendering exception rather than a wrong number.
                      widthFactor: (percent!.clamp(0, 100)) / 100,
                      child: ColoredBox(color: Nocturne.accent500),
                    ),
            ),
    ),
  );
}

/// Diagonal stripes, for a track with no measurement behind it.
class _Hatching extends CustomPainter {
  const _Hatching({required this.color});

  final Color color;

  static const _spacing = 10.0;
  static const _slant = math.pi * 115 / 180;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..strokeWidth = 5
      ..style = PaintingStyle.stroke;

    // Leaned rather than vertical so the stripes cannot be mistaken for tick marks, and swept from
    // behind the left edge so the first one is a full stripe rather than a clipped sliver.
    final dx = size.height / math.tan(_slant);
    for (
      var x = -size.height.abs() - _spacing;
      x < size.width + _spacing;
      x += _spacing
    ) {
      canvas.drawLine(Offset(x, size.height), Offset(x + dx, 0), paint);
    }
  }

  @override
  bool shouldRepaint(_Hatching old) => old.color != color;
}

/// A dashed rounded rectangle around a card whose figure the host does not publish.
class _DashedCard extends CustomPainter {
  const _DashedCard({required this.color});

  final Color color;

  /// The solid card's radius, so the two are interchangeable without moving anything.
  static const _radius = Radius.circular(8);

  @override
  void paint(Canvas canvas, Size size) {
    final path = Path()
      ..addRRect(RRect.fromRectAndRadius(Offset.zero & size, _radius));
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
  bool shouldRepaint(_DashedCard old) => old.color != color;
}
