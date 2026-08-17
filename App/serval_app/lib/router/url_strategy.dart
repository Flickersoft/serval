/// Turns the web build's URLs from `#/wall` into `/wall`.
///
/// Conditionally imported, the same way `playback/vod_player.dart` and `media/media_saver.dart`
/// are: `flutter_web_plugins` ships with the SDK but cannot be imported on Linux, so the desktop
/// build gets the no-op.
///
/// Path URLs are only safe here because the Server already serves the SPA fallback — see
/// `MapFallbackToFile("index.html")` in `Program.cs`. Without it, a reload on `/camera/front-door`
/// would ask the Server for a file by that name and get a 404 instead of the app.
library;

export 'url_strategy_stub.dart'
    if (dart.library.js_interop) 'url_strategy_web.dart';
