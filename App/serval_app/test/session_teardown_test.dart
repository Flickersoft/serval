// What signing out has to forget, when the app keeps running.
//
// Signing out is not a page reload. The rail's button puts you on `/login` with the same
// `LiveServalRepository` still in memory, so unless something empties it, everything `start()`
// filled in survives into the next session: the registry, the activity feed, the wall arrangement,
// the vitals of the machine. The next person to sign in then sees the previous one's house, on a
// Server that told this App none of it.
//
// The other half is that it must be *restartable*. `_RepositoryStarter` in `main.dart` is the sole
// owner of both edges — it starts on the transition into `authenticated` and stops on the way out —
// and a `stop()` that left the sockets permanently closed would mean signing back in got a wall
// that never updated.
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/serval_api.dart';
import 'package:serval_app/data/serval_config.dart';

void main() {
  final config = ServalConfig(baseUrl: Uri.parse('http://localhost:5211'));

  http.Response ok(Object body) => http.Response(
    jsonEncode(body),
    200,
    headers: const {'content-type': 'application/json'},
  );

  Map<String, dynamic> camera(String id) => {
    'id': id,
    'name': 'Cam $id',
    'streams': [
      {
        'name': 'main',
        'url': 'rtsp://example/$id',
        'roles': ['record', 'detect', 'live'],
      },
    ],
  };

  /// A repository over a Server that answers as [account] says — so the second session can be told
  /// a different story from the first, which is the whole point.
  ({LiveServalRepository repository, void Function(String) signIn}) subject() {
    var who = 'first';

    final repository = LiveServalRepository(
      // No session: the ticket mints return null, so the sockets never open and simply retry on
      // their own backoff. Nothing here is about the sockets' traffic — only that `stop` takes
      // them down and `start` may raise them again.
      auth: AuthController(config: config),
      config: config,
      api: ServalApi(
        config: config,
        client: MockClient((request) async {
          final path = request.url.path;

          if (path == '/api/cameras') {
            return ok(
              who == 'first'
                  ? [camera('driveway'), camera('kitchen')]
                  : [camera('garage')],
            );
          }

          if (path == '/api/preferences') {
            return ok(
              who == 'first'
                  ? {
                      'wallLayout': [
                        {'cameraId': 'driveway', 'column': 6, 'row': 3},
                      ],
                    }
                  : {
                      'wallLayout': [
                        {'cameraId': 'garage', 'column': 1, 'row': 1},
                      ],
                    },
            );
          }

          // The telemetry fan-out, and anything else `start` reaches for.
          return ok(const <dynamic>[]);
        }),
      ),
    );

    return (repository: repository, signIn: (value) => who = value);
  }

  test('signing out forgets the account that was signed in', () async {
    final it = subject();
    addTearDown(it.repository.dispose);

    await it.repository.start();

    // In hand, so the assertions after `stop` are about it being taken away rather than about it
    // never having arrived.
    expect(
      [for (final c in it.repository.cameras()) c.id],
      ['driveway', 'kitchen'],
    );
    expect(it.repository.preferencesKnown, isTrue);
    expect(it.repository.wallLayout(), isNotEmpty);

    // The registry slice, which is what a screen reading `cameras()` is subscribed to. Signing out
    // has to reach it: the wall is still mounted and still drawing the last account's tiles until
    // something tells it they are gone.
    var notified = 0;
    it.repository.registryChanges.addListener(() => notified++);

    it.repository.stop();

    expect(it.repository.cameras(), isEmpty);
    expect(it.repository.cameraRecords(), isEmpty);
    expect(it.repository.wallLayout(), isEmpty);

    // The one that mattered most before this existed: left true, the next session would have taken
    // a default pack standing in for an arrangement it had not read as the arrangement, and the
    // first dragged tile would have written it back — see preferences_barrier_test.
    expect(it.repository.preferencesKnown, isFalse);

    // Not asserted here: `systemStats()` and the other lazy reads start a fetch as a side effect of
    // being asked, so calling them is not a way to observe what `stop` cleared. The registry above
    // is, and it is the same clear.

    // The screens are still mounted for the moment it takes the router to reach /login, and they
    // read all of the above during `build`.
    expect(notified, greaterThan(0));
  });

  test('signing back in gets the new account, not the old one', () async {
    final it = subject();
    addTearDown(it.repository.dispose);

    await it.repository.start();
    it.repository.stop();

    it.signIn('second');
    await it.repository.start();

    expect([for (final c in it.repository.cameras()) c.id], ['garage']);
    expect(it.repository.preferencesKnown, isTrue);

    // Not merely absent from the registry — gone from the arrangement too. `wallLayout` reconciles
    // the saved tiles against the cameras that exist, so a stale `_savedLayout` would have shown up
    // here as a tile for somebody else's camera.
    expect(
      [for (final tile in it.repository.wallLayout()) tile.cameraId],
      ['garage'],
    );
  });
}
