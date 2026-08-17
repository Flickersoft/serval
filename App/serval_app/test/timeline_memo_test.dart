import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/timeline.dart';

/// The derived reads, and the memos underneath them.
///
/// `timelineFor` and `activityFor` are both called from `build`, and both walk every document the
/// repository holds to answer a question whose answer does not change until something moves. They
/// are memoised on a revision counter — which is worth pinning, because the failure mode of getting
/// it wrong is not a slow screen but a frozen one: a feed that never shows what just arrived.
///
/// Nothing here reaches the network. The repository is constructed but never started, so the
/// self-priming fetch `timelineFor` kicks off is left suspended on a socket that is never answered
/// — which is exactly the state a cold screen is in, and the one the caching has to be right about.
void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
  final now = DateTime.now();

  SceneDocument scene(String id, DateTime at, {String camera = 'cam1'}) =>
      SceneDocument(
        cameraId: camera,
        source: 'server',
        when: at,
        id: id,
        description: 'something at $id',
        trigger: 'motion',
        motionScore: 1,
        frameSpanSeconds: 2,
      );

  LiveServalRepository seeded(List<TelemetryDocument> documents) {
    final auth = AuthController(config: config);
    final repository = LiveServalRepository(auth: auth, config: config)
      ..seedForTest(order: const ['cam1', 'cam2'], documents: documents);
    addTearDown(() {
      repository.dispose();
      auth.dispose();
    });
    return repository;
  }

  group('the activity memo', () {
    test('answers the same question with the same list', () {
      // The win the memo is for. Without it the whole merge — pool assembly, three filter passes,
      // the conversation index, an item per document — runs again on every build of a live screen.
      final repository = seeded([
        scene('a', now.subtract(const Duration(minutes: 20))),
        scene('b', now.subtract(const Duration(minutes: 5))),
      ]);

      final first = repository.activityFor(range: TimelineRange.hour);
      final second = repository.activityFor(range: TimelineRange.hour);

      expect(identical(first, second), isTrue);
    });

    test('a live screen still sees what just arrived', () {
      // The failure this guards against, and the reason the memo is keyed on a revision rather
      // than on the clock alone: a live read has no playhead to key on, so a memo that ignored
      // arriving documents would leave the column frozen until the minute rolled over.
      final repository = seeded([
        scene('a', now.subtract(const Duration(minutes: 20))),
      ]);

      final before = repository.activityFor(range: TimelineRange.hour);
      expect(before, hasLength(1));

      repository.seedForTest(
        order: const ['cam1', 'cam2'],
        documents: [
          scene('a', now.subtract(const Duration(minutes: 20))),
          scene('b', now.subtract(const Duration(minutes: 1))),
        ],
      );

      expect(repository.activityFor(range: TimelineRange.hour), hasLength(2));
    });

    test('two screens asking different questions do not evict each other', () {
      // A wall and a single-camera view read the same repository with different `cameraId`s. Held
      // in one slot they thrashed, so neither ever hit and the memo bought nothing on the screen
      // where both are open.
      final repository = seeded([
        scene('a', now.subtract(const Duration(minutes: 20))),
        scene('b', now.subtract(const Duration(minutes: 10)), camera: 'cam2'),
      ]);

      final wall = repository.activityFor(range: TimelineRange.hour);
      final camera = repository.activityFor(
        cameraId: 'cam1',
        range: TimelineRange.hour,
      );

      expect(
        identical(repository.activityFor(range: TimelineRange.hour), wall),
        isTrue,
      );
      expect(
        identical(
          repository.activityFor(cameraId: 'cam1', range: TimelineRange.hour),
          camera,
        ),
        isTrue,
      );

      // And they are still different answers, not one served for both.
      expect(wall, hasLength(2));
      expect(camera, hasLength(1));
    });
  });

  group('the timeline window', () {
    test('is the same object until something moves', () {
      // What lets the scrubber memoise its own layers: it compares windows by identity, so a
      // repository handing back a fresh object per build would defeat it however little changed.
      //
      // Deliberately no `await` between the reads — the fetch kicked off by the first is still
      // suspended, which is the state a cold screen rebuilds in.
      final repository = seeded([
        scene('a', now.subtract(const Duration(minutes: 20))),
      ]);

      repository.timelineFor('cam1', TimelineRange.hour);
      final first = repository.timelineFor('cam1', TimelineRange.hour);
      final second = repository.timelineFor('cam1', TimelineRange.hour);

      expect(identical(first, second), isTrue);
    });

    test('is rebuilt when a document arrives', () {
      final repository = seeded([
        scene('a', now.subtract(const Duration(minutes: 20))),
      ]);

      repository.timelineFor('cam1', TimelineRange.hour);
      final before = repository.timelineFor('cam1', TimelineRange.hour);

      repository.seedForTest(
        order: const ['cam1', 'cam2'],
        documents: [scene('a', now.subtract(const Duration(minutes: 20)))],
      );

      expect(
        identical(repository.timelineFor('cam1', TimelineRange.hour), before),
        isFalse,
      );
    });
  });

  group('the bar and the feed', () {
    test('are scoped to the same window', () {
      // "The feed follows the bar" as a property rather than a convention. Both read one anchor per
      // range, so a column cannot list an event the track beside it has no room to draw.
      final repository = seeded([
        scene('a', now.subtract(const Duration(minutes: 20))),
      ]);

      final window = repository.timelineFor('cam1', TimelineRange.hour);
      final items = repository.activityFor(range: TimelineRange.hour);

      expect(items, isNotEmpty);
      for (final item in items) {
        expect(item.at.isBefore(window.from), isFalse);
        expect(item.at.isAfter(window.to), isFalse);
      }
    });

    test('agree on where the window starts', () {
      // The drift this closes: the track anchored its edges once at fetch time while the column
      // recomputed its own against the wall clock on every read, so the two disagreed by up to the
      // refresh interval — the column listing rows off the left of the bar, and the bar drawing
      // marks the column had already dropped.
      final repository = seeded(const []);

      final window = repository.timelineFor('cam1', TimelineRange.hour);
      final held = repository.timelineFor('cam1', TimelineRange.hour);

      // The anchor is fixed on the first read and reused, rather than sliding with the clock.
      expect(held.from, window.from);
      expect(held.to, window.to);
      expect(window.to.difference(window.from), TimelineRange.hour.duration);
    });
  });
}
