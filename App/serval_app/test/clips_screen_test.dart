// Round 13: saved clips, a peer of the wall.
//
// The goldens cover what the two screens look like. What is pinned here is what the list is *for*
// — that a clip can be found again — plus the two rules that are easy to get wrong without a
// Server in front of you: that the empty set of cameras means all of them, and that a clip somebody
// else saved offers no way to change it.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/saved_clip.dart';
import 'package:serval_app/screens/clips_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/clip_card.dart';
import 'package:serval_app/widgets/clip_detail.dart';

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

  Widget clips({ValueChanged<String>? onOpenClip}) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: ClipsScreen(onOpenClip: onOpenClip)),
    ),
  );

  group('the list', () {
    testWidgets('says how many there are and that they are kept', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      // The one fact that makes these different from everything else on the machine, said where
      // the count is rather than buried in a settings page.
      expect(
        find.text('14 clips · kept until you delete them'),
        findsOneWidget,
      );
    });

    testWidgets('draws a card per clip, grouped', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      // Cards rather than a table: a clip is a picture, and the thumbnail is how anyone finds one.
      expect(find.byType(ClipCard), findsWidgets);
      expect(find.text('Parcel behind the planter'), findsWidgets);
    });

    testWidgets('keeps the frame whole however wide the window is', (
      tester,
    ) async {
      // The card's shape is the design's 16:9 and a frame's is its camera's; where they disagree
      // the picture is contained inside the thumbnail and the leftover painted, so a card that
      // grows sideways spends the extra width on ground rather than on picture. Hence a window with
      // room to spare buys another column rather than wider cards.
      for (final width in [1280.0, 1440.0, 1920.0, 2560.0]) {
        sizeTo(tester, Size(width, 900));
        await tester.pumpWidget(clips());
        await tester.pumpAndSettle();

        final thumb = tester.getSize(
          find
              .descendant(
                of: find.byType(ClipCard).first,
                matching: find.byType(AspectRatio),
              )
              .first,
        );
        expect(
          thumb.width / thumb.height,
          closeTo(16 / 9, 0.02),
          reason: 'thumbnail is not 16:9 at ${width}px',
        );
        expect(
          thumb.width,
          lessThan(400),
          reason: 'cards balloon at ${width}px',
        );
      }
    });

    testWidgets('opens the newest in the column rather than leaving it blank', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      // Choosing between clips must not cost a screen load each, so one is always open.
      expect(find.byType(ClipDetail), findsOneWidget);
    });

    testWidgets('searching names and what was said narrows it', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(EditableText).first, 'planter');
      await tester.pumpAndSettle();

      expect(find.text('Parcel behind the planter'), findsWidgets);
      expect(find.text('Cat on the bins again'), findsNothing);
    });

    testWidgets('a search that matches nothing says so in its own words', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(EditableText).first, 'zzzz');
      await tester.pumpAndSettle();

      // Naming what was searched for, rather than a bare "nothing here" — the field still holds
      // it, so `textContaining` alone would match the field itself.
      expect(find.text('Nothing saved matches “zzzz”.'), findsOneWidget);
      expect(find.byType(ClipCard), findsNothing);
    });
  });

  group('the camera filter', () {
    testWidgets('reads All cameras when nothing is chosen', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      // The empty set means everything, as it does on the wall — there is no sentinel entry
      // standing for "all of them", so *All* is a state rather than a choice.
      expect(find.text('All cameras'), findsOneWidget);
    });

    testWidgets('narrows to one camera and says which', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      await tester.tap(find.text('All cameras'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Kitchen').last);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();

      expect(find.text('Kitchen'), findsWidgets);
      expect(find.text('Cat on the bins again'), findsNothing);
    });
  });

  group('on a phone', () {
    testWidgets('the grid becomes a list, and a row opens its own screen', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));

      String? opened;
      await tester.pumpWidget(clips(onOpenClip: (id) => opened = id));
      await tester.pumpAndSettle();

      // A 124px thumbnail beside two lines reads faster in a column than two-up cards do, and the
      // name gets the full width it needs — clip names are sentences, not labels.
      expect(find.byType(ClipCard), findsWidgets);
      expect(find.byType(ClipDetail), findsNothing);

      await tester.tap(find.text('Parcel behind the planter'));
      await tester.pumpAndSettle();

      expect(opened, 'clip-1');
    });

    testWidgets('search is behind a control rather than always shown', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      // At 412px the bar has room for a title or a field, and the title is what says where you are.
      expect(find.byType(EditableText), findsNothing);

      await tester.tap(find.bySemanticsLabel('Search'));
      await tester.pumpAndSettle();

      expect(find.byType(EditableText), findsOneWidget);
    });

    testWidgets('lays out without overflow', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(clips());
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });
  });

  group('who may change one', () {
    test('the person who saved it, or an Admin', () {
      final at = DateTime(2026, 8, 9, 16, 3, 12);
      final clip = SavedClip(
        id: 'clip-1',
        cameraId: 'front-door',
        cameraName: 'Front door',
        name: 'Parcel behind the planter',
        savedBy: 'jeremiah',
        from: at,
        to: at.add(const Duration(seconds: 55)),
        savedAt: at,
        duration: const Duration(seconds: 55),
        sizeBytes: 84000000,
      );

      expect(clip.mayEdit(user: 'jeremiah', isAdmin: false), isTrue);
      expect(clip.mayEdit(user: 'guest', isAdmin: false), isFalse);

      // An Admin can clear up after an account that is gone.
      expect(clip.mayEdit(user: 'guest', isAdmin: true), isTrue);

      // Signed out is nobody, and must not match a clip saved without a username.
      expect(clip.mayEdit(user: null, isAdmin: false), isFalse);
    });
  });
}
