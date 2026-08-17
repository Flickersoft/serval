import 'dart:convert';

import 'package:http/http.dart' as http;

import '../models/alert.dart';
import '../models/camera.dart';
import '../models/clip_selection.dart';
import '../models/config_backup.dart';
import '../models/ptz.dart';
import '../models/push.dart';
import '../models/saved_clip.dart';
import '../models/server_settings.dart';
import '../models/system_stats.dart';
import '../models/timeline.dart';
import '../models/user_preferences.dart';
import '../models/vitals_history.dart';
import 'auth/auth_models.dart' show Role;
import 'auth/user_account.dart';
import 'camera_record.dart';
import 'serval_config.dart';
import 'telemetry_documents.dart';

/// A request the Server refused, carrying what it said.
///
/// The Server's 400s are written to be read by a person — they name the missing encoder, the
/// unassigned role, the rejected codec — so the settings screen shows [message] verbatim rather
/// than replacing it with something vaguer.
class ServalApiException implements Exception {
  ServalApiException(this.statusCode, this.message);

  final int statusCode;
  final String message;

  @override
  String toString() => message;
}

/// Everything the App fetches from, or writes to, the Server over HTTP.
///
/// The two push feeds are elsewhere — see `dashboard_socket.dart` and `events_socket.dart` — so
/// this is only the request/response half.
class ServalApi {
  ServalApi({required this.config, http.Client? client})
    : _client = client ?? http.Client();

  final ServalConfig config;
  final http.Client _client;

  void close() => _client.close();

  // ---------------------------------------------------------------- registry

  Future<List<CameraRecord>> listCameras() async {
    final body = await _getJson('/api/cameras');
    return [
      for (final camera in body as List<dynamic>)
        CameraRecord.fromJson(camera as Map<String, dynamic>),
    ];
  }

  Future<CameraRecord> getCamera(String id) async => CameraRecord.fromJson(
    await _getJson('/api/cameras/$id') as Map<String, dynamic>,
  );

