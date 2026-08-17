// What the column says when the read stopped before the bar did.
//
// The feature rests on "what the bar covers, the column lists", and a busy enough day will outrun
// any single read — so the one thing that must not happen is the column trailing off mid-afternoon
// and reading as an evening when nothing happened.
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/widgets/activity_column.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
  final now = DateTime(2026, 8, 5, 18);

  SceneDocument scene(String id, DateTime at) => SceneDocument(
    cameraId: 'cam1',
    source: 'server',
    when: at,
    id: id,
    description: 'something at $id',
    trigger: 'motion',
    motionScore: 1,
    frameSpanSeconds: 2,
  );

  /// [count] records ending at [newest] and running a minute apart backwards.
  List<SceneDocument> run(int count, {required DateTime newest}) => [
    for (var i = 0; i < count; i++)
      scene('s$i', newest.subtract(Duration(minutes: i))),
  ];

  LiveServalRepository seeded({Map<String, DateTime> horizons = const {}}) {
    final auth = AuthController(config: config);
    final repository = LiveServalRepository(auth: auth, config: config)
      ..seedForTest(order: const ['cam1', 'cam2'], horizons: horizons);
    addTearDown(() {
      repository.dispose();
      auth.dispose();
    });
    return repository;
  }

  group('spotting a truncated read', () {
    test('a read that came back short was not truncated', () {
      expect(
        LiveServalRepository.horizonFrom([run(9, newest: now)], limit: 10),
        isNull,
      );
    });

    test('a read filled to the limit stops at its own oldest record', () {
      expect(
        LiveServalRepository.horizonFrom([run(10, newest: now)], limit: 10),
        now.subtract(const Duration(minutes: 9)),
      );
    });

    test('an empty read is not a truncated one', () {
      // Guards the degenerate limit: nothing came back because nothing happened, which is the
      // opposite of running out of depth.
      expect(LiveServalRepository.horizonFrom([const []], limit: 0), isNull);
    });

    test('the shallowest truncated kind sets the horizon', () {
      // The subtlety worth a test. Detections reach back an hour and utterances only ten minutes;
      // the merged feed is whole for ten minutes, not an hour. Taking the earlier instant would
      // claim coverage down to the deepest read while a shallower one had already run dry.
      final deep = run(10, newest: now);
      final shallow = [
        for (var i = 0; i < 10; i++)
          scene('u$i', now.subtract(Duration(seconds: i))),
      ];

      expect(
        LiveServalRepository.horizonFrom([deep, shallow], limit: 10),
        now.subtract(const Duration(seconds: 9)),
        reason: 'the shallower of the two is what the column can promise',
      );
    });

    test('an untruncated kind does not deepen the claim', () {
      expect(
        LiveServalRepository.horizonFrom([
          run(10, newest: now),
          run(3, newest: now),
        ], limit: 10),
        now.subtract(const Duration(minutes: 9)),
      );
    });
  });

  group('whose horizon', () {
    test('a camera answers with its own read', () {
      final repository = seeded(
        horizons: {'cam1': now.subtract(const Duration(hours: 6))},
      );

      expect(
        repository.feedHorizon(cameraId: 'cam1'),
        now.subtract(const Duration(hours: 6)),
      );
      expect(repository.feedHorizon(cameraId: 'cam2'), isNull);
    });

    test('the house answers with the shallowest of them', () {
      // One truncated camera is enough to make the merged column incomplete from that point on.
      final repository = seeded(
        horizons: {
          'cam1': now.subtract(const Duration(hours: 6)),
          'cam2': now.subtract(const Duration(hours: 2)),
        },
      );

      expect(repository.feedHorizon(), now.subtract(const Duration(hours: 2)));
    });

    test('nothing truncated is null, not an instant', () {
      expect(seeded().feedHorizon(), isNull);
    });
  });

  group('when the column mentions it', () {
    // Only where the window on screen actually reaches past the horizon. A busy morning that
    // truncated at noon says nothing while the bar is set to an hour, because over that hour the
    // column is complete and a warning would be about a window nobody is looking at.
    test('a range inside the horizon says nothing', () {
      expect(
        horizonInView(
          TimelineRange.hour,
          DateTime.now().subtract(const Duration(hours: 6)),
        ),
        isNull,
      );
    });

    test('a range reaching past it says so', () {
      final horizon = DateTime.now().subtract(const Duration(hours: 6));

      expect(horizonInView(TimelineRange.day, horizon), horizon);
    });

    test('an untruncated feed says nothing at any range', () {
      expect(horizonInView(TimelineRange.day, null), isNull);
    });

    test('no range at all says nothing', () {
      // A caller with no scrubber behind it has no window to have overrun.
      expect(
        horizonInView(null, DateTime.now().subtract(const Duration(hours: 6))),
        isNull,
      );
    });
  });
}
