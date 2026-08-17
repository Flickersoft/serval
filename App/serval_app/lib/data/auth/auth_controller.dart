import 'dart:async';

import 'package:flutter/foundation.dart';

import '../serval_api.dart' show ServalApiException;
import '../serval_config.dart';
import 'auth_api.dart';
import 'auth_models.dart';
import 'token_store.dart';

enum AuthStatus {
  /// Storage has not been read yet — `main` awaits [AuthController.restore] before the first
  /// frame, so screens never actually see this, but it is the safe default for a field.
  unknown,

  /// A stored session whose access token has run out, being renewed against a Server that has not
  /// answered yet.
  ///
  /// Distinct from [unauthenticated] because nothing has rejected this session: the refresh token
  /// stays in storage and the retry keeps running. What separates the two is whether the Server
  /// said no or was never reached — see [_isRejection].
  restoring,

  authenticating,
  authenticated,
  unauthenticated,
}

/// The one place the App holds a session: who is logged in, and the tokens that prove it.
///
/// Everything else that needs a credential asks this rather than reading storage itself —
/// [AuthenticatedClient] calls [ensureFreshAccessToken] on every REST request, and the three
/// WebSocket classes (`DashboardSocket`, `EventsSocket`, `AudioLevelFeed`) and the VOD player call
/// [mintWsTicket]/[mintStreamToken] before each connection, since none of those can carry a
/// Bearer header. `ChangeNotifier` rather than a stream: screens gate their whole tree on
/// [isAuthenticated] with a `ListenableBuilder`, the same pattern `ServalRepository` already uses.
class AuthController extends ChangeNotifier {
  AuthController({
    required ServalConfig config,
    AuthApi? api,
    TokenStore? store,
  }) : _api = api ?? AuthApi(config: config),
       _store = store ?? TokenStore();

  final AuthApi _api;
  final TokenStore _store;

  /// Refresh this far ahead of expiry rather than reactively on a 401 — the access token's whole
  /// 10-minute lifetime is short enough that a request landing in the last few seconds before it
  /// runs out is a real race, not a corner case.
  static const _refreshMargin = Duration(minutes: 2);

  /// Bounds on the boot refresh's retry — the same shape and the same bounds as
  /// `LiveServalRepository._watchPreferences` and `DashboardSocket`, because this is the same
  /// situation they are in: a Server that will be there shortly. A third retry cadence in the same
  /// app would only be a third thing to reason about.
  static const _minRestoreBackoff = Duration(seconds: 1);
  static const _maxRestoreBackoff = Duration(seconds: 30);

  AuthStatus _status = AuthStatus.unknown;
  AuthSession? _session;
  String? _error;
  Future<AuthSession>? _refreshInFlight;

  /// The outstanding boot-refresh retry, or null when there is none.
  Timer? _restoreRetry;
  int _restoreAttempt = 0;
  Duration _restoreBackoff = _minRestoreBackoff;

  /// Set by [dispose], and read across the awaits in [_restoreRefresh]: a request in flight when
  /// this goes away would otherwise schedule a timer onto a dead object.
  bool _disposed = false;

  /// Whether the current session should be written to secure storage — set from [login]'s
  /// `remember` and honored by every later [_refresh], not just the first save. Always true once
  /// [restore] has found something in storage: that session was persisted to begin with, so
  /// keeping it fresh there is continuing the same choice, not making a new one.
  bool _remember = true;

  AuthStatus get status => _status;
  bool get isAuthenticated => _status == AuthStatus.authenticated;

  /// Whether a stored session is waiting on a Server that has not answered yet. What the router
  /// sends to `ConnectingScreen` rather than to the login screen.
  bool get isRestoring => _status == AuthStatus.restoring;

  /// How many times the boot refresh has been tried, for the connecting screen to show. Reset on
  /// success, and on an interactive [login].
  int get restoreAttempt => _restoreAttempt;

  String? get username => _session?.username;
  Role get role => _session?.role ?? Role.viewer;
  bool get isAdmin => role == Role.admin;

  /// The login screen's error banner. Cleared at the start of the next attempt.
  String? get error => _error;

