import 'dart:async';
import 'dart:convert';

import 'package:web_socket_channel/web_socket_channel.dart';

import '../models/alert.dart';
import 'serval_config.dart';
import 'telemetry_documents.dart';

/// `WS /api/events` — every AI record, live, as it is ingested.
///
/// History is a REST query; this socket is only the real-time tap. It carries every camera by
/// default (`?camera=` would narrow it, which the App does not want — the wall's activity column
/// is deliberately cross-camera).
///
/// Reconnects on its own. Because it is a tap and not a queue, anything published while the
/// socket was down is simply missed — so [reconnected] fires after a gap so the repository can
/// re-run its history fetch and fill in.
class EventsSocket {
  EventsSocket({required this.config, required this.mintTicket});

  final ServalConfig config;

  /// Mints a single-use `?ticket=` — see `DashboardSocket.mintTicket`, the same reasoning applies
  /// here identically.
  final Future<String?> Function() mintTicket;

  final _documents = StreamController<TelemetryDocument>.broadcast();
  final _alerts = StreamController<Alert>.broadcast();
  final _reconnected = StreamController<void>.broadcast();

  WebSocketChannel? _channel;
  StreamSubscription<dynamic>? _subscription;
  Timer? _retry;
  Duration _backoff = _minBackoff;
  bool _closed = false;
  int _attempt = 0;

  /// Set when the socket drops after having carried traffic, cleared once the repository has
  /// been told to re-read. Not derived from the backoff — that reads as a coincidence.
  bool _gapToClose = false;
  bool _everConnected = false;

  static const _minBackoff = Duration(seconds: 1);
  static const _maxBackoff = Duration(seconds: 30);

  Stream<TelemetryDocument> get documents => _documents.stream;

  /// Alerts, as they are raised and again when their preview clip is ready.
  ///
  /// A lane of its own rather than a seventh telemetry type, because an alert is not a telemetry
  /// record: it is a queue item with state, built from one. Everything that reads [documents] wants
  /// the detection, and nothing that draws the queue wants a `TelemetryDocument`.
  Stream<Alert> get alerts => _alerts.stream;

  /// Fires when the socket comes back after having been up before — i.e. there is a gap to
  /// close. Deliberately silent on the first connect, where the history fetch has just run.
  Stream<void> get reconnected => _reconnected.stream;

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
      config.resolveSocket('/api/events', {'ticket': ticket}),
    );
    _channel = channel;

    _subscription = channel.stream.listen(
      (message) {
        _backoff = _minBackoff;
        _everConnected = true;

        if (_gapToClose) {
          // Whatever was published while we were away is gone — this is a tap, not a queue — so
          // the repository re-reads the window rather than showing a hole.
          _gapToClose = false;
          _reconnected.add(null);
        }

        if (message is! String) return;

        switch (_parse(message)) {
          case final TelemetryDocument document:
            _documents.add(document);
          case final Alert alert:
            _alerts.add(alert);
          case null:
            break;
        }
      },
      onError: (Object _) => _scheduleReconnect(),
      onDone: _scheduleReconnect,
      cancelOnError: true,
    );
  }

  /// `{ "camera_id": ..., "type": ..., "document": { ... } }`.
  ///
  /// The envelope's `camera_id` is authoritative, but the document inside carries its own already
  /// — the Server stamps it at ingest — so the parsed document needs no help from out here.
  ///
  /// Returns a [TelemetryDocument], an [Alert], or null for a type this build does not know. Null
  /// is the important case and is not an error: a newer Server is free to publish types an older
  /// App has never heard of, and ignoring them is what keeps that from being an upgrade order.
  static Object? _parse(String message) {
    try {
      final envelope = jsonDecode(message);
      if (envelope is! Map<String, dynamic>) return null;

      final type = envelope['type'];
      final document = envelope['document'];
      if (type is! String || document is! Map<String, dynamic>) return null;

      if (type == 'alert') return Alert.fromJson(document);

      return parseTelemetryDocument(type, document);
    } on FormatException {
      return null;
    } on TypeError {
      // A document whose shape is not what this build expects. Same posture as an unknown type:
      // one message lost, no socket torn down.
      return null;
    }
  }

  void _scheduleReconnect() {
    _teardown();
    if (_closed) return;

    // Only a drop after real traffic leaves a gap. A socket that never came up has nothing to
    // have missed, and the history fetch on open covers it.
    _gapToClose |= _everConnected;

    _retry = Timer(_backoff, () {
      _backoff = _backoff * 2 > _maxBackoff ? _maxBackoff : _backoff * 2;
      unawaited(connect());
    });
  }

  void _teardown() {
    // See DashboardSocket._teardown — invalidates a ticket mint already in flight.
    _attempt++;
    _retry?.cancel();
    _retry = null;
    _subscription?.cancel();
    _subscription = null;
    _channel?.sink.close();
    _channel = null;
  }

  /// Drops the connection and stops reconnecting, but leaves this reusable — see
  /// `DashboardSocket.disconnect`, which this mirrors.
  ///
  /// [_everConnected] and [_gapToClose] reset with it: the gap this socket exists to report is a
  /// gap in *one account's* feed, and the next session backfills from scratch anyway. Carrying
  /// them over would fire [reconnected] on the new session's first connect and re-read a window
  /// the history fetch had just read.
  void disconnect() {
    _teardown();
    _backoff = _minBackoff;
    _gapToClose = false;
    _everConnected = false;
  }

  /// Reconnects at once rather than waiting out the backoff — see `DashboardSocket.reconnectNow`
  /// for why it is unconditional and why it is safe.
  ///
  /// The one line that method does not need is the gap. [_teardown] does not set [_gapToClose] —
  /// only [_scheduleReconnect] does — so without this a phone resumed after ten minutes in a
  /// pocket would reconnect silently and leave a ten-minute hole in the activity column. This
  /// socket is a tap, not a queue: whatever was published while it was away is gone, and the only
  /// thing that fetches it back is [reconnected] firing on the next message.
  void reconnectNow() {
    if (_closed) return;
    _teardown();
    _gapToClose |= _everConnected;
    _backoff = _minBackoff;
    unawaited(connect());
  }

  Future<void> close() async {
    _closed = true;
    _teardown();
    await _documents.close();
    await _alerts.close();
    await _reconnected.close();
  }
}
