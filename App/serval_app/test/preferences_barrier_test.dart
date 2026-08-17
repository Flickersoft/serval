// What may be written back before this account's preferences have been read.
//
// Nothing, is the answer, and the reason is that the failure is invisible. `GET /api/preferences`
// coming back empty and not coming back at all produce the same screen — the design's default
// packing — because that is the right thing to show either way. What they must not share is the
// write: it sends a whole value the Server stores as stated, so an edit made against a default that
// is standing in for an unread arrangement *becomes* the arrangement.
//
// `saveWallLayout` fires on every accepted move and resize rather than from a save button, so the
// destructive version of this is one dragged tile immediately after a deploy.
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_api.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/models/camera.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:5211'));

  /// Every request the repository actually put on the wire.
  late List<http.Request> sent;

  setUp(() => sent = []);

  /// A repository whose preferences read behaves as [preferences] says.
  ///
  /// Never started: `start()` opens both sockets and reads the registry, none of which these need.
  /// Not starting is also what leaves `preferencesKnown` false, which is the state under test — the
  /// same state a started repository is left in by a read that failed.
  LiveServalRepository repository({
    required Future<http.Response> Function(int attempt) preferences,
  }) {
    var attempt = 0;

    return LiveServalRepository(
      auth: AuthController(config: config),
      config: config,
      api: ServalApi(
        config: config,
        client: MockClient((request) async {
          sent.add(request);
          if (request.url.path == '/api/preferences') {
            if (request.method == 'GET') return preferences(attempt++);

            return http.Response(
              jsonEncode({'wallLayout': <Object?>[]}),
              200,
              headers: const {'content-type': 'application/json'},
            );
          }

          return http.Response(
            '{}',
            200,
            headers: const {'content-type': 'application/json'},
          );
        }),
      ),
    );
  }

  http.Response ok(Map<String, dynamic> body) => http.Response(
    jsonEncode(body),
    200,
    headers: const {'content-type': 'application/json'},
  );

  Iterable<http.Request> writes() =>
      sent.where((r) => r.method == 'PUT' || r.method == 'POST');

  group('before preferences have been read', () {
    test('nothing is known', () {
      expect(
        repository(preferences: (_) async => ok(const {})).preferencesKnown,
        isFalse,
      );
    });

    test('an arrangement is not written back', () async {
      final subject = repository(preferences: (_) async => ok(const {}));

      await subject.saveWallLayout(const [
        TileLayout(cameraId: 'driveway', column: 0, row: 0),
      ]);

      // The assertion is about the wire, not about the return: a refusal that still sent the
      // default would be the bug, and it would pass anything phrased as "did not throw".
      expect(writes(), isEmpty);
    });
  });

  group('once they arrive', () {
    test('a read that succeeds opens the writes', () async {
      final subject = repository(
        preferences: (_) async => ok(const {
          'wallLayout': [
            {'cameraId': 'driveway', 'column': 1, 'row': 2},
          ],
        }),
      );

      await subject.start();

      expect(subject.preferencesKnown, isTrue);

      await subject.saveWallLayout(const [
        TileLayout(cameraId: 'driveway', column: 0, row: 0),
      ]);

      expect(writes(), isNotEmpty);

      subject.dispose();
    });

    test('a read that fails is retried, and the arrangement lands late', () async {
      // The point of the retry: every other startup read heals itself, and without this one the
      // wall is the only thing still wrong minutes after the Server came back.
      final subject = repository(
        preferences: (attempt) async => attempt == 0
            ? throw http.ClientException('connection refused')
            : ok(const {
                'wallLayout': [
                  {'cameraId': 'driveway', 'column': 1, 'row': 2},
                ],
              }),
      );

      // What the retry is worth depends on it announcing itself: the wall is already on screen by
      // the time this lands, and its camera set has not changed, so a quiet assignment reaches
      // nothing at all. `_apply` fires the preference slice for exactly this reason.
      var announced = 0;
      subject.preferenceChanges.addListener(() {
        if (subject.preferencesKnown) announced++;
      });

      await subject.start();
      expect(subject.preferencesKnown, isFalse);

      // Past the first backoff step, which matches DashboardSocket's minimum.
      await Future<void>.delayed(const Duration(milliseconds: 1400));

      expect(subject.preferencesKnown, isTrue);
      expect(announced, greaterThan(0));

      subject.dispose();
    });
  });
}
