import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'auth_models.dart';

/// Where the session survives a restart. Three tiers, in descending order of what the platform
/// will actually give us:
///
/// 1. **Mobile and desktop** — the platform keychain/keystore via `flutter_secure_storage`.
/// 2. **Web on a secure origin** — no keychain exists, so the package AES-wraps the value into
///    `localStorage`. Reachable by any script that can run on the page (XSS).
/// 3. **Web on an insecure origin** — `flutter_secure_storage_web` *refuses to run at all*: it
///    throws `UnsupportedError` unless `window.isSecureContext`. Plain HTTP fails that test, so a
///    LAN deployment reached over `http://host:8080` has no secure storage whatsoever. (`localhost`
///    counts as secure, which is why dev never sees this.) We fall back to plain
///    `SharedPreferences` — `localStorage` on web — under a separate key.
///
/// Tier 3 is not the downgrade it looks like. Tier 2 keeps its wrapping key in the *same*
/// `localStorage` as the ciphertext, so against the threat that matters on web (XSS) it is
/// obfuscation rather than protection. What bounds a leak on web is unchanged either way: a
/// 10-minute access token and refresh rotation (see `AuthEndpoints.RefreshAsync`). HTTPS restores
/// tier 2 and is worth doing anyway — the talk-back microphone needs it — but refusing to log
/// anyone in is the wrong way to ask for it.
///
/// The tier is chosen by *catching the failure*, not by sniffing the platform: the same fallback
/// covers a Linux desktop with no libsecret/gnome-keyring, which fails identically. Every entry
/// point below therefore treats a throw from secure storage as "this tier is unavailable" rather
/// than letting it reach the caller — [AuthController] awaits these on its boot path, and an
/// escaping error there is a blank app.
class TokenStore {
  TokenStore({FlutterSecureStorage? storage})
    : _storage = storage ?? const FlutterSecureStorage();

  final FlutterSecureStorage _storage;

  static const _key = 'serval_auth_session_v1';

  /// Deliberately not [_key]: the two backends must not be able to read each other's writes, and
  /// keeping them distinct makes "which tier wrote this" answerable when debugging.
  static const _fallbackKey = 'serval_auth_session_fallback_v1';

  Future<void> save(AuthSession session) async {
    final payload = jsonEncode(session.toJson());

    try {
      await _storage.write(key: _key, value: payload);
    } on Object catch (error) {
      debugPrint(
        'TokenStore: secure storage unavailable ($error) — using fallback.',
      );
      await _saveFallback(payload);
      return;
    }

    // The secure write worked, so drop any copy an earlier run left in the weaker store rather
    // than leaving a stale session sitting there in the clear.
    await _clearFallback();
  }

  /// Secure storage first, then the fallback — so a session written over HTTP is still found once
  /// the deployment moves to HTTPS, and one written before a keyring broke is not stranded.
  Future<AuthSession?> read() async {
    final stored = await _readSecure() ?? await _readFallback();
    if (stored == null) return null;

    try {
      return AuthSession.fromJson(jsonDecode(stored) as Map<String, dynamic>);
    } on Object {
      // Browser storage nobody controls: an unreadable session is cleared and treated as no
      // session, never thrown. Same posture LiveServalRepository takes with a saved wall layout.
      await clear();
      return null;
    }
  }

  /// Both backends, unconditionally: signing out must not depend on which tier happened to hold
  /// the session, and a partial clear would leave a usable token behind.
  Future<void> clear() async {
    try {
      await _storage.delete(key: _key);
    } on Object catch (error) {
      // Deleting is crypto-free on web so this should not fire there, but a broken desktop
      // keyring can still throw, and there is nothing to do about it but carry on.
      debugPrint('TokenStore: could not clear secure storage ($error).');
    }
    await _clearFallback();
  }

  Future<String?> _readSecure() async {
    try {
      return await _storage.read(key: _key);
    } on Object catch (error) {
      debugPrint(
        'TokenStore: secure storage unreadable ($error) — trying fallback.',
      );
      return null;
    }
  }

  Future<String?> _readFallback() async {
    try {
      return (await SharedPreferences.getInstance()).getString(_fallbackKey);
    } on Object catch (error) {
      debugPrint('TokenStore: fallback storage unreadable ($error).');
      return null;
    }
  }

  Future<void> _saveFallback(String payload) async {
    try {
      await (await SharedPreferences.getInstance()).setString(
        _fallbackKey,
        payload,
      );
    } on Object catch (error) {
      // Nothing left to try. The session still lives in memory for this run — see
      // AuthController.login, which treats persistence as best-effort.
      debugPrint('TokenStore: could not persist session ($error).');
    }
  }

  Future<void> _clearFallback() async {
    try {
      await (await SharedPreferences.getInstance()).remove(_fallbackKey);
    } on Object catch (error) {
      debugPrint('TokenStore: could not clear fallback storage ($error).');
    }
  }
}
