// The Server status page.
//
// Almost all of this is about *absence*. The whole payload behind this screen is built so that a
// host which cannot measure something says so instead of reporting zero — an Intel or NVIDIA box
// publishes no GPU utilisation, a kernel without cgroup v2 publishes no per-container processor
// share, and a volume whose statvfs failed publishes nothing at all. Each of those must read as
// "not reported" here. A meter resting at 0% is what they turn into the moment anything on this
// page defaults a null to zero, and that is a measurement the Server never took.
//
// Rendered from a constructed `SystemStats` through `ServerScreenBody` rather than through a
// repository, so each of these states is reachable — the sample repository has exactly one, and it
// is the healthy one.
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/byte_labels.dart';
import 'package:serval_app/models/system_stats.dart';
import 'package:serval_app/models/vitals_history.dart';
import 'package:serval_app/screens/server_screen.dart';
import 'package:serval_app/widgets/vitals_meter.dart';
import 'package:serval_app/widgets/vitals_sparkline.dart';
import 'package:serval_app/theme/app_theme.dart';

void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1200, 900);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  Widget harness(SystemStats? stats, {VitalsHistory? history}) => MaterialApp(
    debugShowCheckedModeBanner: false,
    theme: buildServalTheme(),
    home: Scaffold(
      body: ServerScreenBody(stats: stats, history: history),
    ),
  );

  SystemStats healthy() => SystemStats(
    sampledAt: DateTime.now(),
    processUptimeSeconds: const Duration(days: 4).inSeconds.toDouble(),
    cpu: const CpuStats(
      containerPercent: 34,
      hostPercent: 41,
      cores: 8,
      loadAverage: [2.1, 1.8, 1.6],
    ),
    memory: const MemoryStats(
      usedBytes: 2617245696,
      limitBytes: 8589934592,
      percent: 30.5,
    ),
    gpu: const GpuStats(
      busyPercent: 42,
      driver: 'amdgpu',
      renderNode: 'renderD129',
      hostWide: true,
    ),
    disk: DiskStats(
      mountPoint: '/media',
      totalBytes: 4000000000000,
      freeBytes: 2200000000000,
      usedBytes: 1800000000000,
      mediaBytes: 1740000000000,
      scanSeconds: 4.8,
      cameras: [
        CameraDiskUsage(
          cameraId: 'driveway',
          label: 'Driveway',
          bytes: 412000000000,
          fileCount: 148231,
          oldestSegmentAt: DateTime.now().subtract(const Duration(days: 7)),
          retentionDays: 7,
          bytesPerDay: 58857142857,
        ),
        const CameraDiskUsage(
          cameraId: null,
          label: 'Conversation audio',
          bytes: 7000000000,
        ),
      ],
    ),
    detection: const DetectionStats(
      budgetPerSecond: 21.5,
      cameras: 4,
      examinedPerSecond: 8.4,
      shedPerSecond: 0,
      droppedFramesPerSecond: 0,
      coverage: 1,
    ),
  );

  testWidgets('lays out without overflow or unbounded constraints', (
    tester,
  ) async {
    await tester.pumpWidget(harness(healthy()));
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
  });

  testWidgets('folds to one column on a window too narrow for two', (
    tester,
  ) async {
    // The meters' column is a fixed width and the volume beside it is not, so there is a point
    // below which the pair stops fitting. Both sides of it have to survive: the golden only ever
    // renders the wide one.
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;

    for (final width in [520.0, 760.0, 800.0, 1400.0]) {
      view.physicalSize = Size(width, 900);
      await tester.pumpWidget(harness(healthy(), history: null));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull, reason: 'at ${width}px');

      // Whichever way it folded, the page is still the whole page.
      expect(find.text('The volume'), findsOneWidget, reason: 'at ${width}px');
      expect(find.text('Load'), findsOneWidget, reason: 'at ${width}px');
      expect(
        find.text('What is using it'),
        findsOneWidget,
        reason: 'at ${width}px',
      );
    }
  });

  testWidgets('leads with free space and names the volume', (tester) async {
    await tester.pumpWidget(harness(healthy()));
    await tester.pumpAndSettle();

    // The design's own sentence, from real fields rather than a hardcoded string. Twice, not
    // once: the hero figure and the bar's own *Free* key both say it.
    expect(find.text('2.2 TB'), findsNWidgets(2));
    expect(find.text('free of 4 TB'), findsOneWidget);
    expect(find.textContaining('/media'), findsWidgets);

    // And the split the hero number cannot show on its own — what is Serval's and what is not.
    expect(find.text('Recordings'), findsOneWidget);
    expect(find.text('1.7 TB'), findsOneWidget);
    expect(find.text('Everything else'), findsOneWidget);
  });

  testWidgets('shows all three load meters with their figures', (tester) async {
    await tester.pumpWidget(harness(healthy()));
    await tester.pumpAndSettle();

    expect(find.text('Processor'), findsOneWidget);
    expect(find.text('34%'), findsOneWidget);

    expect(find.text('Memory'), findsOneWidget);
    expect(find.text('2.6 GB of 8.6 GB'), findsOneWidget);

    // The driver is named, because which one it is decides whether there is a figure at all.
    expect(find.text('Graphics · amdgpu'), findsOneWidget);
    expect(find.text('42%'), findsOneWidget);
  });

  testWidgets('says the GPU figure is the whole GPU, not Serval’s share', (
    tester,
  ) async {
    await tester.pumpWidget(harness(healthy()));
    await tester.pumpAndSettle();

    expect(find.textContaining('The whole GPU'), findsOneWidget);
  });

  testWidgets('a driver that publishes nothing reads as not reported, never as 0%', (
    tester,
  ) async {
    final stats = SystemStats(
      cpu: healthy().cpu,
      memory: healthy().memory,
      gpu: const GpuStats(
        driver: 'nvidia',
        renderNode: 'renderD128',
        unavailableReason:
            'The NVIDIA driver publishes no usage figure to sysfs.',
      ),
      disk: healthy().disk,
      detection: healthy().detection,
    );

    await tester.pumpWidget(harness(stats));
    await tester.pumpAndSettle();

    expect(find.textContaining('publishes no usage figure'), findsOneWidget);

    // In words, and emphatically not a percentage. This is the assertion the whole nullable
    // payload exists to make possible.
    expect(find.text('not reported'), findsOneWidget);
    expect(find.text('0%'), findsNothing);

    // Not the em dash the rest of the app spends on a missing number: on this page the absence is
    // the content, and a glyph shared with "no value yet" is not enough to carry it.
    expect(find.text(kNoFigure), findsNothing);
  });

  /// An Intel host that has not been granted the capability is the case most operators will sit on,
  /// so the sentence under the meter has to be the one that names the line to add.
  testWidgets('an Intel host without the capability is told which line to add', (
    tester,
  ) async {
    final stats = SystemStats(
      cpu: healthy().cpu,
      memory: healthy().memory,
      gpu: const GpuStats(
        driver: 'i915',
        renderNode: 'renderD128',
        unavailableReason:
            'Intel reports GPU usage through a system-wide performance counter '
            'this container is not allowed to open. Add cap_add: [PERFMON] to '
            'the server in your compose file and restart.',
      ),
      disk: healthy().disk,
      detection: healthy().detection,
    );

    await tester.pumpWidget(harness(stats));
    await tester.pumpAndSettle();

    expect(find.text('Graphics · i915'), findsOneWidget);
    expect(find.textContaining('cap_add: [PERFMON]'), findsOneWidget);
    expect(find.text('not reported'), findsOneWidget);
    expect(find.text('0%'), findsNothing);
  });

  /// And once it has been. The meter is the busiest engine; the caption is which one, because on a
  /// recording server "40% encoding" and "40% describing" are different boxes.
  testWidgets(
    'an Intel host with counters shows the busiest engine and names it',
    (tester) async {
      final stats = SystemStats(
        cpu: healthy().cpu,
        memory: healthy().memory,
        gpu: const GpuStats(
          busyPercent: 41,
          driver: 'i915',
          renderNode: 'renderD128',
          engines: [
            GpuEngineStats(name: 'render', busyPercent: 3),
            GpuEngineStats(name: 'video', busyPercent: 41),
            GpuEngineStats(name: 'blitter', busyPercent: 0),
          ],
          hostWide: true,
        ),
        disk: healthy().disk,
        detection: healthy().detection,
      );

      await tester.pumpWidget(harness(stats));
      await tester.pumpAndSettle();

      expect(find.text('Graphics · i915'), findsOneWidget);
      expect(find.text('41%'), findsOneWidget);
      expect(find.text('not reported'), findsNothing);

      // Busiest first, and an engine sitting at zero is not worth a clause.
      expect(find.textContaining('Video 41%, render 3%'), findsOneWidget);
      expect(find.textContaining('blitter'), findsNothing);
    },
  );

  /// The accelerator meter is the one thing on this page that is hidden rather than degraded, and
  /// that exception is deliberate: every other meter describes hardware any host has, so a missing
  /// figure is worth a sentence. Most hosts have no accelerator at all, and a permanent *not
  /// reported* bar on all of them would be noise standing in for an answer nobody asked for.
  testWidgets('no accelerator means no meter at all, not an empty one', (
    tester,
  ) async {
    await tester.pumpWidget(harness(healthy()));
    await tester.pumpAndSettle();

    expect(find.text('Edge TPU'), findsNothing);
    expect(find.text('Accelerator'), findsNothing);
  });

  SystemStats withCorals(AcceleratorStats accelerator) => SystemStats(
    cpu: healthy().cpu,
    memory: healthy().memory,
    gpu: healthy().gpu,
    accelerator: accelerator,
    disk: healthy().disk,
    detection: healthy().detection,
  );

  /// The pair split across USB generations, which is the deployment this was built for. The meter is
  /// the pool; the caption is what each device is actually doing, because a device delivering a
  /// third of its twin is invisible in a pooled number and looks like a slow model.
  testWidgets('two Edge TPUs show the pool and name each device', (
    tester,
  ) async {
    await tester.pumpWidget(
      harness(
        withCorals(
          const AcceleratorStats(
            label: 'Edge TPU',
            busyPercent: 61,
            inferencesPerSecond: 92.5,
            declinedPerSecond: 0,
            devices: [
              AcceleratorDeviceStats(
                name: '2-2',
                healthy: true,
                link: 'USB 3',
                busyPercent: 78,
                inferencesPerSecond: 63.1,
                meanLatencyMs: 15.8,
              ),
              AcceleratorDeviceStats(
                name: '1-1',
                healthy: true,
                link: 'USB 2',
                busyPercent: 44,
                inferencesPerSecond: 29.4,
                meanLatencyMs: 33.4,
              ),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    // The Server's own word for them, not one this screen made up.
    expect(find.text('Edge TPU'), findsOneWidget);
    expect(find.text('61%'), findsOneWidget);

    expect(
      find.textContaining('2-2 at 63 a second, 15.8 ms each over USB 3.'),
      findsOneWidget,
    );
    expect(
      find.textContaining('1-1 at 29 a second, 33.4 ms each over USB 2.'),
      findsOneWidget,
    );

    // Idle is only worth saying when it is idle.
    expect(find.textContaining('Idle is normal'), findsNothing);
  });

  /// Losing a device is the case the meter exists for, and the one a pooled figure cannot carry: the
  /// bar simply reads lower, which is indistinguishable from a quiet afternoon.
  testWidgets('a device that stopped answering is named', (tester) async {
    await tester.pumpWidget(
      harness(
        withCorals(
          const AcceleratorStats(
            label: 'Edge TPU',
            busyPercent: 39,
            inferencesPerSecond: 63.1,
            devices: [
              AcceleratorDeviceStats(
                name: '2-2',
                healthy: true,
                link: 'USB 3',
                busyPercent: 78,
                inferencesPerSecond: 63.1,
                meanLatencyMs: 15.8,
              ),
              AcceleratorDeviceStats(
                name: '1-1',
                healthy: false,
                link: 'USB 2',
                busyPercent: 0,
                inferencesPerSecond: 0,
                failures: 4,
              ),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining('1-1 has stopped answering'), findsOneWidget);
    // Still drawn. The meter must not disappear at the moment it starts mattering.
    expect(find.text('Edge TPU'), findsOneWidget);
    expect(find.text('39%'), findsOneWidget);
  });

  /// Saturation, which is the number that says the accelerator rather than the budget is the limit.
  testWidgets('a saturated pool says what it is refusing', (tester) async {
    await tester.pumpWidget(
      harness(
        withCorals(
          const AcceleratorStats(
            label: 'Edge TPU',
            busyPercent: 98,
            inferencesPerSecond: 95.8,
            declinedPerSecond: 1.4,
            devices: [
              AcceleratorDeviceStats(
                name: '2-2',
                healthy: true,
                busyPercent: 99,
                inferencesPerSecond: 63.1,
                meanLatencyMs: 15.8,
              ),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Declining 1.4 frames a second'),
      findsOneWidget,
    );

    // A device with no link file reported still reads cleanly — the clause is simply absent.
    expect(find.textContaining('over USB'), findsNothing);
  });

  /// Before there are two counter readings to subtract there is no figure, and the same *not
  /// reported* rule the GPU meter follows applies here — a bar at 0% would claim an idle accelerator.
  testWidgets('an accelerator still warming up reads as not reported', (
    tester,
  ) async {
    await tester.pumpWidget(
      harness(
        withCorals(
          const AcceleratorStats(
            label: 'Edge TPU',
            unavailableReason:
                'Warming up — a usage figure is the difference between two '
                'counter readings.',
            devices: [
              AcceleratorDeviceStats(name: '2-2', healthy: true),
              AcceleratorDeviceStats(name: '1-1', healthy: true),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Edge TPU'), findsOneWidget);
    expect(find.textContaining('Warming up'), findsOneWidget);
    expect(find.text('not reported'), findsOneWidget);
    expect(find.text('0%'), findsNothing);
  });

  testWidgets('a track with no measurement behind it is hatched, not empty', (
    tester,
  ) async {
    // The signal that survives being seen from across the room. An empty track and a track at 0%
    // are the same picture; a hatched one is not, and this is the only thing separating them once
    // the figure beside it is gone.
    final stats = SystemStats(
      cpu: healthy().cpu,
      memory: healthy().memory,
      gpu: const GpuStats(
        driver: 'i915',
        unavailableReason:
            'Intel reports GPU usage through a system-wide performance counter '
            'this container is not allowed to open. Add cap_add: [PERFMON] to '
            'the server in your compose file and restart.',
      ),
      disk: healthy().disk,
      detection: healthy().detection,
    );

    await tester.pumpWidget(harness(stats));
    await tester.pumpAndSettle();

    final tracks = tester.widgetList<MeterTrack>(find.byType(MeterTrack));

    // Exactly the one that could not be measured, and every other meter on the page unhatched.
    // Counted against the total rather than a literal, so adding a meter does not turn this into a
    // failure about a number nobody meant to assert.
    expect(tracks.where((t) => t.unavailable), hasLength(1));
    expect(tracks.where((t) => !t.unavailable), hasLength(tracks.length - 1));
  });

  testWidgets('a processor share this kernel cannot separate says so', (
    tester,
  ) async {
    final stats = SystemStats(
      cpu: const CpuStats(
        hostPercent: 41,
        cores: 8,
        unavailableReason:
            'This host publishes no cgroup v2 CPU accounting, so Serval’s own share cannot be '
            'separated from the rest of the machine.',
      ),
      memory: healthy().memory,
      gpu: healthy().gpu,
      disk: healthy().disk,
      detection: healthy().detection,
    );

    await tester.pumpWidget(harness(stats));
    await tester.pumpAndSettle();

    expect(find.textContaining('cgroup v2'), findsOneWidget);
  });

  testWidgets('a volume that could not be measured explains itself', (
    tester,
  ) async {
    const stats = SystemStats(
      disk: DiskStats(
        unavailableReason:
            'No mounted volume was found for the media root (/media).',
      ),
    );

    await tester.pumpWidget(harness(stats));
    await tester.pumpAndSettle();

    expect(find.textContaining('No mounted volume'), findsOneWidget);
    expect(find.text('free of 4 TB'), findsNothing);
  });

  testWidgets('an alert is repeated on the page it sends you to', (
    tester,
  ) async {
    final stats = SystemStats(
      cpu: healthy().cpu,
      memory: healthy().memory,
      gpu: healthy().gpu,
      disk: healthy().disk,
      detection: healthy().detection,
      alerts: const [
        VitalsAlert(
          kind: VitalsAlertKind.diskLow,
          severity: 'warning',
          message: 'Under 10% free on /media.',
        ),
      ],
    );

    await tester.pumpWidget(harness(stats));
    await tester.pumpAndSettle();

    expect(find.text('Under 10% free on /media.'), findsOneWidget);
  });

  group('what is using it', () {
    testWidgets('lists each camera with its span, retention and measured rate', (
      tester,
    ) async {
      await tester.pumpWidget(harness(healthy()));
      await tester.pumpAndSettle();

      expect(find.text('Driveway'), findsOneWidget);
      expect(find.text('412 GB'), findsOneWidget);

      // The byte count given a span, which is the only form in which it answers anything.
      expect(
        find.text('back 7 days · keeping 7 days · about 59 GB/day'),
        findsOneWidget,
      );
    });

    testWidgets('the conversations directory is listed but is not a camera', (
      tester,
    ) async {
      await tester.pumpWidget(harness(healthy()));
      await tester.pumpAndSettle();

      // Present, so the per-camera figures add up to the total — but with no retention and no
      // rate, because neither applies to it.
      expect(find.text('Conversation audio'), findsOneWidget);
      expect(find.text('7 GB'), findsOneWidget);
    });

    testWidgets('a Server with the walk switched off says how to turn it on', (
      tester,
    ) async {
      final stats = SystemStats(
        cpu: healthy().cpu,
        memory: healthy().memory,
        gpu: healthy().gpu,
        disk: const DiskStats(
          mountPoint: '/media',
          totalBytes: 4000000000000,
          freeBytes: 2200000000000,
          usedBytes: 1800000000000,
        ),
      );

      await tester.pumpWidget(harness(stats));
      await tester.pumpAndSettle();

      expect(find.textContaining('DiskScanMinutes'), findsOneWidget);

      // The volume figures survive without it — they are one statvfs and are what the alerts are
      // built on. The bar collapses to in-use and free, with no *Recordings* segment to draw.
      expect(find.text('free of 4 TB'), findsOneWidget);
      expect(find.text('In use'), findsOneWidget);
      expect(find.text('Recordings'), findsNothing);
    });
  });

  group('the sparklines', () {
    /// [gpu] defaults to a series with a hole in it, which is the case that matters.
    VitalsHistory history({int count = 40, List<double?>? gpu}) {
      final start = DateTime.utc(2026, 8, 3, 22);

      return VitalsHistory(
        sampledAt: [
          for (var i = 0; i < count; i++) start.add(Duration(seconds: i * 5)),
        ],
        cpu: [for (var i = 0; i < count; i++) i % 4 < 2 ? 58.4 : 11.2],
        memory: [for (var i = 0; i < count; i++) 27.1],
        gpu:
            gpu ??
            [for (var i = 0; i < count; i++) i >= 12 && i <= 14 ? null : 3.0],
        windowMinutes: 60,
      );
    }

    testWidgets('draw under the meters when the Server has history', (
      tester,
    ) async {
      await tester.pumpWidget(harness(healthy(), history: history()));
      await tester.pumpAndSettle();

      // One per meter: processor, memory, GPU.
      expect(find.byType(VitalsSparkline), findsNWidgets(3));
    });

    testWidgets('are absent entirely where there is no history', (
      tester,
    ) async {
      // The sample repository, and every session before the first read lands. The page draws the
      // meters alone rather than reserving an empty frame for a sparkline.
      await tester.pumpWidget(harness(healthy()));
      await tester.pumpAndSettle();

      expect(find.byType(VitalsSparkline), findsNothing);
    });

    testWidgets(
      'a hole in a series is a break in the line, never a dip to zero',
      (tester) async {
        await tester.pumpWidget(harness(healthy(), history: history()));
        await tester.pumpAndSettle();

        final gpu = tester
            .widgetList<VitalsSparkline>(find.byType(VitalsSparkline))
            .firstWhere((s) => s.series.values.contains(null));

        // The nulls reach the widget as nulls — nothing upstream coalesced them — and they keep
        // their slots so the runs either side stay on their own instants.
        expect(gpu.series.values[12], isNull);
        expect(gpu.series.values.whereType<double>(), isNotEmpty);
        expect(gpu.series.values, hasLength(gpu.series.sampledAt.length));
      },
    );

    testWidgets('a host that can never measure a figure gets no chart at all', (
      tester,
    ) async {
      // Every GPU sample null: an Intel or NVIDIA box. The meter keeps the Server's sentence and
      // draws no frame, rather than an empty chart implying the line is merely offscreen.
      await tester.pumpWidget(
        harness(
          healthy(),
          history: history(gpu: List<double?>.filled(40, null)),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(VitalsSparkline), findsNWidgets(2));
    });

    testWidgets('a buffer still filling after a restart draws what it has', (
      tester,
    ) async {
      // Four minutes into a sixty-minute window. The x-axis stays the full hour, so this reads as
      // a short line rather than stretching edge to edge and looking complete.
      await tester.pumpWidget(harness(healthy(), history: history(count: 48)));
      await tester.pumpAndSettle();

      final line = tester
          .widgetList<VitalsSparkline>(find.byType(VitalsSparkline))
          .first;

      expect(line.series.sampledAt, hasLength(48));
      expect(line.series.windowMinutes, 60);
    });
  });

  testWidgets(
    'before the first sample the page waits rather than inventing zeroes',
    (tester) async {
      await tester.pumpWidget(harness(null));
      await tester.pumpAndSettle();

      expect(find.text('Reading the Server…'), findsOneWidget);
      expect(find.text('0%'), findsNothing);
    },
  );

  // The one part of this page that acts. Everything above is about a figure being drawn as missing;
  // this is about a button being drawn at all.
  group('configuration backup', () {
    Widget backupHarness({
      VoidCallback? onBackup,
      VoidCallback? onRestore,
      String? busy,
      String? status,
      String? error,
    }) => MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(
        body: ServerScreenBody(
          stats: healthy(),
          onBackup: onBackup,
          onRestore: onRestore,
          configBusy: busy,
          configStatus: status,
          configError: error,
        ),
      ),
    );

    /// Both actions are Admin-only, and the sample repository has no Server to reach — so a Viewer
    /// and the design harness both get null callbacks. Asserting the section vanishes entirely is
    /// what keeps `goldens/server.png` stable: that golden renders through the sample repository,
    /// and a section that merely dimmed itself would change the picture.
    testWidgets('is absent altogether when there is nothing behind it', (
      tester,
    ) async {
      await tester.pumpWidget(backupHarness());
      await tester.pumpAndSettle();

      expect(find.text('Configuration backup'), findsNothing);
      expect(find.text('Download backup'), findsNothing);
      expect(find.text('Restore from file…'), findsNothing);
    });

    testWidgets('offers both actions to an Admin, and warns before either', (
      tester,
    ) async {
      var backups = 0;
      await tester.pumpWidget(
        backupHarness(onBackup: () => backups++, onRestore: () {}),
      );
      await tester.pumpAndSettle();

      expect(find.text('Configuration backup'), findsOneWidget);
      expect(
        find.textContaining('password hash in plain text'),
        findsOneWidget,
      );

      // The section is the last thing on a scrolling page, so at this viewport it is built but
      // below the fold — and a tap outside the viewport is not a tap.
      await tester.ensureVisible(find.text('Download backup'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Download backup'));
      await tester.pumpAndSettle();
      expect(backups, 1);
    });

    /// Inert rather than absent while something is in flight — a button that is not there reads as
    /// a bug, one that is there and greyed reads as a condition unmet.
    testWidgets('holds both buttons still while a restore is running', (
      tester,
    ) async {
      var presses = 0;
      await tester.pumpWidget(
        backupHarness(
          onBackup: () => presses++,
          onRestore: () => presses++,
          busy: 'Restoring…',
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Restoring…'), findsOneWidget);

      await tester.ensureVisible(find.text('Download backup'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Download backup'), warnIfMissed: false);
      await tester.tap(find.text('Restore from file…'), warnIfMissed: false);
      await tester.pumpAndSettle();

      expect(presses, 0);
    });

    testWidgets('says where the file went, and says what went wrong', (
      tester,
    ) async {
      await tester.pumpWidget(
        backupHarness(
          onBackup: () {},
          onRestore: () {},
          status:
              'Saved serval-config-20260808-140311.json to /home/j/Downloads',
        ),
      );
      await tester.pumpAndSettle();
      expect(
        find.textContaining('serval-config-20260808-140311.json'),
        findsOneWidget,
      );

      await tester.pumpWidget(
        backupHarness(
          onBackup: () {},
          onRestore: () {},
          error: 'That file is not a Serval configuration backup.',
        ),
      );
      await tester.pumpAndSettle();
      expect(
        find.text('That file is not a Serval configuration backup.'),
        findsOneWidget,
      );
    });
  });
}
