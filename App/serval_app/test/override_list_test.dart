import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/server_camera_defaults.dart';
import 'package:serval_app/models/server_settings.dart';
import 'package:serval_app/widgets/camera_tuning_sections.dart';

/// What a per-camera label list means by its three states, and that a label is taken whole.
///
/// The control is now [CameraSettingCard] over the same chip row the Server settings page uses.
/// Three things about it are easy to lose and expensive to lose:
///
/// **A label is never split on punctuation.** "Gunshot, gunfire" is one AudioSet label. Splitting
/// it asks the detector for two things that do not exist, and the failure is silent — the list
/// simply stops matching.
///
/// **Overriding with nothing is not the same as overriding nothing.** Null means follow the
/// Server, and the Server's list stands behind the row in ghost chips. An empty list means record
/// none of it, which is a real instruction, and drawing the Server's labels behind *that* would
/// say the opposite of what is stored.
///
/// **The Server's list is never drawn as this camera's.** A real chip says "this camera names
/// this", so a camera following the Server draws none — otherwise typing the first label would
/// read as appending to a list this camera never had.
void main() {
  const fallback = ['person', 'car', 'dog'];

  /// A catalogue holding the Server's own list for *Record these objects*.
  final defaults = ServerCameraDefaults.from(
    ServerSettings(
      groups: const ['What it looks for'],
      restartRequired: false,
      settings: const [
        ServerSetting(
          key: 'Serval:Ai:Detection:Classes',
          group: 'What it looks for',
          label: 'Record these objects',
          help: 'Everything else this camera sees is ignored.',
          kind: SettingKind.textList,
          source: SettingSource.builtIn,
          restartRequired: false,
          value: fallback,
        ),
      ],
    ),
  );

  Future<List<String>?> pump(
    WidgetTester tester, {
    required List<String>? value,
  }) async {
    List<String>? emitted;

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        home: Scaffold(
          body: CameraSettingCard(
            field: CameraSetting.detectionClasses,
            defaults: defaults,
            value: value,
            onChanged: (items) => emitted = items as List<String>?,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    return emitted;
  }

  /// The add chip is the only place text can be typed, so this is unambiguous.
  Future<void> type(WidgetTester tester, String label) async {
    await tester.enterText(find.byType(EditableText), label);
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
  }

  testWidgets('following the Server shows its list as ghosts', (tester) async {
    await pump(tester, value: null);

    // The Server page's own chip, for the same state.
    expect(find.text('using the default'), findsOneWidget);
    for (final label in fallback) {
      expect(find.text(label), findsOneWidget);
    }

    // Nothing to take back, so nothing offers to.
    expect(find.text('Use the default list'), findsNothing);
  });

  testWidgets('an override of nothing is not the Server’s list', (
    tester,
  ) async {
    await pump(tester, value: const []);

    expect(find.text('changed here'), findsOneWidget);
    expect(find.text('Use the default list'), findsOneWidget);
    for (final label in fallback) {
      expect(find.text(label), findsNothing);
    }
  });

  testWidgets('an override draws its own labels', (tester) async {
    await pump(tester, value: const ['bicycle', 'truck']);

    expect(find.text('changed here'), findsOneWidget);
    expect(find.text('bicycle'), findsOneWidget);
    expect(find.text('truck'), findsOneWidget);
    // The Server's list stopped applying the moment this one existed.
    expect(find.text('person'), findsNothing);
  });

  testWidgets('typing the first label turns following into overriding', (
    tester,
  ) async {
    List<String>? emitted;

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        home: Scaffold(
          body: CameraSettingCard(
            field: CameraSetting.detectionClasses,
            defaults: defaults,
            value: null,
            onChanged: (items) => emitted = items as List<String>?,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await type(tester, 'bicycle');

    // Not the fallback plus one: choosing a label replaces the built-in set whole.
    expect(emitted, ['bicycle']);
  });

  testWidgets('a comma in a label is not a separator', (tester) async {
    List<String>? emitted;

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        home: Scaffold(
          body: CameraSettingCard(
            field: CameraSetting.detectionClasses,
            defaults: defaults,
            value: const [],
            onChanged: (items) => emitted = items as List<String>?,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await type(tester, '  Gunshot, gunfire  ');

    expect(emitted, ['Gunshot, gunfire']);
  });

  testWidgets('Use the default list clears the override', (tester) async {
    Object? emitted = 'untouched';

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        home: Scaffold(
          body: CameraSettingCard(
            field: CameraSetting.detectionClasses,
            defaults: defaults,
            value: const ['bicycle'],
            onChanged: (items) => emitted = items,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Use the default list'));

    // Null is the instruction "stop overriding", not "override with nothing".
    expect(emitted, isNull);
  });
}
