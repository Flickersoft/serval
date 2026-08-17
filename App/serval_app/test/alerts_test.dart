// The alert queue.
//
// The goldens pin what 14a-14d look like. This pins what makes a queue a queue rather than a
// library: that seen rows stay and dismissed ones leave, that clearing is instant, that a camera
// which was not recording loses only the offer to open the recording, and that a sound alert is a
// row like any other.
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/data/providers.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_repository.dart';
import 'package:serval_app/models/alert.dart';
import 'package:serval_app/router/serval_router.dart';
import 'package:serval_app/screens/alert_screen.dart';
import 'package:serval_app/screens/alerts_screen.dart';
import 'package:serval_app/theme/app_theme.dart';
import 'package:serval_app/widgets/alert_detail_pane.dart';
import 'package:serval_app/widgets/alert_row.dart';
import 'package:serval_app/widgets/icon_rail.dart';
import 'package:serval_app/widgets/serval_shell.dart';

/// Records what the screen asked the Server to do, so the tests can check the call rather than
/// only the pixels — dismissing has no visible difference from a row that failed to render.
class _RecordingRepository extends SampleServalRepository {
  _RecordingRepository();

  final List<String> dismissed = [];
  final List<String> read = [];
  int dismissAllCalls = 0;

  @override
  Future<void> dismissAlert(String id) async => dismissed.add(id);

  @override
  Future<void> markAlertRead(String id) async => read.add(id);

  @override
  Future<int> dismissAllAlerts({String? cameraId}) async {
    dismissAllCalls++;
    return 0;
  }
}

/// An alert whose clip is still being cut, and a way to say that it no longer is.
///
/// The state every notification tap arrives in: the push goes out on the raise and the clip lands
/// some sixteen seconds later, so the first read of an alert opened from one has no poster and
/// nothing to play. The sample fixtures are all settled already and immutable, so this is the only
/// way to put a screen in that state and then take it out again.
class _SettlingRepository extends SampleServalRepository {
  _SettlingRepository();

  final _updates = StreamController<Alert>.broadcast();

  /// Every poster asked for, as `id@state`, in order.
  ///
  /// The state is recorded because it is the part that is easy to get wrong and impossible to see:
  /// the Server replaces an alert's poster when the clip settles, and the clip state in the URL is
  /// the only thing that makes the replacement a different URL from the one the browser has already
  /// cached. Asserting on pixels would pass either way.
  final List<String> posterRequests = [];

  AlertClipState _state = AlertClipState.pending;

  @override
  Stream<Alert> get alertUpdates => _updates.stream;

  @override
  Future<Alert> alert(String id) async => _alert(id);

  @override
  Future<AlertQueue> alerts({String? cameraId, DateTime? before}) async =>
      AlertQueue(items: [_alert('alert-1')], unread: 1);

  @override
  Future<Map<String, Uri>> alertPosterUrls(List<Alert> alerts) async {
    posterRequests.addAll([
      for (final alert in alerts) '${alert.id}@${alert.clipState.name}',
    ]);
    return const {};
  }

  /// What the Server's republish does when [AlertClipWorker] settles the clip.
  void settle(String id) {
    _state = AlertClipState.ready;
    _updates.add(_alert(id));
  }

  void close() => unawaited(_updates.close());

  Alert _alert(String id) => Alert(
    id: id,
    cameraId: 'front-door',
    cameraName: 'Front door',
    kind: AlertKind.object,
    at: DateTime(2026, 8, 12, 8, 12),
    peakAt: DateTime(2026, 8, 12, 8, 12, 4),
    label: 'person',
    title: 'Person at the front door',
    read: false,
    // Null while pending, as the Server sends it: the recording is not looked up until the clip
    // is cut, so before that there is no answer rather than a negative one.
    recorded: _state == AlertClipState.pending ? null : true,
    clipState: _state,
    clipSeconds: 20,
  );
}

