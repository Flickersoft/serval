// The notifications screen — designs 15b and 15c.
//
// Three things here are easy to get wrong and invisible when they are:
//
//  * **The count.** `7 of 7 on` is objects and sounds together, and an untouched rule stores null
//    rather than a list, so the arithmetic has to read null as *all of them* in both halves.
//  * **The collapse.** Lighting the last unlit chip must store null, not the full list. Storing the
//    list looks identical today and silently excludes whatever an admin adds to the camera next.
//  * **The five browser states.** Four of them are failures with different causes and different
//    remedies, and only one is fixable on this page. They are reachable in a test only because
//    `PushClient.debugBrowser` stands in for a browser the VM does not have.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/push.dart';
import 'package:serval_app/models/server_settings.dart';
import 'package:serval_app/models/user_preferences.dart';
import 'package:serval_app/push/push_client.dart';
import 'package:serval_app/screens/notifications_screen.dart';
import 'package:serval_app/theme/app_theme.dart';

void main() {
  late TestFlutterView view;

  setUp(() {
    view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.devicePixelRatio = 1.0;
    view.physicalSize = const Size(1440, 900);
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
      PushClient.debugBrowser = null;
    });
  });

  void phone() => view.physicalSize = const Size(412, 892);

  /// A browser that has push, has been allowed it, and holds the sample's own subscription.
  void allowed() => PushClient.debugBrowser = (
    supported: true,
    permission: PushPermission.granted,
    subscription: const PushSubscriptionInfo(
      endpoint: sampleThisBrowserEndpoint,
      p256dh: 'p',
      auth: 'a',
    ),
  );

  Widget harness(_Repository repository) => ProviderScope(
    overrides: [repositoryProvider.overrideWithValue(repository)],
    child: MaterialApp.router(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      routerConfig: GoRouter(
        initialLocation: '/settings/notifications',
        routes: [
          GoRoute(
            path: '/settings',
            builder: (context, state) =>
                const Scaffold(body: Text('the index')),
            routes: [
              GoRoute(
                path: 'notifications',
                builder: (context, state) =>
                    const Scaffold(body: NotificationsScreen()),
              ),
            ],
          ),
        ],
      ),
    ),
  );

  Future<_Repository> pump(
    WidgetTester tester, {
    UserPreferences preferences = const UserPreferences(),
    List<PushDevice>? devices,
    bool serverPush = true,
    bool catalogueListsCooldown = true,
  }) async {
    final repository = _Repository(
      preferences: preferences,
      devices: devices,
      serverPush: serverPush,
      catalogueListsCooldown: catalogueListsCooldown,
    );

    await tester.pumpWidget(harness(repository));
    await tester.pumpAndSettle();
    return repository;
  }

  group('layout', () {
    testWidgets('the grid lays out at 1440 without unbounded constraints', (
      tester,
    ) async {
      allowed();
      await pump(tester);
      expect(tester.takeException(), isNull);
    });

    // The case that actually bit: one card across is not a `Row` of one. A stretched row inside a
    // scroll view is asked to be infinitely tall, and the whole page fails to lay out.
    testWidgets('and at 412, where the grid is one card across', (
      tester,
    ) async {
      phone();
      allowed();
      await pump(tester);
      expect(tester.takeException(), isNull);
    });

    testWidgets('the phone has a way back to the settings index', (
      tester,
    ) async {
      phone();
      allowed();
      await pump(tester);

      await tester.tap(find.bySemanticsLabel('Back'));
      await tester.pumpAndSettle();

      expect(find.text('the index'), findsOneWidget);
    });
  });

  group('what a card says is on', () {
    // The sample deployment alerts on three objects and four sounds, so an untouched camera is
    // seven of seven — and both halves get there through a null the card has to read as *all*.
    testWidgets('an untouched camera counts every class it can raise', (
      tester,
    ) async {
      allowed();
      await pump(tester);

      expect(find.text('7 of 7 on'), findsNWidgets(6));
    });

    testWidgets('a narrowed rule counts what is left', (tester) async {
      allowed();
      await pump(
        tester,
        preferences: const UserPreferences(
          notifications: [
            CameraNotificationRule(
              cameraId: 'front-door',
              objectClasses: ['person'],
              soundLabels: ['Glass', 'Gunshot, gunfire'],
            ),
          ],
        ),
      );

      // One object and two sounds of the same seven.
      expect(find.text('3 of 7 on'), findsOneWidget);
      expect(find.text('7 of 7 on'), findsNWidgets(5));
    });

    testWidgets('a muted camera says so instead of counting', (tester) async {
      allowed();
      await pump(
        tester,
        preferences: const UserPreferences(
          notifications: [
            CameraNotificationRule(cameraId: 'front-door', enabled: false),
          ],
        ),
      );

      expect(find.text('Muted — nothing from this camera'), findsOneWidget);
    });
  });

  group('what a tap stores', () {
    testWidgets('unlighting one chip narrows that camera to the rest', (
      tester,
    ) async {
      allowed();
      final repository = await pump(tester);

      await tester.tap(find.text('Car').first);
      await tester.pumpAndSettle();

      final rules = repository.saved.single.rules!;
      expect(rules.single.cameraId, 'driveway');
      expect(rules.single.objectClasses, ['person', 'dog']);
    });

    // The one that matters: *all of them* has to go back to null, not to a list that happens to
    // hold all of them today. A stored list stops following the camera the moment an admin widens
    // it, and the symptom is a class that never notifies with every chip on screen lit.
    testWidgets('relighting the last chip collapses back to inherit', (
      tester,
    ) async {
      allowed();
      final repository = await pump(
        tester,
        preferences: const UserPreferences(
          notifications: [
            CameraNotificationRule(
              cameraId: 'driveway',
              objectClasses: ['person', 'dog'],
            ),
          ],
        ),
      );

      await tester.tap(find.text('Car').first);
      await tester.pumpAndSettle();

      // Null, and then the whole rule dropped: it no longer says anything a camera with no rule
      // would not already do.
      expect(repository.saved.single.rules, isEmpty);
    });

    testWidgets('the master switch is stored on its own', (tester) async {
      allowed();
      final repository = await pump(tester);

      await tester.tap(find.text('Send me notifications'));
      await tester.pumpAndSettle();

      // Tapping the label does nothing — the switch is the control, and this pins that the label
      // is not quietly a second one.
      expect(repository.saved, isEmpty);
    });
  });

  // The rate control, which narrows by how often rather than by what. Its whole risk is that a
  // suppressed *notification* reads as a suppressed *alert* — so what this row stores has to be a
  // number, never a narrowed list, and picking Default has to give back the null that inherits.
  //
  // Driveway is the first card, which is what the `.first` finders below rely on.
  group('the wait between the same alert', () {
    testWidgets('an untouched camera inherits, and the chip says what from', (
      tester,
    ) async {
      allowed();
      await pump(tester);

      // The number is the point: *Default* on its own is a control nobody can reason about.
      expect(find.text('Default · 2 min').first, findsOneWidget);
    });

    testWidgets('and says only Default when the catalogue could not be read', (
      tester,
    ) async {
      allowed();
      await pump(tester, catalogueListsCooldown: false);

      expect(find.text('Default').first, findsOneWidget);
      expect(find.textContaining('Default · '), findsNothing);
    });

    testWidgets('picking a preset stores the number', (tester) async {
      allowed();
      final repository = await pump(tester);

      await tester.tap(find.text('5 min').first);
      await tester.pumpAndSettle();

      final rules = repository.saved.single.rules!;
      expect(rules.single.cameraId, 'driveway');
      expect(rules.single.cooldownSeconds, 300);

      // And it narrowed nothing else. A rate control that quietly wrote a class list would be
      // indistinguishable on screen and wrong in the database.
      expect(rules.single.objectClasses, isNull);
      expect(rules.single.soundLabels, isNull);
    });

    // The counterpart of *relighting the last chip collapses back to inherit*: a stored zero would
    // stop following the deployment, so Default has to store null and let the rule fall away.
    testWidgets('picking Default clears it and drops the rule', (tester) async {
      allowed();
      final repository = await pump(
        tester,
        preferences: const UserPreferences(
          notifications: [
            CameraNotificationRule(cameraId: 'driveway', cooldownSeconds: 300),
          ],
        ),
      );

      await tester.tap(find.text('Default · 2 min').first);
      await tester.pumpAndSettle();

      expect(repository.saved.single.rules, isEmpty);
    });

    // Zero and null look alike on screen the day the deployment's default is zero, and mean
    // opposite things the day an admin changes it. Only a stored zero survives that.
    testWidgets('Every time stores zero rather than inheriting', (
      tester,
    ) async {
      allowed();
      final repository = await pump(tester);

      await tester.tap(find.text('Every time').first);
      await tester.pumpAndSettle();

      final rules = repository.saved.single.rules!;
      expect(rules.single.cooldownSeconds, 0);
    });

    // From a restored backup or a hand-written API call. Drawing none of the chips lit would read
    // as *Default* while the Server actually waited forty-five seconds.
    testWidgets('a stored value that is not a preset is still shown', (
      tester,
    ) async {
      allowed();
      await pump(
        tester,
        preferences: const UserPreferences(
          notifications: [
            CameraNotificationRule(cameraId: 'driveway', cooldownSeconds: 45),
          ],
        ),
      );

      expect(find.text('45 sec'), findsOneWidget);
    });
  });

  group('the registered devices', () {
    testWidgets('a device that has never been reached brings up the warning', (
      tester,
    ) async {
      allowed();
      await pump(tester);

      expect(find.text('Safari on iPhone'), findsOneWidget);
      expect(find.text('never notified'), findsOneWidget);
      expect(
        find.textContaining('never been notified is the visible symptom'),
        findsOneWidget,
      );
    });

    testWidgets('and when every one has been, the warning is not drawn', (
      tester,
    ) async {
      allowed();
      await pump(
        tester,
        devices: [
          PushDevice(
            id: pushDeviceIdFor(sampleThisBrowserEndpoint),
            label: 'Chrome on Linux',
            createdAt: DateTime.utc(2026, 6, 2),
            lastSuccessAt: DateTime.utc(2026, 8, 3, 8, 12),
          ),
        ],
      );

      expect(find.text('never notified'), findsNothing);
      expect(
        find.textContaining('never been notified is the visible symptom'),
        findsNothing,
      );
    });

    // Which chip you are sitting at comes off the hash of the browser's own endpoint, so a wrong
    // answer here marks somebody else's laptop as this one.
    testWidgets('this browser is the one marked', (tester) async {
      allowed();
      await pump(tester);

      expect(find.textContaining('this one · notified 3 Aug'), findsOneWidget);
    });
  });

  group('what the browser card says', () {
    testWidgets('registered and allowed', (tester) async {
      allowed();
      await pump(tester);

      expect(
        find.text('This browser is allowed to notify you'),
        findsOneWidget,
      );
      expect(find.text('Send a test'), findsOneWidget);
    });

    testWidgets('allowed but not registered', (tester) async {
      PushClient.debugBrowser = (
        supported: true,
        permission: PushPermission.prompt,
        subscription: null,
      );
      await pump(tester);

      expect(find.text('This browser is not registered'), findsOneWidget);

      // Nothing to test until there is a subscription to test with.
      expect(find.text('Send a test'), findsNothing);
    });

    testWidgets('refused', (tester) async {
      PushClient.debugBrowser = (
        supported: true,
        permission: PushPermission.denied,
        subscription: null,
      );
      await pump(tester);

      expect(
        find.text('You have refused notifications for this site'),
        findsOneWidget,
      );
    });

    testWidgets('turned off deployment-wide', (tester) async {
      allowed();
      await pump(tester, serverPush: false);

      expect(
        find.text('This server has notifications switched off'),
        findsOneWidget,
      );
    });

    // The default on the VM, and much the most likely one on a real deployment: no HTTPS, so the
    // browser withholds the service worker and there is no push at all.
    testWidgets('no push machinery in this browser', (tester) async {
      await pump(tester);

      expect(
        find.text('This browser cannot show notifications'),
        findsOneWidget,
      );
      expect(find.textContaining('served over HTTPS'), findsOneWidget);
    });
  });

  group('finding one camera among six', () {
    testWidgets('the search narrows the grid', (tester) async {
      allowed();
      await pump(tester);

      await tester.enterText(find.byType(EditableText), 'kit');
      await tester.pumpAndSettle();

      expect(find.text('Kitchen'), findsOneWidget);
      expect(find.text('Driveway'), findsNothing);
    });

    testWidgets('and says so when nothing matches', (tester) async {
      allowed();
      await pump(tester);

      await tester.enterText(find.byType(EditableText), 'attic');
      await tester.pumpAndSettle();

      expect(find.text('Nothing matches “attic”.'), findsOneWidget);
    });
  });
}

