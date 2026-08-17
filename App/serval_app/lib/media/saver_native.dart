import 'dart:io';

import 'package:path_provider/path_provider.dart';

import 'media_saver.dart';

MediaSaver makeMediaSaver() => const _NativeMediaSaver();

/// Desktop: straight to the user's Downloads directory.
///
/// Streamed to disk rather than buffered, so a long clip never sits in memory whole — the Server
/// pipes it out of ffmpeg with no `Content-Length`, and a minute of 4K is not small.
class _NativeMediaSaver implements MediaSaver {
  const _NativeMediaSaver();

  @override
  Future<SavedMedia> save({
    required String fileName,
    required String mimeType,
    required Stream<List<int>> stream,
    void Function(int bytes)? onBytes,
  }) async {
    // getDownloadsDirectory reads the XDG user-dirs entry, so it honours a machine whose
    // Downloads is somewhere else. Documents is the fallback when XDG names none.
    final directory =
        await getDownloadsDirectory() ??
        await getApplicationDocumentsDirectory();

    final file = File(_unique(directory.path, fileName));
    final sink = file.openWrite();
    var written = 0;

    try {
      await for (final chunk in stream) {
        sink.add(chunk);
        written += chunk.length;
        onBytes?.call(written);
      }
      await sink.flush();
    } finally {
      await sink.close();
    }

    return SavedMedia(
      fileName: file.uri.pathSegments.last,
      location: directory.path,
      bytes: written,
    );
  }

  /// `front-door-20260802-140530 (2).mp4` — saving the same second twice is unlikely but
  /// silently overwriting somebody's clip is not a thing to risk on an unlikely.
  static String _unique(String directory, String fileName) {
    final dot = fileName.lastIndexOf('.');
    final stem = dot == -1 ? fileName : fileName.substring(0, dot);
    final extension = dot == -1 ? '' : fileName.substring(dot);

    var candidate = '$directory/$fileName';
    var n = 2;
    while (File(candidate).existsSync()) {
      candidate = '$directory/$stem ($n)$extension';
      n++;
    }
    return candidate;
  }
}
