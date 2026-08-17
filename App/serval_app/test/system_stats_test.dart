// `SystemStats.fromJson` against a payload a real Server actually produced.
//
// Every other test in this suite builds the model by hand, which means none of them can catch the
// failure that matters most here: a field the Server spells differently from the App. A `fromJson`
// that silently reads null for a key that does not exist is exactly as quiet as one that works,
// and the page it feeds is *designed* to render nulls calmly — so a rename would ship as "the
// meters stopped filling in" with nothing red anywhere.
//
// `fixtures/system_stats.json` is a verbatim capture from `GET /api/system/stats`, taken from a
// server running against a media root of known size. Re-capture it if the contract moves:
//
//   curl -s localhost:8080/api/system/stats -H "Authorization: Bearer $TOKEN" | jq . \
//     > test/fixtures/system_stats.json
//
// This is the same argument `telemetry_test.dart` makes for keeping copied-off-a-live-Server
// payloads rather than invented ones.
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/system_stats.dart';

void main() {
  late SystemStats stats;

  setUpAll(() {
    final json =
        jsonDecode(File('test/fixtures/system_stats.json').readAsStringSync())
            as Map<String, dynamic>;
    stats = SystemStats.fromJson(json);
  });

  test('the processor figures both arrive, and are not each other', () {
    // Two separate measurements: the container's own share, and the whole machine. Reading one
    // into both fields would be invisible on a box where they are close, which they are here.
    expect(stats.cpu.containerPercent, closeTo(4.48, 0.01));
    expect(stats.cpu.hostPercent, closeTo(4.61, 0.01));
    expect(stats.cpu.cores, 32);

    // Null is the normal answer: neither compose file sets a CPU limit.
    expect(stats.cpu.quotaCores, isNull);
    expect(stats.cpu.loadAverage, [4.69, 4.86, 4.36]);
  });

  test('a group this host cannot measure carries its reason, not a zero', () {
    // The capture came from a bare host, where the cgroup *root* has no memory.current — the
    // exact degraded path the settings page has to render as "not reported".
    expect(stats.memory.usedBytes, isNull);
    expect(stats.memory.limitBytes, isNull);
    expect(stats.memory.percent, isNull);
    expect(stats.memory.unavailableReason, contains('cgroup v2'));
  });

  test('the GPU is the one that answered, not the one that was configured', () {
    // Captured from a two-GPU box. renderD128 is an NVIDIA card with no gpu_busy_percent;
    // renderD129 is the AMD one that has it, and is what the Server fell through to.
    expect(stats.gpu.renderNode, 'renderD129');
    expect(stats.gpu.driver, 'amdgpu');
    expect(stats.gpu.busyPercent, 0);
    expect(stats.gpu.vramTotalBytes, 536870912);

    // Stated on the wire rather than assumed, because the page says so under the meter.
    expect(stats.gpu.hostWide, isTrue);

    // amdgpu publishes one number, so there is no split to report. Empty rather than a
    // single-element list naming the whole GPU, which would be a breakdown of nothing.
    expect(stats.gpu.engines, isEmpty);
  });

  test('an Intel payload carries the per-engine split behind the meter', () {
    // i915 has no gpu_busy_percent. What it has is a counter per engine, and the meter is the
    // busiest of them — so busyPercent must be findable in the list, not beside it.
    final gpu = GpuStats.fromJson({
      'busyPercent': 41,
      'driver': 'i915',
      'renderNode': 'renderD128',
      'hostWide': true,
      'engines': [
        {'name': 'render', 'busyPercent': 3},
        {'name': 'video', 'busyPercent': 41},
        {'name': 'video enhance', 'busyPercent': 0},
      ],
    });

    expect(gpu.busyPercent, 41);
    expect(gpu.engines.map((e) => e.name), [
      'render',
      'video',
      'video enhance',
    ]);
    expect(gpu.engines[1].busyPercent, 41);

    // An integrated part has no pool of its own, and inventing one would be a figure the payload
    // does not carry.
    expect(gpu.vramTotalBytes, isNull);
  });

  test('a processor-only host carries an accelerator group with no devices', () {
    // The Server sends the group with an explicit null device list rather than omitting it, so this
    // has to decode to "nothing to draw" rather than to a meter with an empty bar. It is the one
    // group the page hides outright.
    final accelerator = AcceleratorStats.fromJson({
      'label': null,
      'busyPercent': null,
      'inferencesPerSecond': null,
      'declinedPerSecond': null,
      'devices': null,
      'unavailableReason':
          "This server's object detector runs on the processor, so there is no "
          'accelerator to report.',
    });

    expect(accelerator.hasDevices, isFalse);
    expect(accelerator.devices, isEmpty);
    expect(accelerator.busyPercent, isNull);
    expect(accelerator.degraded, isFalse);
  });

  test('two Edge TPUs decode with their own throughput, latency and link', () {
    final accelerator = AcceleratorStats.fromJson({
      'label': 'Edge TPU',
      'busyPercent': 61,
      'inferencesPerSecond': 92.5,
      'declinedPerSecond': 0,
      'devices': [
        {
          'name': '2-2',
          'healthy': true,
          'link': 'USB 3',
          'busyPercent': 78,
          'inferencesPerSecond': 63.1,
          'meanLatencyMs': 15.8,
          'failures': 0,
        },
        {
          'name': '1-1',
          'healthy': false,
          'link': 'USB 2',
          'busyPercent': 0,
          'inferencesPerSecond': 0,
          'meanLatencyMs': null,
          'failures': 4,
        },
      ],
    });

    expect(accelerator.label, 'Edge TPU');
    expect(accelerator.hasDevices, isTrue);
    expect(accelerator.devices.map((d) => d.name), ['2-2', '1-1']);
    expect(accelerator.devices[0].link, 'USB 3');
    expect(accelerator.devices[0].meanLatencyMs, 15.8);

    // A device that ran nothing has no latency to report, and null must not arrive as a zero — a
    // 0 ms inference would read as the fastest device on the host.
    expect(accelerator.devices[1].meanLatencyMs, isNull);
    expect(accelerator.devices[1].failures, 4);

    // One device down is a degraded pool, which is what the caption leads with.
    expect(accelerator.degraded, isTrue);
  });

  test('a device with nothing said about its health is not assumed broken', () {
    // Absence is not a failure. An older Server, or a payload this App only half understands,
    // should not paint a working accelerator as lost.
    final device = AcceleratorDeviceStats.fromJson({'name': '2-2'});

    expect(device.healthy, isTrue);
    expect(device.link, isNull);
    expect(device.busyPercent, isNull);
    expect(device.failures, 0);
  });

  test('the volume splits into Serval’s footage and everything else', () {
    expect(stats.disk.mountPoint, '/tmp');
    expect(stats.disk.totalBytes, 67058184192);
    expect(stats.disk.freeBytes, 61561434112);

    // Deliberately different numbers: used is the whole volume, media is only ours.
    expect(stats.disk.usedBytes, 5496750080);
    expect(stats.disk.mediaBytes, 13926400);
    expect(stats.disk.otherBytes, 5496750080 - 13926400);
  });

  test('per-camera figures survive with their span and measured rate', () {
    final frontDoor = stats.disk.cameras.first;

    expect(frontDoor.cameraId, 'front-door');
    expect(frontDoor.label, 'Front door');

    // Ground truth: `du -sb` on that directory was 10,342,400 across six files.
    expect(frontDoor.bytes, 10342400);
    expect(frontDoor.fileCount, 6);

    // The camera's own override, not the server default of 7.
    expect(frontDoor.retentionDays, 14);

    // 10,342,400 bytes over the seven days back to its oldest indexed segment.
    expect(frontDoor.oldestSegmentAt, isNotNull);
    expect(frontDoor.bytesPerDay, closeTo(10342400 / 7, 2000));
  });

  test('the conversations directory is present and belongs to no camera', () {
    // Present so the per-camera figures add up to mediaBytes, and null-id so nothing treats it as
    // a camera. It also has no retention and no rate, because neither applies to it.
    final conversations = stats.disk.cameras.last;

    expect(conversations.cameraId, isNull);
    expect(conversations.label, 'conversations');
    expect(conversations.retentionDays, isNull);
    expect(conversations.bytesPerDay, isNull);

    final total = stats.disk.cameras.fold<int>(0, (sum, c) => sum + c.bytes);
    expect(total, stats.disk.mediaBytes);
  });

  test('an alert decodes to its kind and keeps the Server’s wording', () {
    final alert = stats.alerts.single;

    expect(alert.kind, VitalsAlertKind.diskLow);
    expect(alert.isCritical, isFalse);
    expect(alert.message, startsWith('Under 99% free on /tmp.'));
  });

  group('tolerance', () {
    test('an alert kind this build does not know is dropped, not fatal', () {
      // One unknown alert must not cost the App the alerts it does understand, nor the rest of
      // the stats payload sitting beside them.
      final stats = SystemStats.fromJson({
        'alerts': [
          {'kind': 'somethingNew', 'severity': 'warning', 'message': '…'},
          {'kind': 'diskLow', 'severity': 'warning', 'message': 'known'},
        ],
      });

      expect(stats.alerts.single.kind, VitalsAlertKind.diskLow);
    });

    test('an empty payload decodes to all-null rather than throwing', () {
      final stats = SystemStats.fromJson(const {});

      expect(stats.cpu.containerPercent, isNull);
      expect(stats.disk.cameras, isEmpty);
      expect(stats.alerts, isEmpty);
    });

    test('a whole-numbered double arriving as an int still reads', () {
      // JSON has one number type, so 41.0 is serialised as `41` and would otherwise fail an
      // `as double` cast on the first tidy percentage the Server produces.
      final stats = SystemStats.fromJson(const {
        'cpu': {
          'containerPercent': 41,
          'loadAverage': [3, 2, 1],
        },
      });

      expect(stats.cpu.containerPercent, 41.0);
      expect(stats.cpu.loadAverage, [3.0, 2.0, 1.0]);
    });
  });
}
