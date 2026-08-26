import 'dart:js_interop';
import 'dart:js_interop_unsafe';
import 'dart:typed_data';

import 'package:file_saver/file_saver.dart';
import 'package:web/web.dart' as web;

import 'media_saver.dart';

MediaSaver makeMediaSaver() => const _WebMediaSaver();

/// Browser: stream the bytes to a file the user picked, or fall back to building a `Blob`.
///
/// Two paths, because browsers are two kinds of browser here.
///
/// Where the File System Access API exists — Chrome and Edge — `saveAsStream` asks for a
/// destination up front and writes each chunk to it as it arrives. Nothing accumulates, which is
/// the only way a twelve-hour export is survivable: the fallback below holds the finished file
/// three times over at its peak (the growing list, the `Uint8List` copied from it, and the `Blob`
/// copied from that), and a browser tab dies without an error message.
///
/// Where it does not — Firefox and Safari — the `Blob` is still the only way to hand a file over,
/// so it stays. What changes is that the caller knows: [streamsToDisk] is false there and the
/// export is capped to something a tab can hold, rather than being attempted and killing it.
///
/// An anchor pointed straight at the Server would avoid all of this and does not work: the
/// `download` attribute is **ignored cross-origin**, and the App and the Server are always
/// different origins — that is what `Serval:Cors` exists for. Without the blob the browser would
/// navigate to the MP4 and play it in a tab under a name of its own choosing.
class _WebMediaSaver implements MediaSaver {
  const _WebMediaSaver();

  @override
  bool get streamsToDisk =>
      globalContext.hasProperty('showSaveFilePicker'.toJS).toDart;

  @override
  Future<SavedMedia> save({
    required String fileName,
    required String mimeType,
    required Stream<List<int>> stream,
    void Function(int bytes)? onBytes,
  }) async {
    if (streamsToDisk) {
      return _streamToDisk(
        fileName: fileName,
        mimeType: mimeType,
        stream: stream,
        onBytes: onBytes,
      );
    }

    return _collectIntoBlob(
      fileName: fileName,
      mimeType: mimeType,
      stream: stream,
      onBytes: onBytes,
    );
  }

  /// Chrome and Edge: chunks go straight to the file the user chose.
  ///
  /// The byte count is threaded through the stream rather than reported by the package, which
  /// returns only a path at the end — and a count is the whole reason this platform can show
  /// progress at all now, having had nothing to report while it was buffering.
  Future<SavedMedia> _streamToDisk({
    required String fileName,
    required String mimeType,
    required Stream<List<int>> stream,
    void Function(int bytes)? onBytes,
  }) async {
    var written = 0;

    final counted = stream.map((chunk) {
      written += chunk.length;
      onBytes?.call(written);
      return chunk;
    });

    await FileSaver.instance.saveAsStream(
      name: fileName,
      stream: counted,
      // The name already carries its extension: it comes from the Server's own
      // Content-Disposition, which is the name the person should see in their downloads.
      includeExtension: false,
      mimeType: MimeType.custom,
      customMimeType: mimeType,
    );

    // The browser chose the directory and will not say which, so there is no location to report.
    return SavedMedia(fileName: fileName, bytes: written);
  }

  /// Firefox and Safari: the whole file in memory, then an object URL.
  Future<SavedMedia> _collectIntoBlob({
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

    return SavedMedia(fileName: fileName, bytes: bytes.length);
  }
}
