import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/widgets/nocturne_field.dart';
import 'package:serval_app/widgets/timeline_scrubber.dart';

void main() {
  // The design is drawn at 1440x900, and the rail, activity column and detail
  // panel hold fixed widths at that size. The default 800x600 test surface is
  // narrower than the design ever intends to be.
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

  testWidgets('the wall shows every camera and the activity column', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());

    for (final name in [
      'Driveway',
      'Front door',
      'Back yard',
      'Kitchen',
      'Garage',
    ]) {
      expect(
        find.text(name),
        findsWidgets,
        reason: '$name should be on the wall',
      );
    }

    // The offline camera shows its state instead of an image.
    expect(find.text('Side path is offline'), findsOneWidget);

    expect(find.text("What's happening"), findsOneWidget);
    expect(
      find.text('A courier is at the door holding a small parcel.'),
      findsOneWidget,
    );
  });

  testWidgets('a row opens the camera it came from', (tester) async {
    await tester.pumpWidget(const ServalApp());

    // The wall offers no way to talk back — clicking the row routes you into
    // the single-camera view to speak.
    expect(find.text('Hold to talk'), findsNothing);

    await tester.tap(find.text('Glass heard').first);
    await tester.pumpAndSettle();

    expect(find.text('All cameras'), findsOneWidget);
    expect(find.text("Someone's here"), findsOneWidget);
    expect(find.text('Hold to talk'), findsOneWidget);

    // The same feed follows you in, scoped to the camera you landed on: the alert's own row is
    // here, and so is the rest of what this camera has heard.
    expect(find.text("What's happening"), findsOneWidget);
    expect(find.text('Glass heard'), findsOneWidget);
    expect(
      find.text('A courier is at the door holding a small parcel.'),
      findsOneWidget,
    );

    // But not under its name — that question was answered by opening it. The top bar still says
    // Front door; no feed row does.
    expect(
      find.text('Front door'),
      findsOneWidget,
      reason: 'the top bar, and nothing in the feed',
    );
    expect(
      find.text('The door closed and nothing has moved since.'),
      findsNothing,
      reason: "the garage's row stays on the wall",
    );
  });

  testWidgets('scrubbing against the sample repository builds no player', (
    tester,
  ) async {
    // The guard that keeps `flutter test` off libmpv. `dart.library.io` is true under the VM, so
    // the media_kit backend *is* compiled into this binary; what stops it being constructed is
    // `SampleServalRepository.vodUrlFor` returning null, the same job `canStreamLive` does for
    // the WebRTC plugin. If that ever changed, every run of this suite would need libmpv on the
    // machine — so a tap that stays on the live placeholder is worth asserting.
    await tester.pumpWidget(const ServalApp());

    await tester.tap(find.text('Glass heard').first);
    await tester.pumpAndSettle();

    final track = tester.getRect(find.byType(TimelineScrubber));
    await tester.tapAt(Offset(track.center.dx, track.bottom - 20));
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
    expect(find.text('Replaying'), findsNothing);
    expect(find.text('Live'), findsOneWidget);
  });

  testWidgets('the single camera returns to the wall', (tester) async {
    await tester.pumpWidget(const ServalApp());

    await tester.tap(find.text('Glass heard').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('All cameras'));
    await tester.pumpAndSettle();

    expect(find.text("What's happening"), findsOneWidget);
  });

  // The single-camera gear has to land on *this* camera's record, not on the registry's first —
  // which is what the rail's own gear does, and is the failure this would otherwise silently
  // regress to.
  testWidgets('the gear opens the registry on the camera you were watching', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());

    // The row is on Front door, which is not first in the registry — Driveway is.
    await tester.tap(find.text('Glass heard').first);
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(PhosphorIconsRegular.gearSix));
    await tester.pumpAndSettle();

    expect(find.text('Streams'), findsOneWidget, reason: 'the settings screen');
    expect(
      find.widgetWithText(NocturneField, 'Front door'),
      findsOneWidget,
      reason: "the form should hold Front door's record, not Driveway's",
    );
  });

  testWidgets('the rail opens settings on Server status', (tester) async {
    await tester.pumpWidget(const ServalApp());

    // From the wall the gear is a request for settings in general, not for one camera's record.
    // It lands on the page that changes nothing and answers "is this machine alright" — and
    // carries no camera in with it.
    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();

    expect(find.text('The volume'), findsOneWidget);
    expect(
      find.widgetWithText(NocturneField, 'Driveway'),
      findsNothing,
      reason: 'no camera record is open',
    );
  });

  testWidgets('the registry, once asked for, opens on its first camera', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());

    await tester.tap(find.byTooltip('Settings'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cameras'));
    await tester.pumpAndSettle();

    expect(
      find.widgetWithText(NocturneField, 'Driveway'),
      findsOneWidget,
      reason: 'the first camera in the registry',
    );
  });
}
