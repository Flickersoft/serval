// How every surface that draws a camera picture fits one whose shape is not the slot's.
//
// A slot's shape is the design's — a 132×74 row, a 232px stage, a 16:9 card. A frame's is its
// camera's sensor, and the two agree only by luck: of the six streams this was built against, one
// is 32:9 and one is portrait. Filling the slot crops whichever edge is long, and a crop of a
// picture is still a picture, so it is the one kind of wrong nobody can see without the original
// beside them. Hence assertions rather than goldens.
//
// Worth its own file because nothing else reaches this code. The sample repository hands out no
// poster URLs and no snapshot bytes, so every captured screen renders the placeholder and the fit
// of a real picture is invisible to all of them.
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/telemetry_documents.dart';
import 'package:serval_app/models/alert.dart';
import 'package:serval_app/models/saved_clip.dart';
import 'package:serval_app/screens/alerts_screen.dart';
import 'package:serval_app/screens/cameras_screen.dart';
import 'package:serval_app/screens/clips_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/alert_still.dart';
import 'package:serval_app/widgets/clip_card.dart';
import 'package:serval_app/widgets/clip_player.dart';
import 'package:serval_app/widgets/tile_placeholder.dart';

import 'picture_fixture.dart';

/// A repository with a poster behind every clip and a frame behind every camera.
///
/// The sample one deliberately has neither — it stands in for a build with no Server — which is
/// exactly why nothing downstream of a poster has ever been covered.
class _PicturedRepository extends SampleServalRepository {
  const _PicturedRepository();

  static Uri posterFor(String id) =>
      Uri.parse('https://serval.invalid/clips/$id/poster.jpg');

  @override
  Future<Map<String, Uri>> clipPosterUrls(List<String> ids) async => {
    for (final id in ids) id: posterFor(id),
  };

  @override
  Future<Map<String, Uri>> alertPosterUrls(List<Alert> alerts) async => {
    for (final alert in alerts) alert.id: posterFor(alert.id),
  };

  @override
  Uint8List? snapshotFor(String cameraId) => fourByThree;
}

