import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/camera_record.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_settings_form.dart';
import 'package:serval_app/widgets/status_indicators.dart';

/// That editing the per-camera tuning is a change the form will let you save.
///
/// The form works out what changed by naming each field it knows about, and *Save camera* is
/// disabled when that list comes back empty. A section added to the form without a matching line
/// in it renders, accepts input, holds the value — and then sits behind a dead button saying
/// "Everything here matches the Server", which is both wrong and unfixable from the screen.
///
/// That is exactly what happened when the detection, sound and movement sections first landed, and
/// nothing caught it: every other test here asserts on what a widget *stores*, which was correct
/// throughout. It took driving the real app in a browser to see the button never lit up. These
/// pin the wiring so it cannot come loose again.
void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    // Taller than a real screen, so the whole form is laid out and nothing needs scrolling to.
    view.physicalSize = const Size(1440, 4000);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  CameraRecord subject() => CameraRecord.blank().copyWith(
    id: 'testcam',
    name: 'Test',
    aiVision: true,
    aiAudio: true,
    streams: [
      CameraRecord.blank().streams.single.copyWith(
        url: 'rtsp://127.0.0.1:1/main',
      ),
    ],
  );

  /// Pumps the form over [record] and returns a way to read the footer's verdict.
  Future<void> pump(WidgetTester tester, CameraRecord record) async {
    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        home: Scaffold(
          body: CameraSettingsForm(
            record: record,
            creating: false,
            health: CameraHealth.healthy,
            knownLocations: const [],
            existingIds: const {},
            onSave: (_) async {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  /// The Server settings page's own wording, since the save bar is now the same widget.
  const clean = 'No unsaved changes';

  testWidgets('an untouched camera has nothing to save', (tester) async {
    await pump(tester, subject());

    expect(find.text(clean), findsOneWidget);
  });

  group('the footer names an edit to each new section', () {
    /// Renders the form to prove the section is really on screen, then asserts on the comparison
    /// that drives the footer. Both halves are needed: the sections rendered perfectly well while
    /// the comparison ignored them, which is how this shipped broken.
    Future<void> expectNamed(
      WidgetTester tester,
      CameraRecord edited,
      String expected,
    ) async {
      await pump(tester, subject());
      expect(find.text(clean), findsOneWidget);

      expect(
        CameraSettingsForm.changesBetween(subject(), edited),
        contains(expected),
      );
    }

    testWidgets('what it looks for', (tester) async {
      await expectNamed(
        tester,
        subject().copyWith(
          detectionTuning: const DetectionTuningSettings(
            classes: ['person'],
            scoreThreshold: 0.4,
          ),
        ),
        'what it looks for',
      );
    });

    testWidgets('which sounds matter', (tester) async {
      await expectNamed(
        tester,
        subject().copyWith(
          soundTuning: const SoundTuningSettings(alertLabels: ['Siren']),
        ),
        'which sounds matter',
      );
    });

    testWidgets('motion detection', (tester) async {
      await expectNamed(
        tester,
        subject().copyWith(
          motionTuning: const MotionTuningSettings(minChangedFraction: 0.05),
        ),
        'motion detection',
      );
    });

    testWidgets('the speech thresholds still work', (tester) async {
      await expectNamed(
        tester,
        subject().copyWith(
          audioTuning: const AudioTuningSettings(speechGateRmsThreshold: 0.002),
        ),
        'how it listens for speech',
      );
    });

    /// The other half of the audio bag, and the reason it is compared field by field rather than
    /// whole: all three thresholds live in one `AudioTuningSettings`, but they are edited in two
    /// different sections now. Comparing the bag would light up *Speech & transcription* for a
    /// change made in *Sound recognition*.
    testWidgets('the sound gate is named for its own section', (tester) async {
      await expectNamed(
        tester,
        subject().copyWith(
          audioTuning: const AudioTuningSettings(soundGateRmsThreshold: 0.002),
        ),
        'how it listens for sounds',
      );
    });
  });

  group('clearing an override is also a change', () {
    test('dropping the detection bag is noticed', () {
      final tuned = subject().copyWith(
        detectionTuning: const DetectionTuningSettings(classes: ['person']),
      );

      expect(
        CameraSettingsForm.changesBetween(tuned, subject()),
        contains('what it looks for'),
      );
    });

    test('a mask carried but not edited is not a change', () {
      // Opening a camera that has a mask and saving must not read as an edit — that would put a
      // change in the bar of every masked camera.
      final masked = subject().copyWith(
        detectionTuning: const DetectionTuningSettings(
          masks: [
            DetectionMaskSettings(name: 'road', points: [0, 0, 1, 0, 1, 0.3]),
          ],
        ),
      );

      expect(CameraSettingsForm.changesBetween(masked, masked), isEmpty);
    });
  });

  group('masks are their own section', () {
    const mask = DetectionMaskSettings(
      name: 'road',
      points: [0, 0, 1, 0, 1, 0.3],
    );

    CameraRecord withMasks(List<DetectionMaskSettings> masks) => subject()
        .copyWith(detectionTuning: DetectionTuningSettings(masks: masks));

    test('drawing a mask is a change that can be saved', () {
      // The whole point of the editor: without a line naming it, a polygon could be drawn, held,
      // and then never sent.
      expect(
        CameraSettingsForm.changesBetween(subject(), withMasks(const [mask])),
        contains('the masks'),
      );
    });

    test('editing a mask’s classes is a change', () {
      final narrowed = mask.copyWith(classes: const ['car']);

      expect(
        CameraSettingsForm.changesBetween(
          withMasks(const [mask]),
          withMasks([narrowed]),
        ),
        contains('the masks'),
      );
    });

    test('removing the last mask is a change', () {
      expect(
        CameraSettingsForm.changesBetween(withMasks(const [mask]), subject()),
        contains('the masks'),
      );
    });

    test('a mask change is not also a change to what it looks for', () {
      // The two share `detectionTuning` on the wire but are different sections on screen, and the
      // index marks them apart.
      final changes = CameraSettingsForm.changesBySection(
        subject(),
        withMasks(const [mask]),
      );

      expect(changes[CameraSection.masks], contains('the masks'));
      expect(changes[CameraSection.objects], isEmpty);
    });

    test('a threshold change is not also a change to the masks', () {
      final changes = CameraSettingsForm.changesBySection(
        withMasks(const [mask]),
        withMasks(const [mask]).copyWith(
          detectionTuning: const DetectionTuningSettings(
            masks: [mask],
            scoreThreshold: 0.4,
          ),
        ),
      );

      expect(changes[CameraSection.objects], contains('what it looks for'));
      expect(changes[CameraSection.masks], isEmpty);
    });
  });
}
