import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

import 'serval_config.dart';

/// One reading from `WS /api/cameras/{id}/audio-levels`.
///
/// The thresholds arrive with the level rather than being fetched separately, because the meter
/// cannot derive them: a camera that overrides nothing inherits a Server default the App has never
/// seen, and no route exposes it. That is what lets the meter draw an honest line for an untuned
/// camera.
@immutable
class AudioLevel {
  const AudioLevel({
    required this.rms,
    required this.peak,
    required this.speechThreshold,
    required this.soundThreshold,
    required this.speechGateOpen,
    required this.soundGateOpen,
  });

  static AudioLevel? fromJson(Map<String, dynamic> json) {
    final rms = (json['rms'] as num?)?.toDouble();
    final peak = (json['peak'] as num?)?.toDouble();
    if (rms == null || peak == null) return null;

    return AudioLevel(
      rms: rms,
      peak: peak,
      speechThreshold: (json['speech_threshold'] as num?)?.toDouble() ?? 0,
      soundThreshold: (json['sound_threshold'] as num?)?.toDouble() ?? 0,
      speechGateOpen: (json['speech_gate_open'] as bool?) ?? false,
      soundGateOpen: (json['sound_gate_open'] as bool?) ?? false,
    );
  }

  /// Mean level over the reading's interval — the body of the bar.
  final double rms;

  /// Loudest single window in that interval. Held rather than averaged: a mean hides the
  /// transient that actually opens the gate, and whether speech crosses the line is the only
  /// question the meter answers.
  final double peak;

  final double speechThreshold;
  final double soundThreshold;
  final bool speechGateOpen;
  final bool soundGateOpen;
}

/// A live level feed for one camera, for as long as the settings panel showing it is open.
///
/// **Close it.** The Server measures a camera's level only while somebody is subscribed, so a feed
/// left open keeps an RMS pass and a ten-per-second publish running for a panel nobody is looking
/// at. The Server defends itself — it drops a client that stops reading and caps a session at
/// fifteen minutes — but that is a backstop, not a licence to leak one.
///
/// Reconnects while open, because the fifteen-minute cap means a panel left open legitimately will
/// be disconnected by the Server and should come back.
class AudioLevelFeed {
  AudioLevelFeed({
    required this.config,
    required this.cameraId,
    required this.mintTicket,
  }) {
    unawaited(_connect());
  }

  final ServalConfig config;
  final String cameraId;

  /// Mints a single-use `?ticket=` — see `DashboardSocket.mintTicket`, the same reasoning applies
  /// here identically.
  final Future<String?> Function() mintTicket;

  /// A [ValueListenable] rather than a stream the panel rebuilds on: readings arrive about ten
  /// times a second, and a `setState` at that rate would relayout the whole settings form. Same
  /// argument the rest of the App makes for frames and the playhead.
  final ValueNotifier<AudioLevel?> level = ValueNotifier<AudioLevel?>(null);

  WebSocketChannel? _channel;
  StreamSubscription<dynamic>? _subscription;
  Timer? _retry;
  bool _closed = false;
  int _attempt = 0;

  static const _retryDelay = Duration(seconds: 2);

  Future<void> _connect() async {
    if (_closed || _channel != null) return;

    final attempt = ++_attempt;
    final ticket = await mintTicket();
    if (_closed || attempt != _attempt || _channel != null) return;

    if (ticket == null) {
      _scheduleReconnect();
      return;
    }

    final channel = WebSocketChannel.connect(
      config.resolveSocket('/api/cameras/$cameraId/audio-levels', {
        'ticket': ticket,
      }),
    );
    _channel = channel;

    _subscription = channel.stream.listen(
      (message) {
        if (message is! String) return;
        try {
          final decoded = jsonDecode(message);
          if (decoded is! Map<String, dynamic>) return;
          if (AudioLevel.fromJson(decoded) case final reading?) {
            level.value = reading;
          }
        } on FormatException {
          // One unreadable frame on a ~10 Hz stream is worth losing, not crashing over.
        }
      },
      onError: (Object _) => _scheduleReconnect(),
      onDone: _scheduleReconnect,
      cancelOnError: true,
    );
  }

  void _scheduleReconnect() {
    // See DashboardSocket._teardown — invalidates a ticket mint already in flight.
    _attempt++;
    _subscription?.cancel();
    _subscription = null;
    _channel = null;

    // The bar goes blank rather than freezing on the last reading, so a dead feed cannot be
    // mistaken for a silent room — which is the confusion this meter exists to remove.
    level.value = null;

    if (_closed) return;
    _retry?.cancel();
    _retry = Timer(_retryDelay, _connect);
  }

  void close() {
    _closed = true;
    _retry?.cancel();
    _subscription?.cancel();
    _channel?.sink.close();
    _channel = null;
    level.dispose();
  }
}
