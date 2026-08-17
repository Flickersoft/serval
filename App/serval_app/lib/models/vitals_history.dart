/// Retained processor, memory, GPU and accelerator samples — `GET /api/system/stats/history`.
///
/// The Server keeps roughly an hour of the same fast samples [SystemStats] serves one of, so the
/// *Server status* meters can carry a sparkline underneath.
///
/// **A null in a series is not a zero**, and here that matters more than anywhere else in the app.
/// A chart has to put its line *somewhere*, and a line resting on the axis is a claim that the GPU
/// was idle — when the truth may be that this host publishes no utilisation at all. So the series
/// are `List<double?>` rather than `List<double>`, nothing is coalesced to `0`, and
/// [VitalsSparkline] breaks the line at every null instead of drawing through it.
///
/// The wire shape is parallel arrays rather than a list of objects: an hour at the default
/// five-second cadence is 720 points, and repeating four key names per point would roughly triple
/// the payload to say nothing new. Index `i` of every series belongs to [sampledAt] index `i`.
///
/// Hand-written `fromJson` and `const` constructors, matching
/// [system_stats.dart](system_stats.dart) — there is no codegen in this app.
library;

import '../data/json_coerce.dart';

class VitalsHistory {
  const VitalsHistory({
    this.sampledAt = const [],
    this.cpu = const [],
    this.memory = const [],
    this.gpu = const [],
    this.accelerator = const [],
    this.windowMinutes = 0,
    this.unavailableReason,
  });

  factory VitalsHistory.fromJson(Map<String, dynamic> json) {
    final times = _times(json['sampledAt']);

    // Every series is clamped to the timestamps' length. The Server builds them in one pass each
    // and cannot produce a mismatch, but a series longer or shorter than its own time axis would
    // attribute readings to the wrong instants rather than fail visibly — so this decodes to
    // "aligned or absent" rather than trusting the lengths.
    return VitalsHistory(
      sampledAt: times,
      cpu: _series(json['cpu'], times.length),
      memory: _series(json['memory'], times.length),
      gpu: _series(json['gpu'], times.length),
      accelerator: _series(json['accelerator'], times.length),
      windowMinutes: asDouble(json['windowMinutes']) ?? 0,
      unavailableReason: json['unavailableReason'] as String?,
    );
  }

  /// When each sample was taken, oldest first.
  ///
  /// Sent by the Server rather than inferred from the cadence: a missed tick would otherwise shift
  /// every earlier point silently, which is the same class of untruth as a null becoming a zero.
  final List<DateTime> sampledAt;

  /// Container processor share, 0–100 per core-normalised sample. Null where unmeasured.
  final List<double?> cpu;

  /// Memory against the container's limit, 0–100. Null where the host sets no limit to measure against.
  final List<double?> memory;

  /// Whole-GPU utilisation, 0–100. Null on every host whose driver publishes none — which is
  /// everything but amdgpu.
  final List<double?> gpu;

  /// Pooled accelerator utilisation, 0–100. Null throughout on every host with no accelerator, which
  /// is most of them — and the meter it belongs to is not drawn there at all.
  final List<double?> accelerator;

  /// How long a window the Server is willing to keep, from `Vitals:HistoryMinutes`.
  ///
  /// The sparkline scales its time axis to this rather than to the samples it happens to hold, so
  /// a buffer still filling after a restart draws as a partial line across a full-width hour
  /// instead of stretching four minutes edge to edge and looking like a complete picture.
  final double windowMinutes;

  /// Why there is no history, when there is none — retention or vitals switched off. Null when the
  /// series are the answer, **including when they are empty**: a server that started a moment ago
  /// has nothing to show yet and will have shortly, which is not a fault.
  final String? unavailableReason;

  /// True when there is nothing to draw yet but nothing is wrong either.
  bool get isEmpty => sampledAt.isEmpty && unavailableReason == null;

  /// Whether any sample in [series] carries a figure. A series of all nulls is a host that cannot
  /// measure that thing, and the meter should say so rather than render an empty chart.
  static bool hasAnyReading(List<double?> series) =>
      series.any((v) => v != null);

  /// One series with the time axis it belongs to, or null when there is nothing worth drawing.
  ///
  /// Returning null for an all-null series is the point: a host with no amdgpu reports GPU as
  /// unavailable on every sample, and the meter should carry the Server's sentence saying so
  /// rather than an empty chart frame implying the reading is merely offscreen.
  VitalsSeries? seriesOf(List<double?> values) =>
      unavailableReason != null || sampledAt.isEmpty || !hasAnyReading(values)
      ? null
      : VitalsSeries(
          sampledAt: sampledAt,
          values: values,
          windowMinutes: windowMinutes,
        );
}

/// One plottable series: the values, the instants they were taken, and the window they sit in.
///
/// Bundled so [VitalsMeter] takes one nullable object rather than three parameters that would each
/// have to be checked for agreement at every call site.
class VitalsSeries {
  const VitalsSeries({
    required this.sampledAt,
    required this.values,
    required this.windowMinutes,
  });

  final List<DateTime> sampledAt;
  final List<double?> values;
  final double windowMinutes;
}

List<DateTime> _times(Object? value) => switch (value) {
  final List<dynamic> list => [
    for (final entry in list)
      ?switch (entry) {
        final String text => DateTime.tryParse(text)?.toLocal(),
        _ => null,
      },
  ],
  _ => const [],
};

/// One series, padded or truncated to [length] so it stays aligned with the time axis.
List<double?> _series(Object? value, int length) {
  if (value is! List<dynamic>) return List<double?>.filled(length, null);

  return [
    for (var i = 0; i < length; i++)
      i < value.length ? asDouble(value[i]) : null,
  ];
}