  /// Reads whatever is in secure storage and settles on a status for it — so a device with a
  /// still-valid refresh token skips the login screen on a cold start rather than forcing a
  /// re-login every launch.
  ///
  /// **Awaiting this reaches the network in no case.** `main` awaits it before `runApp`, and a
  /// Server that has bound its port but is not answering yet — one still loading its models is a
  /// good half-minute from its first accepted connection — would otherwise hold the app on a blank
  /// page for as long as it took, with no way out but a reload. What this awaits is the local read;
  /// the renewal that needs a Server runs behind [AuthStatus.restoring], which the router can draw.
  Future<void> restore() async {
    final stored = await _readStored();
    if (stored == null) {
      _status = AuthStatus.unauthenticated;
      notifyListeners();
      return;
    }

    _session = stored;
    final now = DateTime.now();

    // The one thing that genuinely means "sign in again", and the Server does not have to be asked:
    // nothing can be traded for an access token once this has passed.
    if (!now.isBefore(stored.refreshTokenExpiresAt)) {
      await _endSession();
      return;
    }

    // A stored access token with life left in it is a cold start that touches the network not at
    // all. Ten minutes is short, but a reload landing inside one is the common case, and
    // [ensureFreshAccessToken] renews on the first request that actually needs it — so the old
    // unconditional refresh here was a mandatory round trip buying nothing.
    if (now.isBefore(stored.accessTokenExpiresAt.subtract(_refreshMargin))) {
      _status = AuthStatus.authenticated;
      notifyListeners();
      return;
    }

    _status = AuthStatus.restoring;
    notifyListeners();

    unawaited(_restoreRefresh());
  }

  /// Renews the restored session, retrying on backoff for as long as the failure is the Server not
  /// being there.
  ///
  /// The distinction [_isRejection] draws is the whole point: treating every failure as the
  /// session ending would throw away a thirty-day credential over a `ClientException` whenever a
  /// cached browser bundle reloads while the Server is still starting — the same failure mode
  /// `LiveServalRepository._watchPreferences` exists to prevent for the wall.
  Future<void> _restoreRefresh() async {
    try {
      await _refresh();
      _restoreRetry?.cancel();
      _restoreRetry = null;
      _restoreAttempt = 0;
      _restoreBackoff = _minRestoreBackoff;
    } on Object catch (error) {
      if (_disposed) return;
      if (_isRejection(error)) {
        await _endSession();
        return;
      }
      _scheduleRestoreRetry();
    }
  }

  void _scheduleRestoreRetry() {
    _restoreRetry?.cancel();
    _restoreAttempt += 1;

    final delay = _restoreBackoff;
    _restoreBackoff = _restoreBackoff * 2 > _maxRestoreBackoff
        ? _maxRestoreBackoff
        : _restoreBackoff * 2;

    _restoreRetry = Timer(delay, () {
      if (_disposed) return;
      unawaited(_restoreRefresh());
    });

    // The connecting screen counts attempts, and there is nothing else to tell it one went by.
    notifyListeners();
  }

  /// Tries the boot refresh now rather than waiting out the backoff — the connecting screen's
  /// button, for somebody who knows they just started the Server.
  void retryRestoreNow() {
    if (_status != AuthStatus.restoring) return;
    _restoreRetry?.cancel();
    _restoreRetry = null;
    _restoreBackoff = _minRestoreBackoff;
    unawaited(_restoreRefresh());
  }

  /// Forgets the session, here and in storage. Only ever reached from a *rejection* — see
  /// [_isRejection] — or from a refresh token that has run out on its own clock.
  Future<void> _endSession() async {
    _restoreRetry?.cancel();
    _restoreRetry = null;
    _restoreAttempt = 0;
    _restoreBackoff = _minRestoreBackoff;

    _session = null;
    _status = AuthStatus.unauthenticated;
    await _store.clear();
    notifyListeners();
  }

  /// Whether the Server said no, as opposed to never having been reached.
  ///
  /// A 401 or 403 from `/api/auth/refresh` means the refresh token is spent, revoked or expired,
  /// and is worth nothing to anybody — clearing it is right. Anything else is a `ClientException`,
  /// a `SocketException`, a timeout or a 5xx: no answer about the session at all, and no grounds to
  /// end one.
  static bool _isRejection(Object error) =>
      error is ServalApiException &&
      (error.statusCode == 401 || error.statusCode == 403);

