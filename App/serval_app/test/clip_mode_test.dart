// Round 12: *Save clip* turns the camera screen into a trimmer rather than opening a dialog.
//
// Structural rather than a golden, and deliberately so — the trimmer opens around the playhead,
// which live is the wall clock, so every time label on that screen is different on every run.
// What is worth pinning is not the pixels but the claims: that the mode is entered rather than an
// export started, that everything which would take you off the screen stops working while a range
// is being set, and that the two ways of moving an end agree about which end is moving.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/camera.dart';
import 'package:serval_app/screens/camera_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/clip_trimmer.dart';
import 'package:serval_app/widgets/compact_app_bar.dart';
import 'package:serval_app/widgets/nocturne_button.dart';
import 'package:serval_app/widgets/save_clip_dialog.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';
import 'package:serval_app/widgets/trim_track.dart';

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

  Camera cameraNamed(String id) =>
      repository.cameras().firstWhere((c) => c.id == id);

  Widget camera(String id) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(
        body: CameraScreen(camera: cameraNamed(id), onBack: () {}),
      ),
    ),
  );

  /// Presses *Save clip* and lets the segment fetch land.
  Future<void> enterClipMode(WidgetTester tester) async {
    await tester.tap(find.text('Save clip'));
    await tester.pumpAndSettle();
  }

  group('entering', () {
    testWidgets('Save clip opens the trimmer instead of exporting', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      expect(find.byType(TrimTrack), findsNothing);
      expect(find.byType(TimelineScrubber), findsOneWidget);

      await enterClipMode(tester);

      // The timeline you were scrubbing *becomes* the track — swapped, not stacked, so there are
      // never two things on screen that answer to a drag.
      expect(find.byType(TrimTrack), findsOneWidget);
      expect(find.byType(TimelineScrubber), findsNothing);
      expect(find.byType(ClipTrimmer), findsOneWidget);
    });

    testWidgets('the range opens already selected', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      await enterClipMode(tester);

      // "That, what just happened" is the common case, so it is chosen the moment the mode opens
      // and everything after is adjustment. The button naming the length proves there is one.
      expect(find.textContaining(RegExp(r'^Save these ')), findsOneWidget);
      expect(find.text('Cancel'), findsOneWidget);
    });

    testWidgets('the bar says so, and the ways off the screen go inert', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      await enterClipMode(tester);

      expect(find.text('Choosing a clip'), findsOneWidget);

      // A gear pressed mid-trim would lose a range that took a minute to set. Snapshot goes with
      // it: it acts on the camera now, which is not what the picture is showing.
      final snapshot = tester.widget<NocturneButton>(
        find.widgetWithText(NocturneButton, 'Snapshot'),
      );
      expect(snapshot.onPressed, isNull);

      // And *Save clip* itself is gone — the trimmer carries its own Cancel and Save, and a third
      // way to act on the range would be the only one that does not say what it will do.
      expect(find.text('Save clip'), findsNothing);
    });

    testWidgets('Cancel puts the scrubber back', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      await enterClipMode(tester);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(find.byType(TrimTrack), findsNothing);
      expect(find.byType(TimelineScrubber), findsOneWidget);
      expect(find.text('Save clip'), findsOneWidget);
    });
  });

  group('setting an end', () {
    testWidgets('the nudges move whichever end is lit', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      await enterClipMode(tester);

      String lengthLabel() => tester
          .widget<Text>(find.textContaining(RegExp(r'^Save these ')))
          .data!;

      final before = lengthLabel();

      // The last + is the *To* stepper's, so growing that end lengthens the clip.
      await tester.tap(find.byIcon(PhosphorIconsRegular.plus).last);
      await tester.pumpAndSettle();

      expect(lengthLabel(), isNot(before));
    });

    testWidgets('the picture follows the end being held', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      await enterClipMode(tester);

      // Which end the picture is showing is stated on the video, because setting a start by
      // watching it is the whole reason this is a mode rather than a form.
      expect(find.text('The end of your clip'), findsOneWidget);

      await tester.tap(find.text('From'));
      await tester.pumpAndSettle();

      expect(find.text('The start of your clip'), findsOneWidget);
    });
  });

  group('the save dialog', () {
    testWidgets('the range is a fact by then, and Back to trimming returns', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();
      await enterClipMode(tester);

      await tester.tap(find.textContaining(RegExp(r'^Save these ')));
      await tester.pumpAndSettle();

      expect(find.byType(SaveClipDialog), findsOneWidget);
      expect(find.text('Save this clip'), findsOneWidget);

      // Two decisions, and neither is a time — the range was settled on the screen behind this.
      expect(find.text('Where it goes'), findsOneWidget);
      expect(find.text('Saved clips'), findsOneWidget);

      await tester.tap(find.text('Back to trimming'));
      await tester.pumpAndSettle();

      // Back to the trimmer, not out of the mode: the range survives being looked at.
      expect(find.byType(SaveClipDialog), findsNothing);
      expect(find.byType(TrimTrack), findsOneWidget);
    });
  });

  group('on a phone', () {
    testWidgets('the app bar becomes Choose a clip, with Next', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Clip'));
      await tester.pumpAndSettle();

      expect(find.text('Choose a clip'), findsOneWidget);
      expect(find.text('Next'), findsOneWidget);

      // *Next* rather than *Save*, because there is one more screen.
      expect(find.text('Save'), findsNothing);

      // The X rather than an arrow: leaving discards a range rather than going back anywhere.
      final bar = tester.widget<CompactAppBar>(find.byType(CompactAppBar));
      expect(bar.backTooltip, 'Stop choosing');
    });

    testWidgets('the two big fields are the real control', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Clip'));
      await tester.pumpAndSettle();

      // A finger cannot land on a handle, so whichever field is lit is the end that drags and the
      // end the nudges move. The lit one says so in its own label.
      expect(find.textContaining("the one you're moving"), findsOneWidget);
      expect(find.text('Whole event'), findsOneWidget);
    });

    testWidgets('lays out without overflow', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await tester.pumpWidget(camera('front-door'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Clip'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });
  });
}