/// A queue of one row whose class name is longer than a chip has room for, on a camera that was
/// not recording — so both chips are on the line at once and something has to give.
///
/// A class name is whatever the detector calls it. The sample fixtures are all one short word, so
/// they are the wrong thing to size a row against.
class _LongLabelRepository extends SampleServalRepository {
  _LongLabelRepository();

  @override
  Future<AlertQueue> alerts({String? cameraId, DateTime? before}) async =>
      AlertQueue(
        items: [
          Alert(
            id: 'alert-1',
            cameraId: 'side-path',
            cameraName: 'Side path by the garden gate',
            kind: AlertKind.object,
            at: DateTime(2026, 8, 12, 8, 12),
            peakAt: DateTime(2026, 8, 12, 8, 12, 4),
            label: 'delivery vehicle',
            title: 'Delivery vehicle on the side path',
            read: false,
            recorded: false,
            clipState: AlertClipState.ready,
            clipSeconds: 20,
          ),
        ],
        unread: 1,
      );
}

/// A repository whose unread count moves and says so — which is what the live feed does, and the
/// one thing the immutable sample repository cannot model.
class _NotifyingRepository extends SampleServalRepository {
  _NotifyingRepository();

  final _alertChanges = RepositorySlice();
  int _unread = 0;

  @override
  Listenable get alertChanges => _alertChanges;

  @override
  int get unreadAlerts => _unread;

  void setUnread(int value) {
    _unread = value;
    _alertChanges.changed();
  }
}

