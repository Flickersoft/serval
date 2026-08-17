import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/server_settings.dart';
import 'package:serval_app/screens/settings_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/nocturne_button.dart';
import 'package:serval_app/widgets/nocturne_field.dart';

/// The *Server settings* page — what this Server is told to do.
///
/// What these pin is the part that is easy to get subtly wrong and impossible to notice: whether a
/// changed setting says it is changed, whether *Use the default* appears only when there is
/// something to revert, and whether the page says what a reset would restore. A settings screen
/// that quietly shows the wrong provenance is worse than one that shows none, because it invites
/// someone to reset a value that was never theirs.
///
/// The rest is about the shape of the page: one group at a time, so a change made in a group
/// nobody is looking at has to announce where it is.
void main() {
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1280, 1400);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  });

  ServerSetting setting({
    String key = 'Serval:Media:RetentionDays',
    String group = 'Recording & storage',
    String label = 'Keep recordings for',
    SettingKind kind = SettingKind.integer,
    SettingSource source = SettingSource.builtIn,
    bool restartRequired = false,
    Object? value = 7,
    Object? defaultValue = 7,
    double? min = 1,
    double? max = 365,
    String? unit = 'days',
    List<String>? fallback,
    List<String>? choices,
    Map<String, String> unavailableChoices = const {},
    SettingDependency? appliesWhen,
  }) => ServerSetting(
    key: key,
    group: group,
    label: label,
    help: 'How long footage is kept before the sweep deletes it.',
    kind: kind,
    source: source,
    restartRequired: restartRequired,
    value: value,
    defaultValue: defaultValue,
    min: min,
    max: max,
    unit: unit,
    fallback: fallback,
    choices: choices,
    unavailableChoices: unavailableChoices,
    appliesWhen: appliesWhen,
  );

  ServerSettings settings(
    List<ServerSetting> entries, {
    bool restartRequired = false,
    List<String> groups = const ['Recording & storage'],
  }) => ServerSettings(
    groups: groups,
    settings: entries,
    restartRequired: restartRequired,
  );

  /// The page with nothing staged — the state it opens in.
  ///
  /// [staged] stands in for the real screen's draft. `inapplicable` is resolved through the same
  /// `valueOf` the controls read, which is the contract under test: a dependency has to be judged
  /// against the value being edited, not the one the Server is running.
  Widget harness(
    ServerSettings? loaded, {
    int changedCount = 0,
    String? error,
    Map<String, Object?> staged = const {},
  }) {
    Object? valueOf(ServerSetting s) =>
        staged.containsKey(s.key) ? staged[s.key] : s.value;

    String? inapplicable(ServerSetting s) {
      final rule = s.appliesWhen;
      if (rule == null) return null;

      final controlling = loaded?.byKey(rule.key);
      if (controlling == null) return null;

      return rule.satisfiedBy(valueOf(controlling)) ? null : rule.reason;
    }

    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(
        body: SettingsScreenBody(
          settings: loaded,
          error: error,
          changedCount: changedCount,
          valueOf: valueOf,
          isPending: staged.containsKey,
          inapplicable: inapplicable,
          onChanged: (_, _) {},
          onReset: (_) {},
          onSave: () {},
          onDiscard: () {},
        ),
      ),
    );
  }

  testWidgets('lays out without overflow or unbounded constraints', (
    tester,
  ) async {
    await tester.pumpWidget(
      harness(
        settings([
          setting(),
          // A card that takes the whole row, whose reset names a value: the shape where the link
          // and the explanation share a line.
          setting(
            key: 'Serval:Ai:Vision:MotionPrompt',
            label: 'What to ask about several frames',
            kind: SettingKind.text,
            source: SettingSource.user,
            value: 'What changed?',
            defaultValue:
                'These are {count} consecutive frames from one security '
                'camera, {seconds} seconds apart. Describe the scene and '
                'what is happening or changing between them, in one or two '
                'sentences.',
            min: null,
            max: null,
            unit: null,
          ),
        ]),
      ),
    );
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
  });

  testWidgets('says it is still reading before anything has arrived', (
    tester,
  ) async {
    await tester.pumpWidget(harness(null));
    await tester.pumpAndSettle();

    expect(find.text('Reading this Server’s settings…'), findsOneWidget);
  });

  testWidgets('shows every setting with its explanation in the flow', (
    tester,
  ) async {
    await tester.pumpWidget(harness(settings([setting()])));
    await tester.pumpAndSettle();

    // The help text is drawn, not hidden behind a hover — see the screen's own doc for why.
    expect(find.text('Keep recordings for'), findsOneWidget);
    expect(
      find.text('How long footage is kept before the sweep deletes it.'),
      findsOneWidget,
    );
  });

  group('where a value came from', () {
    testWidgets('an untouched setting says so and offers nothing to reset', (
      tester,
    ) async {
      await tester.pumpWidget(harness(settings([setting()])));
      await tester.pumpAndSettle();

      expect(find.text('using the default'), findsOneWidget);
      expect(find.textContaining('Use the default'), findsNothing);
    });

    testWidgets('a deployment-set value says so', (tester) async {
      await tester.pumpWidget(
        harness(settings([setting(source: SettingSource.deployment)])),
      );
      await tester.pumpAndSettle();

      expect(find.text('set by this deployment'), findsOneWidget);
      // Still nothing to reset: this is not a change anyone made here.
      expect(find.textContaining('Use the default'), findsNothing);
    });

    testWidgets('a value changed here can be reset, and says to what', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings([
            setting(source: SettingSource.user, value: 30, defaultValue: 7),
          ]),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('changed here'), findsOneWidget);

      // The figure is on the link itself: a reset that only promises a change, without saying
      // what to, is a guess.
      expect(find.text('Use the default · 7'), findsOneWidget);
    });

    testWidgets('a default short enough to read is named whole', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings([
            setting(
              key: 'Serval:Ai:Vision:Prompt',
              label: 'What to ask about one frame',
              kind: SettingKind.text,
              source: SettingSource.user,
              value: 'What is in this picture?',
              defaultValue: 'Describe what you see in one sentence.',
              min: null,
              max: null,
              unit: null,
            ),
          ]),
        ),
      );
      await tester.pumpAndSettle();

      expect(
        find.text('Use the default · Describe what you see in one sentence.'),
        findsOneWidget,
      );
    });

    testWidgets('a default that runs to a paragraph is cut to a phrase', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings([
            setting(
              key: 'Serval:Ai:Vision:MotionPrompt',
              label: 'What to ask about several frames',
              kind: SettingKind.text,
              source: SettingSource.user,
              value: 'What changed?',
              defaultValue:
                  'These are {count} consecutive frames from one security '
                  'camera, {seconds} seconds apart. Describe the scene and '
                  'what is happening or changing between them, in one or two '
                  'sentences.',
              min: null,
              max: null,
              unit: null,
            ),
          ]),
        ),
      );
      await tester.pumpAndSettle();

      // The link is laid out at the width its text asks for, so a whole prompt on it would leave
      // the explanation beside it a column one word wide.
      expect(
        find.text('Use the default · These are {count} consecutive frames…'),
        findsOneWidget,
      );
    });
  });

  group('unsaved changes', () {
    testWidgets('the bar is there with nothing staged, and says so', (
      tester,
    ) async {
      await tester.pumpWidget(harness(settings([setting()])));
      await tester.pumpAndSettle();

      // Always drawn, so the page does not move under the hand that made the first edit.
      expect(find.text('Save settings'), findsOneWidget);
      expect(find.text('No unsaved changes'), findsOneWidget);

      // With nothing to send, neither button does anything.
      final save = tester.widget<NocturneButton>(
        find.widgetWithText(NocturneButton, 'Save settings'),
      );
      final discard = tester.widget<NocturneButton>(
        find.widgetWithText(NocturneButton, 'Discard'),
      );
      expect(save.onPressed, isNull);
      expect(discard.onPressed, isNull);
    });

    testWidgets('a staged change raises the bar and marks the field', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings([setting()]),
          changedCount: 1,
          staged: const {'Serval:Media:RetentionDays': 21},
        ),
      );
      await tester.pumpAndSettle();

      // Named, not merely counted: with one group on screen at a time, the edit may be in a
      // group the person is not looking at.
      expect(
        find.text(
          '1 setting changed, not yet saved — in “Recording & storage”',
        ),
        findsOneWidget,
      );
      expect(find.text('Save settings'), findsOneWidget);
      expect(find.text('not saved'), findsOneWidget);
    });

    testWidgets('changes in one group are counted together', (tester) async {
      await tester.pumpWidget(
        harness(
          settings([
            setting(),
            setting(
              key: 'Serval:Ingest:SegmentSeconds',
              label: 'Segment length',
            ),
          ]),
          changedCount: 2,
          staged: const {
            'Serval:Media:RetentionDays': 21,
            'Serval:Ingest:SegmentSeconds': 8,
          },
        ),
      );
      await tester.pumpAndSettle();

      expect(
        find.text(
          '2 settings changed, not yet saved — both in “Recording & storage”',
        ),
        findsOneWidget,
      );
    });

    testWidgets('a staged change can be taken back before saving', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings([setting()]),
          changedCount: 1,
          staged: const {'Serval:Media:RetentionDays': 21},
        ),
      );
      await tester.pumpAndSettle();

      // Resettable while staged, even though the Server still calls it untouched — otherwise
      // there would be no way back from a change made and not yet sent.
      expect(find.text('Use the default · 7'), findsOneWidget);
      expect(find.text('Discard'), findsOneWidget);
    });
  });

  group('settings that need a restart', () {
    testWidgets('are marked on the field itself', (tester) async {
      await tester.pumpWidget(
        harness(settings([setting(restartRequired: true)])),
      );
      await tester.pumpAndSettle();

      expect(find.text('needs a restart'), findsOneWidget);
    });

    testWidgets('say on the card itself when one is stored but not in use', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings([
            setting(
              label: 'Recognise sounds',
              restartRequired: true,
              source: SettingSource.user,
            ),
          ], restartRequired: true),
        ),
      );
      await tester.pumpAndSettle();

      // On the field, where the change was made, rather than in a banner at the top of a page
      // someone may have scrolled past.
      expect(find.textContaining('Saved, but not in use'), findsOneWidget);

      // And on the group in the list, so it is visible from a group that is not open.
      expect(find.text('restart'), findsOneWidget);
    });

    testWidgets('say nothing about a restart when nothing is pending', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(settings([setting(restartRequired: true)])),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Saved, but not in use'), findsNothing);
      expect(find.text('restart'), findsNothing);
    });
  });

  testWidgets('a list the Server is falling back on names what it is using', (
    tester,
  ) async {
    await tester.pumpWidget(
      harness(
        settings([
          setting(
            key: 'Serval:Ai:Sound:AlertLabels',
            label: 'Alert on these sounds',
            kind: SettingKind.textList,
            value: const <String>[],
            defaultValue: const <String>[],
            min: null,
            max: null,
            unit: null,
            fallback: const ['Glass', 'Siren'],
          ),
        ]),
      ),
    );
    await tester.pumpAndSettle();

    // Drawn in the row the labels themselves occupy, which is where someone looks for what a list
    // holds — an empty row would say nothing about a setting that is quietly doing something.
    expect(find.text('Glass'), findsOneWidget);
    expect(find.text('Siren'), findsOneWidget);
  });

  testWidgets('the built-in list gives way to a list someone has chosen', (
    tester,
  ) async {
    await tester.pumpWidget(
      harness(
        settings([
          setting(
            key: 'Serval:Ai:Sound:AlertLabels',
            label: 'Alert on these sounds',
            kind: SettingKind.textList,
            value: const ['Dog'],
            defaultValue: const <String>[],
            min: null,
            max: null,
            unit: null,
            fallback: const ['Glass', 'Siren'],
          ),
        ]),
      ),
    );
    await tester.pumpAndSettle();

    // The built-in set applies whole or not at all, so it must not sit alongside a chosen label
    // looking like part of the same list.
    expect(find.text('Dog'), findsOneWidget);
    expect(find.text('Glass'), findsNothing);
    expect(find.text('Siren'), findsNothing);
  });

  group('one group at a time', () {
    ServerSettings twoGroups() => settings(
      groups: const ['Recording & storage', 'What it looks for'],
      [
        setting(),
        setting(
          key: 'Serval:Ai:Detection:ScoreThreshold',
          group: 'What it looks for',
          label: 'Confidence floor',
          kind: SettingKind.number,
          value: 0.35,
          defaultValue: 0.35,
          min: 0,
          max: 1,
          unit: null,
        ),
      ],
    );

    testWidgets('opens on the first group and draws only that one', (
      tester,
    ) async {
      await tester.pumpWidget(harness(twoGroups()));
      await tester.pumpAndSettle();

      // The name appears twice for the open group — its row in the list, and the pane's heading.
      expect(find.text('Recording & storage'), findsNWidgets(2));
      expect(find.text('Keep recordings for'), findsOneWidget);
      expect(find.text('Confidence floor'), findsNothing);
    });

    testWidgets('picking a group fills the pane with it', (tester) async {
      await tester.pumpWidget(harness(twoGroups()));
      await tester.pumpAndSettle();

      await tester.tap(find.text('What it looks for'));
      await tester.pumpAndSettle();

      expect(find.text('Confidence floor'), findsOneWidget);
      expect(find.text('Keep recordings for'), findsNothing);
    });

    testWidgets('a search leaves the groups that hold a match', (tester) async {
      await tester.pumpWidget(harness(twoGroups()));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(EditableText).first, 'confidence');
      await tester.pumpAndSettle();

      // The group with nothing matching drops out rather than opening onto an empty pane.
      expect(find.text('Recording & storage'), findsNothing);
      expect(find.text('Confidence floor'), findsOneWidget);
    });
  });

  testWidgets('a list is edited as chips, one label at a time', (tester) async {
    final added = <List<String>>[];

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildServalTheme(),
        home: Scaffold(
          body: SettingsScreenBody(
            settings: settings([
              setting(
                key: 'Serval:Ai:Detection:Classes',
                label: 'Record these objects',
                kind: SettingKind.textList,
                value: const ['person', 'car'],
                defaultValue: const <String>[],
                min: null,
                max: null,
                unit: null,
              ),
            ]),
            valueOf: (s) => s.value,
            isPending: (_) => false,
            onChanged: (_, value) => added.add(value! as List<String>),
            onReset: (_) {},
            onSave: () {},
            onDiscard: () {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('person'), findsOneWidget);
    expect(find.text('car'), findsOneWidget);

    // A label goes in whole. "Gunshot, gunfire" is one AudioSet label, so nothing is split on
    // punctuation on the way in.
    await tester.enterText(find.byType(EditableText).last, 'Gunshot, gunfire');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();

    expect(added, [
      ['person', 'car', 'Gunshot, gunfire'],
    ]);
  });

  group('typing into a number', () {
    /// The screen with a real draft behind it, so what is typed goes up, is staged, and comes
    /// back down the way it does against a Server. The bug this pins only exists on the round
    /// trip: with the staged value thrown away, every field types fine.
    Widget staging(ServerSettings loaded) => MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(
        body: StatefulBuilder(
          builder: (context, setState) {
            final draft = <String, Object?>{};

            return StatefulBuilder(
              builder: (context, rebuild) => SettingsScreenBody(
                settings: loaded,
                changedCount: draft.length,
                valueOf: (s) =>
                    draft.containsKey(s.key) ? draft[s.key] : s.value,
                isPending: draft.containsKey,
                onChanged: (s, value) => rebuild(() => draft[s.key] = value),
                onReset: (s) => rebuild(() => draft[s.key] = null),
                onSave: () {},
                onDiscard: () {},
              ),
            );
          },
        ),
      ),
    );

    Finder box() => find.descendant(
      of: find.byType(NocturneField),
      matching: find.byType(EditableText),
    );

    testWidgets('a fractional setting takes more than one character', (
      tester,
    ) async {
      await tester.pumpWidget(
        staging(
          settings([
            setting(
              key: 'Serval:Vitals:DiskScanMinutes',
              label: 'Measure per-camera disk use every',
              kind: SettingKind.number,
              value: 15.0,
              defaultValue: 15.0,
              min: 0,
              max: 1440,
              unit: 'minutes',
            ),
          ]),
        ),
      );
      await tester.pumpAndSettle();

      // Typed a character at a time, as a person does. "3" stages 3.0, which prints as "3.0" —
      // writing that back into the box mid-word eats the next keystroke.
      await tester.enterText(box(), '3');
      await tester.pump();
      await tester.enterText(box(), '30');
      await tester.pump();

      expect(tester.widget<EditableText>(box()).controller.text, '30');
    });

    testWidgets('a decimal point survives being typed', (tester) async {
      await tester.pumpWidget(
        staging(
          settings([
            setting(
              key: 'Serval:Ingest:SegmentSeconds',
              label: 'Segment length',
              kind: SettingKind.number,
              value: 4.0,
              defaultValue: 4.0,
              min: 1,
              max: 600,
              unit: 'seconds',
            ),
          ]),
        ),
      );
      await tester.pumpAndSettle();

      // "0." parses to 0.0 and prints as "0.0"; the box has to keep what is typed until the
      // number it means actually changes, or the point is swallowed and no fraction can be
      // entered at all.
      await tester.enterText(box(), '0');
      await tester.pump();
      await tester.enterText(box(), '0.');
      await tester.pump();

      expect(tester.widget<EditableText>(box()).controller.text, '0.');

      await tester.enterText(box(), '0.5');
      await tester.pump();

      expect(tester.widget<EditableText>(box()).controller.text, '0.5');
    });

    testWidgets('a value that moves underneath the box replaces it', (
      tester,
    ) async {
      await tester.pumpWidget(
        staging(
          settings([
            setting(
              key: 'Serval:Media:RetentionDays',
              source: SettingSource.user,
              value: 30,
              defaultValue: 7,
            ),
          ]),
        ),
      );
      await tester.pumpAndSettle();

      await tester.enterText(box(), '21');
      await tester.pump();

      // A reset is a change the box did not make, so it does take the new text — with a caret,
      // which is the other half of the same bug.
      await tester.tap(find.text('Use the default · 7'));
      await tester.pumpAndSettle();

      final editor = tester.widget<EditableText>(box());
      expect(editor.controller.text, '');
      expect(editor.controller.selection.isValid, isTrue);
    });
  });

  testWidgets('a refused save is shown in the Server’s own words', (
    tester,
  ) async {
    await tester.pumpWidget(
      harness(
        settings([setting()]),
        error: 'Keep recordings for cannot be below 1 days.',
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.text('Keep recordings for cannot be below 1 days.'),
      findsOneWidget,
    );
  });

  /// What the chosen device does and does not use.
  ///
  /// Two cards sat side by side for a long time saying contradictory things about where detection
  /// ran, because a setting the running configuration ignores looks exactly like one that works.
  /// These pin the two halves of the answer: a setting nothing reads says so in place of its help,
  /// and it says it about the device *being picked* rather than the one already saved.
  group('a setting the chosen device does not read', () {
    const reason = 'An Edge TPU runs one inference at a time.';

    List<ServerSetting> detection({required Object device}) => [
      setting(
        key: 'Serval:Ai:Detection:Device',
        group: 'Detection engine',
        label: 'Detection runs on',
        kind: SettingKind.choice,
        value: device,
        defaultValue: 'onnx-cpu',
        min: null,
        max: null,
        unit: null,
        choices: const ['onnx-cpu', 'onnx-cuda', 'tflite-edgetpu'],
        unavailableChoices: const {
          'onnx-cuda': 'This image’s ONNX Runtime has no cuda provider.',
        },
      ),
      setting(
        key: 'Serval:Ai:Detection:MaxConcurrency',
        group: 'Detection engine',
        label: 'Detections at once',
        value: 0,
        defaultValue: 0,
        min: 0,
        max: 8,
        unit: null,
        appliesWhen: const SettingDependency(
          key: 'Serval:Ai:Detection:Device',
          values: ['onnx-cpu', 'onnx-cuda'],
          reason: reason,
        ),
      ),
    ];

    testWidgets('says so in place of its explanation', (tester) async {
      await tester.pumpWidget(
        harness(
          settings(
            detection(device: 'tflite-edgetpu'),
            groups: const ['Detection engine'],
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Still on the page, still named, with the reason where the help was — dimmed rather than
      // dropped, so a deployment setting an ignored value can still be seen setting it.
      expect(find.text('Detections at once'), findsOneWidget);
      expect(find.text(reason), findsOneWidget);
    });

    testWidgets('comes back the moment a device that uses it is picked', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings(
            detection(device: 'tflite-edgetpu'),
            groups: const ['Detection engine'],
          ),
          // Chosen and not yet saved. Judging by the Server's value would leave this dimmed until a
          // restart — which is exactly when it can no longer be set.
          staged: const {'Serval:Ai:Detection:Device': 'onnx-cpu'},
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text(reason), findsNothing);
    });

    testWidgets('a device this image cannot run is offered with its reason', (
      tester,
    ) async {
      await tester.pumpWidget(
        harness(
          settings(
            detection(device: 'onnx-cpu'),
            groups: const ['Detection engine'],
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('onnx-cpu').last);
      await tester.pumpAndSettle();

      // Listed rather than hidden: a list that silently shrinks leaves somebody wondering whether
      // they misremembered the name.
      expect(find.text('onnx-cuda'), findsOneWidget);
      expect(
        find.text('This image’s ONNX Runtime has no cuda provider.'),
        findsOneWidget,
      );
    });
  });
}
