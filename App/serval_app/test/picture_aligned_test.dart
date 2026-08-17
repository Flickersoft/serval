import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/widgets/picture_aligned.dart';
import 'package:serval_app/widgets/ptz_pad.dart';

/// Where a detection box actually lands on the stage.
///
/// Worth a test rather than a look because a box is plausible *anywhere*: nothing about a rectangle
/// drawn over a person says whether it was laid out against the picture or against the letterboxed
/// box the picture sits in, and the two differ by ~46 px at 640x480 in the single-camera stage.
/// Driving the real app found it only because the footage under it was a fixture whose contents
/// were known; this pins the arithmetic without one.
void main() {
  /// The stage is deliberately not 4:3, which is the case that has a pillarbox to get wrong.
  const stage = Size(600, 400);
  const box = Rect.fromLTWH(0.25, 0.5, 0.125, 0.25);

  Future<Rect> boxRect(WidgetTester tester, Size? videoSize) async {
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Center(
          child: SizedBox(
            width: stage.width,
            height: stage.height,
            child: PictureAligned(
              videoSize: ValueNotifier<Size?>(videoSize),
              child: const Stack(
                fit: StackFit.expand,
                children: [DetectionOverlay(label: 'PERSON · 0.94', rect: box)],
              ),
            ),
          ),
        ),
      ),
    );

    // The stroked rectangle, not the label chip that sits above it.
    final finder = find.byType(DecoratedBox).first;
    return tester.getRect(finder);
  }

  testWidgets('a box covers the picture, not the pillarbox around it', (
    tester,
  ) async {
    final rect = await boxRect(tester, const Size(640, 480));

    // A 4:3 picture fitted by height into this stage is 533.3 wide, leaving 33.3 px of pillarbox
    // each side. The box is a fraction of the picture, so it starts from that inset.
    const picture = 400 * 4 / 3;
    const inset = (600 - picture) / 2;
    final stageLeft =
        (800 - stage.width) / 2; // the SizedBox inside the 800x600 test surface

    expect(rect.left - stageLeft, closeTo(inset + (0.25 * picture), 0.5));
    expect(rect.width, closeTo(0.125 * picture, 0.5));
  });

  testWidgets('a box fills the stage when the picture matches its shape', (
    tester,
  ) async {
    // Nothing to inset by, so the two layouts agree — which is exactly why the bug was invisible
    // at some window sizes and not others.
    final rect = await boxRect(tester, const Size(600, 400));
    final stageLeft = (800 - stage.width) / 2;

    expect(rect.left - stageLeft, closeTo(0.25 * stage.width, 0.5));
    expect(rect.width, closeTo(0.125 * stage.width, 0.5));
  });

  testWidgets('a rotated picture is laid out to the shape on screen', (
    tester,
  ) async {
    // WebRtcView publishes the *displayed* size, swapping the stream's dimensions at 90 and 270,
    // because the letterbox follows what is on screen rather than what was encoded.
    final rect = await boxRect(tester, const Size(480, 640));

    const picture =
        400 * 3 / 4; // portrait, so fitted by height and much narrower
    final stageLeft = (800 - stage.width) / 2;

    expect(
      rect.left - stageLeft,
      closeTo(((600 - picture) / 2) + (0.25 * picture), 0.5),
    );
    expect(rect.width, closeTo(0.125 * picture, 0.5));
  });

  testWidgets(
    'an unknown size falls back to the stage rather than drawing nothing',
    (tester) async {
      // The first moments of a window or a session. A box slightly out beats no box, and it corrects
      // itself the moment the dimensions land.
      final rect = await boxRect(tester, null);
      final stageLeft = (800 - stage.width) / 2;

      expect(rect.left - stageLeft, closeTo(0.25 * stage.width, 0.5));
    },
  );

  testWidgets('a degenerate size is treated as unknown', (tester) async {
    // `RTCVideoValue` starts at 0x0 and a `<video>` reports 0 before its metadata arrives; an
    // aspect ratio built from either is a divide by zero.
    final rect = await boxRect(tester, Size.zero);
    final stageLeft = (800 - stage.width) / 2;

    expect(rect.left - stageLeft, closeTo(0.25 * stage.width, 0.5));
  });
}
