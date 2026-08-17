import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_tile.dart';
import 'package:serval_app/widgets/nocturne_button.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// Handing a camera over from the wall.
///
/// The wall and the single camera each keep their own scrubber, so opening one from the other has
/// to carry the instant across — or tapping a tile mid-replay quietly means "and start again from
/// live", and finding the moment back again means finding it on a different track.
void main() {
  const repository = SampleServalRepository();

  Future<({String? camera, DateTime? at})> tapTile(
    WidgetTester tester, {
    required bool replaying,
    Size size = const Size(1600, 900),
  }) async {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });

    String? camera;
    DateTime? at;

    await tester.pumpWidget(
      ProviderScope(
        overrides: [repositoryProvider.overrideWithValue(repository)],
        child: MaterialApp(
          debugShowCheckedModeBanner: false,
          theme: buildServalTheme(),
          home: Scaffold(
            body: WallScreen(
              onOpenCamera: (id, instant) {
                camera = id;
                at = instant;
              },
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    if (replaying) {
      // Clicking the day is what starts playback — there is no replay mode to enter.
      final track = tester.getRect(find.byType(TimelineScrubber).first);
      await tester.tapAt(Offset(track.center.dx, track.center.dy));
      await tester.pumpAndSettle();
    }

    await tester.tap(find.byType(CameraTile).first, warnIfMissed: false);
    await tester.pumpAndSettle();

    return (camera: camera, at: at);
  }

  testWidgets('a tile tapped while the wall is live opens live', (
    tester,
  ) async {
    final opened = await tapTile(tester, replaying: false);

    expect(opened.camera, isNotNull);
    expect(
      opened.at,
      isNull,
      reason: 'no playhead to carry, so the camera opens where the wall was',
    );
  });

  testWidgets(
    'a tile tapped while the wall is replaying carries the playhead',
    (tester) async {
      final opened = await tapTile(tester, replaying: true);

      expect(opened.camera, isNotNull);
      expect(
        opened.at,
        isNotNull,
        reason:
            'tapping a tile mid-replay asks to see that camera at that '
            'moment, not to leave replay',
      );
      expect(
        opened.at!.isBefore(DateTime.now()),
        isTrue,
        reason: 'the instant handed over is the playhead, not the wall clock',
      );
    },
  );

  testWidgets('the scrubber fits at the design width once replaying', (
    tester,
  ) async {
    // 1440x900 is the design's own size, and the wall gives 376 of it to the activity column.
    // Replaying adds the playhead, the transport and *Back to live* to a header that already
    // carries the range control, and that row overflowed by 20 pixels — clipped controls in a
    // release build, the striped banner in a debug one.
    await tapTile(tester, replaying: true, size: const Size(1440, 900));
    expect(tester.takeException(), isNull);

    // Containment rather than the absence of an exception: wrapping the control in an `Expanded`
    // stops the row *reporting* an overflow whether or not it fits, because its width is then
    // forced to the leftover. Painting outside that box is silent, so this measures where it
    // actually lands.
    final scrubber = find.byType(TimelineScrubber).first;
    final range = find.ancestor(
      of: find.textContaining(RegExp(r'^Last ')),
      matching: find.byType(NocturneButton),
    );

    expect(range, findsOneWidget);
    expect(
      tester.getRect(range).right,
      lessThanOrEqualTo(tester.getRect(scrubber).right + 0.5),
      reason: 'the range control must stay inside the header it sits in',
    );

    // And it must not grow leftwards over its neighbour instead. Right-aligned inside the space
    // it is given, a control that does not shrink overflows *backwards* into the transport —
    // which no exception reports and the right edge above cannot see.
    expect(
      tester.getRect(range).left,
      greaterThanOrEqualTo(tester.getRect(find.text('Back to live')).right),
      reason: 'the range control must not overlap the transport beside it',
    );
  });
}
