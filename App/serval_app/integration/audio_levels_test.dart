import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/audio_levels_socket.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/auth/authenticated_client.dart';
import 'package:serval_app/data/serval_api.dart';
import 'package:serval_app/data/serval_config.dart';

/// The live level feed, against a real Server.
///
/// **Deliberately outside `test/`** so `flutter test` never runs it. By hand, with an existing
/// account (see `Server/Serval.Server/Auth/AdminBootstrap.cs` for how to create one):
///
/// ```bash
/// flutter test integration/audio_levels_test.dart \
///   --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
///   --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=...
/// ```
///
/// **Read-only throughout.** It opens a socket and closes it; no camera is created, edited or
/// moved. That is what makes it safe to point at somebody's house.
///
/// This is also the acceptance test for the meter, and it reproduces the measurement that
/// motivated per-camera thresholds in the first place: point it at a quiet indoor camera and the
/// reported peak sits below the default 0.01 threshold that the same feed reports as being in
/// force.
void main() {
  final config = ServalConfig.fromEnvironment();
  final auth = AuthController(config: config);
  final api = ServalApi(
    config: config,
    client: AuthenticatedClient(auth: auth),
  );

  setUpAll(() async {
    final signedIn = await auth.login(
      const String.fromEnvironment('SERVAL_USERNAME'),
      const String.fromEnvironment('SERVAL_PASSWORD'),
    );
    if (!signedIn) {
      throw StateError(
        'Could not sign in (${auth.error}) — pass '
        '--dart-define=SERVAL_USERNAME=... --dart-define=SERVAL_PASSWORD=...',
      );
    }
  });

  tearDownAll(() {
    api.close();
    auth.dispose();
  });

  /// The first camera with audio analysis on. Chosen rather than hard-coded so this test does not
  /// carry one deployment's camera ids.
  Future<String?> firstListeningCamera() async {
    final cameras = await api.listCameras();
    for (final camera in cameras) {
      if (camera.enabled && camera.aiAudio) return camera.id;
    }
    return null;
  }

  setUpAll(() {
    // The test binding installs an HttpOverrides that fails every request. Opt out — the whole
    // point here is to reach a real Server.
    HttpOverrides.global = null;
  });

  test('carries a steady stream of readings for a listening camera', () async {
    final cameraId = await firstListeningCamera();
    if (cameraId == null) {
      markTestSkipped('No enabled camera has aiAudio on.');
      return;
    }

    final feed = AudioLevelFeed(
      config: config,
      cameraId: cameraId,
      mintTicket: auth.mintWsTicket,
    );
    addTearDown(feed.close);

    final seen = <AudioLevel>[];
    void collect() {
      if (feed.level.value case final reading?) seen.add(reading);
    }

    feed.level.addListener(collect);
    addTearDown(() => feed.level.removeListener(collect));

    await Future<void>.delayed(const Duration(seconds: 3));

    // ~10 Hz, so three seconds is about thirty. Twenty is a floor that tolerates a slow connect
    // without tolerating a feed that has stalled.
    expect(
      seen.length,
      greaterThanOrEqualTo(20),
      reason: 'expected roughly ten readings a second from camera $cameraId',
    );
  });

  test('reports the threshold actually in force for the camera', () async {
    final cameraId = await firstListeningCamera();
    if (cameraId == null) {
      markTestSkipped('No enabled camera has aiAudio on.');
      return;
    }

    final camera = await api.getCamera(cameraId);
    final feed = AudioLevelFeed(
      config: config,
      cameraId: cameraId,
      mintTicket: auth.mintWsTicket,
    );
    addTearDown(feed.close);

    // Wait for a first reading rather than a fixed sleep, so a slow connect fails as a timeout
    // with a reason rather than as a null dereference.
    final deadline = DateTime.now().add(const Duration(seconds: 10));
    while (feed.level.value == null && DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(const Duration(milliseconds: 100));
    }

    final reading = feed.level.value;
    expect(reading, isNotNull, reason: 'no reading arrived within ten seconds');

    // The whole reason the thresholds ride along with the level: the App cannot derive them for a
    // camera that overrides nothing, so this pins that what is drawn is what is enforced.
    final override = camera.audioTuning?.speechGateRmsThreshold;
    if (override != null) {
      expect(reading!.speechThreshold, closeTo(override, 1e-6));
    } else {
      expect(
        reading!.speechThreshold,
        greaterThan(0),
        reason: 'an untuned camera should still report the Server default',
      );
    }

    // ignore: avoid_print
    print(
      'camera $cameraId: rms=${reading.rms.toStringAsFixed(5)} '
      'peak=${reading.peak.toStringAsFixed(5)} '
      'threshold=${reading.speechThreshold.toStringAsFixed(5)} '
      'gate=${reading.speechGateOpen ? "open" : "shut"}',
    );
  });
}
