import 'package:flutter/foundation.dart';
import 'package:share_plus/share_plus.dart';

/// Hands a file to whatever the platform means by "share".
///
/// Almost nothing, because `share_plus` does the work. On the web build — which is what Serval on
/// a phone actually is — its plugin probes `navigator.canShare`, builds the `File` objects and
/// makes the call, so the browser opens the real Android or iOS sheet. There is no conditional
/// import here and no per-platform implementation: the package already is one.
class MediaSharer {
  const MediaSharer();

  /// Whether this platform can share a file at all.
  ///
  /// Only Linux cannot: `share_plus`'s Linux plugin throws `UnimplementedError` the moment a file
  /// is passed to it, because a Linux desktop session has no system share sheet to open. Every
  /// other target it supports — the web, and Android/iOS/macOS/Windows if those platform folders
  /// are ever added — can, so this is a check for the one exception rather than a list.
  ///
  /// Coarser than the browser's own answer, on purpose. A desktop browser without the Web Share
  /// API draws the button and fails on the press with the package's own reason, which the screen
  /// shows — rather than this second-guessing a capability `share_plus` already checks properly.
  bool get canShare => kIsWeb || defaultTargetPlatform != TargetPlatform.linux;

  /// [stream] is consumed once.
  ///
  /// Buffered whole, because the Web Share API takes a `File` and not a pipe. Affordable because a
  /// clip is capped and a share is a deliberate press — but it is why sharing is offered on one
  /// clip at a time rather than beside every row.
  Future<void> share({
    required String fileName,
    required String mimeType,
    required Stream<List<int>> stream,
  }) async {
    final bytes = <int>[];
    await for (final chunk in stream) {
      bytes.addAll(chunk);
    }

    await SharePlus.instance.share(
      ShareParams(
        files: [
          XFile.fromData(
            Uint8List.fromList(bytes),
            name: fileName,
            mimeType: mimeType,
          ),
        ],

        // Named explicitly: `cross_file` ignores an XFile's name everywhere but the web, and the
        // package documents this override as the way to name a file made from data.
        fileNameOverrides: [fileName],

        // Both off, and this is the load-bearing line. Left on, a browser with no Web Share API
        // quietly *downloads* the file instead — which is the button next to this one, so a share
        // that could not happen would look exactly like one that did. Off, the package throws its
        // own reason and the screen shows it.
        downloadFallbackEnabled: false,
        mailToFallbackEnabled: false,
      ),
    );
  }
}
