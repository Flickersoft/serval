// How a wall tile fits a frame whose shape is not the tile's.
//
// A tile's shape comes from its span on the grid; a camera's comes from its sensor. They rarely
// agree, and the wall holds a mix — a 4:3 dome next to a 16:9 bullet. Filling the tile would crop
// whichever edge is long, which on a doorbell is the top and bottom of the frame: the face and the
// parcel. So the picture is contained and the leftover is painted, never cropped.
//
// Worth its own test because nothing else reaches this code. The sample repository hands every tile
// a permanently-null frame notifier, so the wall goldens only ever render the placeholder — the fit
// of a real frame is invisible to them.
import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/camera_tile.dart';

import 'picture_fixture.dart';

void main() {
  final camera = Camera(
    id: 'doorbell',
    name: 'Doorbell',
    placeholder: TilePlaceholder.forCameraId('doorbell'),
  );

  Future<void> pumpTile(
    WidgetTester tester,
    ValueListenable<Uint8List?> frames,
  ) => tester.pumpWidget(
    Directionality(
      textDirection: TextDirection.ltr,
      child: Center(
        // 16:9, so a 4:3 frame has to be letterboxed into it.
        child: SizedBox(
          width: 320,
          height: 180,
          child: CameraTile(camera: camera, frames: frames),
        ),
      ),
    ),
  );

  testWidgets('a frame is contained in its tile, never cropped to fill it', (
    tester,
  ) async {
    final frames = ValueNotifier<Uint8List?>(fourByThree);
    addTearDown(frames.dispose);

    await pumpTile(tester, frames);
    await tester.pump();

    final image = tester.widget<Image>(find.byType(Image));
    expect(
      image.fit,
      BoxFit.contain,
      reason:
          'BoxFit.cover would crop the long edge of any frame whose aspect '
          'ratio differs from its tile.',
    );
  });

  testWidgets('the letterbox is the video ground, not the placeholder', (
    tester,
  ) async {
    final frames = ValueNotifier<Uint8List?>(fourByThree);
    addTearDown(frames.dispose);

    await pumpTile(tester, frames);
    await tester.pump();

    // The placeholder stripes stay mounted underneath as the stalled-stream state, so without a
    // ground of its own the bars would show them through the gap rather than reading as the edge
    // of the picture.
    final ground = tester.widget<ColoredBox>(
      find
          .ancestor(of: find.byType(Image), matching: find.byType(ColoredBox))
          .first,
    );
    expect(ground.color, Serval.tile);
  });

  testWidgets('no frame yet still shows the placeholder label', (tester) async {
    final frames = ValueNotifier<Uint8List?>(null);
    addTearDown(frames.dispose);

    await pumpTile(tester, frames);

    expect(find.byType(Image), findsNothing);
    expect(find.text(camera.placeholderLabel), findsOneWidget);
  });

  // The header's one right-hand mark is the only claim the wall makes about a
  // camera, and where it sits is what says which camera it is about. A name and
  // a `Spacer` that were both flex children of the same row halved the free
  // space between them and left the mark adrift in the middle of the frame,
  // which on a wide tile is nearer the name beside it than the corner it
  // belongs to.
  Future<void> pumpMarked(WidgetTester tester, Camera marked) =>
      tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Center(
            child: SizedBox(
              width: 320,
              height: 180,
              child: CameraTile(camera: marked),
            ),
          ),
        ),
      );

  testWidgets('the attention dot sits in the top right corner', (tester) async {
    await pumpMarked(
      tester,
      Camera(
        id: 'doorbell',
        name: 'Doorbell',
        needsAttention: true,
        placeholder: TilePlaceholder.forCameraId('doorbell'),
      ),
    );

    final tile = tester.getRect(find.byType(CameraTile));
    final dot = tester.getRect(
      find
          .descendant(
            of: find.byType(CameraTile),
            matching: find.byType(DecoratedBox),
          )
          .last,
    );

    // Against the header's own 12px inset, not somewhere out in the picture.
    expect(dot.right, closeTo(tile.right - 12, 1));
    expect(dot.top, lessThan(tile.top + 24));
  });

  testWidgets('so does the audio waveform, and REC stays by the name', (
    tester,
  ) async {
    await pumpMarked(
      tester,
      Camera(
        id: 'doorbell',
        name: 'Doorbell',
        isRecording: true,
        hasAudioActivity: true,
        placeholder: TilePlaceholder.forCameraId('doorbell'),
      ),
    );

    final tile = tester.getRect(find.byType(CameraTile));

    expect(
      tester.getRect(find.byType(PhosphorIcon)).right,
      closeTo(tile.right - 12, 1),
    );
    // The pill is a qualifier on the name, so it stays at the other end.
    expect(tester.getRect(find.text('REC')).left, lessThan(tile.center.dx));
  });
}
