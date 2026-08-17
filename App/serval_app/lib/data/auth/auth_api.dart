import 'dart:convert';

import 'package:http/http.dart' as http;

import '../serval_api.dart' show ServalApiException;
import '../serval_config.dart';
import 'auth_models.dart';

/// Login, token refresh, logout, and the two ticket-minting routes — the client half of
/// `Server/Serval.Server/Auth/AuthEndpoints.cs`.
///
/// Deliberately not routed through [AuthenticatedClient]: login and refresh authenticate with a
/// password or a refresh token, never a Bearer header, so wrapping them in the thing that *adds*
/// a Bearer header would be backwards. [mintWsTicket] and [mintStreamToken] do need one, but it's
/// passed in explicitly by the caller (already holds a valid access token via
/// [AuthController.ensureFreshAccessToken]) rather than by making this class stateful.
class AuthApi {
  AuthApi({required this.config, http.Client? client})
    : _client = client ?? http.Client();

  final ServalConfig config;
  final http.Client _client;

  /// How long any one of these waits for an answer.
  ///
  /// Generous on purpose. [AuthController] retries a timed-out refresh on backoff, and a retry is
  /// the one thing that can go wrong here: a request abandoned while the Server was in fact about
  /// to rotate the token leaves the next attempt presenting a spent one. So this is set well past
  /// any healthy round trip — it exists to stop a *hung* Server from stalling the retry chain
  /// forever, not to give up on a slow one.
  static const _timeout = Duration(seconds: 20);

  void close() => _client.close();

  Future<AuthSession> login(String username, String password) async {
    final response = await _client
        .post(
          config.resolve('/api/auth/login'),
          headers: const {'Content-Type': 'application/json'},
          body: jsonEncode({'username': username, 'password': password}),
        )
        .timeout(_timeout);
    return AuthSession.fromJson(_decode(response));
  }

  Future<AuthSession> refresh(String refreshToken) async {
    final response = await _client
        .post(
          config.resolve('/api/auth/refresh'),
          headers: const {'Content-Type': 'application/json'},
          body: jsonEncode({'refreshToken': refreshToken}),
        )
        .timeout(_timeout);
    return AuthSession.fromJson(_decode(response));
  }

  /// Best-effort: the caller has already forgotten the session locally by the time this is
  /// awaited, so nothing meaningful can be done with a failure here beyond not throwing it.
  Future<void> logout(String refreshToken) async {
    await _client
        .post(
          config.resolve('/api/auth/logout'),
          headers: const {'Content-Type': 'application/json'},
          body: jsonEncode({'refreshToken': refreshToken}),
        )
        .timeout(_timeout);
  }

  Future<WsTicket> mintWsTicket(String accessToken) async {
    final response = await _client
        .post(
          config.resolve('/api/auth/ws-ticket'),
          headers: {'Authorization': 'Bearer $accessToken'},
        )
        .timeout(_timeout);
    return WsTicket.fromJson(_decode(response));
  }

  Future<StreamToken> mintStreamToken(String accessToken) async {
    final response = await _client
        .post(
          config.resolve('/api/auth/stream-token'),
          headers: {'Authorization': 'Bearer $accessToken'},
        )
        .timeout(_timeout);
    return StreamToken.fromJson(_decode(response));
  }

  Map<String, dynamic> _decode(http.Response response) {
    if (response.statusCode >= 400) {
      throw ServalApiException(response.statusCode, _errorMessage(response));
    }
    return jsonDecode(utf8.decode(response.bodyBytes)) as Map<String, dynamic>;
  }

  /// The Server's own words where it gave any — same convention as [ServalApi]'s
  /// `_errorMessage`, including the 423 a locked account answers with.
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
      401 => 'Incorrect username or password.',
      423 => 'This account is temporarily locked. Try again shortly.',
      429 => 'Too many attempts. Wait a moment and try again.',
      _ => 'The Server returned ${response.statusCode}.',
    };
  }
}
