/// What the Server says about itself — `GET /api/system/stats`.
///
/// **Every figure is nullable, and that is the whole point.** The Server distinguishes "this host
/// cannot measure it" from "it measured zero", and carries an [unavailableReason] to say which.
/// A GPU whose driver publishes nothing must render as *not reported*, never as a full-looking
/// meter sitting at 0% — those look identical once a null becomes a zero, and only one of them is
/// true. So nothing here defaults a missing number to 0, and the widgets branch on null rather
/// than on a magic value.
///
/// Hand-written `fromJson` and `const` constructors throughout, matching
/// [camera_record.dart](../data/camera_record.dart): there is no codegen in this app, and
/// [SampleServalRepository](../data/sample_repository.dart) needs a compile-time-constant instance
/// to sit beside its other `const` sample content.
///
/// Casing is camelCase, like `/api/cameras` and unlike the telemetry documents.
library;

import '../data/json_coerce.dart';

class SystemStats {
  const SystemStats({
    this.sampledAt,
    this.processUptimeSeconds,
    this.cpu = const CpuStats(),
    this.memory = const MemoryStats(),
    this.gpu = const GpuStats(),
    this.accelerator = const AcceleratorStats(),
    this.disk = const DiskStats(),
    this.detection = const DetectionStats(),
    this.alerts = const [],
  });

  factory SystemStats.fromJson(Map<String, dynamic> json) => SystemStats(
    sampledAt: _time(json['sampledAt']),
    processUptimeSeconds: asDouble(json['processUptimeSeconds']),
    cpu: CpuStats.fromJson(_object(json['cpu'])),
    memory: MemoryStats.fromJson(_object(json['memory'])),
    gpu: GpuStats.fromJson(_object(json['gpu'])),
    accelerator: AcceleratorStats.fromJson(_object(json['accelerator'])),
    disk: DiskStats.fromJson(_object(json['disk'])),
    detection: DetectionStats.fromJson(_object(json['detection'])),
    alerts: [
      // An unrecognised kind is dropped rather than failing the whole stats decode — one alert
      // the App cannot name should not cost it the page.
      for (final entry in (json['alerts'] as List<dynamic>? ?? const []))
        ?VitalsAlert.fromJson(_object(entry)),
    ],
  );

  final DateTime? sampledAt;

  /// How long the Server process has been running — deliberately the process, not the host, since
  /// `/proc/uptime` inside a container is the host's and answers nobody's question here.
  final double? processUptimeSeconds;

  final CpuStats cpu;
  final MemoryStats memory;
  final GpuStats gpu;
  final AcceleratorStats accelerator;
  final DiskStats disk;
  final DetectionStats detection;

  /// What the Server decided is worth saying, already worded. Empty on a healthy system.
  final List<VitalsAlert> alerts;
}

/// Whether object detection is keeping up, and what it is skipping when it is not.
///
/// Unlike the meters beside it, this one is about accuracy rather than resources. Every part of the
/// detection path degrades by dropping work — a backlog of frames, a crop the budget could not
/// afford — which is correct behaviour for a recorder that must not stall, and completely invisible
/// otherwise. A server quietly examining half of what it wanted to looks exactly like one keeping
/// up, and the difference is detections that never happened.
class DetectionStats {
  const DetectionStats({
    this.budgetPerSecond,
    this.cameras,
    this.backend,
    this.lanes,
    this.healthyLanes,
    this.detectorDegraded,
    this.examinedPerSecond,
    this.shedPerSecond,
    this.droppedFramesPerSecond,
    this.coverage,
    this.unavailableReason,
  });

  factory DetectionStats.fromJson(Map<String, dynamic> json) => DetectionStats(
    budgetPerSecond: asDouble(json['budgetPerSecond']),
    cameras: asInt(json['cameras']),
    backend: json['backend'] as String?,
    lanes: asInt(json['lanes']),
    healthyLanes: asInt(json['healthyLanes']),
    detectorDegraded: json['detectorDegraded'] as String?,
    examinedPerSecond: asDouble(json['examinedPerSecond']),
    shedPerSecond: asDouble(json['shedPerSecond']),
    droppedFramesPerSecond: asDouble(json['droppedFramesPerSecond']),
    coverage: asDouble(json['coverage']),
    unavailableReason: json['unavailableReason'] as String?,
  );

