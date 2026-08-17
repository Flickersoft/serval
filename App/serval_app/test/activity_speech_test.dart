// How a speech row draws its speaker and its face.
//
// Two claims worth pinning at the pixel level, because nothing else asserts either and both are
// easy to break by reaching for a simpler widget. The face is *flush with the right edge of the
// quote* whatever the sentence's length — which is what the `Expanded` between the two marks buys,
// and what a `Wrap` or a trailing `WidgetSpan` would quietly give up. And the bubble appears **on
// the wall**, which never drew a speaker at all: the wall's heading slot is spoken for by the
// camera name, so putting attribution in the body is the whole reason both screens can have it.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/widgets/activity_column.dart';

void main() {
  ActivityItem speech({
    String text = '“Get off my property.”',
    int? speakerNumber,
    ActivityEmotion? emotion,
    List<ActivityTurn> turns = const [],
    bool isAlert = false,
  }) => ActivityItem(
    id: 'a1',
    kind: TelemetryKind.utterance,
    cameraId: 'front-door',
    cameraName: 'Front door',
    at: DateTime(2026, 8, 5, 18),
    timeLabel: 'now',
    text: text,
    icon: ActivityIcon.speech,
    speaker: 'At the camera',
    speakerNumber: speakerNumber,
    emotion: emotion,
    turns: turns,
    isSpeech: true,
    isAlert: isAlert,
    isRecent: true,
  );

  Future<void> pump(
    WidgetTester tester,
    ActivityItem item, {
    bool showCamera = true,
    double width = 376,
  }) => tester.pumpWidget(
    Directionality(
      textDirection: TextDirection.ltr,
      child: Align(
        alignment: Alignment.topLeft,
        child: SizedBox(
          width: width,
          height: 600,
          child: ActivityFeed(items: [item], showCamera: showCamera),
        ),
      ),
    ),
  );

  Finder emotionIcon() => find.byWidgetPredicate(
    (w) => w is PhosphorIcon && w.icon == PhosphorIconsFill.smileyAngry,
  );

  group('the marks', () {
    testWidgets('the bubble leads the quote and the face is pinned right', (
      tester,
    ) async {
      await pump(
        tester,
        speech(speakerNumber: 1, emotion: ActivityEmotion.angry),
      );

      expect(find.text('1'), findsOneWidget);

      final bubble = tester.getRect(find.text('1'));
      final quote = tester.getRect(find.text('“Get off my property.”'));
      final face = tester.getRect(emotionIcon());

      expect(bubble.right, lessThanOrEqualTo(quote.left));
      expect(face.left, greaterThanOrEqualTo(quote.right));
    });

    testWidgets('the face stays right when the quote wraps to two lines', (
      tester,
    ) async {
      // The claim the `Expanded` exists for: a one-word line and a wrapping
      // paragraph put their glyph in the same place, and on a multi-line quote
      // it sits at the *top* right rather than drifting down beside the last
      // line.
      const long =
          '“Get off my property right now or I am going to call somebody '
          'about this, I mean it.”';

      await pump(tester, speech(text: long, emotion: ActivityEmotion.angry));
      final wrapped = tester.getRect(emotionIcon());
      final quote = tester.getRect(find.text(long));

      expect(quote.height, greaterThan(30), reason: 'expected it to wrap');
      expect(wrapped.left, greaterThanOrEqualTo(quote.right));
      // Top-aligned: within a line's height of the quote's own top.
      expect(wrapped.top - quote.top, lessThan(20));
    });

    testWidgets('a row with one voice draws no digit', (tester) async {
      await pump(tester, speech(emotion: ActivityEmotion.angry));

      expect(find.text('1'), findsNothing);
      // The face is unaffected — the two marks are independent.
      expect(emotionIcon(), findsOneWidget);
    });

    testWidgets('a row the analyzer could not read draws no face', (
      tester,
    ) async {
      await pump(tester, speech(speakerNumber: 2));

      expect(find.text('2'), findsOneWidget);
      expect(
        find.byWidgetPredicate((w) => w is PhosphorIcon && _isSmiley(w.icon)),
        findsNothing,
      );
    });
  });

  group('the wall', () {
    testWidgets('shows bubbles, which it never showed a speaker', (
      tester,
    ) async {
      // The half of the feature that had no rendering before. `showCamera` is
      // true here, so the heading is spoken for by the camera name — and the
      // attribution still lands, because it is in the body.
      await pump(
        tester,
        speech(speakerNumber: 2, emotion: ActivityEmotion.angry),
        showCamera: true,
      );

      expect(find.text('2'), findsOneWidget);
      expect(emotionIcon(), findsOneWidget);
      expect(find.text('Front door'), findsOneWidget);
    });
  });

  group('a settled conversation', () {
    testWidgets('stacks its turns inside one row, in order', (tester) async {
      await pump(
        tester,
        speech(
          text: '“Hello?” “Round the back.”',
          turns: const [
            ActivityTurn(text: '“Hello?”', speakerNumber: 1),
            ActivityTurn(
              text: '“Round the back.”',
              speakerNumber: 2,
              emotion: ActivityEmotion.angry,
            ),
          ],
        ),
      );

      expect(find.text('“Hello?”'), findsOneWidget);
      expect(find.text('“Round the back.”'), findsOneWidget);

      // The flowing fallback is not drawn when there are turns to draw instead.
      expect(find.text('“Hello?” “Round the back.”'), findsNothing);

      // In order, top to bottom.
      expect(
        tester.getTopLeft(find.text('“Hello?”')).dy,
        lessThan(tester.getTopLeft(find.text('“Round the back.”')).dy),
      );

      // And only the turn that had a reading wears one.
      expect(emotionIcon(), findsOneWidget);
    });

    testWidgets('falls back to the flowing text when it has no turns', (
      tester,
    ) async {
      // A transcript that arrived with none. The row still says what was said.
      await pump(tester, speech(text: '“Hello? Round the back.”'));

      expect(find.text('“Hello? Round the back.”'), findsOneWidget);
    });
  });

  testWidgets('an alert speech row draws the same body as an ordinary one', (
    tester,
  ) async {
    // Unreachable today — only sounds and detections carry `is_alert` — so this
    // is what stops the two renderings drifting before it becomes reachable.
    // The alternative on that day is a row with no rule, no speaker and no
    // face, and nobody would notice which until the two were side by side.
    await pump(
      tester,
      speech(speakerNumber: 1, emotion: ActivityEmotion.angry, isAlert: true),
    );

    expect(find.text('1'), findsOneWidget);
    expect(emotionIcon(), findsOneWidget);
  });

  testWidgets('a scene is still a plain sentence', (tester) async {
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 376,
            height: 600,
            child: ActivityFeed(
              items: [
                ActivityItem(
                  id: 'a2',
                  kind: TelemetryKind.scene,
                  cameraId: 'front-door',
                  cameraName: 'Front door',
                  at: DateTime(2026, 8, 5, 18),
                  timeLabel: 'now',
                  text: 'A courier is at the door.',
                  icon: ActivityIcon.scene,
                  isRecent: true,
                ),
              ],
            ),
          ),
        ),
      ),
    );

    expect(find.text('A courier is at the door.'), findsOneWidget);
    expect(find.text('1'), findsNothing);
    expect(
      find.byWidgetPredicate((w) => w is PhosphorIcon && _isSmiley(w.icon)),
      findsNothing,
    );
  });
}

/// Any of the six faces, so a "draws no emotion" assertion cannot pass merely
/// because the row happened to pick a different one from the test's guess.
bool _isSmiley(IconData? icon) => <IconData>{
  PhosphorIconsFill.smiley,
  PhosphorIconsFill.smileySad,
  PhosphorIconsFill.smileyAngry,
  PhosphorIconsFill.smileyNervous,
  PhosphorIconsFill.smileyMelting,
  PhosphorIconsFill.smileyXEyes,
}.contains(icon);
