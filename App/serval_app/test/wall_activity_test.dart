// That the wall's column shows telemetry the moment it lands.
//
// The column reads the pool during `build`, so something has to rebuild it when a document arrives.
// That used to be the wall's own subscription to the whole repository, which meant an utterance —
// or a detection heartbeat, twice a second per camera — relaid out every tile to add one row. It is
// now `activityChanges`, watched by a `Consumer` around the column itself.
//
// So this pins the half that has to keep working: the row still appears, without the reader having
// to leave the wall. `activity_slice_rebuild_test` pins the other half, that the tiles are left
// alone while it happens.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/data/serval_repository.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/models/activity.dart';
import 'package:serval_app/models/timeline.dart';

/// The sample content plus a feed that can be pushed to, the way `LiveServalRepository` writes one
/// document and fires the slice.
class _PushableActivity extends SampleServalRepository {
  final _activity = RepositorySlice();
  final _pushed = <ActivityItem>[];

  @override
  Listenable get activityChanges => _activity;

  @override
  List<ActivityItem> activityFor({
    String? cameraId,
    DateTime? asOf,
    TimelineRange? range,
    bool includeAllDetections = false,
  }) => [..._pushed, ...super.activityFor(cameraId: cameraId)];

  /// Newest first, as the live feed orders it.
  void hear(ActivityItem item) {
    _pushed.insert(0, item);
    _activity.changed();
  }
}

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

  testWidgets('an utterance arriving shows up without leaving the wall', (
    tester,
  ) async {
    final repository = _PushableActivity();

    await tester.pumpWidget(ServalApp(repository: repository));
    await tester.pumpAndSettle();

    const said = 'Parcel for you, leaving it by the door.';
    expect(find.textContaining(said), findsNothing);

    repository.hear(
      ActivityItem(
        id: 'utterance-1',
        kind: TelemetryKind.utterance,
        cameraId: 'front-door',
        cameraName: 'Front door',
        at: DateTime.now(),
        timeLabel: 'now',
        text: '“$said”',
        icon: ActivityIcon.person,
        isSpeech: true,
        isRecent: true,
      ),
    );

    // No navigation: the notification alone has to put it on screen.
    await tester.pumpAndSettle();

    expect(find.textContaining(said), findsOneWidget);
  });
}
