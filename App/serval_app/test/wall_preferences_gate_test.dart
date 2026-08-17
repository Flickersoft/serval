// What the wall offers while this account's stored arrangement has not been read.
//
// The tiles on screen then are a default pack standing in for an arrangement nobody has seen, and
// it is indistinguishable from the default belonging to somebody who never arranged one. Rearranging
// writes on every accepted move rather than from a save button, so leaving the mode open means one
// dragged tile stores the stand-in over the real thing.
//
// The mode is therefore closed rather than the write being dropped underneath it: a drag that
// appears to work and is discarded, or one that snaps back mid-gesture, are both worse than a
// control that is plainly not available yet. `preferences_barrier_test.dart` covers the repository
// refusing the write regardless, which is what makes this a second line rather than the only one.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/main.dart';

/// The sample content, with its preferences reported as not yet read.
class _Unread extends SampleServalRepository {
  @override
  bool get preferencesKnown => false;
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

  // The control is a glyph, so its tooltip is the only text it has.
  final rearrangeButton = find.byTooltip('Rearrange the wall');
  final waitingButton = find.byTooltip('Waiting for your saved arrangement');

  testWidgets('an unread arrangement closes the rearrange mode', (
    tester,
  ) async {
    await tester.pumpWidget(ServalApp(repository: _Unread()));
    await tester.pumpAndSettle();

    expect(rearrangeButton, findsNothing);
    expect(waitingButton, findsOneWidget);

    // Said in the header's subtitle, which holds its height whatever it reads. A strip above the
    // grid would jog the whole wall down on the failed read and back up on the retry.
    expect(
      find.textContaining('Still loading your saved arrangement'),
      findsOneWidget,
    );
  });

  testWidgets('pressing it does not open the mode', (tester) async {
    await tester.pumpWidget(ServalApp(repository: _Unread()));
    await tester.pumpAndSettle();

    await tester.tap(waitingButton);
    await tester.pumpAndSettle();

    // 'Done rearranging' is the mode's own label, so its absence is the mode staying shut.
    expect(find.byTooltip('Done rearranging'), findsNothing);
    expect(waitingButton, findsOneWidget);
  });

  testWidgets('a read arrangement leaves the control alone', (tester) async {
    // The other half: this must not be a wall that is permanently read-only.
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    expect(rearrangeButton, findsOneWidget);
    expect(waitingButton, findsNothing);
    expect(
      find.textContaining('Still loading your saved arrangement'),
      findsNothing,
    );
  });
}