  Future<CameraRecord> createCamera(CameraRecord camera) async {
    final response = await _client.post(
      config.resolve('/api/cameras'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode(camera.toJson()),
    );
    return CameraRecord.fromJson(_decode(response) as Map<String, dynamic>);
  }

  /// Replaces the camera outright — the Server does not merge, so [camera] must be complete.
  /// The URL is authoritative for the id.
  Future<CameraRecord> updateCamera(CameraRecord camera) async {
    final response = await _client.put(
      config.resolve('/api/cameras/${camera.id}'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode(camera.toJson()),
    );
    return CameraRecord.fromJson(_decode(response) as Map<String, dynamic>);
  }

  /// Unregisters the camera and stops its stream. Recorded media on disk is left alone; the
  /// retention sweep ages it out.
  Future<void> deleteCamera(String id) async {
    final response = await _client.delete(config.resolve('/api/cameras/$id'));
    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  // -------------------------------------------------------------------- users
  //
  // Admin-only accounts, via `Server/Serval.Server/Auth/AuthEndpoints.cs`'s `/api/auth/users`
  // group. These live on this class rather than `AuthApi` because they require the Bearer token
  // `AuthenticatedClient` attaches — `AuthApi` is deliberately for the anonymous login/refresh
  // routes only.

  Future<List<UserAccount>> listUsers() async {
    final body = await _getJson('/api/auth/users');
    return [
      for (final user in body as List<dynamic>)
        UserAccount.fromJson(user as Map<String, dynamic>),
    ];
  }

  Future<UserAccount> createUser({
    required String username,
    required String displayName,
    required String password,
    required Role role,
  }) async {
    final response = await _client.post(
      config.resolve('/api/auth/users'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        'username': username,
        'displayName': displayName,
        'password': password,
        'role': role.name,
      }),
    );
    return UserAccount.fromJson(_decode(response) as Map<String, dynamic>);
  }

  /// Changes an existing account's role and/or password. Only the fields passed are sent, so
  /// leaving one null leaves it unchanged rather than clearing it.
  ///
  /// [signOutAllSessions] ends every session that account currently holds. It is worth sending
  /// whenever the password is being changed because someone else may know the old one — a reset
  /// alone leaves their refresh token working for another 30 days.
  Future<UserAccount> updateUser(
    String username, {
    Role? role,
    String? password,
    bool signOutAllSessions = false,
  }) async {
    final response = await _client.put(
      config.resolve('/api/auth/users/$username'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        if (role != null) 'role': role.name,
        'password': ?password,
        if (signOutAllSessions) 'signOutAllSessions': true,
      }),
    );
    return UserAccount.fromJson(_decode(response) as Map<String, dynamic>);
  }

  /// Ends every session for one account, without touching its password. What to reach for when an
  /// account is thought to be signed in somewhere it shouldn't be.
  Future<void> signOutUser(String username) async {
    final response = await _client.post(
      config.resolve('/api/auth/users/$username/sign-out'),
    );
    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  /// Changes the signed-in account's own password. The Server verifies [currentPassword] and
  /// answers 401 if it is wrong, so a stolen access token cannot be turned into a permanent
  /// takeover.
  ///
  /// Not on `AuthApi` for the same reason the user routes are not: this one needs the Bearer token.
  Future<void> changeOwnPassword({
    required String currentPassword,
    required String newPassword,
    bool signOutAllSessions = false,
  }) async {
    final response = await _client.put(
      config.resolve('/api/auth/password'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        'currentPassword': currentPassword,
        'newPassword': newPassword,
        if (signOutAllSessions) 'signOutAllSessions': true,
      }),
    );
    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  /// Deletes an account. The Server refuses (400) to delete the last remaining Admin.
  Future<void> deleteUser(String username) async {
    final response = await _client.delete(
      config.resolve('/api/auth/users/$username'),
    );
    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  // ---------------------------------------------------------------- settings
  //
  // The settings that are not about any one camera, via
  // `Server/Serval.Server/Configuration/SettingsEndpoints.cs`. Reading is open to anyone signed
  // in; writing is Admin-only, and the Server enforces that rather than the App hiding the button.

  Future<ServerSettings> settings() async => ServerSettings.fromJson(
    await _getJson('/api/settings') as Map<String, dynamic>,
  );

  /// Applies changes and returns the whole catalogue as it now stands.
  ///
  /// A null value **resets** that setting — it removes the stored override so the value falls back
  /// to whatever the deployment says. That is the only way to un-set one, and it is why [changes]
  /// is a map of nullable values rather than of strings: an empty string is a value, and would pin
  /// the setting to being empty.
  ///
  /// The response is the new state rather than an acknowledgement, so the screen never has to
  /// guess what a change did to `source`, to the reset values, or to the restart banner.
  Future<ServerSettings> updateSettings(Map<String, Object?> changes) async {
    final response = await _client.put(
      config.resolve('/api/settings'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode(changes),
    );
    return ServerSettings.fromJson(_decode(response) as Map<String, dynamic>);
  }

  // ------------------------------------------------- configuration backup
  //
  // The whole configuration in and out of a file, via
  // `Server/Serval.Server/Backup/ConfigBackupEndpoints.cs`. Admin-only on both sides.

  /// Where the backup file comes from. Fetched through [openMedia] like a clip, so it reaches a
  /// [MediaSaver] as a stream and the Server's chosen filename comes back in the headers.
  Uri configBackupUrl() => config.resolve('/api/config/backup');

  /// Merges a backup file into the Server and reports what that came to.
  ///
  /// [bytes] go up exactly as they were read — no decode, no re-encode. The file is JSON that the
  /// Server wrote, and re-encoding it here would put this App's idea of the text between the file
  /// and the parser that has to read it.
  Future<ConfigRestoreResult> restoreConfig(List<int> bytes) async {
    final response = await _client.post(
      config.resolve('/api/config/restore'),
      headers: const {'Content-Type': 'application/json'},
      body: bytes,
    );
    return ConfigRestoreResult.fromJson(
      _decode(response) as Map<String, dynamic>,
    );
  }

  // ------------------------------------------------------------- preferences
  //
  // This account's own state, via `Server/Serval.Server/Preferences/PreferencesEndpoints.cs`.
  // Distinct from `/api/settings` in every way that matters: that one is the deployment's
  // configuration and is Admin-only to write, this one is the signed-in person's own and takes
  // any role. There is no username in the route — the Server reads it off the token — so there is
  // no request shape that reaches somebody else's wall.

  Future<UserPreferences> preferences() async => UserPreferences.fromJson(
    await _getJson('/api/preferences') as Map<String, dynamic>,
  );

  /// Saves the wall arrangement and returns the preferences as they now stand.
  ///
  /// The Server merges rather than replaces, so this sends only `wallLayout` and leaves every other
  /// preference — including ones this build has never heard of — untouched. An empty list is not
  /// the same as omitting it: the list clears the arrangement so the wall falls back to its default
  /// packing, which is what makes a reset expressible.
  Future<UserPreferences> saveWallLayout(List<TileLayout> layout) async {
    final response = await _client.put(
      config.resolve('/api/preferences'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        'wallLayout': [
          for (final tile in layout)
            {
              'cameraId': tile.cameraId,
              'column': tile.column,
              'row': tile.row,
              'columnSpan': tile.columnSpan,
              'rowSpan': tile.rowSpan,
            },
        ],
      }),
    );
    return UserPreferences.fromJson(_decode(response) as Map<String, dynamic>);
  }

  /// Saves this account's notification preferences and returns them as they now stand.
  ///
  /// Sends only what it is changing, for the same reason [saveWallLayout] does — the Server merges,
  /// so a build that predates a preference cannot erase it. Either argument may be omitted to leave
  /// that half alone.
  ///
  /// A rule whose `objectClasses` or `soundLabels` is null is sent as null on purpose: null means
  /// *inherit everything this camera alerts on*, and it is not the same as an empty list.
  Future<UserPreferences> saveNotificationPreferences({
    bool? enabled,
    List<CameraNotificationRule>? rules,
  }) async {
    final response = await _client.put(
      config.resolve('/api/preferences'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        'notificationsEnabled': ?enabled,
        if (rules != null)
          'notifications': [for (final rule in rules) rule.toJson()],
      }),
    );
    return UserPreferences.fromJson(_decode(response) as Map<String, dynamic>);
  }

  // -------------------------------------------------------------------- push
  //
  // Registering a browser for alert notifications, via
  // `Server/Serval.Server/Push/PushEndpoints.cs`. Delivery only — *which* alerts reach this person
  // is a preference, above, because it belongs to the account rather than to one of their browsers.

  Future<PushConfig> pushConfig() async => PushConfig.fromJson(
    await _getJson('/api/push/config') as Map<String, dynamic>,
  );

  Future<List<PushDevice>> pushDevices() async => [
    for (final device
        in await _getJson('/api/push/subscriptions') as List<dynamic>)
      PushDevice.fromJson(device as Map<String, dynamic>),
  ];

  /// Registers this browser, or refreshes what the Server holds for it.
  ///
  /// Called on every launch, not only when somebody turns notifications on: the row is keyed by a
  /// hash of the endpoint, so this overwrites rather than accumulates, and it is how a browser
  /// quietly reissuing its subscription gets noticed.
  Future<PushDevice> registerPushDevice({
    required String endpoint,
    required String p256dh,
    required String auth,
  }) async {
    final response = await _client.post(
      config.resolve('/api/push/subscriptions'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        'endpoint': endpoint,
        'keys': {'p256dh': p256dh, 'auth': auth},
      }),
    );
    return PushDevice.fromJson(_decode(response) as Map<String, dynamic>);
  }

  /// Stops the Server notifying one browser. Scoped to this account by the Server.
  ///
  /// The id names the device, and comes either from the list or from what [registerPushDevice]
  /// returned — which is how this browser learns its own. Deliberately not a DELETE with a body:
  /// minimal APIs refuse to infer one, and a body on DELETE is not reliably forwarded by proxies.
  Future<void> unregisterPushDevice(String deviceId) async {
    final response = await _client.delete(
      config.resolve(
        '/api/push/subscriptions/${Uri.encodeComponent(deviceId)}',
      ),
    );
    _decode(response);
  }

  /// Sends a test notification to every device this account has registered.
  ///
  /// The only practical way to tell a working setup from a silent one: every failure mode here —
  /// no TLS, a permission revoked in the browser, a stale subscription — looks identical from the
  /// screen, which is to say like nothing at all.
  Future<PushTestResult> sendTestNotification() async {
    final response = await _client.post(
      config.resolve('/api/push/test'),
      headers: const {'Content-Type': 'application/json'},
    );
    return PushTestResult.fromJson(_decode(response) as Map<String, dynamic>);
  }

  // --------------------------------------------------------------- telemetry

  Future<List<UtteranceDocument>> utterances(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    int limit = 200,
  }) => _telemetry(
    'utterances',
    cameraId,
    from,
    to,
    limit,
    UtteranceDocument.fromJson,
  );

