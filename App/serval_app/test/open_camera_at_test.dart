import 'package:flutter/material.dart' show MaterialApp, Scaffold;
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/widgets/activity_column.dart';

/// Clicking a row in the feed.
///
/// Three questions, and they are separate: whether the row hands over an instant at all, whether
/// clicking it is offered in the first place, and — once it has — whether the screen it opens can
/// reach that instant on its track.
void main() {
  ActivityItem row({
    required DateTime at,
    bool alert = true,
    String cameraId = 'front-door',
  }) => ActivityItem(
    id: 'row',
    kind: TelemetryKind.detection,
    cameraId: cameraId,
    cameraName: 'Front door',
    at: at,
    timeLabel: 'whenever',
    text: 'Person at the front door',
    icon: ActivityIcon.person,
    isAlert: alert,
    // Deliberately not the flag being tested. *Right now* is a five-minute heading and the live
    // edge is thirty seconds, so a row can be filed under one and still have footage.
    isRecent: false,
  );

  Camera camera(String id, {required bool records}) => Camera(
    id: id,
    name: 'Front door',
    records: records,
    placeholder: TilePlaceholder.forCameraId(id),
  );

  Future<({String? camera, DateTime? at, bool fired})> tapRow(
    WidgetTester tester,
    ActivityItem item, {
    List<Camera> cameras = const [],
  }) async {
    String? opened;
    DateTime? at;
    var fired = false;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ActivityColumn(
            items: [item],
            cameras: cameras,
            filter: ActivityFilter.none,
            facets: ActivityFacets.of([item]),
            onOpenCamera: (id, instant) {
              opened = id;
              at = instant;
              fired = true;
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Person at the front door'));
    await tester.pumpAndSettle();

    return (camera: opened, at: at, fired: fired);
  }

  testWidgets('an older row hands over the instant, backed off by the lead-in', (
    tester,
  ) async {
    // The whole point. Opening a camera off a ten-minute-old detection and landing on the live
    // view shows you an empty driveway and no way to tell that from a broken feature.
    final at = DateTime.now().subtract(const Duration(minutes: 10));

    final opened = await tapRow(tester, row(at: at));

    expect(opened.fired, isTrue);
    expect(opened.camera, 'front-door');
    expect(opened.at, at.subtract(feedLeadIn));
  });

  testWidgets('a plain row opens the same way an alert does', (tester) async {
    // A sound, a sentence and a scene description all name a moment. If only alerts led anywhere,
    // everything else would be a note about footage you had to go and find.
    final at = DateTime.now().subtract(const Duration(minutes: 10));

    final opened = await tapRow(tester, row(at: at, alert: false));

    expect(opened.fired, isTrue);
    expect(opened.at, at.subtract(feedLeadIn));
  });

  testWidgets('a row at the live edge opens live', (tester) async {
    // Nothing to replay: what it describes is on the live view already, and seeking a few seconds
    // back would open a window against footage that has not been written yet.
    final opened = await tapRow(tester, row(at: DateTime.now()));

    expect(opened.fired, isTrue);
    expect(opened.at, isNull);
  });

  testWidgets('a camera that keeps nothing has inert rows', (tester) async {
    // The row still says what happened — that is what the feed is for — but there is no recording
    // behind it, so there is nowhere for the click to go. Failing silently at the far end would be
    // worse than not offering it.
    final opened = await tapRow(
      tester,
      row(at: DateTime.now().subtract(const Duration(minutes: 10))),
      cameras: [camera('front-door', records: false)],
    );

    expect(opened.fired, isFalse);
  });

  testWidgets('a recording camera in the registry still opens', (tester) async {
    final at = DateTime.now().subtract(const Duration(minutes: 10));

    final opened = await tapRow(
      tester,
      row(at: at),
      cameras: [camera('front-door', records: true)],
    );

    expect(opened.fired, isTrue);
    expect(opened.at, at.subtract(feedLeadIn));
  });

  group('where a row starts playback', () {
    final now = DateTime.now();

    test('older than the live edge backs off by the lead-in', () {
      final at = now.subtract(const Duration(minutes: 4));
      expect(replayStartFor(at, now: now), at.subtract(feedLeadIn));
    });

    test('inside the live edge is the live view', () {
      expect(
        replayStartFor(now.subtract(const Duration(seconds: 29)), now: now),
        isNull,
      );
      expect(replayStartFor(now, now: now), isNull);
    });

    test('the edge itself has footage', () {
      // Exactly thirty seconds is outside the window a seek would hand back to live, so it is
      // replayable — and the lead-in puts the target well clear of it.
      final at = now.subtract(feedLiveEdge);
      expect(replayStartFor(at, now: now), at.subtract(feedLeadIn));
    });
  });

  group('the range the camera opens on', () {
    // A track that does not reach the instant asked for cannot be seeked to it, and the screen
    // defaults to an hour. These pin what a row opens instead: the moment at the left, and the
    // half hour after it to play through.
    final now = DateTime.now();

    test('puts the moment at the left, a minute in', () {
      final at = now.subtract(const Duration(hours: 2));
      final range = rangeForOpenAt(at, now: now);

      expect(range.startAt(now), at.subtract(arrivalLeadIn));
      expect(range.duration, arrivalSpan);
    });

    test('reaches no further than the span past it', () {
      final at = now.subtract(const Duration(hours: 2));
      final range = rangeForOpenAt(at, now: now);

      expect(range.live, isFalse);
      expect(range.endingAt, at.add(arrivalSpan - arrivalLeadIn));
    });

    test('anything older opens the same window, not a wider one', () {
      // A day of track is about two seconds a pixel, and what is wanted here is the moment rather
      // than the day it happened on.
      final at = now.subtract(const Duration(days: 3));
      final range = rangeForOpenAt(at, now: now);

      expect(range.startAt(now), at.subtract(arrivalLeadIn));
      expect(range.endingAt, at.add(arrivalSpan - arrivalLeadIn));
    });

    test('a moment whose half hour has not happened yet grows into it', () {
      // Drawn out to where thirty minutes *will* be, the right half of the bar is empty future
      // that never fills, because a window with a fixed right edge is never refetched.
      final at = now.subtract(const Duration(minutes: 10));
      final range = rangeForOpenAt(at, now: now);

      expect(range.live, isTrue);
      expect(range.startAt(now), at.subtract(arrivalLeadIn));
      expect(range.endAt(now), now);
      expect(
        range.startAt(now.add(const Duration(hours: 1))),
        now.add(const Duration(hours: 1)).subtract(arrivalSpan),
      );
    });

    test('nothing to replay stays on the default', () {
      // Inside the live edge the screen opens live — see [replayStartFor] — and a bar a minute
      // wide under a live picture is a worse answer than the hour it would have had.
      expect(
        rangeForOpenAt(now.subtract(const Duration(seconds: 10)), now: now),
        TimelineRange.hour,
      );
      expect(rangeForOpenAt(null, now: now), TimelineRange.hour);
    });
  });
}
