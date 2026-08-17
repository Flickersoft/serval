import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:web_socket_channel/web_socket_channel.dart';

import 'serval_config.dart';

/// One camera's frame off the wall socket.
class SnapshotFrame {
  const SnapshotFrame(this.cameraId, this.jpeg);

  final String cameraId;
  final Uint8List jpeg;
}

/// `WS /api/dashboard` — every camera's ~1 fps JPEG down one socket.
///
/// One socket for the whole wall rather than one per tile is the point: running N live decoders
/// in a grid is expensive and a snapshot grid is not. Frames are binary rather than
/// base64-in-JSON to skip the ~33% tax on every image.
///
/// The Server sends each frame as *two fragments of one message* — a header and the JPEG — so it
/// never has to allocate a combined buffer per viewer per frame. Fragments are reassembled below
/// the channel, so what arrives here is one complete message per frame and [decodeFrame] can
/// treat it as a single buffer.
class DashboardSocket {
  DashboardSocket({required this.config, required this.mintTicket});

  final ServalConfig config;

  /// Mints a single-use `?ticket=` — this is a browser WebSocket, which cannot carry an
  /// `Authorization` header the way every other request in the App does. See
  /// `AuthController.mintWsTicket` and the Server's `StreamTicketService`. Null (no session, or
  /// minting failed) just means this attempt fails and the existing backoff below retries it.
  final Future<String?> Function() mintTicket;

  final _frames = StreamController<SnapshotFrame>.broadcast();
  final _connected = StreamController<bool>.broadcast();

  WebSocketChannel? _channel;
  StreamSubscription<dynamic>? _subscription;
  Timer? _retry;
  Duration _backoff = _minBackoff;
  bool _closed = false;

  /// Guards a slow ticket mint against a closer/reconnect that happened while it was in flight —
  /// without this a `close()` during the mint would still open a channel afterwards.
  int _attempt = 0;

  static const _minBackoff = Duration(seconds: 1);
  static const _maxBackoff = Duration(seconds: 30);

  Stream<SnapshotFrame> get frames => _frames.stream;

  /// Whether the socket is up. The wall uses it to tell "the Server is unreachable" from "this
  /// one camera has stopped sending", which look identical from frame staleness alone.
  Stream<bool> get connected => _connected.stream;

  Future<void> connect() async {
    if (_closed || _channel != null) return;

    final attempt = ++_attempt;
    final ticket = await mintTicket();
    if (_closed || attempt != _attempt || _channel != null) return;

    if (ticket == null) {
      _scheduleReconnect();
      return;
    }

    final channel = WebSocketChannel.connect(
      config.resolveSocket('/api/dashboard', {'ticket': ticket}),
    );
    _channel = channel;

    // The Server paints every camera's latest frame on connect before switching to the live
    // feed, so a reconnect repopulates the whole wall with no extra request from here.
    _subscription = channel.stream.listen(
      (message) {
        _backoff = _minBackoff;
        _connected.add(true);
        if (message is! List<int>) return;
        if (decodeFrame(message) case final frame?) _frames.add(frame);
      },
      onError: (Object _) => _scheduleReconnect(),
      onDone: _scheduleReconnect,
      cancelOnError: true,
    );
  }

  void _scheduleReconnect() {
    _teardown();
    if (_closed) return;

    _connected.add(false);
    _retry = Timer(_backoff, () {
      // Capped exponential backoff, the same shape the Server uses on a dead RTSP source: an NVR
      // that has been unreachable for an hour should not be asking sixty times a minute.
      _backoff = _backoff * 2 > _maxBackoff ? _maxBackoff : _backoff * 2;
      unawaited(connect());
    });
  }

  void _teardown() {
    // Invalidates a ticket mint already in flight from `connect()` — without this, a teardown
    // mid-mint would still open a channel once the mint resolves.
    _attempt++;
    _retry?.cancel();
    _retry = null;
    _subscription?.cancel();
    _subscription = null;
    _channel?.sink.close();
    _channel = null;
  }

