import 'package:flutter/foundation.dart' show kIsWeb;

/// Where the Server is.
///
/// One `--dart-define` rather than a settings screen: the App is an appliance front end pointed
/// at one NVR, and the base URL is the sort of thing that is decided when the app is built or
/// launched, not by the person watching the driveway.
///
/// ```bash
/// flutter run -d chrome --dart-define=SERVAL_BASE_URL=http://nvr.example.lan:8080
/// ```
class ServalConfig {
  const ServalConfig({required this.baseUrl});

  /// The deployed Server. An explicit `--dart-define` always wins. Without one: on web, the
  /// Server now hosts this build itself (see Program.cs's static-file fallback), so the page's
  /// own origin is always correct and needs no build-time hostname baked in.
  ///
  /// Off web there is no page origin to read, so it falls back to `localhost:8080` — the port the
  /// compose files publish, so a bare `flutter run` reaches a Server running on the same machine.
  /// **Anything else is a `--dart-define`**, deliberately: a hostname baked in here would be one
  /// deployment's, wrong for every other, and wrong silently.
  factory ServalConfig.fromEnvironment() {
    const definedUrl = String.fromEnvironment('SERVAL_BASE_URL');
    if (definedUrl.isNotEmpty) {
      return ServalConfig(baseUrl: Uri.parse(definedUrl));
    }

    if (kIsWeb) {
      return ServalConfig(
        baseUrl: Uri.base.replace(path: '', query: ''),
      );
    }

    return ServalConfig(baseUrl: Uri.parse('http://localhost:8080'));
  }

  /// Origin only — `http://host:8080`. Paths are appended by [ServalApi].
  final Uri baseUrl;

  /// The same origin as a WebSocket URL. `http` → `ws`, `https` → `wss`; anything else is left
  /// alone so a misconfiguration surfaces as a connect failure naming the scheme rather than a
  /// silently rewritten one.
  Uri get socketBase => switch (baseUrl.scheme) {
    'http' => baseUrl.replace(scheme: 'ws'),
    'https' => baseUrl.replace(scheme: 'wss'),
    _ => baseUrl,
  };

  Uri resolve(String path, [Map<String, String>? query]) => baseUrl.replace(
    path: path,
    queryParameters: query == null || query.isEmpty ? null : query,
  );

  Uri resolveSocket(String path, [Map<String, String>? query]) =>
      socketBase.replace(
        path: path,
        queryParameters: query == null || query.isEmpty ? null : query,
      );
}
