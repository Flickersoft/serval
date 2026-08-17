// The operating system lights an indicator when an app holds the microphone, and that indicator is
// a promise about what the app is doing. A live view that armed the mic at connect made that
// promise for as long as a camera was on screen, which is what these are here to stop happening
// again — the first of them is the whole bug in one assertion.
//
// `WebRtcSession` itself cannot be tested: it reaches package globals that land on a method
// channel. That is why the state machine lives out here, driven by three plain functions.
import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/playback/microphone_gate.dart';

void main() {
  /// A gate over a fake device, with the permission sheet standing in as a completer nobody has
  /// answered yet.
  ({
    MicrophoneGate<String> gate,
    Completer<String> sheet,
    List<bool> routed,
    List<String> closed,
    int Function() opens,
  })
  build() {
    var sheet = Completer<String>();
    var opens = 0;
    final routed = <bool>[];
    final closed = <String>[];

    final gate = MicrophoneGate<String>(
      open: () {
        opens++;
        // A fresh one per request, so a retry after a refusal can be answered differently.
        if (opens > 1) sheet = Completer<String>();
        return sheet.future;
      },
      route: (microphone, {required bool sending}) => routed.add(sending),
      close: (microphone) async => closed.add(microphone),
    );

    return (
      gate: gate,
      sheet: sheet,
      routed: routed,
      closed: closed,
      opens: () => opens,
    );
  }

  test('asks for nothing until the button is pressed', () async {
    final it = build();

    // Constructed, and left alone exactly as a camera nobody talks into is.
    await Future<void>.delayed(Duration.zero);

    expect(it.opens(), 0);
    expect(it.gate.stage.value, MicStage.closed);
    expect(it.routed, isEmpty);
  });

  test('a press opens the microphone and points it at the camera', () async {
    final it = build();

    it.gate.setTalking(true);
    expect(it.gate.stage.value, MicStage.opening);
    expect(it.opens(), 1);

    it.sheet.complete('mic');
    await Future<void>.delayed(Duration.zero);

    expect(it.gate.stage.value, MicStage.open);
    expect(it.routed, [true]);
  });

  test('a press released while the sheet is still up does not send', () async {
    final it = build();

    it.gate.setTalking(true);
    it.gate.setTalking(false);
    // The request is already in flight and cannot be recalled, so it lands — but into a button
    // nobody is holding any more.
    it.sheet.complete('mic');
    await Future<void>.delayed(Duration.zero);

    expect(it.gate.stage.value, MicStage.open);
    expect(it.routed, [false]);
    expect(it.opens(), 1);
  });

  test(
    'pressing again reuses the microphone rather than reopening it',
    () async {
      final it = build();

      it.gate.setTalking(true);
      it.sheet.complete('mic');
      await Future<void>.delayed(Duration.zero);

      it.gate.setTalking(false);
      it.gate.setTalking(true);

      expect(it.opens(), 1);
      expect(it.routed, [true, false, true]);
    },
  );

  test('pressing twice before the sheet is answered asks once', () async {
    final it = build();

    it.gate.setTalking(true);
    it.gate.setTalking(false);
    it.gate.setTalking(true);

    expect(it.opens(), 1);
    expect(it.gate.stage.value, MicStage.opening);
  });

  test('a refused microphone is reported, and the next press retries', () async {
    final it = build();

    it.gate.setTalking(true);
    it.sheet.completeError(
      StateError('Unable to getUserMedia: NotAllowedError'),
    );
    await Future<void>.delayed(Duration.zero);

    expect(it.gate.stage.value, MicStage.unavailable);
    expect(it.gate.failure, contains('NotAllowedError'));
    expect(it.routed, isEmpty);

    // Nothing retries on its own — someone who has just fixed their site permissions gets
    // talk-back back by pressing the button, not by reloading.
    await Future<void>.delayed(Duration.zero);
    expect(it.opens(), 1);

    it.gate.setTalking(false);
    it.gate.setTalking(true);
    expect(it.opens(), 2);
    expect(it.gate.stage.value, MicStage.opening);
  });

  test('disposing releases a held microphone', () async {
    final it = build();

    it.gate.setTalking(true);
    it.sheet.complete('mic');
    await Future<void>.delayed(Duration.zero);

    await it.gate.dispose();

    expect(it.closed, ['mic']);
  });

  test('a microphone arriving after dispose is released, not kept', () async {
    final it = build();

    it.gate.setTalking(true);
    await it.gate.dispose();
    it.sheet.complete('mic');
    await Future<void>.delayed(Duration.zero);

    // The request outlived the session that made it. Letting it land and stopping it is the only
    // way the device is actually freed — nothing else holds a handle to it.
    expect(it.closed, ['mic']);
    expect(it.routed, isEmpty);
  });
}
