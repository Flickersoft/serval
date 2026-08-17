import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/live_repository.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/serval_config.dart';
import 'package:serval_app/models/ptz.dart';
import 'package:serval_app/models/timeline.dart';
import 'package:serval_app/screens/cameras_screen.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';

/// The App against a real Server.
///
/// **Deliberately outside `test/`**, so `flutter test` stays hermetic and this never runs in CI
/// or on a machine with no Server. Run it by hand against **a Server you started yourself** — see
/// `Docs/testing.md`, which has the throwaway-database invocation and a file-source pseudo-camera
/// that gives this something to read with no hardware. Not somebody's live NVR: this suite only
/// reads, but pointing test runs at a deployment in use is how the ones that write get pointed
/// there too.
///
/// Needs an existing account (see `Server/Serval.Server/Auth/AdminBootstrap.cs` for how to create
/// one):
///
/// ```bash
/// flutter test integration/live_server_test.dart \
///   --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
///   --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=...
/// ```
///
/// It only ever **reads**. Registry writes are exercised against a throwaway camera by hand —
/// nothing here touches a real one.
void main() {
  final config = ServalConfig.fromEnvironment();
  late AuthController auth;
  late LiveServalRepository repository;

  setUpAll(() async {
    // `flutter_test`'s binding installs an HttpOverrides that fails every request, so that a
    // widget test can never accidentally reach the network. This file is the one place that
    // deliberately does, so it opts out — and it has to happen before the first request, not
    // just before the first `testWidgets`, since initialising the binding at all is what
    // installs the override.
    TestWidgetsFlutterBinding.ensureInitialized();
    HttpOverrides.global = null;

    auth = AuthController(config: config);
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

    repository = LiveServalRepository(auth: auth, config: config);
    await repository.start();
  });

  tearDownAll(() {
    repository.dispose();
    auth.dispose();
  });

  test('reads the camera registry', () {
    final cameras = repository.cameraRecords();
    expect(cameras, isNotEmpty, reason: 'no cameras at ${config.baseUrl}');

    for (final camera in cameras) {
      // Every camera the Server accepted satisfies the role rules, so the client-side mirror of
      // them must agree — if it does not, the settings form would refuse to save a camera that
      // is already registered and working.
      expect(
        camera.roleProblem,
        isNull,
        reason: 'the client role check rejects live camera "${camera.id}"',
      );
    }
  });

  test('the view model is built from the registry', () {
    final cameras = repository.cameras();
    expect(cameras, isNotEmpty);
    expect(cameras.first.name, isNotEmpty);
    expect(
      cameras.map((c) => c.id),
      repository.cameraRecords().map((r) => r.id),
    );
  });

  test('the wall socket delivers frames', () async {
    // The Server paints every camera's latest frame on connect, then ~1 fps. Ten seconds is
    // several intervals of slack for a cold connection.
    final deadline = DateTime.now().add(const Duration(seconds: 10));
    final id = repository.cameraRecords().first.id;

    while (repository.snapshotFor(id) == null &&
        DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(const Duration(milliseconds: 250));
    }

    final frame = repository.snapshotFor(id);
    expect(frame, isNotNull, reason: 'no frame for "$id" on WS /api/dashboard');
    // A JPEG starts FF D8 — proof the length prefix was stripped at the right offset rather
    // than the image merely being non-empty.
    expect(frame!.take(2), [0xFF, 0xD8]);
    expect(repository.connected, isTrue);
  });

  test('telemetry history reaches the activity column', () {
    final items = repository.activityFor();
    expect(items, isNotEmpty, reason: 'no telemetry in the last 24h');

    // Newest first, and every row joined to a camera name rather than showing a raw id.
    for (var i = 1; i < items.length; i++) {
      expect(items[i - 1].id, isNot(items[i].id));
    }
    expect(items.first.cameraName, isNotEmpty);
    expect(items.first.text.trim(), isNotEmpty);
    expect(items.first.timeLabel, isNotEmpty);
  });

  test('scene descriptions become summaries and scrubber marks', () async {
    final id = repository.cameraRecords().first.id;

    final summary = repository.summaryFor(id);
    if (summary != null) expect(summary.text.trim(), isNotEmpty);

    // The scrubber's window is fetched behind a synchronous read, so the first call primes it and
    // comes back loading. That is the interface's own contract, not a quirk of this test.
    expect(repository.timelineFor(id, TimelineRange.day).loading, isTrue);
    await Future<void>.delayed(const Duration(seconds: 3));

    final window = repository.timelineFor(id, TimelineRange.day);
    expect(
      window.loading,
      isFalse,
      reason: 'the timeline window never arrived',
    );
    expect(window.span, const Duration(hours: 24));

    for (final mark in window.marks) {
      expect(window.positionOf(mark.at), inInclusiveRange(0, 1));
      expect(mark.ran, greaterThanOrEqualTo(Duration.zero));
    }

    // Coverage comes from `GET /api/cameras/{id}/coverage`. An empty list is a legitimate answer
    // — a camera can be registered and not recording — but a *failed* read leaves the window
    // loading, which the assertion above catches. What is checked here is that every span the
    // Server did return is well formed and inside the window it was asked for.
    for (final span in window.coverage) {
      expect(
        span.to.isAfter(span.from),
        isTrue,
        reason: 'a coverage span runs backwards',
      );
      expect(
        span.from.isBefore(window.from),
        isFalse,
        reason: 'coverage starts before the window',
      );
      expect(
        span.to.isAfter(window.to),
        isFalse,
        reason: 'coverage runs past the window',
      );
    }
  });

  test('a recorded window has a VOD playlist to play', () async {
    final id = repository.cameraRecords().first.id;

    // Prime, then read: the first call starts the fetch and returns the empty window.
    repository.timelineFor(id, TimelineRange.day);
    await Future<void>.delayed(const Duration(seconds: 3));

    final window = repository.timelineFor(id, TimelineRange.day);
    if (window.coverage.isEmpty) {
      markTestSkipped('no recorded footage on "$id" in the last 24 h');
      return;
    }

    // The bounded window the replay controller opens: fifteen minutes, ~225 segments, rather than
    // a day's ~21,600. Fetched here rather than played, because a playlist that parses is what
    // this file can check without a decoder.
    final span = window.coverage.last;
    final from = span.from;
    final to = span.to.difference(from) > const Duration(minutes: 15)
        ? from.add(const Duration(minutes: 15))
        : span.to;

    final baseUrl = repository.vodUrlFor(id, from: from, to: to);
    expect(baseUrl, isNotNull);

    // Fetched with a raw HttpClient rather than through ServalApi, on purpose — this is exactly
    // the path the video player takes, which cannot carry an Authorization header and so needs
    // the scoped stream token as a query parameter instead. See MediaEndpoints.cs's MediaAccess
    // policy and ReplayController._openWindow, which this mirrors.
    final streamToken = await auth.mintStreamToken();
    expect(streamToken, isNotNull);
    final url = baseUrl!.replace(
      queryParameters: {
        ...baseUrl.queryParameters,
        'stream_token': streamToken!,
      },
    );

    final response = await HttpClient()
        .getUrl(url)
        .then((request) => request.close());
    expect(
      response.statusCode,
      200,
      reason: 'no playlist for footage the Server said exists',
    );

    final playlist = await response.transform(const Utf8Decoder()).join();
    expect(playlist, contains('#EXT-X-PLAYLIST-TYPE:VOD'));
    expect(playlist, contains('#EXT-X-MAP:'));
    expect(
      RegExp('#EXTINF:').allMatches(playlist).length,
      greaterThan(0),
      reason: 'the playlist lists no segments',
    );

    // And then actually fetch what the playlist points at, which is where this used to fall over.
    // A player resolves these relative URIs against the playlist's own URL, and RFC 3986 drops
    // that URL's query when it does — so a token carried only on the playlist request buys one
    // authorised fetch and no more, and every segment 401s. Checking that the playlist parses is
    // not the same as checking it can be played, and the difference is a whole broken feature.
    final initUri = RegExp(
      '#EXT-X-MAP:URI="([^"]+)"',
    ).firstMatch(playlist)?.group(1);
    final firstSegment = LineSplitter.split(
      playlist,
    ).firstWhere((line) => line.isNotEmpty && !line.startsWith('#'));

    for (final entry in <String>[?initUri, firstSegment]) {
      final resolved = url.resolve(entry);
      final fetched = await HttpClient()
          .getUrl(resolved)
          .then((request) => request.close());
      await fetched.drain<void>();

      expect(
        fetched.statusCode,
        200,
        reason: 'the player cannot fetch $entry as the playlist wrote it',
      );
    }
  });

  test(
    'every camera reports what its PTZ can actually do',
    () async {
      for (final camera in repository.cameras()) {
        // Self-priming: the first read kicks the probe and returns "probing".
        expect(repository.ptzProbeFor(camera.id), isA<PtzProbe>());
      }

      // One ONVIF round trip per camera, plus slack for an unresponsive one.
      await Future<void>.delayed(const Duration(seconds: 12));

      for (final camera in repository.cameras()) {
        final probe = repository.ptzProbeFor(camera.id);
        expect(
          probe,
          isNot(isA<PtzProbing>()),
          reason: '${camera.id} should have settled by now',
        );

        switch (probe) {
          case PtzKnown(:final panTilt, :final zoom, :final presets):
            // The whole point: a camera that pans but does not zoom must say so, rather than being
            // drawn from "an ONVIF URL is set".
            debugPrint(
              '${camera.id}: panTilt=$panTilt zoom=$zoom presets=${presets.length}',
            );
            for (final preset in presets) {
              expect(
                preset.token,
                isNotEmpty,
                reason: 'a preset with no token cannot be recalled',
              );
            }
          case PtzUnknown(:final reason):
            debugPrint('${camera.id}: unavailable — $reason');
          case PtzNotConfigured():
            expect(
              repository.cameraRecordById(camera.id)?.ptzConfigured,
              isFalse,
              reason:
                  'only a camera with no ONVIF endpoint should read as unconfigured',
            );
          case PtzProbing():
            fail('unreachable — asserted above');
        }
      }
    },
    timeout: const Timeout(Duration(seconds: 60)),
  );

  test(
    'a camera says what make and model it is',
    () async {
      final withOnvif = repository
          .cameraRecords()
          .where((record) => record.ptzConfigured)
          .toList();

      if (withOnvif.isEmpty) {
        markTestSkipped('no camera on this Server has an ONVIF endpoint');
        return;
      }

      for (final record in withOnvif) {
        repository.deviceInformationFor(record.id);
      }
      await Future<void>.delayed(const Duration(seconds: 8));

      // At least one should have answered. Every field is optional in ONVIF, so this asserts the
      // shape rather than any particular field being present.
      final answered = [
        for (final record in withOnvif)
          ?repository.deviceInformationFor(record.id),
      ];

      expect(
        answered,
        isNotEmpty,
        reason: 'no camera answered GetDeviceInformation',
      );
      for (final info in answered) {
        debugPrint(
          'device: ${info.productLabel} · firmware ${info.firmwareVersion}',
        );
      }
    },
    timeout: const Timeout(Duration(seconds: 60)),
  );

  test(
    'a recorded window exports as a clip, and says what it covers',
    () async {
      final camera = repository.cameras().first;
      final to = DateTime.now().subtract(const Duration(minutes: 1));
      final from = to.subtract(const Duration(seconds: 20));

      final coverage = await repository.api.coverage(
        camera.id,
        from: from,
        to: to,
      );
      if (coverage.isEmpty) {
        markTestSkipped('${camera.id} recorded nothing in the last minute');
        return;
      }

      final download = await repository.api.openMedia(
        repository.api.clipUrl(camera.id, from: from, to: to),
        fallbackName: 'fallback.mp4',
      );

      // Drained rather than saved: this test writes nothing to the machine it runs on.
      var bytes = 0;
      await for (final chunk in download.stream) {
        bytes += chunk.length;
      }

      expect(
        bytes,
        greaterThan(0),
        reason: 'an exported clip should have a body',
      );
      expect(
        download.fileName,
        endsWith('.mp4'),
        reason: 'the Server names the file in Content-Disposition',
      );

      // Only readable at all because the Server exposes these through CORS; on the VM they always
      // arrive, so this pins that the route sets them.
      expect(download.from, isNotNull, reason: 'X-Serval-Clip-From');
      expect(download.to, isNotNull, reason: 'X-Serval-Clip-To');
      expect(
        download.from!.isBefore(download.to!),
        isTrue,
        reason: 'the reported window should run forwards',
      );
      debugPrint(
        'clip: $bytes bytes, ${download.covered?.inSeconds} s, truncated=${download.truncated}',
      );
    },
    timeout: const Timeout(Duration(seconds: 90)),
  );

  group('the screens render this Server’s own data', () {
    // The widget tests cover layout against the sample content. These cover it against whatever
    // is actually registered — a camera with two streams and split roles lays out differently
    // from the one-stream sample, and a real ONVIF url turns on a section the sample does not.
    setUp(() {
      final view = TestWidgetsFlutterBinding.ensureInitialized()
          .platformDispatcher
          .views
          .first;
      view.devicePixelRatio = 1.0;
      view.physicalSize = const Size(1440, 900);
      addTearDown(() {
        view.resetPhysicalSize();
        view.resetDevicePixelRatio();
      });
    });

    // The screens read the repository from the container now, so this supplies the override
    // `ServalApp` would have — with the live repository these tests just signed in against.
    Widget harness(Widget child) => ProviderScope(
      overrides: [repositoryProvider.overrideWithValue(repository)],
      child: MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        home: Scaffold(body: child),
      ),
    );

    testWidgets('the wall', (tester) async {
      await tester.pumpWidget(harness(WallScreen(onOpenCamera: (_, _) {})));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      for (final camera in repository.cameraRecords()) {
        expect(find.text(camera.name), findsWidgets, reason: camera.id);
      }
    });

    testWidgets('the cameras screen, for every registered camera', (
      tester,
    ) async {
      await tester.pumpWidget(harness(const CamerasScreen()));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);

      for (final camera in repository.cameraRecords()) {
        await tester.tap(find.text(camera.name).last);
        await tester.pumpAndSettle();
        expect(
          tester.takeException(),
          isNull,
          reason: 'laying out ${camera.id}',
        );

        // Opened, not edited. If this ever fails, the form has changed something just by
        // rendering the record — which against a live registry is the bug that matters most.
        expect(
          find.text('Everything here matches the Server.'),
          findsOneWidget,
          reason: 'opening ${camera.id} should not dirty it',
        );
      }
    });
  });
}
