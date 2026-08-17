import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/screens/cameras_screen.dart';
import 'package:serval_app/widgets/camera_settings_form.dart';
import 'package:serval_app/theme/app_theme.dart';

/// The cameras & settings screen — design 2a.
///
/// It is the one screen that lays out a dense two-column form inside a scroll view, which is
/// where unbounded-constraint bugs live: a `RenderFlex was not laid out` there does not fail
/// anything else, so it needs its own test rather than being caught by the wall's.
void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1440, 900);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  // The screen reads its repository from the container rather than from a constructor argument,
  // so pumping it outside `ServalApp` means supplying the override `ServalApp` would have.
  Widget harness() => ProviderScope(
    overrides: [
      repositoryProvider.overrideWithValue(const SampleServalRepository()),
    ],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: const Scaffold(body: CamerasScreen()),
    ),
  );

  testWidgets('lays out without overflow or unbounded constraints', (
    tester,
  ) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    // Any layout assertion, unbounded constraint or overflow surfaces here rather than as a
    // wall of red at runtime.
    expect(tester.takeException(), isNull);
  });

  testWidgets('groups the registry by where each camera is', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    expect(find.text('FRONT YARD'), findsOneWidget);
    expect(find.text('BACK GARDEN'), findsOneWidget);
    expect(find.text('INSIDE'), findsOneWidget);
    expect(find.text('Driveway'), findsWidgets);
    expect(find.text('Kitchen'), findsOneWidget);
  });

  testWidgets('says what each camera is doing, in the design’s words', (
    tester,
  ) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    // Derived from the record: retention for an ordinary camera, talk-back where it is on, and
    // the plain statement that a disabled camera is off rather than broken.
    expect(find.text('Recording · 7 days kept'), findsWidgets);
    expect(find.text('Recording · talk-back on'), findsOneWidget);
    expect(find.text('Turned off'), findsOneWidget);
  });

  testWidgets('opens the first camera and shows its whole record', (
    tester,
  ) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    // Every section in the index. *General* appears twice — once as the row and once as the
    // heading of the pane it opens onto.
    expect(find.text('General'), findsNWidgets(2));
    for (final section in CameraSection.values) {
      expect(
        find.text(section.title),
        findsWidgets,
        reason: 'the index should list “${section.title}”',
      );
    }
  });

  testWidgets('opening Streams shows the record’s streams', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    await tester.tap(find.text(CameraSection.streams.title).first);
    await tester.pumpAndSettle();

    // Roles are chips, one per job per stream — which is what makes an unassigned role visible.
    expect(find.text('Recording'), findsWidgets);
    expect(find.text('Watching for things'), findsWidgets);
    expect(find.text('Live view'), findsWidgets);
  });

  testWidgets('starts clean, with nothing to save', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    // The Server settings page's wording, since it is now the same save bar.
    expect(find.text('No unsaved changes'), findsOneWidget);
  });

  testWidgets('switching cameras lays out every one of them', (tester) async {
    await tester.pumpWidget(harness());
    await tester.pumpAndSettle();

    // A camera with one stream carrying all three roles lays out differently from one with two,
    // and a camera with no ONVIF endpoint differently again.
    for (final name in [
      'Front door',
      'Side path',
      'Back yard',
      'Garage',
      'Kitchen',
    ]) {
      await tester.tap(find.text(name).last);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull, reason: 'laying out $name');
    }
  });

  testWidgets(
    'adding a camera offers an editable id and refuses to save it empty',
    (tester) async {
      await tester.pumpWidget(harness());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Add'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.text('New camera'), findsWidgets);
      // The id is fixed once a camera exists, so it is only editable here.
      expect(find.text('ID'), findsOneWidget);
      expect(find.text('Give the camera an id.'), findsOneWidget);
    },
  );

  group('speech & transcription', () {
    /// The form opens on its first section, so every test here has to navigate to this one.
    ///
    /// A section is a place you go rather than a place you scroll to, which is the whole point of
    /// the index.
    Future<void> openSection(WidgetTester tester) async {
      await tester.pumpWidget(harness());
      await tester.pumpAndSettle();

      await tester.tap(find.text(CameraSection.speech.title).first);
      await tester.pumpAndSettle();
    }

    testWidgets('renders, and lays out without overflow', (tester) async {
      await openSection(tester);

      expect(tester.takeException(), isNull);
      // Twice: the index row, and the heading of the pane it opened.
      expect(find.text('Speech & transcription'), findsNWidgets(2));

      // Both rows are named by the catalogue rather than by the form. They were hand-labelled
      // once — *Hear speech above*, *How sure it must be that it is speech* — which left the
      // section's own search index looking for words that were nowhere on screen.
      expect(find.text('Counts as silence below'), findsOneWidget);
      expect(find.text('Speech confidence floor'), findsOneWidget);

      // And the sound gate is *not* here. It carries the same catalogue label as the speech gate,
      // which is exactly why the two cannot share a pane: the group is what tells them apart.
      expect(find.text('Alert on these sounds'), findsNothing);
    });

    testWidgets('an untuned camera says it is using the Server’s defaults', (
      tester,
    ) async {
      await openSection(tester);

      // The sample cameras override nothing, so both read as inherited — and neither offers
      // *Use the default*, because there is nothing to revert.
      //
      // Two rather than three: the sound gate moved to *Sound recognition* with the settings it
      // gates, leaving the speech gate and the speech-certainty floor here.
      expect(find.text('the Server’s default'), findsNWidgets(2));
      expect(find.text('Use the default'), findsNothing);
    });

    testWidgets('the meter says so when there is no Server to hear', (
      tester,
    ) async {
      await openSection(tester);

      // SampleServalRepository returns no feed. The rail renders dimmed rather than vanishing,
      // so the layout does not jump when a real Server appears.
      expect(find.text('What this camera hears'), findsOneWidget);
      expect(find.text('no live level'), findsOneWidget);
    });
  });

  // Both figures come from `GET /api/system/stats`. What these pin is that the numbers shown are
  // the measured ones, and that a Server which reports nothing shows nothing rather than a guess.
  group('how much disk this is taking', () {
    testWidgets(
      'the registry footer reports the volume rather than "not reported"',
      (tester) async {
        await tester.pumpWidget(harness());
        await tester.pumpAndSettle();

        // The design's own string, from real fields.
        expect(find.text('1.8 TB of 4 TB'), findsOneWidget);
        expect(find.text('not reported'), findsNothing);
      },
    );

    testWidgets('the retention slider says what this camera is actually holding', (
      tester,
    ) async {
      await tester.pumpWidget(harness());
      await tester.pumpAndSettle();

      await tester.tap(find.text(CameraSection.recording.title).first);
      await tester.pumpAndSettle();

      // Measured, not estimated from a bitrate. The projection is absent because the slider is
      // still at the span the measurement covers; it appears once you drag it somewhere else.
      expect(find.text('Holding 412 GB now, about 59 GB/day.'), findsOneWidget);
    });
  });
}
