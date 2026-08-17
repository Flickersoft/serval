// The wall on a phone.
//
// 412x892 is what the design is drawn at and what most of this pins. 700x892 is
// there for the one rule the design does not draw: two tiles to a row from
// `kPairedMinWidth` up, which is also what a phone held sideways gets. Anything
// at or above `Serval.compactWidth` is the desktop wall, which this must leave
// exactly as it was — `wall_activity_test` and the goldens cover that, so only
// the boundary is checked here.
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/wall_layout.dart';
import 'package:serval_app/screens/wall_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/activity_filter_panel.dart';
import 'package:serval_app/widgets/activity_sheet.dart';
import 'package:serval_app/widgets/camera_tile.dart';
import 'package:serval_app/widgets/compact_app_bar.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

/// A 90x160 JPEG — a doorbell's shape, and deliberately nothing like the 16:9
/// the wall would otherwise assume.
final _portraitJpeg = base64Decode(
  '/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAwAP/bAEMACAQEBAQEBQUFBQUFBgYG'
  'BgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgICQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAEwAAQEA'
  'AAAAAAAAAAAAAAAAAAAHAQEBAAAAAAAAAAAAAAAAAAAABBABAAAAAAAAAAAAAAAAAAAAABEBAAAA'
  'AAAAAAAAAAAAAAAAAP/AABEIAKAAWgMBIgACEQADEQD/2gAMAwEAAhEDEQA/AJeArSAAAAAAAAAA'
  'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
  'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP//Z',
);

/// The sample wall, with real frames for the cameras named.
class _ShapedRepository extends SampleServalRepository {
  _ShapedRepository(this.frames);

  final Map<String, ValueListenable<Uint8List?>> frames;

  @override
  ValueListenable<Uint8List?> frameNotifier(String cameraId) =>
      frames[cameraId] ?? super.frameNotifier(cameraId);
}

