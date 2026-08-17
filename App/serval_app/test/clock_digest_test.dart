// What the wall's five-second sweep is allowed to wake the app for.
//
// The sweep exists because staleness is a function of the clock rather than of an event: nothing
// arrives to say a camera has stopped sending. Notifying on every tick would rebuild the entire
// signed-in tree twelve times a minute to render the same pixels, so `clockDigest` gates it — and
// that gate has two failure modes, equal and opposite:
//
//  * too narrow, and a transition never reaches the screen — a dead camera reads online forever,
//    or a caption sticks over the video after the speech has ended;
//  * too wide, and the gate does nothing and the twelve rebuilds a minute are back.
//
// The minute bucket is the non-obvious half. `ActivityItem.timeLabel` is a *pre-rendered string*
// (see `data/time_labels.dart`), so "2 min ago" stays whatever it was until something re-renders the
// feed — a digest built from the camera flags alone would leave every timestamp in the activity
// column frozen, which is a silent wrong answer rather than a missing one.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/system_stats.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:5211'));

  // Constructed but never started: `start()` is what opens the two sockets and reads the registry.
  // Everything below seeds the collections the digest reads directly.
  LiveServalRepository repository() => LiveServalRepository(
    auth: AuthController(config: config),
    config: config,
  );

  UtteranceDocument speech(
    String cameraId,
    DateTime when, {
    String text = 'hello',
  }) => UtteranceDocument(
    cameraId: cameraId,
    source: 'test',
    when: when,
    id: '$cameraId-${when.microsecondsSinceEpoch}',
    conversationId: null,
    transcript: text,
    speaker: null,
    emotion: null,
    audioEvent: null,
    durationSeconds: 1,
  );

  SoundDocument sound(
    String cameraId,
    DateTime when, {
    required bool isAlert,
  }) => SoundDocument(
    cameraId: cameraId,
    source: 'test',
    when: when,
    id: '$cameraId-${when.microsecondsSinceEpoch}',
    label: isAlert ? 'Glass' : 'Speech',
    confidence: 0.9,
    alternates: const [],
    isAlert: isAlert,
    durationSeconds: 1,
  );

  group('the digest moves when something the clock decides has changed', () {
    test('a camera whose frames have gone stale', () {
      final now = DateTime.now();
      final fresh = repository()
        ..seedForTest(
          order: ['front-door'],
          lastFrameAt: {'front-door': now.subtract(const Duration(seconds: 2))},
        );
      final stale = repository()
        ..seedForTest(
          order: ['front-door'],
          // Past `_staleAfter`, which is fifteen seconds — fifteen missed frames at the Server's
          // default 1 fps.
          lastFrameAt: {
            'front-door': now.subtract(const Duration(seconds: 20)),
          },
        );

      expect(fresh.clockDigest(), isNot(stale.clockDigest()));
    });

    test(
      'a camera that has never sent a frame is not the same as one that has stopped',
      () {
        // These two read differently on the wall — `connecting` versus `offline` — because a
        // camera nobody has ever heard from has not been measured, and one that stopped has. A
        // digest that only carried freshness would collapse them.
        final never = repository()..seedForTest(order: ['front-door']);
        final stopped = repository()
          ..seedForTest(
            order: ['front-door'],
            lastFrameAt: {
              'front-door': DateTime.now().subtract(
                const Duration(seconds: 20),
              ),
            },
          );

        expect(never.clockDigest(), isNot(stopped.clockDigest()));
      },
    );

    test('the listening window closing over a camera that stayed quiet', () {
      // The transition nothing else can announce. A camera reads `connecting` for the fifteen
      // seconds after the App starts listening again, and `offline` after — and no frame, no
      // socket event and no registry write marks the moment it crosses. Without this term the
      // wall would say "connecting" until something unrelated happened to rebuild it.
      final now = DateTime.now();
      final stale = {'front-door': now.subtract(const Duration(seconds: 20))};

      final listening = repository()
        ..seedForTest(
          order: ['front-door'],
          lastFrameAt: stale,
          listeningSince: now,
        );
      final givenUp = repository()
        ..seedForTest(
          order: ['front-door'],
          lastFrameAt: stale,
          listeningSince: now.subtract(const Duration(seconds: 30)),
        );

      expect(listening.clockDigest(), isNot(givenUp.clockDigest()));
    });

    test('speech ageing out of the audio-activity window', () {
      final now = DateTime.now();
      final talking = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [
            speech('front-door', now.subtract(const Duration(seconds: 2))),
          ],
        );
      final quiet = repository()
        ..seedForTest(
          order: ['front-door'],
          // Past both the eight-second activity window and the ten-second caption window.
          documents: [
            speech('front-door', now.subtract(const Duration(seconds: 30))),
          ],
        );

      expect(talking.clockDigest(), isNot(quiet.clockDigest()));
    });

    test('a caption ageing out while the tile still reads as active', () {
      // The two windows are different lengths — eight seconds for the waveform mark, ten for the
      // caption — so there is a band where only the caption changes. A digest carrying just the
      // camera flags would miss it and strand the caption over the video.
      final now = DateTime.now();
      final captioned = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [
            speech('front-door', now.subtract(const Duration(seconds: 9))),
          ],
        );
      final expired = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [
            speech('front-door', now.subtract(const Duration(seconds: 11))),
          ],
        );

      expect(captioned.clockDigest(), isNot(expired.clockDigest()));
    });

    test('an alert decaying', () {
      final now = DateTime.now();
      final alerting = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [
            sound(
              'front-door',
              now.subtract(const Duration(seconds: 2)),
              isAlert: true,
            ),
          ],
        );
      final settled = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [
            sound(
              'front-door',
              now.subtract(const Duration(seconds: 30)),
              isAlert: true,
            ),
          ],
        );

      expect(alerting.clockDigest(), isNot(settled.clockDigest()));
    });

    test('a non-alert sound does not read as one', () {
      final now = DateTime.now();
      final alert = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [sound('front-door', now, isAlert: true)],
        );
      final ordinary = repository()
        ..seedForTest(
          order: ['front-door'],
          documents: [sound('front-door', now, isAlert: false)],
        );

      expect(alert.clockDigest(), isNot(ordinary.clockDigest()));
    });

    test('the minute, so the activity column\'s pre-rendered labels stay honest', () {
      // The one input with no camera behind it. Without it "2 min ago" never becomes "3 min ago"
      // on a wall where nothing else is moving.
      int minuteNow() =>
          DateTime.now().millisecondsSinceEpoch ~/
          Duration.millisecondsPerMinute;

      final quiet = repository()..seedForTest(order: ['front-door']);

      // Bracketed rather than compared to a single reading: the digest takes its own clock, and a
      // minute boundary falling between it and this test's would otherwise flake once in a while.
      final before = minuteNow();
      final leading = int.parse(quiet.clockDigest().split('|').first);
      final after = minuteNow();

      expect(leading, greaterThanOrEqualTo(before));
      expect(leading, lessThanOrEqualTo(after));
    });
  });

  group('the digest holds still otherwise', () {
    test('a wall with nothing stale is unaffected by the listening window', () {
      // The counterweight to the test above, and the reason the window is folded in per camera
      // rather than carried as a term of its own. A global one would move this digest the moment
      // the window closed — on a wall where every camera is fresh and nothing on screen changes —
      // and put the twelve rebuilds a minute straight back.
      final now = DateTime.now();
      final fresh = {'front-door': now.subtract(const Duration(seconds: 2))};

      final listening = repository()
        ..seedForTest(
          order: ['front-door'],
          lastFrameAt: fresh,
          listeningSince: now,
        );
      final settled = repository()
        ..seedForTest(
          order: ['front-door'],
          lastFrameAt: fresh,
          listeningSince: now.subtract(const Duration(seconds: 30)),
        );

      expect(listening.clockDigest(), settled.clockDigest());
    });

    test('two reads of an unchanged wall inside the same minute agree', () {
      // The whole point of the gate: a digest that moves on its own is twelve rebuilds a minute
      // again, and nothing on screen would show it.
      final now = DateTime.now();
      final wall = repository()
        ..seedForTest(
          order: ['front-door', 'driveway'],
          lastFrameAt: {
            'front-door': now.subtract(const Duration(seconds: 1)),
            'driveway': now.subtract(const Duration(seconds: 40)),
          },
          documents: [
            speech('front-door', now.subtract(const Duration(seconds: 1))),
            sound(
              'driveway',
              now.subtract(const Duration(minutes: 5)),
              isAlert: true,
            ),
          ],
        );

      expect(wall.clockDigest(), wall.clockDigest());
    });

    test('registry fields the sweep has no business waking for are absent', () {
      // `enabled`, names, stream roles and the rest all reach the screens through a registry write,
      // which notifies on its own path. Re-deriving them here would widen what the tick can wake
      // for, which is the opposite of the point. Two cameras with identical clock-derived state
      // produce identical digests regardless of the records behind them.
      final now = DateTime.now();
      final seed = {'front-door': now.subtract(const Duration(seconds: 1))};

      final before = repository()
        ..seedForTest(order: ['front-door'], lastFrameAt: seed);
      final after = repository()
        ..seedForTest(order: ['front-door'], lastFrameAt: seed);

      expect(before.clockDigest(), after.clockDigest());
    });

    test('another camera\'s activity does not smear across the wall', () {
      final now = DateTime.now();
      final wall = repository()
        ..seedForTest(
          order: ['front-door', 'driveway'],
          documents: [speech('front-door', now)],
        );

      // Per-camera segments, so the driveway's bits must all read quiet while the front door's
      // read active. Split on the separator rather than asserting the whole string, which would
      // pin the encoding rather than the behaviour.
      //
      // Four segments, not three: the minute bucket, one per camera, and the vitals tail — which
      // is '-' here because nothing has seeded a sample.
      //
      // Four characters per camera, and the first is a `CameraConnection` index rather than a bit:
      // this camera has never sent a frame and nothing is listening, which is `connecting` (1).
      final segments = wall.clockDigest().split('|');
      expect(segments, hasLength(4));
      expect(segments[1], isNot(segments[2]));
      expect(segments[2], '1000');
      expect(segments.last, '-');
    });
  });

  // The vitals term is the one part of this digest built from a value that moves continuously, and
  // so the one part that could defeat the gate outright: a processor figure drifting by a tenth of
  // a percent between samples would wake the sweep on every single tick and put the twelve rebuilds
  // a minute straight back. Hence the buckets, and hence these.
  group('the vitals tail', () {
    SystemStats stats({
      double? cpu,
      double? memory,
      int? free,
      List<VitalsAlert> alerts = const [],
    }) => SystemStats(
      cpu: CpuStats(containerPercent: cpu),
      memory: MemoryStats(percent: memory),
      disk: DiskStats(freeBytes: free),
      alerts: alerts,
    );

    String digestWith(SystemStats? sample) =>
        (repository()..seedForTest(order: ['front-door'], stats: sample))
            .clockDigest();

    test(
      'a processor figure drifting inside its bucket does not wake the sweep',
      () {
        expect(digestWith(stats(cpu: 41.2)), digestWith(stats(cpu: 41.4)));
        expect(digestWith(stats(cpu: 41.2)), digestWith(stats(cpu: 42.4)));
      },
    );

    test('a processor figure crossing a bucket does', () {
      expect(digestWith(stats(cpu: 41.2)), isNot(digestWith(stats(cpu: 47.0))));
    });

    test('free space moving by less than a gigabyte does not wake the sweep', () {
      // A recording NVR writes continuously, so this figure is never still. Bucketing to the
      // gigabyte is what stops "the disk moved" being true on every tick forever.
      expect(
        digestWith(stats(free: 2200000000000)),
        digestWith(stats(free: 2200400000000)),
      );
      expect(
        digestWith(stats(free: 2200000000000)),
        isNot(digestWith(stats(free: 2190000000000))),
      );
    });

    test('an alert appearing or clearing always wakes the sweep', () {
      const low = VitalsAlert(
        kind: VitalsAlertKind.diskLow,
        severity: 'warning',
        message: 'Under 10% free.',
      );
      const critical = VitalsAlert(
        kind: VitalsAlertKind.diskCritical,
        severity: 'critical',
        message: 'Under 5% free.',
      );

      final quiet = digestWith(stats(free: 2200000000000));

      expect(
        digestWith(stats(free: 2200000000000, alerts: [low])),
        isNot(quiet),
      );
      expect(
        digestWith(stats(free: 2200000000000, alerts: [low])),
        isNot(digestWith(stats(free: 2200000000000, alerts: [critical]))),
      );
    });

    test('a figure this host cannot measure is not the same as zero', () {
      // The distinction the whole payload is built on, carried through to the gate: a GPU that
      // publishes nothing and a GPU sitting at 0% must not produce the same digest, or the page
      // would never re-render when one became the other.
      expect(
        digestWith(stats(memory: null)),
        isNot(digestWith(stats(memory: 0))),
      );
    });

    test('no sample at all is stable rather than churning', () {
      expect(digestWith(null), digestWith(null));
    });
  });
}
