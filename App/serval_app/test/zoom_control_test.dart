// The zoom track: that it can be driven at all, and that it only states a figure it was told.
//
// Worth its own test for two reasons, both of which were live. The track carried an `onChanged`
// that no gesture ever called, so the knob was decorative — a control that looks driveable and is
// not. And its position came from a logarithmic 1x..8x mapping, which claimed an eightfold lens for
// every camera and placed the knob by a magnification no camera reports.
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/models/ptz.dart';
import 'package:serval_app/widgets/ptz_pad.dart';

void main() {
  Future<void> pump(
    WidgetTester tester,
    ZoomPosition zoom, {
    ValueChanged<double>? onChanged,
  }) => tester.pumpWidget(
    Directionality(
      textDirection: TextDirection.ltr,
      child: Center(
        child: ZoomControl(zoom: zoom, onChanged: onChanged),
      ),
    ),
  );

  group('the readout', () {
    testWidgets('a measured position is stated as a percentage', (
      tester,
    ) async {
      await pump(tester, const ZoomPosition.measured(0.4));

      expect(find.text('40%'), findsOneWidget);
    });

    testWidgets('a reckoned position states nothing', (tester) async {
      // Dead reckoning is what we asked for, not where the lens is. Drawing the same figure in
      // both cases is what would make it a lie.
      await pump(tester, const ZoomPosition.reckoned(0.4));

      expect(find.text('40%'), findsNothing);
    });

    testWidgets('a camera reporting nothing draws no figure', (tester) async {
      await pump(tester, ZoomPosition.unknown);

      expect(find.textContaining('%'), findsNothing);
    });
  });

  group('the gesture', () {
    testWidgets('a tap near the top asks to zoom in', (tester) async {
      double? asked;
      await pump(
        tester,
        const ZoomPosition.measured(0.5),
        onChanged: (value) => asked = value,
      );

      final track = tester.getRect(find.byType(GestureDetector));
      await tester.tapAt(Offset(track.center.dx, track.top + 1));

      // The top is zoomed in, agreeing with the magnifier glyph above it.
      expect(asked, isNotNull);
      expect(asked, greaterThan(0.9));
    });

    testWidgets('a tap near the bottom asks to zoom out', (tester) async {
      double? asked;
      await pump(
        tester,
        const ZoomPosition.measured(0.5),
        onChanged: (value) => asked = value,
      );

      final track = tester.getRect(find.byType(GestureDetector));
      await tester.tapAt(Offset(track.center.dx, track.bottom - 1));

      expect(asked, isNotNull);
      expect(asked, lessThan(0.1));
    });

    testWidgets('a drag reports continuously, not only where it started', (
      tester,
    ) async {
      // A zoom drag runs the length of the column. Reporting only the start would make the track
      // behave as a tap target wearing a knob.
      final asked = <double>[];
      await pump(
        tester,
        const ZoomPosition.measured(0.5),
        onChanged: asked.add,
      );

      final track = tester.getRect(find.byType(GestureDetector));
      final gesture = await tester.startGesture(
        Offset(track.center.dx, track.bottom - 2),
      );
      await tester.pump();
      await gesture.moveTo(Offset(track.center.dx, track.center.dy));
      await tester.pump();
      await gesture.moveTo(Offset(track.center.dx, track.top + 2));
      await tester.pump();
      await gesture.up();

      // More than once is the whole claim: the recognizer folds the move that clears the touch
      // slop into the drag start, so two moves report twice rather than three times.
      expect(asked.length, greaterThan(1));
      // Dragging up zooms in, so the run has to end higher than it began.
      expect(asked.last, greaterThan(asked.first));
    });

    testWidgets('a drag past the end clamps rather than overshooting', (
      tester,
    ) async {
      final asked = <double>[];
      await pump(
        tester,
        const ZoomPosition.measured(0.5),
        onChanged: asked.add,
      );

      final track = tester.getRect(find.byType(GestureDetector));
      final gesture = await tester.startGesture(track.center);
      await tester.pump();
      await gesture.moveTo(Offset(track.center.dx, track.top - 40));
      await tester.pump();
      await gesture.up();

      expect(asked, isNotEmpty);
      expect(asked.every((v) => v >= 0 && v <= 1), isTrue);
      expect(asked.last, 1.0);
    });

    testWidgets('a control with no handler is inert rather than throwing', (
      tester,
    ) async {
      // Drawn without a handler in at least one layout. A null callback must not take the screen
      // down when somebody drags it anyway.
      await pump(tester, const ZoomPosition.measured(0.5));

      final track = tester.getRect(find.byType(GestureDetector));
      await tester.tapAt(track.center);

      expect(tester.takeException(), isNull);
    });
  });

  group('the knob', () {
    testWidgets('sits at the bottom when the lens is wide', (tester) async {
      await pump(tester, const ZoomPosition.measured(0));
      final wide = tester.getRect(find.byType(GestureDetector));
      final knobWide = _knob(tester);

      await pump(tester, const ZoomPosition.measured(1));
      final knobTight = _knob(tester);

      // Linear over travel, and the right way up: 0 is wide and sits low, 1 is tight and sits high.
      expect(knobWide.top, greaterThan(knobTight.top));
      expect(knobTight.top, lessThan(wide.center.dy));
    });

    testWidgets('sits halfway at halfway, rather than on a log curve', (
      tester,
    ) async {
      // The old mapping was logarithmic over a hard-coded 1x..8x, so the midpoint of the lens's
      // travel did not land at the midpoint of the track.
      await pump(tester, const ZoomPosition.measured(0));
      final low = _knob(tester).top;

      await pump(tester, const ZoomPosition.measured(1));
      final high = _knob(tester).top;

      await pump(tester, const ZoomPosition.measured(0.5));
      final mid = _knob(tester).top;

      expect(mid, closeTo((low + high) / 2, 0.5));
    });
  });
}

/// The accent dot, which is the only circular `Container` in the control.
Rect _knob(WidgetTester tester) => tester.getRect(
  find.descendant(
    of: find.byType(Stack),
    matching: find.byWidgetPredicate(
      (w) =>
          w is Container &&
          w.decoration is BoxDecoration &&
          (w.decoration! as BoxDecoration).shape == BoxShape.circle,
    ),
  ),
);
