import 'dart:js_interop';
import 'dart:typed_data';

import 'package:web/web.dart' as web;

import 'media_saver.dart';

MediaSaver makeMediaSaver() => const _WebMediaSaver();

/// Browser: collect the bytes, wrap them in a `Blob`, and click a synthetic anchor at an object
/// URL.
///
/// An anchor pointed straight at the Server would be simpler and does not work: the `download`
/// attribute is **ignored cross-origin**, and the App and the Server are always different origins
/// — that is what `Serval:Cors` exists for. Without the blob the browser would navigate to the
/// MP4 and play it in a tab under a name of its own choosing.
///
/// No byte count here. `package:http`'s browser client buffers the whole body before yielding it,
/// so there is nothing to report progress from until it is all present — which is why the caller's
/// label says "Exporting…" rather than a figure on this platform.
class _WebMediaSaver implements MediaSaver {
  const _WebMediaSaver();

  @override
  Future<SavedMedia> save({
    required String fileName,
    required String mimeType,
    required Stream<List<int>> stream,
    void Function(int bytes)? onBytes,
  }) async {
    final chunks = <int>[];
    await for (final chunk in stream) {
      chunks.addAll(chunk);
      onBytes?.call(chunks.length);
    }

    final bytes = Uint8List.fromList(chunks);
    final blob = web.Blob(
      [bytes.toJS].toJS,
      web.BlobPropertyBag(type: mimeType),
    );

    final url = web.URL.createObjectURL(blob);
    try {
      final anchor = web.document.createElement('a') as web.HTMLAnchorElement
        ..href = url
        ..download = fileName;

      // Not attached to the document: a click on a detached anchor still triggers the download in
      // every browser that supports the attribute, and leaves no node to clean up.
      anchor.click();
    } finally {
      web.URL.revokeObjectURL(url);
    }

    // The browser chose the directory and will not say which, so there is no location to report.
    return SavedMedia(fileName: fileName, bytes: bytes.length);
  }
}
