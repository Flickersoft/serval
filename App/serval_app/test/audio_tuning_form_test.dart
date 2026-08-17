import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/camera_record.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_settings_form.dart';
import 'package:serval_app/widgets/nocturne_slider.dart';
import 'package:serval_app/widgets/status_indicators.dart';

/// What the hearing sliders actually store.
///
/// A logarithmic track maps a pixel to a full-precision double, so a drag naturally produces
/// something like `0.003162277660168379` — four meaningful figures and thirteen digits of
/// wherever the pointer happened to land. Storing that is not wrong so much as unreadable: the
/// registry is something people inspect through the API, and the row above the slider is already
/// rounding to whole decibels, so the record and the label would disagree in a way only an API
/// client would ever see.
void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    // Deliberately taller than the real screen. This test is about what a drag stores, not about
    // layout, and a surface that fits the whole form removes the scrolling from the middle of it.
    view.physicalSize = const Size(1440, 3000);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  CameraRecord subject() => CameraRecord.blank().copyWith(
    id: 'testcam',
    name: 'Test',
    aiAudio: true,
    streams: [
      CameraRecord.blank().streams.single.copyWith(
        url: 'rtsp://127.0.0.1:1/main',
      ),
    ],
  );

  /// The number of significant figures in a positive double, as written out shortest-first.
  int significantFigures(double value) {
    final text = value.toString();
    if (text.contains('e')) {
      return text.split('e').first.replaceAll(RegExp(r'[-.]'), '').length;
    }
    return text
        .replaceAll(RegExp(r'[-.]'), '')
        .replaceFirst(RegExp(r'^0+'), '')
        .length;
  }

  Future<CameraRecord?> dragAndSave(
    WidgetTester tester,
    CameraSection section,
    String label,
    double fraction,
  ) async {
    CameraRecord? saved;

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        // No scroll view of our own: the form supplies one, and wrapping it in another hands it
        // an unbounded height that its Expanded cannot resolve.
        home: Scaffold(
          body: CameraSettingsForm(
            record: subject(),
            creating: false,
            health: CameraHealth.healthy,
            knownLocations: const [],
            existingIds: const {},
            onSave: (record) async => saved = record,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    // The form opens on its first section, so the threshold has to be navigated to — and which
    // section it is in is now part of what these tests pin. The two gates carry the *same*
    // catalogue label, *Counts as silence below*, so the section is the only thing that says which
    // one a drag lands on.
    //
    // The index row and the pane heading share the name, so the row is picked by its position in
    // the index rather than by the text alone.
    await tester.tap(find.text(section.title).first, warnIfMissed: false);
    await tester.pumpAndSettle();

    // The row's own Column is the closest Column ancestor of its label, and the slider is the
    // only one inside it — so this cannot pick up a neighbouring threshold's track.
    final row = find
        .ancestor(of: find.text(label), matching: find.byType(Column))
        .first;
    final slider = find.descendant(
      of: row,
      matching: find.byType(NocturneSlider),
    );
    expect(slider, findsOneWidget, reason: 'no slider found for “$label”');

    final box = tester.getRect(slider);
    await tester.tapAt(Offset(box.left + box.width * fraction, box.center.dy));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Save camera'));
    await tester.pumpAndSettle();

    return saved;
  }

  testWidgets('a dragged speech gate is stored to four significant figures', (
    tester,
  ) async {
    final saved = await dragAndSave(
      tester,
      CameraSection.speech,
      'Counts as silence below',
      0.5,
    );

    final stored = saved?.audioTuning?.speechGateRmsThreshold;
    expect(stored, isNotNull, reason: 'the drag should have set a threshold');
    expect(
      significantFigures(stored!),
      lessThanOrEqualTo(4),
      reason:
          'stored $stored, which carries the pointer position rather than a setting',
    );
  });

  testWidgets('a dragged sound gate is stored to four significant figures', (
    tester,
  ) async {
    final saved = await dragAndSave(
      tester,
      CameraSection.sound,
      'Counts as silence below',
      0.6,
    );

    final stored = saved?.audioTuning?.soundGateRmsThreshold;
    expect(stored, isNotNull);
    expect(
      significantFigures(stored!),
      lessThanOrEqualTo(4),
      reason: 'stored $stored',
    );
  });

  /// The percentage on screen has to be the number in the record, or the label and the API
  /// disagree about what was set.
  testWidgets('the speech certainty is snapped to the percent it displays', (
    tester,
  ) async {
    final saved = await dragAndSave(
      tester,
      CameraSection.speech,
      'Speech confidence floor',
      0.5,
    );

    final stored = saved?.audioTuning?.vadThreshold;
    expect(stored, isNotNull);
    expect(
      (stored! * 100) % 1,
      0,
      reason: 'stored $stored, which is not a whole percent',
    );
  });
}