void main() {
  Alert alertWith({DetectionBounds? box}) => Alert(
    id: 'a1',
    cameraId: 'doorbell',
    cameraName: 'Doorbell',
    kind: AlertKind.object,
    at: DateTime(2026, 8, 12, 8, 12),
    peakAt: DateTime(2026, 8, 12, 8, 12, 4),
    label: 'person',
    title: 'Person at the front door',
    read: false,
    recorded: true,
    clipState: AlertClipState.ready,
    box: box,
  );

  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  /// A 16:9 slot, so a 4:3 picture has to be pillarboxed into it: 240 of the 320, with a 40px bar
  /// down each side.
  Future<void> pumpInSlot(WidgetTester tester, Widget child) =>
      tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: Center(child: SizedBox(width: 320, height: 180, child: child)),
        ),
      );

  /// The letterbox ground, found from the painted picture outwards.
  ///
  /// Outwards rather than inwards because the two ways a surface gets one put it on opposite sides
  /// of the `Image`: bytes already in hand are wrapped in it, and a poster being fetched raises it
  /// from `frameBuilder` once a frame lands. Both end up as the nearest [ColoredBox] above the
  /// [RawImage] that does the painting.
  final ground = find.ancestor(
    of: find.byType(RawImage),
    matching: find.byType(ColoredBox),
  );

  ColoredBox groundUnderPicture(WidgetTester tester) =>
      tester.widget<ColoredBox>(ground.first);

  Widget screen(Widget body) => ProviderScope(
    overrides: [
      repositoryProvider.overrideWithValue(const _PicturedRepository()),
    ],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: body),
    ),
  );

  group('the alert still', () {
    final url = Uri.parse('https://serval.invalid/alerts/a1/poster.jpg');

    testWidgets('contains the frame the alert fired on', (tester) async {
      seedPoster(url);
      await pumpInSlot(tester, AlertStill(alert: alertWith(), poster: url));
      await tester.pump();

      expect(
        tester.widget<Image>(find.byType(Image)).fit,
        BoxFit.contain,
        reason:
            'BoxFit.cover would crop the long edge of any frame whose aspect '
            'ratio differs from the slot it was given.',
      );
      expect(groundUnderPicture(tester).color, Serval.tile);
    });

    testWidgets('keeps the camera stripe until the poster lands', (
      tester,
    ) async {
      final held = HeldPoster(url.replace(queryParameters: {'v': 'pending'}));

      await pumpInSlot(
        tester,
        AlertStill(alert: alertWith(), poster: held.url),
      );
      await tester.pump();

      // Flat ground under a slot with no picture in it yet would say this camera has nothing to
      // show, where the stripe says a picture is coming.
      expect(find.byType(TilePlaceholderView), findsOneWidget);
      expect(ground, findsNothing);

      held.land();
      await tester.pump();
      await tester.pump();

      expect(groundUnderPicture(tester).color, Serval.tile);
    });

    testWidgets('hangs the detection box on the picture, not on the slot', (
      tester,
    ) async {
      seedPoster(url);
      await pumpInSlot(
        tester,
        AlertStill(
          alert: alertWith(
            box: const DetectionBounds(
              x: 0.25,
              y: 0.5,
              width: 0.125,
              height: 0.25,
            ),
          ),
          poster: url,
        ),
      );
      await tester.pump();
      await tester.pump();

      final slot = tester.getRect(find.byType(AlertStill));
      final box = tester.getRect(find.byType(DecoratedBox));

      // A quarter across a 240px picture that starts 40px in, not a quarter across the 320px slot:
      // the difference is 20px here and 17 in the queue's 132px row, which on a person is most of
      // one.
      expect(box.left - slot.left, closeTo(40 + 0.25 * 240, 0.5));
      expect(box.width, closeTo(0.125 * 240, 0.5));
      expect(box.top - slot.top, closeTo(0.5 * 180, 0.5));
      expect(box.height, closeTo(0.25 * 180, 0.5));
    });

    testWidgets('falls back to the slot while the shape is unknown', (
      tester,
    ) async {
      await pumpInSlot(
        tester,
        AlertStill(
          alert: alertWith(
            box: const DetectionBounds(
              x: 0.25,
              y: 0.5,
              width: 0.125,
              height: 0.25,
            ),
          ),
        ),
      );
      await tester.pump();

      // An alert with no poster at all — the clip that could not be cut. A box slightly out beats
      // no box, and there is no picture for it to be out against.
      final slot = tester.getRect(find.byType(AlertStill));
      final box = tester.getRect(find.byType(DecoratedBox));

      expect(box.left - slot.left, closeTo(0.25 * 320, 0.5));
      expect(box.width, closeTo(0.125 * 320, 0.5));
    });
  });

  group('the clip player', () {
    testWidgets('fits its poster the way it fits the video', (tester) async {
      final url = Uri.parse('https://serval.invalid/clips/c1/poster.jpg');
      seedPoster(url);

      await pumpInSlot(
        tester,
        ClipPlayer(
          clip: SavedClip(
            id: 'c1',
            cameraId: 'doorbell',
            cameraName: 'Doorbell',
            name: 'Parcel behind the planter',
            savedBy: 'Jeremiah',
            from: DateTime(2026, 8, 12, 8, 12),
            to: DateTime(2026, 8, 12, 8, 12, 20),
            savedAt: DateTime(2026, 8, 12, 8, 13),
            duration: const Duration(seconds: 20),
            sizeBytes: 4 << 20,
          ),
          // No source, so no platform player is opened: what is under test is the poster that
          // covers one until the first press.
          source: null,
          poster: url,
        ),
      );
      await tester.pump();

      // Both `contain`, in the same stage, on frames of the same clip — so the first press moves
      // the picture rather than re-framing it.
      expect(tester.widget<Image>(find.byType(Image)).fit, BoxFit.contain);
      expect(groundUnderPicture(tester).color, Serval.tile);
    });
  });

  group('the clips grid', () {
    testWidgets('contains every card poster', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      for (final clip in await const _PicturedRepository().savedClips()) {
        seedPoster(_PicturedRepository.posterFor(clip.id));
      }

      await tester.pumpWidget(screen(const ClipsScreen()));
      await tester.pumpAndSettle();

      final posters = find.descendant(
        of: find.byType(ClipCard),
        matching: find.byType(Image),
      );
      expect(posters, findsWidgets);
      expect(
        tester.widgetList<Image>(posters).every((i) => i.fit == BoxFit.contain),
        isTrue,
      );
    });

    testWidgets('keeps the camera stripe under the poster', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(screen(const ClipsScreen()));
      await tester.pumpAndSettle();

      // Under rather than instead of: a contained poster's bars would otherwise show the card's own
      // background, and swapping the stripe out the moment a URL is known blanks a whole grid for
      // the length of a fetch.
      expect(
        find.descendant(
          of: find.byType(ClipCard),
          matching: find.byType(TilePlaceholderView),
        ),
        findsWidgets,
      );
    });
  });

  group('the camera registry', () {
    testWidgets('contains the row preview', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(screen(const CamerasScreen()));
      await tester.pumpAndSettle();

      // A 4:3 box, so this is the one preview in the app that crops the *common* case: filling it
      // takes a side each off every 16:9 camera.
      final previews = tester.widgetList<Image>(find.byType(Image));
      expect(previews, isNotEmpty);
      expect(previews.every((i) => i.fit == BoxFit.contain), isTrue);
    });
  });

  // The rule itself, rather than the four surfaces that were breaking it — so the next `cover`
  // somebody reaches for is caught wherever it is added, and not only where one used to be.
  group('nothing anywhere fills a slot by cropping', () {
    for (final (name, body, size) in <(String, Widget, Size)>[
      // Wide, because that is where the queue draws both of its pictures at once: a row thumbnail
      // per alert and the hero in the column beside them.
      ('the alerts queue', const AlertsScreen(), Size(1440, 900)),
      ('the clips grid', const ClipsScreen(), Size(1440, 900)),
      ('the camera registry', const CamerasScreen(), Size(1440, 900)),
    ]) {
      testWidgets(name, (tester) async {
        sizeTo(tester, size);
        for (final clip in await const _PicturedRepository().savedClips()) {
          seedPoster(_PicturedRepository.posterFor(clip.id));
        }
        for (final alert
            in (await const _PicturedRepository().alerts()).items) {
          seedPoster(_PicturedRepository.posterFor(alert.id));
        }

        await tester.pumpWidget(screen(body));
        await tester.pumpAndSettle();

        final pictures = tester.widgetList<Image>(find.byType(Image));
        expect(pictures, isNotEmpty);
        expect(
          pictures.map((i) => i.fit).toSet(),
          isNot(contains(BoxFit.cover)),
        );
      });
    }
  });
}
