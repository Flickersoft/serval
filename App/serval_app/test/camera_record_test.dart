import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/camera_record.dart';

/// The camera record, and the rules the settings screen enforces before saving.
///
/// The JSON below is `GET /api/cameras` from the live Server, with the password changed. Two
/// things here are load-bearing beyond ordinary parsing: the record has to survive a round trip
/// because `PUT` replaces rather than merges, and [CameraRecord.roleProblem] has to agree with
/// the Server's validation or the form disables a save the Server would have accepted — or
/// worse, permits one it rejects.
void main() {
  const json = '''
  {
    "id": "1",
    "name": "Driveway",
    "location": "Front Yard",
    "streams": [
      {
        "name": "main",
        "url": "rtsp://view:secret@192.168.1.50:554/h264Preview_01_main",
        "roles": ["record"],
        "transcode": null
      },
      {
        "name": "sub",
        "url": "rtsp://view:secret@192.168.1.50:554/h264Preview_01_sub",
        "roles": ["detect", "live"],
        "transcode": null
      }
    ],
    "enabled": true,
    "retentionDays": 7,
    "onvifUrl": "http://192.168.1.50/onvif/device_service",
    "onvifUsername": "view",
    "onvifPassword": "secret",
    "onvifProfileToken": null,
    "twoWayAudio": true,
    "recordAudio": true,
    "aiVision": true,
    "aiAudio": true,
    "ptzConfigured": true
  }''';

  CameraRecord parse() =>
      CameraRecord.fromJson(jsonDecode(json) as Map<String, dynamic>);

  group('reading', () {
    test('parses every field the settings screen edits', () {
      final camera = parse();

      expect(camera.id, '1');
      expect(camera.name, 'Driveway');
      expect(camera.location, 'Front Yard');
      expect(camera.retentionDays, 7);
      expect(camera.twoWayAudio, isTrue);
      expect(camera.recordAudio, isTrue);
      expect(camera.aiVision, isTrue);
      expect(camera.aiAudio, isTrue);
      expect(camera.onvifUsername, 'view');
    });

    test('resolves each role to the stream that claimed it', () {
      final camera = parse();

      expect(camera.streamFor(StreamRole.record)!.name, 'main');
      expect(camera.streamFor(StreamRole.detect)!.name, 'sub');
      expect(camera.streamFor(StreamRole.live)!.name, 'sub');
    });

    test(
      'derives ptzConfigured from the ONVIF url rather than reading it back',
      () {
        // `ptzConfigured` is computed and read-only on the Server, so trusting the field would mean
        // trusting something we can never send. Clearing the url has to clear the capability.
        expect(parse().ptzConfigured, isTrue);
        expect(parse().copyWith(clearOnvif: true).ptzConfigured, isFalse);
      },
    );

    test('takes the header host from the recording stream', () {
      expect(parse().host, '192.168.1.50');
    });

    test('masks the password inside a stream url but keeps the user', () {
      final stream = parse().streamFor(StreamRole.record)!;

      expect(stream.maskedUrl, contains('view:'));
      expect(stream.maskedUrl, isNot(contains('secret')));
      // The real value survives for the copy button — a copy that yields dots is useless.
      expect(stream.url, contains('secret'));
    });

    test(
      'drops a role this build does not know instead of failing the list',
      () {
        final camera = CameraRecord.fromJson({
          'id': 'x',
          'name': 'X',
          'streams': [
            {
              'name': 'main',
              'url': 'rtsp://x/1',
              'roles': ['record', 'teleport'],
            },
          ],
        });

        expect(camera.streams.single.roles, [StreamRole.record]);
      },
    );

    /// A Server predating the field answers without it, and every camera reading as "keeps
    /// nothing" would be a wall of cameras claiming they record nothing while recording fine.
    test('a camera with no recording field reads as recording', () {
      final camera = CameraRecord.fromJson({
        'id': 'x',
        'name': 'X',
        'streams': [
          {
            'name': 'main',
            'url': 'rtsp://x/1',
            'roles': ['record', 'detect', 'live'],
          },
        ],
      });

      expect(camera.recording, isTrue);
      expect(camera.records, isTrue);
    });
  });

  group('writing', () {
    test('round-trips, because PUT replaces rather than merges', () {
      final original = parse();
      final round = CameraRecord.fromJson(
        jsonDecode(jsonEncode(original.toJson())) as Map<String, dynamic>,
      );

      expect(round.id, original.id);
      expect(round.name, original.name);
      expect(round.location, original.location);
      expect(round.retentionDays, original.retentionDays);
      expect(
        round.streams.map((s) => s.name),
        original.streams.map((s) => s.name),
      );
      expect(round.streams.first.roles, original.streams.first.roles);
      expect(round.aiVision, original.aiVision);
    });

    /// `PUT` replaces rather than merges, so a `recording` missing from the body would read back
    /// as the Server's default of true — a save of any unrelated field silently switching
    /// recording back on for a camera deliberately holding off.
    test('always sends recording, so a save cannot switch it back on', () {
      final off = parse().copyWith(recording: false);

      expect(off.toJson()['recording'], isFalse);
      expect(CameraRecord.fromJson(off.toJson()).recording, isFalse);
    });

    test('carries the ONVIF password through untouched', () {
      // The form never displays it, but dropping it on read would delete the camera's
      // credentials the next time any unrelated field is saved.
      expect(parse().toJson()['onvifPassword'], 'secret');
    });

    test('never sends ptzConfigured, which the Server computes', () {
      expect(parse().toJson().containsKey('ptzConfigured'), isFalse);
    });
  });

  group('role rules, mirroring the Server', () {
    test('accepts the live camera as configured', () {
      expect(parse().roleProblem, isNull);
    });

    test('accepts one stream carrying all three roles', () {
      expect(
        CameraRecord.blank()
            .copyWith(
              name: 'Doorbell',
              streams: [
                const CameraStreamRecord(
                  name: 'main',
                  url: 'rtsp://x/1',
                  roles: [
                    StreamRole.record,
                    StreamRole.detect,
                    StreamRole.live,
                  ],
                ),
              ],
            )
            .roleProblem,
        isNull,
      );
    });

    test('rejects an unassigned role, naming it in the design’s words', () {
      final problem = parse()
          .copyWith(
            streams: [
              const CameraStreamRecord(
                name: 'main',
                url: 'rtsp://x/1',
                roles: [StreamRole.record],
              ),
              const CameraStreamRecord(
                name: 'sub',
                url: 'rtsp://x/2',
                roles: [StreamRole.detect],
              ),
            ],
          )
          .roleProblem;

      expect(problem, contains('Live view'));
    });

    test('rejects two streams claiming the same role', () {
      final problem = parse()
          .copyWith(
            streams: [
              const CameraStreamRecord(
                name: 'main',
                url: 'rtsp://x/1',
                roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
              ),
              const CameraStreamRecord(
                name: 'sub',
                url: 'rtsp://x/2',
                roles: [StreamRole.detect],
              ),
            ],
          )
          .roleProblem;

      expect(problem, contains('exactly one'));
    });

    // Recording is the one job a camera may leave unassigned, so its two-holders message has to
    // say "one, or none" — "exactly one" would be the app contradicting what it just allowed.
    test(
      'rejects two streams recording, and offers none as the alternative',
      () {
        final problem = parse()
            .copyWith(
              streams: [
                const CameraStreamRecord(
                  name: 'main',
                  url: 'rtsp://x/1',
                  roles: [
                    StreamRole.record,
                    StreamRole.detect,
                    StreamRole.live,
                  ],
                ),
                const CameraStreamRecord(
                  name: 'sub',
                  url: 'rtsp://x/2',
                  roles: [StreamRole.record],
                ),
              ],
            )
            .roleProblem;

        expect(problem, contains('one, or none'));
      },
    );

    test('accepts a camera with nothing set to Recording', () {
      final camera = parse().copyWith(
        recording: false,
        streams: [
          const CameraStreamRecord(
            name: 'main',
            url: 'rtsp://x/1',
            roles: [StreamRole.detect, StreamRole.live],
          ),
        ],
      );

      expect(camera.roleProblem, isNull);
      expect(camera.records, isFalse);
    });

    test('a camera with a record stream reports that it records', () {
      expect(parse().records, isTrue);
    });

    /// The switch on *Keeping footage*, and the whole reason it is a field rather than a role
    /// edit: the assignment survives it, so turning recording back on needs no second decision
    /// about which stream gets the job.
    test('recording switched off keeps nothing, and keeps the assignment', () {
      final camera = parse().copyWith(recording: false);

      expect(camera.records, isFalse);
      expect(camera.recordStreamName, 'main');
      expect(camera.roleProblem, isNull);
    });

    test('rejects Recording switched on with no stream to write', () {
      // Not a state the form can reach — the toggle is inert without a record stream — but the
      // Server rejects it, so the form has to say so rather than posting a request that 400s.
      final problem = parse()
          .copyWith(
            streams: [
              const CameraStreamRecord(
                name: 'main',
                url: 'rtsp://x/1',
                roles: [StreamRole.detect, StreamRole.live],
              ),
            ],
          )
          .roleProblem;

      expect(problem, contains('Recording is on'));
    });

    test('accepts a stream with no job at all', () {
      // Kept and never pulled, which is how a source is held out of service without losing its
      // address. The Server names it in the log, since a mistyped role list looks the same.
      final camera = parse().copyWith(
        streams: [
          const CameraStreamRecord(
            name: 'main',
            url: 'rtsp://x/1',
            roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
          ),
          const CameraStreamRecord(name: 'spare', url: 'rtsp://x/2', roles: []),
        ],
      );

      expect(camera.roleProblem, isNull);
    });

    test('still wants an address on a stream with no job', () {
      // The stream is stored whole, so an address blanked while it is out of service is gone when
      // it is put back to work.
      final problem = parse()
          .copyWith(
            streams: [
              const CameraStreamRecord(
                name: 'main',
                url: 'rtsp://x/1',
                roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
              ),
              const CameraStreamRecord(name: 'spare', url: '  ', roles: []),
            ],
          )
          .roleProblem;

      expect(problem, contains('no address'));
    });

    test(
      'rejects a transcode on a stream that is not the one being recorded',
      () {
        // Only the record stream is written to disk; a transcode elsewhere is rejected rather than
        // ignored, so it cannot become a core of CPU nobody asked for.
        final problem = parse()
            .copyWith(
              streams: [
                const CameraStreamRecord(
                  name: 'main',
                  url: 'rtsp://x/1',
                  roles: [StreamRole.record],
                ),
                const CameraStreamRecord(
                  name: 'sub',
                  url: 'rtsp://x/2',
                  roles: [StreamRole.detect, StreamRole.live],
                  transcode: TranscodeSettings(codec: 'h264'),
                ),
              ],
            )
            .roleProblem;

        expect(problem, contains('only the recording stream'));
      },
    );

    test('leaves a re-encode alone on a stream with no job', () {
      // Kept and inert, so taking a stream out of service and putting it back does not lose the
      // setting — the same treatment the audio thresholds get while speech is switched off.
      final camera = parse().copyWith(
        streams: [
          const CameraStreamRecord(
            name: 'main',
            url: 'rtsp://x/1',
            roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
          ),
          const CameraStreamRecord(
            name: 'spare',
            url: 'rtsp://x/2',
            roles: [],
            transcode: TranscodeSettings(codec: 'h264'),
          ),
        ],
      );

      expect(camera.roleProblem, isNull);
    });

    test('rejects a stream with no address', () {
      final problem = parse()
          .copyWith(
            streams: [
              const CameraStreamRecord(
                name: 'main',
                url: '   ',
                roles: [StreamRole.record, StreamRole.detect, StreamRole.live],
              ),
            ],
          )
          .roleProblem;

      expect(problem, contains('no address'));
    });
  });

  test('a blank camera starts in a shape the Server would accept', () {
    // The Add flow's starting point: one stream carrying all three roles, since there is no
    // fallback and no stream may be role-less. Only the id, name and url are left to fill in.
    final blank = CameraRecord.blank().copyWith(
      id: 'testcam',
      name: 'Test',
      streams: [
        CameraRecord.blank().streams.single.copyWith(url: '/tmp/testcam.mp4'),
      ],
    );

    expect(blank.roleProblem, isNull);
  });

  group('audio tuning', () {
    CameraRecord tuned() => CameraRecord.blank().copyWith(
      id: 'testcam',
      name: 'Test',
      audioTuning: const AudioTuningSettings(
        speechGateRmsThreshold: 0.0015,
        vadThreshold: 0.7,
        soundGateRmsThreshold: 0.002,
      ),
    );

    test('parses from the wire', () {
      final parsed = CameraRecord.fromJson({
        'id': 'testcam',
        'name': 'Test',
        'streams': <dynamic>[],
        'audioTuning': {
          'speechGateRmsThreshold': 0.0015,
          'vadThreshold': 0.7,
          'soundGateRmsThreshold': 0.002,
        },
      });

      expect(parsed.audioTuning?.speechGateRmsThreshold, 0.0015);
      expect(parsed.audioTuning?.vadThreshold, 0.7);
      expect(parsed.audioTuning?.soundGateRmsThreshold, 0.002);
    });

    test('an absent block parses as no tuning', () {
      final parsed = CameraRecord.fromJson({
        'id': 'testcam',
        'name': 'Test',
        'streams': <dynamic>[],
      });

      expect(parsed.audioTuning, isNull);
    });

    /// The field-loss test. `PUT` replaces rather than merges, so a threshold missing from
    /// [CameraRecord.toJson] is a threshold deleted on the next save of any unrelated field —
    /// the same way dropping `onvifPassword` would delete a camera's credentials.
    test('survives a round trip through toJson', () {
      final restored = CameraRecord.fromJson(tuned().toJson());

      expect(restored.audioTuning?.speechGateRmsThreshold, 0.0015);
      expect(restored.audioTuning?.vadThreshold, 0.7);
      expect(restored.audioTuning?.soundGateRmsThreshold, 0.002);
    });

    test(
      'toJson always carries the key, so an untuned camera clears it explicitly',
      () {
        final untuned = CameraRecord.blank().copyWith(
          id: 'testcam',
          name: 'Test',
        );

        expect(untuned.toJson().containsKey('audioTuning'), isTrue);
        expect(untuned.toJson()['audioTuning'], isNull);
      },
    );

    test('a partially tuned camera omits only what it has not set', () {
      final partial = CameraRecord.blank().copyWith(
        audioTuning: const AudioTuningSettings(speechGateRmsThreshold: 0.0015),
      );

      final json = partial.toJson()['audioTuning'] as Map<String, dynamic>;

      expect(json.containsKey('speechGateRmsThreshold'), isTrue);
      expect(json.containsKey('vadThreshold'), isFalse);
      expect(json.containsKey('soundGateRmsThreshold'), isFalse);
    });

    test('one threshold can be cleared without disturbing the others', () {
      final cleared = tuned().audioTuning!.copyWith(clearVad: true);

      expect(cleared.vadThreshold, isNull);
      expect(cleared.speechGateRmsThreshold, 0.0015);
      expect(cleared.soundGateRmsThreshold, 0.002);
    });

    test(
      'clearing every threshold leaves an empty object, which the form collapses',
      () {
        final cleared = tuned().audioTuning!
            .copyWith(clearVad: true)
            .copyWith(clearSpeechGate: true)
            .copyWith(clearSoundGate: true);

        expect(cleared.isEmpty, isTrue);
      },
    );

    test(
      'clearAudioTuning drops the whole block',
      () =>
          expect(tuned().copyWith(clearAudioTuning: true).audioTuning, isNull),
    );

    /// The settings form compares before and after to name unsaved changes, so value equality is
    /// what stops it reporting a change that is not one.
    test('compares by value', () {
      expect(
        const AudioTuningSettings(speechGateRmsThreshold: 0.0015),
        const AudioTuningSettings(speechGateRmsThreshold: 0.0015),
      );
      expect(
        const AudioTuningSettings(speechGateRmsThreshold: 0.0015),
        isNot(const AudioTuningSettings(speechGateRmsThreshold: 0.002)),
      );
    });
  });

  /// The bug this group exists for: `detectionTuning` was absent from [CameraRecord] entirely —
  /// not in `fromJson`, not in `toJson`, nowhere in the app. Since `PUT` replaces rather than
  /// merges, **every save from the settings screen silently deleted that camera's classes,
  /// thresholds and masks.** The same trap the `onvifPassword` and `audioTuning` comments describe,
  /// never applied to detection. These pin all four bags against it.
  group('every tuning bag survives a save', () {
    const tunedJson = '''
    {
      "id": "1",
      "name": "Driveway",
      "streams": [
        { "name": "main", "url": "rtsp://cam/main", "roles": ["record", "detect", "live"] }
      ],
      "audioTuning": { "speechGateRmsThreshold": 0.0015 },
      "detectionTuning": {
        "classes": ["person", "car"],
        "describeClasses": ["person"],
        "alertClasses": ["person"],
        "scoreThreshold": 0.4,
        "alertMinConfidence": 0.75,
        "trackConfirmSeconds": 3,
        "trackCoastSeconds": 4,
        "maxFps": 0.5,
        "minMovementFraction": 0.03,
        "absenceSeconds": 45,
        "noveltySeconds": 300,
        "masks": [{ "name": "road", "points": [0, 0, 1, 0, 1, 0.3] }]
      },
      "soundTuning": {
        "alertLabels": ["Glass", "Gunshot, gunfire"],
        "ignoredLabels": ["Speech"],
        "minConfidence": 0.4,
        "alertMinConfidence": 0.8,
        "cooldownSeconds": 90,
        "alertCooldownSeconds": 10
      },
      "motionTuning": {
        "pixelDelta": 30,
        "minChangedFraction": 0.05,
        "maxChangedFraction": 0.6
      }
    }''';

    CameraRecord tunedCamera() =>
        CameraRecord.fromJson(jsonDecode(tunedJson) as Map<String, dynamic>);

    test('detection tuning is read back in full', () {
      final detection = tunedCamera().detectionTuning!;

      expect(detection.classes, ['person', 'car']);
      expect(detection.describeClasses, ['person']);
      expect(detection.alertClasses, ['person']);
      expect(detection.scoreThreshold, 0.4);
      expect(detection.alertMinConfidence, 0.75);
      expect(detection.trackConfirmSeconds, 3);
      expect(detection.trackCoastSeconds, 4);
      expect(detection.maxFps, 0.5);
      expect(detection.minMovementFraction, 0.03);
      expect(detection.absenceSeconds, 45);
      expect(detection.noveltySeconds, 300);
      expect(detection.masks!.single.name, 'road');
    });

    test('detection tuning survives a round trip through toJson', () {
      final restored = CameraRecord.fromJson(tunedCamera().toJson());

      expect(restored.detectionTuning, tunedCamera().detectionTuning);
    });

    test('sound tuning survives a round trip through toJson', () {
      final restored = CameraRecord.fromJson(tunedCamera().toJson());

      expect(restored.soundTuning, tunedCamera().soundTuning);
      // The comma inside an AudioSet label has to survive, since splitting on one would turn a
      // single label into two that match nothing.
      expect(restored.soundTuning!.alertLabels, contains('Gunshot, gunfire'));
    });

    test('movement tuning survives a round trip through toJson', () {
      final restored = CameraRecord.fromJson(tunedCamera().toJson());

      expect(restored.motionTuning, tunedCamera().motionTuning);
    });

    /// Masks are not editable in the app — drawing a polygon over a live view is its own screen,
    /// and there isn't one. They still have to be carried, or opening a camera and saving an
    /// unrelated field would delete regions someone set through the API.
    test('masks are carried through a save the app cannot edit them in', () {
      final restored = CameraRecord.fromJson(tunedCamera().toJson());

      expect(restored.detectionTuning!.masks!.single.points, [
        0,
        0,
        1,
        0,
        1,
        0.3,
      ]);
    });

    test('editing an unrelated field leaves every bag intact', () {
      // The exact shape of the bug: change the name, save, and the tuning must still be there.
      final renamed = tunedCamera().copyWith(name: 'Front drive');
      final restored = CameraRecord.fromJson(renamed.toJson());

      expect(restored.name, 'Front drive');
      expect(restored.detectionTuning, tunedCamera().detectionTuning);
      expect(restored.soundTuning, tunedCamera().soundTuning);
      expect(restored.motionTuning, tunedCamera().motionTuning);
      expect(restored.audioTuning, tunedCamera().audioTuning);
    });

    test('an untuned camera carries each key explicitly as null', () {
      final untuned = CameraRecord.blank().copyWith(
        id: 'testcam',
        name: 'Test',
      );
      final json = untuned.toJson();

      for (final key in ['detectionTuning', 'soundTuning', 'motionTuning']) {
        expect(json.containsKey(key), isTrue, reason: '$key must be sent');
        expect(
          json[key],
          isNull,
          reason: '$key must clear rather than be omitted',
        );
      }
    });

    test('copyWith can clear a bag without disturbing the others', () {
      // Passing null clears; omitting keeps. The sentinel is what tells those apart.
      final cleared = tunedCamera().copyWith(soundTuning: null);

      expect(cleared.soundTuning, isNull);
      expect(cleared.detectionTuning, isNotNull);
      expect(cleared.motionTuning, isNotNull);
    });

    test('copyWith leaves a bag alone when it is not passed', () {
      final renamed = tunedCamera().copyWith(name: 'Elsewhere');

      expect(renamed.soundTuning, isNotNull);
      expect(renamed.detectionTuning, isNotNull);
      expect(renamed.motionTuning, isNotNull);
    });

    test('a movement gate that can never open reports the problem', () {
      const impossible = MotionTuningSettings(
        minChangedFraction: 0.6,
        maxChangedFraction: 0.5,
      );

      expect(impossible.problem, isNotNull);
      expect(
        const MotionTuningSettings(
          minChangedFraction: 0.05,
          maxChangedFraction: 0.5,
        ).problem,
        isNull,
      );
    });
  });
}
