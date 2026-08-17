// The activity feed's filter, and the per-camera scoping the single-camera panel reads. Both are
// predicates over one in-memory list, so they are testable without a Server — which is the point,
// since the wrong predicate here silently hides events rather than failing.
//
// A value rather than a segmented choice: a search box, four kinds, a camera set, a label set and
// an alert flag, any of which can be on at once. The exclusions still hold — speech excludes
// sounds, an alerting detection is not a sound — but as facets that combine.
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/widgets/activity_column.dart';
import 'package:serval_app/widgets/activity_filter_panel.dart';
import 'package:serval_app/widgets/activity_panel.dart';
import 'package:serval_app/widgets/nocturne_button.dart';

void main() {
  final repository = SampleServalRepository();

  List<ActivityItem> pool({String? cameraId}) =>
      repository.activityFor(cameraId: cameraId);

  List<String> ids(ActivityFilter filter, {String? cameraId}) => [
    for (final item in filter.apply(pool(cameraId: cameraId))) item.id,
  ];

  group('kinds', () {
    test('speech excludes sounds, including the alerting one', () {
      const filter = ActivityFilter(kinds: {ActivityKind.speech});
      final speech = filter.apply(pool());

      expect(speech, isNotEmpty);
      expect(
        speech.every((i) => i.kind != TelemetryKind.sound),
        isTrue,
        reason: 'a barking dog is not speech, however loud',
      );
      expect(ids(filter), ['a2', 'a2b', 'a3']);
    });

    test('sounds are the sound rows, alerting or not', () {
      // 'a1b' is the alerting glass; 'a4b' is an ordinary vehicle horn. Both are sounds, and the
      // facet is the only thing that reaches them without reaching everything.
      expect(ids(const ActivityFilter(kinds: {ActivityKind.sounds})), [
        'a1b',
        'a4b',
      ]);
    });

    test('objects seen are the detections', () {
      expect(ids(const ActivityFilter(kinds: {ActivityKind.objects})), ['a1c']);
    });

    // The whole reason the segmented control had to go: it could hold one choice, so Speech and
    // Sounds were rivals because of the control rather than because of the data.
    test('speech and sounds are a union, not a choice', () {
      expect(
        ids(
          const ActivityFilter(
            kinds: {ActivityKind.speech, ActivityKind.sounds},
          ),
        ),
        ['a1b', 'a2', 'a2b', 'a3', 'a4b'],
      );
    });

    test('the four kinds partition the feed', () {
      final counted = {
        for (final kind in ActivityKind.values)
          ...ids(ActivityFilter(kinds: {kind})),
      };

      expect(
        counted,
        pool().map((i) => i.id).toSet(),
        reason: 'every row belongs to exactly one kind, and none to none',
      );
    });

    test('an empty facet set means everything, not nothing', () {
      expect(ids(ActivityFilter.none), hasLength(pool().length));
      expect(
        ids(const ActivityFilter(kinds: {})),
        hasLength(pool().length),
        reason: 'ticking no box is not a request for a blank column',
      );
    });
  });

  group('alerts', () {
    test('a detection alert is reachable from alerts but not from sounds', () {
      expect(ids(const ActivityFilter(alertsOnly: true)), contains('a1c'));
      expect(
        ids(const ActivityFilter(kinds: {ActivityKind.sounds})),
        isNot(contains('a1c')),
      );
    });

    test('alerts span every source of severity, not just sounds', () {
      // Not a strict subset of sounds: a detection of a class the operator listed carries severity
      // too, and an alert filter hiding those would hide the ones most worth seeing.
      final sounds = ids(
        const ActivityFilter(kinds: {ActivityKind.sounds}),
      ).toSet();
      final alerts = ids(const ActivityFilter(alertsOnly: true)).toSet();

      expect(alerts, isNotEmpty);
      expect(
        alerts.difference(sounds),
        isNotEmpty,
        reason: 'a detection alert is an alert and is not a sound',
      );
      expect(
        sounds.difference(alerts),
        isNotEmpty,
        reason: 'otherwise the two would say the same thing',
      );
    });

    // A flag across kinds rather than a fifth kind, which is the reason it is a toggle above the
    // list in the panel and not a checkbox in it.
    test('the alert flag narrows a kind rather than replacing it', () {
      expect(
        ids(
          const ActivityFilter(kinds: {ActivityKind.sounds}, alertsOnly: true),
        ),
        ['a1b'],
        reason: 'the ordinary horn is a sound and is not an alert',
      );
    });
  });

  group('cameras and labels', () {
    test('the camera facet is a union across the chosen ones', () {
      expect(ids(const ActivityFilter(cameraIds: {'garage'})), ['a7']);
      expect(ids(const ActivityFilter(cameraIds: {'garage', 'back-yard'})), [
        'a5',
        'a7',
      ]);
    });

    test('a label is the class the model named, comma and all', () {
      expect(
        ids(const ActivityFilter(labels: {'Vehicle horn, car horn, honking'})),
        ['a4b'],
        reason: 'an AudioSet phrase is one filter, not three',
      );
    });

    test('facets intersect rather than accumulate', () {
      expect(
        ids(
          const ActivityFilter(
            kinds: {ActivityKind.sounds},
            cameraIds: {'driveway'},
          ),
        ),
        ['a4b'],
      );
      expect(
        ids(
          const ActivityFilter(
            kinds: {ActivityKind.speech},
            cameraIds: {'driveway'},
          ),
        ),
        isEmpty,
        reason: 'nothing was said on the driveway',
      );
    });
  });

  group('search', () {
    test('matches what was said or seen, and who said it', () {
      // Across kinds, and across cameras: 'a1' is a scene on the front door, 'a7' a scene on the
      // garage. Search is the one filter that does not care which drawer a row is in.
      expect(ids(const ActivityFilter(query: 'door')), ['a1', 'a7']);
      expect(ids(const ActivityFilter(query: '2 speakers')), ['a2b', 'a3']);
    });

    test('is case- and space-insensitive', () {
      expect(ids(const ActivityFilter(query: '  PARCEL ')), ['a1']);
    });

    test('matches a word only a turn carries', () {
      // A settled conversation keeps `text` as its flowing fallback *and* draws
      // its turns, and today the Server builds the former by joining the
      // latter. This searches a word present only in a turn, so it fails if
      // `matches` reads `text` alone — because the row shows words that field
      // only promises to contain, and this side cannot check that promise.
      final item = ActivityItem(
        id: 'turns-only',
        kind: TelemetryKind.conversationTranscript,
        cameraId: 'front-door',
        cameraName: 'Front door',
        at: DateTime(2026, 8, 5, 18),
        timeLabel: 'now',
        text: '“Hello?”',
        icon: ActivityIcon.speech,
        isSpeech: true,
        turns: const [
          ActivityTurn(text: '“Hello?”', speakerNumber: 1),
          ActivityTurn(text: '“Behind the planter.”', speakerNumber: 2),
        ],
      );

      expect(item.matches('planter'), isTrue);
    });

    // The camera is the facet's job. A search for a room name that returned every row from that
    // room would bury the one where somebody actually said the word.
    test('does not match the camera name', () {
      expect(ids(const ActivityFilter(query: 'garage')), isEmpty);
    });

    test('composes with the facets', () {
      expect(ids(const ActivityFilter(query: 'door', cameraIds: {'garage'})), [
        'a7',
      ]);
      expect(
        ids(const ActivityFilter(query: 'door', kinds: {ActivityKind.speech})),
        isEmpty,
        reason: 'nobody said the word, the scenes only described it',
      );
    });
  });

  group('what the header says it is doing', () {
    test('an empty filter is empty and claims nothing', () {
      expect(ActivityFilter.none.isEmpty, isTrue);
      expect(ActivityFilter.none.facetCount, 0);
      expect(ActivityFilter.none.chips(const {}), isEmpty);
    });

    // The badge counts what the panel holds. The query is on screen in its own field, and a badge
    // that included it would be counting something the reader can already see.
    test('the query is not a facet', () {
      const filter = ActivityFilter(query: 'postman');

      expect(filter.isEmpty, isFalse);
      expect(filter.facetCount, 0);
    });

    test('there is one chip per facet value, each removing only itself', () {
      const filter = ActivityFilter(
        kinds: {ActivityKind.speech},
        cameraIds: {'front-door'},
        alertsOnly: true,
      );
      final chips = filter.chips(const {'front-door': 'Front door'});

      expect(filter.facetCount, 3);
      expect(chips.map((c) => c.label), [
        'Alerts only',
        'Speech',
        'Front door',
      ]);

      final withoutCamera = chips[2].without;
      expect(withoutCamera.cameraIds, isEmpty);
      expect(
        withoutCamera.kinds,
        {ActivityKind.speech},
        reason: 'dropping one filter must not drop the others',
      );
      expect(withoutCamera.alertsOnly, isTrue);
    });

    test('Reset all leaves the search field alone', () {
      const filter = ActivityFilter(
        query: 'postman',
        kinds: {ActivityKind.speech},
        alertsOnly: true,
      );

      expect(filter.withoutFacets.query, 'postman');
      expect(filter.withoutFacets.facetCount, 0);
    });

    // The memo in the live repository keys on a record holding one of these.
    test('two filters built by different taps are equal', () {
      final a = ActivityFilter.none
          .toggleKind(ActivityKind.speech)
          .toggleKind(ActivityKind.sounds);
      final b = ActivityFilter.none
          .toggleKind(ActivityKind.sounds)
          .toggleKind(ActivityKind.speech);

      expect(a, b);
      expect(a.hashCode, b.hashCode);
    });

    test('toggling twice is the same as not toggling', () {
      expect(
        ActivityFilter.none.toggleCamera('garage').toggleCamera('garage'),
        ActivityFilter.none,
      );
    });
  });

  group('facet counts', () {
    test('are the whole pool, and do not move as boxes are ticked', () {
      final facets = ActivityFacets.of(pool());

      expect(facets.total, pool().length);
      expect(
        facets.kinds.values.fold(0, (a, b) => a + b),
        facets.total,
        reason: 'the kinds partition the column, so their counts sum to it',
      );
      expect(
        facets.cameras.values.fold(0, (a, b) => a + b),
        facets.total,
        reason: 'every row is on exactly one camera',
      );
      expect(facets.alerts, ids(const ActivityFilter(alertsOnly: true)).length);
    });

    // The one exception, and the reason for it: with the counts unscoped, *Speech* read 3 under
    // *Alerts only* and then emptied the column, because none of those three was an alert. The
    // panel could see that coming and did not say so.
    test('the alert toggle rescopes everything below it', () {
      final scoped = ActivityFacets.of(pool(), alertsOnly: true);

      expect(scoped.kinds[ActivityKind.speech] ?? 0, 0);
      expect(scoped.kinds[ActivityKind.scenes] ?? 0, 0);
      expect(scoped.kinds[ActivityKind.objects], 1);
      expect(scoped.kinds[ActivityKind.sounds], 1);
      expect(scoped.cameras.keys, [
        'front-door',
      ], reason: 'both alerts are on the one camera');
    });

    test('the toggle leaves the total and the alert count whole', () {
      final scoped = ActivityFacets.of(pool(), alertsOnly: true);
      final whole = ActivityFacets.of(pool());

      expect(
        scoped.total,
        whole.total,
        reason: 'the total is the M in "N of the M events in view"',
      );
      expect(
        scoped.alerts,
        whole.alerts,
        reason: '"2 of them, across everything below" is not "2 of 2"',
      );
    });

    test('a label the toggle empties stays in the list at zero', () {
      final scoped = ActivityFacets.of(pool(), alertsOnly: true);
      final horn = scoped.labels.firstWhere(
        (f) => f.label.startsWith('Vehicle horn'),
      );

      expect(horn.count, 0, reason: 'the ordinary horn is not an alert');
      expect(
        horn.icon,
        ActivityIcon.car,
        reason: 'a chip dimmed to zero still wears its glyph',
      );
    });

    test('label counts cover only the rows that carry a class', () {
      final facets = ActivityFacets.of(pool());
      final labelled = pool().where((i) => i.label != null).length;

      expect(
        facets.labels.fold(0, (a, b) => a + b.count),
        labelled,
        reason: 'speech and scenes name no class, so they count towards none',
      );
    });

    test('labels are most frequent first, then alphabetical', () {
      final counts = [
        for (final f in ActivityFacets.of(pool()).labels) f.count,
      ];

      expect(counts, isNotEmpty);
      expect(
        counts,
        orderedEquals(List.of(counts)..sort((a, b) => b.compareTo(a))),
      );
    });

    test('a chip wears the glyph its rows wear', () {
      final person = ActivityFacets.of(
        pool(),
      ).labels.firstWhere((f) => f.label == 'person');

      expect(person.icon, ActivityIcon.person);
    });
  });

  group('per camera', () {
    test('scopes to one camera and keeps the merged order', () {
      expect(ids(ActivityFilter.none, cameraId: 'front-door'), [
        'a1',
        'a1b',
        'a1c',
        'a2',
        'a2b',
      ]);
      expect(ids(ActivityFilter.none, cameraId: 'garage'), ['a7']);
    });

    test('composes with a filter', () {
      expect(
        ids(
          const ActivityFilter(kinds: {ActivityKind.sounds}),
          cameraId: 'front-door',
        ),
        ['a1b'],
      );
      expect(
        ids(
          const ActivityFilter(kinds: {ActivityKind.speech}),
          cameraId: 'driveway',
        ),
        isEmpty,
      );
    });

    test('an unknown camera is empty rather than everything', () {
      expect(ids(ActivityFilter.none, cameraId: 'no-such-camera'), isEmpty);
    });
  });

  group('the single-camera panel', () {
    Widget app(String cameraId, {ActivityFilter filter = ActivityFilter.none}) {
      final items = repository.activityFor(cameraId: cameraId);

      return Directionality(
        textDirection: TextDirection.ltr,
        child: MediaQuery(
          data: const MediaQueryData(),
          child: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              height: 700,
              child: ActivityPanel(
                filter: filter,
                facets: ActivityFacets.of(items),
                activity: filter.apply(items),
              ),
            ),
          ),
        ),
      );
    }

    testWidgets('shows this camera and not the others', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(app('front-door'));
      await tester.pumpAndSettle();

      expect(
        find.text('A courier is at the door holding a small parcel.'),
        findsOneWidget,
      );
      expect(find.text('Glass heard'), findsOneWidget);
      expect(
        find.text('The door closed and nothing has moved since.'),
        findsNothing,
        reason: "the garage's row belongs to the garage",
      );
    });

    // The whole reason the name comes off: on this screen it is the same six words above every
    // row, answering a question the screen has already answered.
    testWidgets('names no camera, and names the speaker instead', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(app('front-door'));
      await tester.pumpAndSettle();

      expect(
        find.text('Front door'),
        findsNothing,
        reason: 'the panel carries no camera name of its own',
      );
      expect(
        find.text('At the camera'),
        findsOneWidget,
        reason: "the utterance's speaker takes the slot the name left",
      );
      expect(find.text('2 speakers'), findsOneWidget);
    });

    testWidgets('a scene keeps its time and gains no speaker', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      // The garage row is a scene: nobody to name, so the heading is the time alone.
      await tester.pumpWidget(app('garage'));
      await tester.pumpAndSettle();

      expect(find.text('6:40 pm yesterday'), findsOneWidget);
      expect(find.text('Garage'), findsNothing);
    });

    testWidgets('the filter narrows this camera', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        app(
          'front-door',
          filter: const ActivityFilter(kinds: {ActivityKind.sounds}),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Glass heard'), findsOneWidget);
      expect(
        find.text('A courier is at the door holding a small parcel.'),
        findsNothing,
        reason: 'a scene is not a sound',
      );
    });

    testWidgets('says what is filtered, and how much of the column is left', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        app(
          'front-door',
          filter: const ActivityFilter(kinds: {ActivityKind.sounds}),
        ),
      );
      await tester.pumpAndSettle();

      // The chip and the readout together are what stop a narrowed column reading as a quiet
      // house — neither is decoration for the other.
      expect(find.text('Sounds'), findsOneWidget);
      expect(find.text('1 of the 5 events in view'), findsOneWidget);
      expect(find.text('Clear'), findsOneWidget);
    });

    testWidgets('at rest it counts nothing and shows no chips', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(app('front-door'));
      await tester.pumpAndSettle();

      expect(
        find.textContaining('events in view'),
        findsNothing,
        reason: '"5 of the 5" is arithmetic in place of a fact',
      );
      expect(find.text('Clear'), findsNothing);
    });

    // An empty column with nothing filtered means nothing happened; an empty one with a filter on
    // means the filter is hiding it, and saying the first when the second is true sends you
    // looking for a fault that is not there.
    testWidgets('says which kind of empty it is', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        app(
          'driveway',
          filter: const ActivityFilter(kinds: {ActivityKind.speech}),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Nothing in view matches this filter.'), findsOneWidget);
    });

    testWidgets('an empty search says so, and says why', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        app('front-door', filter: const ActivityFilter(query: 'postman')),
      );
      await tester.pumpAndSettle();

      expect(find.text('Nothing matched “postman”.'), findsOneWidget);
      expect(
        find.textContaining('Serval only keeps words it heard'),
        findsOneWidget,
      );
    });

    // The bug this scoping exists for: the panel let you tick Speech under Alerts only, showed a
    // count of 3 while doing it, and handed back a blank column.
    testWidgets('a facet the alert toggle empties cannot be ticked', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      var filter = const ActivityFilter(alertsOnly: true);
      final items = repository.activityFor(cameraId: 'front-door');

      await tester.pumpWidget(
        Directionality(
          textDirection: TextDirection.ltr,
          child: MediaQuery(
            data: const MediaQueryData(),
            child: Align(
              alignment: Alignment.topLeft,
              child: SizedBox(
                height: 700,
                child: ActivityPanel(
                  filter: filter,
                  facets: ActivityFacets.of(items, alertsOnly: true),
                  activity: filter.apply(items),
                  onFilterChanged: (f) => filter = f,
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Filter'));
      await tester.pumpAndSettle();

      await tester.tap(
        find.descendant(
          of: find.byType(ActivityFilterPanel),
          matching: find.text('Speech'),
        ),
      );
      await tester.pumpAndSettle();

      expect(
        filter.kinds,
        isEmpty,
        reason: 'no alert on this camera is speech, so the row is inert',
      );

      // And the one that is reachable still is, so this is not a panel that has gone dead.
      await tester.tap(
        find.descendant(
          of: find.byType(ActivityFilterPanel),
          matching: find.text('Sounds'),
        ),
      );
      await tester.pumpAndSettle();

      expect(filter.kinds, {ActivityKind.sounds});
    });

    testWidgets('an alert here offers no Dismiss — that is the wall’s book', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(app('front-door'));
      await tester.pumpAndSettle();

      // The alert row itself is present, tint and all — only its action is gone. Clicking it still
      // does something here; it seeks this camera rather than routing to it.
      expect(find.text('Glass heard'), findsOneWidget);
      expect(find.byType(NocturneButton), findsNothing);
    });

    testWidgets('a camera with nothing on it says so', (tester) async {
      await tester.binding.setSurfaceSize(const Size(500, 700));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(app('no-such-camera'));
      await tester.pumpAndSettle();

      expect(
        find.text('Nothing heard or seen on this camera yet.'),
        findsOneWidget,
      );
    });
  });

  testWidgets('the wall names every alert row\'s camera', (tester) async {
    await tester.binding.setSurfaceSize(const Size(500, 700));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    const alertsOnly = ActivityFilter(alertsOnly: true);

    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: MediaQuery(
          data: const MediaQueryData(),
          child: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              height: 700,
              child: ActivityFeed(
                items: alertsOnly.apply(pool()),
                onOpen: (_, _) {},
              ),
            ),
          ),
        ),
      ),
    );

    // Counted against the rows rather than hard-coded, so demo data gaining an
    // alert is not a test failure.
    final alerts = alertsOnly.apply(pool()).length;

    expect(
      find.text('Front door'),
      findsNWidgets(alerts),
      reason: 'across six cameras each heading still has to say which one',
    );
  });
}
