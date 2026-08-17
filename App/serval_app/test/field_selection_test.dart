// Dragging across a field's text highlights it.
//
// Every field in the app is an [EditableText] rather than a `TextField`, to keep Material's
// underline and focus blue out of Nocturne — and a bare [EditableText] ships no pointer handling
// at all, so for a long time none of them could be selected in, only typed in. This pins the
// gesture layer [NocturneEditableText] adds back.
import 'package:flutter/gestures.dart' show PointerDeviceKind;
import 'package:flutter/material.dart' show MaterialApp;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/widgets/nocturne_field.dart';

void main() {
  // The pointer half of this is platform-shaped, and Serval is driven from a desktop browser.
  final desktop = TargetPlatformVariant.only(TargetPlatform.linux);

  Future<TextEditingController> pump(WidgetTester tester, String text) async {
    final controller = TextEditingController(text: text);
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 300,
            child: NocturneField(label: 'Where it is', controller: controller),
          ),
        ),
      ),
    );

    return controller;
  }

  /// The rect the characters actually occupy, which is narrower than the 36px box they sit in.
  Rect textRect(WidgetTester tester) =>
      tester.getRect(find.byType(EditableText));

  testWidgets('a drag across the text selects the run it crossed', (
    tester,
  ) async {
    final controller = await pump(tester, 'front gate');
    final rect = textRect(tester);

    // A mouse: dragging a finger across a field scrolls it, and always has.
    final gesture = await tester.startGesture(
      Offset(rect.left + 2, rect.center.dy),
      kind: PointerDeviceKind.mouse,
    );
    await tester.pump(const Duration(milliseconds: 200));
    await gesture.moveTo(Offset(rect.left + 60, rect.center.dy));
    await tester.pump();
    await gesture.up();
    await tester.pump();

    expect(
      controller.selection.isCollapsed,
      isFalse,
      reason: 'a drag left a caret rather than a highlight',
    );
    expect(controller.selection.start, 0);
    expect(controller.selection.end, greaterThan(0));
  }, variant: desktop);

  testWidgets('a double-click takes the word under it', (tester) async {
    final controller = await pump(tester, 'front gate');
    final rect = textRect(tester);
    final at = Offset(rect.left + 8, rect.center.dy);

    // One pointer down-up-down-up rather than two `tapAt`s: consecutive taps are counted per
    // pointer, and a second tap from a second pointer is a first tap again.
    final gesture = await tester.startGesture(
      at,
      kind: PointerDeviceKind.mouse,
    );
    await gesture.up();
    await tester.pump(const Duration(milliseconds: 50));
    await gesture.down(at);
    await gesture.up();
    await tester.pump();

    expect(controller.selection.textInside('front gate'), 'front');
  }, variant: desktop);

  testWidgets('a tap puts the caret where it landed, not at the end', (
    tester,
  ) async {
    final controller = await pump(tester, 'front gate');
    final rect = textRect(tester);

    await tester.tapAt(Offset(rect.left + 1, rect.center.dy));
    await tester.pump();

    expect(controller.selection.isCollapsed, isTrue);
    expect(controller.selection.baseOffset, 0);
  }, variant: desktop);
}
