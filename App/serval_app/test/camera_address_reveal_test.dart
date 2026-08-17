// A camera's address says that it has to be revealed before it can be edited.
//
// The address carries the camera's password, so it ships masked — and masking here swaps the
// editor out of the tree rather than obscuring its characters, which means the box takes no
// caret and answers a click with nothing. That gate is deliberate. Being unable to find out it
// is there was not: the reveal was a 14px glyph with no label beside an identical one for copy.
// This pins the three things that tell someone the way in.
import 'package:flutter/material.dart' show MaterialApp;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:serval_app/widgets/nocturne_editable_text.dart';
import 'package:serval_app/widgets/nocturne_field.dart';

void main() {
  const url = 'rtsp://view:hunter2@192.168.1.50:554/h264Preview_01_main';
  const masked = 'rtsp://view:••••••••@192.168.1.50:554/h264Preview_01_main';
  const note =
      'Hidden because it carries the camera’s password — reveal it to edit.';

  Future<TextEditingController> pump(
    WidgetTester tester, {
    String? maskedPreview = masked,
    String? maskedNote = note,
  }) async {
    final controller = TextEditingController(text: url);
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      MaterialApp(
        home: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: 420,
            child: NocturneField(
              label: 'Address',
              controller: controller,
              mono: true,
              obscure: true,
              maskedPreview: maskedPreview,
              maskedNote: maskedNote,
              copyable: true,
            ),
          ),
        ),
      ),
    );

    return controller;
  }

  testWidgets('a masked address refuses the caret and says why', (
    tester,
  ) async {
    await pump(tester);

    expect(find.text(masked), findsOneWidget);
    expect(
      find.byType(NocturneEditableText),
      findsNothing,
      reason:
          'the mask stands in for the editor, so there is nothing to type into',
    );
    expect(
      find.text(note),
      findsOneWidget,
      reason: 'a box that will not take typing has to say what unlocks it',
    );
  });

  testWidgets('clicking the address is what reveals it', (tester) async {
    final controller = await pump(tester);

    // Where someone clicks when they mean to type — not the eye, which they have to find first.
    await tester.tap(find.text(masked));
    await tester.pump();

    expect(find.byType(NocturneEditableText), findsOneWidget);
    expect(find.text(masked), findsNothing);
    expect(
      find.text(note),
      findsNothing,
      reason: 'the note is only true while the box is refusing typing',
    );
    expect(
      controller.text,
      url,
      reason:
          'revealing shows the real address, never the stars it was drawn with',
    );
  });

  testWidgets('the eye reveals too, and hovering it says so', (tester) async {
    final controller = await pump(tester);

    expect(find.byTooltip('Reveal to edit'), findsOneWidget);

    await tester.tap(find.byTooltip('Reveal to edit'));
    await tester.pump();

    expect(find.byType(NocturneEditableText), findsOneWidget);
    expect(find.text(note), findsNothing);
    expect(controller.text, url);
    expect(find.byTooltip('Hide again'), findsOneWidget);
  });

  testWidgets('a plain password field gets no note', (tester) async {
    // The ONVIF password's shape: obscured, but as dots from the editor itself, which is
    // editable and explains itself. Nothing to say.
    await pump(tester, maskedPreview: null, maskedNote: null);

    expect(find.byType(NocturneEditableText), findsOneWidget);
    expect(find.text(note), findsNothing);
    expect(find.byTooltip('Reveal'), findsOneWidget);
  });
}