  /// Inferences a second this host was measured at and is willing to spend. Null when the detector
  /// could not be timed, in which case nothing is being throttled.
  final double? budgetPerSecond;

  /// Cameras sharing that budget.
  final int? cameras;

  /// Which detector is running — `onnx/cpu model`, `edgetpu/...`. Worth showing because more than one
  /// backend is buildable, so "which one is this host actually using" is a real question.
  final String? backend;

  /// Lanes the detector holds, and how many can run now.
  ///
  /// Equal on a CPU backend, always. Unequal means a piece of hardware was unplugged or stopped
  /// answering and real capacity fell with it — which coverage alone will not show.
  final int? lanes;

  /// See [lanes].
  final int? healthyLanes;

  /// Why the detector says it is degraded, or null when it is not.
  final String? detectorDegraded;

  /// True when the detector is running on fewer lanes than it holds.
  bool get lanesDegraded {
    final held = lanes;
    final healthy = healthyLanes;
    // Both nullable, and absence must not read as zero: an older server, or a backend with nothing to
    // say about lanes, is not a degraded one.
    return held != null && healthy != null && healthy < held;
  }

  /// Regions actually examined, per second.
  final double? examinedPerSecond;

  /// Regions the planner wanted and the budget could not afford — a place in a frame nothing looked
  /// at.
  final double? shedPerSecond;

  /// Whole frames that never reached detection — a moment nothing looked at, which costs more than
  /// a shed region because it loses time rather than area.
  final double? droppedFramesPerSecond;

  /// Of everything detection wanted to examine, the fraction it did. 1.0 means nothing is skipped.
  ///
  /// Null when nothing was asked for, which is an idle scene rather than a failing one — a still
  /// frame proposes no crops, and rendering that as 0% would be exactly backwards.
  final double? coverage;

  final String? unavailableReason;
}

/// Processor load, from two sources answering different questions.
class CpuStats {
  const CpuStats({
    this.containerPercent,
    this.hostPercent,
    this.cores,
    this.quotaCores,
    this.loadAverage,
    this.unavailableReason,
  });

  factory CpuStats.fromJson(Map<String, dynamic> json) => CpuStats(
    containerPercent: asDouble(json['containerPercent']),
    hostPercent: asDouble(json['hostPercent']),
    cores: asDouble(json['cores']),
    quotaCores: asDouble(json['quotaCores']),
    loadAverage: switch (json['loadAverage']) {
      final List<dynamic> values => [
        for (final value in values) asDouble(value) ?? 0,
      ],
      _ => null,
    },
    unavailableReason: json['unavailableReason'] as String?,
  );

  /// Serval itself — the Server process *and* the one ffmpeg per camera it spawns, which is why
  /// the Server reads it from cgroup accounting rather than from its own process counter.
  final double? containerPercent;

  /// The whole machine, including Mongo, go2rtc and anything else sharing the box.
  final double? hostPercent;

  final double? cores;

  /// The CPU ceiling, in cores, or null for none. Null is the normal answer — neither compose
  /// file sets a limit.
  final double? quotaCores;

  /// One, five and fifteen minutes. The figure that actually separates "busy" from
  /// "oversubscribed", which a percentage cannot.
  final List<double>? loadAverage;

  final String? unavailableReason;
}

/// Memory against the container's limit — the ceiling that actually bites, and the one whose
/// breach kills the container with no warning and no trace in the app.
class MemoryStats {
  const MemoryStats({
    this.usedBytes,
    this.limitBytes,
    this.percent,
    this.unavailableReason,
  });

  factory MemoryStats.fromJson(Map<String, dynamic> json) => MemoryStats(
    usedBytes: asInt(json['usedBytes']),
    limitBytes: asInt(json['limitBytes']),
    percent: asDouble(json['percent']),
    unavailableReason: json['unavailableReason'] as String?,
  );