  /// The stored session, or null if there is none or it cannot be read.
  ///
  /// Never throws: [restore] runs before the first frame, so anything escaping here is a blank app
  /// rather than a login screen, and a stored session that cannot be read means exactly what
  /// finding none means. [TokenStore] already swallows a failure of either tier; this covers the
  /// decode and the clear behind it.
  Future<AuthSession?> _readStored() async {
    try {
      return await _store.read();
    } on Object catch (error) {
      debugPrint('AuthController: stored session unreadable ($error).');
      return null;
    }
  }

  /// [remember] false skips writing the session to secure storage — it still lives in memory for
  /// the rest of this run (everything above still works: refresh, sockets, sign-out), but
  /// [restore] finds nothing on the next launch, so "stay signed in" unchecked really does mean
  /// only this session.
  Future<bool> login(
    String username,
    String password, {
    bool remember = true,
  }) async {
    // A password being typed settles the question the boot refresh was still retrying.
    _restoreRetry?.cancel();
    _restoreRetry = null;
    _restoreAttempt = 0;
    _restoreBackoff = _minRestoreBackoff;

    _status = AuthStatus.authenticating;
    _error = null;
    notifyListeners();

    try {
      final session = await _api.login(username, password);
      _session = session;
      _remember = remember;
      if (remember) await _persist(session);
      _status = AuthStatus.authenticated;
      notifyListeners();
      return true;
    } on Object catch (e, stack) {
      _error = _errorTextFor(e, stack);
      _status = AuthStatus.unauthenticated;
      notifyListeners();
      return false;
    }
  }

  Future<void> logout() async {
    final refreshToken = _session?.refreshToken;
    await _endSession();

    if (refreshToken != null) {
      // Fire-and-forget: the App has already forgotten the session locally, which is what
      // actually matters here. If the Server never sees the revoke, the refresh token simply
      // expires on its own schedule (Serval:Auth:RefreshTokenDays) instead of being revoked early.
      unawaited(_api.logout(refreshToken).catchError((_) {}));
    }
  }

  /// A valid access token for the `Authorization` header, refreshing first if it's within
  /// [_refreshMargin] of expiring (or already has). Null if there is no session — the caller
  /// (e.g. [AuthenticatedClient]) then sends the request unauthenticated and lets the Server's 401
  /// speak for itself, since there is nothing else meaningful to do at that point.
  ///
  /// [force] skips the margin check — used for the one-retry-after-a-401 path, which exists for
  /// the rare case a token is invalidated server-side mid-life (an admin revoked the session)
  /// rather than by ordinary expiry, since the margin above already covers ordinary expiry.
  Future<String?> ensureFreshAccessToken({bool force = false}) async {
    final session = _session;
    if (session == null) return null;

    final freshEnough =
        !force &&
        DateTime.now().isBefore(
          session.accessTokenExpiresAt.subtract(_refreshMargin),
        );
    if (freshEnough) return session.accessToken;

    try {
      final refreshed = await _refresh();
      return refreshed.accessToken;
    } on Object catch (error) {
      // A rejection mid-session is the session actually ending, and it has to reach the router:
      // `refreshListenable` is what moves the app to the login screen, and swallowing this left
      // somebody sitting on stale tiles while every request 401'd behind them.
      //
      // A transport failure is not, and must not be treated as one — the sockets are already
      // retrying on their own backoff, and nobody should be signed out because the Wi-Fi dropped
      // for a second.
      if (_isRejection(error)) await _endSession();
      return null;
    }
  }

  /// One refresh in flight at a time — [ensureFreshAccessToken] is called from every concurrent
  /// REST request plus every socket's connect, and each of those hitting `/api/auth/refresh`
  /// independently would rotate the refresh token out from under one another, since rotation
  /// invalidates the token that was just used.
  Future<AuthSession> _refresh() {
    final inFlight = _refreshInFlight;
    if (inFlight != null) return inFlight;

    final current = _session;
    if (current == null) {
      return Future<AuthSession>.error(StateError('No session to refresh.'));
    }

    final future = _performRefresh(current)
        .then((session) async {
          _session = session;
          if (_remember) await _persist(session);
          _status = AuthStatus.authenticated;
          notifyListeners();
          return session;
        })
        .whenComplete(() => _refreshInFlight = null);

    _refreshInFlight = future;
    return future;
  }

