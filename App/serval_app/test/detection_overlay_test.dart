// Which detections the UI is willing to draw.
//
// The detector stores every class it knows about — eight by default, including cars and trucks —
// but only an alert is a claim that someone should look. So the rule the whole overlay hangs on
// is that a box and an orange tick appear for an alert and for nothing else, and it has to hold
// in both directions: live asks what is there now, replay asks what was there at an instant, and
// either can disagree with the scrubber underneath them — a car drawing a full alert-orange box
// over the video while the same episode puts a calm tick on the track.
//
// `includeAllDetections` is the one way past that rule, and the tests for it are here rather than
// in a file of their own because what has to hold is a property *of this rule*: it widens what is
// drawn for the screen that asked, and it does not turn anything into an alert.
import 'dart:ui' show Canvas, Paint, Path, PictureRecorder;

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/theme/nocturne.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/ptz_pad.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:5211'));

  // Constructed but never started, as in `clock_digest_test.dart`: `start()` is what opens the
  // sockets, and the feed is seeded directly.
  LiveServalRepository repository(List<TelemetryDocument> documents) =>
      LiveServalRepository(
        auth: AuthController(config: config),
        config: config,
      )..seedForTest(
        order: const ['driveway', 'back-yard'],
        documents: documents,
      );

  DateTime at(int second) =>
      DateTime.parse('2026-08-04T12:00:${second.toString().padLeft(2, '0')}Z');

  /// One object, present from [start] and still there, with a track that puts it somewhere at
  /// every instant this file asks about.
  ///
  /// One box, because an episode is one object. Several people in shot is several of these.
  ///
  /// [start] defaults to the file's fixed clock, which is what the replay tests want: they name
  /// the instant they are asking about, so a frozen episode is the readable fixture. The live
  /// tests pass a recent one instead — see [seen] — because the live overlay is bounded by how
  /// long ago an episode was last measured, and an episode dated 2026-08-04 is a claim nothing
  /// can vouch for by the time the suite runs.
  DetectionDocument episode({
    required String id,
    required String label,
    required bool isAlert,
    double x = 0.1,
    double score = 0.93,
    String cameraId = 'driveway',
    DateTime? start,
    DateTime? endedAt,
  }) {
    final geometry = {
      'x': x,
      'y': 0.2,
      'width': 0.2,
      'height': 0.4,
      'score': score,
    };

    final from = (start ?? DateTime.parse('2026-08-04T12:00:00Z')).toUtc();

    return parseTelemetryDocument('detection', {
          'type': 'detection',
          'schema_version': 7,
          'id': id,
          'camera_id': cameraId,
          'timestamp': from.toIso8601String(),
          'ended_at': endedAt?.toUtc().toIso8601String(),
          'label': label,
          'peak_confidence': score,
          'peak_frame_at': from
              .add(const Duration(seconds: 5))
              .toIso8601String(),
          'frame_count': 20,
          'best_box': geometry,
          'track': [
            {'at': from.toIso8601String(), 'box': geometry},
          ],
          'is_alert': isAlert,
          'source': 'server',
        })!
        as DetectionDocument;
  }

  /// The same episode, last measured [secondsAgo] ago rather than in 2026.
  ///
  /// What the live overlay is actually handed: the Server re-publishes an open episode every
  /// detection frame, so anything really in front of a camera has evidence seconds old.
  DetectionDocument seen({
    required String id,
    required String label,
    required bool isAlert,
    double x = 0.1,
    double score = 0.93,
    String cameraId = 'driveway',
    int secondsAgo = 1,
  }) => episode(
    id: id,
    label: label,
    isAlert: isAlert,
    x: x,
    score: score,
    cameraId: cameraId,
    start: DateTime.now().toUtc().subtract(Duration(seconds: secondsAgo)),
  );

  final person = episode(id: 'det-person', label: 'person', isAlert: true);
  final car = episode(id: 'det-car', label: 'car', isAlert: false);

  final personNow = seen(id: 'det-person', label: 'person', isAlert: true);
  final carNow = seen(id: 'det-car', label: 'car', isAlert: false);

  // Three people in shot: three records, each with its own id, its own place and the score it in
  // particular was seen at.
  final crowd = [
    for (var i = 0; i < 3; i++)
      episode(
        id: 'det-person-$i',
        label: 'person',
        isAlert: true,
        x: 0.1 + (0.3 * i),
        score: 0.93 - (0.1 * i),
      ),
  ];

  final crowdNow = [
    for (var i = 0; i < 3; i++)
      seen(
        id: 'det-person-$i',
        label: 'person',
        isAlert: true,
        x: 0.1 + (0.3 * i),
        score: 0.93 - (0.1 * i),
      ),
  ];

  group('the live overlay', () {
    test('draws an alert that is still in view', () {
      final boxes = repository([personNow]).detectionsFor('driveway');

      expect(boxes, hasLength(1));
      expect(boxes.single.label, 'person');
    });

    test('draws nothing for a parked car, which is stored all the same', () {
      // The episode is ongoing and carries geometry, so every reason to draw it except the one
      // that matters is present.
      expect(carNow.isOngoing, isTrue);
      expect(carNow.overlays, isNotEmpty);

      expect(repository([carNow]).detectionsFor('driveway'), isEmpty);
    });

    test('draws only the alert when both are in view at once', () {
      final boxes = repository([personNow, carNow]).detectionsFor('driveway');

      expect(boxes.map((box) => box.label), ['person']);
    });

    test('draws a box on each of three people, each with its own score', () {
      // Three records rather than one carrying three boxes. The overlay pools them, so what is
      // drawn looks the same — but each box now comes from a record with its own start and its
      // own duration, and the caption's confidence is the one that object was actually seen at.
      final boxes = repository(crowdNow).detectionsFor('driveway');

      expect(boxes, hasLength(3));
      expect(boxes.map((box) => box.rect.left), [
        closeTo(0.1, 1e-9),
        closeTo(0.4, 1e-9),
        closeTo(0.7, 1e-9),
      ]);
      expect(boxes.map((box) => box.caption), [
        'PERSON · 0.93',
        'PERSON · 0.83',
        'PERSON · 0.73',
      ]);
    });
  });

  // The regression that started this: with the overlay widened over a driveway, boxes piled up
  // across a session rather than showing what was in front of the camera.
  //
  // Nothing was stale in storage — the cause was on the wire. An episode closes by one `ended_at`
  // message and nothing repeats it, and the Server's broadcast queue used to carry those in the
  // same drop-oldest lane as the position heartbeats that fill it. Evicting from the head reached
  // the close first, because the close was published before everything piled up behind it. The
  // App kept the episode open forever after that, and the live overlay had no upper bound on how
  // long an open episode may keep drawing.
  //
  // The queue is fixed at the source. This is the second half: the overlay refuses to vouch for
  // an episode nothing has measured lately, whatever the wire did.
  group('an open episode nothing has measured lately', () {
    test('stops drawing on live once its evidence has aged out', () {
      final stranded = seen(
        id: 'det-car-stranded',
        label: 'car',
        isAlert: false,
        secondsAgo: 60,
      );

      // Every other reason to draw it is intact: still open, still carrying geometry, and the
      // screen has explicitly asked to see cars.
      expect(stranded.isOngoing, isTrue);
      expect(stranded.overlays, isNotEmpty);

      expect(
        repository([
          stranded,
        ]).detectionsFor('driveway', includeAllDetections: true),
        isEmpty,
      );
    });

    test('an alert left open is dropped too, not just a widened box', () {
      // The bound is about evidence, not severity. A person episode whose close was lost would
      // otherwise paint an orange box over an empty driveway for the rest of the session.
      final stranded = seen(
        id: 'det-person-stranded',
        label: 'person',
        isAlert: true,
        secondsAgo: 60,
      );

      expect(repository([stranded]).detectionsFor('driveway'), isEmpty);
    });

    test(
      'something really there keeps drawing, because it keeps being re-sent',
      () {
        // The reason the bound is safe: the Server re-publishes an open episode every detection
        // frame, so a parked car that is genuinely parked carries evidence a second old and its
        // bound moves forward with it. Only a lost close leaves one behind.
        expect(
          repository([
            seen(id: 'det-car-parked', label: 'car', isAlert: false),
          ]).detectionsFor('driveway', includeAllDetections: true),
          hasLength(1),
        );
      },
    );

    test('the grace matches what replay already allowed itself', () {
      // One rule, two overlays. `overlaysAt` has bounded replay by `coversUntil` since the
      // four-days-of-boxes incident; live now reads the same property rather than a second
      // figure that could drift away from it.
      final stranded = seen(
        id: 'det-car-stranded',
        label: 'car',
        isAlert: false,
        secondsAgo: 20,
      );

      expect(
        stranded.coversUntil.isAfter(DateTime.now()),
        isTrue,
        reason: '20s ago plus a 30s grace is still ahead of now',
      );
      expect(
        repository([
          stranded,
        ]).detectionsFor('driveway', includeAllDetections: true),
        hasLength(1),
      );
    });

    test('a heartbeat arriving after the close cannot reopen the episode', () {
      // The hazard the Server's lane split introduces, guarded here. State and positions travel
      // separately now, so a close can overtake a heartbeat published before it — and the feed is
      // keyed by episode id, last write wins. Without this the straggler would put the episode
      // back to open and pin its box to the picture, which is the very failure the split was made
      // to end.
      final subject = repository(const []);

      final open = seen(id: 'det-car', label: 'car', isAlert: false);
      final closed = episode(
        id: 'det-car',
        label: 'car',
        isAlert: false,
        start: DateTime.now().toUtc().subtract(const Duration(seconds: 5)),
        endedAt: DateTime.now().toUtc(),
      );

      subject
        ..receiveForTest(open)
        ..receiveForTest(closed)
        // The straggler: published before the close, delivered after it.
        ..receiveForTest(open);

      expect(
        subject.detectionsFor('driveway', includeAllDetections: true),
        isEmpty,
      );
    });
  });

  group('the replay overlay', () {
    test('draws an alert where its track puts it', () {
      final boxes = repository([person]).detectionsAt('driveway', at(3));

      expect(boxes, hasLength(1));
      expect(boxes.single.rect.left, closeTo(0.1, 1e-9));
    });

    test('draws nothing for a car, though its track covers the instant', () {
      // `overlaysAt` would happily answer here — the filter is the repository's, not the
      // document's, so this is the assertion that the two overlays share one rule.
      expect(car.overlaysAt(at(3)), isNotEmpty);

      expect(repository([car]).detectionsAt('driveway', at(3)), isEmpty);
    });

    test('draws each of them where its own track sample puts it', () {
      final boxes = repository(crowd).detectionsAt('driveway', at(3));

      expect(boxes, hasLength(3));
      expect(boxes.last.rect.left, closeTo(0.7, 1e-9));
    });
  });

  group('showing every detection', () {
    test('draws the car the ordinary view refuses', () {
      final boxes = repository([
        carNow,
      ]).detectionsFor('driveway', includeAllDetections: true);

      expect(boxes.map((box) => box.label), ['car']);
    });

    test('draws it in replay too, so the two overlays still agree', () {
      final boxes = repository([
        car,
      ]).detectionsAt('driveway', at(3), includeAllDetections: true);

      expect(boxes.map((box) => box.label), ['car']);
    });

    test('marks the car as no alert, which is what keeps it out of orange', () {
      // The whole point of the toggle: it widens what is *drawn*, and changes nothing about what
      // is claimed to matter. `isAlert` is what picks the box's colour and the scrubber mark's
      // kind, so a car admitted this way must still carry false.
      final boxes = repository([
        carNow,
      ]).detectionsFor('driveway', includeAllDetections: true);

      expect(boxes.single.isAlert, isFalse);
    });

    test('leaves an alert an alert', () {
      final boxes = repository([
        personNow,
        carNow,
      ]).detectionsFor('driveway', includeAllDetections: true);

      expect(
        {for (final box in boxes) box.label: box.isAlert},
        {'person': true, 'car': false},
      );
    });

    test('is asked for per read, so one screen cannot widen another', () {
      // The property the wall depends on. It has no such control and never passes the flag, so
      // the same repository, at the same instant, answers it with alerts only.
      final subject = repository([carNow]);

      expect(
        subject.detectionsFor('driveway', includeAllDetections: true),
        hasLength(1),
      );
      expect(subject.detectionsFor('driveway'), isEmpty);
    });

    test(
      'is off by default, so a caller that says nothing gets the ordinary view',
      () {
        expect(repository([carNow]).detectionsFor('driveway'), isEmpty);
      },
    );
  });

  group('an episode that never closed', () {
    // The failure this guards, seen on the live server: eight episodes left open by a retired
    // build were returned by every replay window — the Server's detections query matches
    // `ended_at == null` against any range after the start — and painted boxes over four days of
    // footage, with no tick on the scrubber, because a null end read as "present from here on".
    test('stops covering time a grace period after its last sample', () {
      // The track's only sample is at 12:00:00, so the episode covers to 12:00:30. Compared as a
      // moment rather than with `==`, which in Dart also insists the two agree on being UTC.
      expect(car.coversUntil.isAtSameMomentAs(at(30)), isTrue);

      expect(car.overlaysAt(at(29)), isNotEmpty);
      expect(car.overlaysAt(at(30)), isNotEmpty);
      expect(car.overlaysAt(at(31)), isEmpty);
    });

    test('draws nothing days later, however open it still is', () {
      expect(car.isOngoing, isTrue);
      expect(car.overlaysAt(DateTime.parse('2026-08-08T13:36:31Z')), isEmpty);
    });

    test(
      'a closed episode is still bounded by its own end, not by the grace',
      () {
        final closed =
            parseTelemetryDocument('detection', {
                  'type': 'detection',
                  'schema_version': 7,
                  'id': 'det-closed',
                  'camera_id': 'driveway',
                  'timestamp': '2026-08-04T12:00:00Z',
                  'ended_at': '2026-08-04T12:00:10Z',
                  'label': 'car',
                  'peak_confidence': 0.5,
                  'peak_frame_at': '2026-08-04T12:00:05Z',
                  'frame_count': 10,
                  'best_box': {
                    'x': 0.1,
                    'y': 0.2,
                    'width': 0.2,
                    'height': 0.4,
                    'score': 0.5,
                  },
                  'track': [
                    {
                      'at': '2026-08-04T12:00:00Z',
                      'box': {
                        'x': 0.1,
                        'y': 0.2,
                        'width': 0.2,
                        'height': 0.4,
                        'score': 0.5,
                      },
                    },
                  ],
                  'is_alert': false,
                  'source': 'server',
                })!
                as DetectionDocument;

        // The grace is for episodes with no end at all. One that ended stops when it said it did,
        // even though its last sample is 10s older than that.
        expect(closed.coversUntil.isAtSameMomentAs(at(10)), isTrue);
        expect(closed.overlaysAt(at(10)), isNotEmpty);
        expect(closed.overlaysAt(at(11)), isEmpty);
      },
    );

    test('the replay overlay drops it too, not just the document', () {
      // The repository is the layer that actually paints, so the bound has to survive the trip
      // through it — this is the assertion that matches what was on screen.
      final subject = repository([car]);

      expect(
        subject.detectionsAt('driveway', at(3), includeAllDetections: true),
        isNotEmpty,
      );
      expect(
        subject.detectionsAt(
          'driveway',
          DateTime.parse('2026-08-08T13:36:31Z'),
          includeAllDetections: true,
        ),
        isEmpty,
      );
    });
  });

  group('a box with no measurement behind it', () {
    /// An episode seen at :00 and :10, absent between them, still open. The gap is the case:
    /// the object has not left, and nothing was measured where it was.
    DetectionDocument gapped({String? endedAt}) {
      Map<String, Object?> box(double x) => {
        'x': x,
        'y': 0.2,
        'width': 0.2,
        'height': 0.4,
        'score': 0.8,
      };

      return parseTelemetryDocument('detection', {
            'type': 'detection',
            'schema_version': 7,
            'id': 'det-gapped',
            'camera_id': 'driveway',
            'timestamp': '2026-08-04T12:00:00Z',
            'ended_at': endedAt,
            'label': 'person',
            'peak_confidence': 0.8,
            'peak_frame_at': '2026-08-04T12:00:00Z',
            'frame_count': 4,
            'best_box': box(0.1),
            'track': [
              {'at': '2026-08-04T12:00:00Z', 'box': box(0.1)},
              {'at': '2026-08-04T12:00:02Z', 'box': null},
              {'at': '2026-08-04T12:00:10Z', 'box': box(0.5)},
            ],
            'is_alert': true,
            'source': 'server',
          })!
          as DetectionDocument;
    }

    test('a measured instant is solid and keeps its score', () {
      final drawn = gapped().overlaysAt(at(1)).single;

      expect(drawn.isStale, isFalse);
      expect(drawn.rect.left, closeTo(0.1, 1e-9));
      expect(drawn.caption, 'PERSON · 0.80');
    });

    test('a gap holds the last position, dotted and without a score', () {
      final drawn = gapped().overlaysAt(at(5)).single;

      expect(drawn.isStale, isTrue);
      expect(drawn.rect.left, closeTo(0.1, 1e-9));
      expect(drawn.caption, 'PERSON');
    });

    test('the next sighting is solid again, at the new place', () {
      final drawn = gapped().overlaysAt(at(11)).single;

      expect(drawn.isStale, isFalse);
      expect(drawn.rect.left, closeTo(0.5, 1e-9));
    });

    test('a gap inside a closed episode is drawn the same way', () {
      // At that instant the episode was open, so the reasoning is identical — nothing about it
      // depends on whether the object has left by the time you scrub back to look.
      final drawn = gapped(
        endedAt: '2026-08-04T12:00:20Z',
      ).overlaysAt(at(5)).single;

      expect(drawn.isStale, isTrue);
    });

    test('a stale box keeps the colour its episode earned', () {
      // The outline says how sure we are of the position; the colour says whether the thing in it
      // matters. Collapsing the two would make every gap look like a downgrade.
      expect(gapped().overlaysAt(at(5)).single.isAlert, isTrue);
    });

    test('the live snapshot of an unseen episode is stale', () {
      // What the Server sends between CoastSeconds and AbsenceSeconds: the last position known,
      // and a sample with no box saying it was not seen this frame.
      //
      // Dated to a second ago rather than to the file's fixed clock, because this one goes through
      // the live overlay — which now refuses an episode nothing has measured lately. A gap is a
      // frame the object was looked for and not found, which is still evidence and still recent;
      // an abandoned episode is neither, and the two must not be answered the same way.
      final now = DateTime.now().toUtc();
      final unseen =
          parseTelemetryDocument('detection', {
                'type': 'detection',
                'schema_version': 7,
                'id': 'det-unseen',
                'camera_id': 'driveway',
                'timestamp': now
                    .subtract(const Duration(seconds: 5))
                    .toIso8601String(),
                'ended_at': null,
                'label': 'person',
                'peak_confidence': 0.8,
                'peak_frame_at': now
                    .subtract(const Duration(seconds: 5))
                    .toIso8601String(),
                'frame_count': 4,
                'best_box': {
                  'x': 0.1,
                  'y': 0.2,
                  'width': 0.2,
                  'height': 0.4,
                  'score': 0.8,
                },
                'track': [
                  {
                    'at': now
                        .subtract(const Duration(seconds: 1))
                        .toIso8601String(),
                    'box': null,
                  },
                ],
                'is_alert': true,
                'source': 'server',
              })!
              as DetectionDocument;

      final drawn = unseen.overlays.single;

      expect(drawn.isStale, isTrue);
      expect(drawn.caption, 'PERSON');
      expect(repository([unseen]).detectionsFor('driveway'), hasLength(1));
    });

    test('a live snapshot that was seen is solid', () {
      expect(person.overlays.single.isStale, isFalse);
    });
  });

  group('the colour a box is drawn in', () {
    Future<void> pump(
      WidgetTester tester, {
      required bool isAlert,
      bool isStale = false,
    }) => tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: SizedBox(
          width: 640,
          height: 360,
          child: DetectionOverlay(
            label: 'CAR · 0.42',
            rect: const Rect.fromLTWH(0.1, 0.2, 0.2, 0.4),
            isAlert: isAlert,
            isStale: isStale,
          ),
        ),
      ),
    );

    /// The solid outline, if there is one. The label chip is a `Container` and contributes a
    /// `DecoratedBox` of its own, so the box is the one carrying a border rather than the first.
    Border? solidBorder(WidgetTester tester) {
      for (final element in find.byType(DecoratedBox).evaluate()) {
        final decoration = (element.widget as DecoratedBox).decoration;
        if (decoration is BoxDecoration && decoration.border is Border) {
          return decoration.border! as Border;
        }
      }
      return null;
    }

    CustomPainter? dashedPainter(WidgetTester tester) {
      for (final element in find.byType(CustomPaint).evaluate()) {
        final painter = (element.widget as CustomPaint).painter;
        if (painter != null && painter.runtimeType.toString() == '_DashedBox') {
          return painter;
        }
      }
      return null;
    }

    /// The colour of whichever outline was drawn. Compared as packed ARGB: `Paint` stores its
    /// colour as four float32s, so the value read back is a hair off the `Color` that went in and
    /// never equals it.
    int borderOf(WidgetTester tester) {
      if (dashedPainter(tester) case final painter?) {
        return _strokeColour(painter).toARGB32();
      }
      return solidBorder(tester)!.top.color.toARGB32();
    }

    testWidgets('an alert is orange', (tester) async {
      await pump(tester, isAlert: true);
      expect(borderOf(tester), Serval.alert.toARGB32());
    });

    testWidgets('anything else is the accent, never orange', (tester) async {
      // The rule this protects: orange is a claim that someone should look. A camera showing
      // every detection draws a great many things that are not that, and painting them the same
      // colour would spend the meaning of the one that is.
      await pump(tester, isAlert: false);

      expect(borderOf(tester), Nocturne.accent.toARGB32());
      expect(borderOf(tester), isNot(Serval.alert.toARGB32()));
    });

    testWidgets('a stale box is dashed, with no solid outline left', (
      tester,
    ) async {
      // Dashes are the whole difference between "it is here" and "it was here", so the solid
      // border has to be gone rather than merely joined by something.
      await pump(tester, isAlert: true, isStale: true);

      expect(dashedPainter(tester), isNotNull);
      expect(solidBorder(tester), isNull);
    });

    testWidgets('a measured box is solid, with nothing dashed', (tester) async {
      await pump(tester, isAlert: true);

      expect(solidBorder(tester), isNotNull);
      expect(dashedPainter(tester), isNull);
    });

    testWidgets('staleness does not change the colour', (tester) async {
      // The two channels are independent by design: the outline says how sure we are of the
      // position, the colour says whether the thing in it matters.
      await pump(tester, isAlert: true, isStale: true);
      expect(borderOf(tester), Serval.alert.toARGB32());

      await pump(tester, isAlert: false, isStale: true);
      expect(borderOf(tester), Nocturne.accent.toARGB32());
    });
  });
}

/// The colour a painter strokes with, recovered by letting it paint onto a canvas that records
/// what it was asked to do. Reaching into the painter's fields would test its shape instead.
Color _strokeColour(CustomPainter painter) {
  final canvas = _PaintSpy(PictureRecorder());
  painter.paint(canvas, const Size(128, 144));
  return canvas.strokes.first.color;
}

class _PaintSpy implements Canvas {
  _PaintSpy(PictureRecorder recorder) : _inner = Canvas(recorder);

  final Canvas _inner;
  final strokes = <Paint>[];

  @override
  void drawPath(Path path, Paint paint) {
    strokes.add(paint);
    _inner.drawPath(path, paint);
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}