  final int? usedBytes;

  /// Null means no limit is set, so there is nothing to be near.
  final int? limitBytes;

  final double? percent;
  final String? unavailableReason;
}

/// GPU utilisation, where the hardware publishes one — amdgpu from a sysfs file, Intel from the
/// i915 performance counters where the container has been allowed to open them.
class GpuStats {
  const GpuStats({
    this.busyPercent,
    this.driver,
    this.renderNode,
    this.vramUsedBytes,
    this.vramTotalBytes,
    this.engines = const [],
    this.hostWide = false,
    this.unavailableReason,
  });

  factory GpuStats.fromJson(Map<String, dynamic> json) => GpuStats(
    busyPercent: asDouble(json['busyPercent']),
    driver: json['driver'] as String?,
    renderNode: json['renderNode'] as String?,
    vramUsedBytes: asInt(json['vramUsedBytes']),
    vramTotalBytes: asInt(json['vramTotalBytes']),
    engines: [
      for (final engine in json['engines'] as List<dynamic>? ?? const [])
        GpuEngineStats.fromJson(engine as Map<String, dynamic>),
    ],
    hostWide: json['hostWide'] as bool? ?? false,
    unavailableReason: json['unavailableReason'] as String?,
  );

  final double? busyPercent;
  final String? driver;
  final String? renderNode;
  final int? vramUsedBytes;
  final int? vramTotalBytes;

  /// The per-engine split, where the source reports one — empty on amdgpu, which publishes a
  /// single number. [busyPercent] is the busiest of these, so this is the breakdown of the meter
  /// rather than anything to add up.
  final List<GpuEngineStats> engines;

  /// Unlike the processor and memory figures beside it, this is the whole GPU rather than
  /// Serval's share of it. The page says so under the meter rather than leaving it to be assumed.
  final bool hostWide;

  final String? unavailableReason;
}

/// One engine of a GPU that reports them separately — "render", "video", "blitter".
class GpuEngineStats {
  const GpuEngineStats({required this.name, required this.busyPercent});

  factory GpuEngineStats.fromJson(Map<String, dynamic> json) => GpuEngineStats(
    name: json['name'] as String? ?? '',
    busyPercent: asDouble(json['busyPercent']) ?? 0,
  );

  final String name;
  final double busyPercent;
}

/// How hard the object detector's accelerators are working — a pair of Coral Edge TPUs, today.
///
/// **The only group on this page the App hides outright.** The others describe hardware every host
/// has, so a missing figure is worth a sentence saying why; this describes hardware most hosts do
/// not have, and a *not reported* meter on every processor-only server would be noise standing in
/// for an answer nobody asked for. So [hasDevices] gates the whole meter.
///
/// A host that *has* accelerators and has lost them still lists them, unhealthy — the detector never
/// drops a device from its pool. The meter must not vanish at the moment it starts mattering.
class AcceleratorStats {
  const AcceleratorStats({
    this.label,
    this.busyPercent,
    this.inferencesPerSecond,
    this.declinedPerSecond,
    this.devices = const [],
    this.unavailableReason,
  });

  factory AcceleratorStats.fromJson(Map<String, dynamic> json) =>
      AcceleratorStats(
        label: json['label'] as String?,
        busyPercent: asDouble(json['busyPercent']),
        inferencesPerSecond: asDouble(json['inferencesPerSecond']),
        declinedPerSecond: asDouble(json['declinedPerSecond']),
        devices: switch (json['devices']) {
          final List<dynamic> entries => [
            for (final entry in entries)
              AcceleratorDeviceStats.fromJson(_object(entry)),
          ],
          _ => const [],
        },
        unavailableReason: json['unavailableReason'] as String?,
      );

  /// What to call these devices — *Edge TPU*. From the Server, because only it knows what the
  /// detector opened.
  final String? label;

  /// Time the devices spent inferring over the last window, against the time they were all available
  /// to. Pooled rather than the busiest device: a frame goes to whichever is idle, so the pool is the
  /// resource. That is the one place this meter departs from the Graphics meter above it, which shows
  /// its busiest *engine* because GPU engines are not interchangeable.
  final double? busyPercent;