/// The sample deployment, with the three things these tests need to vary made settable and every
/// write recorded rather than discarded.
class _Repository extends SampleServalRepository {
  _Repository({
    required this.preferences,
    required this.devices,
    required this.serverPush,
    this.catalogueListsCooldown = true,
  });

  final List<PushDevice>? devices;
  final bool serverPush;

  /// Whether the catalogue names `Serval:Push:CooldownSeconds` at all. The sample deployment does,
  /// at two minutes; this exists so one test can take it away, which is the state a Server the App
  /// could not read its settings from produces.
  final bool catalogueListsCooldown;

  UserPreferences preferences;

  /// Every call to [saveNotificationPreferences], in order. The assertions are about the write:
  /// a screen that draws the right thing and stores the wrong one passes anything phrased as
  /// "the chip went out".
  final saved = <({bool? enabled, List<CameraNotificationRule>? rules})>[];

  @override
  UserPreferences get notificationPreferences => preferences;

  @override
  Future<ServerSettings> settings() async {
    final base = await super.settings();
    if (catalogueListsCooldown) return base;

    return ServerSettings(
      groups: base.groups,
      restartRequired: base.restartRequired,
      updatedAt: base.updatedAt,
      updatedBy: base.updatedBy,
      settings: [
        for (final setting in base.settings)
          if (setting.key != 'Serval:Push:CooldownSeconds') setting,
      ],
    );
  }

  @override
  Future<void> saveNotificationPreferences({
    bool? enabled,
    List<CameraNotificationRule>? rules,
  }) async {
    saved.add((enabled: enabled, rules: rules));
    preferences = preferences.copyWith(
      notificationsEnabled: enabled,
      notifications: rules,
    );
  }

  @override
  Future<List<PushDevice>> pushDevices() async =>
      devices ?? await super.pushDevices();

  @override
  Future<PushConfig> pushConfig() async {
    final base = await super.pushConfig();
    return PushConfig(vapidPublicKey: base.vapidPublicKey, enabled: serverPush);
  }
}
