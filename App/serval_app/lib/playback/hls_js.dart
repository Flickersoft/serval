import 'dart:js_interop';

import 'package:web/web.dart' as web;

/// The slice of [hls.js](https://github.com/video-dev/hls.js) the replay player uses.
///
/// hls.js is here because Chrome cannot play HLS on its own — only Safari has native support —
/// and Media Source Extensions is the only way to feed a browser fMP4 segments from a playlist.
/// It is vendored at `web/hls.js` and script-tagged in `web/index.html` rather than fetched from
/// a CDN, for the same reason the fonts are vendored: an offline run should still work.
extension type Hls._(JSObject _) implements JSObject {
  external factory Hls(HlsConfig config);

  /// Whether this browser has Media Source Extensions. False where the `<video>` element can take
  /// the playlist directly instead — Safari, and everything on iOS.
  external static bool isSupported();

  external void loadSource(String url);
  external void attachMedia(web.HTMLMediaElement media);
  external void on(String event, JSFunction listener);
  external void destroy();
}

/// The player configuration replay wants.
extension type HlsConfig._(JSObject _) implements JSObject {
  external factory HlsConfig({
    /// -1 means "honour the playlist's own `EXT-X-START`". Load-bearing: that tag is how the
    /// Server says "this window was asked for two seconds into its first segment", and without
    /// this the first thing on screen is footage from before the click.
    int startPosition,
    bool lowLatencyMode,

    /// A VOD window is at most fifteen minutes and seeking within it is the common case, so there
    /// is no reason to buffer deeply ahead or to keep much behind.
    int maxBufferLength,
    int backBufferLength,
  });
}

/// hls.js's own name for its error event, read off the library rather than hard-coded.
@JS('Hls.Events.ERROR')
external String get hlsErrorEvent;

@JS('Hls')
external JSAny? get _hlsGlobal;

/// Whether the page actually got the vendored script.
///
/// A missing `web/hls.js` is otherwise a `ReferenceError` from inside a constructor; checking
/// turns it into a sentence the stage can show.
bool get hlsLoaded => _hlsGlobal != null;

HlsConfig replayHlsConfig() => HlsConfig(
  startPosition: -1,
  lowLatencyMode: false,
  maxBufferLength: 30,
  backBufferLength: 30,
);
