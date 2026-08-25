import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/camera_record.dart';
import 'package:serval_app/models/server_camera_defaults.dart';
import 'package:serval_app/models/server_settings.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/camera_settings_form.dart';
import 'package:serval_app/widgets/nocturne_toggle.dart';
import 'package:serval_app/widgets/status_indicators.dart';

/// *Look for objects* in *Analysis*: the one capability card that overrides a Server-wide switch
/// rather than being a fact the camera holds alone.
///
/// The other two cards are plain bools with two states. This one has three — on, off, and *not
/// said*, which follows the Server — and a two-state switch cannot show the difference between a
/// camera that chose *on* and one that is only following. So it draws the chip and the reset link
/// every other overridable camera setting draws, and its switch shows the *effective* value.
///
/// The failure this pins is quiet: a card that always drew *changed here*, or one whose switch read
/// its own null as *off*, would look right on a Server that detects and lie about every camera on
/// one that does not.
void main() {
  ServerCameraDefaults defaultsWith({required bool serverDetects}) =>
      ServerCameraDefaults.from(
        ServerSettings(
          groups: const ['AI'],
          restartRequired: false,
          settings: [
            ServerSetting(
              key: 'Serval:Ai:Detection:Enabled',
              group: 'AI',
              label: 'Look for objects',
              help: 'Runs the detector on this camera.',
              kind: SettingKind.boolean,
              source: SettingSource.builtIn,
              restartRequired: true,
              value: serverDetects,
            ),
          ],
        ),
      );

  CameraRecord subject({bool? detection}) => CameraRecord.blank().copyWith(
    id: 'testcam',
    name: 'Test',
    aiVision: true,
    aiAudio: true,
    detectionTuning: detection == null
        ? null
        : DetectionTuningSettings(enabled: detection),
    streams: [
      CameraRecord.blank().streams.single.copyWith(
        url: 'rtsp://127.0.0.1:1/main',
      ),
    ],
  );

  Future<CameraRecord?> pump(
    WidgetTester tester, {
    required CameraRecord record,
    required bool serverDetects,
  }) async {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1440, 4000);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });

    CameraRecord? saved;
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
            defaults: defaultsWith(serverDetects: serverDetects),
            onSave: (edited) async => saved = edited,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    // One section is on screen at a time and the index opens on the first, so every assertion below
    // has to walk to *Analysis* first.
    await tester.tap(find.text('Analysis').first);
    await tester.pumpAndSettle();

    return saved;
  }

  testWidgets('Analysis draws three capability cards', (tester) async {
    await pump(tester, record: subject(), serverDetects: true);

    expect(find.byType(CapabilityCard), findsNWidgets(3));
    expect(find.text('Look for objects'), findsOneWidget);
  });

  testWidgets('an untouched camera is following the Server', (tester) async {
    await pump(tester, record: subject(), serverDetects: true);

    expect(find.text('using the default'), findsWidgets);
    // Nothing to restore while nothing is overridden.
    expect(find.textContaining('Use the default ·'), findsNothing);
  });

  testWidgets('the switch shows what the camera is running on', (tester) async {
    // The card's own value is null in both of these; what differs is the Server behind it, and the
    // switch has to show that rather than its own absence of an answer.
    await pump(tester, record: subject(), serverDetects: true);
    expect(_detectionSwitch(tester).value, isTrue);

    await pump(tester, record: subject(), serverDetects: false);
    expect(_detectionSwitch(tester).value, isFalse);
  });

  testWidgets('switching it off says the camera chose that', (tester) async {
    await pump(tester, record: subject(), serverDetects: true);

    await tester.tap(find.byWidget(_detectionSwitch(tester)));
    await tester.pumpAndSettle();

    expect(_detectionSwitch(tester).value, isFalse);
    expect(find.text('changed here'), findsOneWidget);
    expect(find.text('Use the default · on'), findsOneWidget);
  });

  testWidgets('the reset link goes back to following the Server', (
    tester,
  ) async {
    await pump(tester, record: subject(detection: false), serverDetects: true);

    expect(find.text('changed here'), findsOneWidget);

    await tester.tap(find.text('Use the default · on'));
    await tester.pumpAndSettle();

    // Back to the Server's answer, and saying so.
    expect(_detectionSwitch(tester).value, isTrue);
    expect(find.textContaining('Use the default ·'), findsNothing);
  });

  testWidgets('a Server that is not detecting warns rather than pretends', (
    tester,
  ) async {
    // Switching a camera on cannot load a model the Server never opened, which is the one thing
    // about these two settings that does not read the way it works.
    await pump(tester, record: subject(), serverDetects: false);

    expect(
      find.textContaining('off on the Server', findRichText: true),
      findsOneWidget,
    );
  });
}

/// The middle card's switch — the three sit in Analysis in the order descriptions, objects, audio.
NocturneToggle _detectionSwitch(WidgetTester tester) => tester.widget(
  find.descendant(
    of: find.ancestor(
      of: find.text('Look for objects'),
      matching: find.byType(CapabilityCard),
    ),
    matching: find.byType(NocturneToggle),
  ),
);