  Future<List<SceneDocument>> scenes(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    int limit = 200,
  }) => _telemetry('scenes', cameraId, from, to, limit, SceneDocument.fromJson);

  Future<List<SoundDocument>> sounds(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    int limit = 200,
  }) => _telemetry('sounds', cameraId, from, to, limit, SoundDocument.fromJson);

  /// Object-detection episodes. The Server returns anything *present* during the window rather
  /// than anything that started in it, so a car parked since before `from` is included.
  Future<List<DetectionDocument>> detections(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    int limit = 200,
  }) => _telemetry(
    'detections',
    cameraId,
    from,
    to,
    limit,
    DetectionDocument.fromJson,
  );

  Future<List<ConversationTranscriptDocument>> conversationTranscripts(
    String cameraId, {
    required DateTime from,
    required DateTime to,
    int limit = 200,
  }) => _telemetry(
    'conversation-transcripts',
    cameraId,
    from,
    to,
    limit,
    ConversationTranscriptDocument.fromJson,
  );

  Future<List<T>> _telemetry<T>(
    String route,
    String cameraId,
    DateTime from,
    DateTime to,
    int limit,
    T Function(Map<String, dynamic>) parse,
  ) async {
    final body = await _getJson('/api/cameras/$cameraId/$route', {
      'from': from.toUtc().toIso8601String(),
      'to': to.toUtc().toIso8601String(),
      'limit': '$limit',
    });

    return [
      for (final document in body as List<dynamic>)
        parse(document as Map<String, dynamic>),
    ];
  }

  // -------------------------------------------------------------------- ptz

  /// Start a continuous move. Velocities are −1..1 (positive pan = right, tilt = up, zoom = in)
  /// and clamped server-side.
  ///
  /// Every move also carries an ONVIF auto-stop after ~1 s, so a held button has to re-send
  /// inside that window — which `PtzPad` already does on its own repeat.
  Future<void> ptzMove(
    String cameraId, {
    double pan = 0,
    double tilt = 0,
    double zoom = 0,
  }) => _postNoContent('/api/cameras/$cameraId/ptz/move', {
    'pan': pan,
    'tilt': tilt,
    'zoom': zoom,
  });

  Future<void> ptzStop(String cameraId) =>
      _postNoContent('/api/cameras/$cameraId/ptz/stop', null);

  /// Drive zoom to a position, 0..1 of the lens's travel. Only for cameras whose capabilities
  /// report `absoluteZoom`; the rest are nudged with [ptzMove] and read back with [ptzStatus].
  Future<void> ptzZoomTo(String cameraId, double position) =>
      _postNoContent('/api/cameras/$cameraId/ptz/zoom', {'position': position});

  /// Where the camera says its lens is, or null for an axis it does not report.
  ///
  /// A live ONVIF round trip, so this is asked on opening a camera and again once a zoom drag
  /// settles — not on a timer. Its own route rather than a field on `/capabilities` because that
  /// one is cached for minutes and this one is worthless the moment it is stale.
  Future<ZoomPosition?> ptzStatus(String cameraId) async {
    final body =
        await _getJson('/api/cameras/$cameraId/ptz/status')
            as Map<String, dynamic>;

    final zoom = (body['zoom'] as num?)?.toDouble();

    // Null is the camera declining to say, which is a different thing from zero and has to stay
    // distinguishable all the way to the slider. See [ZoomPosition].
    return zoom == null ? null : ZoomPosition.measured(zoom.clamp(0.0, 1.0));
  }

  Future<void> ptzPreset(String cameraId, String preset) =>
      _postNoContent('/api/cameras/$cameraId/ptz/preset', {'preset': preset});

  /// Recall the camera's stored home position, for cameras whose capabilities report one.
  Future<void> ptzHome(String cameraId) =>
      _postNoContent('/api/cameras/$cameraId/ptz/home', null);

  /// What this camera's PTZ can actually do, asked of the camera itself.
  ///
  /// Its own route rather than a field on the camera record, because probing is a live SOAP round
  /// trip: folding it into `GET /api/cameras` — which `main` awaits before the first frame — would
  /// put an ONVIF timeout per unreachable camera on every cold start.
  ///
  /// All-false is a real answer, not a failure. A fixed camera with an ONVIF endpoint reports
  /// exactly that, and the Server returns 200 for it rather than an error.
  Future<PtzKnown> ptzCapabilities(String cameraId) async {
    final body =
        await _getJson('/api/cameras/$cameraId/ptz/capabilities')
            as Map<String, dynamic>;

    return PtzKnown(
      panTilt: body['panTilt'] as bool? ?? false,
      zoom: body['zoom'] as bool? ?? false,
      absoluteZoom: body['absoluteZoom'] as bool? ?? false,
      home: body['home'] as bool? ?? false,
      maximumPresets: body['maximumPresets'] as int?,
      presets: [
        for (final preset in (body['presets'] as List<dynamic>? ?? const []))
          PtzPreset(
            (preset as Map<String, dynamic>)['token'] as String,
            preset['name'] as String?,
          ),
      ],
    );
  }

  /// What the Server can say about itself — processor, memory, GPU and the media volume.
  ///
  /// A plain read rather than a fourth socket: the Server samples on its own timer regardless
  /// (a CPU percentage is a delta between two samples), so this route serves the last one from
  /// memory and a socket would buy only a session to keep alive.
  Future<SystemStats> systemStats() async => SystemStats.fromJson(
    await _getJson('/api/system/stats') as Map<String, dynamic>,
  );

  /// The retained samples behind the meters' sparklines.
  ///
  /// A second route rather than a fatter `/api/system/stats`, and the Server's own comment says
  /// why: `/stats` is polled by the dashboard wall for its disk banner, so an hour of samples
  /// folded into it would ride every wall poll to serve a settings page that is usually closed.
  Future<VitalsHistory> vitalsHistory() async => VitalsHistory.fromJson(
    await _getJson('/api/system/stats/history') as Map<String, dynamic>,
  );

  /// Make, model and firmware, as the camera reports them — ONVIF `GetDeviceInformation`.
  Future<DeviceInformation> deviceInformation(String cameraId) async {
    final body =
        await _getJson('/api/cameras/$cameraId/device-information')
            as Map<String, dynamic>;

    return DeviceInformation(
      manufacturer: body['manufacturer'] as String?,
      model: body['model'] as String?,
      firmwareVersion: body['firmwareVersion'] as String?,
      serialNumber: body['serialNumber'] as String?,
      hardwareId: body['hardwareId'] as String?,
    );
  }

  Future<void> _postNoContent(String path, Map<String, dynamic>? body) async {
    final response = await _client.post(
      config.resolve(path),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode(body ?? const <String, dynamic>{}),
    );

    // 502 means the camera or its ONVIF service failed, not us — worth distinguishing, since the
    // fix is at the camera rather than in the request.
    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  // ----------------------------------------------------------------- webrtc

  /// Exchanges an SDP offer for the answer, proxied to the go2rtc sidecar.
  ///
  /// Raw `application/sdp` in both directions — not JSON. The media never touches the Server:
  /// once this returns, SRTP flows directly between this process and go2rtc.
  Future<String> exchangeSdp(String cameraId, String offer) async {
    final response = await _client.post(
      config.resolve('/api/cameras/$cameraId/webrtc'),
      headers: const {'Content-Type': 'application/sdp'},
      body: offer,
    );

    if (response.statusCode != 200) {
      throw ServalApiException(
        response.statusCode,
        switch (response.statusCode) {
          503 => 'The Server has WebRTC turned off (Serval:WebRtc:Enabled).',
          502 => 'go2rtc could not start this camera’s stream.',
          404 => 'This camera has no live stream go2rtc can serve.',
          _ => _errorMessage(response),
        },
      );
    }

    return response.body;
  }

  // ------------------------------------------------------------------ media

  /// The latest still, served from memory when ingest has published a frame. Used as the
  /// fallback body when the WebRTC view cannot start.
  Uri snapshotUrl(String cameraId) =>
      config.resolve('/api/cameras/$cameraId/snapshot.jpg');

  /// The contiguous runs of recorded footage in a window — what the scrubber draws as coverage.
  ///
  /// Not `/recordings`, which is the same information unmerged: one row per four-second segment,
  /// so a day is ~21,600 rows and megabytes of JSON. `/coverage` returns one span per ffmpeg
  /// session, typically a handful.
  Future<List<CoverageSpan>> coverage(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    final body = await _getJson('/api/cameras/$cameraId/coverage', {
      'from': from.toUtc().toIso8601String(),
      'to': to.toUtc().toIso8601String(),
    });

    return [
      for (final span in body as List<dynamic>)
        CoverageSpan(
          DateTime.parse(
            (span as Map<String, dynamic>)['from'] as String,
          ).toLocal(),
          DateTime.parse(span['to'] as String).toLocal(),
        ),
    ];
  }

  /// The segment index for a window — where a trim handle may land.
  ///
  /// The unmerged form `coverage` avoids, asked for deliberately: a segment is the smallest thing
  /// the Server can copy without re-encoding, so these boundaries are the only positions a clip can
  /// actually start and stop at. Affordable because clip mode asks for twelve minutes or an hour,
  /// not a day.
  Future<List<RecordedSegment>> recordedSegments(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    final body = await _getJson('/api/cameras/$cameraId/recordings', {
      'from': from.toUtc().toIso8601String(),
      'to': to.toUtc().toIso8601String(),
    });

    return [
      for (final segment in body as List<dynamic>)
        RecordedSegment.fromJson(segment as Map<String, dynamic>),
    ];
  }

  /// A standalone MP4 of a window — the *Download* destination, which keeps nothing server-side.
  Uri clipUrl(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) => config.resolve('/api/cameras/$cameraId/clip.mp4', {
    'from': from.toUtc().toIso8601String(),
    'to': to.toUtc().toIso8601String(),
  });

  // -------------------------------------------------------------------- clips
  //
  // Saved clips: `Server/Serval.Server/Clips/ClipEndpoints.cs`. A shared library — everyone sees
  // every clip — with owned edits, so a rename or a delete can come back 403 and the screens hide
  // what they know will.

  Future<List<SavedClip>> listClips({String? query, String? cameraId}) async {
    final body = await _getJson('/api/clips', {
      if (query != null && query.isNotEmpty) 'query': query,
      if (cameraId != null && cameraId.isNotEmpty) 'cameraId': cameraId,
    });

    return [
      for (final clip in body as List<dynamic>)
        SavedClip.fromJson(clip as Map<String, dynamic>),
    ];
  }

  Future<SavedClipDetail> getClip(String id) async => SavedClipDetail.fromJson(
    await _getJson('/api/clips/$id') as Map<String, dynamic>,
  );

  /// Asks the Server to keep a range, and returns the clip it accepted.
  ///
  /// Comes back as soon as the range is validated, not when the file exists — the clip is
  /// [ClipState.writing] until [clipProgress] says otherwise.
  Future<SavedClip> saveClipToServer({
    required String cameraId,
    required DateTime from,
    required DateTime to,
    required String name,
  }) async {
    final response = await _client.post(
      config.resolve('/api/clips'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({
        'cameraId': cameraId,
        'from': from.toUtc().toIso8601String(),
        'to': to.toUtc().toIso8601String(),
        'name': name,
      }),
    );

    return SavedClip.fromJson(_decode(response) as Map<String, dynamic>);
  }

  Future<ClipProgress> clipProgress(String id) async => ClipProgress.fromJson(
    await _getJson('/api/clips/$id/status') as Map<String, dynamic>,
  );

  Future<void> renameClip(String id, String name) async {
    final response = await _client.patch(
      config.resolve('/api/clips/$id'),
      headers: const {'Content-Type': 'application/json'},
      body: jsonEncode({'name': name}),
    );

    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  Future<void> deleteClip(String id) async {
    final response = await _client.delete(config.resolve('/api/clips/$id'));
    if (response.statusCode != 204) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
  }

  /// The saved clip's video, for playing and for downloading.
  ///
  /// Takes a stream token rather than the bearer header, like the HLS routes: a `<video>` element
  /// and libmpv are handed a URL and cannot set one.
  Uri savedClipUrl(String id, {String? streamToken}) =>
      config.resolve('/api/clips/$id/clip.mp4', _streamQuery(streamToken));

  Uri clipPosterUrl(String id, {String? streamToken}) =>
      config.resolve('/api/clips/$id/poster.jpg', _streamQuery(streamToken));

  static Map<String, String>? _streamQuery(String? streamToken) =>
      streamToken == null ? null : {'stream_token': streamToken};

  // ------------------------------------------------------------------- alerts
  //
  // The alert queue: `Server/Serval.Server/Alerts/AlertEndpoints.cs`. Cross-camera by default,
  // which is the difference from every telemetry route — an alert queue you have to already know
  // which camera to look at is not being told about anything. Read and dismissed belong to the
  // alert rather than to the account, because the footage is the household's.

  Future<AlertQueue> listAlerts({String? cameraId, DateTime? before}) async =>
      AlertQueue.fromJson(
        await _getJson('/api/alerts', {
              if (cameraId != null && cameraId.isNotEmpty) 'cameraId': cameraId,
              if (before != null) 'before': before.toUtc().toIso8601String(),
            })
            as Map<String, dynamic>,
      );

  Future<Alert> getAlert(String id) async =>
      Alert.fromJson(await _getJson('/api/alerts/$id') as Map<String, dynamic>);

  /// Marks an alert seen. Called when the card is opened — seen is not dismissed, so the row stays
  /// in the queue and only stops counting as new.
  Future<void> markAlertRead(String id) =>
      _postNoContent('/api/alerts/$id/read', null);

  Future<void> dismissAlert(String id) =>
      _postNoContent('/api/alerts/$id/dismiss', null);

  /// Clears the queue, or one camera's part of it. Returns how many went.
  Future<int> dismissAllAlerts({String? cameraId}) async {
    final response = await _client.post(
      config.resolve('/api/alerts/dismiss-all', {
        if (cameraId != null && cameraId.isNotEmpty) 'cameraId': cameraId,
      }),
    );

    final body = _decode(response);
    return body is Map<String, dynamic>
        ? ((body['cleared'] as num?)?.toInt() ?? 0)
        : 0;
  }

  /// The alert's preview clip — a short MP4 cut from the detect stream, present whether or not the
  /// camera was recording. Stream token rather than the bearer header, for the reason
  /// [savedClipUrl] takes one.
  Uri alertClipUrl(String id, {String? streamToken}) =>
      config.resolve('/api/alerts/$id/clip.mp4', _streamQuery(streamToken));

  /// The frame the alert fired on — not a poster from the middle of the clip. It is the frame
  /// [Alert.box] describes, so the box is drawn over this rather than expected to be in it.
  ///
  /// **[state] is in the URL because the picture behind it changes.** An alert is given a poster the
  /// moment it is raised, from the camera's live snapshot, and the Server replaces that with the
  /// exact frame once the clip is cut some sixteen seconds later. `Image.network` caches by URL, so
  /// without something that moves when the file does, the approximate picture is the only one this
  /// ever shows — and nothing looks wrong, which is what would make it hard to notice.
  Uri alertPosterUrl(String id, {String? streamToken, AlertClipState? state}) =>
      config.resolve('/api/alerts/$id/poster.jpg', {
        ...?_streamQuery(streamToken),
        if (state != null) 'v': state.name,
      });

  /// Opens a media response for streaming to disk, rather than materialising it in memory.
  ///
  /// A clip is an ffmpeg pipe with **no `Content-Length`**, so there is no total to measure
  /// against and nothing here reports a percentage. On a failure the body is small and is read in
  /// full, so the Server's own sentence is what surfaces.
  Future<MediaDownload> openMedia(
    Uri url, {
    required String fallbackName,
  }) async {
    final response = await _client.send(http.Request('GET', url));

    if (response.statusCode >= 400) {
      final body = await http.Response.fromStream(response);
      throw ServalApiException(response.statusCode, _errorMessage(body));
    }

    return MediaDownload(
      stream: response.stream,
      fileName:
          _fileNameFrom(response.headers['content-disposition']) ??
          fallbackName,
      contentLength: response.contentLength,
      from: _dateHeader(response.headers['x-serval-clip-from']),
      to: _dateHeader(response.headers['x-serval-clip-to']),
      truncated: response.headers['x-serval-clip-truncated'] == 'true',
    );
  }

  static DateTime? _dateHeader(String? value) =>
      value == null ? null : DateTime.tryParse(value)?.toLocal();

  /// The filename the Server chose, so both platforms save under the same name.
  ///
  /// On the web this header is only readable because the Server's CORS policy names it in
  /// `WithExposedHeaders` — `AllowAnyHeader` governs the request, not the response — so a Server
  /// predating that returns null here and the composed fallback is used.
  static String? _fileNameFrom(String? disposition) {
    if (disposition == null) return null;
    final match = RegExp(
      r'filename\*?=(?:"([^"]+)"|([^;]+))',
    ).firstMatch(disposition);
    final name = (match?.group(1) ?? match?.group(2))?.trim();
    return (name == null || name.isEmpty) ? null : name;
  }

  /// The VOD playlist for a past window.
  ///
  /// A URL rather than a fetch: the player resolves this and every `.m4s` under it itself. The
  /// Server reads `from` twice — to pick the segments, and to write the `EXT-X-START` that opens
  /// playback at the instant asked for rather than at the segment boundary before it.
  Uri vodUrl(String cameraId, {required DateTime from, required DateTime to}) =>
      config.resolve('/api/cameras/$cameraId/vod.m3u8', {
        'from': from.toUtc().toIso8601String(),
        'to': to.toUtc().toIso8601String(),
      });

  /// How far into the playlist the instant it was asked for actually sits.
  ///
  /// Segments are four seconds and a window can be asked for at any instant inside one, so the
  /// playlist necessarily starts at the segment boundary at or before `from` and says so with
  /// `EXT-X-START:TIME-OFFSET`. Playback position is measured from that boundary, which means the
  /// caller cannot turn a position into a wall-clock instant without this number — it would read
  /// up to four seconds ahead of the picture on screen.
  ///
  /// Read from the playlist rather than from the player: the two backends disagree about
  /// `EXT-X-START` (hls.js honours it, ffmpeg's HLS demuxer does not implement it), so the
  /// playlist is the only place the answer is the same on both.
  ///
  /// Zero when the tag is absent, which is what the Server writes when the window happens to
  /// begin on a boundary.
  Future<Duration> vodStartOffset(
    String cameraId, {
    required DateTime from,
    required DateTime to,
  }) async {
    final response = await _client.get(vodUrl(cameraId, from: from, to: to));
    if (response.statusCode != 200) return Duration.zero;

    final match = RegExp(
      r'#EXT-X-START:TIME-OFFSET=([0-9.]+)',
    ).firstMatch(response.body);
    final seconds = double.tryParse(match?.group(1) ?? '');
    if (seconds == null) return Duration.zero;

    return Duration(milliseconds: (seconds * 1000).round());
  }

  // ------------------------------------------------------------------ plumbing

  Future<Object?> _getJson(String path, [Map<String, String>? query]) async {
    final response = await _client.get(config.resolve(path, query));
    return _decode(response);
  }

  Object? _decode(http.Response response) {
    if (response.statusCode >= 400) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
    // Body decoded from UTF-8 explicitly: `http`'s `.body` falls back to latin-1 when the
    // response omits a charset, which would mangle a scene description's curly quotes.
    return jsonDecode(utf8.decode(response.bodyBytes));
  }

  /// The Server's own words where it gave any. Its failures are `{"error": "..."}`; a bare status
  /// code gets a sentence naming it so a failure is never silent.
  String _errorMessage(http.Response response) {
    try {
      final body = jsonDecode(utf8.decode(response.bodyBytes));
      if (body is Map<String, dynamic>) {
        final message = body['error'] ?? body['detail'] ?? body['title'];
        if (message is String && message.isNotEmpty) return message;
      }
    } on FormatException {
      // Not JSON — fall through to the status line.
    }

    return switch (response.statusCode) {
      404 => 'Not found on the Server.',
      _ => 'The Server returned ${response.statusCode}.',
    };
  }
}

/// An open media response: the bytes, the name to save them under, and what the Server says the
/// file actually covers.
class MediaDownload {
  const MediaDownload({
    required this.stream,
    required this.fileName,
    this.contentLength,
    this.from,
    this.to,
    this.truncated = false,
  });

  final Stream<List<int>> stream;
  final String fileName;

  /// Null for a clip: the body is piped straight out of ffmpeg, so there is no total.
  final int? contentLength;

  /// What the exported file actually starts and ends at, where the Server said.
  final DateTime? from;
  final DateTime? to;

  /// The range crossed an ffmpeg session restart and the export stopped at that boundary — so the
  /// file is shorter than the window asked for, and honest reporting says so.
  final bool truncated;

  Duration? get covered =>
      from == null || to == null ? null : to!.difference(from!);
}
