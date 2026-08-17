import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/activity.dart';

/// That the App reads what the Server actually sends.
///
/// The payloads below are copied verbatim from a live Server rather than written to match
/// the parser — which is the only version of this test worth having. The Server's own suite pins
/// the same contract from the other side (`TelemetryContractTests`), so between them the wire
/// format cannot drift silently in either direction.
void main() {
  group('scene', () {
    // GET /api/cameras/1/scenes?limit=1, unedited.
    const json = '''
    {
      "type": "scene",
      "schema_version": 5,
      "id": "96d97635-ead0-4c6d-bcf3-d11877f00431",
      "camera_id": "1",
      "received_at": "2026-08-01T01:02:07.972+00:00",
      "timestamp": "2026-08-01T01:01:39.493+00:00",
      "description": "The scene is a nighttime view of a residential driveway.",
      "trigger": "motion",
      "motion_score": 0.0345,
      "frame_count": 2,
      "frame_span_seconds": 1,
      "source": "server"
    }''';

    test('parses the fields the feed renders', () {
      final document = SceneDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(document.cameraId, '1');
      expect(document.description, startsWith('The scene is a nighttime view'));
      expect(document.trigger, 'motion');
      expect(document.motionScore, 0.0345);
      expect(document.frameSpanSeconds, 1);
      // Server-side AI rather than an edge module — the reason the field exists.
      expect(document.source, 'server');
    });

    test('sorts on timestamp, not received_at', () {
      // The two differ by ~28s here, because a description is produced well after the frames it
      // describes were captured. A feed ordered by delivery would put a slow description above
      // something that happened later.
      final document = SceneDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(
        document.when,
        DateTime.parse('2026-08-01T01:01:39.493Z').toLocal(),
      );
    });

    test('is discriminated by its type', () {
      final document = parseTelemetryDocument(
        'scene',
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(document, isA<SceneDocument>());
      expect(document!.kind, TelemetryKind.scene);
    });
  });

  group('utterance', () {
    const json = '''
    {
      "type": "utterance",
      "schema_version": 5,
      "id": "u-1",
      "camera_id": "1",
      "conversation_id": "c-1",
      "received_at": "2026-08-01T01:02:07.972+00:00",
      "timestamp": "2026-08-01T01:01:39.493+00:00",
      "transcript": "Hi, I've got a package for you.",
      "language": "en",
      "duration_seconds": 2.4,
      "speaker": "speaker_0",
      "emotion": "happy",
      "speaker_source": "live",
      "source": "module"
    }''';

    test('carries the transcript, its speaker and its conversation', () {
      final document = UtteranceDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(document.transcript, "Hi, I've got a package for you.");
      expect(document.conversationId, 'c-1');
      expect(document.durationSeconds, 2.4);

      // The literal shape `SpeakerLabeller` publishes, and 0-based. Pinned
      // because this fixture read `Speaker 1` for a long time, which is neither
      // — and anything reading a trailing digit off that would number the first
      // voice as the second.
      expect(document.speaker, 'speaker_0');

      // Carried as the raw wire word. Turning it into something drawable is
      // `ActivityEmotion.fromWire`'s job, and most of that vocabulary maps to
      // nothing on purpose.
      expect(document.emotion, 'happy');
    });

    test('tolerates the fields a module omits', () {
      // Nulls are written as absences, not as `"emotion": null` — the shared serializer drops
      // them deliberately, so an absent field has to parse as undetermined rather than throw.
      final document = UtteranceDocument.fromJson({
        'type': 'utterance',
        'id': 'u-2',
        'camera_id': '1',
        'timestamp': '2026-08-01T01:01:39.493+00:00',
        'transcript': 'Hello?',
      });

      expect(document.emotion, isNull);
      expect(document.speaker, isNull);
      expect(document.conversationId, isNull);
      expect(document.audioEvent, isNull);
      expect(document.durationSeconds, 0);
    });
  });

  group('conversation transcript', () {
    const json = '''
    {
      "type": "conversation_transcript",
      "schema_version": 5,
      "conversation_id": "c-1",
      "camera_id": "1",
      "started_at": "2026-08-01T01:01:30.000+00:00",
      "audio_seconds": 12.5,
      "speaker_count": 2,
      "text": "Hello? Delivery for number twelve. Be right there.",
      "turns": [
        { "start": 0.0, "end": 2.1, "speaker": 0, "text": "Hello?" },
        { "start": 2.4, "end": 5.0, "speaker": 0, "text": "Delivery for number twelve." },
        { "start": 6.0, "end": 7.4, "speaker": 1, "text": "Be right there." }
      ],
      "language": "en",
      "retranscribed_turns": 1,
      "source": "module"
    }''';

    test('takes its time from started_at, since it has no timestamp', () {
      final document = ConversationTranscriptDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(
        document.when,
        DateTime.parse('2026-08-01T01:01:30.000Z').toLocal(),
      );
    });

    test('keeps turns with their offsets and speaker numbers', () {
      final document = ConversationTranscriptDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(document.turns, hasLength(3));
      expect(document.turns.last.speaker, 1);
      expect(document.turns.last.start, 6.0);
      expect(document.speakerCount, 2);
    });

    test(
      'is keyed by its conversation, so a redelivery replaces rather than repeats',
      () {
        final document = ConversationTranscriptDocument.fromJson(
          jsonDecode(json) as Map<String, dynamic>,
        );

        // The Server upserts telemetry by the record's own id so a batch redelivered after a
        // network gap updates in place. The feed keys on the same thing for the same reason.
        expect(document.feedId, 'conversation_transcript:c-1');
      },
    );
  });

  group('sound', () {
    // HAND-WRITTEN, not captured. Sound detection needs the audio tagging model, which is not in
    // git; replace this with a verbatim GET /api/cameras/1/sounds payload once one exists, as
    // every other group here is. Field names and types are pinned against
    // `SoundDocument` in Serval.Contracts, and by TelemetryContractTests on the other side.
    const json = '''
    {
      "type": "sound",
      "schema_version": 5,
      "id": "3f2a91c4-77b1-4a0e-9e5d-2c8f10b4e6a3",
      "camera_id": "1",
      "received_at": "2026-08-01T01:04:11.204+00:00",
      "timestamp": "2026-08-01T01:04:08.750+00:00",
      "label": "Vehicle horn, car horn, honking",
      "confidence": 0.812,
      "alternates": [
        { "label": "Vehicle horn, car horn, honking", "confidence": 0.812 },
        { "label": "Vehicle", "confidence": 0.441 },
        { "label": "Car", "confidence": 0.287 }
      ],
      "is_alert": false,
      "duration_seconds": 2.4,
      "source": "server"
    }''';

    test('keeps the label exactly as the model spelled it', () {
      final document = SoundDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      // Commas and all. The Server deliberately does not tidy AudioSet labels into slugs, so
      // trimming one here would put the App's display choice into the parser.
      expect(document.label, 'Vehicle horn, car horn, honking');
      expect(document.confidence, 0.812);
      expect(document.durationSeconds, 2.4);
      expect(document.isAlert, isFalse);
    });

    test('shortens the label for display without losing the original', () {
      final document = SoundDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(document.shortLabel, 'Vehicle horn');
      expect(document.label, contains('honking'));
    });

    test('parses the scored shortlist, winner first', () {
      final document = SoundDocument.fromJson(
        jsonDecode(json) as Map<String, dynamic>,
      );

      // Stored so a threshold can be re-derived from real recordings later; useless if the
      // ordering or the scores are lost on the way in.
      expect(document.alternates, hasLength(3));
      expect(document.alternates.first.label, document.label);
      expect(document.alternates.last.label, 'Car');
      expect(document.alternates.last.confidence, 0.287);
    });

    test('dispatches on its type discriminator', () {
      final document = parseTelemetryDocument(
        'sound',
        jsonDecode(json) as Map<String, dynamic>,
      );

      expect(document, isA<SoundDocument>());
      expect(document!.kind, TelemetryKind.sound);
      expect(document.feedId, 'sound:3f2a91c4-77b1-4a0e-9e5d-2c8f10b4e6a3');
    });

    test('an alert is carried through', () {
      final raw = jsonDecode(json) as Map<String, dynamic>
        ..['label'] = 'Glass'
        ..['is_alert'] = true;

      expect(SoundDocument.fromJson(raw).isAlert, isTrue);
    });
  });

  test('an unknown record type is dropped, not fatal', () {
    // One record the parser cannot name should not empty the activity column of every record it
    // can.
    expect(
      parseTelemetryDocument('weather', const {'type': 'weather'}),
      isNull,
    );
  });

  group('a detection', () {
    Map<String, dynamic> json({Object? endedAt = '2026-08-04T12:00:42Z'}) => {
      'type': 'detection',
      'schema_version': 7,
      'id': 'det-1',
      'camera_id': 'driveway',
      'timestamp': '2026-08-04T12:00:00Z',
      'ended_at': endedAt,
      'label': 'person',
      'peak_confidence': 0.91,
      'peak_frame_at': '2026-08-04T12:00:12Z',
      'frame_count': 40,
      'best_box': {
        'x': 0.1,
        'y': 0.2,
        'width': 0.3,
        'height': 0.4,
        'score': 0.91,
      },
      'is_alert': true,
      'source': 'server',
    };

    test('is discriminated by its type', () {
      final document = parseTelemetryDocument('detection', json());

      expect(document, isA<DetectionDocument>());
      expect(document!.kind, TelemetryKind.detection);
      expect(document.feedId, 'detection:det-1');
    });

    test('a null end means it is still there', () {
      final open =
          parseTelemetryDocument('detection', json(endedAt: null))!
              as DetectionDocument;
      final closed =
          parseTelemetryDocument('detection', json())! as DetectionDocument;

      expect(open.isOngoing, isTrue);
      expect(closed.isOngoing, isFalse);
      expect(closed.duration, const Duration(seconds: 42));
    });

    test('an omitted end is the same as a null one', () {
      // The Server drops nulls when serialising, so "still present" reaches the
      // App as an absent field rather than an explicit null.
      final fields = json()..remove('ended_at');

      expect(
        (parseTelemetryDocument('detection', fields)! as DetectionDocument)
            .isOngoing,
        isTrue,
      );
    });

    test('becomes the overlay box the video draws', () {
      final document =
          parseTelemetryDocument('detection', json())! as DetectionDocument;

      final box = document.overlays.single;
      expect(box.label, 'person');
      expect(box.confidence, 0.91);
      expect(box.caption, 'PERSON · 0.91');
      expect(box.rect.left, closeTo(0.1, 1e-9));
      expect(box.rect.width, closeTo(0.3, 1e-9));
    });

    test('becomes exactly one overlay box, because it is one object', () {
      // Three people is three records, so nothing here ever holds more than one box. The overlay
      // pools them across records — see detection_overlay_test.dart.
      expect(
        (parseTelemetryDocument('detection', json())! as DetectionDocument)
            .overlays,
        hasLength(1),
      );
    });

    test('carries no overlay when it carries no geometry', () {
      final fields = json()..remove('best_box');

      expect(
        (parseTelemetryDocument('detection', fields)! as DetectionDocument)
            .overlays,
        isEmpty,
      );
    });

    group('replayed at an instant', () {
      // Present at 12:00 on the left, at 12:00:10 on the right, gone from
      // 12:00:20, and the episode itself ends at 12:00:42.
      DetectionDocument tracked() {
        final fields = json()
          ..['track'] = [
            {
              'at': '2026-08-04T12:00:00Z',
              'box': {'x': 0.1, 'y': 0.2, 'width': 0.3, 'height': 0.4},
            },
            {
              'at': '2026-08-04T12:00:10Z',
              'box': {'x': 0.6, 'y': 0.2, 'width': 0.3, 'height': 0.4},
            },
            {'at': '2026-08-04T12:00:20Z', 'box': null},
          ];
        return parseTelemetryDocument('detection', fields)!
            as DetectionDocument;
      }

      DateTime at(int second) => DateTime.parse(
        '2026-08-04T12:00:${second.toString().padLeft(2, '0')}Z',
      );

      test('draws where the object was, not where it looked best', () {
        // best_box is the 0.1 one. Ten seconds in, the object is at 0.6, and
        // painting the peak frame's box there would be a box over empty road.
        expect(
          tracked().overlaysAt(at(12)).single.rect.left,
          closeTo(0.6, 1e-9),
        );
      });

      test('holds a sample until the next one', () {
        // The whole of the run-length encoding: nothing was written between
        // 12:00:00 and 12:00:10 because nothing moved.
        expect(
          tracked().overlaysAt(at(5)).single.rect.left,
          closeTo(0.1, 1e-9),
        );
      });

      test('holds the last position through a gap, marked stale', () {
        // A gap is a sample whose box is null: the episode is still open — the
        // Server is waiting out the absence window — and nothing was measured
        // here. The last place the object was known to be is what there is to
        // say, so it is said dotted rather than not at all, which is what stops
        // the overlay going blank while the row still reads "still there".
        final drawn = tracked().overlaysAt(at(25)).single;

        expect(drawn.rect.left, closeTo(0.6, 1e-9));
        expect(drawn.isStale, isTrue);
      });

      test('draws nothing outside the episode', () {
        expect(
          tracked().overlaysAt(DateTime.parse('2026-08-04T11:59:59Z')),
          isEmpty,
        );
        expect(
          tracked().overlaysAt(DateTime.parse('2026-08-04T12:00:43Z')),
          isEmpty,
        );
      });

      test('holds the last sample to the end of the episode', () {
        final fields = json()
          ..['track'] = [
            {
              'at': '2026-08-04T12:00:00Z',
              'box': {'x': 0.1, 'y': 0.2, 'width': 0.3, 'height': 0.4},
            },
          ];
        final document =
            parseTelemetryDocument('detection', fields)! as DetectionDocument;

        // Something that arrived and stood still writes one sample and stops.
        // Reading that as "gone after 12:00:00" would lose the whole episode.
        expect(
          document.overlaysAt(at(41)).single.rect.left,
          closeTo(0.1, 1e-9),
        );
      });

      test('draws nothing for an episode that carries no track', () {
        // best_box is present on these, and pinning it for the episode's whole
        // duration would put a box wherever the object once was for as long as
        // it was anywhere.
        final document =
            parseTelemetryDocument('detection', json())! as DetectionDocument;

        expect(document.track, isEmpty);
        expect(document.overlaysAt(at(12)), isEmpty);
        expect(document.overlays, isNotEmpty);
      });

      test('an ongoing episode replays up to its last sample and beyond', () {
        final fields = json(endedAt: null)
          ..['track'] = [
            {
              'at': '2026-08-04T12:00:00Z',
              'box': {'x': 0.1, 'y': 0.2, 'width': 0.3, 'height': 0.4},
            },
          ];
        final document =
            parseTelemetryDocument('detection', fields)! as DetectionDocument;

        expect(document.overlaysAt(at(30)), isNotEmpty);
      });
    });
  });
}
