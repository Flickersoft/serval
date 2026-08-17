import 'dart:async';

import 'package:flutter/foundation.dart';

/// Where the microphone is, in the terms the operating system reports it in.
///
/// [open] is the one that costs something to be wrong about: it means a device is genuinely
/// held and the phone's microphone indicator is genuinely lit.
enum MicStage {
  /// No device held. The indicator is dark.
  closed,

  /// A request is in flight, and the permission sheet may be up. Nothing is being captured yet.
  opening,

  /// A live device is held.
  open,

  /// The last request failed — refused, or no microphone to give. See [MicrophoneGate.failure].
  unavailable,
}

/// Opens the microphone on the first press of *Hold to talk*, and not before.
///
/// The indicator a phone lights when an app holds the microphone is a promise about what the app
/// is doing, and it must not be made before the button is pressed — a live view that arms the mic
/// at connect is telling the operating system it is listening to the room for as long as the
/// camera is on screen, which is not what anyone asked for.
///
/// Generic over an opaque handle and driven entirely by the three functions it is given, so it
/// holds no WebRTC types and can be exercised without a peer connection — which matters, because
/// `WebRtcSession` cannot be: it reaches package globals that all land on a method channel.
///
/// Once open the device stays open for the rest of the session, and pressing only gates whether
/// it sends. Re-acquiring per press would be more honest about the indicator, but the device
/// open lands *after* the press and would clip the first syllable of every one.
class MicrophoneGate<T extends Object> {
  MicrophoneGate({
    required this._open,
    required this._route,
    required this._close,
  });

  /// Asks for the device. Throwing is the refusal, and it is expected rather than exceptional.
  final Future<T> Function() _open;

  /// Points a held device at the far end, or stops it — called on every change once there is
  /// something to point, including the first time.
  final void Function(T microphone, {required bool sending}) _route;

  final Future<void> Function(T microphone) _close;

  final stage = ValueNotifier<MicStage>(MicStage.closed);

  /// Why the last request failed, for a log line. Deliberately not shown: every platform flattens
  /// a `getUserMedia` failure to one prose string, so "refused" and "no such device" are not
  /// distinguishable here and a screen that claimed to tell them apart would be guessing.
  String? failure;

  T? _microphone;
  bool _talking = false;
  bool _disposed = false;

  T? get microphone => _microphone;

  void setTalking(bool talking) {
    _talking = talking;

    final microphone = _microphone;
    if (microphone != null) {
      _route(microphone, sending: talking);
      return;
    }

    if (!talking || stage.value == MicStage.opening) return;
    unawaited(_acquire());
  }

  /// A refused microphone is not a failed live view: the video still plays and only *Hold to talk*
  /// goes quiet, so this lands on [MicStage.unavailable] rather than throwing. Nothing retries on
  /// its own — the next press does, which is how someone who has just fixed their site permissions
  /// gets talk-back back without reloading.
  Future<void> _acquire() async {
    stage.value = MicStage.opening;

    try {
      final microphone = await _open();
      if (_disposed) {
        await _close(microphone);
        return;
      }

      _microphone = microphone;
      // Against the current state of the button rather than the one that started this: a press
      // released while the permission sheet was up must not leave a device sending into a room.
      _route(microphone, sending: _talking);
      stage.value = MicStage.open;
    } on Object catch (error) {
      if (_disposed) return;
      failure = error.toString();
      stage.value = MicStage.unavailable;
    }
  }

  Future<void> dispose() async {
    _disposed = true;

    final microphone = _microphone;
    _microphone = null;
    if (microphone != null) await _close(microphone);

    stage.dispose();
  }
}
