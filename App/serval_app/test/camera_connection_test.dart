// What the wall is allowed to claim about a camera it cannot hear.
//
// The Server publishes no status field, so every reading here is derived from one signal: whether
// that camera's ~1 fps JPEG is still arriving on `WS /api/dashboard`. Silence is the only evidence
// there is, and it has two completely unrelated causes — the camera stopped sending, or nobody
// here has been listening long enough to have heard it yet.
//
// Collapsing those two is the bug this file exists for. Bring a PWA back from the background and
// every camera's last frame is minutes old at once; a wall that reads staleness alone paints all
// six "is offline" on the strength of one socket nobody has told it went away. So `offline` is a
// failure state and nothing weaker — we listened, and heard nothing — with `connecting` covering
// everything up to that point.
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

  Map<String, dynamic> camera(String id, {bool enabled = true}) => {
    'id': id,
    'name': 'Cam $id',
    'enabled': enabled,
    'streams': [
      {
        'name': 'main',
        'url': 'rtsp://example/$id',
        'roles': ['record', 'detect', 'live'],
      },
    ],
  };

  /// A started repository with a real registry behind it, since `_viewOf` reads a `CameraRecord`
  /// and the seam that describes the frame clock does not fill one.
  ///
  /// No session, so both ticket mints answer null: the sockets never open and simply retry on
  /// their own backoff. Nothing here is about their traffic — the frame clock is seeded directly.
  Future<LiveServalRepository> started(
    List<Map<String, dynamic>> cameras,
  ) async {
    final repository = LiveServalRepository(
      auth: AuthController(config: config),
      config: config,
      api: ServalApi(
        config: config,
        client: MockClient((request) async {
          final body = request.url.path == '/api/cameras'
              ? cameras
              : const <dynamic>[];
          return http.Response(
            jsonEncode(body),
            200,
            headers: const {'content-type': 'application/json'},
          );
        }),
      ),
    );
    addTearDown(repository.dispose);

    await repository.start();
    return repository;
  }

  CameraConnection connectionOf(LiveServalRepository repository, String id) =>
      repository.cameraById(id)!.connection;

  test('a fresh frame is online', () async {
    final repository = await started([camera('driveway')]);
    repository.seedForTest(
      order: ['driveway'],
      lastFrameAt: {'driveway': DateTime.now()},
    );

    expect(connectionOf(repository, 'driveway'), CameraConnection.online);
  });

  test('stale frames inside the listening window read connecting', () async {
    // The reported bug, stated once. A phone coming back from the background has every camera's
    // last frame minutes old, and none of that is the cameras' fault.
    final now = DateTime.now();
    final repository = await started([camera('driveway')]);
    repository.seedForTest(
      order: ['driveway'],
      lastFrameAt: {'driveway': now.subtract(const Duration(minutes: 5))},
      listeningSince: now,
    );

    expect(connectionOf(repository, 'driveway'), CameraConnection.connecting);
  });

  test('stale frames after the window has closed read offline', () async {
    // The other half, and the reason the window is a window rather than a permanent excuse: once
    // we really have been listening and heard nothing, saying so is the whole job.
    final now = DateTime.now();
    final repository = await started([camera('driveway')]);
    repository.seedForTest(
      order: ['driveway'],
      lastFrameAt: {'driveway': now.subtract(const Duration(minutes: 5))},
      listeningSince: now.subtract(const Duration(seconds: 30)),
    );

    expect(connectionOf(repository, 'driveway'), CameraConnection.offline);
  });

  test('a camera that has never sent a frame reads connecting', () async {
    // Deliberately not bounded by the window. This says we have never successfully heard from this
    // camera — which is not a measurement of it, and no amount of elapsed listening turns it into
    // one. It is also what stops a cold start painting the whole wall as broken.
    final repository = await started([camera('driveway')]);
    repository.seedForTest(
      order: ['driveway'],
      listeningSince: DateTime.now().subtract(const Duration(minutes: 5)),
    );

    expect(connectionOf(repository, 'driveway'), CameraConnection.connecting);
  });

  test(
    'a camera that stayed dead across a resume goes offline while its neighbours come back',
    () async {
      // This is the test that earns the window over the obvious alternative, which was to clear
      // the frame clock on resume and let the cold-start branch cover it. Clearing forgets *which*
      // cameras were dead — so a camera unreachable for a week would read "connecting" forever,
      // alongside its working neighbours, every time the phone came out of a pocket.
      //
      // Keeping the clock and dating our own listening instead gives every camera the same fifteen
      // seconds, then lets them part company on the evidence.
      final now = DateTime.now();
      final repository = await started([
        camera('driveway'),
        camera('side-path'),
      ]);

      final frames = {
        'driveway': now,
        'side-path': now.subtract(const Duration(days: 7)),
      };

      repository.seedForTest(
        order: ['driveway', 'side-path'],
        lastFrameAt: frames,
        listeningSince: now,
      );

      // Just resumed: nothing has been heard from either, and neither is accused of anything.
      expect(
        connectionOf(repository, 'side-path'),
        CameraConnection.connecting,
      );

      repository.seedForTest(
        order: ['driveway', 'side-path'],
        lastFrameAt: frames,
        listeningSince: now.subtract(const Duration(seconds: 30)),
      );

      expect(connectionOf(repository, 'driveway'), CameraConnection.online);
      expect(connectionOf(repository, 'side-path'), CameraConnection.offline);
    },
  );

  test('a disabled camera is offline rather than connecting', () async {
    // `enabled` decides before the frame clock is consulted at all. A camera switched off on
    // purpose has never been asked for a frame, so the window has nothing to say about it — and
    // leaving it "connecting" would be a wall waiting forever for something nobody requested.
    final repository = await started([camera('kitchen', enabled: false)]);
    repository.seedForTest(order: ['kitchen'], listeningSince: DateTime.now());

    expect(connectionOf(repository, 'kitchen'), CameraConnection.offline);
  });
}