  /// Inferences a second across every device.
  final double? inferencesPerSecond;

  /// Frames a second the detector refused because every device was still busy — the most direct
  /// statement there is that the accelerator, rather than the budget, is the limit.
  final double? declinedPerSecond;

  /// One entry per device, in the order the detector holds them.
  final List<AcceleratorDeviceStats> devices;

  final String? unavailableReason;

  /// Whether this host has an accelerator at all, which is what the meter is drawn on.
  bool get hasDevices => devices.isNotEmpty;

  /// True when at least one device has stopped answering. Real capacity has fallen with it.
  bool get degraded => devices.any((device) => !device.healthy);
}

/// One accelerator device over the last window.
///
/// Per device rather than pooled only, because they are routinely not alike: a Coral on a USB 2 path
/// measured 29.7 inferences a second against its twin's 64.2 on USB 3. The pooled meter is right
/// about capacity and silent about that, and it is a fault that otherwise looks like a slow model.
class AcceleratorDeviceStats {
  const AcceleratorDeviceStats({
    required this.name,
    required this.healthy,
    this.link,
    this.busyPercent,
    this.inferencesPerSecond,
    this.meanLatencyMs,
    this.failures = 0,
  });

  factory AcceleratorDeviceStats.fromJson(
    Map<String, dynamic> json,
  ) => AcceleratorDeviceStats(
    name: json['name'] as String? ?? '',
    // Absence must not read as broken. An older Server, or a payload this App only half
    // understands, is not a failed device.
    healthy: json['healthy'] as bool? ?? true,
    link: json['link'] as String?,
    busyPercent: asDouble(json['busyPercent']),
    inferencesPerSecond: asDouble(json['inferencesPerSecond']),
    meanLatencyMs: asDouble(json['meanLatencyMs']),
    failures: asInt(json['failures']) ?? 0,
  );

  /// How the device names itself — a Coral's sysfs path, `2-2`. The same short form the Server's
  /// startup line prints, so the two can be read against each other.
  final String name;

  final bool healthy;

  /// The link it is attached over — `USB 3`, `USB 2`. Null where the host does not publish one.
  final String? link;

  final double? busyPercent;
  final double? inferencesPerSecond;

  /// Mean time inside one inference — busy time over the count, not a percentile. Null over a window
  /// this device ran nothing in.
  final double? meanLatencyMs;

  /// Errors since the Server started, not over the window: a rate would round to zero for the
  /// failure that matters, which happens once.
  final int failures;
}

/// The media volume, and what each camera is using of it.
class DiskStats {
  const DiskStats({
    this.mountPoint,
    this.totalBytes,
    this.freeBytes,
    this.usedBytes,
    this.mediaBytes,
    this.scannedAt,
    this.scanSeconds,
    this.cameras = const [],
    this.unavailableReason,
  });

  factory DiskStats.fromJson(Map<String, dynamic> json) => DiskStats(
    mountPoint: json['mountPoint'] as String?,
    totalBytes: asInt(json['totalBytes']),
    freeBytes: asInt(json['freeBytes']),
    usedBytes: asInt(json['usedBytes']),
    mediaBytes: asInt(json['mediaBytes']),
    scannedAt: _time(json['scannedAt']),
    scanSeconds: asDouble(json['scanSeconds']),
    cameras: [
      for (final entry in (json['cameras'] as List<dynamic>? ?? const []))
        CameraDiskUsage.fromJson(_object(entry)),
    ],
    unavailableReason: json['unavailableReason'] as String?,
  );

  final String? mountPoint;
  final int? totalBytes;
  final int? freeBytes;

  /// Everything on the volume, Serval's and anyone else's. Deliberately not the same number as
  /// [mediaBytes] — on a shared pool they are very different, and conflating them would blame a
  /// full disk on Serval when something else filled it.
  final int? usedBytes;

  /// Only what Serval wrote. Null when the per-camera walk is switched off
  /// (`Serval:Vitals:DiskScanMinutes` of 0) or has not run yet.
  final int? mediaBytes;