void main() {
  const repository = SampleServalRepository();

  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  Widget wall({
    void Function(String, DateTime?)? onOpenCamera,
    SampleServalRepository? repository,
  }) => ProviderScope(
    overrides: [
      repositoryProvider.overrideWithValue(
        repository ?? const SampleServalRepository(),
      ),
    ],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: WallScreen(onOpenCamera: onOpenCamera ?? (_, _) {})),
    ),
  );

  /// The sheet's height right now. Read off the rect rather than the state, so
  /// what is checked is what is on screen.
  double sheetHeight(WidgetTester tester) => tester
      .getRect(
        find
            .descendant(
              of: find.byType(ActivitySheet),
              matching: find.byType(ClipRRect),
            )
            .first,
      )
      .height;

  /// Drags the grabber — the 18px strip at the top of the sheet — by [dy], with
  /// a fling when [velocity] is given.
  Future<void> dragSheet(
    WidgetTester tester,
    double dy, {
    double? velocity,
  }) async {
    final sheet = tester.getRect(find.byType(ActivitySheet));
    final grabber = Offset(
      sheet.center.dx,
      tester.getRect(find.byType(ActivitySheet)).bottom -
          sheetHeight(tester) +
          9,
    );

    if (velocity == null) {
      await tester.dragFrom(grabber, Offset(0, dy));
    } else {
      await tester.flingFrom(grabber, Offset(0, dy), velocity);
    }
    await tester.pumpAndSettle();
  }

  /// Scrolls the tiles to their end, from a fixed point on the wall.
  ///
  /// Not from a tile finder: after the first of these the first tile is above the viewport and a
  /// gesture aimed at its centre lands on nothing. This point is over the wall at every detent
  /// the sheet has.
  Future<void> scrollWallToEnd(WidgetTester tester) async {
    await tester.dragFrom(const Offset(206, 300), const Offset(0, -3000));
    await tester.pumpAndSettle();
  }

  group('the chrome', () {
    testWidgets('lays out without overflow', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });

    testWidgets('is a 56px bar naming the house and its cameras', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      expect(tester.getRect(find.byType(CompactAppBar)).height, 56);
      expect(find.text('Home'), findsOneWidget);
      // Six cameras in the sample registry, `side-path` offline.
      expect(find.text('5 of 6 cameras live'), findsOneWidget);
      expect(find.text('Home · live view'), findsNothing);
    });

    testWidgets('carries the alerts, the clips and the gear, and nothing else', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      // Three destinations rather than one: with no rail at this width, the wall's bar is where
      // the rail's other items have to go. Rearranging still is not here — that is a wide-window
      // act.
      //
      // By semantics label, not tooltip: at 44px the glyph is the only thing naming the action, so
      // that label is the accessible name rather than a hover affordance.
      expect(find.byType(CompactBarAction), findsNWidgets(3));
      expect(find.bySemanticsLabel('Alerts'), findsOneWidget);
      expect(find.bySemanticsLabel('Saved clips'), findsOneWidget);
      expect(find.bySemanticsLabel('Settings'), findsOneWidget);
      expect(find.byTooltip('Rearrange the wall'), findsNothing);
    });

    testWidgets('has no scrubber — replay is a single-camera act', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      expect(find.byType(TimelineScrubber), findsNothing);
    });
  });

  group('the tiles', () {
    testWidgets('are full width at 16:9, in the saved reading order', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      final tiles = tester.widgetList<CameraTile>(find.byType(CameraTile));
      expect(tiles.length, repository.wallLayout().length);
      expect(tiles.every((t) => t.compact), isTrue);

      final first = tester.getRect(find.byType(CameraTile).first);
      // 412 less the wall's 12px either side.
      expect(first.width, 388);
      expect(first.height, closeTo(388 / Serval.pictureAspect, 0.01));

      final drawn = [
        for (final tile in tester.widgetList<CameraTile>(
          find.byType(CameraTile),
        ))
          tile.camera.id,
      ];
      expect(
        drawn,
        WallGrid.readingOrder(
          repository.wallLayout(),
        ).map((t) => t.cameraId).toList(),
      );
    });

    testWidgets('take the shape of the picture, not the wall', (tester) async {
      sizeTo(tester, const Size(412, 892));

      final frames = ValueNotifier<Uint8List?>(_portraitJpeg);
      addTearDown(frames.dispose);

      // The first camera in reading order, so this is also the tile the raised
      // detent measures itself against.
      final shaped = _ShapedRepository({'driveway': frames});
      await tester.pumpWidget(wall(repository: shaped));
      await tester.pumpAndSettle();

      // Decoding a real image needs a real event loop, which a pumped test does
      // not have. The shape lands on the frame after it.
      await tester.runAsync(
        () => Future<void>.delayed(const Duration(milliseconds: 50)),
      );
      await tester.pumpAndSettle();

      final tile = tester.getRect(find.byType(CameraTile).first);
      expect(tile.width, 388, reason: 'still edge to edge');
      expect(
        tile.height,
        closeTo(388 * 160 / 90, 1),
        reason:
            'a 90x160 doorbell contained inside a 16:9 tile is a strip down '
            'the middle of a letterbox — the tile takes the camera’s shape '
            'instead',
      );

      // The camera below it is unaffected: a shape is one camera's, not the
      // wall's.
      expect(
        tester.getRect(find.byType(CameraTile).at(1)).height,
        closeTo(388 / Serval.pictureAspect, 1),
      );
    });

    testWidgets('pair up from kPairedMinWidth', (tester) async {
      sizeTo(tester, const Size(700, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      final first = tester.getRect(find.byType(CameraTile).at(0));
      final second = tester.getRect(find.byType(CameraTile).at(1));

      // 700 less 24 of padding and the 10px between them, halved.
      expect(first.width, closeTo((700 - 24 - 10) / 2, 0.01));
      expect(second.top, first.top);
    });

    testWidgets('open their camera from behind the resting sheet', (
      tester,
    ) async {
      String? opened;
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall(onOpenCamera: (id, _) => opened = id));
      await tester.pumpAndSettle();

      await tester.tap(find.byType(CameraTile).first);
      await tester.pumpAndSettle();

      expect(
        opened,
        WallGrid.readingOrder(repository.wallLayout()).first.cameraId,
      );
    });
  });

  group('the sheet', () {
    testWidgets('rests across the foot of the screen', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      final sheet = tester.getRect(find.byType(ActivitySheet));
      expect(sheet.width, 412);
      expect(sheet.bottom, 892);
      expect(sheetHeight(tester), Serval.activitySheetResting);
    });

    testWidgets('says what it is holding before anything is filtered', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      expect(find.text('What’s happening'), findsOneWidget);
      expect(find.text('Everything, as it happens'), findsOneWidget);
      expect(find.text('RIGHT NOW'), findsOneWidget);
    });

    testWidgets('drags to a raised detent and back', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      await dragSheet(tester, -200);
      final raised = sheetHeight(tester);
      expect(raised, greaterThan(Serval.activitySheetResting));

      // The wall is still there, which is what the sheet leaves at the top of
      // its travel however hard it is thrown.
      expect(find.byType(CameraTile), findsWidgets);

      // A short drag that does not reach halfway comes back rather than
      // stopping wherever it was let go.
      await dragSheet(tester, 40);
      expect(sheetHeight(tester), raised);

      await dragSheet(tester, 200);
      expect(sheetHeight(tester), Serval.activitySheetResting);
    });

    testWidgets('and down to a bar that is only its name', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      await dragSheet(tester, 400);
      expect(
        sheetHeight(tester),
        closeTo(Serval.activitySheetStowed, 0.01),
        reason: 'the wall has this detent too, not only the camera screen',
      );

      final sheet = tester.getRect(find.byType(ActivitySheet));
      final bar = tester.getRect(find.byKey(activityStowBarKey));
      expect(bar.bottom, closeTo(sheet.bottom, 0.01));
      expect(
        find.descendant(
          of: find.byKey(activityStowBarKey),
          matching: find.text('What’s happening'),
        ),
        findsOneWidget,
      );

      // The search field and the feed's first row are what a resting sheet is
      // for, and both are off the bottom of the screen here.
      expect(
        tester.getRect(find.byType(ActivitySearchField)).top,
        greaterThan(sheet.bottom),
      );

      // A tap on what is left is the way back, and on a sheet with no controls
      // to stop at that is straight to the feed.
      await tester.tapAt(bar.center);
      await tester.pumpAndSettle();
      expect(sheetHeight(tester), Serval.activitySheetResting);
    });

    testWidgets('and comes back up in one gesture, not 40px of one', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      await dragSheet(tester, 400);
      expect(sheetHeight(tester), closeTo(Serval.activitySheetStowed, 0.01));

      // Step by step from the bar itself, which is what a finger does. The bar fades out 40px up,
      // and it used to take the drag with it: the recognizer that had won the gesture left the
      // tree, disposing it cancelled the drag, and the sheet stopped dead with the search field
      // half revealed until the finger was lifted and put down again.
      final bar = tester.getRect(find.byKey(activityStowBarKey));
      final gesture = await tester.startGesture(bar.center);
      final heights = <double>[];
      for (var i = 0; i < 8; i++) {
        await gesture.moveBy(const Offset(0, -40));
        await tester.pump();
        heights.add(sheetHeight(tester));
      }

      // The first move of a gesture is spent winning the arena and moves the sheet by nothing, so
      // eight moves are worth seven.
      expect(
        heights.last,
        closeTo(Serval.activitySheetStowed + 7 * 40, 1),
        reason: 'stopped following at ${heights.join(', ')}',
      );

      await gesture.up();
      await tester.pumpAndSettle();
      expect(sheetHeight(tester), Serval.activitySheetResting);
    });

    testWidgets('and the wall takes back the room a stowed sheet gives up', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      // Scrolled to the end, which is the only place the bottom padding is the thing between the
      // last tile and the foot of the screen. Anywhere else the tiles are held up by the ones
      // above them and the room below is the sheet's whatever the padding says.
      await scrollWallToEnd(tester);

      final sheet = tester.getRect(find.byType(ActivitySheet));
      final resting = tester.getRect(find.byType(CameraTile).last).bottom;
      expect(
        resting,
        closeTo(sheet.bottom - Serval.activitySheetResting, 1),
        reason: 'the last tile rests on the sheet, not behind it',
      );

      await dragSheet(tester, 400);
      expect(sheetHeight(tester), closeTo(Serval.activitySheetStowed, 0.01));

      // The tile follows the sheet down rather than leaving a strip of nothing where the feed
      // used to be — the wall's version of the picture growing on the camera screen.
      final stowed = tester.getRect(find.byType(CameraTile).last).bottom;
      expect(stowed, greaterThan(resting));
      expect(stowed, closeTo(sheet.bottom - Serval.activitySheetStowed, 1));

      // And gives it back on the way up. The tile does not float up by itself — where the wall is
      // scrolled to is the reader's, not the tray's — but the room to clear a resting sheet is
      // there again, which is all the padding was ever promising.
      await dragSheet(tester, -60, velocity: 1000);
      expect(sheetHeight(tester), Serval.activitySheetResting);

      await scrollWallToEnd(tester);
      expect(
        tester.getRect(find.byType(CameraTile).last).bottom,
        closeTo(resting, 1),
      );

      // Never more than that, though. Raising the sheet is an act of reading the feed, and a wall
      // that grew room to scroll into every time would be rearranging itself behind a sheet that
      // is covering it anyway.
      await dragSheet(tester, -60, velocity: 1000);
      expect(sheetHeight(tester), greaterThan(Serval.activitySheetResting));

      await scrollWallToEnd(tester);
      expect(
        tester.getRect(find.byType(CameraTile).last).bottom,
        closeTo(resting, 1),
      );
    });

    testWidgets('takes a fling in the direction it was thrown', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      // Thrown short of halfway. Position alone would put this back at rest;
      // the throw is what carries it.
      await dragSheet(tester, -120, velocity: 1000);
      final raised = sheetHeight(tester);
      expect(raised, greaterThan(Serval.activitySheetResting));

      await dragSheet(tester, 120, velocity: 1000);
      expect(sheetHeight(tester), Serval.activitySheetResting);
    });

    testWidgets('stays worth raising over a camera taller than the room', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));

      final frames = ValueNotifier<Uint8List?>(_portraitJpeg);
      addTearDown(frames.dispose);

      await tester.pumpWidget(
        wall(repository: _ShapedRepository({'driveway': frames})),
      );
      await tester.pumpAndSettle();
      await tester.runAsync(
        () => Future<void>.delayed(const Duration(milliseconds: 50)),
      );
      await tester.pumpAndSettle();

      await dragSheet(tester, -400);

      // The tile rhythm alone would put the sheet's top under a 690px doorbell,
      // leaving it shorter than it already was at rest — a gesture that gives
      // back less than it took.
      expect(
        sheetHeight(tester),
        closeTo(892 * Serval.activitySheetRaisedShare, 1),
      );
    });

    testWidgets('leaves one whole tile above it when raised', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      await dragSheet(tester, -300);

      final sheet = tester.getRect(find.byType(ActivitySheet));
      final top = sheet.bottom - sheetHeight(tester);

      expect(
        tester.getRect(find.byType(CameraTile).at(0)).bottom,
        lessThan(top),
      );
      // And the next one peeking behind it.
      expect(tester.getRect(find.byType(CameraTile).at(1)).top, lessThan(top));
    });
  });

  group('the filter', () {
    Future<void> openFilter(WidgetTester tester) async {
      await tester.tap(find.text('Filter'));
      await tester.pumpAndSettle();
    }

    testWidgets('is the sheet, at phone targets, over a dimmed wall', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();
      await openFilter(tester);

      final sheet = tester.getRect(find.byType(ActivitySheet));
      // Measured from the very top of the screen: the sheet covers the app bar
      // as well, which is what the round draws and why the bar is inside the
      // stack rather than above it.
      expect(sheet.top, 0);
      expect(
        sheet.bottom - sheetHeight(tester),
        closeTo(Serval.activitySheetFilterTop, 0.01),
      );

      expect(find.byType(ActivityFilterPanel), findsOneWidget);

      // Every check row is a thumb's target.
      expect(
        tester
            .getRect(
              find
                  .ancestor(
                    of: find.text('Speech'),
                    matching: find.byType(ConstrainedBox),
                  )
                  .first,
            )
            .height,
        greaterThanOrEqualTo(44),
      );
    });

    testWidgets('repeats the count beside Done, and Done comes back', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();
      await openFilter(tester);

      await tester.tap(find.text('Speech'));
      await tester.pumpAndSettle();

      // The same sentence in the footer as the head will carry once it is back.
      final footer = find.textContaining('events in view');
      expect(footer, findsOneWidget);
      final counted = tester.widget<Text>(footer).data!;

      expect(tester.getRect(find.text('Done')).height, lessThan(44));
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();

      expect(sheetHeight(tester), Serval.activitySheetResting);
      expect(find.text(counted), findsOneWidget);
    });

    testWidgets('leaves a chip and a Clear in the fixed head', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();
      await openFilter(tester);

      await tester.tap(find.text('Speech'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();

      expect(find.byType(ActivityFilterChip), findsOneWidget);
      expect(find.text('Clear'), findsOneWidget);
      expect(find.text('Everything, as it happens'), findsNothing);
    });
  });

  group('what it must not disturb', () {
    testWidgets('the shared collapse preference is never written', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();

      await dragSheet(tester, -300);
      await tester.tap(find.text('Filter'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();

      // The column and the single-camera panel share this. A phone putting its
      // sheet down must not collapse either of them.
      expect(repository.activityPanelCollapsed.value, isFalse);
    });

    testWidgets('the search field is 32px on a desktop and 44 here', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();
      expect(tester.getRect(find.byType(ActivitySearchField)).height, 32);

      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(wall());
      await tester.pumpAndSettle();
      expect(tester.getRect(find.byType(ActivitySearchField)).height, 44);
    });
  });
}