void main() {
  void sizeTo(WidgetTester tester, Size size) {
    final view = tester.view;
    view.devicePixelRatio = 1.0;
    view.physicalSize = size;
    addTearDown(() {
      view.resetPhysicalSize();
      view.resetDevicePixelRatio();
    });
  }

  Widget queue({
    SampleServalRepository? repository,
    void Function(String, DateTime)? onWatch,
  }) => ProviderScope(
    overrides: [
      repositoryProvider.overrideWithValue(
        repository ?? const SampleServalRepository(),
      ),
    ],
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildServalTheme(),
      home: Scaffold(body: AlertsScreen(onWatch: onWatch)),
    ),
  );

  Widget one(
    String id, {
    void Function(String, DateTime)? onWatch,
    VoidCallback? onBack,
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
      home: Scaffold(
        body: AlertScreen(alertId: id, onWatch: onWatch, onBack: onBack),
      ),
    ),
  );

  group('the queue', () {
    testWidgets('lists every camera, newest first', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(queue());
      await tester.pumpAndSettle();

      // Cross-camera by default. A queue you have to already know which camera to open is not
      // being told about anything.
      expect(find.text('Person at Front door'), findsWidgets);
      expect(find.text('Car at Driveway'), findsWidgets);
      expect(find.text('Person at Side path'), findsOneWidget);
    });

    testWidgets('says how much of it is new', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(queue());
      await tester.pumpAndSettle();

      expect(
        find.text('4 new · everything you asked to be told about'),
        findsOneWidget,
      );
    });

    testWidgets('keeps rows that have been seen', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(queue());
      await tester.pumpAndSettle();

      // Three of the seven fixtures are read, and all seven are drawn. Time never clears this
      // list — that is the difference between "seen" and "dismissed", and it is the whole reason
      // the group heading says so instead of the rows vanishing.
      expect(find.byType(AlertRow), findsNWidgets(7));
      expect(find.text('Cat at Back yard'), findsOneWidget);
    });

    testWidgets('takes a row away the moment it is dismissed', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _RecordingRepository();
      await tester.pumpWidget(queue(repository: repository));
      await tester.pumpAndSettle();

      // The row leaves on the press rather than on the response. Waiting on a round trip is what
      // turns a queue you clear while walking into one you fight.
      await tester.tap(find.byType(AlertRow).last);
      await tester.pumpAndSettle();

      final before = tester.widgetList(find.byType(AlertRow)).length;
      await tester.tap(
        find
            .descendant(
              of: find.byType(AlertRow).first,
              matching: find.byIcon(PhosphorIconsRegular.x),
            )
            .first,
      );
      await tester.pump();

      expect(tester.widgetList(find.byType(AlertRow)).length, before - 1);
      expect(repository.dismissed, hasLength(1));
    });

    testWidgets('marks an alert seen when it is opened, not before', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _RecordingRepository();
      await tester.pumpWidget(queue(repository: repository));
      await tester.pumpAndSettle();

      // Drawn is not read. A row scrolling past under your thumb is not you having read it.
      expect(repository.read, isEmpty);

      await tester.tap(find.text('Car at Driveway').first);
      await tester.pumpAndSettle();

      expect(repository.read, contains('alert-2'));
    });

    testWidgets('clears the whole queue from the header', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _RecordingRepository();
      await tester.pumpWidget(queue(repository: repository));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Dismiss all'));
      await tester.pump();

      expect(repository.dismissAllCalls, 1);
      expect(find.byType(AlertRow), findsNothing);
    });
  });

  /// The rest of this file sizes to a desktop, where the queue has 400px of text column and
  /// nothing on a row can run out of room. A phone gives it 239px, and an overflow there is
  /// invisible to a finder: the chip is still built, still found, still tapped — it is just
  /// painted past the edge of the row holding it.
  group('the queue at phone width', () {
    void expectChipsInsideTheirRows(WidgetTester tester) {
      final rows = find.byType(AlertRow);
      for (var i = 0; i < rows.evaluate().length; i++) {
        final row = rows.at(i);
        final bounds = tester.getRect(row);
        for (final kind in [
          find.byType(AlertChip),
          find.byType(NotRecordedChip),
        ]) {
          final chips = find.descendant(of: row, matching: kind);
          for (var j = 0; j < chips.evaluate().length; j++) {
            expect(
              tester.getRect(chips.at(j)).right,
              lessThanOrEqualTo(bounds.right),
              reason: 'a chip on row $i is drawn past the edge of the row',
            );
          }
        }
      }
    }

    testWidgets('draws the whole sample queue inside the screen', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 915));
      await tester.pumpWidget(queue());
      await tester.pumpAndSettle();

      expect(find.byType(AlertRow), findsNWidgets(7));
      // A RenderFlex overflow is raised during layout, so the pump above is the real assertion —
      // this is here to say which failure the test is about.
      expect(tester.takeException(), isNull);
      expectChipsInsideTheirRows(tester);
    });

    testWidgets('gives the class name up rather than the row', (tester) async {
      sizeTo(tester, const Size(412, 915));
      await tester.pumpWidget(queue(repository: _LongLabelRepository()));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      // Both chips are on the line and the narrow one still fits: *Not recorded* is the chip that
      // changes what the row can do, so the class is the one that ellipsizes.
      expect(find.byType(NotRecordedChip), findsOneWidget);
      expectChipsInsideTheirRows(tester);
    });
  });

  group('a camera that was not recording', () {
    testWidgets('still gets a row, marked as such', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(queue());
      await tester.pumpAndSettle();

      // Detections fire whether or not a camera is set to record, so this row has to exist. The
      // chip is the only thing that says the difference.
      expect(find.text('Person at Side path'), findsOneWidget);
      expect(find.byType(NotRecordedChip), findsOneWidget);
    });

    testWidgets('offers no way to open a recording that is not there', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      final watched = <String>[];
      await tester.pumpWidget(
        one('alert-4', onWatch: (id, at) => watched.add(id)),
      );
      await tester.pumpAndSettle();

      // Absent rather than disabled, and with no explanation card either: the preview plays like
      // any other alert, so there is nothing to apologise for — only a button with nowhere to go.
      expect(find.textContaining('Watch it on'), findsNothing);
      expect(find.text('Dismiss'), findsOneWidget);
      expect(find.byType(NotRecordedChip), findsOneWidget);
    });

    testWidgets('a recorded alert does offer it, naming the camera', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      final watched = <String>[];
      await tester.pumpWidget(
        one('alert-1', onWatch: (id, at) => watched.add(id)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Watch it on Front door'), findsOneWidget);

      await tester.tap(find.text('Watch it on Front door'));
      await tester.pump();

      expect(watched, ['front-door']);
    });
  });

  group('one alert on a phone', () {
    testWidgets('is marked seen by arriving, however you arrived', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      final repository = _RecordingRepository();
      await tester.pumpWidget(one('alert-2', repository: repository));
      await tester.pumpAndSettle();

      // This is also where a tapped notification lands, and a notification that had to be tapped
      // twice to count as read would be a worse notification.
      expect(repository.read, contains('alert-2'));
    });

    testWidgets('really dismisses, rather than only going back', (
      tester,
    ) async {
      sizeTo(tester, const Size(412, 892));
      final repository = _RecordingRepository();
      var backs = 0;
      await tester.pumpWidget(
        one('alert-2', repository: repository, onBack: () => backs++),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Dismiss'));
      await tester.pumpAndSettle();

      expect(repository.dismissed, ['alert-2']);
      expect(backs, 1);
    });
  });

  group('an alert opened before its clip exists', () {
    testWidgets('fills in when the Server says the clip has settled', (
      tester,
    ) async {
      sizeTo(tester, const Size(420, 900));
      final repository = _SettlingRepository();
      addTearDown(repository.close);

      await tester.pumpWidget(one('alert-1', repository: repository));
      await tester.pumpAndSettle();

      // Where every notification tap lands. The clip is not cut, so the pill says so — but the
      // poster is asked for anyway, because the Server writes one from the camera's live snapshot
      // at the moment of the raise.
      expect(find.text('Clip in a moment'), findsOneWidget);
      expect(repository.posterRequests, ['alert-1@pending']);

      repository.settle('alert-1');
      await tester.pumpAndSettle();

      // Two things at once: the screen noticed the settle at all — without the subscription it
      // holds the answer above for as long as it stays open — and it asked again at the new state,
      // which is what makes the exact frame a different URL from the stand-in it replaces.
      expect(find.text('Clip in a moment'), findsNothing);
      expect(repository.posterRequests, ['alert-1@pending', 'alert-1@ready']);
    });

    testWidgets('re-reads the queue when a listed row settles', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _SettlingRepository();
      addTearDown(repository.close);

      await tester.pumpWidget(queue(repository: repository));
      await tester.pumpAndSettle();

      // Every listed row, pending ones included — they have a poster too.
      expect(find.text('Clip in a moment'), findsOneWidget);
      expect(repository.posterRequests, ['alert-1@pending']);

      repository.settle('alert-1');
      await tester.pumpAndSettle();

      expect(find.text('Clip in a moment'), findsNothing);
      expect(repository.posterRequests, ['alert-1@pending', 'alert-1@ready']);
    });

    testWidgets('says nothing about the recording until it knows', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _SettlingRepository();
      addTearDown(repository.close);

      await tester.pumpWidget(queue(repository: repository));
      await tester.pumpAndSettle();

      // The sixteen seconds between the raise and the cut are the whole window this is about. A
      // chip here would mark the newest alert on a recording camera as not recorded, and then
      // silently take it back — which is what the queue used to do to every alert it drew.
      expect(find.byType(NotRecordedChip), findsNothing);

      repository.settle('alert-1');
      await tester.pumpAndSettle();

      expect(find.byType(NotRecordedChip), findsNothing);
    });
  });

  group('the rail badge', () {
    testWidgets('lights when the count moves, not only when the shell is built', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _NotifyingRepository();

      final router = GoRouter(
        initialLocation: '/wall',
        routes: [
          ShellRoute(
            builder: (context, state, child) => ServalShell(child: child),
            routes: [
              GoRoute(
                path: '/wall',
                builder: (context, state) => const SizedBox.shrink(),
              ),
            ],
          ),
        ],
      );
      addTearDown(router.dispose);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [repositoryProvider.overrideWithValue(repository)],
          child: MaterialApp.router(
            debugShowCheckedModeBanner: false,
            theme: buildServalTheme(),
            routerConfig: router,
          ),
        ),
      );
      await tester.pumpAndSettle();

      RailItem bell() => tester
          .widget<IconRail>(find.byType(IconRail))
          .items
          .firstWhere((i) => i.tooltip == 'Alerts');

      expect(bell().badge, isFalse);

      // What the live feed does when an alert arrives. `ref.watch` alone cannot see this — the
      // repository's identity never changes — so a shell that only read the count would stay dark
      // until something else happened to rebuild it.
      repository.setUnread(3);
      await tester.pumpAndSettle();

      expect(bell().badge, isTrue);
    });
  });

  group('a tapped notification', () {
    /// The real router, because the whole question here is which screen `/alerts/:id` resolves to
    /// at a given width — a harness that pumps one screen has already answered it.
    Future<void> pumpAt(
      WidgetTester tester,
      String location, {
      SampleServalRepository? repository,
    }) async {
      final router = buildServalRouter(auth: null);
      addTearDown(router.dispose);
      router.go(location);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            repositoryProvider.overrideWithValue(
              repository ?? const SampleServalRepository(),
            ),
            authProvider.overrideWithValue(null),
          ],
          child: MaterialApp.router(
            debugShowCheckedModeBanner: false,
            theme: buildServalTheme(),
            routerConfig: router,
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('opens the phone screen on a phone', (tester) async {
      sizeTo(tester, const Size(412, 892));
      await pumpAt(tester, '/alerts/alert-2');

      expect(find.byType(AlertScreen), findsOneWidget);
    });

    testWidgets('opens the queue with that alert beside it, wide', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      await pumpAt(tester, '/alerts/alert-2');

      // Not [AlertScreen]: that screen is drawn for 412px, and stretched across a desktop its
      // 232px stage becomes a slot eight times wider than it is tall — which nothing here answers
      // by cropping, so it draws a stamp of picture marooned in ground. Wide, an alert is read in
      // the column beside the queue — the same place clicking its row puts it.
      expect(find.byType(AlertScreen), findsNothing);
      expect(find.byType(AlertsScreen), findsOneWidget);

      final pane = tester.widget<AlertDetailPane>(find.byType(AlertDetailPane));
      expect(pane.alert.id, 'alert-2');
    });

    testWidgets('counts as read wide, the same as on the phone', (
      tester,
    ) async {
      sizeTo(tester, const Size(1440, 900));
      final repository = _RecordingRepository();
      await pumpAt(tester, '/alerts/alert-2', repository: repository);

      expect(repository.read, contains('alert-2'));
    });

    testWidgets('says so when the alert it names is gone', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await pumpAt(tester, '/alerts/no-such-alert');

      // The alternative is opening the newest one instead, which answers a question nobody asked
      // while the address bar still names the one it is not showing.
      expect(find.byType(AlertDetailPane), findsNothing);
      expect(find.textContaining('no longer here'), findsOneWidget);
    });
  });

  group('a sound alert', () {
    testWidgets('is a row like any other, without a box', (tester) async {
      sizeTo(tester, const Size(1440, 900));
      await tester.pumpWidget(queue());
      await tester.pumpAndSettle();

      // Sounds share the queue because "glass at the back door" is a thing you want to look at,
      // not only listen to — and they get a video clip for the same reason. What they do not have
      // is a place in the frame.
      expect(find.text('Glass heard at Back yard'), findsOneWidget);

      final sound = tester
          .widgetList<AlertRow>(find.byType(AlertRow))
          .firstWhere((row) => row.alert.kind == AlertKind.sound);

      expect(sound.alert.box, isNull);
    });
  });
}
