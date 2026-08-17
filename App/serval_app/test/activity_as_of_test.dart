import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/timeline.dart';

/// The feed clamped to the playhead, and scoped to the scrubber's range.
///
/// Nothing here reaches the network: the repository is constructed but never started, and the
/// documents go in through the same `seedForTest` seam the overlay suite uses.
DateTime _toSecond(DateTime at) => DateTime.fromMillisecondsSinceEpoch(
  at.millisecondsSinceEpoch - at.millisecond,
  isUtc: at.isUtc,
);

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

  UtteranceDocument utterance(String id, DateTime at) => UtteranceDocument(
    cameraId: 'cam1',
    source: 'server',
    when: at,
    id: id,
    conversationId: null,
    transcript: 'said at $id',
    speaker: null,
    emotion: null,
    audioEvent: null,
    durationSeconds: 1,
  );

  DetectionDocument detection(String id, DateTime at, {bool alert = false}) =>
      DetectionDocument(
        cameraId: 'cam1',
        source: 'server',
        when: at,
        id: id,
        label: alert ? 'person' : 'car',
        peakConfidence: 0.9,
        frameCount: 3,
        isAlert: alert,
        endedAt: at.add(const Duration(seconds: 5)),
      );

  LiveServalRepository seeded(List<TelemetryDocument> documents) {
    final auth = AuthController(config: config);
    final repository = LiveServalRepository(auth: auth, config: config)
      ..seedForTest(order: const ['cam1'], documents: documents);
    addTearDown(() {
      repository.dispose();
      auth.dispose();
    });
    return repository;
  }

  test('a null asOf lists everything, as today', () {
    final repository = seeded([
      scene('a', now.subtract(const Duration(minutes: 30))),
      scene('b', now.subtract(const Duration(minutes: 10))),
    ]);

    expect(repository.activityFor(), hasLength(2));
  });

  test('nothing later than the playhead is listed', () {
    // The whole point. A wall showing half past five must not list what happened at six — that is
    // a spoiler for footage you have not watched yet.
    final repository = seeded([
      scene('a', now.subtract(const Duration(minutes: 30))),
      scene('b', now.subtract(const Duration(minutes: 10))),
      scene('c', now),
    ]);

    final items = repository.activityFor(
      asOf: now.subtract(const Duration(minutes: 20)),
    );

    expect(items, hasLength(1));
    expect(items.single.text, contains('a'));
  });

  test('an event exactly at the playhead is listed', () {
    // It is happening on screen at that instant, which is when it should appear rather than a
    // moment later.
    final at = now.subtract(const Duration(minutes: 10));
    final repository = seeded([scene('a', at)]);

    expect(repository.activityFor(asOf: at), hasLength(1));
  });

  test('rows are dated against the wall clock, not the playhead', () {
    // A four-hour-old event is four hours old whether or not you are watching it. Dated against
    // the playhead it read "now" over a recording of the past, which is a claim about the present
    // the row is not entitled to make.
    final at = _toSecond(DateTime.now().subtract(const Duration(hours: 4)));
    final repository = seeded([scene('a', at)]);

    final replayed = repository.activityFor(asOf: at).single;

    expect(replayed.timeLabel, isNot('now'));
    expect(
      replayed.timeLabel,
      repository.activityFor().single.timeLabel,
      reason: 'the same row reads the same whether or not it is being replayed',
    );
  });

  test('the same question twice returns the identical list', () {
    // The memo. The playhead ticks ten times a second and the column is not a lazy list, so this
    // is what stops a replaying wall rebuilding every row it draws ten times a second.
    final repository = seeded([
      scene('a', now.subtract(const Duration(minutes: 30))),
    ]);

    final first = repository.activityFor(asOf: now);
    final second = repository.activityFor(asOf: now);

    expect(identical(first, second), isTrue);
  });

  test('the memo is keyed on the second, not the microsecond', () {
    final repository = seeded([
      scene('a', now.subtract(const Duration(minutes: 30))),
    ]);

    final first = repository.activityFor(asOf: now);
    final later = repository.activityFor(
      asOf: now.add(const Duration(milliseconds: 300)),
    );

    expect(identical(first, later), isTrue);
  });

  test('a later second is answered afresh', () {
    final repository = seeded([
      scene('a', now.subtract(const Duration(minutes: 30))),
      scene('b', now.add(const Duration(seconds: 2))),
    ]);

    expect(repository.activityFor(asOf: now), hasLength(1));
    expect(
      repository.activityFor(asOf: now.add(const Duration(seconds: 3))),
      hasLength(2),
    );
  });

  test('the feed lags the picture rather than running ahead of it', () {
    // The playhead is quantised down to the second, so an event half a second into the second
    // being watched appears when the next one starts. That direction is deliberate: a feed a
    // fraction of a second late is invisible, and one a fraction early is the spoiler this whole
    // feature exists to prevent.
    final at = now.add(const Duration(milliseconds: 500));
    final repository = seeded([scene('a', at)]);

    expect(
      repository.activityFor(asOf: now.add(const Duration(milliseconds: 900))),
      isEmpty,
    );
    expect(
      repository.activityFor(asOf: now.add(const Duration(seconds: 1))),
      hasLength(1),
    );
  });

  test('the kind filter still applies at a playhead', () {
    // The clamp and the filter are two different jobs now — the repository does the first and
    // hands back a pool, `ActivityFilter` does the second — so this checks they compose rather
    // than that one of them does both.
    final repository = seeded([
      scene('a', now.subtract(const Duration(minutes: 30))),
    ]);

    expect(
      const ActivityFilter(
        kinds: {ActivityKind.speech},
      ).apply(repository.activityFor(asOf: now)),
      isEmpty,
      reason: 'a scene is not speech',
    );
  });

  group('the scrubber range', () {
    // What the bar covers, the column lists. The range button is the one control naming the
    // window, and it names it for the track and the feed both.

    test('a null range lists everything held, as before', () {
      final repository = seeded([
        scene('a', DateTime.now().subtract(const Duration(hours: 8))),
        scene('b', DateTime.now().subtract(const Duration(minutes: 10))),
      ]);

      expect(repository.activityFor(), hasLength(2));
    });

    test('a live range drops what falls before its start', () {
      final wall = DateTime.now();
      final repository = seeded([
        scene('old', wall.subtract(const Duration(hours: 8))),
        scene('new', wall.subtract(const Duration(minutes: 10))),
      ]);

      final items = repository.activityFor(range: TimelineRange.sixHours);

      expect(items, hasLength(1));
      expect(items.single.text, contains('new'));
    });

    test('widening the range brings the older row back', () {
      // The narrowing has to be a reading of the same pool rather than a discard, or stepping the
      // bar back out would come back short and only a reload would fix it.
      final wall = DateTime.now();
      final repository = seeded([
        scene('old', wall.subtract(const Duration(hours: 8))),
        scene('new', wall.subtract(const Duration(minutes: 10))),
      ]);

      expect(
        repository.activityFor(range: TimelineRange.sixHours),
        hasLength(1),
      );
      expect(repository.activityFor(range: TimelineRange.day), hasLength(2));
    });

    test('a chosen period clamps the top edge too', () {
      // A track showing last Tuesday under a column listing tonight is the same disagreement the
      // other way round — so a fixed window drops what happened after it, with no playhead
      // involved at all.
      final wall = DateTime.now();
      final window = TimelineRange.window(
        from: wall.subtract(const Duration(hours: 6)),
        to: wall.subtract(const Duration(hours: 4)),
      );

      final repository = seeded([
        scene('before', wall.subtract(const Duration(hours: 7))),
        scene('inside', wall.subtract(const Duration(hours: 5))),
        scene('after', wall.subtract(const Duration(hours: 1))),
      ]);

      final items = repository.activityFor(range: window);

      expect(items, hasLength(1));
      expect(items.single.text, contains('inside'));
    });

    test('the playhead still wins where it is the earlier top edge', () {
      // The two compose rather than replace each other: the range is the track, and the playhead
      // is where along it the picture has got to.
      final wall = DateTime.now();
      final repository = seeded([
        scene('a', wall.subtract(const Duration(minutes: 50))),
        scene('b', wall.subtract(const Duration(minutes: 10))),
      ]);

      final items = repository.activityFor(
        range: TimelineRange.hour,
        asOf: wall.subtract(const Duration(minutes: 30)),
      );

      expect(items, hasLength(1));
      expect(items.single.text, contains('a'));
    });

    test('two ranges at one playhead are answered apart', () {
      // The memo is keyed on the range as well, or stepping the bar while replay is paused would
      // hand back the previous window's answer.
      final wall = DateTime.now();
      final repository = seeded([
        scene('old', wall.subtract(const Duration(hours: 8))),
        scene('new', wall.subtract(const Duration(minutes: 10))),
      ]);

      final narrow = repository.activityFor(
        asOf: wall,
        range: TimelineRange.sixHours,
      );
      final wide = repository.activityFor(asOf: wall, range: TimelineRange.day);

      expect(narrow, hasLength(1));
      expect(wide, hasLength(2));
    });
  });

  group('what the column is allowed to show', () {
    test('a non-alert detection is not a row', () {
      // The same gate the box and the scrubber tick pass through. A parked car is stored and
      // queryable and is not a claim that anyone should look; a column reporting every one of them
      // buries the rows that are.
      final repository = seeded([
        detection('car', now.subtract(const Duration(minutes: 5))),
        detection(
          'person',
          now.subtract(const Duration(minutes: 4)),
          alert: true,
        ),
      ]);

      final items = repository.activityFor();

      expect(items, hasLength(1));
      expect(items.single.label, 'person');
    });

    test('a screen asking to see everything puts them back', () {
      final repository = seeded([
        detection('car', now.subtract(const Duration(minutes: 5))),
      ]);

      expect(repository.activityFor(includeAllDetections: true), hasLength(1));
    });

    test('and puts them back only for the read that asked', () {
      // The wall reads the same repository without the flag, so a camera screen widening its own
      // column cannot add rows to a screen that has no such control. Both directions from one
      // seeding, because what has to hold is that the two answers differ at the same instant.
      final repository = seeded([
        detection('car', now.subtract(const Duration(minutes: 5))),
      ]);

      expect(repository.activityFor(includeAllDetections: true), hasLength(1));
      expect(repository.activityFor(), isEmpty);
    });
  });

  group('what the feed holds', () {
    test('a flood of detections does not evict speech', () {
      // The regression. A cap counted in documents let a gate that can see a road push every
      // utterance out within the hour, so the column showed a few minutes of speech under a bar
      // claiming a day and nothing on screen said so.
      final wall = DateTime.now();
      final repository = seeded([
        utterance('said', wall.subtract(const Duration(hours: 3))),
        for (var i = 0; i < 800; i++)
          detection('d$i', wall.subtract(Duration(seconds: i)), alert: true),
      ])..trimFeedForTest();

      expect(
        repository
            .activityFor(range: TimelineRange.sixHours)
            .map((i) => i.text),
        contains(contains('said')),
      );
    });

    test('what no range can reach is dropped', () {
      // The one thing retention is for. `maxSpan` is the widest window the range button offers, so
      // anything older cannot appear on any bar or in any column.
      final wall = DateTime.now();
      final repository = seeded([
        scene('stale', wall.subtract(const Duration(hours: 30))),
        scene('held', wall.subtract(const Duration(hours: 20))),
      ])..trimFeedForTest();

      final items = repository.activityFor();

      expect(items, hasLength(1));
      expect(items.single.text, contains('held'));
    });
  });
}
