// What a tile says while nobody knows.
//
// The wall's three readings are a tile of its own each in effect: the ordinary tile, the same tile
// with a word over it, and the dashed card. Getting the middle one wrong is what the whole
// `CameraConnection` change is for — a PWA resumed from the background has every camera stale at
// once, and the dashed card claims six failures on the strength of one socket.
import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/widgets/camera_tile.dart';

import 'picture_fixture.dart';

void main() {
  Camera cameraWith(CameraConnection connection) => Camera(
    id: 'driveway',
    name: 'Driveway',
    connection: connection,
    placeholder: TilePlaceholder.forCameraId('driveway'),
  );

  Widget tile(
    CameraConnection connection, {
    ValueListenable<Uint8List?>? frames,
  }) => Directionality(
    textDirection: TextDirection.ltr,
    child: Center(
      child: SizedBox(
        width: 320,
        height: 180,
        child: CameraTile(camera: cameraWith(connection), frames: frames),
      ),
    ),
  );

  testWidgets(
    'a connecting camera keeps its tile rather than the dashed card',
    (tester) async {
      await tester.pumpWidget(tile(CameraConnection.connecting));

      expect(find.text('Driveway is offline'), findsNothing);
      expect(find.text('Driveway'), findsOneWidget);
    },
  );

  testWidgets('an offline camera still says so', (tester) async {
    // The guard in the other direction. Offline is still a real state and still earns the card —
    // the change was what counts as offline, not whether it is drawn.
    await tester.pumpWidget(tile(CameraConnection.offline));

    expect(find.text('Driveway is offline'), findsOneWidget);
  });

  testWidgets('a held frame is labelled while the camera is only connecting', (
    tester,
  ) async {
    // The one case a resumed wall could still mislead. Dropping the frame would put the flash back
    // in a different typeface, so it stays — and the word is the difference between showing a
    // picture from a moment ago and claiming it is the driveway right now.
    final frames = ValueNotifier<Uint8List?>(fourByThree);
    addTearDown(frames.dispose);

    await tester.pumpWidget(tile(CameraConnection.connecting, frames: frames));
    expect(find.text('CONNECTING'), findsOneWidget);

    await tester.pumpWidget(tile(CameraConnection.online, frames: frames));
    expect(find.text('CONNECTING'), findsNothing);
  });

  testWidgets('a tile with no frame yet shows the name, not the word', (
    tester,
  ) async {
    // The cold start is `connecting` too, and there the placeholder's own label is already the
    // whole story. A second word over an empty tile would be saying it twice.
    final frames = ValueNotifier<Uint8List?>(null);
    addTearDown(frames.dispose);

    await tester.pumpWidget(tile(CameraConnection.connecting, frames: frames));

    expect(find.text('CONNECTING'), findsNothing);
    expect(find.text('DRIVEWAY'), findsOneWidget);
  });
}
