import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/dashboard_socket.dart';

/// The wall socket's binary frame format.
///
/// `[uint32 BE cameraId length][cameraId UTF-8][JPEG bytes]`, chosen over base64-in-JSON to skip
/// the ~33% tax on every image. It is the one place the App parses bytes by hand, so it gets
/// tested on its own — including the malformed cases, because a throw inside the socket's
/// listener would take the whole wall down rather than dropping one frame.
void main() {
  Uint8List frame(String cameraId, List<int> jpeg) {
    final id = utf8.encode(cameraId);
    final bytes = BytesBuilder()
      ..add(
        (ByteData(4)..setUint32(0, id.length, Endian.big)).buffer.asUint8List(),
      )
      ..add(id)
      ..add(jpeg);
    return bytes.toBytes();
  }

  test('splits the camera id from the image', () {
    final decoded = DashboardSocket.decodeFrame(
      frame('1', [0xFF, 0xD8, 0xFF, 0xD9]),
    );

    expect(decoded, isNotNull);
    expect(decoded!.cameraId, '1');
    expect(decoded.jpeg, [0xFF, 0xD8, 0xFF, 0xD9]);
  });

  test('reads the length big-endian', () {
    // A little-endian reader would see 16777216 for a 1-byte id and reject every frame — the
    // failure would be total and silent, so it is worth pinning explicitly.
    final bytes = frame('1', const [1, 2, 3]);
    expect(bytes.sublist(0, 4), [0, 0, 0, 1]);
    expect(DashboardSocket.decodeFrame(bytes)!.cameraId, '1');
  });

  test('handles a multi-byte camera id', () {
    // Ids are constrained to [A-Za-z0-9_-] by the Server, so this cannot happen today — but the
    // length prefix counts *bytes* and reading it as characters is the bug that would follow a
    // loosening of that rule.
    final decoded = DashboardSocket.decodeFrame(frame('café-1', const [9]));

    expect(decoded!.cameraId, 'café-1');
    expect(decoded.jpeg, [9]);
  });

  test('a camera with no frame body decodes to an empty image', () {
    final decoded = DashboardSocket.decodeFrame(frame('1', const []));

    expect(decoded!.cameraId, '1');
    expect(decoded.jpeg, isEmpty);
  });

  test('the JPEG is a standalone buffer, not a view onto the frame', () {
    // Not a style preference — a view is invisible on Chrome-over-HTTPS and fatal everywhere
    // else. Flutter Web only uses the browser's `ImageDecoder` (which honours a view's byte
    // offset) on Blink in a secure context; otherwise CanvasKit's WASM codec reads the underlying
    // buffer from zero, finds this frame's header where the JPEG magic should be, and renders a
    // blank tile with no exception and nothing in the console. Served over plain HTTP — which is
    // how this runs on the LAN — every wall tile is blank.
    final decoded = DashboardSocket.decodeFrame(
      frame('front-door', [0xFF, 0xD8, 0xFF, 0xD9]),
    )!;

    expect(decoded.jpeg.offsetInBytes, 0);
    expect(decoded.jpeg.buffer.lengthInBytes, decoded.jpeg.length);
    // The bytes the decoder sees really do start at the JPEG magic.
    expect(decoded.jpeg.buffer.asUint8List().take(2), [0xFF, 0xD8]);
  });

  group('malformed frames are dropped rather than thrown', () {
    test('shorter than the length prefix', () {
      expect(DashboardSocket.decodeFrame(const [0, 0]), isNull);
    });

    test('a length that overruns the message', () {
      final bytes = Uint8List.fromList([0, 0, 0, 99, ...utf8.encode('1')]);
      expect(DashboardSocket.decodeFrame(bytes), isNull);
    });

    test('a zero-length camera id', () {
      expect(DashboardSocket.decodeFrame(const [0, 0, 0, 0, 1, 2]), isNull);
    });
  });
}
