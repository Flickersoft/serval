// The web file is the default branch, so there is no third throw-only stub for a platform that
// has neither library.
import 'saver_web.dart' if (dart.library.io) 'saver_native.dart';

export 'saver_web.dart' if (dart.library.io) 'saver_native.dart';

/// Where a saved file went, in the words the status line should use.
///
/// [location] is null on the web, where the browser decides the directory and does not say which
/// — so the app reports the filename and stops, rather than naming a path it is guessing at.
class SavedMedia {
  const SavedMedia({
    required this.fileName,
    this.location,
    this.bytes = 0,
    this.truncatedTo,
  });

  final String fileName;
  final String? location;
  final int bytes;

  /// How much the Server actually gave, when it gave less than was asked for — a clip cut at an
  /// ffmpeg session boundary. Null when the file covers the whole requested window.
  final Duration? truncatedTo;
}

/// Writes bytes where the user will find them. One implementation per platform, resolved by the
/// conditional import above — the same shape `lib/playback/` uses for its two players, and for
/// the same reason: `path_provider` has no web implementation and a `Blob` has no native one.
abstract interface class MediaSaver {
  /// [stream] is consumed once. [onBytes] ticks as it arrives, where the platform can tell.
  Future<SavedMedia> save({
    required String fileName,
    required String mimeType,
    required Stream<List<int>> stream,
    void Function(int bytes)? onBytes,
  });
}

MediaSaver createMediaSaver() => makeMediaSaver();
