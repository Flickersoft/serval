// The one browser fact the UI has to know about, behind the same conditional-import shape
// `vod_player.dart` uses. `dart.library.io` is true under the VM — including `flutter test` — so
// the stub is what the test binary compiles.
import 'secure_context_stub.dart'
    if (dart.library.js_interop) 'secure_context_web.dart'
    if (dart.library.io) 'secure_context_stub.dart'
    as platform;

/// Whether the page is a *secure context*, which is the gate browsers put in front of a handful of
/// APIs. Always true off the web, where the concept does not exist.
///
/// Serving Serval over plain HTTP is supported — trying it out should not require standing up
/// certificates first — but three browser capabilities are withheld on an insecure origin, and all
/// three fail *silently*, which is what makes them expensive to diagnose:
///
///  * `crypto.subtle` — `flutter_secure_storage` refuses to run, so sessions fall back to plain
///    `localStorage` (see `TokenStore`).
///  * `ImageDecoder` — image decoding falls back to CanvasKit's WASM codec, which does not honour
///    a byte-offset view (see `DashboardSocket.decodeFrame`).
///  * `getUserMedia` — no microphone, so talk-back cannot work. This is the only one with no
///    workaround, which is why it is the only one the UI has to talk about.
///
/// `localhost` and `127.0.0.1` count as secure even over HTTP, so this is not the same question as
/// "is the scheme https" and must not be written as one — a scheme check would wrongly disable
/// talk-back for anyone developing against a local server.
bool get isSecureContext => platform.isSecureContext;
