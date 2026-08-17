// A control that says nothing is the same failure mode as a blank tile.
//
// Two ways *Hold to talk* can look live and do nothing. `getUserMedia` is secure-context-only, so
// over plain HTTP the browser never hands over a microphone at all — structural, and named in the
// label. And the microphone is only asked for on the press, so a refusal or a permission sheet
// left unanswered is now an ordinary thing to be in the middle of; a refused mic does not tear
// down the working video, so nothing else on screen would say why the button is silent.
//
// These pin that both reach the screen, and that the halo — which claims a device is open to a
// room you are not in — waits until one actually is.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/playback/microphone_gate.dart';
import 'package:serval_app/widgets/talk_controls.dart';

void main() {
  Future<void> pump(WidgetTester tester, Widget child) => tester.pumpWidget(
    Directionality(
      textDirection: TextDirection.ltr,
      child: Center(child: child),
    ),
  );

  testWidgets('names the reason when the origin is why the mic is missing', (
    tester,
  ) async {
    await pump(
      tester,
      const HoldToTalkButton(
        enabled: false,
        disabledReason: 'Talk-back needs HTTPS',
      ),
    );

    expect(find.text('Talk-back needs HTTPS'), findsOneWidget);
    expect(find.text('Hold to talk'), findsNothing);
  });

  testWidgets('says nothing extra when disabled for an unrelated reason', (
    tester,
  ) async {
    // A camera with no backchannel, or one being replayed, would not talk over HTTPS either —
    // blaming the scheme would send someone off configuring TLS to fix something else.
    await pump(tester, const HoldToTalkButton(enabled: false));

    expect(find.text('Hold to talk'), findsOneWidget);
    expect(find.textContaining('HTTPS'), findsNothing);
  });

  testWidgets('reads normally when talk-back is available', (tester) async {
    await pump(tester, const HoldToTalkButton());

    expect(find.text('Hold to talk'), findsOneWidget);
  });

  testWidgets('a disabled button does not report talking', (tester) async {
    var started = false;
    await pump(
      tester,
      HoldToTalkButton(
        enabled: false,
        disabledReason: 'Talk-back needs HTTPS',
        onTalkStart: () => started = true,
      ),
    );

    await tester.tap(find.byType(HoldToTalkButton));
    await tester.pump();

    expect(started, isFalse);
    // Still the reason, not "Talking…" — pressing it must not look like it worked.
    expect(find.text('Talk-back needs HTTPS'), findsOneWidget);
  });

  /// Holds the button down and leaves it down. Returns the gesture so it can be released.
  Future<TestGesture> hold(WidgetTester tester) async {
    final gesture = await tester.startGesture(
      tester.getCenter(find.byType(HoldToTalkButton)),
    );
    await tester.pump(const Duration(milliseconds: 200));
    return gesture;
  }

  /// Whether the button is wearing the halo that claims an open microphone.
  bool haloed(WidgetTester tester) {
    final container = tester.widget<Container>(
      find
          .descendant(
            of: find.byType(HoldToTalkButton),
            matching: find.byType(Container),
          )
          .first,
    );
    return (container.decoration! as BoxDecoration).boxShadow != null;
  }

  testWidgets('says it is waiting while the microphone is being asked for', (
    tester,
  ) async {
    final mic = ValueNotifier(MicStage.opening);
    addTearDown(mic.dispose);
    await pump(tester, HoldToTalkButton(mic: mic));

    final gesture = await hold(tester);

    // The press registered, but the device has not arrived: saying "Release to stop" here would
    // claim a microphone that a permission sheet is still standing in front of.
    expect(find.text('Waiting for the microphone…'), findsOneWidget);
    expect(find.text('Release to stop'), findsNothing);
    expect(haloed(tester), isFalse);

    await gesture.up();
  });

  testWidgets('haloes only once the microphone is actually open', (
    tester,
  ) async {
    final mic = ValueNotifier(MicStage.closed);
    addTearDown(mic.dispose);
    await pump(tester, HoldToTalkButton(mic: mic));

    final gesture = await hold(tester);
    expect(haloed(tester), isFalse);

    mic.value = MicStage.open;
    await tester.pump();

    expect(find.text('Release to stop'), findsOneWidget);
    expect(haloed(tester), isTrue);

    await gesture.up();
  });

  testWidgets('a refused microphone says so, and stays pressable', (
    tester,
  ) async {
    final mic = ValueNotifier(MicStage.unavailable);
    addTearDown(mic.dispose);

    var started = false;
    await pump(
      tester,
      HoldToTalkButton(mic: mic, onTalkStart: () => started = true),
    );

    // Kept while idle rather than reverting to the invitation: pressing again is exactly what
    // someone who has just fixed their browser's site permissions should do, but they should not
    // be told the last press worked.
    expect(find.text('Microphone unavailable'), findsOneWidget);

    await tester.tap(find.byType(HoldToTalkButton));
    await tester.pump();
    expect(started, isTrue);
  });

  testWidgets('a stage with no live session behaves as it always did', (
    tester,
  ) async {
    // Replay, or a wall that cannot stream live: there is no session to ask, and the press has
    // always been the whole of the feedback.
    await pump(tester, const HoldToTalkButton());

    final gesture = await hold(tester);

    expect(find.text('Release to stop'), findsOneWidget);
    expect(haloed(tester), isTrue);

    await gesture.up();
  });
}