  /// Drops the connection and stops reconnecting, but leaves this reusable — a later [connect]
  /// starts again from scratch.
  ///
  /// The difference from [close] is the streams: that ends them, which is right when the app is
  /// going away and wrong when a sign-out is going to be followed by a sign-in on the same page.
  /// `LiveServalRepository.stop` is the caller, and the backoff resets because the next session's
  /// first attempt should not inherit a wait earned by the last one.
  void disconnect() {
    _teardown();
    _backoff = _minBackoff;
    _connected.add(false);
  }

  /// Reconnects at once rather than waiting out the backoff.
  ///
  /// For the App coming back from the background, where the backoff is exactly wrong: a phone away
  /// for ten minutes is sitting on the thirty-second cap, so the wall would hold its last frames
  /// for most of a minute after somebody looked at it.
  ///
  /// **Unconditional**, rather than skipped when [_channel] is already set, and that is the point.
  /// A socket whose peer went away while the radio slept is half-open: nothing here has been told,
  /// so this object reads as connected and no frame will ever arrive on it. Being wrong the other
  /// way costs one handshake — the Server repaints every camera's latest frame on connect, so a
  /// needless reconnect is a round trip and no gap on screen.
  ///
  /// Two things make it safe. [_teardown] bumps `_attempt`, so a ticket mint already in flight
  /// sees itself superseded and returns without opening a second channel; and it cancels the
  /// subscription before closing the sink, so neither `onDone` nor `onError` fires and no stray
  /// [_scheduleReconnect] races the fresh [connect].
  ///
  /// Deliberately does not emit `connected: false`. Nothing was learned about the Server here, and
  /// announcing a drop would have `LiveServalRepository` re-arm its listening window on the way
  /// back up — which the resume has already done, more promptly and for better reasons.
  void reconnectNow() {
    if (_closed) return;
    _teardown();
    // Not inherited from the session that was interrupted: somebody is looking at this now, and
    // the wait earned by a socket that dropped while nobody was should not be spent on them.
    _backoff = _minBackoff;
    unawaited(connect());
  }

  Future<void> close() async {
    _closed = true;
    _teardown();
    await _frames.close();
    await _connected.close();
  }

  /// `[uint32 BE cameraId length][cameraId UTF-8][JPEG bytes]`.
  ///
  /// Static and pure so the wire format is testable without a Server. Returns null for anything
  /// that cannot be a frame — a truncated buffer, or a length prefix that overruns the message —
  /// rather than throwing into the socket's listener and killing the whole wall.
  static SnapshotFrame? decodeFrame(List<int> message) {
    final bytes = message is Uint8List ? message : Uint8List.fromList(message);
    if (bytes.length < 4) return null;

    final idLength = ByteData.sublistView(bytes, 0, 4).getUint32(0, Endian.big);
    if (idLength == 0 || 4 + idLength > bytes.length) return null;

    final cameraId = utf8.decode(
      bytes.sublist(4, 4 + idLength),
      allowMalformed: true,
    );

    // A copy, not a `Uint8List.sublistView`, and the copy is load-bearing: Flutter Web decodes a
    // JPEG one of two ways, and only one of them honours a view's byte offset. On Chrome over
    // HTTPS (or localhost) the engine uses the browser's `ImageDecoder`, which handles a view
    // fine — which is how a view survived here. Everywhere else it falls back to CanvasKit's own
    // WASM codec, which reads the view's underlying buffer from zero, sees this frame's header
    // instead of the JPEG's magic, and renders *nothing at all*: no exception, no console error,
    // just a permanently blank tile. `flutter.js` gates that choice on
    // `typeof ImageDecoder !== 'undefined' && browserEngine === 'blink'`, and `ImageDecoder` is a
    // secure-context-only API — so plain HTTP on a LAN address takes the fallback path.
    //
    // Copying costs about 8 us for a 150 KB frame, once per camera per second, against a JPEG
    // decode of the same bytes that costs milliseconds. It is also no worse on memory: the view
    // pinned the whole message buffer for the life of the tile, where the copy lets it go.
    return SnapshotFrame(cameraId, bytes.sublist(4 + idLength));
  }
}
