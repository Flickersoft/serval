import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phosphor_icons/phosphor_icons.dart';
import 'package:serval_app/data/sample_repository.dart';
import 'package:serval_app/main.dart';
import 'package:serval_app/theme/serval_tokens.dart';
import 'package:serval_app/widgets/activity_column.dart';
import 'package:serval_app/widgets/activity_panel.dart';
import 'package:serval_app/widgets/nocturne_button.dart';

/// The chevron inside [panel], by the direction it points.
///
/// Scoped rather than global because the same two carets are the PTZ pad's pan
/// arrows, and on the single-camera screen both are on stage at once.
Finder _chevron(PhosphorIconData icon, Finder panel) => find.descendant(
  of: panel,
  matching: find.byWidgetPredicate(
    (widget) => widget is NocturneButton && widget.icon == icon,
  ),
);

void main() {
  // The design's own surface, for the same reason widget_test.dart uses it: the
  // panel widths this file measures only make sense at 1440x900.
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

      // The sample repository holds this statically, so a test that leaves the
      // panel shut hands it to the next one.
      const SampleServalRepository().setActivityPanelCollapsed(false);
    });
  });

  testWidgets('the wall column collapses to its rail and back', (tester) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    expect(
      tester.getSize(find.byType(ActivityColumn)).width,
      Serval.activityColumnWidth,
    );

    await tester.tap(
      _chevron(PhosphorIconsRegular.caretRight, find.byType(ActivityColumn)),
    );
    await tester.pumpAndSettle();

    expect(
      tester.getSize(find.byType(ActivityColumn)).width,
      Serval.activityRailWidth,
    );

    // The feed goes with the width — the rail is the chevron and nothing else.
    expect(find.text("What's happening"), findsNothing);
    expect(find.text('Speech'), findsNothing);
    expect(
      find.text('A courier is at the door holding a small parcel.'),
      findsNothing,
    );

    await tester.tap(
      _chevron(PhosphorIconsRegular.caretLeft, find.byType(ActivityColumn)),
    );
    await tester.pumpAndSettle();

    expect(
      tester.getSize(find.byType(ActivityColumn)).width,
      Serval.activityColumnWidth,
    );
    expect(find.text("What's happening"), findsOneWidget);
  });

  testWidgets('collapsing on the wall collapses the single camera too', (
    tester,
  ) async {
    await tester.pumpWidget(const ServalApp());
    await tester.pumpAndSettle();

    await tester.tap(
      _chevron(PhosphorIconsRegular.caretRight, find.byType(ActivityColumn)),
    );
    await tester.pumpAndSettle();

    // The tile, not a row in the feed: the rows are in the column that was just
    // shut, which is the whole point of this test.
    await tester.tap(find.text('Front door'));
    await tester.pumpAndSettle();

    // The preference is the repository's, not either screen's, so it arrives
    // here already made.
    expect(
      tester.getSize(find.byType(ActivityPanel)).width,
      Serval.activityRailWidth,
    );
    expect(find.text("What's happening"), findsNothing);

    await tester.tap(
      _chevron(PhosphorIconsRegular.caretLeft, find.byType(ActivityPanel)),
    );
    await tester.pumpAndSettle();

    expect(
      tester.getSize(find.byType(ActivityPanel)).width,
      Serval.detailPanelWidth,
    );
  });
}