  final DateTime? scannedAt;
  final double? scanSeconds;

  /// Biggest first, as the Server orders them. Includes one entry with a null [CameraDiskUsage.cameraId]
  /// for the conversation audio directory, which lives under the same root and is nobody's camera.
  final List<CameraDiskUsage> cameras;

  final String? unavailableReason;

  /// What is on the volume that is not Serval's, or null when either half is unknown.
  int? get otherBytes {
    final used = usedBytes;
    final media = mediaBytes;
    if (used == null || media == null) return null;
    final other = used - media;
    return other < 0 ? 0 : other;
  }
}

/// One camera's footprint, measured by walking its directory rather than summed from the recording
/// index — so it counts the orphaned segments and init files the index does not know about, which
/// are exactly the ones most likely to be quietly eating the volume.
class CameraDiskUsage {
  const CameraDiskUsage({
    required this.label,
    this.cameraId,
    this.bytes = 0,
    this.fileCount = 0,
    this.oldestSegmentAt,
    this.retentionDays,
    this.bytesPerDay,
    this.note,
  });

  factory CameraDiskUsage.fromJson(Map<String, dynamic> json) =>
      CameraDiskUsage(
        cameraId: json['cameraId'] as String?,
        label: (json['label'] as String?) ?? 'Unknown',
        bytes: asInt(json['bytes']) ?? 0,
        fileCount: asInt(json['fileCount']) ?? 0,
        oldestSegmentAt: _time(json['oldestSegmentAt']),
        retentionDays: asInt(json['retentionDays']),
        bytesPerDay: asDouble(json['bytesPerDay']),
        note: json['note'] as String?,
      );

  /// Null for the conversation audio directory, which belongs to no camera.
  final String? cameraId;

  final String label;
  final int bytes;
  final int fileCount;

  /// Start of the oldest indexed segment, so a byte count can be given a span — "7 days · 412 GB"
  /// rather than a number with nothing to compare it to.
  final DateTime? oldestSegmentAt;

  final int? retentionDays;

  /// The measured write rate, which is what the retention slider projects from. Measured rather
  /// than derived from a nominal bitrate: this is what the camera has actually written, audio and
  /// keyframes and all.
  final double? bytesPerDay;

  /// What this directory is, where a byte count alone would mislead.
  ///
  /// Null for a camera, whose row already says how far back it goes and what it keeps. Set for the
  /// directories retention never touches — saved clips only ever grow, and a row that looked like
  /// every other row would not say so.
  final String? note;
}

/// Which condition an alert is about. Deliberately a closed set the App knows, with anything
/// unrecognised decoding to null so it can be dropped rather than failing the whole payload.
enum VitalsAlertKind {
  diskCritical('diskCritical'),
  diskLow('diskLow'),
  memoryHigh('memoryHigh');

  const VitalsAlertKind(this.wireName);

  final String wireName;

  static VitalsAlertKind? fromWire(String? value) {
    for (final kind in values) {
      if (kind.wireName == value) return kind;
    }
    return null;
  }
}

/// Something the Server thinks somebody should know, already worded by the Server so the threshold
/// and its phrasing live in one place — including for a future notification path with no App
/// running.
class VitalsAlert {
  const VitalsAlert({
    required this.kind,
    required this.severity,
    required this.message,
  });

  /// Null for a kind this build does not recognise, which the caller drops.
  static VitalsAlert? fromJson(Map<String, dynamic> json) {
    final kind = VitalsAlertKind.fromWire(json['kind'] as String?);
    if (kind == null) return null;

    return VitalsAlert(
      kind: kind,
      severity: (json['severity'] as String?) ?? 'warning',
      message: (json['message'] as String?) ?? '',
    );
  }

  final VitalsAlertKind kind;

  /// `warning` or `critical`. Critical is what the wall refuses to let you dismiss.
  final String severity;

  /// Shown verbatim.
  final String message;

  bool get isCritical => severity == 'critical';
}

Map<String, dynamic> _object(Object? value) =>
    value is Map<String, dynamic> ? value : const {};

DateTime? _time(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toLocal() : null;
