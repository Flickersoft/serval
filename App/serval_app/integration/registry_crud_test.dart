import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/auth/auth_controller.dart';
import 'package:serval_app/data/auth/authenticated_client.dart';
import 'package:serval_app/data/camera_record.dart';
import 'package:serval_app/data/serval_api.dart';
import 'package:serval_app/data/serval_config.dart';

/// Registry writes, against a throwaway camera.
///
/// **Deliberately outside `test/`** so `flutter test` never runs it. By hand, against **a Server
/// you started yourself** — `Docs/testing.md` has the throwaway-database invocation. This one
/// writes, so a live NVR is out: the registry has no undo, and there is no version of "it only
/// touches one id" that makes a registry someone depends on a safe place to find that out.
///
/// Needs an **Admin** account. Camera reads take any signed-in account, but every write —
/// create, update, delete — is Admin-only, so a Viewer's credentials fail here on the first POST.
///
/// ```bash
/// flutter test integration/registry_crud_test.dart \
///   --dart-define=SERVAL_BASE_URL=http://127.0.0.1:5211 \
///   --dart-define=SERVAL_USERNAME=admin --dart-define=SERVAL_PASSWORD=...
/// ```
///
/// Two rules this obeys, and they are not incidental:
///
///  * **It only ever touches [testId].** Every real camera on the Server is left alone — no
///    read-modify-write, no delete, nothing. A registry with no undo is not a place to be casual.
///  * **The camera is created disabled.** A disabled camera is registered but not ingested, so
///    the ingest manager never starts an ffmpeg against the fake source and never enters a
///    retry loop on somebody's NVR.
///
/// It exercises the exact calls the settings screen makes, so a green run here means the screen
/// can add, edit and remove a camera against this Server.
void main() {
  final config = ServalConfig.fromEnvironment();
  late AuthController auth;
  late ServalApi api;

  const testId = 'serval-app-crud-check';

  Future<void> removeIfPresent() async {
    try {
      await api.deleteCamera(testId);
    } on ServalApiException {
      // Not there, which is the normal case.
    }
  }

  setUpAll(() async {
    // `flutter_test`'s binding installs an HttpOverrides that fails every request, so a widget
    // test can never accidentally reach the network. This file deliberately does, so it opts out —
    // before the first request, since initialising the binding at all is what installs it.
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

    api = ServalApi(
      config: config,
      client: AuthenticatedClient(auth: auth),
    );
  });

  setUp(removeIfPresent);
  tearDownAll(() async {
    await removeIfPresent();
    api.close();
    auth.dispose();
  });

  test('creates, edits and removes a camera', () async {
    final before = await api.listCameras();
    expect(
      before.map((c) => c.id),
      isNot(contains(testId)),
      reason: 'the throwaway camera should not exist yet',
    );

    // ---- create -----------------------------------------------------------
    final created = await api.createCamera(
      CameraRecord.blank().copyWith(
        id: testId,
        name: 'CRUD check',
        location: 'Nowhere',
        // Registered but never pulled. Nothing starts because of this test.
        enabled: false,
        retentionDays: 3,
        streams: [
          CameraRecord.blank().streams.single.copyWith(
            url: 'rtsp://127.0.0.1:1/serval-app-crud-check',
          ),
        ],
      ),
    );

    expect(created.id, testId);
    expect(created.name, 'CRUD check');
    expect(created.enabled, isFalse);
    expect(created.streams.single.roles, hasLength(3));

    // ---- edit -------------------------------------------------------------
    // A whole-record PUT, which is what the settings screen sends: the Server replaces rather
    // than merges, so anything not sent would be lost.
    final edited = await api.updateCamera(
      created.copyWith(
        name: 'CRUD check, renamed',
        retentionDays: 14,
        recordAudio: true,
        aiVision: true,
        audioTuning: const AudioTuningSettings(
          speechGateRmsThreshold: 0.0015,
          vadThreshold: 0.7,
          soundGateRmsThreshold: 0.002,
        ),
        onvifUrl: 'http://127.0.0.1/onvif/device_service',
        onvifUsername: 'someone',
        onvifPassword: 'a-secret',
        streams: [
          const CameraStreamRecord(
            name: 'main',
            url: 'rtsp://127.0.0.1:1/main',
            roles: [StreamRole.record, StreamRole.live],
          ),
          const CameraStreamRecord(
            name: 'sub',
            url: 'rtsp://127.0.0.1:1/sub',
            roles: [StreamRole.detect],
          ),
        ],
      ),
    );
    expect(edited.name, 'CRUD check, renamed');

    // Read back rather than trusting the response, since the round trip through Mongo is the
    // part that could drop a field.
    final reread = await api.getCamera(testId);
    expect(reread.name, 'CRUD check, renamed');
    expect(reread.retentionDays, 14);
    expect(reread.recordAudio, isTrue);
    expect(reread.aiVision, isTrue);
    expect(reread.aiAudio, isFalse);

    // The nested object is the one most likely to be lost in transit: it is optional on the
    // Server, omitted from the document when null, and PUT replaces rather than merges.
    expect(reread.audioTuning?.speechGateRmsThreshold, closeTo(0.0015, 1e-9));
    expect(reread.audioTuning?.vadThreshold, closeTo(0.7, 1e-9));
    expect(reread.audioTuning?.soundGateRmsThreshold, closeTo(0.002, 1e-9));
    expect(reread.streams, hasLength(2));
    expect(reread.streamFor(StreamRole.record)!.name, 'main');
    expect(reread.streamFor(StreamRole.detect)!.name, 'sub');
    expect(reread.streamFor(StreamRole.live)!.name, 'main');
    expect(reread.ptzConfigured, isTrue);

    // The password survived a PUT that was not about the password. This is the whole reason the
    // form keeps the fetched value instead of showing an empty field.
    expect(reread.onvifPassword, 'a-secret');

    // ---- delete -----------------------------------------------------------
    await api.deleteCamera(testId);
    final after = await api.listCameras();
    expect(after.map((c) => c.id), isNot(contains(testId)));
  });

  test('a camera the Server would refuse comes back with its reason', () async {
    // The client mirrors these rules so the form can disable Save — but the mirror is only worth
    // having if the Server really does refuse, and really does say why.
    final broken = CameraRecord.blank().copyWith(
      id: testId,
      name: 'CRUD check',
      enabled: false,
      streams: [
        const CameraStreamRecord(
          name: 'main',
          url: 'rtsp://127.0.0.1:1/main',
          roles: [StreamRole.record],
        ),
      ],
    );

    expect(
      broken.roleProblem,
      isNotNull,
      reason: 'the client should catch this first',
    );

    await expectLater(
      api.createCamera(broken),
      throwsA(
        isA<ServalApiException>()
            .having((e) => e.statusCode, 'statusCode', 400)
            .having((e) => e.message, 'message', isNotEmpty),
      ),
    );
  });
}
