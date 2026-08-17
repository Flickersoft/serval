/// The split is configuration against operation. Admin can register/edit/delete cameras, change
/// settings, and manage other accounts. Viewer can watch live and recorded video, read telemetry,
/// and work the live controls — pan/tilt/zoom and talk-back — but cannot change how a camera is
/// set up.
///
/// Mirrors `Serval.Server.Auth.Role` — the Server writes it as `"viewer"` / `"admin"`, not a
/// number, for the same reason `StreamRole` does (see the Server's `RoleConverter`): a raw ordinal
/// would bake the enum's declaration order into the wire format.
enum Role {
  viewer,
  admin;

  static Role fromJson(String value) => Role.values.firstWhere(
    (role) => role.name == value,
    orElse: () => Role.viewer,
  );
}

/// What `POST /api/auth/login` and `POST /api/auth/refresh` both return: a short-lived access
/// token for the `Authorization: Bearer` header, and a longer-lived refresh token to trade in for
/// the next one. Both expiries travel with the tokens rather than being computed client-side, so
/// a clock difference between this device and the Server never matters.
class AuthSession {
  const AuthSession({
    required this.accessToken,
    required this.accessTokenExpiresAt,
    required this.refreshToken,
    required this.refreshTokenExpiresAt,
    required this.username,
    required this.role,
  });

  factory AuthSession.fromJson(Map<String, dynamic> json) => AuthSession(
    accessToken: json['accessToken'] as String,
    accessTokenExpiresAt: DateTime.parse(
      json['accessTokenExpiresAt'] as String,
    ),
    refreshToken: json['refreshToken'] as String,
    refreshTokenExpiresAt: DateTime.parse(
      json['refreshTokenExpiresAt'] as String,
    ),
    username: json['username'] as String,
    role: Role.fromJson(json['role'] as String),
  );

  final String accessToken;
  final DateTime accessTokenExpiresAt;
  final String refreshToken;
  final DateTime refreshTokenExpiresAt;
  final String username;
  final Role role;

  Map<String, dynamic> toJson() => {
    'accessToken': accessToken,
    'accessTokenExpiresAt': accessTokenExpiresAt.toIso8601String(),
    'refreshToken': refreshToken,
    'refreshTokenExpiresAt': refreshTokenExpiresAt.toIso8601String(),
    'username': username,
    'role': role.name,
  };
}

/// A single-use ticket for the WebSocket endpoints (`/api/dashboard`, `/api/events`,
/// `/api/cameras/{id}/audio-levels`), which cannot carry an `Authorization` header. Passed back
/// as `?ticket=`; the Server consumes it on the way in, so it is already spent by the time the
/// upgrade completes.
class WsTicket {
  const WsTicket(this.ticket, this.expiresAt);

  factory WsTicket.fromJson(Map<String, dynamic> json) => WsTicket(
    json['ticket'] as String,
    DateTime.parse(json['expiresAt'] as String),
  );

  final String ticket;
  final DateTime expiresAt;
}

/// A short-lived, GET-only-scoped token for the HLS routes the video player fetches directly
/// (`vod.m3u8` and the segments under it), which also cannot carry a header.
/// Passed back as `?stream_token=`. Unlike [WsTicket] this is multi-use for its ~10 minute
/// lifetime, since a playback session issues many requests, not one handshake.
class StreamToken {
  const StreamToken(this.token, this.expiresAt);

  factory StreamToken.fromJson(Map<String, dynamic> json) => StreamToken(
    json['token'] as String,
    DateTime.parse(json['expiresAt'] as String),
  );

  final String token;
  final DateTime expiresAt;
}