  /// The round trip, plus the one thing that lets a second tab survive it.
  ///
  /// Rotation invalidates the token it was handed, and presenting a rotated token is what the
  /// Server reads as theft — `AuthEndpoints.RefreshAsync` revokes the whole *family* for it, which
  /// signs out every tab on the account rather than the one that lost a race. Two tabs on one
  /// machine share a [TokenStore], so the loser of a startup race is holding a token its sibling
  /// has already spent, through no fault of anyone's.
  ///
  /// Re-reading storage first, and again after a rejection, turns that into an adoption: the tab
  /// that lost picks up the session the winner just wrote instead of replaying a spent one. Only a
  /// rejection of the *newest* stored token is a session that is really gone.
  Future<AuthSession> _performRefresh(AuthSession current) async {
    final token =
        await _siblingToken(current, current.refreshToken) ??
        current.refreshToken;

    try {
      return await _api.refresh(token);
    } on Object catch (error) {
      if (!_isRejection(error)) rethrow;

      // Rotated by a sibling while this request was in flight. One more attempt, with what is
      // actually in storage now — not a loop: the token below is the newest there is.
      final adopted = await _siblingToken(current, token);
      if (adopted == null) rethrow;
      return _api.refresh(adopted);
    }
  }

  /// The stored refresh token when it differs from [known] and is a sibling's to lend — null
  /// otherwise, meaning "use what you are holding".
  ///
  /// Two things disqualify storage from answering. **[_remember] false**: this session was never
  /// written, so whatever is in there belongs to some earlier run and is stale rather than newer —
  /// adopting it would trade a working token for a dead one. **A different account**: a sibling tab
  /// signed in as somebody else is not a rotation of this session, and picking its token up would
  /// silently change who is logged in here.
  Future<String?> _siblingToken(AuthSession current, String known) async {
    if (!_remember) return null;

    final stored = await _readStored();
    if (stored == null || stored.refreshToken == known) return null;
    if (stored.username != current.username) return null;
    return stored.refreshToken;
  }

  /// A single-use WebSocket connect ticket, or null if minting failed — the socket classes treat
  /// that the same as any other connect failure and retry on their existing backoff.
  Future<String?> mintWsTicket() async {
    final token = await ensureFreshAccessToken();
    if (token == null) return null;
    try {
      return (await _api.mintWsTicket(token)).ticket;
    } on Object {
      return null;
    }
  }

  /// A scoped playback token for a `vod.m3u8` URL, or null if minting failed.
  Future<String?> mintStreamToken() async {
    final token = await ensureFreshAccessToken();
    if (token == null) return null;
    try {
      return (await _api.mintStreamToken(token)).token;
    } on Object {
      return null;
    }
  }

  /// Persisting is best-effort, and deliberately cannot fail the sign-in that triggered it: the
  /// session is already valid in memory, and everything this class does — refresh, sockets,
  /// sign-out — works whether or not it reached disk. [TokenStore] already falls back a tier and
  /// swallows what it cannot write, so this is the belt to that braces; without it, a storage
  /// fault would surface to the user as a failed login of a session that actually succeeded.
  Future<void> _persist(AuthSession session) async {
    try {
      await _store.save(session);
    } on Object catch (error) {
      debugPrint('AuthController: session not persisted ($error).');
    }
  }

  /// What the login screen's banner says. An [Exception] is an expected outcome and knows its own
  /// wording; anything else is a bug reaching us as an [Error], and must not be dressed up as a
  /// network failure — a wrong-but-plausible "Could not reach the Server." on a login that
  /// returned 200 is the kind of message that costs an evening to see through.
  String _errorTextFor(Object error, StackTrace stack) {
    if (error is Exception) return _messageOf(error);

    debugPrint('AuthController: unexpected error during login — $error');
    debugPrintStack(stackTrace: stack);
    return 'Something went wrong signing in. See the console for details.';
  }

  String _messageOf(Object error) {
    final text = error.toString();
    // ServalApiException's toString() is already just its message (see serval_api.dart) — this
    // avoids a direct dependency on that type so AuthApi's own exceptions read the same way.
    return text.isEmpty ? 'Something went wrong.' : text;
  }

  @override
  void dispose() {
    _disposed = true;
    _restoreRetry?.cancel();
    _api.close();
    super.dispose();
  }
}
