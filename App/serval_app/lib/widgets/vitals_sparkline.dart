import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/widgets.dart';

import '../models/vitals_history.dart';
import '../theme/nocturne.dart';

/// One vitals series over the retained window, drawn under its [VitalsMeter].
///
/// A sparkline in the strict sense: no axes, no grid, no labels, no touch response. The meter
/// above it already carries the number and the scale, so everything this adds is *shape* — whether
/// 12% is the trough of a duty cycle or a flat idle. That distinction is the entire reason the
/// route behind it exists; a page showing only the instantaneous bar could not tell a periodic
/// 800% spike from a one-off, which is what sent the investigation to `docker stats` over SSH.
///
/// **Nulls break the line.** [VitalsHistory] keeps unmeasured samples as null, and this converts
/// each into [FlSpot.nullSpot], which fl_chart renders as a genuine gap rather than interpolating
/// across it. Drawing through a hole would assert a reading nobody took, and a series coalesced to
/// zero would draw a confident line along the axis claiming the GPU was idle — the exact confusion
/// every `unavailableReason` on this page exists to prevent.
///
/// The y-axis is pinned to 0–100 rather than fitted to the data. A sparkline that rescales to its
/// own minimum and maximum makes idle noise look like a crisis, and makes two meters' charts
/// incomparable at a glance.
class VitalsSparkline extends StatelessWidget {
  const VitalsSparkline({super.key, required this.series, this.height = 28});

  /// The values, the instants they were taken, and the window they sit in.
  ///
  /// The x-axis comes from the window the Server retains, not the span the samples happen to
  /// cover. A buffer still filling after a restart then draws as a short line at the right-hand
  /// edge, which is what is true, rather than stretching edge to edge and looking complete.
  final VitalsSeries series;

  final double height;

  @override
  Widget build(BuildContext context) {
    final samples = series.sampledAt;
    final values = series.values;

    if (samples.isEmpty || !VitalsHistory.hasAnyReading(values)) {
      return SizedBox(height: height);
    }

    // Time is measured backwards from the newest sample, in minutes, so x runs -windowMinutes..0
    // regardless of how full the buffer is.
    final newest = samples.last;
    final spots = <FlSpot>[
      for (var i = 0; i < values.length && i < samples.length; i++)
        if (values[i] case final value?)
          FlSpot(
            -newest.difference(samples[i]).inMilliseconds / 60000.0,
            value.clamp(0, 100),
          )
        else
          // The gap. Its x is irrelevant to fl_chart — nullSpot is compared by identity — but the
          // slot must exist so the runs either side are not joined.
          FlSpot.nullSpot,
    ];

    return SizedBox(
      height: height,
      child: LineChart(
        LineChartData(
          minX: -series.windowMinutes,
          maxX: 0,
          minY: 0,
          maxY: 100,
          gridData: const FlGridData(show: false),
          titlesData: const FlTitlesData(show: false),
          borderData: FlBorderData(show: false),
          // The meter above owns interaction; a tooltip here would be a second affordance for a
          // number already printed six pixels up.
          lineTouchData: const LineTouchData(enabled: false),
          lineBarsData: [
            LineChartBarData(
              spots: spots,
              isCurved: false,
              barWidth: 1.4,
              // The same accent as the meter fill, so bar and line read as one object. Status hues
              // stay reserved for the alert row — see VitalsMeter, which makes the same argument
              // about a meter that turns red on its own.
              color: Nocturne.accent500,
              dotData: const FlDotData(show: false),
              belowBarData: BarAreaData(
                show: true,
                color: Nocturne.mix(Nocturne.accent, 16),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
