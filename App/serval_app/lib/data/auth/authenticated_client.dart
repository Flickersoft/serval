import 'package:http/http.dart' as http;

import 'auth_controller.dart';

/// Wraps the plain `http.Client` every other request in the App goes through, attaching
/// `Authorization: Bearer <token>` and refreshing it first when it's close to expiring (see
/// [AuthController.ensureFreshAccessToken]).
///
/// Passed as `ServalApi`'s `client` — `serval_api.dart` itself needs no changes, since that class
/// already takes an injected `http.Client?` and knows nothing about where its bytes come from.
class AuthenticatedClient extends http.BaseClient {
  AuthenticatedClient({required this._auth, http.Client? inner})
    : _inner = inner ?? http.Client();

  final AuthController _auth;
  final http.Client _inner;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final token = await _auth.ensureFreshAccessToken();
    if (token != null) {
      request.headers['Authorization'] = 'Bearer $token';
    }

    final response = await _inner.send(request);
    if (response.statusCode != 401) return response;

    // The proactive refresh above already covers ordinary expiry, so reaching here means the
    // token was invalidated some other way — the Server restarted with a new signing key, or an
    // admin revoked the session. One retry with a forced refresh, not a loop: a second 401 means
    // the session is genuinely gone, and that 401 is returned to the caller as-is.
    //
    // Only a plain `Request` can be resent — its body is a stored byte array, unlike a streamed
    // upload, which can only be read once. Every request `ServalApi` builds is one of these
    // (`get`/`post`/`put`/`delete`, and the raw `http.Request` `openMedia` sends for downloads),
    // so this covers everything the App actually does.
    if (request is! http.Request) return response;

    final refreshedToken = await _auth.ensureFreshAccessToken(force: true);
    if (refreshedToken == null) return response;

    final retry = http.Request(request.method, request.url)
      ..headers.addAll(request.headers)
      ..headers['Authorization'] = 'Bearer $refreshedToken'
      ..bodyBytes = request.bodyBytes;

    return _inner.send(retry);
  }

  @override
  void close() {
    _inner.close();
    super.close();
  }
}
