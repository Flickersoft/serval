// What a pinch does to the picture, and — the part worth a test — what it does to the boxes over
// it.
//
// A zoomed picture is plausible on its own: nothing about a magnified scene says whether the
// detection boxes came along. They are a separate layer laid out by `PictureAligned` against the
// box it is given, so a transform applied to the video and not to them leaves every box sitting
// over whatever happens to be at its old fraction of the stage — still rectangular, still labelled,
// and no longer over the object. This pins them to the same matrix.
import 'package:flutter/gestures.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/widgets/picture_aligned.dart';
import 'package:serval_app/widgets/picture_zoom.dart';
import 'package:serval_app/widgets/ptz_pad.dart';

void main() {
  /// Deliberately not 4:3, so the picture is pillarboxed inside it — the case that has an inset to
  /// get wrong. The same stage `picture_aligned_test.dart` uses.
  const stage = Size(600, 400);
  const videoSize = Size(640, 480);
  const box = Rect.fromLTWH(0.25, 0.5, 0.125, 0.25);

  /// The stand-in for the video layer: the same box `RTCVideoView` would be handed, `contain`ed
  /// the same way, so its rect moves under the matrix exactly as a real surface does.
  const pictureKey = Key('picture');

  final scale = ValueNotifier<double>(1);

  Future<void> pump(WidgetTester tester) async {
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Center(
          child: SizedBox(
            width: stage.width,
            height: stage.height,
            child: PictureZoom(
              scale: scale,
              child: Stack(
                fit: StackFit.expand,
                children: [
                  Center(
                    child: AspectRatio(
                      aspectRatio: videoSize.aspectRatio,
                      child: const ColoredBox(
                        key: pictureKey,
                        color: Color(0xFF000000),
                      ),
                    ),
                  ),
                  PictureAligned(
                    videoSize: ValueNotifier<Size?>(videoSize),
                    child: const Stack(
                      fit: StackFit.expand,
                      children: [
                        DetectionOverlay(label: 'PERSON · 0.94', rect: box),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Rect picture(WidgetTester tester) => tester.getRect(find.byKey(pictureKey));

  /// The stroked rectangle, not the label chip above it.
  Rect detection(WidgetTester tester) =>
      tester.getRect(find.byType(DecoratedBox).first);

  Matrix4 matrix(WidgetTester tester) => tester
      .widget<InteractiveViewer>(find.byType(InteractiveViewer))
      .transformationController!
      .value;

  /// Two fingers moving apart from the centre of the stage.
  Future<void> pinch(WidgetTester tester, double factor) async {
    final centre = tester.getCenter(find.byType(PictureZoom));
    const reach = Offset(60, 0);

    final left = await tester.startGesture(centre - reach);
    final right = await tester.startGesture(centre + reach);

    // In several steps rather than one: a scale gesture has to move past slop before it claims the
    // arena, and one jump from rest is indistinguishable from a teleport.
    for (var step = 1; step <= 8; step++) {
      final at = reach * (1 + (factor - 1) * step / 8);
      await left.moveTo(centre - at);
      await right.moveTo(centre + at);
      await tester.pump();
    }

    await left.up();
    await right.up();
    await tester.pumpAndSettle();
  }

  testWidgets('at rest it changes nothing', (tester) async {
    await pump(tester);

    // A 4:3 picture fitted by height into a 600x400 stage is 533.3 wide, and the box is a fraction
    // of that picture — the arithmetic `picture_aligned_test.dart` pins, unmoved by the wrapper.
    const width = 400 * 4 / 3;
    const inset = (600 - width) / 2;
    final stageLeft = (800 - stage.width) / 2;

    expect(
      detection(tester).left - stageLeft,
      closeTo(inset + 0.25 * width, 0.5),
    );
    expect(scale.value, 1);
  });

  testWidgets('a box stays over the same part of the scene when zoomed', (
    tester,
  ) async {
    await pump(tester);

    final restPicture = picture(tester);
    final restBox = detection(tester);

    await pinch(tester, 2);

    final zoomed = matrix(tester).getMaxScaleOnAxis();
    expect(zoomed, greaterThan(1.4), reason: 'the pinch did not take');
    expect(scale.value, closeTo(zoomed, 0.001));

    // Where the box *was* in the picture, and where it is now. Both measured as a fraction of the
    // picture's own rect, so they are comparable across the magnification.
    double fractionAcross(Rect within, Rect of) =>
        (of.left - within.left) / within.width;
    double fractionDown(Rect within, Rect of) =>
        (of.top - within.top) / within.height;

    final now = picture(tester);
    final nowBox = detection(tester);

    expect(
      fractionAcross(now, nowBox),
      closeTo(fractionAcross(restPicture, restBox), 0.002),
    );
    expect(
      fractionDown(now, nowBox),
      closeTo(fractionDown(restPicture, restBox), 0.002),
    );
    expect(
      nowBox.width / now.width,
      closeTo(restBox.width / restPicture.width, 0.002),
    );
  });

  testWidgets('the picture cannot be dragged off the stage', (tester) async {
    await pump(tester);

    // Nothing to pan at 1x, so a hard drag over a full-frame picture does nothing at all — which is
    // what keeps a stray swipe from nudging the scene.
    await tester.drag(find.byType(PictureZoom), const Offset(-200, -150));
    await tester.pumpAndSettle();
    expect(matrix(tester), Matrix4.identity());

    await pinch(tester, 2);

    await tester.drag(find.byType(PictureZoom), const Offset(-4000, -4000));
    await tester.pumpAndSettle();

    // Dragged far past anything a thumb could do, and it stops where the picture's own edge meets
    // the stage's — so a zoomed camera can never be flicked into an empty corner.
    final panned = matrix(tester);
    final zoomed = panned.getMaxScaleOnAxis();
    final translation = panned.getTranslation();
    expect(translation.x, closeTo(-(zoomed - 1) * stage.width, 0.5));
    expect(translation.y, closeTo(-(zoomed - 1) * stage.height, 0.5));
  });

  // Both pointer kinds, because they are two different gestures reaching the same arena and only
  // one of them was ever checked: a finger's double tap resolved while a mouse's double click did
  // not, and the two look identical from here.
  for (final kind in const [PointerDeviceKind.touch, PointerDeviceKind.mouse]) {
    testWidgets('a double tap by $kind gives the whole scene back', (
      tester,
    ) async {
      await pump(tester);
      await pinch(tester, 2);
      expect(matrix(tester).getMaxScaleOnAxis(), greaterThan(1.4));

      final centre = tester.getCenter(find.byType(PictureZoom));
      await tester.tapAt(centre, kind: kind);
      await tester.pump(kDoubleTapMinTime);
      await tester.tapAt(centre, kind: kind);
      await tester.pumpAndSettle();

      expect(matrix(tester), Matrix4.identity());
      expect(scale.value, 1);
    });
  }
}
